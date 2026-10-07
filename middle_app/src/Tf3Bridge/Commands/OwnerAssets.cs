using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tf3Bridge.Commands;

/// <summary>
/// Everything one viewer owns (game names "Name | Owner"), grouped the same way as the site's Owners tab
/// (web/js/owners.js): case-insensitive owner, <see cref="Towns"/> largest first (the first is the main town).
/// <see cref="Profit12m"/>: profit of the owner's lines over 12 months, null without line stats.
/// <see cref="Score"/>: wealth for the ranking, same formula as the site (owners.js, <see cref="OwnerIndex.Score"/>).
/// </summary>
public sealed record OwnerAssets(string Key, string Name, IReadOnlyList<string> Towns, IReadOnlyList<string> Industries,
    int Stations, int Lines, int Vehicles, double? Profit12m, double Score);

public static class OwnerIndex
{
    /// <summary>Grouping key, also used in the site link (#owner=…).</summary>
    public static string Key(string owner) => owner.Trim().ToLowerInvariant();

    /// <summary>
    /// Majątek do rankingu, jak score w owners.js: miasto <see cref="TownWorth"/>, zakład 50, stacja i linia po 1.
    /// <paramref name="vehiclePoints"/>: pojazdy na liniach widza według nośnika (<see cref="VehicleWorth"/>).
    /// </summary>
    public static double Score(IEnumerable<double> townPoints, int industries, int stations, int lines, double vehiclePoints = 0) =>
        townPoints.Sum() + 50 * industries + stations + lines + vehiclePoints;

    /// <summary>
    /// Miasto: 100 pkt plus wielkość / 25 plus wzrost od startu gry × 100 (podwojone miasto +100, zmalałe bez kary,
    /// bez <paramref name="startSize"/> bez premii). Każde miasto dużo daje, duże i rozwinięte waży znacznie więcej.
    /// </summary>
    public static double TownWorth(double size, double startSize) =>
        100 + size / 25 + (startSize > 0 ? 100 * Math.Max(0, size / startSize - 1) : 0);

    /// <summary>Pojazd: pociąg i samolot 10 pkt, statek 2, drogowy (też tramwaj i nieznany nośnik) 1.</summary>
    public static int VehicleWorth(string? carrier) => carrier switch { "RAIL" or "AIR" => 10, "WATER" => 2, _ => 1 };

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
            if (Add(Str(t, "name")) is { } g) g.Towns.Add((g.LastName, TownSize(t, "capacities"), TownSize(t, "initialCapacities")));
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
            var vehicles = (int)(Num(l, "vehicleCount") ?? 0);
            g.Vehicles += vehicles;
            g.VehiclePoints += vehicles * VehicleWorth(Str(l, "carrier"));
            if (l.TryGetProperty("stats", out var st) && Num(st, "profit12m") is { } p) g.Profit12m = (g.Profit12m ?? 0) + p;
        }

        return groups.ToDictionary(kv => kv.Key, kv =>
        {
            var g = kv.Value;
            return new OwnerAssets(kv.Key, g.Spellings.MaxBy(s => s.Value).Key,
                [.. g.Towns.OrderByDescending(t => t.Size).Select(t => t.Name)],
                [.. g.Industries.OrderByDescending(i => i.Level).Select(i => i.Name)],
                g.Stations, g.Lines, g.Vehicles, g.Profit12m,
                Score(g.Towns.Select(t => TownWorth(t.Size, t.StartSize)), g.Industries.Count, g.Stations, g.Lines, g.VehiclePoints));
        });
    }

    private sealed class Group
    {
        public Dictionary<string, int> Spellings { get; } = [];
        public string LastName { get; set; } = "";
        public List<(string Name, double Size, double StartSize)> Towns { get; } = [];
        public List<(string Name, double Level)> Industries { get; } = [];
        public int Stations { get; set; }
        public int Lines { get; set; }
        public int Vehicles { get; set; }
        public int VehiclePoints { get; set; }
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

    // Wielkość miasta jak na stronie (townSize): suma pojemności stref; "initialCapacities" = na starcie gry.
    private static double TownSize(JsonElement t, string key) =>
        t.TryGetProperty(key, out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number).Sum(v => v.GetDouble()) : 0;
}
