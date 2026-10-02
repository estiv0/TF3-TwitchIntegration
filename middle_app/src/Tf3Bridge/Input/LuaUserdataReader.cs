using System.Text;

namespace Tf3Bridge.Input;

/// <summary>
/// Reads files written by the mod through <c>app.saveUserdata(dir, name, { json = "..." })</c>.
/// The game serializes them as:
/// <code>
/// function data()
/// return {
///     json = "{\"v\":5,...}",
/// }
/// end
/// </code>
/// The JSON is a Lua string literal; this class extracts and unescapes it.
/// </summary>
public static class LuaUserdataReader
{
    private static readonly byte[] JsonKey = "json = \""u8.ToArray();
    private static readonly byte[] EndKeyword = "end"u8.ToArray();

    /// <summary>
    /// Returns the embedded JSON, or null when the file is incomplete (still being written)
    /// or does not contain a <c>json</c> field.
    /// </summary>
    public static string? TryExtractJson(ReadOnlySpan<byte> file)
    {
        if (!EndsWithEnd(file)) return null;

        var start = file.IndexOf(JsonKey);
        if (start < 0) return null;

        var bytes = UnescapeLuaString(file[(start + JsonKey.Length)..], out var closed);
        return closed ? Encoding.UTF8.GetString(bytes) : null;
    }

    private static bool EndsWithEnd(ReadOnlySpan<byte> file)
    {
        var trimmed = file.TrimEnd(" \t\r\n"u8);
        return trimmed.EndsWith(EndKeyword);
    }

    /// <summary>
    /// Decodes a Lua double-quoted string body starting right after the opening quote.
    /// Lua strings are byte strings, so decoding works on bytes and UTF-8 passes through.
    /// </summary>
    internal static byte[] UnescapeLuaString(ReadOnlySpan<byte> s, out bool closed)
    {
        var output = new List<byte>(s.Length);
        closed = false;
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == (byte)'"')
            {
                closed = true;
                break;
            }
            if (c != (byte)'\\')
            {
                output.Add(c);
                i++;
                continue;
            }

            if (i + 1 >= s.Length) break;
            var e = s[i + 1];
            i += 2;
            switch (e)
            {
                case (byte)'n': output.Add((byte)'\n'); break;
                case (byte)'r': output.Add((byte)'\r'); break;
                case (byte)'t': output.Add((byte)'\t'); break;
                case (byte)'a': output.Add(0x07); break;
                case (byte)'b': output.Add(0x08); break;
                case (byte)'f': output.Add(0x0C); break;
                case (byte)'v': output.Add(0x0B); break;
                case (byte)'\\': output.Add((byte)'\\'); break;
                case (byte)'"': output.Add((byte)'"'); break;
                case (byte)'\'': output.Add((byte)'\''); break;
                case (byte)'\n': output.Add((byte)'\n'); break; // escaped line break
                case (byte)'\r':
                    output.Add((byte)'\n');
                    if (i < s.Length && s[i] == (byte)'\n') i++;
                    break;
                case (byte)'x':
                    if (i + 2 > s.Length) return output.ToArray();
                    output.Add(Convert.ToByte(Encoding.ASCII.GetString(s.Slice(i, 2)), 16));
                    i += 2;
                    break;
                case (byte)'z':
                    while (i < s.Length && char.IsWhiteSpace((char)s[i])) i++;
                    break;
                default:
                    if (e >= (byte)'0' && e <= (byte)'9')
                    {
                        // \ddd: up to three decimal digits
                        var value = e - (byte)'0';
                        for (var n = 0; n < 2 && i < s.Length && s[i] >= (byte)'0' && s[i] <= (byte)'9'; n++, i++)
                            value = value * 10 + (s[i] - (byte)'0');
                        output.Add((byte)value);
                    }
                    else
                    {
                        output.Add(e); // unknown escape: keep the character
                    }
                    break;
            }
        }
        return output.ToArray();
    }
}
