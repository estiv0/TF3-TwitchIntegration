using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Tf3Bridge.Output;

/// <summary>Minimal PNG encoder for 8-bit RGB images (no external packages).</summary>
public static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <param name="rgb">width * height * 3 bytes, rows top to bottom.</param>
    public static byte[] EncodeRgb(int width, int height, ReadOnlySpan<byte> rgb)
    {
        if (rgb.Length != width * height * 3) throw new ArgumentException("rgb length must be width * height * 3");

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // color type: RGB
        WriteChunk(png, "IHDR", ihdr);

        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            {
                // Filter "Up" per row: terrain rows are similar, so this compresses noticeably better than none.
                var stride = width * 3;
                var line = new byte[stride + 1];
                line[0] = 2;
                for (var y = 0; y < height; y++)
                {
                    var row = rgb.Slice(y * stride, stride);
                    for (var i = 0; i < stride; i++)
                        line[i + 1] = y == 0 ? row[i] : (byte)(row[i] - rgb[(y - 1) * stride + i]);
                    z.Write(line);
                }
            }
            WriteChunk(png, "IDAT", raw.ToArray());
        }

        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>
    /// 8-bit RGBA. The filter is chosen per row (smallest sum of absolute values, like libpng), which keeps
    /// icons with transparent areas small.
    /// </summary>
    /// <param name="rgba">width * height * 4 bytes, rows top to bottom.</param>
    public static byte[] EncodeRgba(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (rgba.Length != width * height * 4) throw new ArgumentException("rgba length must be width * height * 4");

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // color type: RGBA
        WriteChunk(png, "IHDR", ihdr);

        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                var stride = width * 4;
                var prev = new byte[stride];
                var best = new byte[stride + 1];
                var line = new byte[stride + 1];
                for (var y = 0; y < height; y++)
                {
                    var row = rgba.Slice(y * stride, stride);
                    var bestScore = long.MaxValue;
                    for (byte kind = 0; kind < 5; kind++)
                    {
                        line[0] = kind;
                        long score = 0;
                        for (var i = 0; i < stride; i++)
                        {
                            int a = i >= 4 ? row[i - 4] : 0, b = prev[i], c = i >= 4 ? prev[i - 4] : 0;
                            var pred = kind switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) >> 1, _ => Paeth(a, b, c) };
                            var v = (byte)(row[i] - pred);
                            line[i + 1] = v;
                            score += v < 128 ? v : 256 - v;
                        }
                        if (score < bestScore)
                        {
                            bestScore = score;
                            line.CopyTo(best, 0);
                        }
                    }
                    z.Write(best);
                    row.CopyTo(prev);
                }
            }
            WriteChunk(png, "IDAT", raw.ToArray());
        }

        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, data.Length);
        s.Write(buf);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc(Crc(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(buf, crc);
        s.Write(buf);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
