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

    /// <param name="sites">Ground from the newest heightmap; null = not known yet (towns are then not checked for water and slopes).</param>
    public static Result Parse(string input, bool industry, IReadOnlyCollection<GameObject> objects, MapBounds? bounds, TownSites? sites = null)
    {
        var m = Code().Match(input);
        if (!m.Success) return Error("to nie jest kod z mapy (skopiuj go ze strony: przycisk 🏗)");
        var x = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var y = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        var type = m.Groups[3].Success ? m.Groups[3].Value : null;
        if (industry && type is null) return Error("to kod miasta, a nagroda buduje zakład");
        if (!industry && type is not null) return Error("to kod zakładu, a nagroda zakłada miasto");

        var name = Spaces().Replace(OwnerName.Clean(m.Groups[4].Value).Replace("|", ""), " ").Trim();
        if (name.Length < MinName) return Error("podaj nazwę po kodzie");
        if (name.Length > MaxName) return Error($"nazwa jest za długa (max {MaxName} znaków)");

        if (bounds is { } b && (x < b.MinX + MapMargin || x > b.MaxX - MapMargin || y < b.MinY + MapMargin || y > b.MaxY - MapMargin))
            return Error("to miejsce jest poza mapą albo przy samej krawędzi");
        if (!industry && sites?.At(x, y) is { } site && TownSites.TownProblem(site) is { } ground) return Error(ground);

        string? file = null;
        if (type is not null)
        {
            file = objects.FirstOrDefault(o => o.Kind == GameObjectKind.Industry && string.Equals(IndustryType(o.File), type, StringComparison.OrdinalIgnoreCase))?.File;
            if (file is null) return Error($"nieznany rodzaj zakładu „{type}” (na tej mapie takich nie ma)");
        }

        var key = TargetResolver.Normalize(name);
        var same = objects.FirstOrDefault(o => o.Kind == (industry ? GameObjectKind.Industry : GameObjectKind.Town) && TargetResolver.Normalize(o.Name) == key);
        if (same is not null) return Error($"{(industry ? "zakład" : "miasto")} o nazwie {same.Name} już jest");

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
        var what = kind == GameObjectKind.Town ? "miasta" : "zakładu";
        return string.Create(CultureInfo.InvariantCulture,
            $"za blisko {what} {nearest.Object.Name} ({nearest.Dist:0} m, a trzeba co najmniej {min:0} m)");
    }

    private static Result Error(string why) => new(null, why);
}
