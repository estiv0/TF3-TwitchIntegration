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
        {"towns":[{"id":1,"name":"Barcin | Estiv0","capacities":[100,50,10]},{"id":2,"name":"Szczecin | estiv0","capacities":[500,300,100]},
                  {"id":3,"name":"Łódź","capacities":[900]}],
         "industries":[{"id":10,"name":"Browar | estiv0","level":2}],
         "stations":[{"id":20,"name":"Dworzec Szczecin | estiv0 Zachód"},{"id":21,"name":"Dworzec Szczecin | estiv0"},
                     {"id":22,"name":"Stacja browaru | estiv0","industry":10},{"id":23,"name":"Port | ala"}],
         "lines":[{"id":30,"name":"Linia 1 | estiv0","vehicleCount":10,"stats":{"profit12m":653957.4}},
                  {"id":31,"name":"Linia 2 | ESTIV0","vehicleCount":6,"stats":{"profit12m":179924}},
                  {"id":32,"name":"Linia 3"}]}
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

    private ChatCommandService Service(string site = "https://gin.estfosoft.com/") =>
        new(new ChatApi(null!, new HttpClient()), mock: true, _dir, site, _time, NullLogger.Instance);

    private static ChatMessage Msg(string text, string login = "kermit33", string name = "Kermit33", string id = "7") =>
        new("m1", id, login, name, text, []);

    [Fact]
    public void Owners_are_grouped_case_insensitively_with_main_town_first()
    {
        using var doc = JsonDocument.Parse(Dynamic);
        var o = OwnerIndex.Parse(doc.RootElement)["estiv0"];

        Assert.Equal("estiv0", o.Name); // najczęstszy zapis
        Assert.Equal(["Szczecin", "Barcin"], o.Towns);
        Assert.Equal(["Browar"], o.Industries);
        Assert.Equal(0, o.Stations); // stacje nazwane od miasta i stacja własnego zakładu się nie liczą
        Assert.Equal(2, o.Lines);
        Assert.Equal(16, o.Vehicles);
        Assert.Equal(833881.4, o.Profit12m!.Value, 1);
        Assert.Equal(1, OwnerIndex.Parse(doc.RootElement)["ala"].Stations);
    }

    [Fact]
    public async Task Majatek_with_a_name_describes_that_owner_and_links_to_the_site()
    {
        var answer = await Service().HandleAsync(Msg("!tf_majatek @Estiv0"));

        Assert.Equal("@Kermit33 estiv0: 2 miasta (Szczecin – główne, Barcin), 1 zakład (Browar), 2 linie, 16 pojazdów, "
            + "zysk linii (12 mies.) $ 833 881. Mapa: https://gin.estfosoft.com/#owner=estiv0", answer);
    }

    [Fact]
    public void Majatek_without_a_name_describes_the_asker()
    {
        var service = Service(site: "");

        Assert.Equal("@estiv0 estiv0: 2 miasta (Szczecin – główne, Barcin), 1 zakład (Browar), 2 linie, 16 pojazdów, zysk linii (12 mies.) $ 833 881.",
            service.Answer(Msg("!TF_majatek", "estiv0", "estiv0")));
        Assert.Equal("@Kermit33 nie masz jeszcze nic w grze.", service.Answer(Msg("!tf_majątek", id: "8")));
        Assert.Equal("@Kermit33 nikt nie ma jeszcze nic w grze.", service.Answer(Msg("!tf_majatek nikt", id: "9")));
    }

    [Fact]
    public void Other_messages_are_ignored_and_each_viewer_has_a_cooldown()
    {
        var service = Service();

        Assert.Null(service.Answer(Msg("!majatek estiv0")));
        Assert.Null(service.Answer(Msg("hej !tf_majatek")));
        Assert.NotNull(service.Answer(Msg("!tf_majatek estiv0")));
        Assert.Null(service.Answer(Msg("!tf_majatek estiv0")));
        Assert.NotNull(service.Answer(Msg("!tf_majatek estiv0", id: "other")));
        _time.Now += ChatCommandService.Cooldown;
        Assert.NotNull(service.Answer(Msg("!tf_majatek estiv0")));
    }

    [Fact]
    public void Without_export_the_answer_says_there_is_no_data()
    {
        File.Delete(Path.Combine(_dir, "latest", "dynamic.json"));
        Assert.Equal("@Kermit33 nie mam jeszcze danych z gry.", Service().Answer(Msg("!tf_majatek")));
    }

    [Theory]
    [InlineData(1, "1 miasto")]
    [InlineData(3, "3 miasta")]
    [InlineData(5, "5 miast")]
    [InlineData(12, "12 miast")]
    [InlineData(22, "22 miasta")]
    public void Polish_plurals(int n, string expected) =>
        Assert.Equal(expected, ChatCommandService.Count(n, "miasto", "miasta", "miast"));

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 5, 18, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
