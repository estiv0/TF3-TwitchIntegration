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
    /// Base game industries (base/content/industries) with their in-game English names (translations as the game's,
    /// the same as the website's web/js/i18n.js), in dialog order.
    /// </summary>
    public static IReadOnlyList<(string Type, string En)> Known { get; } =
    [
        ("coal_mine", Loc.N("Coal Mine")),
        ("iron_ore_mine", Loc.N("Iron Ore Mine")),
        ("quarry", Loc.N("Quarry")),
        ("sand_pit", Loc.N("Sand Pit")),
        ("clay_pit", Loc.N("Clay Pit")),
        ("farm", Loc.N("Crop Farm")),
        ("livestock_farm", Loc.N("Livestock Farm")),
        ("cotton_farm", Loc.N("Cotton Farm")),
        ("rubber_farm", Loc.N("Rubber Farm")),
        ("forest", Loc.N("Logging Camp")),
        ("oil_well", Loc.N("Oil Well")),
        ("brewery", Loc.N("Brewery")),
        ("bricks_works", Loc.N("Brickworks")),
        ("cement_plant", Loc.N("Cement Plant")),
        ("chemical_plant", Loc.N("Chemical Plant")),
        ("food_factory", Loc.N("Canning Factory")),
        ("furniture_factory", Loc.N("Furniture Factory")),
        ("glass_works", Loc.N("Glassworks")),
        ("machine_factory", Loc.N("Machine Factory")),
        ("oil_refinery", Loc.N("Oil Refinery")),
        ("paper_mill", Loc.N("Paper Mill")),
        ("printing_press", Loc.N("Printing Factory")),
        ("saw_mill", Loc.N("Saw Mill")),
        ("steel_mill", Loc.N("Steel Mill")),
        ("textile_factory", Loc.N("Textile Factory")),
        ("tire_factory", Loc.N("Tire Factory")),
        ("tool_factory", Loc.N("Tools Factory")),
        ("vehicle_factory", Loc.N("Vehicle Factory")),
        ("weaving_mill", Loc.N("Weaving Mill")),
    ];

    public static IReadOnlyDictionary<string, IndustryZone> Defaults { get; } = new Dictionary<string, IndustryZone>
    {
        ["coal_mine"] = IndustryZone.Mountains,
        ["iron_ore_mine"] = IndustryZone.Mountains,
        ["quarry"] = IndustryZone.Mountains,
        ["sand_pit"] = IndustryZone.NearWater,
        ["clay_pit"] = IndustryZone.NearWater,
        ["farm"] = IndustryZone.Lowland,
        ["livestock_farm"] = IndustryZone.Lowland,
        ["cotton_farm"] = IndustryZone.Lowland,
        ["rubber_farm"] = IndustryZone.Lowland,
        ["forest"] = IndustryZone.Inland,
    };

    public static string Name(string type) =>
        Known.FirstOrDefault(k => k.Type == type) is { Type: not null } k ? Loc.T(k.En) : type.Replace('_', ' ');

    /// <summary>"in the mountains" / "w górach" etc., after "can only be built" / "można budować tylko".</summary>
    public static string Label(IndustryZone zone) => zone switch
    {
        IndustryZone.Mountains => Loc.T("in the mountains"),
        IndustryZone.NearWater => Loc.T("near water (within 1 km)"),
        IndustryZone.Lowland => Loc.T("on flat lowland"),
        IndustryZone.Inland => Loc.T("away from water (over 1 km)"),
        _ => Loc.T("anywhere"),
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
