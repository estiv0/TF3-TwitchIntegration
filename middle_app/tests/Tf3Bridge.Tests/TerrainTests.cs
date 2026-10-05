using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tf3Bridge.Output;
using Tf3Bridge.Processing;
using Tf3Bridge.Rewards;

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
        Assert.True(File.Exists(Path.Combine(output, "latest", "terrain-site.png")));
        Assert.NotNull(processor.BuildTerrain);
        Assert.True(File.Exists(Path.Combine(output, "latest", "terrain-zone.png")));
        var info = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "latest", "terrain.json")))!;
        Assert.Equal(3, info["width"]!.GetValue<int>());
        Assert.Null(info["waterLevel"]);
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "latest", "meta.json")))!;
        Assert.Equal(1, meta["terrainSeq"]!.GetValue<long>());
        Assert.Null(meta["networkSeq"]);
        Assert.Empty(Directory.GetFiles(input, "*.lua"));
    }

    // 60 x 30 samples, 32 m apart: west third sea (-5 m), middle third flat land (10 m), east third a 30% slope.
    private static TerrainGrid Grid(int nx, int ny, Func<int, int, double> height)
    {
        var heights = new double[nx * ny];
        for (var j = 0; j < ny; j++)
            for (var i = 0; i < nx; i++)
                heights[j * nx + i] = height(i, j);
        return TerrainGrid.Parse(JsonNode.Parse(TerrainJson(nx, ny, heights))!.AsObject());
    }

    private static BuildTerrain Terrain(TerrainGrid g) => new(TownSites.Compute(g), TerrainZones.Compute(g));

    private static TownSites ThreeStrips()
    {
        var heights = new double[60 * 30];
        for (var j = 0; j < 30; j++)
            for (var i = 0; i < 60; i++)
                heights[j * 60 + i] = i < 20 ? -5 : i < 40 ? 10 : 10 + (i - 39) * 32 * 0.3;
        return TownSites.Compute(TerrainGrid.Parse(JsonNode.Parse(TerrainJson(60, 30, heights))!.AsObject()));
    }

    private static double X(int i) => -64 + i * 32;
    private static double Y(int j) => -32 + j * 32;

    [Fact]
    public void TownSitesMeasureWaterAndSlopesAround()
    {
        var sites = ThreeStrips();

        var sea = sites.At(X(5), Y(15))!.Value;
        Assert.True(sea.OnWater);
        Assert.Equal(1, sea.Water, 0.01);
        var flat = sites.At(X(30), Y(15))!.Value;
        Assert.Equal((0, 0, false), (flat.Water, flat.Steep, flat.OnWater));
        var shore = sites.At(X(22), Y(15))!.Value; // window i = 14..30: 6 of 17 columns are sea
        Assert.False(shore.OnWater);
        Assert.Equal(6 / 17.0, shore.Water, 0.01);
        var hill = sites.At(X(52), Y(15))!.Value;
        Assert.Equal(1, hill.Steep, 0.01);
        Assert.Null(sites.At(X(-5), Y(15)));

        Assert.Contains("na wodzie", TownSites.TownProblem(sea));
        Assert.Contains("na wodzie", TownSites.TownProblem(shore)); // 35% wody > 30%
        Assert.Contains("za stromy (100% zboczy", TownSites.TownProblem(hill));
        Assert.Null(TownSites.TownProblem(flat));
    }

    [Fact]
    public void BuildCodeRefusesTownsOnWaterAndSlopesButNotIndustries()
    {
        var sites = new BuildTerrain(ThreeStrips(), TerrainZones.Compute(Grid(60, 30, (_, _) => 10)));
        Tf3Bridge.Commands.GameObject[] objects =
            [new(Tf3Bridge.Commands.GameObjectKind.Industry, 2, "Kopalnia", (-50000, 0), "::/industries/coal_mine/coal_mine.con")];

        Assert.NotNull(BuildCode.Parse($"[{X(30)}, {Y(15)}] Równina", false, objects, null, sites).Place);
        Assert.Contains("za stromy", BuildCode.Parse($"[{X(52)}, {Y(15)}] Szczyt", false, objects, null, sites).Error);
        Assert.Contains("na wodzie", BuildCode.Parse($"[{X(5)}, {Y(15)}] Atlantyda", false, objects, null, sites).Error);
        Assert.NotNull(BuildCode.Parse($"[{X(52)}, {Y(15)}, coal_mine] Kopalnia Jasia", true, objects, null, sites).Place);
    }

    // 200 x 60 samples, 32 m apart: sea (i < 20), flat land at 10 m (i < 120), then rugged mountains (i >= 175).
    private static TerrainGrid SeaPlainMountains() => Grid(200, 60, (i, j) =>
        i < 20 ? -5 : i < 175 ? 10 : 150 + 120 * Math.Sin(i * 0.4) * Math.Cos(j * 0.4));

    [Fact]
    public void ZonesFindMountainsWaterAndLowland()
    {
        var zones = TerrainZones.Compute(SeaPlainMountains());

        Assert.True(zones.Allows(IndustryZone.Mountains, X(190), Y(30)));
        Assert.False(zones.Allows(IndustryZone.Mountains, X(80), Y(30)));
        Assert.InRange(zones.Coverage(IndustryZone.Mountains), 0.01, TerrainZones.MaxMountainShare + 0.01);
        Assert.True(zones.Allows(IndustryZone.NearWater, X(30), Y(30)));
        Assert.False(zones.Allows(IndustryZone.NearWater, X(80), Y(30))); // morze kończy się ponad 1 km dalej
        Assert.True(zones.Allows(IndustryZone.Inland, X(80), Y(30)));
        Assert.True(zones.Allows(IndustryZone.Lowland, X(80), Y(30)));
        Assert.False(zones.Allows(IndustryZone.Lowland, X(190), Y(30)));
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], zones.RenderPng()[..4]);
    }

    [Fact]
    public void ZoneMissingOnTheMapDoesNotRestrict()
    {
        var zones = TerrainZones.Compute(Grid(100, 60, (_, _) => 10)); // płasko, bez wody

        Assert.False(zones.Exists(IndustryZone.Mountains));
        Assert.True(zones.Allows(IndustryZone.Mountains, X(50), Y(30)));
        Assert.False(zones.Exists(IndustryZone.NearWater));
        Assert.True(zones.Allows(IndustryZone.NearWater, X(50), Y(30)));
    }

    [Fact]
    public void BuildCodeKeepsMinesInTheMountains()
    {
        var terrain = Terrain(SeaPlainMountains());
        Tf3Bridge.Commands.GameObject[] objects =
            [new(Tf3Bridge.Commands.GameObjectKind.Industry, 2, "Kopalnia", (-50000, 0), "::/industries/coal_mine/coal_mine.con"),
             new(Tf3Bridge.Commands.GameObjectKind.Industry, 3, "Browar", (-60000, 0), "::/industries/brewery/brewery.con")];

        Assert.NotNull(BuildCode.Parse($"[{X(190)}, {Y(30)}, coal_mine] Kopalnia Jasia", true, objects, null, terrain).Place);
        Assert.Contains("Kopalnia węgla: ten rodzaj zakładu można budować tylko w górach",
            BuildCode.Parse($"[{X(80)}, {Y(30)}, coal_mine] Kopalnia Jasia", true, objects, null, terrain).Error);
        Assert.NotNull(BuildCode.Parse($"[{X(80)}, {Y(30)}, brewery] Browar Jasia", true, objects, null, terrain).Place);
        // Streamer zdjął ograniczenie, a bez danych o terenie nic nie sprawdzamy.
        Assert.NotNull(BuildCode.Parse($"[{X(80)}, {Y(30)}, coal_mine] Kopalnia Jasia", true, objects, null, terrain,
            new Dictionary<string, IndustryZone>()).Place);
        Assert.NotNull(BuildCode.Parse($"[{X(80)}, {Y(30)}, coal_mine] Kopalnia Jasia", true, objects, null).Place);
    }

    [Fact]
    public void ZoneRulesAreNormalizedAndVersioned()
    {
        var rules = new IndustryZoneRules();
        Assert.Equal(IndustryZone.Mountains, rules.Zones["coal_mine"]);
        var v = rules.Version;

        rules.Set(new Dictionary<string, IndustryZone> { ["COAL_MINE "] = IndustryZone.NearWater, ["farm"] = IndustryZone.Any });
        Assert.Equal(v + 1, rules.Version);
        Assert.Equal(new Dictionary<string, IndustryZone> { ["coal_mine"] = IndustryZone.NearWater }, rules.Zones);
        rules.Set(new Dictionary<string, IndustryZone> { ["coal_mine"] = IndustryZone.NearWater });
        Assert.Equal(v + 1, rules.Version);
        Assert.Equal("""{"industries":{"coal_mine":"nearWater"}}""", IndustryZones.ToJson(rules.Zones).ToJsonString());
    }

    [Fact]
    public void ZonesSurviveSavingTheRewardSettings()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "rewards.json");
        Assert.Equal(IndustryZone.Mountains, RewardSettingsStore.Load(path).Normalized().Zones!["quarry"]); // brak pliku = domyślne

        RewardSettingsStore.Save(RewardSettings.Default with { Zones = new Dictionary<string, IndustryZone> { ["quarry"] = IndustryZone.Lowland } }, path);
        Assert.Contains("\"Lowland\"", File.ReadAllText(path));
        Assert.Equal(new Dictionary<string, IndustryZone> { ["quarry"] = IndustryZone.Lowland }, RewardSettingsStore.Load(path).Zones);

        File.WriteAllText(path, File.ReadAllText(path).Replace("\"Zones\"", "\"Old\"")); // plik sprzed stref
        Assert.Equal(IndustryZone.Mountains, RewardSettingsStore.Load(path).Zones!["coal_mine"]);
    }

    [Fact]
    public async Task TerrainComesBackFromTheCacheAfterARestart()
    {
        var input = Path.Combine(_root, "in");
        var output = Path.Combine(_root, "out");
        Directory.CreateDirectory(input);
        var options = Options.Create(new BridgeOptions { InputDir = input, OutputDir = output, TerrainCacheFile = Path.Combine(_root, "cache", "terrain.json") });
        string Lua(string json) => $"function data()\r\nreturn {{ \r\n\t\tjson = \"{json.Replace("\"", "\\\"")}\",\r\n\t}}\r\nend\r\n";

        File.WriteAllText(Path.Combine(input, "terrain_aa_000001.lua"), Lua(TerrainJson(3, 2, [1, 2, 3, 4, 5, 6])));
        await new ExportProcessor(options, new LocalFolderPublisher(options), NullLogger<ExportProcessor>.Instance).ProcessOnceAsync(CancellationToken.None);
        Assert.True(File.Exists(options.Value.TerrainCacheFile));

        // Restart: the mod sends only dynamic snapshots, no terrain.
        Directory.Delete(output, recursive: true);
        File.WriteAllText(Path.Combine(input, "dynamic_aa_000002.lua"), Lua("""{"towns":[]}"""));
        var restarted = new ExportProcessor(options, new LocalFolderPublisher(options), NullLogger<ExportProcessor>.Instance);
        await restarted.ProcessOnceAsync(CancellationToken.None);

        Assert.NotNull(restarted.BuildTerrain);
        Assert.True(File.Exists(Path.Combine(output, "latest", "terrain-zone.png")));
        var meta = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "latest", "meta.json")))!;
        Assert.Equal(1, meta["terrainSeq"]!.GetValue<long>());

        // Another session (a different save) must not get the old terrain.
        File.WriteAllText(Path.Combine(input, "dynamic_bb_000001.lua"), Lua("""{"towns":[]}"""));
        var other = new ExportProcessor(options, new LocalFolderPublisher(options), NullLogger<ExportProcessor>.Instance);
        await other.ProcessOnceAsync(CancellationToken.None);
        Assert.Null(other.BuildTerrain);
    }
}
