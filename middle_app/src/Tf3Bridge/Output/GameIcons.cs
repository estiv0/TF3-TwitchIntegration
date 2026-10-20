using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Tf3Bridge.Output;

/// <summary>
/// The game icons for the local website, extracted from the player's own TF3 install into
/// IconCacheDir\img\{stations,resources,vehicles}\*.png, the same files as tools/extract_game_icons.py
/// writes to web/img/. The artwork belongs to Urban Games, so it is never built into the exe. Runs again
/// only when the game's packages changed (stamp.txt).
/// </summary>
public sealed class GameIcons(IOptions<BridgeOptions> options, ILogger<GameIcons> logger)
{
    // A new value re-extracts everything (change it when the output changes).
    private const string FormatVersion = "1";
    private const string VehicleSuffix = "_icon_small@2x.tga";
    private const string GuiIcons = "gui/hud/icons/";
    private const int MaxVehicleWidth = 512; // wider ones (long consists, ships) are scaled down, keeping the aspect ratio

    /// <summary>The folder that has img\ inside, a root for <see cref="LocalWebServer"/>.</summary>
    public string Dir { get; } = options.Value.IconCacheDir;

    public Task UpdateAsync(CancellationToken ct) => Task.Run(() =>
    {
        var game = options.Value.GameDir;
        if (game.Length == 0)
        {
            logger.LogWarning("Game icons: Transport Fever 3 install not found in Steam, the local website shows no vehicle and cargo icons "
                + "(set --Bridge:GameDir=<Steam library>\\steamapps\\common\\Transport Fever 3)");
            return;
        }
        try
        {
            var sw = Stopwatch.StartNew();
            var count = Update(game, Dir, ct);
            if (count is { } n) logger.LogInformation("Game icons: {Count} extracted from {Game} to {Dir} in {Seconds:0.0} s", n, game, Dir, sw.Elapsed.TotalSeconds);
            else logger.LogInformation("Game icons: up to date in {Dir}", Dir);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Game icons: extraction from {Game} failed", game);
        }
    }, ct);

    /// <summary>Extracts into dir\img when the packages changed; the number of icons, or null when up to date.</summary>
    internal static int? Update(string game, string dir, CancellationToken ct = default)
    {
        var sources = new (string Folder, List<(string Key, string Zip, string Entry)> Icons, int MaxWidth)[]
        {
            ("stations", GuiIconList(game), 0),
            ("resources", CargoIconList(game), 0),
            ("vehicles", VehicleIconList(game), MaxVehicleWidth),
        };
        var zips = sources.SelectMany(s => s.Icons.Select(i => i.Zip)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        var stamp = Stamp(zips);
        var stampFile = Path.Combine(dir, "stamp.txt");
        var img = Path.Combine(dir, "img");
        if (Directory.Exists(img) && File.Exists(stampFile) && File.ReadAllText(stampFile) == stamp) return null;

        // Into a new folder, then swap: the website keeps the old icons until the new ones are complete.
        var fresh = Path.Combine(dir, "img.new");
        if (Directory.Exists(fresh)) Directory.Delete(fresh, recursive: true);
        var count = 0;
        foreach (var (folder, icons, maxWidth) in sources)
        {
            var outDir = Directory.CreateDirectory(Path.Combine(fresh, folder)).FullName;
            var done = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in icons.GroupBy(i => i.Zip))
            {
                using var zip = ZipFile.OpenRead(group.Key);
                foreach (var (key, _, entry) in group)
                {
                    ct.ThrowIfCancellationRequested();
                    if (done.Contains(key) || zip.GetEntry(entry) is not { } e) continue; // the same model in several packages: the first wins
                    byte[] data;
                    using (var s = e.Open())
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        data = ms.ToArray();
                    }
                    if (ReadTga(data) is not var (w, h, rgba)) continue; // a format the website doesn't need
                    if (maxWidth > 0) (w, h, rgba) = Downscale(w, h, rgba, maxWidth);
                    File.WriteAllBytes(Path.Combine(outDir, key + ".png"), PngWriter.EncodeRgba(w, h, rgba));
                    done.Add(key);
                }
            }
            count += done.Count;
        }

