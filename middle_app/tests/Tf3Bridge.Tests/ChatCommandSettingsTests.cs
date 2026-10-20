using Microsoft.Extensions.Logging.Abstractions;
using Tf3Bridge.Chat;
using Tf3Bridge.Twitch;
using Tf3Bridge.Ui;

namespace Tf3Bridge.Tests;

/// <summary>Chat commands configured by the streamer: names, aliases, on/off, cooldowns, the settings file.</summary>
public sealed class ChatCommandSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tf3bridge-chatcfg-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new();

    public ChatCommandSettingsTests()
    {
        var latest = Path.Combine(_dir, "latest");
        Directory.CreateDirectory(latest);
        File.WriteAllText(Path.Combine(latest, "dynamic.json"), """
            {"date":{"year":1922,"month":5,"day":12},
             "towns":[{"id":3,"name":"Łódź","capacities":[900]}],"industries":[],"lines":[]}
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string SettingsPath => Path.Combine(_dir, "chat-commands.json");

    private ChatCommandService Service(string? settingsPath = null) =>
        new(new ChatApi(null!, new HttpClient()), mock: true, _dir, "https://tf3.example.com/", _time, NullLogger.Instance, settingsPath);

    private static ChatMessage Msg(string text, string id) => new("m1", id, "kermit33", "Kermit33", text, []);

    private static ChatCommandSettings With(ChatCommandSettings s, ChatCommandKind kind, Func<ChatCommandConfig, ChatCommandConfig> change) =>
        new([.. s.Commands.Select(c => c.Kind == kind ? change(c) : c)]);

    [Fact]
    public void No_file_means_every_english_and_polish_name_answers_as_before()
    {
        var s = ChatCommandSettingsStore.Load(SettingsPath);
        Assert.Equal(Enum.GetValues<ChatCommandKind>(), s.Commands.Select(c => c.Kind));
        Assert.All(s.Commands, c => Assert.True(c.Enabled));
        Assert.Equal(["majatek", "majątek", "assets"], s.Get(ChatCommandKind.Assets).AllNames);
        Assert.Equal(["pomoc", "komendy", "help", "commands"], s.Get(ChatCommandKind.Help).AllNames);
        Assert.Equal(30, s.Get(ChatCommandKind.Town).CooldownSec);
        Assert.Equal(60, s.Get(ChatCommandKind.Ranking).CooldownSec);

        var service = Service(SettingsPath);
        string[] all = ["!help", "!commands", "!pomoc", "!komendy", "!map", "!mapa", "!assets", "!majatek", "!majątek", "!town Łódź", "!miasto Łódź",
            "!industry", "!zaklad", "!zakład", "!line", "!linia", "!ranking", "!top", "!free", "!wolne", "!date", "!data", "!company", "!firma",
            "!best", "!najlepsza", "!najlepszy", "!worst", "!najgorsza", "!najgorszy"];
        for (var i = 0; i < all.Length; i++)
        {
            _time.Now += TimeSpan.FromHours(1);
            Assert.True(service.Answer(Msg(all[i], "v" + i)) is not null, all[i]);
        }
        Assert.False(File.Exists(SettingsPath)); // nothing is written until the streamer saves
    }

    [Fact]
    public void English_defaults_put_the_english_name_first()
    {
        var en = ChatCommandKinds.DefaultsFor(Lang.En).Single(c => c.Kind == ChatCommandKind.Assets);
        Assert.Equal("assets", en.Name);
        Assert.Equal(["majatek", "majątek"], en.Aliases);
    }

    [Fact]
    public void Custom_names_and_aliases_answer_and_old_names_stop()
    {
        var service = Service();
        service.UpdateSettings(With(service.Settings, ChatCommandKind.Town, c => c with { Name = "City", Aliases = ["!Gród", "m"] }));

        Assert.NotNull(service.Answer(Msg("!city Łódź", "1")));
        Assert.Null(service.Answer(Msg("!GRÓD lodz", "2"))); // the same command and argument: on the shared cooldown
        _time.Now += TimeSpan.FromMinutes(5);
        Assert.NotNull(service.Answer(Msg("!gród lodz", "2")));
        Assert.Equal("@Kermit33 podaj nazwę, np. !city Szczecin", service.Answer(Msg("!m", "3")));
        Assert.Null(service.Answer(Msg("!miasto Łódź", "4")));
        Assert.Null(service.Answer(Msg("!town Łódź", "5")));
    }

    [Fact]
    public void Disabled_commands_are_ignored_and_left_out_of_help()
    {
        var service = Service();
        var s = With(service.Settings, ChatCommandKind.Town, c => c with { Enabled = false });
        s = With(s, ChatCommandKind.Worst, c => c with { Enabled = false });
        s = With(s, ChatCommandKind.Assets, c => c with { Name = "stan" });
        service.UpdateSettings(s);

        Assert.Null(service.Answer(Msg("!miasto Łódź", "1")));
        Assert.Null(service.Answer(Msg("!town Łódź", "1b")));
        Assert.Null(service.Answer(Msg("!najgorsza", "2")));
        Assert.Equal("@Kermit33 Komendy TF3: !stan [widz], !zaklad <nazwa>, !linia <nazwa>, !ranking, !wolne [miasta|zaklady], "
            + "!najlepsza [linia|firma], !firma, !data, !mapa. Grę zmieniasz tylko punktami kanału.", service.Answer(Msg("!pomoc", "3")));
    }

    [Fact]
    public void Help_in_english_uses_the_english_names()
    {
        using var _ = Loc.Use(Lang.En);
        var answer = Service().Answer(Msg("!help", "1"));
        Assert.Equal("@Kermit33 TF3 commands: !assets [viewer], !town <name>, !industry <name>, !line <name>, !ranking, "
            + "!free [towns|industries], !best / !worst [line|company], !company, !date, !map. Only channel points change the game.", answer);
    }

    [Fact]
    public void Answers_point_to_the_configured_names()
    {
        var service = Service();
        var s = With(service.Settings, ChatCommandKind.Free, c => c with { Name = "dostepne" });
        s = With(s, ChatCommandKind.Best, c => c with { Name = "szczyt", Aliases = [] });
        service.UpdateSettings(s);

        Assert.Equal("@Kermit33 nikt jeszcze nic nie kupił. Zobacz !dostepne", service.Answer(Msg("!ranking", "1")));
        Assert.Equal("@Kermit33 użyj: !dostepne [miasta|zaklady]", service.Answer(Msg("!dostepne stacje", "2")));
        Assert.Equal("@Kermit33 użyj: !szczyt [linia|firma]", service.Answer(Msg("!szczyt miasto", "3")));

        service.UpdateSettings(With(s, ChatCommandKind.Free, c => c with { Enabled = false }));
        _time.Now += TimeSpan.FromMinutes(5);
        Assert.Equal("@Kermit33 nikt jeszcze nic nie kupił.", service.Answer(Msg("!ranking", "1")));
    }

    [Fact]
    public void Cooldown_comes_from_the_settings()
    {
        var service = Service();
        service.UpdateSettings(With(service.Settings, ChatCommandKind.Date, c => c with { CooldownSec = 0 }));

        Assert.NotNull(service.Answer(Msg("!data", "1")));
        Assert.NotNull(service.Answer(Msg("!data", "2"))); // another viewer, no shared cooldown
    }

    [Fact]
    public void Saving_reloads_live_and_writes_the_file()
    {
        var service = Service(SettingsPath);
        Assert.NotNull(service.Answer(Msg("!mapa", "1")));
        service.UpdateSettings(With(service.Settings, ChatCommandKind.Map, c => c with { Enabled = false }));

        Assert.Null(service.Answer(Msg("!mapa", "2")));
        var loaded = ChatCommandSettingsStore.Load(SettingsPath);
        Assert.False(loaded.Get(ChatCommandKind.Map).Enabled);
        Assert.Contains("majątek", File.ReadAllText(SettingsPath)); // readable, not ą
        Assert.Null(Service(SettingsPath).Answer(Msg("!mapa", "3"))); // after a restart too
    }

    [Fact]
    public void Duplicate_names_are_rejected()
    {
        var s = ChatCommandSettings.Default;
        Assert.Null(ChatCommandSettings.Validate(s.Commands));

        var clash = With(s, ChatCommandKind.Town, c => c with { Aliases = [.. c.Aliases, "top"] });
        Assert.Equal("!top jest użyta w dwóch komendach: „Miasto: właściciel, wielkość, efekty” i „Ranking widzów”.",
            ChatCommandSettings.Validate(clash.Commands));

        // An off command still owns its names (switching it on later must not clash).
        var off = With(clash, ChatCommandKind.Town, c => c with { Enabled = false });
        Assert.NotNull(ChatCommandSettings.Validate(off.Commands));
    }

    [Theory]
    [InlineData("!Miasto", "gród, !m", true, "")]
    [InlineData("", "m", false, "Miasto: właściciel, wielkość, efekty: brakuje nazwy.")]
    [InlineData("mia-sto", "", false, "Miasto: właściciel, wielkość, efekty: „mia-sto” to niepoprawna nazwa (litery, cyfry i „_”, do 25 znaków).")]
    [InlineData("miasto", "ok, a.b", false, "Miasto: właściciel, wielkość, efekty: „a.b” to niepoprawna nazwa (litery, cyfry i „_”, do 25 znaków).")]
    public void Dialog_rows_are_checked(string name, string aliases, bool ok, string error)
    {
        Assert.Equal(ok, ChatCommandsDialog.Parse(ChatCommandKind.Town, name, aliases, "30", true, out var c, out var e));
        if (ok)
        {
            Assert.Equal("miasto", c!.Name);
            Assert.Equal(["gród", "m"], c.Aliases);
        }
        else Assert.Equal(error, e);
    }

    [Fact]
    public void Dialog_rejects_a_bad_cooldown()
    {
        Assert.False(ChatCommandsDialog.Parse(ChatCommandKind.Town, "miasto", "", "-1", true, out _, out var e));
        Assert.Equal("Miasto: właściciel, wielkość, efekty: cooldown od 0 do 3600 s (liczba całkowita).", e);
    }

    [Fact]
    public void Old_or_hand_edited_files_are_repaired()
    {
        File.WriteAllText(SettingsPath, """
            {"Commands":[
              {"Kind":"Town","Enabled":false,"Name":" !Gród ","Aliases":["gród","zła nazwa","x"],"CooldownSec":99999},
              {"Kind":"Map","Enabled":true,"Name":"???","Aliases":null,"CooldownSec":10}
            ]}
            """);
        var s = ChatCommandSettingsStore.Load(SettingsPath);

        Assert.Equal(Enum.GetValues<ChatCommandKind>(), s.Commands.Select(c => c.Kind)); // missing kinds from defaults
        var town = s.Get(ChatCommandKind.Town);
        Assert.False(town.Enabled);
        Assert.Equal("gród", town.Name);
        Assert.Equal(["x"], town.Aliases);
        Assert.Equal(3600, town.CooldownSec);
        var map = s.Get(ChatCommandKind.Map);
        Assert.Equal("mapa", map.Name); // no valid name left: the default comes back
        Assert.Equal(10, map.CooldownSec);
        Assert.True(s.Get(ChatCommandKind.Assets).Enabled);
    }

    [Fact]
    public void Broken_file_gives_defaults()
    {
        File.WriteAllText(SettingsPath, "{ nope");
        Assert.Equal(ChatCommandSettings.Default.Get(ChatCommandKind.Help).AllNames, ChatCommandSettingsStore.Load(SettingsPath).Get(ChatCommandKind.Help).AllNames);
    }

    [Fact]
    public void Unchanged_default_names_follow_a_language_switch()
    {
        ChatCommandSettings saved;
        using (Loc.Use(Lang.En))
            saved = With(ChatCommandSettings.Default, ChatCommandKind.Town, c => c with { Name = "city", CooldownSec = 5 });

        var pl = saved.Normalized(); // the app now runs in Polish
        Assert.Equal("majatek", pl.Get(ChatCommandKind.Assets).Name);
        Assert.Equal(["majątek", "assets"], pl.Get(ChatCommandKind.Assets).Aliases);
        Assert.Equal("city", pl.Get(ChatCommandKind.Town).Name); // the streamer's own name stays
        Assert.Equal(5, pl.Get(ChatCommandKind.Town).CooldownSec);
    }
}
