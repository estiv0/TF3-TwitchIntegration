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
            {"towns":[{"id":10,"name":"Łódź"},{"id":11,"name":"Kraków | ala"},{"id":12,"name":"Nowy Sącz"},{"id":13,"name":"Nowy Targ"}],
             "industries":[{"id":20,"name":"Kopalnia węgla Bytom"}],
             "lines":[{"id":30,"name":"Linia 1"}]}
            """);
        File.WriteAllText(Path.Combine(latest, "meta.json"), "{}");
        _queue = new ModCommandQueue(Path.Combine(_dir, "cmd"), NullLogger.Instance, TimeProvider.System);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private RewardService Service(string broadcasterType = "affiliate", bool mock = false)
    {
        var store = Path.Combine(_dir, "twitch.json");
        TwitchAccountStore.Save(new TwitchAccount("42", "streamer", broadcasterType, TwitchAuthClient.Scopes, "ACCESS", "REFRESH", DateTimeOffset.UtcNow.AddHours(3)), store);
        var session = new TwitchSession(new TwitchAuthClient(new HttpClient(), "cid"), store, NullLogger.Instance);
        var service = new RewardService(session, new ChannelPointsApi(session, new HttpClient(_helix)), _queue, mock,
            Path.Combine(_dir, "out"), Path.Combine(_dir, "rewards.json"), NullLogger.Instance);
        service.Finished += _outcomes.Enqueue;
        return service;
    }

    private string QueueText => File.ReadAllText(Path.Combine(_dir, "cmd", "queue.lua"));

    private void ModReports(string status, string? error = null)
    {
        var id = System.Text.RegularExpressions.Regex.Match(QueueText, "id = \"(\\w+)\"").Groups[1].Value;
        var json = $$"""{"results":[{"id":"{{id}}","status":"{{status}}"{{(error is null ? "" : $",\"error\":\"{error}\"")}}}]}""";
        var path = Path.Combine(_dir, "cmd", "results.lua");
        File.WriteAllText(path, $"function data()\r\nreturn {{\r\n\t\tjson = \"{json.Replace("\"", "\\\"")}\",\r\n\t}}\r\nend");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 1000)));
        _queue.Poll();
    }

    // ------------------------------------------------------------------ dopasowanie celu

    [Theory]
    [InlineData("lodz", 10)]
    [InlineData("  ŁÓDŹ ", 10)]
    [InlineData("krakow", 11)]          // właściciel w nazwie nie przeszkadza
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
    [InlineData("bytom", "nie znaleziono „bytom”")] // zakład nie jest miastem
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
            Rewards = [RewardKinds.Defaults[1] with { Cost = 1234, Months = 999, Factor = 9 }],
            ChannelUserId = "42",
            TwitchIds = new Dictionary<RewardKind, string> { [RewardKind.BoostTown] = "rw1" },
        };

        RewardSettingsStore.Save(custom.Normalized(), path);
        var loaded = RewardSettingsStore.Load(path);

        Assert.Equal(Enum.GetValues<RewardKind>(), loaded.Rewards.Select(r => r.Kind));
        var boost = loaded.Get(RewardKind.BoostTown);
        Assert.Equal((1234, 60, 5.0), (boost.Cost, boost.Months, boost.Factor)); // przycięte do zakresów
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
    public void Prompt_shows_days_and_percent()
    {
        var c = RewardKinds.Defaults[2] with { Prompt = "+{percent}% na {months} mies.", Months = 7, Factor = 1.25 };

        Assert.Equal("+25% na 7 mies.", c.TwitchPrompt);
    }

    [Fact]
    public void Twitch_body_requires_input_and_keeps_redemptions_in_the_queue()
    {
        var body = RewardService.Body(RewardKinds.Defaults[0] with { CooldownSec = 0, MaxPerUserPerStream = 2 }, paused: true);

        Assert.True((bool)body["is_user_input_required"]!);
        Assert.False((bool)body["should_redemptions_skip_request_queue"]!); // inaczej nie da się zwrócić punktów
        Assert.True((bool)body["is_paused"]!);
        Assert.False((bool)body["is_global_cooldown_enabled"]!);
        Assert.False(body.ContainsKey("global_cooldown_seconds"));
        Assert.Equal(2, (int)body["max_per_user_per_stream"]!);
    }

    // ------------------------------------------------------------------ wykupienia (symulacja)

    [Fact]
    public void Adoption_sends_an_adopt_command_from_channel_points()
    {
        var service = Service();

        Assert.Null(service.Simulate(RewardKind.AdoptTown, "lodz", "Widz|X", out _));

        Assert.Contains("kind = \"adopt\"", QueueText);
        Assert.Contains("entity = 10", QueueText);
        Assert.Contains("owner = \"WidzX\"", QueueText);
    }

    [Fact]
    public void Adoption_of_a_town_with_an_owner_is_refunded_right_away()
    {
        var service = Service();

        var outcome = service.Simulate(RewardKind.AdoptTown, "kraków", "widz", out _);

        Assert.True(outcome!.Refunded);
        Assert.Contains("ma już właściciela (ala)", outcome.Message);
        Assert.Equal(0, _queue.PendingCount);
    }

    [Theory]
    [InlineData(RewardKind.BoostTown, "nowy sacz", "effect = \"townBoost\", entity = 12, months = 12, factor = 1.5")]
    [InlineData(RewardKind.BoostIndustry, "kopalnia", "effect = \"industryBoost\", entity = 20, months = 6, factor = 1.5")]
    [InlineData(RewardKind.StopLine, "linia 1", "effect = \"lineStop\", entity = 30, months = 1 }")]
    [InlineData(RewardKind.HoldLine, "linia 1", "effect = \"lineHold\", entity = 30, months = 1 }")]
    public void Effects_carry_months_and_factor_from_settings(RewardKind kind, string input, string expected)
    {
        var service = Service();

        Assert.Null(service.Simulate(kind, input, "widz", out _));

        Assert.Contains(expected, QueueText);
    }

    [Fact]
    public void Disabled_reward_is_refunded()
    {
        var service = Service();
        service.UpdateSettings(service.Settings with { Rewards = [.. service.Settings.Rewards.Select(r => r with { Enabled = r.Kind != RewardKind.StopLine })] });

        var outcome = service.Simulate(RewardKind.StopLine, "linia 1", "widz", out _);

        Assert.True(outcome!.Refunded);
    }

    [Fact]
    public void Result_from_the_game_finishes_the_simulation()
    {
        var service = Service();
        service.Simulate(RewardKind.BoostTown, "lodz", "widz", out var token);

        ModReports("ok");

        var o = Assert.Single(_outcomes);
        Assert.Equal(token, o.Token);
        Assert.True(o.Ok);
        Assert.Empty(_helix.Requests); // symulacja nie dotyka Twitcha
    }

    [Fact]
    public void Old_mod_in_the_game_is_explained()
    {
        var service = Service();
        service.Simulate(RewardKind.HoldLine, "linia 1", "widz", out _);

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
        Assert.Equal(5, creates.Count);
        Assert.All(creates, r => Assert.Contains("broadcaster_id=42", r.Url));
        Assert.Equal(5, service.Settings.TwitchIds.Count);
        Assert.Equal("42", RewardSettingsStore.Load(Path.Combine(_dir, "rewards.json")).ChannelUserId);
    }

    [Fact]
    public async Task Sync_reuses_existing_rewards()
    {
        _helix.Rewards = """{"data":[{"id":"old","title":"TF3: adoptuj miasto","is_enabled":true,"is_paused":false}]}""";
        var service = Service();

        await service.TickAsync(default);

        Assert.Equal("old", service.Settings.TwitchIds[RewardKind.AdoptTown]);
        Assert.Contains(_helix.Requests, r => r.Method == "PATCH" && r.Url.Contains("id=old"));
        Assert.Equal(4, _helix.Requests.Count(r => r.Method == "POST"));
    }

    [Fact]
    public async Task Pauses_rewards_when_the_game_stops_exporting()
    {
        _helix.Rewards = """{"data":[]}""";
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "out", "latest", "meta.json"), DateTime.UtcNow.AddMinutes(-10));
        var service = Service();

        await service.TickAsync(default);

        Assert.Equal(RewardState.Paused, service.Status.State);
        Assert.All(_helix.Requests.Where(r => r.Method == "POST"), r => Assert.Contains("\"is_paused\":true", r.Body));
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

    private sealed record Request(string Method, string Url, string Body);

    private sealed class FakeHelix : HttpMessageHandler
    {
        private int _next;

        public string Rewards { get; set; } = """{"data":[]}""";

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
            var json = request.Method == HttpMethod.Get && url.Contains("custom_rewards?") ? Rewards
                : request.Method == HttpMethod.Get ? """{"data":[]}"""
                : request.Method == HttpMethod.Post ? $$"""{"data":[{"id":"rw{{Interlocked.Increment(ref _next)}}"}]}"""
                : """{"data":[]}""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
