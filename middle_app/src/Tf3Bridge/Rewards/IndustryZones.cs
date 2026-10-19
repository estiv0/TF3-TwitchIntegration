using System.Text.Json.Nodes;
using Tf3Bridge.Processing;

namespace Tf3Bridge.Rewards;

/// <summary>
/// Where viewers may build each industry type (on top of water / land, which create.lua checks):
/// mines in the mountains, sand and clay near water, farms on lowland, forests inland.
/// The streamer can change it in the rewards dialog; types not listed may be built anywhere.
/// </summary>
public static class IndustryZones
{
    /// <summary>Base game industries (base/content/industries) with their in-game Polish names (base/strings/pl), in dialog order.</summary>
    public static IReadOnlyList<(string Type, string Name)> Known { get; } =
    [
        ("coal_mine", "Kopalnia węgla"), ("iron_ore_mine", "Kopalnia rudy żelaza"), ("quarry", "Kamieniołom"),
        ("sand_excavator", "Pogłębiarka"), ("sand_pit", "Piaskarnia"), ("clay_pit", "Glinianka"),
        ("farm", "Gospodarstwo rolne"), ("livestock_farm", "Hodowla bydła"), ("cotton_farm", "Plantacja bawełny"),
        ("rubber_farm", "Plantacja kauczuku"), ("forest", "Poręba"), ("oil_well", "Kiwon"),
        ("brewery", "Browar"), ("bricks_works", "Cegielnia"), ("cement_plant", "Cementownia"), ("chemical_plant", "Fabryka chemiczna"),
        ("food_factory", "Fabryka konserw"), ("furniture_factory", "Fabryka mebli"), ("glass_works", "Huta szkła"),
        ("machine_factory", "Fabryka maszyn"), ("oil_refinery", "Rafineria"), ("paper_mill", "Papiernia"),
        ("printing_press", "Drukarnia"), ("saw_mill", "Tartak"), ("steel_mill", "Stalownia"),
        ("textile_factory", "Fabryka tkanin"), ("tire_factory", "Fabryka opon"), ("tool_factory", "Fabryka narzędzi"),
        ("vehicle_factory", "Fabryka samochodów"), ("weaving_mill", "Tkalnia"),
    ];

    public static IReadOnlyDictionary<string, IndustryZone> Defaults { get; } = new Dictionary<string, IndustryZone>
    {
        ["coal_mine"] = IndustryZone.Mountains,
        ["iron_ore_mine"] = IndustryZone.Mountains,
        ["quarry"] = IndustryZone.Mountains,
        ["sand_excavator"] = IndustryZone.NearWater,
        ["sand_pit"] = IndustryZone.NearWater,
        ["clay_pit"] = IndustryZone.NearWater,
        ["farm"] = IndustryZone.Lowland,
        ["livestock_farm"] = IndustryZone.Lowland,
        ["cotton_farm"] = IndustryZone.Lowland,
        ["rubber_farm"] = IndustryZone.Lowland,
        ["forest"] = IndustryZone.Inland,
    };

    public static string Name(string type) =>
        Known.FirstOrDefault(k => k.Type == type).Name ?? type.Replace('_', ' ');

    /// <summary>"w górach" ("in the mountains") etc., after "można budować tylko" ("can only be built").</summary>
    public static string Label(IndustryZone zone) => zone switch
    {
        IndustryZone.Mountains => "w górach",
        IndustryZone.NearWater => "przy wodzie (do 1 km od niej)",
        IndustryZone.Lowland => "na płaskiej nizinie",
        IndustryZone.Inland => "z dala od wody (ponad 1 km)",
        _ => "wszędzie",
    };

    /// <summary>Lower-case keys, only real restrictions (no Any), unknown values dropped.</summary>
    public static Dictionary<string, IndustryZone> Normalize(IReadOnlyDictionary<string, IndustryZone>? zones) =>
        (zones ?? Defaults)
            .Where(z => z.Value != IndustryZone.Any && Enum.IsDefined(z.Value) && z.Key.Trim().Length > 0)
            .GroupBy(z => z.Key.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.Last().Value);

    /// <summary>latest/build-rules.json for the site: { "industries": { "coal_mine": "mountains", ... } }.</summary>
    public static JsonObject ToJson(IReadOnlyDictionary<string, IndustryZone> zones)
    {
        var industries = new JsonObject();
        foreach (var (type, zone) in zones.OrderBy(z => z.Key, StringComparer.Ordinal))
            industries[type] = JsonName(zone);
        return new JsonObject { ["industries"] = industries };
    }

    public static string JsonName(IndustryZone zone) => zone switch
    {
        IndustryZone.Mountains => "mountains",
        IndustryZone.NearWater => "nearWater",
        IndustryZone.Lowland => "lowland",
        IndustryZone.Inland => "inland",
        _ => "any",
    };
}

/// <summary>
/// The current industry zones, shared between <see cref="RewardService"/> (owns the settings) and
/// <see cref="ExportProcessor"/> (publishes latest/build-rules.json when <see cref="Version"/> changes).
/// </summary>
public sealed class IndustryZoneRules
{
    private volatile IReadOnlyDictionary<string, IndustryZone> _zones = IndustryZones.Normalize(null);
    private int _version = 1;

    public IReadOnlyDictionary<string, IndustryZone> Zones => _zones;

    public int Version => Volatile.Read(ref _version);

    public void Set(IReadOnlyDictionary<string, IndustryZone>? zones)
    {
        var next = IndustryZones.Normalize(zones);
        if (next.Count == _zones.Count && next.All(z => _zones.TryGetValue(z.Key, out var v) && v == z.Value)) return;
        _zones = next;
        Interlocked.Increment(ref _version);
    }
}
