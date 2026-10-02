using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tf3Bridge.Output;
using Tf3Bridge.Processing;

namespace Tf3Bridge.Tests;

public sealed class TerrainTests : IDisposable
{
    private const string B64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tf3bridge-terrain-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Same encoding as lua_mod terrain.lua (encodeRow): q = round((h - zMin) / zScale), 3 chars, high bits first.</summary>
    private static string Encode(double[] heights, double zMin, double zScale)
    {
        var sb = new StringBuilder();
        foreach (var h in heights)
        {
            var q = (int)Math.Floor((h - zMin) / zScale + 0.5);
            sb.Append(B64[q >> 12]).Append(B64[(q >> 6) & 63]).Append(B64[q & 63]);
        }
        return sb.ToString();
    }

    private static string TerrainJson(int nx, int ny, double[] heights, double? waterLevel = 0)
    {
        var doc = new JsonObject
        {
            ["v"] = 5, ["kind"] = "terrain", ["session"] = "aa", ["seq"] = 1, ["method"] = "baseHeight",
            ["nx"] = nx, ["ny"] = ny, ["step"] = 32, ["x0"] = -64, ["y0"] = -32,
            ["zMin"] = heights.Min(), ["zMax"] = heights.Max(), ["zScale"] = 0.1,
            ["waterLevel"] = waterLevel, ["encoding"] = "b64x3",
            ["data"] = Encode(heights, heights.Min(), 0.1),
        };
        return doc.ToJsonString();
    }

    [Fact]
    public void DecodesHeightsRowMajorFromSouth()
    {
        double[] heights = [-12.3, 0, 5.5, 100, 2500.7, 7];
        var grid = TerrainGrid.Parse(JsonNode.Parse(TerrainJson(3, 2, heights))!.AsObject());

        Assert.Equal((3, 2), (grid.Nx, grid.Ny));
        Assert.Equal(-64, grid.X0);
        for (var k = 0; k < heights.Length; k++)
            Assert.Equal(heights[k], grid.Heights[k], 0.051);
        Assert.Equal(2500.7, grid.At(1, 1), 0.051);
    }

    [Fact]
    public void RejectsSizeMismatch()
    {
        var doc = JsonNode.Parse(TerrainJson(3, 2, [1, 2, 3, 4, 5, 6]))!.AsObject();
        doc["nx"] = 4;
        Assert.Throws<FormatException>(() => TerrainGrid.Parse(doc));
    }

    [Fact]
    public void RendersPngAndPlacement()
    {
        var heights = Enumerable.Range(0, 20 * 10).Select(k => (double)(k % 20) * 3 - 10).ToArray();
        var grid = TerrainGrid.Parse(JsonNode.Parse(TerrainJson(20, 10, heights))!.AsObject());

        var png = TerrainRenderer.RenderPng(grid);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png[..4]);
        Assert.Equal(20, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(10, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));

        var info = TerrainRenderer.Info(grid, "aa", 1);
        Assert.Equal(-80, info["minX"]!.GetValue<double>());
        Assert.Equal(-64 + 19 * 32 + 16, info["maxX"]!.GetValue<double>());
        Assert.Equal(-48, info["minY"]!.GetValue<double>());
    }

    [Fact]
    public async Task ProcessorPublishesTerrainAndMeta()
    {
        var input = Path.Combine(_root, "in");
        var output = Path.Combine(_root, "out");
        Directory.CreateDirectory(input);
        var options = Options.Create(new BridgeOptions { InputDir = input, OutputDir = output });
        var processor = new ExportProcessor(options, new LocalFolderPublisher(options), NullLogger<ExportProcessor>.Instance);

        var json = TerrainJson(3, 2, [1, 2, 3, 4, 5, 6], waterLevel: null);
        File.WriteAllText(Path.Combine(input, "terrain_aa_000001.lua"),
            $"function data()\r\nreturn {{ \r\n\t\tjson = \"{json.Replace("\"", "\\\"")}\",\r\n\t}}\r\nend\r\n");

        await processor.ProcessOnceAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(output, "latest", "terrain.png")));
        var info = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "latest", "terrain.json")))!;
        Assert.Equal(3, info["width"]!.GetValue<int>());
        Assert.Null(info["waterLevel"]);
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "latest", "meta.json")))!;
        Assert.Equal(1, meta["terrainSeq"]!.GetValue<long>());
        Assert.Null(meta["networkSeq"]);
        Assert.Empty(Directory.GetFiles(input, "*.lua"));
    }
}
