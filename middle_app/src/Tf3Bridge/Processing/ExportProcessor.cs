using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Tf3Bridge.Input;
using Tf3Bridge.Output;

namespace Tf3Bridge.Processing;

/// <summary>
/// One pass over the mod's output folder:
/// <list type="number">
/// <item>files of older sessions are retired (a new game start / loaded save makes them obsolete),</item>
/// <item>network files of the current session are applied in seq order (full snapshot, then deltas),</item>
/// <item>only the newest dynamic snapshot is published, older ones are retired,</item>
/// <item>latest/network.json, latest/dynamic.json and latest/meta.json are published when something changed.</item>
/// </list>
/// Files that cannot be parsed yet are assumed to be mid-write and retried on the next pass.
/// </summary>
public sealed class ExportProcessor(IOptions<BridgeOptions> options, IPublisher publisher, ILogger<ExportProcessor> logger)
{
    public const string DynamicPath = "latest/dynamic.json";
    public const string NetworkPath = "latest/network.json";
    public const string MetaPath = "latest/meta.json";

    private readonly BridgeOptions _options = options.Value;
    private readonly NetworkState _network = new();

    private string? _session;
    private long _dynamicSeq;
    private long? _gameTime;
    private JsonNode? _date;

    private JsonSerializerOptions JsonOptions => new() { WriteIndented = _options.IndentOutput };

    public NetworkState Network => _network;

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

        if (networkChanged)
            await publisher.PublishAsync(NetworkPath, _network.ToJson().ToJsonString(JsonOptions), ct);
        if (dynamicJson is not null)
            await publisher.PublishAsync(DynamicPath, dynamicJson, ct);
        if (networkChanged || dynamicChanged)
            await publisher.PublishAsync(MetaPath, BuildMeta().ToJsonString(JsonOptions), ct);
    }

    private bool ProcessNetwork(List<ExportFile> files)
    {
        var changed = false;
        foreach (var file in files.Where(f => f.Kind != ExportKind.Dynamic).OrderBy(f => f.Seq))
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
            var dir = Path.Combine(_options.InputDir, subfolder);
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
