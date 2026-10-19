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
    /// <summary>
    /// Base game industries (base/content/industries) with their in-game English and Polish names (the same as the
    /// website's web/js/i18n.js), in dialog order.
    /// </summary>
    public static IReadOnlyList<(string Type, string En, string Pl)> Known { get; } =
    [
        ("coal_mine", "Coal Mine", "Kopalnia węgla"), ("iron_ore_mine", "Iron Ore Mine", "Kopalnia rudy żelaza"), ("quarry", "Quarry", "Kamieniołom"),
        ("sand_excavator", "Sand Dredger", "Pogłębiarka"), ("sand_pit", "Sand Pit", "Piaskarnia"), ("clay_pit", "Clay Pit", "Glinianka"),
        ("farm", "Crop Farm", "Gospodarstwo rolne"), ("livestock_farm", "Livestock Farm", "Hodowla bydła"), ("cotton_farm", "Cotton Farm", "Plantacja bawełny"),
        ("rubber_farm", "Rubber Farm", "Plantacja kauczuku"), ("forest", "Logging Camp", "Poręba"), ("oil_well", "Oil Well", "Kiwon"),
        ("brewery", "Brewery", "Browar"), ("bricks_works", "Brickworks", "Cegielnia"), ("cement_plant", "Cement Plant", "Cementownia"),
        ("chemical_plant", "Chemical Plant", "Fabryka chemiczna"), ("food_factory", "Canning Factory", "Fabryka konserw"),
        ("furniture_factory", "Furniture Factory", "Fabryka mebli"), ("glass_works", "Glassworks", "Huta szkła"),
        ("machine_factory", "Machine Factory", "Fabryka maszyn"), ("oil_refinery", "Oil Refinery", "Rafineria"), ("paper_mill", "Paper Mill", "Papiernia"),
        ("printing_press", "Printing Factory", "Drukarnia"), ("saw_mill", "Saw Mill", "Tartak"), ("steel_mill", "Steel Mill", "Stalownia"),
        ("textile_factory", "Textile Factory", "Fabryka tkanin"), ("tire_factory", "Tire Factory", "Fabryka opon"),
        ("tool_factory", "Tools Factory", "Fabryka narzędzi"), ("vehicle_factory", "Vehicle Factory", "Fabryka samochodów"),
        ("weaving_mill", "Weaving Mill", "Tkalnia"),
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
        Known.FirstOrDefault(k => k.Type == type) is { Type: not null } k ? Loc.T(k.En, k.Pl) : type.Replace('_', ' ');

    /// <summary>"in the mountains" / "w górach" etc., after "can only be built" / "można budować tylko".</summary>
    public static string Label(IndustryZone zone) => zone switch
    {
        IndustryZone.Mountains => Loc.T("in the mountains", "w górach"),
        IndustryZone.NearWater => Loc.T("near water (within 1 km)", "przy wodzie (do 1 km od niej)"),
        IndustryZone.Lowland => Loc.T("on flat lowland", "na płaskiej nizinie"),
        IndustryZone.Inland => Loc.T("away from water (over 1 km)", "z dala od wody (ponad 1 km)"),
        _ => Loc.T("anywhere", "wszędzie"),
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
