using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Tf3Bridge.Commands;
using Tf3Bridge.Processing;
using Tf3Bridge.Twitch;

namespace Tf3Bridge.Rewards;

public enum RewardState { Off, WaitingForLogin, NotAffiliate, Ok, Inactive, Error }

public sealed record RewardStatus(RewardState State, string Message);

/// <summary>
/// How a redemption ended. <see cref="Refunded"/>: the points went back to the viewer.
/// <see cref="ChatText"/>: the answer for the viewer in chat (sent only for real redemptions,
/// or for a simulation when asked to).
/// </summary>
public sealed record RewardOutcome(string Token, RewardKind Kind, string User, string Input, bool Ok, string Message,
    string? Target = null, GameDate? Until = null, string ChatText = "")
{
    public bool Refunded => !Ok;
}

/// <summary>
/// Channel points rewards: keeps them in sync on Twitch (create, update, enabled only while the bridge runs
/// and a loaded save exports data, disabled when Tf3Bridge closes), turns redemptions into mod commands and closes them: FULFILLED when the game confirms,
/// CANCELED (points refunded) when the target is not found, already taken or the game fails.
/// The only path from viewers to game changes (see CommandSource).
/// </summary>
public sealed class RewardService
{
    /// <summary>Redemptions waiting longer than this (e.g. while Tf3Bridge was off) are refunded.</summary>
    internal static readonly TimeSpan MaxRedemptionAge = TimeSpan.FromMinutes(5);
    /// <summary>No export for this long = game not running: rewards are disabled on Twitch.</summary>
    internal static readonly TimeSpan GameIdleAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LoopEvery = TimeSpan.FromSeconds(15);

    private readonly TwitchSession _session;
    private readonly ChannelPointsApi _api;
    private readonly ChatApi _chat;
    private readonly ModCommandQueue _queue;
    private readonly bool _mock;
    private readonly string _dynamicPath;
    private readonly Func<string?> _whyInactive; // null = gra działa i wysyła dane
    private readonly string _settingsPath;
    private readonly string _siteUrl;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Pending> _pending = []; // id komendy -> wykupienie
    private RewardSettings _settings;
    private volatile RewardStatus _status = new(RewardState.Off, "wyłączone");
    private string? _syncedFor;      // konto, dla którego nagrody są zsynchronizowane
    private bool? _active;           // ostatnio ustawione włączenie na Twitchu
    private readonly SemaphoreSlim _wake = new(0);
    private readonly SemaphoreSlim _tick = new(1, 1); // pętla i wyłączanie przy zamykaniu

    public event Action<RewardOutcome>? Finished;

    public RewardService(TwitchSession session, EventSubClient eventSub, ModCommandQueue queue, BridgeRunner runner, ExportProcessor processor,
        IOptions<BridgeOptions> options, IOptions<TwitchOptions> twitch, ILogger<RewardService> log)
        : this(session, new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, eventSub.IsMock, queue, options.Value.OutputDir, twitch.Value.SiteUrl,
            () => WhyInactive(runner.IsRunning, processor.LastDynamicUtc, DateTime.UtcNow), log)
    {
        eventSub.RedemptionReceived += r => _ = HandleRedemptionAsync(r);
        runner.StateChanged += () => _wake.Release(); // Zatrzymaj / Uruchom: nagrody od razu, nie po 15 s
    }

    // Jeden HttpClient dla API nagród i czatu.
    private RewardService(TwitchSession session, HttpClient http, bool mock, ModCommandQueue queue, string outputDir, string siteUrl,
        Func<string?> whyInactive, ILogger log)
        : this(session, new ChannelPointsApi(session, http), new ChatApi(session, http), queue, mock, outputDir, RewardSettingsStore.FilePath,
            whyInactive, log, siteUrl) { }

