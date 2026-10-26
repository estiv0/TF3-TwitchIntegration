using Tf3Bridge.Commands;
using Tf3Bridge.Rewards;

namespace Tf3Bridge.Tests;

public sealed class BuildCodeTests
{
    private const string Coal = "::/industries/coal_mine/coal_mine.con";

    private static readonly GameObject[] Objects =
    [
        new(GameObjectKind.Town, 1, "Bochnia | ala", (0, 0)),
        new(GameObjectKind.Industry, 2, "Bochnia Kopalnia", (5000, 0), Coal),
    ];

    private static readonly MapBounds Bounds = new(-10000, 10000, -20000, 20000);

    [Fact]
    public void Town_code()
    {
        var r = BuildCode.Parse("  [3000, -2500.4]   Nowa  Wieś ", industry: false, Objects, Bounds);

        Assert.Equal(new BuildPlace(3000, -2500, "Nowa Wieś", null), r.Place);
    }

    [Fact]
    public void Industry_code_takes_the_file_from_the_export()
    {
        var r = BuildCode.Parse("[-3000,2000,COAL_MINE] Kopalnia Jasia", industry: true, Objects, Bounds);

        Assert.Equal(new BuildPlace(-3000, 2000, "Kopalnia Jasia", Coal), r.Place);
    }

    [Fact]
    public void Industry_code_may_use_any_game_type_not_on_the_map()
    {
        const string sandPit = "::/industries/sand_pit/sand_pit.con";
        var types = GameObjects.ParseIndustryTypes(System.Text.Json.JsonDocument.Parse(
            """{ "industryTypes": [{ "file": "::/industries/coal_mine/coal_mine.con", "name": "Coal Mine" }, { "file": "::/industries/sand_pit/sand_pit.con" }] }""").RootElement);

        var r = BuildCode.Parse("[-3000,2000,sand_pit] Piaskarnia Jasia", industry: true, Objects, Bounds, industryTypes: types);

        Assert.Equal(new BuildPlace(-3000, 2000, "Piaskarnia Jasia", sandPit), r.Place);
        Assert.Contains("nieznany rodzaj zakładu", BuildCode.Parse("[-3000,2000,steel_mill] Huta", true, Objects, Bounds, industryTypes: types).Error);
    }

    [Theory]
    [InlineData("Nowa Wieś", false, "to nie jest kod z mapy")]
    [InlineData("[3000, -2500] ", false, "podaj nazwę")]
    [InlineData("[3000, -2500, coal_mine] Kopalnia", false, "to kod zakładu")]
    [InlineData("[3000, -2500] Kopalnia", true, "to kod miasta")]
    [InlineData("[3000, -2500, steel_mill] Huta", true, "nieznany rodzaj zakładu „steel_mill”")]
    [InlineData("[9900, 0] Brzeg", false, "poza mapą")]
    [InlineData("[3000, -2500] bochnia", false, "miasto o nazwie Bochnia już jest")]
    [InlineData("[1000, 1000] Za blisko", false, "za blisko miasta Bochnia (1414 m, a trzeba co najmniej 1500 m)")]
    [InlineData("[5000, 400, coal_mine] Za blisko", true, "za blisko zakładu Bochnia Kopalnia (400 m, a trzeba co najmniej 600 m)")]
    public void Bad_codes_are_explained(string input, bool industry, string error)
    {
        var r = BuildCode.Parse(input, industry, Objects, Bounds);

        Assert.Null(r.Place);
        Assert.Contains(error, r.Error);
    }

    [Fact]
    public void Owner_separator_is_removed_from_the_name()
    {
        var r = BuildCode.Parse("[3000, -2500] Nowa | Wieś", industry: false, Objects, bounds: null);

        Assert.Equal("Nowa Wieś", r.Place!.Name);
    }
}
