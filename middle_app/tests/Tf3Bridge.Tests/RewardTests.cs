using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Tf3Bridge.Commands;
using Tf3Bridge.Rewards;
using Tf3Bridge.Twitch;

namespace Tf3Bridge.Tests;

public sealed class RewardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tf3bridge-rewards-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHelix _helix = new();
    private readonly ModCommandQueue _queue;
    private readonly ConcurrentQueue<RewardOutcome> _outcomes = new();

    public RewardTests()
    {
        var latest = Path.Combine(_dir, "out", "latest");
        Directory.CreateDirectory(latest);
        File.WriteAllText(Path.Combine(latest, "dynamic.json"), """
            {"towns":[{"id":10,"name":"Łódź","pos":[0,0,5]},{"id":11,"name":"Kraków | ala"},{"id":12,"name":"Nowy Sącz"},{"id":13,"name":"Nowy Targ"}],
             "industries":[{"id":20,"name":"Kopalnia węgla Bytom","file":"::/industries/coal_mine/coal_mine.con","pos":[5000,5000,5]},{"id":21,"name":"Huta Katowice | ola"}],
             "lines":[{"id":30,"name":"Linia 1"},{"id":31,"name":"Tramwaj | ola"},{"id":32,"name":"Autobus | widz"}]}
            """);
        File.WriteAllText(Path.Combine(latest, "meta.json"), "{}");
        _queue = new ModCommandQueue(Path.Combine(_dir, "cmd"), NullLogger.Instance, TimeProvider.System);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string? _whyInactive; // null = the game runs

    private RewardService Service(string broadcasterType = "affiliate", bool mock = false)
    {
        var store = Path.Combine(_dir, "twitch.json");
        TwitchAccountStore.Save(new TwitchAccount("42", "streamer", broadcasterType, TwitchAuthClient.Scopes, "ACCESS", "REFRESH", DateTimeOffset.UtcNow.AddHours(3)), store);
        var session = new TwitchSession(new TwitchAuthClient(new HttpClient(), "cid"), store, NullLogger.Instance);
        var service = new RewardService(session, new ChannelPointsApi(session, new HttpClient(_helix)), new ChatApi(session, new HttpClient(_helix)), _queue, mock,
            Path.Combine(_dir, "out"), Path.Combine(_dir, "rewards.json"), () => _whyInactive, NullLogger.Instance, "https://tf3.example.com/");
        service.Finished += _outcomes.Enqueue;
        return service;
    }

    private string QueueText => File.ReadAllText(Path.Combine(_dir, "cmd", "tf3export_queue.lua"));

    private void ModReports(string status, string? error = null, string? until = null, long? entity = null, int? count = null, int? total = null)
    {
        var id = System.Text.RegularExpressions.Regex.Match(QueueText, "id = \"(\\w+)\"").Groups[1].Value;
        var extra = (error is null ? "" : $",\"error\":\"{error}\"") + (until is null ? "" : $",\"until\":{until}")
            + (entity is null ? "" : $",\"entity\":{entity}") + (count is null ? "" : $",\"count\":{count}")
            + (total is null ? "" : $",\"total\":{total}");
        var json = $$"""{"results":[{"id":"{{id}}","status":"{{status}}"{{extra}}}]}""";
        var path = Path.Combine(_dir, "cmd", "tf3export_results.lua");
        File.WriteAllText(path, $"function data()\r\nreturn {{\r\n\t\tjson = \"{json.Replace("\"", "\\\"")}\",\r\n\t}}\r\nend");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 1000)));
        _queue.Poll();
    }

    // ------------------------------------------------------------------ dopasowanie celu

    [Theory]
    [InlineData("lodz", 10)]
    [InlineData("  ŁÓDŹ ", 10)]
    [InlineData("krakow", 11)]          // the owner in the name doesn't get in the way
    [InlineData("nowy s", 12)]          // jednoznaczny prefiks
    public void Finds_towns_by_forgiving_name(string input, long entity)
    {
        var objects = GameObjects.Load(Path.Combine(_dir, "out", "latest", "dynamic.json"));

        Assert.Equal(entity, TargetResolver.Resolve(objects, GameObjectKind.Town, input).Target!.Entity);
    }

    [Theory]
    [InlineData("nowy", "pasuje do kilku: Nowy Sącz, Nowy Targ")]
    [InlineData("gdańsk", "nie znaleziono „gdańsk”")]
    [InlineData("  ", "nie podano nazwy")]
    [InlineData("bytom", "nie znaleziono „bytom”")] // an industry isn't a town
    public void Rejects_unclear_targets(string input, string error)
    {
        var objects = GameObjects.Load(Path.Combine(_dir, "out", "latest", "dynamic.json"));

        var r = TargetResolver.Resolve(objects, GameObjectKind.Town, input);

        Assert.Null(r.Target);
        Assert.Contains(error, r.Error);
    }

    // ------------------------------------------------------------------ ustawienia

    [Fact]
    public void Settings_round_trip_and_fill_missing_rewards()
    {
        var path = Path.Combine(_dir, "rewards-test.json");
        var custom = RewardSettings.Default with
        {
            Rewards = [RewardKinds.Defaults.Single(d => d.Kind == RewardKind.BoostTown) with { Cost = 1234, Months = 999, Factor = 9 }],
            ChannelUserId = "42",
            TwitchIds = new Dictionary<RewardKind, string> { [RewardKind.BoostTown] = "rw1" },
        };

        RewardSettingsStore.Save(custom.Normalized(), path);
        var loaded = RewardSettingsStore.Load(path);

        Assert.Equal(Enum.GetValues<RewardKind>(), loaded.Rewards.Select(r => r.Kind));
        var boost = loaded.Get(RewardKind.BoostTown);
        Assert.Equal((1234, 60, 5.0), (boost.Cost, boost.Months, boost.Factor)); // clamped to the ranges
        Assert.Equal("rw1", loaded.TwitchIds[RewardKind.BoostTown]);
    }

    [Fact]
    public void Old_settings_with_days_get_default_months()
    {
        var path = Path.Combine(_dir, "rewards-old.json");
        File.WriteAllText(path, """
            {"Rewards":[{"Kind":"StopLine","Enabled":true,"Title":"Stop","Prompt":"p","Cost":777,"CooldownSec":0,"MaxPerStream":0,"MaxPerUserPerStream":0,"Days":3,"Factor":1}],
             "ChannelUserId":"42","TwitchIds":{"StopLine":"rw9"}}
            """);

        var stop = RewardSettingsStore.Load(path).Get(RewardKind.StopLine);

        Assert.Equal(777, stop.Cost);
        Assert.Equal(RewardKinds.Defaults.Single(d => d.Kind == RewardKind.StopLine).Months, stop.Months);
    }

    [Fact]
    public void Old_adoption_texts_become_buying()
    {
        var path = Path.Combine(_dir, "rewards-adopt.json");
        File.WriteAllText(path, """
            {"Rewards":[{"Kind":"AdoptTown","Enabled":true,"Title":"TF3: adoptuj miasto","Prompt":"Wpisz nazwę miasta bez właściciela. Zostaniesz jego właścicielem (pokaże się w grze i na mapie).","Cost":4321,"CooldownSec":0,"MaxPerStream":0,"MaxPerUserPerStream":0,"Months":1,"Factor":1},
                         {"Kind":"BoostTown","Enabled":true,"Title":"Mój boost","Prompt":"mój opis","Cost":1,"CooldownSec":0,"MaxPerStream":0,"MaxPerUserPerStream":0,"Months":1,"Factor":2}],
             "ChannelUserId":"","TwitchIds":{}}
            """);

        var s = RewardSettingsStore.Load(path);

        Assert.Equal(("TF3: kup miasto", 4321), (s.Get(RewardKind.AdoptTown).Title, s.Get(RewardKind.AdoptTown).Cost));
        Assert.StartsWith("Wpisz nazwę miasta bez właściciela. Kupujesz je", s.Get(RewardKind.AdoptTown).Prompt);
        Assert.Equal(("Mój boost", "mój opis"), (s.Get(RewardKind.BoostTown).Title, s.Get(RewardKind.BoostTown).Prompt)); // custom texts stay
    }

    [Theory]
    [InlineData(RewardKind.AdoptTown, 10, "https://tf3.example.com/#town=10")]
    [InlineData(RewardKind.BoostIndustry, 20, "https://tf3.example.com/#industry=20")]
    [InlineData(RewardKind.HoldLine, 30, "https://tf3.example.com/#line=30")]
    public void Links_open_the_object_on_the_site(RewardKind kind, long entity, string link) =>
        Assert.Equal(link, RewardMessages.Link("https://tf3.example.com", kind, entity));

    [Fact]
    public async Task Buying_a_town_links_to_it_in_chat()
    {
        var service = Service();
        service.Simulate(RewardKind.AdoptTown, "łódź", "widz", true, out _);

        ModReports("ok");

        var chat = await _helix.WaitFor(r => r.Url.EndsWith("/chat/messages"));
        Assert.Equal("@widz miasto Łódź jest teraz Twoje! https://tf3.example.com/#town=10", Message(chat));
    }

    [Fact]
    public void Prompt_shows_days_and_percent()
    {
        var c = RewardKinds.Defaults.Single(d => d.Kind == RewardKind.BoostIndustry) with { Prompt = "+{percent}% na {months} mies.", Months = 7, Factor = 1.25 };

        Assert.Equal("+25% na 7 mies.", c.TwitchPrompt);
    }

    [Fact]
    public void Twitch_body_requires_input_and_keeps_redemptions_in_the_queue()
    {
        var body = RewardService.Body(RewardKinds.Defaults[0] with { CooldownSec = 0, MaxPerUserPerStream = 2 }, active: false);

        Assert.True((bool)body["is_user_input_required"]!);
        Assert.False((bool)body["should_redemptions_skip_request_queue"]!); // otherwise points can't be refunded
        Assert.False((bool)body["is_enabled"]!);
        Assert.False((bool)body["is_paused"]!);
        Assert.False((bool)body["is_global_cooldown_enabled"]!);
        Assert.False(body.ContainsKey("global_cooldown_seconds"));
        Assert.Equal(2, (int)body["max_per_user_per_stream"]!);
    }

    [Fact]
    public void Prompt_gets_the_site_address()
    {
        var c = RewardKinds.Defaults.Single(d => d.Kind == RewardKind.CreateTown);

        var prompt = (string)RewardService.Body(c, active: true, RewardMessages.SiteHost("https://tf3.example.com/"))["prompt"]!;

        Assert.StartsWith("Na mapie tf3.example.com kliknij", prompt);
    }

    // ------------------------------------------------------------------ wykupienia (symulacja)

    [Fact]
    public void New_town_from_a_map_code()
    {
        var service = Service();

        Assert.Null(service.Simulate(RewardKind.CreateTown, "[3000, -2000] Nowa Wieś", "widz", false, out _));

        Assert.Contains("kind = \"createTown\"", QueueText);
        Assert.Contains("x = 3000, y = -2000, name = \"Nowa Wieś\", owner = \"widz\"", QueueText);
    }

    [Fact]
    public void New_industry_uses_the_file_from_the_export_and_links_to_the_built_industry()
    {
        var service = Service();
        service.Simulate(RewardKind.CreateIndustry, "[9000, 9000, coal_mine] Kopalnia Ali", "ala", true, out _);

        Assert.Contains("file = \"::/industries/coal_mine/coal_mine.con\", name = \"Kopalnia Ali\"", QueueText);
        ModReports("ok", entity: 4321);

        var o = Assert.Single(_outcomes);
        Assert.True(o.Ok);
        Assert.EndsWith("https://tf3.example.com/#industry=4321", o.ChatText);
    }

    [Fact]
    public void New_town_links_to_a_pin_when_the_mod_does_not_report_its_id()
    {
        var service = Service();
        service.Simulate(RewardKind.CreateTown, "[3000, -2000] Nowa Wieś", "widz", true, out _);

        ModReports("ok"); // an older mod version: no entity

        Assert.EndsWith("https://tf3.example.com/#pin=3000_-2000&note=Nowa%20Wie%C5%9B", Assert.Single(_outcomes).ChatText);
    }

    [Fact]
    public void New_town_too_close_to_another_is_refunded()
    {
        var service = Service();

        var outcome = service.Simulate(RewardKind.CreateTown, "[400, 300] Przedmieście", "widz", false, out _);

        Assert.True(outcome!.Refunded);
        Assert.Contains("za blisko miasta Łódź (500 m", outcome.Message);
        Assert.Equal(0, _queue.PendingCount);
    }

    [Fact]
    public void Game_errors_while_building_are_explained()
    {
        var service = Service();
        service.Simulate(RewardKind.CreateTown, "[3000, -2000] Nowa Wieś", "widz", false, out _);

        ModReports("invalid", "on water");

        Assert.Contains("na wodzie", Assert.Single(_outcomes).Message);
    }

    [Fact]
    public void Adoption_sends_an_adopt_command_from_channel_points()
    {
        var service = Service();

        Assert.Null(service.Simulate(RewardKind.AdoptTown, "lodz", "Widz|X", false, out _));

        Assert.Contains("kind = \"adopt\"", QueueText);
        Assert.Contains("entity = 10", QueueText);
        Assert.Contains("owner = \"WidzX\"", QueueText);
    }

    [Fact]
    public async Task Buying_an_industry_adopts_it_and_links_to_it_in_chat()
    {
        var service = Service();

        Assert.Null(service.Simulate(RewardKind.BuyIndustry, "kopalnia", "widz", true, out _));
        Assert.Contains("kind = \"adopt\"", QueueText);
        Assert.Contains("entity = 20", QueueText);
        ModReports("ok");

        var chat = await _helix.WaitFor(r => r.Url.EndsWith("/chat/messages"));
        Assert.Equal("@widz zakład Kopalnia węgla Bytom jest teraz Twój! https://tf3.example.com/#industry=20", Message(chat));
    }

    [Fact]
    public void Buying_an_industry_with_an_owner_is_refunded_right_away()
    {
        var service = Service();

        var outcome = service.Simulate(RewardKind.BuyIndustry, "huta", "widz", false, out _);

        Assert.True(outcome!.Refunded);
        Assert.Contains("ma już właściciela (ola)", outcome.Message);
        Assert.Equal(0, _queue.PendingCount);
    }

    [Fact]
    public void Adoption_of_a_town_with_an_owner_is_refunded_right_away()
    {
        var service = Service();

        var outcome = service.Simulate(RewardKind.AdoptTown, "kraków", "widz", false, out _);

        Assert.True(outcome!.Refunded);
        Assert.Contains("ma już właściciela (ala)", outcome.Message);
        Assert.Equal(0, _queue.PendingCount);
    }

    [Theory]
    [InlineData(RewardKind.BoostTown, "nowy sacz", "effect = \"townBoost\", entity = 12, months = 12, user = \"widz\", factor = 1.5")]
    [InlineData(RewardKind.BoostIndustry, "kopalnia", "effect = \"industryBoost\", entity = 20, months = 6, user = \"widz\", factor = 1.5")]
    [InlineData(RewardKind.StopLine, "linia 1", "effect = \"lineStop\", entity = 30, months = 1, user = \"widz\" }")]
    [InlineData(RewardKind.HoldLine, "linia 1", "effect = \"lineHold\", entity = 30, months = 1, user = \"widz\" }")]
    public void Effects_carry_months_and_factor_from_settings(RewardKind kind, string input, string expected)
    {
        var service = Service();

        Assert.Null(service.Simulate(kind, input, "widz", false, out _));

        Assert.Contains(expected, QueueText);
    }

    // ------------------------------------------------------------------ sabotage

    [Theory]
    [InlineData(RewardKind.StrikeIndustry, "huta", "effect = \"industryStrike\", entity = 21, months = 3, user = \"widz\", factor = 0.01 }")]
    [InlineData(RewardKind.BadFuel, "tramwaj", "effect = \"lineFuelBad\", entity = 31, months = 3, user = \"widz\", factor = 0.8 }")]
    [InlineData(RewardKind.PremiumFuel, "linia 1", "effect = \"lineFuelPremium\", entity = 30, months = 6, user = \"widz\", factor = 1.2 }")]
    [InlineData(RewardKind.BrakeSabotage, "tramwaj", "kind = \"wreck\"")]
    public void Sabotage_and_fuel_send_their_commands(RewardKind kind, string input, string expected)
    {
        var service = Service();

        Assert.Null(service.Simulate(kind, input, "widz", false, out _));

        Assert.Contains(expected, QueueText);
    }

    [Fact]
    public void Brake_sabotage_sends_the_largest_share()
    {
        var service = Service();

        service.Simulate(RewardKind.BrakeSabotage, "tramwaj", "widz", false, out _);

        Assert.Contains("entity = 31, share = 0.5 }", QueueText);
    }

    [Theory]
    [InlineData(RewardKind.StrikeIndustry, "kopalnia", "nie ma właściciela")]  // unowned objects are exempt
    [InlineData(RewardKind.BadFuel, "linia 1", "nie ma właściciela")]
    [InlineData(RewardKind.BrakeSabotage, "autobus", "należy do Ciebie")]       // we don't sabotage one's own
    [InlineData(RewardKind.TaxRaid, "widz", "nie możesz nasłać skarbówki na siebie")]
    [InlineData(RewardKind.TaxRaid, "ala", "ala nie ma zakładów")]              // only has a town
    [InlineData(RewardKind.TaxRaid, "nikt", "widz „nikt” nie ma nic w grze")]
    public void Sabotage_only_hits_other_viewers(RewardKind kind, string input, string error)
    {
        var service = Service();

        var outcome = service.Simulate(kind, input, "Widz", false, out _);

        Assert.True(outcome!.Refunded);
        Assert.Contains(error, outcome.Message);
        Assert.Equal(0, _queue.PendingCount);
    }

    [Fact]
    public async Task Tax_raid_hits_all_industries_of_the_viewer_by_nick()
    {
        var service = Service();

        Assert.Null(service.Simulate(RewardKind.TaxRaid, "@OLA", "widz", false, out _));
        Assert.Contains("effect = \"industryTaxRaid\", entity = 21, months = 3, user = \"widz\", factor = 0.5, entities = \"21\", target = \"ola\"", QueueText);
        ModReports("ok", until: """{"year":1922,"month":5,"day":12}""");

        var o = Assert.Single(_outcomes);
        Assert.Equal("@widz nalot skarbówki na zakłady widza ola (1) (-50% produkcji) trwa do 12 maja 1922 (data w grze). https://tf3.example.com/#owner=ola",
            o.ChatText);
        var queue = await WaitForNotify();
        Assert.Contains("text = \"widz nasłał(a) skarbówkę na zakłady widza ola (1): produkcja -50% do 12 maja 1922.\"", queue);
    }

    [Fact]
    public async Task Brake_sabotage_reports_wrecked_vehicles_and_points_at_the_line()
    {
        var service = Service();
        service.Simulate(RewardKind.BrakeSabotage, "tramwaj", "widz", false, out _);

        ModReports("ok", count: 2, total: 5);

        var o = Assert.Single(_outcomes);
        Assert.Equal("@widz sabotaż hamulców na linii Tramwaj: zniszczone pojazdy: 2 z 5. https://tf3.example.com/#line=31", o.ChatText);
        var queue = await WaitForNotify();
        Assert.Contains("text = \"Problemy techniczne na linii Tramwaj: zniszczone pojazdy: 2 z 5 (sabotaż: widz).\"", queue);
        Assert.Contains("entity = 31", queue); // clicking the notification shows the line
    }

    [Fact]
    public void Sabotage_prompts_show_the_drop_without_sign_and_factors_stay_in_their_range()
    {
        var strike = RewardKinds.Defaults.Single(d => d.Kind == RewardKind.StrikeIndustry);

        Assert.Contains("spada o 99%", strike.TwitchPrompt);
        Assert.Equal(0.95, (strike with { Factor = 3 }).Normalized().Factor);
        Assert.Equal(1.05, (RewardKinds.Defaults.Single(d => d.Kind == RewardKind.PremiumFuel) with { Factor = 0.5 }).Normalized().Factor);
        Assert.Contains("do 50%", RewardKinds.Defaults.Single(d => d.Kind == RewardKind.BrakeSabotage).TwitchPrompt);
    }

    // ------------------------------------------------------------------ subsidy and loan

    [Fact]
    public async Task Subsidy_sends_the_amount_without_viewer_text()
    {
        var service = Service();

        Assert.Null(service.Simulate(RewardKind.Subsidy, "", "widz", false, out _));
        Assert.Contains("kind = \"subsidy\"", QueueText);
        Assert.Contains("amount = 10000000, user = \"widz\"", QueueText);
        ModReports("ok");

        var o = Assert.Single(_outcomes);
        Assert.Equal("@widz dotacja 10 mln $ trafiła na konto firmy. Dzięki!", o.ChatText);
        var queue = await WaitForNotify();
        Assert.Contains("text = \"widz przekazał(a) firmie dotację 10 mln $.\"", queue);
    }

    [Fact]
    public async Task Loan_sends_amount_months_and_interest_from_settings()
    {
        var service = Service();
        service.UpdateSettings(service.Settings with
        {
            Rewards = [.. service.Settings.Rewards.Select(r => r.Kind == RewardKind.Loan ? r with { Amount = 2_500_000, Months = 36, Factor = 1.5 } : r)],
        });

        Assert.Null(service.Simulate(RewardKind.Loan, "", "widz", false, out _));
        Assert.Contains("kind = \"loan\"", QueueText);
        Assert.Contains("amount = 2500000, months = 36, percent = 1.5, user = \"widz\"", QueueText);
        ModReports("ok");

        Assert.Equal("@widz kredyt 2,5 mln $ na 1,5% przyznany: gra spłaca go w ratach przez 36 mies. gry.", Assert.Single(_outcomes).ChatText);
        var queue = await WaitForNotify();
        Assert.Contains("text = \"widz udzielił(a) firmie kredytu 2,5 mln $ na 1,5% na 36 mies. (raty w zakładce Kredyty).\"", queue);
    }

    [Fact]
    public void Loan_over_the_game_limit_is_refunded()
    {
        var service = Service();
        service.Simulate(RewardKind.Loan, "", "widz", false, out _);

        ModReports("invalid", error: "loan limit 4");

        var o = Assert.Single(_outcomes);
        Assert.True(o.Refunded);
        Assert.Equal("@widz kredyt: 10 mln $: firma ma już największą liczbę kredytów w grze (4). Punkty wróciły do Ciebie.", o.ChatText);
    }

    [Fact]
    public void Money_rewards_have_defaults_need_no_text_and_keep_ranges()
    {
        var loan = RewardKinds.Defaults.Single(d => d.Kind == RewardKind.Loan);
        var subsidy = RewardKinds.Defaults.Single(d => d.Kind == RewardKind.Subsidy);

        Assert.Equal((10_000_000L, 60, 2.0), (loan.Amount, loan.Months, loan.Factor));
        Assert.Equal(10_000_000L, subsidy.Amount);
        Assert.False(loan.RequiresInput);
        Assert.False(subsidy.RequiresInput);
        Assert.False((bool)RewardService.Body(loan, true)["is_user_input_required"]!);
        Assert.Contains("kredytu 10 mln $ na 2% na 60 mies.", loan.TwitchPrompt);
        Assert.Contains("dotację 10 mln $", subsidy.TwitchPrompt);
        Assert.Equal((360, 50.0), ((loan with { Months = 999, Factor = 99 }).Normalized().Months, (loan with { Factor = 99 }).Normalized().Factor));
        Assert.Equal(RewardConfig.AmountRange.Min, (subsidy with { Amount = 5 }).Normalized().Amount);
        Assert.Equal(0, RewardKinds.Defaults.Single(d => d.Kind == RewardKind.BoostTown).Normalized().Amount);
    }

    [Fact]
    public void Old_settings_without_amount_get_the_default()
    {
        var path = Path.Combine(_dir, "rewards-money.json");
        File.WriteAllText(path, """
            {"Rewards":[{"Kind":"Subsidy","Enabled":true,"Title":"Kasa","Prompt":"p","Cost":1,"CooldownSec":0,"MaxPerStream":0,"MaxPerUserPerStream":0,"Months":1,"Factor":1}],
             "ChannelUserId":"","TwitchIds":{}}
            """);

        Assert.Equal(10_000_000L, RewardSettingsStore.Load(path).Get(RewardKind.Subsidy).Amount);
    }

    [Theory]
    [InlineData(10_000_000L, "10 mln $")]
    [InlineData(2_500_000L, "2,5 mln $")]
    [InlineData(750_000L, "750 tys. $")]
    [InlineData(1_250_000_000L, "1,25 mld $")]
    [InlineData(999L, "999 $")]
    public void Money_is_short_and_polish(long amount, string text) => Assert.Equal(text, RewardMessages.Money(amount));

    [Theory]
    [InlineData("10 000 000", 10_000_000L)]
    [InlineData("10.000.000", 10_000_000L)]
    [InlineData("10 000 000", 10_000_000L)]
    [InlineData("5000000", 5_000_000L)]
    public void Amount_field_ignores_thousands_separators(string text, long amount)
    {
        Assert.True(Tf3Bridge.Ui.RewardsDialog.TryParseAmount(text, out var parsed));
        Assert.Equal(amount, parsed);
    }

    [Fact]
    public void Disabled_reward_is_refunded()
    {
        var service = Service();
        service.UpdateSettings(service.Settings with { Rewards = [.. service.Settings.Rewards.Select(r => r with { Enabled = r.Kind != RewardKind.StopLine })] });

        var outcome = service.Simulate(RewardKind.StopLine, "linia 1", "widz", false, out _);

        Assert.True(outcome!.Refunded);
    }

    [Fact]
    public void Result_from_the_game_finishes_the_simulation()
    {
        var service = Service();
        service.Simulate(RewardKind.BoostTown, "lodz", "widz", false, out var token);

        ModReports("ok");

        var o = Assert.Single(_outcomes);
        Assert.Equal(token, o.Token);
        Assert.True(o.Ok);
        Assert.Empty(_helix.Requests); // the simulation doesn't touch Twitch
    }

    [Fact]
    public void Old_mod_in_the_game_is_explained()
    {
        var service = Service();
        service.Simulate(RewardKind.HoldLine, "linia 1", "widz", false, out _);

        ModReports("invalid", "unknown kind effect");

        var o = Assert.Single(_outcomes);
        Assert.True(o.Refunded);
        Assert.Contains("uruchom TF3 ponownie", o.Message);
    }

    [Fact]
    public void Log_shows_numbers_like_the_game_file()
    {
        var c = ModCommand.Effect("townBoost", 1, 30, 1.5, CommandSource.Test, "x");

        Assert.Contains("factor=1.5", c.ToString());
    }

    // ------------------------------------------------------------------ prawdziwe wykupienia

    private async Task<RewardService> SyncedService()
    {
        _helix.Rewards = """{"data":[]}""";
        var service = Service();
        await service.TickAsync(default);
        Assert.Equal(RewardState.Ok, service.Status.State);
        _helix.Requests.Clear();
        return service;
    }

    [Fact]
    public async Task Sync_creates_enabled_rewards_and_remembers_their_ids()
    {
        _helix.Rewards = """{"data":[]}""";
        var service = Service();

        await service.TickAsync(default);

        var creates = _helix.Requests.Where(r => r.Method == "POST").ToList();
        Assert.Equal(RewardKinds.Defaults.Count, creates.Count);
        Assert.All(creates, r => Assert.Contains("broadcaster_id=42", r.Url));
        Assert.Equal(RewardKinds.Defaults.Count, service.Settings.TwitchIds.Count);
        Assert.Equal("42", RewardSettingsStore.Load(Path.Combine(_dir, "rewards.json")).ChannelUserId);
    }

    [Fact]
    public async Task Sync_reuses_existing_rewards()
    {
        _helix.Rewards = """{"data":[{"id":"old","title":"TF3: kup miasto","is_enabled":true,"is_paused":false}]}""";
        var service = Service();

        await service.TickAsync(default);

        Assert.Equal("old", service.Settings.TwitchIds[RewardKind.AdoptTown]);
        Assert.Contains(_helix.Requests, r => r.Method == "PATCH" && r.Url.Contains("id=old"));
        Assert.Equal(RewardKinds.Defaults.Count - 1, _helix.Requests.Count(r => r.Method == "POST"));
    }

    [Fact]
    public async Task Creates_rewards_disabled_while_the_game_sends_no_data()
    {
        _helix.Rewards = """{"data":[]}""";
        _whyInactive = "brak danych z gry (wczytaj zapis)";
        var service = Service();

        await service.TickAsync(default);

        Assert.Equal(RewardState.Inactive, service.Status.State);
        Assert.Contains("wczytaj zapis", service.Status.Message);
        Assert.All(_helix.Requests.Where(r => r.Method == "POST"), r => Assert.Contains("\"is_enabled\":false", r.Body));
    }

    [Fact]
    public async Task Enables_and_disables_rewards_with_the_game()
    {
        var service = await SyncedService();

        _whyInactive = "most jest zatrzymany";
        await service.TickAsync(default);
        var off = _helix.Requests.Where(r => r.Method == "PATCH").ToList();
        _helix.Requests.Clear();
        _whyInactive = null;
        await service.TickAsync(default);
        var on = _helix.Requests.Where(r => r.Method == "PATCH").ToList();

        Assert.Equal(RewardKinds.Defaults.Count, off.Count);
        Assert.All(off, r => Assert.Equal("""{"is_enabled":false}""", r.Body));
        Assert.Equal(RewardKinds.Defaults.Count, on.Count);
        Assert.All(on, r => Assert.Equal("""{"is_enabled":true}""", r.Body));
        Assert.Equal(RewardState.Ok, service.Status.State);
    }

    [Fact]
    public async Task Disables_rewards_when_the_bridge_closes()
    {
        var service = await SyncedService();

        await service.DisableAsync(default);
        await service.DisableAsync(default); // the second time sends nothing

        var patches = _helix.Requests.Where(r => r.Method == "PATCH").ToList();
        Assert.Equal(RewardKinds.Defaults.Count, patches.Count);
        Assert.All(patches, r => Assert.Equal("""{"is_enabled":false}""", r.Body));
    }

    [Fact]
    public async Task Refunds_redemptions_while_the_game_sends_no_data()
    {
        var service = await SyncedService();
        var rewardId = service.Settings.TwitchIds[RewardKind.BoostTown];
        _whyInactive = "gra nie wysyła danych od 2 min";

        await service.HandleRedemptionForTestAsync(new ChannelPointsRedemption("red1", rewardId, "", 0, "7", "widz", "Widz", "łódź", DateTimeOffset.UtcNow));

        var close = await _helix.WaitFor(r => r.Url.Contains("redemptions") && r.Method == "PATCH");
        Assert.Contains("CANCELED", close.Body);
        Assert.False(File.Exists(Path.Combine(_dir, "cmd", "tf3export_queue.lua")) && QueueText.Contains("townBoost"));
        Assert.Contains("gra nie działa", Assert.Single(_outcomes).Message);
    }

    [Theory]
    [InlineData(false, 0.0, "most jest zatrzymany")]
    [InlineData(true, null, "brak danych z gry (wczytaj zapis)")]
    [InlineData(true, 3.0, "gra nie wysyła danych od 2 min")]
    [InlineData(true, 0.5, null)]
    public void Rewards_need_a_running_bridge_and_fresh_game_data(bool running, double? minutesAgo, string? why)
    {
        var now = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(why, RewardService.WhyInactive(running, minutesAgo is { } m ? now.AddMinutes(-m) : null, now));
    }

    [Fact]
    public async Task Without_affiliate_nothing_is_sent_to_twitch()
    {
        var service = Service(broadcasterType: "");

        await service.TickAsync(default);

        Assert.Equal(RewardState.NotAffiliate, service.Status.State);
        Assert.Empty(_helix.Requests);
    }

    [Fact]
    public async Task Fulfills_the_redemption_when_the_game_confirms()
    {
        var service = await SyncedService();
        var rewardId = service.Settings.TwitchIds[RewardKind.BoostTown];

        await service.HandleRedemptionForTestAsync(new ChannelPointsRedemption("red1", rewardId, "", 0, "7", "widz", "Widz", "łódź", DateTimeOffset.UtcNow));
        ModReports("ok");

        var patch = await _helix.WaitFor(r => r.Method == "PATCH" && r.Url.Contains("redemptions"));
        Assert.Contains("id=red1", patch.Url);
        Assert.Contains($"reward_id={rewardId}", patch.Url);
        Assert.Contains("\"status\":\"FULFILLED\"", patch.Body);
    }

    [Fact]
    public async Task The_same_redemption_is_carried_out_once()
    {
        var service = await SyncedService();
        var rewardId = service.Settings.TwitchIds[RewardKind.Subsidy];
        var redemption = new ChannelPointsRedemption("red1", rewardId, "", 0, "7", "widz", "Widz", "", DateTimeOffset.UtcNow);

        // EventSub, then the backlog after a resync: Twitch still lists it as UNFULFILLED while the game works on it
        await service.HandleRedemptionForTestAsync(redemption);
        await service.HandleRedemptionForTestAsync(redemption);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(QueueText, "kind = \"subsidy\""));
    }

    // ------------------------------------------------------------------ in-game notification

    // The notification goes to the queue in the background (after Twitch answers), so we wait for the file.
    private async Task<string?> WaitForNotify(int ms = 3000)
    {
        for (var waited = 0; waited < ms; waited += 20)
        {
            if (QueueText.Contains("kind = \"notify\"")) return QueueText;
            await Task.Delay(20);
        }
        return null;
    }

    [Fact]
    public async Task Game_notification_after_the_redemption_is_fulfilled()
    {
        var service = await SyncedService();
        var rewardId = service.Settings.TwitchIds[RewardKind.AdoptTown];

        await service.HandleRedemptionForTestAsync(new ChannelPointsRedemption("red1", rewardId, "", 0, "7", "widz", "Widz", "łódź", DateTimeOffset.UtcNow));
        ModReports("ok");

        await _helix.WaitFor(r => r.Method == "PATCH" && r.Body.Contains("FULFILLED"));
        var queue = await WaitForNotify();
        Assert.NotNull(queue);
        Assert.Contains("title = \"Twitch: Widz\", text = \"Widz kupił(a) miasto Łódź.\"", queue);
        Assert.Contains("entity = 10", queue); // clicking the notification shows the town
    }

    [Fact]
    public async Task No_game_notification_when_twitch_does_not_take_the_points()
    {
        var service = await SyncedService();
        var rewardId = service.Settings.TwitchIds[RewardKind.AdoptTown];
        _helix.Forbidden.Add("red1");

        await service.HandleRedemptionForTestAsync(new ChannelPointsRedemption("red1", rewardId, "", 0, "7", "widz", "Widz", "łódź", DateTimeOffset.UtcNow));
        ModReports("ok");

        await _helix.WaitFor(r => r.Method == "PATCH" && r.Body.Contains("FULFILLED"));
        Assert.Null(await WaitForNotify(300));
    }

    [Fact]
    public async Task No_game_notification_after_a_refund()
    {
        var service = await SyncedService();
        var rewardId = service.Settings.TwitchIds[RewardKind.AdoptTown];

        await service.HandleRedemptionForTestAsync(new ChannelPointsRedemption("red2", rewardId, "", 0, "7", "widz", "Widz", "łódź", DateTimeOffset.UtcNow));
        ModReports("invalid", "already adopted by ola");

        await _helix.WaitFor(r => r.Method == "PATCH" && r.Body.Contains("CANCELED"));
        Assert.Null(await WaitForNotify(300));
    }

    [Fact]
    public async Task Simulation_shows_a_test_notification_with_the_new_town()
    {
        var service = Service();
        service.Simulate(RewardKind.CreateTown, "[3000, -2000] Nowa Wieś", "widz", false, out _);

        ModReports("ok", entity: 777);

        var queue = await WaitForNotify();
        Assert.NotNull(queue);
        Assert.Contains("title = \"[TEST] Twitch: widz\", text = \"widz założył(a) miasto Nowa Wieś.\"", queue);
        Assert.Contains("entity = 777", queue);
    }

    [Fact]
    public async Task Game_notification_stays_on_screen_as_long_as_set()
    {
        var service = Service();
        service.UpdateSettings(service.Settings with { NotificationSeconds = 12 });
        service.Simulate(RewardKind.AdoptTown, "lodz", "widz", false, out _);

        ModReports("ok");

        Assert.Contains("dismissMs = 12000", await WaitForNotify());
    }

    [Fact]
    public void Notification_seconds_round_trip_and_are_clamped()
    {
        var path = Path.Combine(_dir, "notify.json");
        Assert.Equal(30, RewardSettingsStore.Load(path).NotificationSeconds); // no file = defaults

        RewardSettingsStore.Save(RewardSettings.Default with { NotificationSeconds = 45 }, path);
        Assert.Equal(45, RewardSettingsStore.Load(path).NotificationSeconds);

        File.WriteAllText(path, File.ReadAllText(path).Replace("\"NotificationSeconds\": 45,", "")); // a file from before this setting
        Assert.Equal(30, RewardSettingsStore.Load(path).NotificationSeconds);
        Assert.Equal(300, (RewardSettings.Default with { NotificationSeconds = 9999 }).Normalized().NotificationSeconds);
        Assert.Equal(3, (RewardSettings.Default with { NotificationSeconds = 1 }).Normalized().NotificationSeconds);
    }

    [Fact]
    public void Prompts_with_the_old_days_placeholder_switch_to_months()
    {
        var path = Path.Combine(_dir, "days.json");
        var boost = RewardSettings.Default.Get(RewardKind.BoostTown) with { Prompt = "Przez {days} dni gry miasto rośnie o {percent}%." };
        RewardSettingsStore.Save(RewardSettings.Default with { Rewards = [boost] }, path);

        var loaded = RewardSettingsStore.Load(path).Get(RewardKind.BoostTown);
        Assert.Equal("Przez {months} mies. gry miasto rośnie o {percent}%.", loaded.Prompt);
        Assert.DoesNotContain("{", loaded.TwitchPrompt);
    }

    [Fact]
    public async Task No_game_notification_when_turned_off()
    {
        var service = Service();
        service.UpdateSettings(service.Settings with { GameNotifications = false });
        service.Simulate(RewardKind.AdoptTown, "lodz", "widz", false, out _);

        ModReports("ok");

        Assert.True(Assert.Single(_outcomes).Ok);
        Assert.Null(await WaitForNotify(300));
    }

    [Theory]
    [InlineData(RewardKind.BoostTown, "{\"year\":1922,\"month\":5,\"day\":12}", "widz przyspieszył(a) wzrost miasta Łódź o +")]
    [InlineData(RewardKind.StopLine, "{\"year\":1922,\"month\":5,\"day\":12}", "widz zatrzymał(a) linię Linia 1 do 12 maja 1922.")]
    public async Task Game_notification_for_effects_has_the_end_date(RewardKind kind, string until, string expected)
    {
        var service = Service();
        service.Simulate(kind, kind == RewardKind.StopLine ? "linia 1" : "łódź", "widz", false, out _);

        ModReports("ok", until: until);

        var queue = await WaitForNotify();
        Assert.Contains(expected, queue);
        Assert.Contains("do 12 maja 1922", queue);
    }

    [Fact]
    public void Every_reward_kind_has_a_game_notification_text()
    {
        foreach (var config in RewardKinds.Defaults.Where(d => !RewardKinds.Manual(d.Kind)))
            Assert.Contains("Cel", RewardMessages.Notification(config, "widz", "Cel", null));
    }

    // ------------------------------------------------------------------ rewards carried out by the streamer

    [Fact]
    public async Task Manual_rewards_are_created_and_switched_with_the_game()
    {
        var service = await SyncedService();
        var bomb = service.Settings.TwitchIds[RewardKind.Bomb];

        _whyInactive = "most jest zatrzymany";
        await service.TickAsync(default);

        Assert.Contains(_helix.Requests, r => r.Method == "PATCH" && r.Url.Contains($"id={bomb}") && r.Body == """{"is_enabled":false}""");
    }

    [Fact]
    public async Task Manual_reward_redemptions_stay_in_the_twitch_queue()
    {
        var service = await SyncedService();
        var rewardId = service.Settings.TwitchIds[RewardKind.RoadVehicle];

        await service.HandleRedemptionForTestAsync(new ChannelPointsRedemption("red9", rewardId, "", 0, "7", "widz", "Widz", "", DateTimeOffset.UtcNow));
        _whyInactive = "most jest zatrzymany"; // even when the game isn't running, no points are refunded
        await service.HandleRedemptionForTestAsync(new ChannelPointsRedemption("red10", rewardId, "", 0, "7", "widz", "Widz", "", DateTimeOffset.UtcNow));
        await Task.Delay(100);

        Assert.DoesNotContain(_helix.Requests, r => r.Url.Contains("redemptions"));
        Assert.Empty(_outcomes);
        Assert.Equal(0, _queue.PendingCount);
    }

    [Fact]
    public void Manual_rewards_ask_for_text_only_when_the_streamer_wants()
    {
        var bomb = RewardKinds.Defaults.Single(d => d.Kind == RewardKind.Bomb);

        Assert.False((bool)RewardService.Body(bomb, active: true)["is_user_input_required"]!);
        Assert.True((bool)RewardService.Body(bomb with { UserInput = true }, active: true)["is_user_input_required"]!);
        var town = RewardKinds.Defaults.Single(d => d.Kind == RewardKind.BoostTown) with { UserInput = false };
        Assert.True((bool)RewardService.Body(town, active: true)["is_user_input_required"]!);
    }

    [Fact]
    public void Manual_reward_settings_round_trip()
    {
        var path = Path.Combine(_dir, "manual.json");
        RewardSettingsStore.Save(RewardSettings.Default with
        {
            Rewards = [RewardKinds.Defaults.Single(d => d.Kind == RewardKind.Nature) with { UserInput = true, Prompt = "Napisz, co posadzić" }],
        }, path);

        var nature = RewardSettingsStore.Load(path).Get(RewardKind.Nature);

        Assert.True(nature.UserInput);
        Assert.Equal("Napisz, co posadzić", nature.Prompt);
        Assert.False(RewardSettingsStore.Load(path).Get(RewardKind.Bomb).UserInput);
    }

    [Fact]
    public void Manual_rewards_cannot_be_simulated()
    {
        var service = Service();

        var outcome = service.Simulate(RewardKind.Bomb, "", "widz", chat: false, out _);

        Assert.False(outcome!.Ok);
        Assert.Equal(0, _queue.PendingCount);
    }

    [Fact]
    public async Task Refunds_when_the_game_fails()
    {
        var service = await SyncedService();
        var rewardId = service.Settings.TwitchIds[RewardKind.AdoptTown];

        await service.HandleRedemptionForTestAsync(new ChannelPointsRedemption("red2", rewardId, "", 0, "7", "widz", "Widz", "łódź", DateTimeOffset.UtcNow));
        ModReports("invalid", "already adopted by ola");

        var patch = await _helix.WaitFor(r => r.Method == "PATCH" && r.Url.Contains("redemptions"));
        Assert.Contains("\"status\":\"CANCELED\"", patch.Body);
        Assert.Contains("ma już właściciela (ola)", _outcomes.Last().Message);
    }

    [Fact]
    public async Task Refunds_when_the_target_is_unknown()
    {
        var service = await SyncedService();
        var rewardId = service.Settings.TwitchIds[RewardKind.StopLine];

        await service.HandleRedemptionForTestAsync(new ChannelPointsRedemption("red3", rewardId, "", 0, "7", "widz", "Widz", "linia 99", DateTimeOffset.UtcNow));

        var patch = await _helix.WaitFor(r => r.Method == "PATCH" && r.Url.Contains("redemptions"));
        Assert.Contains("\"status\":\"CANCELED\"", patch.Body);
        Assert.Equal(0, _queue.PendingCount);
    }

    [Fact]
    public async Task Ignores_rewards_that_are_not_ours()
    {
        var service = await SyncedService();

        await service.HandleRedemptionForTestAsync(new ChannelPointsRedemption("red4", "someone-elses-reward", "", 0, "7", "widz", "Widz", "łódź", DateTimeOffset.UtcNow));

        Assert.Empty(_helix.Requests);
        Assert.Empty(_outcomes);
    }

    // ------------------------------------------------------------------ czat

    [Fact]
    public void Chat_text_names_the_effect_and_its_end_in_the_game_calendar()
    {
        var boost = RewardKinds.Defaults.Single(r => r.Kind == RewardKind.BoostTown);

        Assert.Equal("@widz boost miasta Szczecin (+50%) działa do 12 maja 1922 (data w grze).",
            RewardMessages.Text(boost, "widz", "Szczecin", ok: true, new GameDate(1922, 5, 12), reason: null));
        Assert.Equal("@widz boost miasta Szczecin (+50%) działa przez 12 mies. gry.",
            RewardMessages.Text(boost, "@widz", "Szczecin", ok: true, until: null, reason: null));
        Assert.Equal("@widz boost miasta: nie znaleziono „x”. Punkty wróciły do Ciebie.",
            RewardMessages.Text(boost, "widz", null, ok: false, until: null, reason: "nie znaleziono „x”"));
    }

    [Fact]
    public async Task Answers_the_viewer_in_chat_after_a_real_redemption()
    {
        var service = await SyncedService();
        var rewardId = service.Settings.TwitchIds[RewardKind.StopLine];

        await service.HandleRedemptionForTestAsync(new ChannelPointsRedemption("red5", rewardId, "", 0, "7", "widz", "Widz", "linia 1", DateTimeOffset.UtcNow));
        ModReports("ok", until: """{"year":1922,"month":5,"day":12}""");

        var chat = await _helix.WaitFor(r => r.Url.EndsWith("/chat/messages"));
        Assert.Contains("\"broadcaster_id\":\"42\"", chat.Body);
        Assert.Contains("\"sender_id\":\"42\"", chat.Body);
        Assert.Equal("@widz linia Linia 1 stoi do 12 maja 1922 (data w grze). https://tf3.example.com/#line=30", Message(chat));
        Assert.Equal(new GameDate(1922, 5, 12), _outcomes.Last().Until);
    }

    [Fact]
    public async Task Tells_the_viewer_why_the_points_came_back()
    {
        var service = await SyncedService();
        var rewardId = service.Settings.TwitchIds[RewardKind.BoostTown];

        await service.HandleRedemptionForTestAsync(new ChannelPointsRedemption("red6", rewardId, "", 0, "7", "widz", "Widz", "gdańsk", DateTimeOffset.UtcNow));

        var chat = await _helix.WaitFor(r => r.Url.EndsWith("/chat/messages"));
        Assert.Contains("nie znaleziono", Message(chat));
        Assert.Contains("Punkty wróciły", Message(chat));
    }

    [Fact]
    public async Task Chat_answers_can_be_turned_off()
    {
        var service = await SyncedService();
        service.UpdateSettings(service.Settings with { ChatMessages = false });
        var rewardId = service.Settings.TwitchIds[RewardKind.BoostTown];

        await service.HandleRedemptionForTestAsync(new ChannelPointsRedemption("red7", rewardId, "", 0, "7", "widz", "Widz", "gdańsk", DateTimeOffset.UtcNow));
        await _helix.WaitFor(r => r.Url.Contains("redemptions"));
        await Task.Delay(50);

        Assert.DoesNotContain(_helix.Requests, r => r.Url.EndsWith("/chat/messages"));
    }

    [Fact]
    public async Task Simulation_posts_to_chat_only_when_asked()
    {
        var service = Service();

        service.Simulate(RewardKind.BoostTown, "gdańsk", "widz", false, out _);
        service.Simulate(RewardKind.BoostTown, "gdańsk", "widz", true, out _);

        var chat = await _helix.WaitFor(r => r.Url.EndsWith("/chat/messages"));
        Assert.Single(_helix.Requests, r => r.Url.EndsWith("/chat/messages"));
        Assert.Contains("@widz", chat.Body);
    }

    // ------------------------------------------------------------------ own TF3 rewards

    [Fact]
    public async Task Switches_own_TF3_rewards_with_the_game()
    {
        _helix.AllRewards = """{"data":[{"id":"own","title":"TF3: wybierz następną linię","cost":2500,"is_enabled":true},{"id":"other","title":"Nawodnij się","is_enabled":true}]}""";
        _whyInactive = "brak danych z gry (wczytaj zapis)";
        var service = Service();
        await service.TickAsync(default);
        Assert.Contains(_helix.Requests, r => r.Method == "PATCH" && r.Url.Contains("id=own") && r.Body == """{"is_enabled":false}""");
        _helix.Requests.Clear();

        _whyInactive = null;
        await service.TickAsync(default);
        _whyInactive = "most jest zatrzymany";
        await service.TickAsync(default);

        Assert.Equal(["""{"is_enabled":true}""", """{"is_enabled":false}"""],
            _helix.Requests.Where(r => r.Method == "PATCH" && r.Url.Contains("id=own")).Select(r => r.Body));
        Assert.DoesNotContain(_helix.Requests, r => r.Url.Contains("id=other"));
        Assert.DoesNotContain(_helix.Requests, r => r.Method is "POST" or "DELETE");
        var extra = Assert.Single(service.Settings.Extras);
        Assert.Equal(("TF3: wybierz następną linię", 2500, true), (extra.Title, extra.Cost, extra.Switchable));
    }

    [Fact]
    public async Task Disables_own_TF3_rewards_when_the_bridge_closes()
    {
        _helix.AllRewards = """{"data":[{"id":"own","title":"TF3: pogoda","cost":100,"is_enabled":true}]}""";
        var service = Service();
        await service.TickAsync(default);
        _helix.Requests.Clear();

        await service.DisableAsync(default);

        Assert.Contains(_helix.Requests, r => r.Method == "PATCH" && r.Url.Contains("id=own") && r.Body == """{"is_enabled":false}""");
    }

    [Fact]
    public async Task Reports_TF3_rewards_twitch_does_not_let_it_switch()
    {
        _helix.AllRewards = """{"data":[{"id":"hand","title":"TF3: pogoda","cost":100,"is_enabled":true},{"id":"own","title":"TF3: muzyka","cost":50,"is_enabled":true}]}""";
        _helix.Forbidden.Add("hand");
        var service = Service();

        await service.TickAsync(default);
        _helix.Requests.Clear();
        _whyInactive = "most jest zatrzymany";
        await service.TickAsync(default);

        Assert.Equal(RewardState.Inactive, service.Status.State);
        Assert.Contains("Twitch nie pozwala Tf3Bridge włączać i wyłączać: TF3: pogoda", service.Status.Message);
        Assert.False(service.Settings.Extras.Single(e => e.TwitchId == "hand").Switchable);
        Assert.DoesNotContain(_helix.Requests, r => r.Url.Contains("id=hand")); // doesn't keep retrying
        Assert.Contains(_helix.Requests, r => r.Url.Contains("id=own") && r.Body == """{"is_enabled":false}""");
    }

    [Fact]
    public async Task Leaves_TF3_rewards_the_streamer_switched_off_disabled()
    {
        _helix.AllRewards = """{"data":[{"id":"own","title":"TF3: pogoda","cost":100,"is_enabled":true}]}""";
        var service = Service();
        await service.TickAsync(default);
        _helix.Requests.Clear();

        service.UpdateSettings(service.Settings with { Extras = [service.Settings.Extras.Single() with { Enabled = false }] });
        await service.TickAsync(default);
        _helix.Requests.Clear();
        _whyInactive = "most jest zatrzymany";
        await service.TickAsync(default);
        _whyInactive = null;
        await service.TickAsync(default);

        Assert.False(service.Settings.Extras.Single().Enabled);
        Assert.DoesNotContain(_helix.Requests, r => r.Url.Contains("id=own"));
    }

    [Fact]
    public async Task Looks_for_TF3_rewards_only_when_connecting()
    {
        var service = await SyncedService();
        _helix.AllRewards = """{"data":[{"id":"own","title":"TF3: pogoda","cost":100,"is_enabled":true}]}""";

        for (var i = 0; i < 3; i++) await service.TickAsync(default);

        Assert.Empty(_helix.Requests);
        Assert.Empty(service.Settings.Extras);
    }

    [Fact]
    public async Task Hand_made_reward_with_our_title_is_reported()
    {
        _helix.AllRewards = """{"data":[{"id":"hand","title":"TF3: kup miasto","cost":1,"is_enabled":true}]}""";
        var service = Service();

        await service.TickAsync(default);

        Assert.Equal(RewardKinds.Defaults.Count - 1, _helix.Requests.Count(r => r.Method == "POST"));
        Assert.False(service.Settings.TwitchIds.ContainsKey(RewardKind.AdoptTown));
        Assert.Contains("TF3: kup miasto", service.Status.Message);
        Assert.Empty(service.Settings.Extras);
    }

    private static string Message(Request chat) => (string)System.Text.Json.Nodes.JsonNode.Parse(chat.Body)!["message"]!;

    private sealed record Request(string Method, string Url, string Body);

    private sealed class FakeHelix : HttpMessageHandler
    {
        private int _next;

        public string Rewards { get; set; } = """{"data":[]}""";

        /// <summary>Listing without only_manageable_rewards (also hand-made rewards); null = same as <see cref="Rewards"/>.</summary>
        public string? AllRewards { get; set; }

        /// <summary>Reward IDs this client may not change (created by hand or by another app): PATCH gets 403.</summary>
        public HashSet<string> Forbidden { get; } = [];

        public ConcurrentQueue<Request> Requests { get; } = new();

        public async Task<Request> WaitFor(Func<Request, bool> match)
        {
            for (var i = 0; i < 300; i++)
            {
                if (Requests.FirstOrDefault(match) is { } r) return r;
                await Task.Delay(10);
            }
            throw new Xunit.Sdk.XunitException("Expected request was not sent");
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var url = request.RequestUri!.ToString();
            Requests.Enqueue(new Request(request.Method.Method, url, body));
            if (request.Method == HttpMethod.Patch && Forbidden.Any(id => url.EndsWith("&id=" + id)))
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("""{"error":"Forbidden","status":403,"message":"The ID in the Client-Id header must match the client ID used to create the custom reward."}""",
                        Encoding.UTF8, "application/json"),
                };
            var json = request.Method == HttpMethod.Get && url.Contains("custom_rewards?") && !url.Contains("only_manageable") ? AllRewards ?? Rewards
                : request.Method == HttpMethod.Get && url.Contains("custom_rewards?") ? Rewards
                : request.Method == HttpMethod.Get ? """{"data":[]}"""
                : request.Method == HttpMethod.Post ? $$"""{"data":[{"id":"rw{{Interlocked.Increment(ref _next)}}"}]}"""
                : """{"data":[]}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
