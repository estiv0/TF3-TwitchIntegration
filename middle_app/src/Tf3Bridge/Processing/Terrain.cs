using System.Text.Json.Nodes;
using Tf3Bridge.Output;

namespace Tf3Bridge.Processing;

/// <summary>
/// Heightmap from a "terrain" file (lua_mod terrain.lua): nx * ny samples, sample (i, j) at
/// world (X0 + i*Step, Y0 + j*Step), rows from j = 0 (south, min y).
/// </summary>
public sealed record TerrainGrid(
    int Nx, int Ny, double Step, double X0, double Y0, double ZMin, double ZMax, double? WaterLevel, float[] Heights)
{
    public float At(int i, int j) => Heights[j * Nx + i];

    public static TerrainGrid Parse(JsonObject doc)
    {
        var encoding = JsonRead.String(doc["encoding"]);
        if (encoding != "b64x3") throw new FormatException($"unsupported terrain encoding '{encoding}'");

        var nx = (int)(JsonRead.Long(doc["nx"]) ?? 0);
        var ny = (int)(JsonRead.Long(doc["ny"]) ?? 0);
        var data = JsonRead.String(doc["data"]) ?? "";
        if (nx <= 0 || ny <= 0 || data.Length != nx * ny * 3)
            throw new FormatException($"terrain size mismatch: {nx} x {ny}, {data.Length} chars");

        var zMin = Number(doc["zMin"]);
        var zScale = Number(doc["zScale"]);
        var heights = new float[nx * ny];
        for (var k = 0; k < heights.Length; k++)
        {
            var q = (B64(data[3 * k]) << 12) | (B64(data[3 * k + 1]) << 6) | B64(data[3 * k + 2]);
            heights[k] = (float)(zMin + q * zScale);
        }

        return new TerrainGrid(nx, ny, Number(doc["step"]), Number(doc["x0"]), Number(doc["y0"]),
            zMin, Number(doc["zMax"]), NumberOrNull(doc["waterLevel"]), heights);
    }

    private static int B64(char c) => c switch
    {
        >= 'A' and <= 'Z' => c - 'A',
        >= 'a' and <= 'z' => c - 'a' + 26,
        >= '0' and <= '9' => c - '0' + 52,
        '+' => 62,
        '/' => 63,
        _ => throw new FormatException($"invalid base64 character '{c}'"),
    };

    private static double Number(JsonNode? node) =>
        NumberOrNull(node) ?? throw new FormatException("missing number in terrain header");

    private static double? NumberOrNull(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out double d) ? d : null;
}

/// <summary>
/// Renders the heightmap as a muted shaded-relief PNG for the dark map background
/// (one pixel per sample, north up) plus the JSON that places it in world coordinates.
/// </summary>
public static class TerrainRenderer
{
    // Land colors by relative height (dark, low contrast: the network must stay readable on top).
    private static readonly (double At, double R, double G, double B)[] LandRamp =
    [
        (0.00, 30, 44, 36),
        (0.30, 42, 54, 40),
        (0.65, 62, 60, 48),
        (1.00, 100, 98, 94),
    ];

    private static readonly (double R, double G, double B) WaterShallow = (24, 48, 70);
    private static readonly (double R, double G, double B) WaterDeep = (12, 24, 40);
    private const double WaterDepthRange = 40; // m to the darkest water

    private const double Exaggeration = 2.5;
    // light from north-west, 45° above the horizon (x east, y north, z up)
    private static readonly (double X, double Y, double Z) Light = Normalize(-1, 1, Math.Sqrt(2));

    public static byte[] RenderPng(TerrainGrid g)
    {
        var rgb = new byte[g.Nx * g.Ny * 3];
        var landBase = g.WaterLevel ?? g.ZMin;
        var landRange = Math.Max(1, g.ZMax - landBase);
        var flat = Light.Z;

        for (var row = 0; row < g.Ny; row++)
        {
            var j = g.Ny - 1 - row; // image top = north (max y)
            for (var i = 0; i < g.Nx; i++)
            {
                var h = g.At(i, j);
                var dzdx = (g.At(Math.Min(i + 1, g.Nx - 1), j) - g.At(Math.Max(i - 1, 0), j)) / (2 * g.Step);
                var dzdy = (g.At(i, Math.Min(j + 1, g.Ny - 1)) - g.At(i, Math.Max(j - 1, 0))) / (2 * g.Step);
                var n = Normalize(-dzdx * Exaggeration, -dzdy * Exaggeration, 1);
                var shade = Math.Max(0, n.X * Light.X + n.Y * Light.Y + n.Z * Light.Z) / flat;

                (double R, double G, double B) c;
                if (g.WaterLevel is { } water && h < water)
                {
                    var t = Math.Clamp((water - h) / WaterDepthRange, 0, 1);
                    c = Lerp(WaterShallow, WaterDeep, t);
                    shade = 1; // flat water surface
                }
                else
                {
                    c = Ramp((h - landBase) / landRange);
                }

                var k = Math.Clamp(0.35 + 0.65 * shade, 0.2, 1.6);
                var o = (row * g.Nx + i) * 3;
                rgb[o] = ToByte(c.R * k);
                rgb[o + 1] = ToByte(c.G * k);
                rgb[o + 2] = ToByte(c.B * k);
            }
        }
        return PngWriter.EncodeRgb(g.Nx, g.Ny, rgb);
    }

    /// <summary>Image placement: pixel centers are the samples, so the image extends half a step beyond them.</summary>
    public static JsonObject Info(TerrainGrid g, string? session, long seq) => new()
    {
        ["session"] = session,
        ["seq"] = seq,
        ["width"] = g.Nx,
        ["height"] = g.Ny,
        ["step"] = g.Step,
        ["minX"] = g.X0 - g.Step / 2,
        ["maxX"] = g.X0 + (g.Nx - 1) * g.Step + g.Step / 2,
        ["minY"] = g.Y0 - g.Step / 2,
        ["maxY"] = g.Y0 + (g.Ny - 1) * g.Step + g.Step / 2,
        ["zMin"] = g.ZMin,
        ["zMax"] = g.ZMax,
        ["waterLevel"] = g.WaterLevel,
    };

    private static (double R, double G, double B) Ramp(double t)
    {
        t = Math.Clamp(t, 0, 1);
        for (var s = 1; s < LandRamp.Length; s++)
        {
            var (a, b) = (LandRamp[s - 1], LandRamp[s]);
            if (t <= b.At)
                return Lerp((a.R, a.G, a.B), (b.R, b.G, b.B), (t - a.At) / (b.At - a.At));
        }
        var last = LandRamp[^1];
        return (last.R, last.G, last.B);
    }

    private static (double R, double G, double B) Lerp((double R, double G, double B) a, (double R, double G, double B) b, double t) =>
        (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    private static (double X, double Y, double Z) Normalize(double x, double y, double z)
    {
        var len = Math.Sqrt(x * x + y * y + z * z);
        return (x / len, y / len, z / len);
    }

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
}
