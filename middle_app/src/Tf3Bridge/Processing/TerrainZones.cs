using Tf3Bridge.Output;

namespace Tf3Bridge.Processing;

/// <summary>Kind of region an industry may require (Rewards/IndustryZones.cs maps industry types to these).</summary>
public enum IndustryZone { Any, Mountains, NearWater, Lowland, Inland }

/// <summary>
/// Regions of the map from the heightmap, judged over a wide window (±<see cref="Radius"/>), so a mine may
/// stand in a flat valley as long as the valley is in the mountains:
/// <list type="bullet">
/// <item>mountains: rugged around, i.e. the average slope in the window is at least <see cref="MinMountainSlope"/>,
/// and the land is among the <see cref="MaxMountainShare"/> most rugged of the map, whatever the map,</item>
/// <item>near water: some water within the window,</item>
/// <item>lowland: almost flat around (average slope ≤ <see cref="MaxLowlandSlope"/>),</item>
/// <item>inland: no water within the window.</item>
/// </list>
/// The average slope (not the spread of heights) is used, so the core of a range counts as mountains more
/// than the foot of it, where a window half on the plain also sees a big height difference.
/// A region that is nowhere on the map (e.g. no mountains on a flat map) does not restrict anything,
/// otherwise its industries could not be built at all. Published as latest/terrain-zone.png
/// (R = mountains, G = near water, B = lowland; 255 or 0) for the site (web/js/build.js).
/// </summary>
public sealed class TerrainZones
{
    public const double Radius = 1000;
    /// <summary>Average slope (rise / run) from which land may count as mountains.</summary>
    public const double MinMountainSlope = 0.08;
    public const double MaxMountainShare = 0.15;
    public const double MaxLowlandSlope = 0.03;

    private const byte MountainsBit = 1, NearWaterBit = 2, LowlandBit = 4, OnWaterBit = 8;

    private readonly TerrainGrid _grid;
    private readonly byte[] _flags;
    private readonly int[] _landCount = new int[5]; // per IndustryZone: land samples in the zone

    /// <summary>Average slope from which land counts as mountains on this map.</summary>
    public double MountainSlope { get; }

    private TerrainZones(TerrainGrid grid, byte[] flags, double mountainSlope)
    {
        _grid = grid;
        _flags = flags;
        MountainSlope = mountainSlope;
        foreach (var f in flags)
        {
            if ((f & OnWaterBit) != 0) continue;
            _landCount[(int)IndustryZone.Any]++;
            foreach (var zone in (IndustryZone[])[IndustryZone.Mountains, IndustryZone.NearWater, IndustryZone.Lowland, IndustryZone.Inland])
                if (Matches(f, zone)) _landCount[(int)zone]++;
        }
    }

    public static TerrainZones Compute(TerrainGrid g)
    {
        int nx = g.Nx, ny = g.Ny, stride = nx + 1;
        var level = g.WaterLevel;
        // Prefix sums of the slope and of water samples; water is flat (its surface, not the bottom).
        var slope = new double[stride * (ny + 1)];
        var water = new int[stride * (ny + 1)];
        for (var j = 0; j < ny; j++)
        {
            for (var i = 0; i < nx; i++)
            {
                var wet = level is { } l && g.At(i, j) < l;
                var dzdx = (g.At(Math.Min(i + 1, nx - 1), j) - g.At(Math.Max(i - 1, 0), j)) / (2 * g.Step);
                var dzdy = (g.At(i, Math.Min(j + 1, ny - 1)) - g.At(i, Math.Max(j - 1, 0))) / (2 * g.Step);
                var s = wet ? 0 : Math.Sqrt(dzdx * dzdx + dzdy * dzdy);
                var k = (j + 1) * stride + i + 1;
                slope[k] = s + slope[k - 1] + slope[k - stride] - slope[k - stride - 1];
                water[k] = (wet ? 1 : 0) + water[k - 1] + water[k - stride] - water[k - stride - 1];
            }
        }

        var r = Math.Max(1, (int)Math.Round(Radius / g.Step));
        var rugged = new float[nx * ny]; // average slope in the window
        var nearWater = new bool[nx * ny];
        var landRugged = new List<float>(nx * ny);
        for (var j = 0; j < ny; j++)
        {
            int j0 = Math.Max(0, j - r), j1 = Math.Min(ny, j + r + 1);
            for (var i = 0; i < nx; i++)
            {
                int i0 = Math.Max(0, i - r), i1 = Math.Min(nx, i + r + 1);
                double n = (i1 - i0) * (j1 - j0);
                var k = j * nx + i;
                rugged[k] = (float)(Window(slope, stride, i0, j0, i1, j1) / n);
                nearWater[k] = Window(water, stride, i0, j0, i1, j1) > 0;
                if (!(level is { } l && g.At(i, j) < l)) landRugged.Add(rugged[k]);
            }
        }

        // Mountains = at most the MaxMountainShare most rugged land, and never gentle hills.
        var mountainSlope = MinMountainSlope;
        if (landRugged.Count > 0)
        {
            landRugged.Sort();
            mountainSlope = Math.Max(MinMountainSlope, landRugged[(int)Math.Min(landRugged.Count - 1, Math.Floor(landRugged.Count * (1 - MaxMountainShare)))]);
        }

        var flags = new byte[nx * ny];
        for (var j = 0; j < ny; j++)
        {
            for (var i = 0; i < nx; i++)
            {
                var k = j * nx + i;
                byte f = 0;
                if (rugged[k] >= mountainSlope) f |= MountainsBit;
                if (nearWater[k]) f |= NearWaterBit;
                if (rugged[k] <= MaxLowlandSlope) f |= LowlandBit;
                if (level is { } l && g.At(i, j) < l) f |= OnWaterBit;
                flags[k] = f;
            }
        }
        return new TerrainZones(g, flags, mountainSlope);
    }

