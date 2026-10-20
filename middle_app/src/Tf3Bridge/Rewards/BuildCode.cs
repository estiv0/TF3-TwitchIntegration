using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tf3Bridge.Commands;
using Tf3Bridge.Processing;

namespace Tf3Bridge.Rewards;

/// <summary>Where to build, from a code the viewer copied from the site map. <see cref="File"/>: industry type (.con), null for a town.</summary>
public sealed record BuildPlace(double X, double Y, string Name, string? File);

/// <summary>Map area from latest/terrain.json (game coordinates, y to the north).</summary>
public sealed record MapBounds(double MinX, double MaxX, double MinY, double MaxY)
{
    /// <summary>Null when the file is missing or broken (the mod still checks the coordinates).</summary>
    public static MapBounds? Load(string terrainJsonPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(terrainJsonPath));
            var r = doc.RootElement;
            double Num(string name) => r.GetProperty(name).GetDouble();
            return new MapBounds(Num("minX"), Num("maxX"), Num("minY"), Num("maxY"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// The code the site (web/js/build.js) gives the viewer to paste into the reward:
/// <c>[x, y] Name</c> for a town, <c>[x, y, type] Name</c> for an industry (type = "coal_mine" etc.).
/// Coordinates are game metres. The same distances are checked on the site, so a viewer sees
/// a bad place before spending points; here they are checked again against the latest export.
/// </summary>
public static partial class BuildCode
{
    public const int MinName = 2;
    public const int MaxName = 40;
    /// <summary>Distances in metres (web/js/build.js has the same numbers).</summary>
    public const double TownFromTown = 1500, TownFromIndustry = 600, IndustryFromTown = 800, IndustryFromIndustry = 600, MapMargin = 300;

    public sealed record Result(BuildPlace? Place, string? Error);

    [GeneratedRegex(@"^\s*\[\s*(-?\d+(?:\.\d+)?)\s*[,;]\s*(-?\d+(?:\.\d+)?)\s*(?:[,;]\s*([A-Za-z0-9_]+)\s*)?\]\s*(.*)$", RegexOptions.Singleline)]
    private static partial Regex Code();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>"coal_mine" from "::/industries/coal_mine/coal_mine.con"; null for other files.</summary>
    public static string? IndustryType(string? file)
    {
        if (file is null || !file.StartsWith("::/industries/", StringComparison.Ordinal) || !file.EndsWith(".con", StringComparison.Ordinal)) return null;
        var name = file[(file.LastIndexOf('/') + 1)..^4];
        return name.Length > 0 ? name : null;
    }

    /// <param name="terrain">Ground from the newest heightmap; null = not known yet (then neither water and slopes
    /// for towns nor regions for industries are checked).</param>
    /// <param name="zones">Industry type -> required region (<see cref="IndustryZones"/>); null = defaults.</param>
    public static Result Parse(string input, bool industry, IReadOnlyCollection<GameObject> objects, MapBounds? bounds,
        BuildTerrain? terrain = null, IReadOnlyDictionary<string, IndustryZone>? zones = null)
    {
        var m = Code().Match(input);
        if (!m.Success) return Error(Loc.T("that isn't a code from the map (copy it from the website: the 🏗 button)"));
        var x = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var y = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var type = m.Groups[3].Success ? m.Groups[3].Value : null;
        if (industry && type is null) return Error(Loc.T("that's a town code, but this reward builds an industry"));
        if (!industry && type is not null) return Error(Loc.T("that's an industry code, but this reward founds a town"));

        var name = Spaces().Replace(OwnerName.Clean(m.Groups[4].Value).Replace("|", ""), " ").Trim();
        if (name.Length < MinName) return Error(Loc.T("give a name after the code"));
        if (name.Length > MaxName) return Error(Loc.F("the name is too long (max {0} characters)", MaxName));

        if (bounds is { } b && (x < b.MinX + MapMargin || x > b.MaxX - MapMargin || y < b.MinY + MapMargin || y > b.MaxY - MapMargin))
            return Error(Loc.T("that spot is outside the map or right at its edge"));
        if (!industry && terrain?.Sites.At(x, y) is { } site && TownSites.TownProblem(site) is { } ground) return Error(ground);

        string? file = null;
        if (type is not null)
        {
            file = objects.FirstOrDefault(o => o.Kind == GameObjectKind.Industry && string.Equals(IndustryType(o.File), type, StringComparison.OrdinalIgnoreCase))?.File;
            if (file is null) return Error(Loc.F("unknown industry type \"{0}\" (there are none on this map)", type));
            var kind = type.ToLowerInvariant();
            if (terrain is not null && (zones ?? IndustryZones.Defaults).TryGetValue(kind, out var zone) && !terrain.Zones.Allows(zone, x, y))
                return Error(Loc.F("{0}: this industry type can only be built {1} (forbidden ground is red on the map)", IndustryZones.Name(kind), IndustryZones.Label(zone)));
        }

        var key = TargetResolver.Normalize(name);
        var same = objects.FirstOrDefault(o => o.Kind == (industry ? GameObjectKind.Industry : GameObjectKind.Town) && TargetResolver.Normalize(o.Name) == key);
        if (same is not null)
            return Error(industry ? Loc.F("an industry named {0} already exists", same.Name)
                : Loc.F("a town named {0} already exists", same.Name));

        var (fromTown, fromIndustry) = industry ? (IndustryFromTown, IndustryFromIndustry) : (TownFromTown, TownFromIndustry);
        if (TooClose(objects, GameObjectKind.Town, x, y, fromTown) is { } town) return Error(town);
        if (TooClose(objects, GameObjectKind.Industry, x, y, fromIndustry) is { } ind) return Error(ind);
        return new(new BuildPlace(Math.Round(x), Math.Round(y), name, file), null);
    }

    private static string? TooClose(IEnumerable<GameObject> objects, GameObjectKind kind, double x, double y, double min)
    {
        var nearest = objects.Where(o => o.Kind == kind && o.Pos is not null)
            .Select(o => (Object: o, Dist: Math.Sqrt(Math.Pow(o.Pos!.Value.X - x, 2) + Math.Pow(o.Pos!.Value.Y - y, 2))))
            .DefaultIfEmpty() // MinBy throws on an empty sequence of tuples (e.g. a map without industries)
            .MinBy(o => o.Dist);
        if (nearest.Object is null || nearest.Dist >= min) return null;
        return kind == GameObjectKind.Town
            ? Loc.F("too close to the town {0} ({1:0} m, at least {2:0} m needed)", nearest.Object.Name, nearest.Dist, min)
            : Loc.F("too close to the industry {0} ({1:0} m, at least {2:0} m needed)", nearest.Object.Name, nearest.Dist, min);
    }

    private static Result Error(string why) => new(null, why);
}
