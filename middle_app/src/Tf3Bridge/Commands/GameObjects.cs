using System.Text.Json;

namespace Tf3Bridge.Commands;

/// <summary>
/// Game names follow "Name | Owner" (the site splits them the same way, web/js/format.js):
/// everything after the last '|' is the owner.
/// </summary>
public static class OwnerName
{
    /// <summary>Longer names are cut; the mod rejects anything over 200 bytes anyway.</summary>
    public const int MaxLength = 80;

    public static (string Name, string? Owner) Split(string full)
    {
        var i = full.LastIndexOf('|');
        if (i < 0) return (full.Trim(), null);
        var owner = full[(i + 1)..].Trim();
        var name = full[..i].Trim();
        return (name.Length > 0 ? name : full.Trim(), owner.Length > 0 ? owner : null);
    }

    /// <summary>The same name with a new owner (null = without owner).</summary>
    public static string WithOwner(string full, string? owner)
    {
        var name = Split(full).Name;
        var clean = owner is null ? "" : Clean(owner).Replace("|", "");
        return Cut(clean.Length > 0 ? $"{name} | {clean}" : name);
    }

    /// <summary>Viewer text without control characters (new lines etc.), trimmed and cut to <see cref="MaxLength"/>.</summary>
    public static string Clean(string text) =>
        Cut(new string([.. text.Where(c => !char.IsControl(c))]).Trim());

    private static string Cut(string s) => s.Length <= MaxLength ? s : s[..MaxLength].TrimEnd();
}

public enum GameObjectKind { Town, Industry, Station, Line }

/// <summary>
/// Something a viewer can target. <see cref="Entity"/> is the entity that carries the name
/// (for stations: the station group, like the name in the export).
/// </summary>
public sealed record GameObject(GameObjectKind Kind, long Entity, string FullName)
{
    public string Name => OwnerName.Split(FullName).Name;
    public string? Owner => OwnerName.Split(FullName).Owner;
}

public static class GameObjects
{
    /// <summary>Towns, industries, stations and lines from the latest published dynamic.json; empty when missing.</summary>
    public static List<GameObject> Load(string dynamicJsonPath)
    {
        if (!File.Exists(dynamicJsonPath)) return [];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(dynamicJsonPath));
            return Parse(doc.RootElement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    internal static List<GameObject> Parse(JsonElement root)
    {
        var list = new List<GameObject>();
        void Add(string key, GameObjectKind kind, Func<JsonElement, long?> entity)
        {
            if (!root.TryGetProperty(key, out var items) || items.ValueKind != JsonValueKind.Array) return;
            foreach (var x in items.EnumerateArray())
            {
                if (x.ValueKind != JsonValueKind.Object) continue;
                var name = x.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || entity(x) is not { } id) continue;
                list.Add(new GameObject(kind, id, name));
            }
        }
        static long? Id(JsonElement x) => x.TryGetProperty("id", out var v) && v.TryGetInt64(out var id) ? id : null;
        static long? StationGroup(JsonElement x) =>
            x.TryGetProperty("groups", out var g) && g.ValueKind == JsonValueKind.Array && g.GetArrayLength() > 0 && g[0].TryGetInt64(out var id)
                ? id : Id(x);

        Add("towns", GameObjectKind.Town, Id);
        Add("industries", GameObjectKind.Industry, Id);
        Add("stations", GameObjectKind.Station, StationGroup);
        Add("lines", GameObjectKind.Line, Id);
        list.Sort((a, b) => a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind) : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }
}