    private static double Window(double[] s, int stride, int i0, int j0, int i1, int j1) =>
        s[j1 * stride + i1] - s[j0 * stride + i1] - s[j1 * stride + i0] + s[j0 * stride + i0];

    private static int Window(int[] s, int stride, int i0, int j0, int i1, int j1) =>
        s[j1 * stride + i1] - s[j0 * stride + i1] - s[j1 * stride + i0] + s[j0 * stride + i0];

    private static bool Matches(byte f, IndustryZone zone) => zone switch
    {
        IndustryZone.Mountains => (f & MountainsBit) != 0,
        IndustryZone.NearWater => (f & NearWaterBit) != 0,
        IndustryZone.Lowland => (f & LowlandBit) != 0,
        IndustryZone.Inland => (f & NearWaterBit) == 0,
        _ => true,
    };

    /// <summary>Share of the land in the zone (0..1).</summary>
    public double Coverage(IndustryZone zone) =>
        _landCount[(int)IndustryZone.Any] == 0 ? 0 : (double)_landCount[(int)zone] / _landCount[(int)IndustryZone.Any];

    /// <summary>False when the zone is nowhere on the map: then it does not restrict building.</summary>
    public bool Exists(IndustryZone zone) => _landCount[(int)zone] > 0;

    /// <summary>Whether (x, y) is in the zone; true outside the heightmap and for zones missing on this map.</summary>
    public bool Allows(IndustryZone zone, double x, double y)
    {
        if (zone == IndustryZone.Any || !Exists(zone)) return true;
        var i = (int)Math.Round((x - _grid.X0) / _grid.Step);
        var j = (int)Math.Round((y - _grid.Y0) / _grid.Step);
        if (i < 0 || j < 0 || i >= _grid.Nx || j >= _grid.Ny) return true;
        return Matches(_flags[j * _grid.Nx + i], zone);
    }

    /// <summary>One pixel per sample, north up (like terrain.png): R = mountains, G = near water, B = lowland (255 or 0).</summary>
    public byte[] RenderPng()
    {
        var g = _grid;
        var rgb = new byte[g.Nx * g.Ny * 3];
        for (var row = 0; row < g.Ny; row++)
        {
            var j = g.Ny - 1 - row;
            for (var i = 0; i < g.Nx; i++)
            {
                var f = _flags[j * g.Nx + i];
                var o = (row * g.Nx + i) * 3;
                rgb[o] = (f & MountainsBit) != 0 ? (byte)255 : (byte)0;
                rgb[o + 1] = (f & NearWaterBit) != 0 ? (byte)255 : (byte)0;
                rgb[o + 2] = (f & LowlandBit) != 0 ? (byte)255 : (byte)0;
            }
        }
        return PngWriter.EncodeRgb(g.Nx, g.Ny, rgb);
    }
}

/// <summary>Everything Tf3Bridge knows about the ground for build rewards (from the newest heightmap).</summary>
public sealed record BuildTerrain(TownSites Sites, TerrainZones Zones);
