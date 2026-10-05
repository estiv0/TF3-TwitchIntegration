using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Tf3Bridge.Input;

namespace Tf3Bridge.Commands;

/// <summary>
/// Commands for the mod: local\tf3export_cmd\queue.lua (written here) and results.lua (written by
/// the mod through app.saveUserdata). A command stays in the queue until the mod reports its
/// result; the mod skips ids it has already handled, so rewriting the queue never runs one twice.
/// </summary>
public sealed class ModCommandQueue
{
    public const string DirName = "tf3export_cmd";
    public const string QueueName = "queue";
    public const string ResultsName = "results";

    /// <summary>The mod drops commands older than 300 s; give it a moment more before giving up here.</summary>
    internal static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(320);
    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(500);
    private const int MaxRecent = 200;

    private readonly string? _dir;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly List<ModCommand> _pending = [];
    private readonly Dictionary<string, ModCommandResult> _recent = [];
    private DateTime _resultsWrite;

    /// <summary>Fired (on a background thread) when the mod reports a result or the command expires.</summary>
    public event Action<ModCommand, ModCommandResult>? Completed;

    public ModCommandQueue(IOptions<BridgeOptions> options, ILogger<ModCommandQueue> log)
        : this(DirFor(options.Value.InputDir), log, TimeProvider.System) { }

    internal ModCommandQueue(string? dir, ILogger log, TimeProvider time)
    {
        _dir = dir;
        _log = log;
        _time = time;
    }

    /// <summary>Next to the export folder (like the mod settings); null when that folder is unknown.</summary>
    public static string? DirFor(string inputDir)
    {
        if (string.IsNullOrWhiteSpace(inputDir)) return null;
        var local = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(inputDir)));
        return local is null ? null : Path.Combine(local, DirName);
    }

    public bool IsAvailable => _dir is not null;

    public int PendingCount { get { lock (_gate) return _pending.Count; } }

    public ModCommand Enqueue(ModCommand command)
    {
        if (_dir is null) throw new InvalidOperationException("The TF3 userdata folder is unknown.");
        lock (_gate)
        {
            _pending.Add(command);
            WriteQueue();
        }
        _log.LogInformation("Command queued: {Command}", command);
        return command;
    }

    /// <summary>Result of a recent command, or null while it is still pending (or unknown).</summary>
    public ModCommandResult? ResultOf(string id)
    {
        lock (_gate) return _recent.GetValueOrDefault(id);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_dir is null) return;
        // Kolejka sprzed restartu Tf3Bridge nie ma już odbiorcy wyników: zaczynamy od pustej
        // (plik musi istnieć, bo brak pliku w app.loadUserdata kończy się oknem błędu w grze).
        lock (_gate) WriteQueue();
        try
        {
            while (true)
            {
                Poll();
                await Task.Delay(PollEvery, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    internal void Poll()
    {
        var results = ReadResults();
        var completed = new List<(ModCommand, ModCommandResult)>();
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                var c = _pending[i];
                ModCommandResult? r = null;
                if (results?.TryGetValue(c.Id, out var found) == true) r = found;
                else if (now - c.Created > GiveUpAfter) r = new ModCommandResult(c.Id, "expired", "gra nie odebrała komendy (czy TF3 działa z włączonym modem?)");
                if (r is null) continue;
                _pending.RemoveAt(i);
                _recent[c.Id] = r;
                completed.Add((c, r));
            }
            if (completed.Count == 0) return;
            while (_recent.Count > MaxRecent) _recent.Remove(_recent.Keys.First());
            WriteQueue();
        }
        foreach (var (c, r) in completed)
        {
            if (r.Ok) _log.LogInformation("Command done: {Command}", c);
            else _log.LogWarning("Command {Status}: {Command}: {Error}", r.Status, c, r.Error);
            Completed?.Invoke(c, r);
        }
    }

    // Wyniki tylko po zmianie pliku; null = brak zmian albo plik niekompletny (mod właśnie pisze).
    private Dictionary<string, ModCommandResult>? ReadResults()
    {
        var path = Path.Combine(_dir!, ResultsName + ".lua");
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (!File.Exists(path) || stamp == _resultsWrite) return null;
            var json = LuaUserdataReader.TryExtractJson(File.ReadAllBytes(path));
            if (json is null) return null; // spróbujemy przy następnym odczycie
            _resultsWrite = stamp;
            var map = new Dictionary<string, ModCommandResult>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("results", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in list.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.Object || r.GetProperty("id").GetString() is not { } id) continue;
                    var status = r.TryGetProperty("status", out var s) ? s.GetString() ?? "failed" : "failed";
                    var error = r.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                    var entity = r.TryGetProperty("entity", out var en) && en.TryGetInt64(out var n) ? n : (long?)null;
                    map[id] = new ModCommandResult(id, status, error, ReadDate(r), entity);
                }
            }
            return map;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _log.LogDebug("Reading command results failed: {Message}", ex.Message);
            return null;
        }
    }

    private static GameDate? ReadDate(JsonElement r)
    {
        if (!r.TryGetProperty("until", out var u) || u.ValueKind != JsonValueKind.Object) return null;
        int? Int(string name) => u.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;
        return Int("year") is { } y && Int("month") is { } m && Int("day") is { } d ? new GameDate(y, m, d) : null;
    }

    // Pod _gate. Najpierw plik tymczasowy, żeby gra nie przeczytała połowy.
    private void WriteQueue()
    {
        var path = Path.Combine(_dir!, QueueName + ".lua");
        try
        {
            Directory.CreateDirectory(_dir!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, ModCommandLua.ToLua(_pending), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Writing the command queue failed: {Message}", ex.Message);
        }
    }
}
