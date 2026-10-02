using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tf3Bridge.Processing;

/// <summary>
/// Number reads that work for parsed JSON (JsonElement-backed) and for nodes built in code
/// (int/long/double-backed), where <c>GetValue&lt;long&gt;()</c> would throw on an int.
/// </summary>
internal static class JsonRead
{
    public static long? Long(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out double d)) return (long)d;
        if (v.TryGetValue(out JsonElement e) && e.ValueKind == JsonValueKind.Number)
            return e.TryGetInt64(out var el) ? el : (long)e.GetDouble();
        return null;
    }

    public static string? String(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}
