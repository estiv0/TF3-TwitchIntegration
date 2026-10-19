using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Tf3Bridge.Chat;
using Tf3Bridge.Commands;
using Tf3Bridge.Twitch;

namespace Tf3Bridge.Tests;

public sealed class ChatCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tf3bridge-chat-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();

    private const string Dynamic = """
        {"date":{"year":1922,"month":5,"day":12},"player":{"balance":1234567.4},
         "company":{"history":{"companyTotalValue":[[1921.5,500000],[1922.3,9876543]]},
                    "finance":{"header":["1921","1922"],"total":[-50000,120000]}},
         "effects":[{"type":"townBoost","entity":2,"user":"ola","factor":1.5,"until":{"year":1922,"month":8,"day":1}},
                    {"type":"lineFuelBad","entity":30,"user":"ola","factor":0.7,"until":{"year":1922,"month":6,"day":3}}],
         "towns":[{"id":1,"name":"Barcin | Estiv0","capacities":[100,50,10]},
                  {"id":2,"name":"Szczecin | estiv0","capacities":[500,300,100],"initialCapacities":[400,300,100],
                   "supply":[{"cargo":"FOOD","supply":30,"demand":40},{"cargo":"GOODS","supply":80,"demand":60}]},
                  {"id":3,"name":"Łódź","capacities":[900]},{"id":4,"name":"Kraków","capacities":[1200]}],
         "industries":[{"id":10,"name":"Browar | estiv0","level":2,"maxLevel":4,"productivity":1.2,"year":{"produced":1234}},
                       {"id":11,"name":"Kopalnia węgla","level":1},{"id":12,"name":"Tartak","level":3}],
         "stations":[{"id":20,"name":"Dworzec Szczecin | estiv0 Zachód"},{"id":21,"name":"Dworzec Szczecin | estiv0"},
                     {"id":22,"name":"Stacja browaru | estiv0","industry":10},{"id":23,"name":"Port | ala"}],
         "lines":[{"id":30,"name":"Linia 1 | estiv0","vehicleCount":10,"stats":{"profit12m":653957.4}},
                  {"id":31,"name":"Linia 2 | ESTIV0","vehicleCount":6,"stats":{"profit12m":179924}},
                  {"id":32,"name":"Linia 3","vehicleCount":2,"stats":{"profit12m":-457279}},
                  {"id":33,"name":"Linia 4 | ala","vehicleCount":1,"stats":{"profit12m":1000}}],
         "vehicles":[{"id":40,"line":30,"stopped":true},{"id":41,"line":30,"stopped":false},{"id":42,"line":31,"stopped":true}]}
        """;

    public ChatCommandTests()
    {
        var latest = Path.Combine(_dir, "latest");
        Directory.CreateDirectory(latest);
        File.WriteAllText(Path.Combine(latest, "dynamic.json"), Dynamic);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private ChatCommandService Service(string site = "https://tf3.example.com/") =>
        new(new ChatApi(null!, new HttpClient()), mock: true, _dir, site, _time, NullLogger.Instance);

    private static ChatMessage Msg(string text, string login = "kermit33", string name = "Kermit33", string id = "7") =>
        new("m1", id, login, name, text, []);

    [Fact]
    public void Owners_are_grouped_case_insensitively_with_main_town_first()
    {
        using var doc = JsonDocument.Parse(Dynamic);
        var o = OwnerIndex.Parse(doc.RootElement)["estiv0"];

        Assert.Equal("estiv0", o.Name); // the most common spelling
        Assert.Equal(["Szczecin", "Barcin"], o.Towns);
        Assert.Equal(["Browar"], o.Industries);
        Assert.Equal(0, o.Stations); // stations named after the town and the station of one's own industry don't count
        Assert.Equal(2, o.Lines);
        Assert.Equal(16, o.Vehicles);
        Assert.Equal(833881.4, o.Profit12m!.Value, 1);
        Assert.Equal(1, OwnerIndex.Parse(doc.RootElement)["ala"].Stations);
        Assert.Equal(1, OwnerIndex.Parse(doc.RootElement)["ala"].Lines);
    }

    [Fact]
    public void Score_weighs_towns_by_size()
    {
        // As in the game save: Szczecin (5172) and 2 lines rank higher than two small towns (246, 286).
        Assert.True(OwnerIndex.Score([OwnerIndex.TownWorth(5172, 0)], 0, 0, 2)
            > OwnerIndex.Score([OwnerIndex.TownWorth(246, 0), OwnerIndex.TownWorth(286, 0)], 0, 0, 0));
        Assert.Equal(100 + 10 + 50 + 1 + 1, OwnerIndex.Score([OwnerIndex.TownWorth(250, 0)], 1, 1, 1));
    }

    [Fact]
    public void Town_growth_since_start_adds_points_but_shrinking_does_not_cost()
    {
        Assert.Equal(100 + 20 + 100, OwnerIndex.TownWorth(500, 250)); // podwojone
        Assert.Equal(100 + 10, OwnerIndex.TownWorth(250, 300)); // shrunk
        Assert.Equal(100 + 10, OwnerIndex.TownWorth(250, 0)); // brak danych o starcie
        // Szczecin in the test data: 900, 800 at the start.
        using var doc = JsonDocument.Parse(Dynamic);
        var o = OwnerIndex.Parse(doc.RootElement)["estiv0"];
        Assert.Equal(OwnerIndex.TownWorth(900, 800) + OwnerIndex.TownWorth(160, 0) + 50 + 2 + 16, o.Score, 6); // industry, 2 lines, 16 vehicles without a carrier
    }

    [Fact]
    public void Vehicles_count_by_carrier()
    {
        Assert.Equal(10, OwnerIndex.VehicleWorth("RAIL"));
        Assert.Equal(10, OwnerIndex.VehicleWorth("AIR"));
        Assert.Equal(2, OwnerIndex.VehicleWorth("WATER"));
        Assert.Equal(1, OwnerIndex.VehicleWorth("ROAD"));
        Assert.Equal(1, OwnerIndex.VehicleWorth("TRAM"));
        Assert.Equal(1, OwnerIndex.VehicleWorth(null));
        Assert.Equal(1 + 3 * 10, OwnerIndex.Score([], 0, 0, 1, vehiclePoints: 3 * OwnerIndex.VehicleWorth("RAIL")));
    }

    [Fact]
    public async Task Majatek_with_a_name_describes_that_owner_and_links_to_the_site()
    {
        var answer = await Service().HandleAsync(Msg("!majatek @Estiv0"));

        Assert.Equal("@Kermit33 estiv0: 2 miasta (Szczecin – główne, Barcin), 1 zakład (Browar), 2 linie, 16 pojazdów, "
            + "zysk linii (12 mies.) $ 833 881. Mapa: https://tf3.example.com/#owner=estiv0", answer);
    }

    [Fact]
    public void Majatek_without_a_name_describes_the_asker()
    {
        var service = Service(site: "");

        Assert.Equal("@estiv0 estiv0: 2 miasta (Szczecin – główne, Barcin), 1 zakład (Browar), 2 linie, 16 pojazdów, zysk linii (12 mies.) $ 833 881.",
            service.Answer(Msg("!majatek", "estiv0", "estiv0")));
        Assert.Equal("@Kermit33 nie masz jeszcze nic w grze.", service.Answer(Msg("!majątek", id: "8")));
        Assert.Equal("@Kermit33 nikt nie ma jeszcze nic w grze.", service.Answer(Msg("!majatek nikt", id: "9")));
    }

    [Fact]
    public void Other_messages_are_ignored_and_each_viewer_has_a_cooldown()
    {
        var service = Service();

        Assert.Null(service.Answer(Msg("hej !majatek")));
        Assert.Null(service.Answer(Msg("!nieznana")));
        Assert.NotNull(service.Answer(Msg("!majatek estiv0")));
        Assert.Null(service.Answer(Msg("!miasto Łódź"))); // the same viewer, a different command
        Assert.NotNull(service.Answer(Msg("!majatek ala", id: "other")));
        _time.Now += ChatCommandService.Cooldown;
        Assert.NotNull(service.Answer(Msg("!miasto Łódź")));
    }

    [Fact]
    public void The_same_command_with_the_same_argument_has_a_cooldown_for_the_whole_chat()
    {
        var service = Service();

        Assert.NotNull(service.Answer(Msg("!miasto Łódź", id: "1")));
        Assert.Null(service.Answer(Msg("!miasto lodz", id: "2"))); // the same answer: not repeated in chat
        Assert.NotNull(service.Answer(Msg("!miasto Kraków", id: "3")));
        Assert.NotNull(service.Answer(Msg("!ranking", id: "4")));
        Assert.Null(service.Answer(Msg("!top", id: "5"))); // an alias of the same command
        Assert.NotNull(service.Answer(Msg("!majatek", "a", "a", id: "6"))); // the asker's estate: separate for each viewer
        Assert.NotNull(service.Answer(Msg("!majatek", "b", "b", id: "7")));

        _time.Now += TimeSpan.FromSeconds(30);
        Assert.NotNull(service.Answer(Msg("!miasto lodz", id: "2")));
        Assert.Null(service.Answer(Msg("!ranking", id: "5"))); // ranking: 60 s
        _time.Now += TimeSpan.FromSeconds(30);
        Assert.NotNull(service.Answer(Msg("!ranking", id: "5")));
    }

    [Fact]
    public void Pomoc_and_mapa()
    {
        var help = Service().Answer(Msg("!pomoc"))!;
        Assert.StartsWith("@Kermit33 Komendy TF3: !majatek [widz], !miasto <nazwa>", help);
        Assert.True(help.Length <= 500, "limit wiadomości Twitcha");

        Assert.Equal("@Kermit33 mapa na żywo: https://tf3.example.com/", Service().Answer(Msg("!mapa")));
        Assert.Equal("@Kermit33 mapa nie jest jeszcze ustawiona.", Service(site: "").Answer(Msg("!mapa")));
    }

    [Fact]
    public void Miasto_shows_owner_size_growth_supply_effects_and_link()
    {
        var service = Service();

        Assert.Equal("@Kermit33 Szczecin (właściciel: estiv0): pojemność 900, +13% od startu gry, zaspokojenie potrzeb 90%, "
            + "efekty: boost miasta +50% do 1 sierpnia 1922 (od ola). https://tf3.example.com/#town=2",
            service.Answer(Msg("!miasto szczecin", id: "1")));
        Assert.Equal("@Kermit33 Łódź (bez właściciela): pojemność 900. https://tf3.example.com/#town=3",
            service.Answer(Msg("!miasto lodz", id: "2")));
        Assert.Equal("@Kermit33 nie znaleziono „Gdańsk”.", service.Answer(Msg("!miasto Gdańsk", id: "3")));
        Assert.Equal("@Kermit33 podaj nazwę, np. !miasto Szczecin", service.Answer(Msg("!miasto", id: "4")));
    }

    [Fact]
    public void Zaklad_and_linia()
    {
        var service = Service();

        Assert.Equal("@Kermit33 Browar (właściciel: estiv0): poziom 2/4, produktywność 120%, produkcja (rok) 1 234. "
            + "https://tf3.example.com/#industry=10", service.Answer(Msg("!zakład browar", id: "1")));
        Assert.Equal("@Kermit33 Linia 1 (właściciel: estiv0): 10 pojazdów (zatrzymane: 1), zysk (12 mies.) $ 653 957, "
            + "efekty: chrzczone paliwo -30% do 3 czerwca 1922 (od ola). https://tf3.example.com/#line=30",
            service.Answer(Msg("!linia linia 1", id: "2")));
        Assert.Equal("@Kermit33 „linia” pasuje do kilku: Linia 1, Linia 2, Linia 3, Linia 4.", service.Answer(Msg("!linia linia", id: "3")));
    }

    [Fact]
    public void Ranking_orders_viewers_by_score()
    {
        Assert.Equal("@Kermit33 ranking: 1. estiv0 (2 miasta, 1 zakład, 2 linie), 2. ala (1 linia). Wszyscy: https://tf3.example.com/",
            Service().Answer(Msg("!ranking")));
    }

    [Fact]
    public void Wolne_lists_towns_by_size_and_industries_by_level()
    {
        var service = Service(site: "");

        Assert.Equal("@Kermit33 wolne miasta (2): Kraków, Łódź; wolne zakłady (2): Tartak, Kopalnia węgla. Kupisz je za punkty kanału.",
            service.Answer(Msg("!wolne", id: "1")));
        Assert.Equal("@Kermit33 wolne miasta (2): Kraków, Łódź. Kupisz je za punkty kanału.", service.Answer(Msg("!wolne miasta", id: "2")));
        Assert.Equal("@Kermit33 wolne zakłady (2): Tartak, Kopalnia węgla. Kupisz je za punkty kanału.", service.Answer(Msg("!wolne zakłady", id: "3")));
        Assert.Equal("@Kermit33 użyj: !wolne [miasta|zaklady]", service.Answer(Msg("!wolne stacje", id: "4")));
    }

    [Fact]
    public void Data_warns_when_the_export_is_old()
    {
        var path = Path.Combine(_dir, "latest", "dynamic.json");
        File.SetLastWriteTimeUtc(path, _time.Now.UtcDateTime.AddSeconds(-20));
        Assert.Equal("@Kermit33 w grze jest 12 maja 1922.", Service().Answer(Msg("!data")));

        File.SetLastWriteTimeUtc(path, _time.Now.UtcDateTime.AddMinutes(-5));
        Assert.Equal("@Kermit33 w grze jest 12 maja 1922. Ostatnie dane z gry sprzed 5 min (pauza albo gra wyłączona).",
            Service().Answer(Msg("!data")));
    }

    [Fact]
    public void Firma_shows_balance_value_profit_and_fleet()
    {
        Assert.Equal("@Kermit33 firma streamera: saldo $ 1 234 567, wartość firmy $ 9 876 543, zysk w 1922 $ 120 000 (1921: $ -50 000), "
            + "4 linie, 3 pojazdy.", Service().Answer(Msg("!firma")));
    }

    [Fact]
    public void Najlepsza_and_najgorsza_line_or_viewer_company()
    {
        var service = Service();

        Assert.Equal("@Kermit33 najlepsza linia: Linia 1 (estiv0), zysk (12 mies.) $ 653 957. https://tf3.example.com/#line=30",
            service.Answer(Msg("!najlepsza", id: "1")));
        Assert.Equal("@Kermit33 najgorsza linia: Linia 3, zysk (12 mies.) $ -457 279. https://tf3.example.com/#line=32",
            service.Answer(Msg("!najgorsza linia", id: "2")));
        Assert.Equal("@Kermit33 najlepsza firma widza: estiv0, zysk linii (12 mies.) $ 833 881 (2 linie). https://tf3.example.com/#owner=estiv0",
            service.Answer(Msg("!najlepsza firma", id: "3")));
        Assert.Equal("@Kermit33 najgorsza firma widza: ala, zysk linii (12 mies.) $ 1 000 (1 linia). https://tf3.example.com/#owner=ala",
            service.Answer(Msg("!najgorszy firma", id: "4")));
        Assert.Equal("@Kermit33 użyj: !najlepsza [linia|firma]", service.Answer(Msg("!najlepsza miasto", id: "5")));
    }

    [Fact]
    public void Without_export_the_answer_says_there_is_no_data()
    {
        File.Delete(Path.Combine(_dir, "latest", "dynamic.json"));
        Assert.Equal("@Kermit33 nie mam jeszcze danych z gry.", Service().Answer(Msg("!majatek")));
    }

    [Theory]
    [InlineData(1, "1 miasto")]
    [InlineData(3, "3 miasta")]
    [InlineData(5, "5 miast")]
    [InlineData(12, "12 miast")]
    [InlineData(22, "22 miasta")]
    public void Polish_plurals(int n, string expected) =>
        Assert.Equal(expected, ChatAnswers.Count(n, "town", "towns", "miasto", "miasta", "miast"));

    [Theory]
    [InlineData(1, "1 town")]
    [InlineData(3, "3 towns")]
    public void English_plurals(int n, string expected)
    {
        using var _ = Loc.Use(Lang.En);
        Assert.Equal(expected, ChatAnswers.Count(n, "town", "towns", "miasto", "miasta", "miast"));
    }
}

internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 5, 18, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
