using System.Text.Json.Nodes;

namespace Tf3Bridge.Processing;

/// <summary>
/// The road/rail network assembled from one full <c>network</c> snapshot and the
/// <c>networkDelta</c> files that follow it (same session, shared seq counter, base = seq - 1).
/// Edge rows are arrays ordered by <see cref="Fields"/>; the first value is the edge id.
/// </summary>
public sealed class NetworkState
{
    private readonly SortedDictionary<long, JsonNode> _edges = new();

    public string? Session { get; private set; }
    public long Seq { get; private set; }
    public long GameTime { get; private set; }
    public int FormatVersion { get; private set; }
    public JsonArray Fields { get; private set; } = new();
    public int EdgeCount => _edges.Count;
    public bool HasData => Session is not null;

    public void ApplyFull(JsonObject doc)
    {
        _edges.Clear();
        foreach (var row in doc["edges"]?.AsArray() ?? new JsonArray())
            Upsert(row);

        Session = JsonRead.String(doc["session"]);
        Seq = JsonRead.Long(doc["seq"]) ?? 0;
        ReadHeader(doc);
    }

    public DeltaResult ApplyDelta(JsonObject doc)
    {
        var session = JsonRead.String(doc["session"]);
        var seq = JsonRead.Long(doc["seq"]) ?? 0;
        var baseSeq = JsonRead.Long(doc["base"]) ?? seq - 1;

        if (session != Session) return DeltaResult.NoBase;
        if (seq <= Seq) return DeltaResult.Stale;

        // Deltas are upserts + removals, so applying one after a gap still converges
        // for every edge it touches; the gap is reported so the caller can log it.
        var result = baseSeq == Seq ? DeltaResult.Applied : DeltaResult.AppliedAfterGap;

        foreach (var row in doc["changed"]?.AsArray() ?? new JsonArray())
            Upsert(row);
        foreach (var id in doc["removed"]?.AsArray() ?? new JsonArray())
            if (JsonRead.Long(id) is { } removedId) _edges.Remove(removedId);

        Seq = seq;
        ReadHeader(doc);
        return result;
    }

    /// <summary>Full network in the same format the mod writes (kind "network").</summary>
    public JsonObject ToJson()
    {
        var edges = new JsonArray();
        foreach (var row in _edges.Values)
            edges.Add(row.DeepClone());

        return new JsonObject
        {
            ["v"] = FormatVersion,
            ["kind"] = "network",
            ["session"] = Session,
            ["seq"] = Seq,
            ["gameTime"] = GameTime,
            ["fields"] = Fields.DeepClone(),
            ["edges"] = edges,
        };
    }

    private void Upsert(JsonNode? row)
    {
        if (row is not JsonArray arr || arr.Count == 0 || JsonRead.Long(arr[0]) is not { } id) return;
        _edges[id] = arr.DeepClone();
    }

    private void ReadHeader(JsonObject doc)
    {
        GameTime = JsonRead.Long(doc["gameTime"]) ?? GameTime;
        FormatVersion = (int?)JsonRead.Long(doc["v"]) ?? FormatVersion;
        if (doc["fields"] is JsonArray fields) Fields = (JsonArray)fields.DeepClone();
    }
}

public enum DeltaResult
{
    Applied,
    AppliedAfterGap,
    /// <summary>No full snapshot for this session yet.</summary>
    NoBase,
    /// <summary>Already applied (seq not newer than current).</summary>
    Stale,
}