    internal RewardService(TwitchSession session, ChannelPointsApi api, ChatApi chat, ModCommandQueue queue, bool mock,
        string outputDir, string settingsPath, Func<string?> whyInactive, ILogger log, string siteUrl = "")
    {
        _whyInactive = whyInactive;
        _siteUrl = siteUrl;
        _session = session;
        _api = api;
        _chat = chat;
        _queue = queue;
        _mock = mock;
        _dynamicPath = Path.Combine(outputDir, ExportProcessor.DynamicPath.Replace('/', Path.DirectorySeparatorChar));
        _settingsPath = settingsPath;
        _log = log;
        _settings = RewardSettingsStore.Load(settingsPath);
        queue.Completed += OnCommandCompleted;
    }

    public RewardStatus Status => _status;

    public RewardSettings Settings { get { lock (_gate) return _settings; } }

    /// <summary>Saves new settings; the next loop pushes them to Twitch.</summary>
    public void UpdateSettings(RewardSettings settings)
    {
        lock (_gate)
        {
            _settings = (settings with { ChannelUserId = _settings.ChannelUserId, TwitchIds = _settings.TwitchIds }).Normalized();
            SaveLocked();
            _syncedFor = null;
        }
        _wake.Release();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!_session.IsConfigured) return;
        try
        {
            while (true)
            {
                await TickAsync(ct);
                await _wake.WaitAsync(LoopEvery, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>Null when rewards may be on: the bridge runs and this run got game data in the last <see cref="GameIdleAfter"/>.</summary>
    internal static string? WhyInactive(bool bridgeRunning, DateTime? lastDynamicUtc, DateTime nowUtc) =>
        !bridgeRunning ? "most jest zatrzymany"
        : lastDynamicUtc is not { } last ? "brak danych z gry (wczytaj zapis)"
        : nowUtc - last >= GameIdleAfter ? "gra nie wysyła danych od 2 min"
        : null;

    internal async Task TickAsync(CancellationToken ct)
    {
        await _tick.WaitAsync(ct);
        try { await TickLockedAsync(ct); }
        finally { _tick.Release(); }
    }

    /// <summary>
    /// Disables the rewards on Twitch before Tf3Bridge closes, so viewers cannot buy anything while nobody handles it.
    /// </summary>
    public async Task DisableAsync(CancellationToken ct)
    {
        try
        {
            await _tick.WaitAsync(ct);
        }
        catch (OperationCanceledException) { return; }
        try
        {
            if (_mock || _syncedFor is null || _active == false) return;
            await SetActiveAsync(false, ct);
            _active = false;
            _log.LogInformation("Rewards disabled on Twitch (closing)");
        }
        catch (Exception ex) when (ex is TwitchAuthException or HttpRequestException or OperationCanceledException)
        {
            _log.LogWarning("Could not disable rewards on Twitch: {Message}", ex.Message);
        }
        finally { _tick.Release(); }
    }

    private async Task TickLockedAsync(CancellationToken ct)
    {
        if (_mock)
        {
            _status = new(RewardState.Ok, "tryb testowy (Twitch CLI): bez zmian na Twitchu");
            return;
        }
        if (_session.UserId is not { } userId)
        {
            _status = new(RewardState.WaitingForLogin, "czeka na zalogowanie");
            _syncedFor = null;
            return;
        }
        if (!_session.CanUseChannelPoints)
        {
            _status = new(RewardState.NotAffiliate, "kanał bez Affiliate/Partner: nagrody wyłączone (zostaje „Symuluj nagrodę”)");
            return;
        }
        try
        {
            var why = _whyInactive();
            var active = why is null;
            if (_syncedFor != userId)
            {
                await SyncAsync(userId, active, ct);
                _syncedFor = userId;
                _active = active;
                await ProcessBacklogAsync(ct);
            }
            else if (_active != active)
            {
                await SetActiveAsync(active, ct);
                _active = active;
                _log.LogInformation("Rewards {State}", active ? "enabled on Twitch (game data arrives)" : $"disabled on Twitch: {why}");
            }
            var enabled = Settings.Rewards.Count(r => r.Enabled);
            _status = active
                ? new(RewardState.Ok, $"aktywne: {enabled} z {Settings.Rewards.Count}")
                : new(RewardState.Inactive, $"wyłączone na Twitchu, bo {why} ({enabled} nagród)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _status = new(RewardState.Error, ex.Message);
            _log.LogWarning("Rewards: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Same path as a real redemption, without Twitch points (test from the bridge window).
    /// <paramref name="chat"/>: also post the answer in chat (to test chat messages without Affiliate).
    /// </summary>
    public RewardOutcome? Simulate(RewardKind kind, string input, string user, bool chat, out string token)
    {
        token = "sim-" + Guid.NewGuid().ToString("N")[..8];
        return Process(kind, input, user, DateTimeOffset.UtcNow, new Origin(null, user, chat), token);
    }

    // ------------------------------------------------------------------ wykupienia

    private sealed record Redemption(string RewardId, string Id);

    /// <summary>Where a redemption came from: Twitch (with <see cref="Redemption"/>) or a simulation.</summary>
    private sealed record Origin(Redemption? Redemption, string Login, bool Chat);

    private sealed record Pending(string Token, RewardKind Kind, string User, string Input, string Target, long Entity, Origin Origin);

    private Task HandleRedemptionAsync(ChannelPointsRedemption r)
    {
        var kind = KindOf(r.RewardId);
        if (kind is null) return Task.CompletedTask; // nie nasza nagroda
        // Kupione tuż przed wyłączeniem nagród albo czekające od poprzedniego uruchomienia: gra i tak by tego nie wykonała.
        if (!_mock && _whyInactive() is { } why)
        {
            Refuse(r, kind.Value, "gra nie działa: " + why);
            return Task.CompletedTask;
        }
        Process(kind.Value, r.UserInput, r.UserName.Length > 0 ? r.UserName : r.UserLogin,
            r.RedeemedAt, new Origin(new Redemption(r.RewardId, r.Id), r.UserLogin.Length > 0 ? r.UserLogin : r.UserName, Chat: true), r.Id);
        return Task.CompletedTask;
    }

    internal Task HandleRedemptionForTestAsync(ChannelPointsRedemption r) => HandleRedemptionAsync(r);

    private RewardKind? KindOf(string rewardId)
    {
        var s = Settings;
        foreach (var (kind, id) in s.TwitchIds) if (id == rewardId) return kind;
        // Twitch CLI: "twitch event trigger ... -i AdoptTown" wskazuje nagrodę po nazwie rodzaju
        return _mock && Enum.TryParse<RewardKind>(rewardId, ignoreCase: true, out var k) ? k : null;
    }

    /// <summary>Null = sent to the game (the result comes through <see cref="Finished"/>); otherwise rejected right away.</summary>
    private RewardOutcome? Process(RewardKind kind, string input, string user, DateTimeOffset redeemedAt, Origin origin, string token)
    {
        var config = Settings.Get(kind);
        RewardOutcome Reject(string why)
        {
            var o = new RewardOutcome(token, kind, user, input, false, why,
                ChatText: RewardMessages.Text(config, origin.Login, null, ok: false, until: null, reason: why));
            _log.LogWarning("Reward \"{Title}\" by {User} ({Input}): refused, {Why}", config.Title, user, input, why);
            Complete(origin, o);
            return o;
        }

        if (!config.Enabled) return Reject("nagroda jest wyłączona");
        if (!_queue.IsAvailable) return Reject("brak folderu gry");
        var owner = OwnerName.Clean(user).Replace("|", "");
        if (owner.Length == 0) return Reject("pusta nazwa widza");

        var resolved = TargetResolver.Resolve(GameObjects.Load(_dynamicPath), RewardKinds.Target(kind), input);
        if (resolved.Target is not { } target) return Reject(resolved.Error!);
        if (kind == RewardKind.AdoptTown && target.Owner is { } current) return Reject($"{target.Name} ma już właściciela ({current})");

        var created = redeemedAt > DateTimeOffset.UtcNow ? DateTimeOffset.UtcNow : redeemedAt;
        var command = RewardKinds.Effect(kind) is { } effect
            ? ModCommand.Effect(effect, target.Entity, config.Months, RewardKinds.HasFactor(kind) ? config.Factor : null,
                CommandSource.ChannelPoints, owner, created)
            : ModCommand.Adopt(target.Entity, owner, CommandSource.ChannelPoints, owner, created);
        lock (_gate) _pending[command.Id] = new Pending(token, kind, owner, input, target.Name, target.Entity, origin);
        _queue.Enqueue(command);
        _log.LogInformation("Reward \"{Title}\" by {User}: {Target}", config.Title, owner, target.Name);
        return null;
    }

    private void OnCommandCompleted(ModCommand command, ModCommandResult result)
    {
        Pending? p;
        lock (_gate)
        {
            if (!_pending.Remove(command.Id, out p)) return;
        }
        var reason = result.Ok ? null : $"{p.Target}: {Explain(result)}";
        var message = result.Ok
            ? $"wykonano: {p.Target}{(result.Until is { } u ? $", do {RewardMessages.Date(u)}" : "")}"
            : reason!;
        var chat = RewardMessages.Text(Settings.Get(p.Kind), p.Origin.Login, p.Target, result.Ok, result.Until, reason,
            RewardMessages.Link(_siteUrl, p.Kind, p.Entity));
        var outcome = new RewardOutcome(p.Token, p.Kind, p.User, p.Input, result.Ok, message, p.Target, result.Until, chat);
        if (result.Ok) _log.LogInformation("Reward {Kind} by {User}: {Message}", p.Kind, p.User, message);
        else _log.LogWarning("Reward {Kind} by {User}: {Message}, refunding", p.Kind, p.User, message);
        Complete(p.Origin, outcome);
    }

    // Koniec wykupienia: zdarzenie dla okna, rozliczenie na Twitchu i odpowiedź na czacie.
    private void Complete(Origin origin, RewardOutcome outcome)
    {
        Finished?.Invoke(outcome);
        if (origin.Redemption is { } r) _ = CloseAsync(r, outcome);
        if (origin.Chat && Settings.ChatMessages) _ = SayAsync(outcome.ChatText);
    }

    private async Task SayAsync(string text)
    {
        if (_mock || text.Length == 0) return; // serwer testowy nie ma czatu
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _chat.SendAsync(text, cts.Token);
            _log.LogInformation("Chat: {Text}", text);
        }
        catch (Exception ex) when (ex is TwitchAuthException or HttpRequestException or OperationCanceledException)
        {
            _log.LogWarning("Chat message not sent: {Message}", ex.Message);
        }
    }

    private static string Explain(ModCommandResult r) => r.Status switch
    {
        "expired" => "gra nie wykonała komendy na czas",
        // Gra trzyma skrypty wczytane przy starcie: nowy plik moda działa dopiero po restarcie TF3.
        _ when r.Error?.StartsWith("unknown kind ", StringComparison.Ordinal) == true => "gra ma starszą wersję moda (uruchom TF3 ponownie)",
        _ when r.Error?.StartsWith("already adopted by ", StringComparison.Ordinal) == true => "ma już właściciela (" + r.Error["already adopted by ".Length..] + ")",
        _ => r.Error ?? r.Status,
    };

    private async Task CloseAsync(Redemption r, RewardOutcome outcome)
    {
        if (_mock) return; // serwer testowy nie ma prawdziwych wykupień
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _api.SetRedemptionStatusAsync(r.RewardId, r.Id, outcome.Ok, cts.Token);
        }
        catch (Exception ex) when (ex is TwitchAuthException or HttpRequestException or OperationCanceledException)
        {
            // Zostaje w kolejce wykupień na Twitchu; streamer może je rozliczyć ręcznie w panelu.
            _log.LogWarning("Could not mark redemption {Id} as {Status}: {Message}", r.Id, outcome.Ok ? "fulfilled" : "canceled", ex.Message);
        }
    }

    // Wykupienia sprzed startu Tf3Bridge: świeże wykonujemy, stare zwracamy.
    private async Task ProcessBacklogAsync(CancellationToken ct)
    {
        foreach (var (kind, rewardId) in Settings.TwitchIds)
        {
            foreach (var r in await _api.GetUnfulfilledAsync(rewardId, ct))
            {
                var redemption = new ChannelPointsRedemption(r.Id, rewardId, "", 0, "", r.UserLogin, r.UserName, r.UserInput, r.RedeemedAt);
                if (DateTimeOffset.UtcNow - r.RedeemedAt > MaxRedemptionAge)
                {
                    _log.LogWarning("Refunding old redemption of {Kind} by {User} from {At:g}", kind, r.UserName, r.RedeemedAt.ToLocalTime());
                    Refuse(redemption, kind, "gra nie działała, gdy nagroda została kupiona");
                    continue;
                }
                await HandleRedemptionAsync(redemption);
            }
        }
    }

    // Zwrot punktów bez wysyłania do gry.
    private void Refuse(ChannelPointsRedemption r, RewardKind kind, string why)
    {
        var login = r.UserLogin.Length > 0 ? r.UserLogin : r.UserName;
        _log.LogWarning("Reward {Kind} by {User} ({Input}): refunding, {Why}", kind, r.UserName, r.UserInput, why);
        Complete(new Origin(new Redemption(r.RewardId, r.Id), login, Chat: true),
            new RewardOutcome(r.Id, kind, r.UserName.Length > 0 ? r.UserName : login, r.UserInput, false, why,
                ChatText: RewardMessages.Text(Settings.Get(kind), login, null, ok: false, until: null, reason: why)));
    }

    // ------------------------------------------------------------------ nagrody na Twitchu

    private async Task SyncAsync(string userId, bool active, CancellationToken ct)
    {
        var s = Settings;
        var ids = s.ChannelUserId == userId ? new Dictionary<RewardKind, string>(s.TwitchIds) : [];
        var existing = await _api.GetManageableRewardsAsync(ct);
        foreach (var config in s.Rewards)
        {
            var id = ids.TryGetValue(config.Kind, out var known) && existing.Any(e => e.Id == known)
                ? known
                : existing.FirstOrDefault(e => e.Title == config.Title)?.Id; // np. po utracie rewards.json
            var body = Body(config, active);
            if (id is null)
            {
                if (!config.Enabled) { ids.Remove(config.Kind); continue; }
                id = await _api.CreateRewardAsync(body, ct);
                _log.LogInformation("Reward created on Twitch: {Title}", config.Title);
            }
            else await _api.UpdateRewardAsync(id, body, ct);
            ids[config.Kind] = id;
        }
        lock (_gate)
        {
            _settings = _settings with { ChannelUserId = userId, TwitchIds = ids };
            SaveLocked();
        }
        _log.LogInformation("Rewards synced with Twitch ({Count} enabled)", s.Rewards.Count(r => r.Enabled));
    }

    private async Task SetActiveAsync(bool active, CancellationToken ct)
    {
        foreach (var config in Settings.Rewards.Where(r => r.Enabled))
        {
            if (Settings.TwitchIds.TryGetValue(config.Kind, out var id))
                await _api.UpdateRewardAsync(id, new JsonObject { ["is_enabled"] = active }, ct);
        }
    }

    /// <param name="active">false = hidden from viewers (is_enabled), so nobody buys while the game cannot handle it.</param>
    internal static JsonObject Body(RewardConfig c, bool active)
    {
        var body = new JsonObject
        {
            ["title"] = c.Title,
            ["prompt"] = c.TwitchPrompt,
            ["cost"] = c.Cost,
            ["is_enabled"] = c.Enabled && active,
            ["is_paused"] = false, // starsze wersje pauzowały zamiast wyłączać
            ["is_user_input_required"] = true,
            ["should_redemptions_skip_request_queue"] = false,
            ["is_global_cooldown_enabled"] = c.CooldownSec > 0,
            ["is_max_per_stream_enabled"] = c.MaxPerStream > 0,
            ["is_max_per_user_per_stream_enabled"] = c.MaxPerUserPerStream > 0,
        };
        if (c.CooldownSec > 0) body["global_cooldown_seconds"] = c.CooldownSec;
        if (c.MaxPerStream > 0) body["max_per_stream"] = c.MaxPerStream;
        if (c.MaxPerUserPerStream > 0) body["max_per_user_per_stream"] = c.MaxPerUserPerStream;
        return body;
    }

    private void SaveLocked()
    {
        try { RewardSettingsStore.Save(_settings, _settingsPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Saving reward settings failed: {Message}", ex.Message);
        }
    }
}
