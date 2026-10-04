using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Tf3Bridge.Commands;
using Tf3Bridge.Processing;
using Tf3Bridge.Twitch;

namespace Tf3Bridge.Rewards;

public enum RewardState { Off, WaitingForLogin, NotAffiliate, Ok, Paused, Error }

public sealed record RewardStatus(RewardState State, string Message);

/// <summary>How a redemption ended. <see cref="Refunded"/>: the points went back to the viewer.</summary>
public sealed record RewardOutcome(string Token, RewardKind Kind, string User, string Input, bool Ok, string Message)
{
    public bool Refunded => !Ok;
}

/// <summary>
/// Channel points rewards: keeps them in sync on Twitch (create, update, pause while the game is not
/// exporting), turns redemptions into mod commands and closes them: FULFILLED when the game confirms,
/// CANCELED (points refunded) when the target is not found, already taken or the game fails.
/// The only path from viewers to game changes (see CommandSource).
/// </summary>
public sealed class RewardService
{
    /// <summary>Redemptions waiting longer than this (e.g. while Tf3Bridge was off) are refunded.</summary>
    internal static readonly TimeSpan MaxRedemptionAge = TimeSpan.FromMinutes(5);
    /// <summary>No export for this long = game not running: rewards are paused on Twitch.</summary>
    internal static readonly TimeSpan GameIdleAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LoopEvery = TimeSpan.FromSeconds(15);

    private readonly TwitchSession _session;
    private readonly ChannelPointsApi _api;
    private readonly ModCommandQueue _queue;
    private readonly bool _mock;
    private readonly string _dynamicPath;
    private readonly string _metaPath;
    private readonly string _settingsPath;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Pending> _pending = []; // id komendy -> wykupienie
    private RewardSettings _settings;
    private volatile RewardStatus _status = new(RewardState.Off, "wyłączone");
    private string? _syncedFor;      // konto, dla którego nagrody są zsynchronizowane
    private bool? _paused;           // ostatnio ustawiona pauza na Twitchu
    private readonly SemaphoreSlim _wake = new(0);

    public event Action<RewardOutcome>? Finished;

    public RewardService(TwitchSession session, EventSubClient eventSub, ModCommandQueue queue,
        IOptions<BridgeOptions> options, ILogger<RewardService> log)
        : this(session, new ChannelPointsApi(session, new HttpClient { Timeout = TimeSpan.FromSeconds(20) }), queue,
            eventSub.IsMock, options.Value.OutputDir, RewardSettingsStore.FilePath, log)
    {
        eventSub.RedemptionReceived += r => _ = HandleRedemptionAsync(r);
    }

    internal RewardService(TwitchSession session, ChannelPointsApi api, ModCommandQueue queue, bool mock,
        string outputDir, string settingsPath, ILogger log)
    {
        _session = session;
        _api = api;
        _queue = queue;
        _mock = mock;
        _dynamicPath = Path.Combine(outputDir, ExportProcessor.DynamicPath.Replace('/', Path.DirectorySeparatorChar));
        _metaPath = Path.Combine(outputDir, ExportProcessor.MetaPath.Replace('/', Path.DirectorySeparatorChar));
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

    internal async Task TickAsync(CancellationToken ct)
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
            var paused = !GameRunning();
            if (_syncedFor != userId)
            {
                await SyncAsync(userId, paused, ct);
                _syncedFor = userId;
                _paused = paused;
                await ProcessBacklogAsync(ct);
            }
            else if (_paused != paused)
            {
                await SetPausedAsync(paused, ct);
                _paused = paused;
                _log.LogInformation("Rewards {State}", paused ? "paused (no game data for 2 min)" : "resumed");
            }
            var enabled = Settings.Rewards.Count(r => r.Enabled);
            _status = paused
                ? new(RewardState.Paused, $"wstrzymane na Twitchu, bo gra nie wysyła danych ({enabled} nagród)")
                : new(RewardState.Ok, $"aktywne: {enabled} z {Settings.Rewards.Count}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _status = new(RewardState.Error, ex.Message);
            _log.LogWarning("Rewards: {Message}", ex.Message);
        }
    }

    /// <summary>Same path as a real redemption, without Twitch (test from the bridge window).</summary>
    public RewardOutcome? Simulate(RewardKind kind, string input, string user, out string token)
    {
        token = "sim-" + Guid.NewGuid().ToString("N")[..8];
        return Process(kind, input, user, DateTimeOffset.UtcNow, redemption: null, token);
    }

    // ------------------------------------------------------------------ wykupienia

