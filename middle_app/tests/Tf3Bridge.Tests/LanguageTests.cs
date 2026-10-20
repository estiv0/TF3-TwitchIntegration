using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Tf3Bridge.Chat;
using Tf3Bridge.Commands;
using Tf3Bridge.Rewards;
using Tf3Bridge.Twitch;

namespace Tf3Bridge.Tests;

/// <summary>The English side of the app texts (the other tests run in Polish, see <see cref="TestLanguage"/>).</summary>
public sealed class LanguageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tf3bridge-lang-" + Guid.NewGuid().ToString("N"));

    private const string Dynamic = """
        {"date":{"year":1922,"month":5,"day":12},
         "towns":[{"id":1,"name":"Barcin | Estiv0","capacities":[100,50,10]},{"id":2,"name":"Szczecin | estiv0","capacities":[500,300,100]}],
         "industries":[{"id":10,"name":"Browar | estiv0","level":2}],
         "stations":[],
         "lines":[{"id":30,"name":"Linia 1 | estiv0","vehicleCount":10,"stats":{"profit12m":653957.4}},
                  {"id":31,"name":"Linia 2 | estiv0","vehicleCount":6,"stats":{"profit12m":179924}}]}
        """;

    public LanguageTests()
    {
        var latest = Path.Combine(_dir, "latest");
        Directory.CreateDirectory(latest);
        File.WriteAllText(Path.Combine(latest, "dynamic.json"), Dynamic);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private ChatCommandService Service() =>
        new(new ChatApi(null!, new HttpClient()), mock: true, _dir, "https://tf3.example.com/", new ManualTime(), NullLogger.Instance);

    private static ChatMessage Msg(string text, string id = "7") => new("m1", id, "kermit33", "Kermit33", text, []);

    [Fact]
    public void Help_is_in_english_and_lists_the_english_command_names()
    {
        using var _ = Loc.Use("en");

        Assert.Equal("@Kermit33 TF3 commands: !assets [viewer], !town <name>, !industry <name>, !line <name>, !ranking, "
            + "!free [towns|industries], !best / !worst [line|company], !company, !date, !map. Only channel points change the game.",
            Service().Answer(Msg("!help")));
    }

    [Fact]
    public void Commands_answer_to_english_and_polish_names_in_the_current_language()
    {
        // A new service each time: a command has a cooldown and the same answer is not repeated.
        using (Loc.Use("en"))
        {
            Assert.Equal("@Kermit33 estiv0: 2 towns (Szczecin – main, Barcin), 1 industry (Browar), 2 lines, 16 vehicles, "
                + "line profit (12 mo.) $833,881. Map: https://tf3.example.com/#owner=estiv0",
                Service().Answer(Msg("!assets estiv0")));
            Assert.StartsWith("@Kermit33 estiv0: 2 towns", Service().Answer(Msg("!majatek estiv0")));
            Assert.Equal("@Kermit33 in the game it's 12 May 1922.", Service().Answer(Msg("!date")));
        }
        Assert.StartsWith("@Kermit33 estiv0: 2 miasta", Service().Answer(Msg("!assets estiv0")));
    }

    [Fact]
    public void Redemption_answer_and_game_notification_are_in_english()
    {
        using var _ = Loc.Use("en");
        var boost = RewardKinds.Defaults.Single(r => r.Kind == RewardKind.BoostTown);

        Assert.Equal("@viewer town boost Szczecin (+50%) runs until 12 May 1922 (in-game date).",
            RewardMessages.Text(boost, "viewer", "Szczecin", ok: true, new GameDate(1922, 5, 12), reason: null));
        Assert.Equal("@viewer town boost: not found. Your points have been refunded.",
            RewardMessages.Text(boost, "viewer", null, ok: false, until: null, reason: "not found"));
        Assert.Equal("viewer sped up the growth of Szczecin by +50% until 12 May 1922.",
            RewardMessages.Notification(boost, "viewer", "Szczecin", new GameDate(1922, 5, 12)));
        Assert.Equal("$10M", RewardMessages.Money(10_000_000));
    }

    [Fact]
    public void Industry_names_follow_the_language()
    {
        Assert.Equal("Kopalnia węgla", IndustryZones.Name("coal_mine"));
        using var _ = Loc.Use("en");
        Assert.Equal("Coal Mine", IndustryZones.Name("coal_mine"));
    }

    [Fact]
    public void Unchanged_default_titles_switch_language_but_own_titles_stay()
    {
        var polish = RewardSettings.Default; // Polish defaults
        var custom = polish with
        {
            Rewards = [.. polish.Rewards.Select(r => r.Kind == RewardKind.StopLine ? r with { Title = "Stop!", Prompt = "Mój opis" } : r)],
        };

        using var _ = Loc.Use("en");
        var english = custom.Normalized();
        var defaults = RewardKinds.Defaults;

        Assert.Equal(defaults.Single(d => d.Kind == RewardKind.BoostTown).Title, english.Get(RewardKind.BoostTown).Title);
        Assert.Equal(defaults.Single(d => d.Kind == RewardKind.BoostTown).Prompt, english.Get(RewardKind.BoostTown).Prompt);
        Assert.Equal("TF3: town boost", english.Get(RewardKind.BoostTown).Title);
        Assert.Equal("Stop!", english.Get(RewardKind.StopLine).Title);
        Assert.Equal("Mój opis", english.Get(RewardKind.StopLine).Prompt);
    }

    [Fact]
    public void A_status_set_before_a_language_switch_shows_in_the_new_language()
    {
        var session = new TwitchSession(null, Path.Combine(_dir, "twitch.json"), NullLogger.Instance);

        Assert.Equal("brak Twitch:ClientId w appsettings.json", session.Status.Message);
        using var _ = Loc.Use("en");
        Assert.Equal("no Twitch:ClientId in appsettings.json", session.Status.Message);
    }

    [Fact]
    public void The_language_choice_is_saved_and_defaults_to_the_windows_language()
    {
        var path = Path.Combine(_dir, "language.txt");
        Assert.Null(Loc.Load(path));
        Loc.Save("en", path);
        Assert.Equal("en", Loc.Load(path));
        File.WriteAllText(path, "Pl"); // written by the versions with a language enum
        Assert.Equal("pl", Loc.Load(path));
        File.WriteAllText(path, "klingon");
        Assert.Null(Loc.Load(path));

        Assert.Equal("pl", Loc.FromCulture(CultureInfo.GetCultureInfo("pl-PL")));
        Assert.Equal(["English", "Polski"], Loc.Languages.Select(Loc.NativeName)); // the language list in the main window
        Assert.Equal("en", Loc.FromCulture(CultureInfo.GetCultureInfo("de-DE")));
    }
}
