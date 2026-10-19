using System.Globalization;
using Tf3Bridge.Output;

namespace Tf3Bridge.Processing;

/// <summary>What the ground looks like around one point: shares of the town area under water and too steep.</summary>
public readonly record struct TownSite(double Water, double Steep, bool OnWater);

/// <summary>
/// For every heightmap sample: how much of the area a new town would grow into is water or steep slope.
/// A town founded on a mountainside or a lake is created by the game, but it never gets a single
/// street or house (it stays at size 0), so such places are refused before the viewer spends points.
/// Published as latest/terrain-site.png for the site (web/js/build.js has the same thresholds).
/// </summary>
public sealed class TownSites
{
    /// <summary>Half side of the square around the point that the town needs (m).</summary>
    public const double Radius = 250;
    /// <summary>Ground steeper than this (rise / run) counts as steep.</summary>
    public const double SteepSlope = 0.15;
    /// <summary>A town is refused when a larger share of its area is water / steep.</summary>
    public const double MaxWater = 0.3, MaxSteep = 0.5;

    private readonly TerrainGrid _grid;
    private readonly byte[] _water; // 0..255 = share of water around the sample
    private readonly byte[] _steep; // 0..255 = share of steep land around the sample

    private TownSites(TerrainGrid grid, byte[] water, byte[] steep)
    {
        _grid = grid;
        _water = water;
        _steep = steep;
    }

    public static TownSites Compute(TerrainGrid g)
    {
        int nx = g.Nx, ny = g.Ny;
        var w = g.WaterLevel;
        // Prefix sums (one extra row/column of zeros) -> any square window in O(1).
        var water = new int[(nx + 1) * (ny + 1)];
        var steep = new int[(nx + 1) * (ny + 1)];
        for (var j = 0; j < ny; j++)
        {
            for (var i = 0; i < nx; i++)
            {
                var h = g.At(i, j);
                var wet = w is { } level && h < level;
                var dzdx = (g.At(Math.Min(i + 1, nx - 1), j) - g.At(Math.Max(i - 1, 0), j)) / (2 * g.Step);
                var dzdy = (g.At(i, Math.Min(j + 1, ny - 1)) - g.At(i, Math.Max(j - 1, 0))) / (2 * g.Step);
                var isSteep = !wet && dzdx * dzdx + dzdy * dzdy > SteepSlope * SteepSlope;
                var k = (j + 1) * (nx + 1) + i + 1;
                water[k] = (wet ? 1 : 0) + water[k - 1] + water[k - nx - 1] - water[k - nx - 2];
                steep[k] = (isSteep ? 1 : 0) + steep[k - 1] + steep[k - nx - 1] - steep[k - nx - 2];
            }
        }

        var r = Math.Max(1, (int)Math.Round(Radius / g.Step));
        var outWater = new byte[nx * ny];
        var outSteep = new byte[nx * ny];
        for (var j = 0; j < ny; j++)
        {
            int j0 = Math.Max(0, j - r), j1 = Math.Min(ny, j + r + 1);
            for (var i = 0; i < nx; i++)
            {
                int i0 = Math.Max(0, i - r), i1 = Math.Min(nx, i + r + 1);
                double n = (i1 - i0) * (j1 - j0);
                outWater[j * nx + i] = Share(Sum(water, nx, i0, j0, i1, j1) / n);
                outSteep[j * nx + i] = Share(Sum(steep, nx, i0, j0, i1, j1) / n);
            }
        }
        return new TownSites(g, outWater, outSteep);
    }

    private static int Sum(int[] s, int nx, int i0, int j0, int i1, int j1) =>
        s[j1 * (nx + 1) + i1] - s[j0 * (nx + 1) + i1] - s[j1 * (nx + 1) + i0] + s[j0 * (nx + 1) + i0];

    private static byte Share(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

    /// <summary>The nearest sample; null outside the heightmap.</summary>
    public TownSite? At(double x, double y)
    {
        var i = (int)Math.Round((x - _grid.X0) / _grid.Step);
        var j = (int)Math.Round((y - _grid.Y0) / _grid.Step);
        if (i < 0 || j < 0 || i >= _grid.Nx || j >= _grid.Ny) return null;
        var k = j * _grid.Nx + i;
        var onWater = _grid.WaterLevel is { } level && _grid.At(i, j) < level;
        return new TownSite(_water[k] / 255.0, _steep[k] / 255.0, onWater);
    }

    /// <summary>Why a town cannot be founded here (for the chat, in the app language); null = the ground is fine.</summary>
    public static string? TownProblem(TownSite s) =>
        s.OnWater || s.Water > MaxWater
            ? Loc.T($"that spot is on or right next to water ({Percent(s.Water)} water around), a town won't get built there",
                $"to miejsce jest na wodzie albo tuż przy niej ({Percent(s.Water)} wody wokół), miasto się tu nie zbuduje")
        : s.Steep > MaxSteep
            ? Loc.T($"the ground is too steep here ({Percent(s.Steep)} slopes around), a town won't get built there",
                $"teren jest tu za stromy ({Percent(s.Steep)} zboczy wokół), miasto się tu nie zbuduje")
        : null;

    private static string Percent(double share) => ((int)Math.Round(share * 100)).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>One pixel per sample, north up (like terrain.png): R = water share, G = steep share, B = 255 when the sample is under water.</summary>
    public byte[] RenderPng()
    {
        var g = _grid;
        var rgb = new byte[g.Nx * g.Ny * 3];
        for (var row = 0; row < g.Ny; row++)
        {
            var j = g.Ny - 1 - row;
            for (var i = 0; i < g.Nx; i++)
            {
                var k = j * g.Nx + i;
                var o = (row * g.Nx + i) * 3;
                rgb[o] = _water[k];
                rgb[o + 1] = _steep[k];
                rgb[o + 2] = g.WaterLevel is { } level && g.At(i, j) < level ? (byte)255 : (byte)0;
            }
        }
        return PngWriter.EncodeRgb(g.Nx, g.Ny, rgb);
    }
}
