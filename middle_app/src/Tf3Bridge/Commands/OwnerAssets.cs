using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tf3Bridge.Commands;

/// <summary>
/// Everything one viewer owns (game names "Name | Owner"), grouped the same way as the site's Owners tab
/// (web/js/owners.js): case-insensitive owner, <see cref="Towns"/> largest first (the first is the main town).
/// <see cref="Profit12m"/>: profit of the owner's lines over 12 months, null without line stats.
/// </summary>
public sealed record OwnerAssets(string Key, string Name, IReadOnlyList<string> Towns, IReadOnlyList<string> Industries,
    int Stations, int Lines, int Vehicles, double? Profit12m);

public static class OwnerIndex
{
    /// <summary>Grouping key, also used in the site link (#owner=…).</summary>
    public static string Key(string owner) => owner.Trim().ToLowerInvariant();

    /// <summary>Owners from the latest published dynamic.json; null when it is missing or unreadable.</summary>
    public static Dictionary<string, OwnerAssets>? Load(string dynamicJsonPath)
    {
        if (!File.Exists(dynamicJsonPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(dynamicJsonPath));
            return Parse(doc.RootElement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static Dictionary<string, OwnerAssets> Parse(JsonElement root)
    {
        var towns = Items(root, "towns");
        var industries = Items(root, "industries");
        var stations = Items(root, "stations");
        var lines = Items(root, "lines");

        // Gra nazywa stacje od miasta, także kupionego: "Dworzec Szczecin | estiv0 Zachód" to nie stacja
        // widza "estiv0 Zachód". Pełne nazwy miast i zakładów z właścicielem zamieniamy na same nazwy (jak format.js).
        var owned = towns.Concat(industries).Select(x => Str(x, "name")).OfType<string>().Where(n => n.Contains('|'))
            .Distinct().OrderByDescending(n => n.Length)
            .Select(full => (Re: new Regex(Regex.Escape(full) + @"(?![\p{L}\p{N}_])"), Plain: OwnerName.Split(full).Name))
            .ToList();
        string StationName(string name) =>
            name.Contains('|') ? owned.Aggregate(name, (n, o) => o.Re.Replace(n, o.Plain)) : name;

        var groups = new Dictionary<string, Group>();
        Group? Add(string? fullName)
        {
            if (fullName is null || OwnerName.Split(fullName) is not (var name, { } owner)) return null;
            var key = Key(owner);
            if (!groups.TryGetValue(key, out var g)) groups[key] = g = new Group();
            g.Spellings[owner] = g.Spellings.GetValueOrDefault(owner) + 1;
            g.LastName = name;
            return g;
        }

        foreach (var t in towns)
            if (Add(Str(t, "name")) is { } g) g.Towns.Add((g.LastName, TownSize(t)));
        var industryOwner = new Dictionary<long, string>();
        foreach (var i in industries)
        {
            if (Add(Str(i, "name")) is not { } g) continue;
            g.Industries.Add((g.LastName, Num(i, "level") ?? 0));
            if (Long(i, "id") is { } id) industryOwner[id] = Key(OwnerName.Split(Str(i, "name")!).Owner!);
        }
        foreach (var s in stations)
        {
            if (Str(s, "name") is not { } name) continue;
            var full = StationName(name);
            // Stacja zakładu tego samego widza jest już przy zakładzie.
            if (OwnerName.Split(full).Owner is { } o && Long(s, "industry") is { } ind
                && industryOwner.GetValueOrDefault(ind) == Key(o)) continue;
            if (Add(full) is { } g) g.Stations++;
        }
        foreach (var l in lines)
        {
            if (Add(Str(l, "name")) is not { } g) continue;
            g.Lines++;
            g.Vehicles += (int)(Num(l, "vehicleCount") ?? 0);
            if (l.TryGetProperty("stats", out var st) && Num(st, "profit12m") is { } p) g.Profit12m = (g.Profit12m ?? 0) + p;
        }

        return groups.ToDictionary(kv => kv.Key, kv =>
        {
            var g = kv.Value;
            return new OwnerAssets(kv.Key, g.Spellings.MaxBy(s => s.Value).Key,
                [.. g.Towns.OrderByDescending(t => t.Size).Select(t => t.Name)],
                [.. g.Industries.OrderByDescending(i => i.Level).Select(i => i.Name)],
                g.Stations, g.Lines, g.Vehicles, g.Profit12m);
        });
    }

    private sealed class Group
    {
        public Dictionary<string, int> Spellings { get; } = [];
        public string LastName { get; set; } = "";
        public List<(string Name, double Size)> Towns { get; } = [];
        public List<(string Name, double Level)> Industries { get; } = [];
        public int Stations { get; set; }
        public int Lines { get; set; }
        public int Vehicles { get; set; }
        public double? Profit12m { get; set; }
    }

    private static List<JsonElement> Items(JsonElement root, string key) =>
        root.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array
            ? [.. a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)] : [];

    private static string? Str(JsonElement x, string key) =>
        x.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static double? Num(JsonElement x, string key) =>
        x.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static long? Long(JsonElement x, string key) =>
        x.TryGetProperty(key, out var v) && v.TryGetInt64(out var id) ? id : null;

    // Wielkość miasta jak na stronie (townSize): suma pojemności stref.
    private static double TownSize(JsonElement t) =>
        t.TryGetProperty("capacities", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number).Sum(v => v.GetDouble()) : 0;
}