        if (Directory.Exists(img)) Directory.Delete(img, recursive: true);
        Directory.Move(fresh, img);
        File.WriteAllText(stampFile, stamp);
        return count;
    }

    // Lists in the order the script uses, so the same package wins for a model found twice.
    private static List<(string, string, string)> VehicleIconList(string game)
    {
        var vehicleDir = Path.Combine(game, "base", "content", "vehicle");
        var dlcs = Path.Combine(game, "dlcs");
        IEnumerable<string> baseZips = Directory.Exists(vehicleDir)
            ? Directory.GetFiles(vehicleDir, "*.zip", SearchOption.AllDirectories).Order(StringComparer.Ordinal) : [];
        IEnumerable<string> dlcZips = Directory.Exists(dlcs)
            ? Directory.GetDirectories(dlcs).Order(StringComparer.Ordinal).Select(d => Path.Combine(d, "content", "vehicle.zip")).Where(File.Exists) : [];
        var zips = baseZips.Concat(dlcZips);
        var list = new List<(string, string, string)>();
        foreach (var z in zips)
        {
            using var zip = ZipFile.OpenRead(z);
            foreach (var e in zip.Entries)
                if (e.FullName.EndsWith(VehicleSuffix, StringComparison.Ordinal))
                    list.Add((e.FullName[(e.FullName.LastIndexOf('/') + 1)..^VehicleSuffix.Length], z, e.FullName));
        }
        return list;
    }

    private static List<(string, string, string)> CargoIconList(string game)
    {
        var dir = Path.Combine(game, "base", "content", "cargos");
        if (!Directory.Exists(dir)) return [];
        // cargos\ also has shared packages (classes, icons, ...) without an icon; those entries are skipped later
        return [.. Directory.GetFiles(dir, "*.zip").Order(StringComparer.Ordinal)
            .Select(z => Path.GetFileNameWithoutExtension(z))
            .Select(name => (name, Path.Combine(dir, name + ".zip"), $"{name}/{name}@2x.tga"))];
    }

    private static List<(string, string, string)> GuiIconList(string game)
    {
        var z = Path.Combine(game, "base", "content", "gui.zip");
        if (!File.Exists(z)) return [];
        using var zip = ZipFile.OpenRead(z);
        return [.. zip.Entries
            .Where(e => e.FullName.StartsWith(GuiIcons, StringComparison.Ordinal) && e.FullName.IndexOf('/', GuiIcons.Length) < 0
                && e.FullName.EndsWith("@2x.tga", StringComparison.Ordinal))
            .Select(e => (e.FullName[GuiIcons.Length..^"@2x.tga".Length], z, e.FullName))];
    }

    private static string Stamp(IEnumerable<string> zips)
    {
        var text = new StringBuilder(FormatVersion).Append('\n');
        foreach (var z in zips)
        {
            var info = new FileInfo(z);
            text.Append(z).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// True-color TGA (type 2/10, 24/32 bpp) or grayscale (type 3/11, 8 bpp), with or without RLE → RGBA rows
    /// top-down; null for other formats. Grayscale ones are interface icon masks: white with alpha = brightness.
    /// </summary>
    internal static (int W, int H, byte[] Rgba)? ReadTga(byte[] data)
    {
        if (data.Length < 18) return null;
        int idLen = data[0], cmapType = data[1], type = data[2];
        int w = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12)), h = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14));
        int bpp = data[16], desc = data[17];
        var gray = type is 3 or 11;
        if (cmapType != 0 || !((type is 2 or 10 && bpp is 24 or 32) || (gray && bpp == 8))) return null;
        var px = bpp / 8;
        var pos = 18 + idLen;
        var size = w * h * px;

        byte[] raw;
        if (type is 2 or 3)
        {
            if (pos + size > data.Length) return null;
            raw = data.AsSpan(pos, size).ToArray();
        }
        else
        {
            raw = new byte[size];
            var o = 0;
            while (o < size)
            {
                if (pos >= data.Length) return null;
                var head = data[pos++];
                var count = (head & 0x7F) + 1;
                var n = Math.Min(count * px, size - o);
                if ((head & 0x80) != 0)
                {
                    if (pos + px > data.Length) return null;
                    for (var i = 0; i < n; i++) raw[o + i] = data[pos + i % px];
                    pos += px;
                }
                else
                {
                    if (pos + n > data.Length) return null;
                    Array.Copy(data, pos, raw, o, n);
                    pos += count * px;
                }
                o += n;
            }
        }

        var rgba = new byte[w * h * 4];
        var bottomUp = (desc & 0x20) == 0; // origin in the bottom left corner
        for (var y = 0; y < h; y++)
        {
            var src = y * w * px;
            var dst = (bottomUp ? h - 1 - y : y) * w * 4;
            for (var x = 0; x < w; x++, src += px, dst += 4)
            {
                if (gray)
                {
                    rgba[dst] = rgba[dst + 1] = rgba[dst + 2] = 255;
                    rgba[dst + 3] = raw[src];
                }
                else
                {
                    rgba[dst] = raw[src + 2]; // BGR(A) -> RGB(A)
                    rgba[dst + 1] = raw[src + 1];
                    rgba[dst + 2] = raw[src];
                    rgba[dst + 3] = px == 4 ? raw[src + 3] : (byte)255;
                }
            }
        }
        return (w, h, rgba);
    }

    /// <summary>
    /// Shrinks an image wider than maxW to that width, keeping the aspect ratio: a box filter on
    /// premultiplied alpha, so transparent pixels add no dark rims.
    /// </summary>
    internal static (int W, int H, byte[] Rgba) Downscale(int w, int h, byte[] rgba, int maxW)
    {
        if (w <= maxW) return (w, h, rgba);
        int nw = maxW, nh = Math.Max(1, (int)Math.Round(h * (double)maxW / w, MidpointRounding.ToEven));

        var pre = new double[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            var a = rgba[i * 4 + 3] / 255.0;
            pre[i * 4] = rgba[i * 4] * a;
            pre[i * 4 + 1] = rgba[i * 4 + 1] * a;
            pre[i * 4 + 2] = rgba[i * 4 + 2] * a;
            pre[i * 4 + 3] = rgba[i * 4 + 3];
        }
        var horizontal = Resample(pre, w, h, nw, horizontalAxis: true); // nw x h
        var both = Resample(horizontal, nw, h, nh, horizontalAxis: false); // nw x nh

        var result = new byte[nw * nh * 4];
        for (var i = 0; i < nw * nh; i++)
        {
            var a = both[i * 4 + 3];
            var k = a > 0 ? 255 / a : 0;
            for (var c = 0; c < 3; c++) result[i * 4 + c] = ToByte(both[i * 4 + c] * k);
            result[i * 4 + 3] = ToByte(a);
        }
        return (nw, nh, result);
    }

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v, MidpointRounding.ToEven), 0, 255);

    // One axis of the box filter: each output pixel averages the input pixels it covers, weighted by coverage.
    private static double[] Resample(double[] src, int w, int h, int nOut, bool horizontalAxis)
    {
        var nIn = horizontalAxis ? w : h;
        var scale = (double)nIn / nOut;
        var spans = new List<(int Index, double Weight)>[nOut];
        for (var o = 0; o < nOut; o++)
        {
            double lo = o * scale, hi = (o + 1) * scale;
            var span = new List<(int, double)>();
            for (var i = (int)lo; i < hi && i < nIn; i++)
            {
                var weight = Math.Min(hi, i + 1) - Math.Max(lo, i);
                if (weight > 0) span.Add((i, weight / scale));
            }
            spans[o] = span;
        }

        int ow = horizontalAxis ? nOut : w, oh = horizontalAxis ? h : nOut;
        var dst = new double[ow * oh * 4];
        for (var y = 0; y < oh; y++)
        for (var x = 0; x < ow; x++)
        {
            var d = (y * ow + x) * 4;
            foreach (var (i, weight) in spans[horizontalAxis ? x : y])
            {
                var s = (horizontalAxis ? y * w + i : i * w + x) * 4;
                for (var c = 0; c < 4; c++) dst[d + c] += src[s + c] * weight;
            }
        }
        return dst;
    }
}
