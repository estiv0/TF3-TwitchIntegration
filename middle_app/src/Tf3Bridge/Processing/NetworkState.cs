using System.Text.Json.Nodes;

namespace Tf3Bridge.Processing;

/// <summary>
/// The road/rail network assembled from one full <c>network</c> snapshot and the
/// <c>networkDelta</c> files that follow it (same session, shared seq counter, base = seq - 1).
/// Edge rows are arrays ordered by <see cref="Fields"/>; the first value is the edge id.
/// Footprint rows (areas of stations, depots and warehouses) are <c>[constructionId, type, polygons]</c> and
/// route rows (real line routes) <c>[lineId, legs]</c>. Each set is a list in the full snapshot, and changed rows
/// + removed ids in a delta: <c>changed</c>/<c>removed</c> for edges, <c>footprints</c>/<c>footprintsRemoved</c>,
/// <c>routes</c>/<c>routesRemoved</c>.
/// </summary>
public sealed class NetworkState
{
    private readonly SortedDictionary<long, JsonNode> _edges = new();
    private readonly SortedDictionary<long, JsonNode> _footprints = new();
    private readonly SortedDictionary<long, JsonNode> _routes = new();

    public string? Session { get; private set; }
    public long Seq { get; private set; }
    public long GameTime { get; private set; }
    public int FormatVersion { get; private set; }
    public JsonArray Fields { get; private set; } = new();
    public int EdgeCount => _edges.Count;
    public int FootprintCount => _footprints.Count;
    public int RouteCount => _routes.Count;
    public bool HasData => Session is not null;

    public void ApplyFull(JsonObject doc)
    {
        Replace(_edges, doc["edges"]);
        Replace(_footprints, doc["footprints"]);
        Replace(_routes, doc["routes"]);

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

        Merge(_edges, doc["changed"], doc["removed"]);
        Merge(_footprints, doc["footprints"], doc["footprintsRemoved"]);
        Merge(_routes, doc["routes"], doc["routesRemoved"]);

        Seq = seq;
        ReadHeader(doc);
        return result;
    }

    /// <summary>Full network in the same format the mod writes (kind "network").</summary>
    public JsonObject ToJson() => new()
    {
        ["v"] = FormatVersion,
        ["kind"] = "network",
        ["session"] = Session,
        ["seq"] = Seq,
        ["gameTime"] = GameTime,
        ["fields"] = Fields.DeepClone(),
        ["edges"] = ToArray(_edges),
        ["footprints"] = ToArray(_footprints),
        ["routes"] = ToArray(_routes),
    };

    private static void Replace(SortedDictionary<long, JsonNode> rows, JsonNode? list)
    {
        rows.Clear();
        foreach (var row in list as JsonArray ?? new JsonArray())
            Upsert(rows, row);
    }

    private static void Merge(SortedDictionary<long, JsonNode> rows, JsonNode? changed, JsonNode? removed)
    {
        foreach (var row in changed as JsonArray ?? new JsonArray())
            Upsert(rows, row);
        foreach (var id in removed as JsonArray ?? new JsonArray())
            if (JsonRead.Long(id) is { } removedId) rows.Remove(removedId);
    }

    private static void Upsert(SortedDictionary<long, JsonNode> rows, JsonNode? row)
    {
        if (row is not JsonArray arr || arr.Count == 0 || JsonRead.Long(arr[0]) is not { } id) return;
        rows[id] = arr.DeepClone();
    }

    private static JsonArray ToArray(SortedDictionary<long, JsonNode> rows)
    {
        var list = new JsonArray();
        foreach (var row in rows.Values)
            list.Add(row.DeepClone());
        return list;
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