    private sealed record Redemption(string RewardId, string Id);

    private sealed record Pending(string Token, RewardKind Kind, string User, string Input, string Target, Redemption? Redemption);

    private async Task HandleRedemptionAsync(ChannelPointsRedemption r)
    {
        var kind = KindOf(r.RewardId);
        if (kind is null) return; // nie nasza nagroda
        var outcome = Process(kind.Value, r.UserInput, r.UserName.Length > 0 ? r.UserName : r.UserLogin,
            r.RedeemedAt, new Redemption(r.RewardId, r.Id), r.Id);
        if (outcome is not null) await CloseAsync(new Redemption(r.RewardId, r.Id), outcome);
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
    private RewardOutcome? Process(RewardKind kind, string input, string user, DateTimeOffset redeemedAt, Redemption? redemption, string token)
    {
        var config = Settings.Get(kind);
        RewardOutcome Reject(string why)
        {
            var o = new RewardOutcome(token, kind, user, input, false, why);
            _log.LogWarning("Reward \"{Title}\" by {User} ({Input}): refused, {Why}", config.Title, user, input, why);
            Finished?.Invoke(o);
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
            ? ModCommand.Effect(effect, target.Entity, config.Days, RewardKinds.HasFactor(kind) ? config.Factor : null,
                CommandSource.ChannelPoints, owner, created)
            : ModCommand.Adopt(target.Entity, owner, CommandSource.ChannelPoints, owner, created);
        lock (_gate) _pending[command.Id] = new Pending(token, kind, owner, input, target.Name, redemption);
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
        var message = result.Ok ? $"wykonano: {p.Target}" : $"{p.Target}: {Explain(result)}";
        var outcome = new RewardOutcome(p.Token, p.Kind, p.User, p.Input, result.Ok, message);
        if (result.Ok) _log.LogInformation("Reward {Kind} by {User}: {Message}", p.Kind, p.User, message);
        else _log.LogWarning("Reward {Kind} by {User}: {Message}, refunding", p.Kind, p.User, message);
        Finished?.Invoke(outcome);
        if (p.Redemption is { } r) _ = CloseAsync(r, outcome);
    }

    private static string Explain(ModCommandResult r) => r.Status switch
    {
        "expired" => "gra nie wykonała komendy na czas",
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
                if (DateTimeOffset.UtcNow - r.RedeemedAt > MaxRedemptionAge)
                {
                    _log.LogWarning("Refunding old redemption of {Kind} by {User} from {At:g}", kind, r.UserName, r.RedeemedAt.ToLocalTime());
                    await CloseAsync(new Redemption(rewardId, r.Id), new RewardOutcome(r.Id, kind, r.UserName, r.UserInput, false, "za stare"));
                    continue;
                }
                await HandleRedemptionAsync(new ChannelPointsRedemption(r.Id, rewardId, "", 0, "", r.UserLogin, r.UserName, r.UserInput, r.RedeemedAt));
            }
        }
    }

    // ------------------------------------------------------------------ nagrody na Twitchu

    private async Task SyncAsync(string userId, bool paused, CancellationToken ct)
    {
        var s = Settings;
        var ids = s.ChannelUserId == userId ? new Dictionary<RewardKind, string>(s.TwitchIds) : [];
        var existing = await _api.GetManageableRewardsAsync(ct);
        foreach (var config in s.Rewards)
        {
            var id = ids.TryGetValue(config.Kind, out var known) && existing.Any(e => e.Id == known)
                ? known
                : existing.FirstOrDefault(e => e.Title == config.Title)?.Id; // np. po utracie rewards.json
            var body = Body(config, paused);
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

    private async Task SetPausedAsync(bool paused, CancellationToken ct)
    {
        foreach (var config in Settings.Rewards.Where(r => r.Enabled))
        {
            if (Settings.TwitchIds.TryGetValue(config.Kind, out var id))
                await _api.UpdateRewardAsync(id, new JsonObject { ["is_paused"] = paused }, ct);
        }
    }

    internal static JsonObject Body(RewardConfig c, bool paused)
    {
        var body = new JsonObject
        {
            ["title"] = c.Title,
            ["prompt"] = c.TwitchPrompt,
            ["cost"] = c.Cost,
            ["is_enabled"] = c.Enabled,
            ["is_paused"] = paused,
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

    private bool GameRunning() =>
        File.Exists(_metaPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(_metaPath) < GameIdleAfter;

    private void SaveLocked()
    {
        try { RewardSettingsStore.Save(_settings, _settingsPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Saving reward settings failed: {Message}", ex.Message);
        }
    }
}
