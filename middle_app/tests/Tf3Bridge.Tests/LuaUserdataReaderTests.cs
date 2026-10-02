using System.Text;
using Tf3Bridge.Input;

namespace Tf3Bridge.Tests;

public class LuaUserdataReaderTests
{
    // Exact shape written by app.saveUserdata in TF3 (CRLF, tabs), taken from probe_gui.lua.
    private const string ProbeFile =
        "function data()\r\nreturn { \r\n\t\tjson = \"{\\\"probe\\\":\\\"gui\\\",\\\"text\\\":\\\"zażółć \\\\\\\"cytat\\\\\\\"\\\"}\",\r\n\t\tn = 1,\r\n\t}\r\nend\r\n";

    [Fact]
    public void ExtractsJsonWritenByGame()
    {
        var json = LuaUserdataReader.TryExtractJson(Encoding.UTF8.GetBytes(ProbeFile));

        Assert.Equal("{\"probe\":\"gui\",\"text\":\"zażółć \\\"cytat\\\"\"}", json);
    }

    [Fact]
    public void ReturnsNullForIncompleteFile()
    {
        var truncated = ProbeFile[..(ProbeFile.Length / 2)];

        Assert.Null(LuaUserdataReader.TryExtractJson(Encoding.UTF8.GetBytes(truncated)));
    }

    [Fact]
    public void ReturnsNullWithoutJsonField()
    {
        const string file = "function data()\r\nreturn { \r\n\t\tn = 1,\r\n\t}\r\nend\r\n";

        Assert.Null(LuaUserdataReader.TryExtractJson(Encoding.UTF8.GetBytes(file)));
    }

    [Theory]
    [InlineData(@"a\nb""", "a\nb")]
    [InlineData(@"tab\there""", "tab\there")]
    [InlineData(@"back\\slash""", @"back\slash")]
    [InlineData(@"dec\065\066""", "decAB")]
    [InlineData(@"hex\x41""", "hexA")]
    [InlineData(@"quote\""q""", "quote\"q")]
    public void UnescapesLuaEscapes(string body, string expected)
    {
        var bytes = LuaUserdataReader.UnescapeLuaString(Encoding.UTF8.GetBytes(body), out var closed);

        Assert.True(closed);
        Assert.Equal(expected, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void DecimalEscapesFormUtf8()
    {
        // "ż" = C5 BC, as a serializer might write it with \ddd escapes
        var bytes = LuaUserdataReader.UnescapeLuaString(Encoding.UTF8.GetBytes(@"\197\188"""), out _);

        Assert.Equal("ż", Encoding.UTF8.GetString(bytes));
    }
}
