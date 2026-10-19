using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Tf3Bridge.Input;
using Tf3Bridge.Output;
using Tf3Bridge.Rewards;

namespace Tf3Bridge.Processing;

/// <summary>
/// One pass over the mod's output folder:
/// <list type="number">
/// <item>files of older sessions are retired (a new game start / loaded save makes them obsolete),</item>
/// <item>network files of the current session are applied in seq order (full snapshot, then deltas),</item>
/// <item>only the newest dynamic snapshot is published, older ones are retired,</item>
/// <item>the newest terrain heightmap is rendered to latest/terrain.png + latest/terrain-site.png + latest/terrain.json,</item>
/// <item>latest/network.json, latest/dynamic.json and latest/meta.json are published when something changed.</item>
/// </list>
/// Files that cannot be parsed yet are assumed to be mid-write and retried on the next pass.
/// </summary>
public sealed class ExportProcessor(IOptions<BridgeOptions> options, IPublisher publisher, ILogger<ExportProcessor> logger,
    IndustryZoneRules? zoneRules = null)
{
    public const string DynamicPath = "latest/dynamic.json";
    public const string NetworkPath = "latest/network.json";
    public const string MetaPath = "latest/meta.json";
    public const string TerrainPngPath = "latest/terrain.png";
    public const string TerrainInfoPath = "latest/terrain.json";
    public const string TownSitesPath = "latest/terrain-site.png";
    public const string TerrainZonesPath = "latest/terrain-zone.png";
    public const string BuildRulesPath = "latest/build-rules.json";

    private readonly BridgeOptions _options = options.Value;
    private readonly NetworkState _network = new();

    private string? _session;
    private long _dynamicSeq;
    private string? _terrainSession;
    private long? _terrainSeq;
    private long? _gameTime;
    private JsonNode? _date;

    private JsonSerializerOptions JsonOptions => new() { WriteIndented = _options.IndentOutput };

    public NetworkState Network => _network;

    private volatile BuildTerrain? _buildTerrain;
    private int _rulesVersion; // published version of zoneRules (0 = not yet)
    private string? _cacheCheckedFor; // session for which the terrain cache was already tried

    /// <summary>Ground around every point of the newest heightmap (for build rewards); null until the first terrain export.</summary>
    public BuildTerrain? BuildTerrain => _buildTerrain;

    private long _lastDynamicTicks;

    /// <summary>When this run last published a dynamic snapshot (a save is loaded and the game exports), null = not yet.</summary>
    public DateTime? LastDynamicUtc => Volatile.Read(ref _lastDynamicTicks) is var t and > 0 ? new DateTime(t, DateTimeKind.Utc) : null;

    public async Task ProcessOnceAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_options.InputDir)) return;

        var files = Directory.EnumerateFiles(_options.InputDir, "*.lua")
            .Select(p => ExportFile.TryParse(p, File.GetLastWriteTimeUtc(p)))
            .OfType<ExportFile>()
            .ToList();
        if (files.Count == 0) return;

        var session = files.MaxBy(f => f.LastWriteUtc)!.Session;
        if (session != _session)
        {
            logger.LogInformation("Session {Session}", session);
            _session = session;
        }

        var stale = files.Where(f => f.Session != session).ToList();
        if (stale.Count > 0)
        {
            logger.LogInformation("Retiring {Count} file(s) from older sessions", stale.Count);
            stale.ForEach(Retire);
        }

        var current = files.Where(f => f.Session == session).ToList();
        var networkChanged = ProcessNetwork(current);
        var dynamicChanged = ProcessDynamic(current, out var dynamicJson);
        var terrainChanged = await ProcessTerrainAsync(current, ct) || await LoadTerrainCacheAsync(session, ct);
        var rulesChanged = await PublishRulesAsync(ct);

        if (networkChanged)
            await publisher.PublishAsync(NetworkPath, _network.ToJson().ToJsonString(JsonOptions), ct);
        if (dynamicJson is not null)
        {
            await publisher.PublishAsync(DynamicPath, dynamicJson, ct);
            Volatile.Write(ref _lastDynamicTicks, DateTime.UtcNow.Ticks);
        }
        if (networkChanged || dynamicChanged || terrainChanged || rulesChanged)
            await publisher.PublishAsync(MetaPath, BuildMeta().ToJsonString(JsonOptions), ct);
    }

    /// <summary>Newest terrain file -> PNGs + placement JSON (PNGs first, so meta never points at a missing image).</summary>
    private async Task<bool> ProcessTerrainAsync(List<ExportFile> files, CancellationToken ct)
    {
        var terrains = files.Where(f => f.Kind == ExportKind.Terrain).OrderByDescending(f => f.Seq).ToList();
        if (terrains.Count == 0) return false;

        var newest = terrains[0];
        var doc = ReadJsonObject(newest);
        if (doc is null) return false; // mid-write (or moved to failed); retry next pass

        TerrainGrid grid;
        try
        {
            grid = TerrainGrid.Parse(doc);
        }
        catch (FormatException ex)
        {
            logger.LogError(ex, "terrain #{Seq}: invalid data, moving to failed", newest.Seq);
            MoveTo(newest, "failed");
            return false;
        }

        await PublishTerrainAsync(grid, newest.Session, newest.Seq, ct);
        SaveTerrainCache(doc, newest.Session, newest.Seq);
        terrains.ForEach(Retire);
        return true;
    }

    /// <summary>
    /// After a restart: the newest terrain of this session from <see cref="BridgeOptions.TerrainCacheFile"/>
    /// (the mod does not send it again until the next load or terraforming). Tried once per session.
    /// </summary>
    private async Task<bool> LoadTerrainCacheAsync(string session, CancellationToken ct)
    {
        if (_terrainSession == session || _cacheCheckedFor == session) return false;
        _cacheCheckedFor = session;
        var path = _options.TerrainCacheFile;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        try
        {
            var cache = JsonNode.Parse(await File.ReadAllTextAsync(path, ct))?.AsObject();
            if (cache is null || JsonRead.String(cache["session"]) != session || cache["terrain"] is not JsonObject doc) return false;
            var seq = JsonRead.Long(cache["seq"]) ?? 0;
            await PublishTerrainAsync(TerrainGrid.Parse(doc), session, seq, ct);
            logger.LogInformation("terrain #{Seq}: restored from {Path}", seq, path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
        {
            logger.LogWarning("Terrain cache {Path} not used: {Message}", path, ex.Message);
            return false;
        }
    }

    private void SaveTerrainCache(JsonObject doc, string session, long seq)
    {
        var path = _options.TerrainCacheFile;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var cache = new JsonObject { ["session"] = session, ["seq"] = seq, ["terrain"] = doc.DeepClone() };
            File.WriteAllText(path + ".tmp", cache.ToJsonString());
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Terrain cache {Path} not saved: {Message}", path, ex.Message);
        }
    }

    /// <summary>Terrain PNGs + placement JSON (PNGs first, so meta never points at a missing image) and the build checks.</summary>
    private async Task PublishTerrainAsync(TerrainGrid grid, string session, long seq, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var png = TerrainRenderer.RenderPng(grid);
        await publisher.PublishBytesAsync(TerrainPngPath, png, ct);
        var sites = TownSites.Compute(grid);
        await publisher.PublishBytesAsync(TownSitesPath, sites.RenderPng(), ct);
        var zones = TerrainZones.Compute(grid);
        await publisher.PublishBytesAsync(TerrainZonesPath, zones.RenderPng(), ct);
        _buildTerrain = new BuildTerrain(sites, zones);
        logger.LogInformation("terrain #{Seq}: zones of the land: mountains {Mountains:P0} (average slope >= {Slope:P0}), near water {Water:P0}, lowland {Lowland:P0}, inland {Inland:P0}",
            seq, zones.Coverage(IndustryZone.Mountains), zones.MountainSlope, zones.Coverage(IndustryZone.NearWater),
            zones.Coverage(IndustryZone.Lowland), zones.Coverage(IndustryZone.Inland));
        await publisher.PublishAsync(TerrainInfoPath,
            TerrainRenderer.Info(grid, session, seq).ToJsonString(JsonOptions), ct);

        _terrainSession = session;
        _terrainSeq = seq;
        logger.LogInformation("terrain #{Seq}: {Nx} x {Ny} @ {Step} m, z {ZMin:F0}..{ZMax:F0}, PNG {Bytes} B in {Ms:F0} ms",
            seq, grid.Nx, grid.Ny, grid.Step, grid.ZMin, grid.ZMax, png.Length, (DateTime.UtcNow - started).TotalMilliseconds);
    }

    /// <summary>latest/build-rules.json (where viewers may build industries) whenever the streamer changes it.</summary>
    private async Task<bool> PublishRulesAsync(CancellationToken ct)
    {
        if (zoneRules is null) return false;
        var version = zoneRules.Version;
        if (version == _rulesVersion) return false;
        await publisher.PublishAsync(BuildRulesPath, IndustryZones.ToJson(zoneRules.Zones).ToJsonString(JsonOptions), ct);
        _rulesVersion = version;
        return true;
    }

    private bool ProcessNetwork(List<ExportFile> files)
    {
        var changed = false;
        foreach (var file in files.Where(f => f.Kind is ExportKind.Network or ExportKind.NetworkDelta).OrderBy(f => f.Seq))
        {
            var doc = ReadJsonObject(file);
            if (doc is null)
            {
                // Keep order: later deltas must wait until this file is readable (or declared broken).
                if (File.Exists(file.Path)) break;
                continue;
            }

            if (file.Kind == ExportKind.Network)
            {
                _network.ApplyFull(doc);
                changed = true;
                logger.LogInformation("network #{Seq}: full snapshot, {Edges} edges", file.Seq, _network.EdgeCount);
            }
            else
            {
                var result = _network.ApplyDelta(doc);
                switch (result)
                {
                    case DeltaResult.Applied:
                    case DeltaResult.AppliedAfterGap:
                        changed = true;
                        var level = result == DeltaResult.Applied ? LogLevel.Information : LogLevel.Warning;
                        logger.Log(level, "networkDelta #{Seq}: {Changed} changed, {Removed} removed{Gap}",
                            file.Seq, doc["changed"]?.AsArray().Count ?? 0, doc["removed"]?.AsArray().Count ?? 0,
                            result == DeltaResult.Applied ? "" : " (after a gap: some earlier delta is missing)");
                        break;
                    case DeltaResult.NoBase:
                        logger.LogWarning("networkDelta #{Seq} skipped: no full network snapshot for this session", file.Seq);
                        break;
                    case DeltaResult.Stale:
                        logger.LogDebug("networkDelta #{Seq} skipped: already applied", file.Seq);
                        break;
                }
            }
            Retire(file);
        }
        return changed;
    }

    private bool ProcessDynamic(List<ExportFile> files, out string? json)
    {
        json = null;
        var dynamics = files.Where(f => f.Kind == ExportKind.Dynamic).OrderByDescending(f => f.Seq).ToList();
        if (dynamics.Count == 0) return false;

        var newest = dynamics[0];
        var raw = ReadJson(newest);
        if (raw is null) return false; // mid-write (or moved to failed); retry next pass

        JsonObject? doc;
        try
        {
            doc = JsonNode.Parse(raw)?.AsObject();
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "dynamic #{Seq}: invalid JSON, moving to failed", newest.Seq);
            MoveTo(newest, "failed");
            return false;
        }

        _dynamicSeq = newest.Seq;
        _gameTime = JsonRead.Long(doc?["gameTime"]);
        _date = doc?["date"]?.DeepClone();
        json = raw;

        if (dynamics.Count > 1)
            logger.LogDebug("dynamic: skipping {Count} older snapshot(s)", dynamics.Count - 1);
        logger.LogInformation("dynamic #{Seq}: {Bytes} B", newest.Seq, raw.Length);

        dynamics.ForEach(Retire);
        return true;
    }

    private JsonObject BuildMeta() => new()
    {
        ["session"] = _session,
        ["dynamicSeq"] = _dynamicSeq,
        ["networkSeq"] = _network.HasData ? _network.Seq : null,
        ["networkSession"] = _network.Session,
        ["edges"] = _network.EdgeCount,
        ["terrainSeq"] = _terrainSeq,
        ["terrainSession"] = _terrainSession,
        // Changes with the streamer's settings; Tf3Bridge restarts count from 1 again, so the session is part of it.
        ["rulesVersion"] = _rulesVersion > 0 ? $"{_session}:{_rulesVersion}" : null,
        ["gameTime"] = _gameTime,
        ["date"] = _date?.DeepClone(),
        ["updatedAtUtc"] = DateTime.UtcNow.ToString("O"),
    };

    /// <summary>Embedded JSON text, or null if the file is incomplete / unreadable right now.</summary>
    private string? ReadJson(ExportFile file)
    {
        byte[] bytes;
        try
        {
            using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            bytes = ms.ToArray();
        }
        catch (IOException)
        {
            return null; // locked or gone; retry next pass
        }

        var json = LuaUserdataReader.TryExtractJson(bytes);
        if (json is null && DateTime.UtcNow - file.LastWriteUtc > TimeSpan.FromSeconds(_options.IncompleteFileTimeoutSec))
        {
            logger.LogError("{File}: still unreadable after {Sec} s, moving to failed",
                Path.GetFileName(file.Path), _options.IncompleteFileTimeoutSec);
            MoveTo(file, "failed");
        }
        return json;
    }

    private JsonObject? ReadJsonObject(ExportFile file)
    {
        var json = ReadJson(file);
        if (json is null) return null;
        try
        {
            return JsonNode.Parse(json)?.AsObject();
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "{File}: invalid JSON, moving to failed", Path.GetFileName(file.Path));
            MoveTo(file, "failed");
            return null;
        }
    }

    private void Retire(ExportFile file)
    {
        if (_options.KeepProcessedFiles) MoveTo(file, "processed");
        else TryDelete(file.Path);
    }

    private void MoveTo(ExportFile file, string subfolder)
    {
        try
        {
            // next to it, not in towns_industries: there the game (map editor) lists everything as its own exports
            var local = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(_options.InputDir)) ?? _options.InputDir;
            var dir = Path.Combine(local, "tf3export", subfolder);
            Directory.CreateDirectory(dir);
            File.Move(file.Path, Path.Combine(dir, Path.GetFileName(file.Path)), overwrite: true);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not move {File} to {Folder}", file.Path, subfolder);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not delete {File}", path);
        }
    }
}
