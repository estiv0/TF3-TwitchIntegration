using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Tf3Bridge.Commands;
using Tf3Bridge.Processing;
using Tf3Bridge.Twitch;

namespace Tf3Bridge.Rewards;

/// <summary><see cref="Warning"/>: active, but Twitch does not let Tf3Bridge switch some TF3 rewards.</summary>
public enum RewardState { Off, WaitingForLogin, NotAffiliate, Ok, Warning, Inactive, Error }

/// <summary>A status whose <see cref="Message"/> is translated when shown, so it follows a language switch.</summary>
public sealed record RewardStatus(RewardState State, Func<string> Text)
{
    public RewardStatus(RewardState state, string message) : this(state, () => message) { }

    public string Message => Text();
}

/// <summary>
/// How a redemption ended. <see cref="Refunded"/>: the points went back to the viewer.
/// <see cref="ChatText"/>: the answer for the viewer in chat (sent only for real redemptions,
/// or for a simulation when asked to). <see cref="Count"/> / <see cref="Total"/>: vehicles wrecked by the brake sabotage.
/// </summary>
public sealed record RewardOutcome(string Token, RewardKind Kind, string User, string Input, bool Ok, string Message,
    string? Target = null, GameDate? Until = null, string ChatText = "", int? Count = null, int? Total = null)
{
    public bool Refunded => !Ok;
}

/// <summary>
/// Channel points rewards: keeps them in sync on Twitch (create, update, enabled only while the bridge runs
/// and a loaded save exports data, disabled when Tf3Bridge closes; the same for the streamer's own rewards with the
/// TF3 prefix, see <see cref="ExtraReward"/>), turns redemptions into mod commands and closes them: FULFILLED when the game confirms,
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
    private readonly string _terrainPath;
    private readonly Func<string?> _whyInactive; // null = the game runs and sends data
    private readonly Func<BuildTerrain?> _terrain;
    private readonly IndustryZoneRules? _zoneRules;
    private readonly string _settingsPath;
    private readonly string _siteUrl;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Pending> _pending = []; // command id -> redemption
    private RewardSettings _settings;
    private volatile RewardStatus _status = new(RewardState.Off, () => Loc.T("off"));
    private string? _syncedFor;      // the account the rewards are synced for
    private bool? _active;           // the enabled state last set on Twitch
    private IReadOnlyList<string> _locked = []; // TF3 rewards Twitch won't let Tf3Bridge change
    private readonly SemaphoreSlim _wake = new(0);
    private readonly SemaphoreSlim _tick = new(1, 1); // the loop and disabling on close

    public event Action<RewardOutcome>? Finished;

    public RewardService(TwitchSession session, EventSubClient eventSub, ModCommandQueue queue, BridgeRunner runner, ExportProcessor processor,
        IndustryZoneRules zoneRules, IOptions<BridgeOptions> options, IOptions<TwitchOptions> twitch, ILogger<RewardService> log)
        : this(session, new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, eventSub.IsMock, queue, options.Value.OutputDir, twitch.Value.SiteUrl,
            () => WhyInactive(runner.IsRunning, processor.LastDynamicUtc, DateTime.UtcNow), () => processor.BuildTerrain, zoneRules, log)
    {
        eventSub.RedemptionReceived += r => _ = HandleRedemptionAsync(r);
        runner.StateChanged += () => _wake.Release(); // Stop / Start: rewards right away, not after 15 s
    }

    // One HttpClient for the rewards and chat APIs.
    private RewardService(TwitchSession session, HttpClient http, bool mock, ModCommandQueue queue, string outputDir, string siteUrl,
        Func<string?> whyInactive, Func<BuildTerrain?> terrain, IndustryZoneRules zoneRules, ILogger log)
        : this(session, new ChannelPointsApi(session, http), new ChatApi(session, http), queue, mock, outputDir, RewardSettingsStore.FilePath,
            whyInactive, log, siteUrl, terrain, zoneRules) { }

    internal RewardService(TwitchSession session, ChannelPointsApi api, ChatApi chat, ModCommandQueue queue, bool mock,
        string outputDir, string settingsPath, Func<string?> whyInactive, ILogger log, string siteUrl = "", Func<BuildTerrain?>? terrain = null,
        IndustryZoneRules? zoneRules = null)
    {
        _whyInactive = whyInactive;
        _terrain = terrain ?? (() => null);
        _zoneRules = zoneRules;
        _siteUrl = siteUrl;
        _session = session;
        _api = api;
        _chat = chat;
        _queue = queue;
        _mock = mock;
        _dynamicPath = Path.Combine(outputDir, ExportProcessor.DynamicPath.Replace('/', Path.DirectorySeparatorChar));
        _terrainPath = Path.Combine(outputDir, ExportProcessor.TerrainInfoPath.Replace('/', Path.DirectorySeparatorChar));
        _settingsPath = settingsPath;
        _log = log;
        _settings = RewardSettingsStore.Load(settingsPath);
        _zoneRules?.Set(_settings.Zones);
        queue.Completed += OnCommandCompleted;
    }

    public RewardStatus Status => _status;

    public RewardSettings Settings { get { lock (_gate) return _settings; } }

    /// <summary>Saves new settings; the next loop pushes them to Twitch.</summary>
    public void UpdateSettings(RewardSettings settings)
    {
        lock (_gate)
        {
            _settings = (settings with
            {
                ChannelUserId = _settings.ChannelUserId, TwitchIds = _settings.TwitchIds,
                Extras = KeepEnabled(_settings.Extras, settings.Extras), // the window only changes the enabled flag
            }).Normalized();
            _zoneRules?.Set(_settings.Zones);
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
        !bridgeRunning ? Loc.T("the bridge is stopped")
        : lastDynamicUtc is not { } last ? Loc.T("no game data (load a save)")
        : nowUtc - last >= GameIdleAfter ? Loc.T("the game hasn't sent data for 2 min")
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
            _status = new(RewardState.Ok, () => Loc.T("test mode (Twitch CLI): no changes on Twitch"));
            return;
        }
        if (_session.UserId is not { } userId)
        {
            _status = new(RewardState.WaitingForLogin, () => Loc.T("waiting for login"));
            _syncedFor = null;
            return;
        }
        if (!_session.CanUseChannelPoints)
        {
            _status = new(RewardState.NotAffiliate, () => Loc.T("channel without Affiliate/Partner: rewards off (\"Simulate reward\" still works)"));
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
            var s = Settings;
            var enabled = s.Rewards.Count(r => r.Enabled);
            var extras = s.Extras.Count(e => e.Switchable && e.Enabled);
            var of = Loc.T("of");
            var counts = extras > 0 ? $"{enabled} {of} {s.Rewards.Count} + {extras} TF3" : $"{enabled} {of} {s.Rewards.Count}";
            var locked = _locked.Count == 0 ? ""
                : Loc.F("; Twitch won't let Tf3Bridge switch: {0} (added by hand in the dashboard)", string.Join(", ", _locked));
            _status = !active ? new(RewardState.Inactive, () => Loc.F("off on Twitch, because {0} ({1}){2}", why, counts, locked))
                : locked.Length > 0 ? new(RewardState.Warning, () => Loc.F("active: {0}{1}", counts, locked))
                : new(RewardState.Ok, () => Loc.F("active: {0}", counts));
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

    /// <param name="Link">Where the object is on the site (for the chat answer).</param>
    /// <param name="Entity">The target in the game; null for something new (the mod reports its id).</param>
    private sealed record Pending(string Token, RewardKind Kind, string User, string Input, string Target, string? Link, Origin Origin, long? Entity);

    private Task HandleRedemptionAsync(ChannelPointsRedemption r)
    {
        var kind = KindOf(r.RewardId);
        if (kind is null) return Task.CompletedTask; // not our reward
        if (RewardKinds.Manual(kind.Value)) return Task.CompletedTask; // stays in the Twitch queue for the streamer
        // Bought just before the rewards were disabled or waiting since the previous run: the game wouldn't carry it out anyway.
        if (!_mock && _whyInactive() is { } why)
        {
            Refuse(r, kind.Value, Loc.T("the game isn't running: ") + why);
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
        // Twitch CLI: "twitch event trigger ... -i AdoptTown" picks the reward by kind name
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

        if (RewardKinds.Manual(kind)) return Reject(Loc.T("the streamer carries out this reward, not the game"));
        if (!config.Enabled) return Reject(Loc.T("the reward is turned off"));
        if (!_queue.IsAvailable) return Reject(Loc.T("no game folder"));
        var owner = OwnerName.Clean(user).Replace("|", "");
        if (owner.Length == 0) return Reject(Loc.T("empty viewer name"));

        var objects = GameObjects.Load(_dynamicPath);
        var created = redeemedAt > DateTimeOffset.UtcNow ? DateTimeOffset.UtcNow : redeemedAt;
        ModCommand command;
        string targetName;
        string? link = null;
        long? entity = null;
        if (RewardKinds.HasAmount(kind))
        {
            command = kind == RewardKind.Loan
                ? ModCommand.Loan(config.Amount, config.Months, config.Factor, CommandSource.ChannelPoints, owner, created)
                : ModCommand.Subsidy(config.Amount, CommandSource.ChannelPoints, owner, created);
            targetName = RewardMessages.Money(config.Amount);
        }
        else if (RewardKinds.Creates(kind))
        {
            if (objects.Count == 0) return Reject(Loc.T("no game data (is TF3 running?)"));
            var parsed = BuildCode.Parse(input, kind == RewardKind.CreateIndustry, objects, MapBounds.Load(_terrainPath), _terrain(), Settings.Zones,
                GameObjects.LoadIndustryTypes(_dynamicPath));
            if (parsed.Place is not { } place) return Reject(parsed.Error!);
            command = place.File is { } file
                ? ModCommand.CreateIndustry(place.X, place.Y, file, place.Name, owner, CommandSource.ChannelPoints, created)
                : ModCommand.CreateTown(place.X, place.Y, place.Name, owner, CommandSource.ChannelPoints, created);
            (targetName, link) = (place.Name, RewardMessages.PlaceLink(_siteUrl, place.X, place.Y, place.Name));
        }
        else if (RewardKinds.TargetsOwner(kind))
        {
            var victim = Victim(objects, input, owner);
            if (victim.Error is { } error) return Reject(error);
            command = ModCommand.Effect(RewardKinds.Effect(kind)!, victim.Industries[0], config.Months, config.Factor,
                CommandSource.ChannelPoints, owner, created, victim.Industries, victim.Key);
            (targetName, link) = (Loc.F("the industries of viewer {0} ({1})", victim.Name, victim.Industries.Count),
                RewardMessages.OwnerLink(_siteUrl, victim.Key));
        }
        else
        {
            var resolved = TargetResolver.Resolve(objects, RewardKinds.Target(kind), input);
            if (resolved.Target is not { } target) return Reject(resolved.Error!);
            if (RewardKinds.Buys(kind) && target.Owner is { } current)
                return Reject(Loc.F("{0} already has an owner ({1})", target.Name, current));
            if (RewardKinds.Sabotage(kind))
            {
                if (target.Owner is not { } victim)
                    return Reject(Loc.F("{0} has no owner (only other viewers' objects can be sabotaged)", target.Name));
                if (OwnerIndex.Key(victim) == OwnerIndex.Key(owner)) return Reject(Loc.F("{0} belongs to you", target.Name));
            }
            command = kind == RewardKind.BrakeSabotage
                ? ModCommand.Wreck(target.Entity, config.Factor, CommandSource.ChannelPoints, owner, created)
                : RewardKinds.Effect(kind) is { } effect
                ? ModCommand.Effect(effect, target.Entity, config.Months, RewardKinds.HasFactor(kind) ? config.Factor : null,
                    CommandSource.ChannelPoints, owner, created)
                : ModCommand.Adopt(target.Entity, owner, CommandSource.ChannelPoints, owner, created);
            (targetName, link, entity) = (target.Name, RewardMessages.Link(_siteUrl, kind, target.Entity), target.Entity);
        }
        lock (_gate) _pending[command.Id] = new Pending(token, kind, owner, input, targetName, link, origin, entity);
        _queue.Enqueue(command);
        _log.LogInformation("Reward \"{Title}\" by {User}: {Target}", config.Title, owner, targetName);
        return null;
    }

    private sealed record VictimResult(string Key, string Name, IReadOnlyList<long> Industries, string? Error);

    /// <summary>
    /// The viewer whose nick another viewer typed (tax raid): all industries with that owner.
    /// Nick compared like object names (case, Polish letters, a leading @ do not matter).
    /// </summary>
    private static VictimResult Victim(List<GameObject> objects, string input, string buyer)
    {
        var query = TargetResolver.Normalize(input.Trim().TrimStart('@'));
        if (query.Length == 0) return new("", "", [], Loc.T("no viewer nick given"));
        var owned = objects.Where(o => o.Owner is { } w && TargetResolver.Normalize(w) == query).ToList();
        if (owned.Count == 0) return new("", "", [], Loc.F("viewer \"{0}\" doesn't own anything in the game", input.Trim()));
        var name = owned.GroupBy(o => o.Owner!).MaxBy(g => g.Count())!.Key;
        var key = OwnerIndex.Key(name);
        if (key == OwnerIndex.Key(buyer)) return new(key, name, [], Loc.T("you can't send the tax office after yourself"));
        var industries = owned.Where(o => o.Kind == GameObjectKind.Industry).Select(o => o.Entity).Distinct().ToList();
        return industries.Count == 0 ? new(key, name, [], Loc.F("{0} has no industries", name)) : new(key, name, industries, null);
    }

    private void OnCommandCompleted(ModCommand command, ModCommandResult result)
    {
        if (command.Kind == "notify")
        {
            if (!result.Ok) _log.LogWarning("Game notification not shown: {Error}", Explain(result));
            return;
        }
        Pending? p;
        lock (_gate)
        {
            if (!_pending.Remove(command.Id, out p)) return;
        }
        var reason = result.Ok ? null : $"{p.Target}: {Explain(result)}";
        var message = result.Ok
            ? Loc.T("done: ") + p.Target
                + (result.Until is { } u ? Loc.T(", until ") + RewardMessages.Date(u) : "")
            : reason!;
        // New town / industry: a link to the object by its game id; a pin at the building spot only when the mod didn't give it.
        var link = result.Entity is { } built && RewardKinds.Creates(p.Kind) ? RewardMessages.Link(_siteUrl, p.Kind, built) : p.Link;
        var chat = RewardMessages.Text(Settings.Get(p.Kind), p.Origin.Login, p.Target, result.Ok, result.Until, reason, link, result.Count, result.Total);
        var outcome = new RewardOutcome(p.Token, p.Kind, p.User, p.Input, result.Ok, message, p.Target, result.Until, chat, result.Count, result.Total);
        if (result.Ok) _log.LogInformation("Reward {Kind} by {User}: {Message}", p.Kind, p.User, message);
        else _log.LogWarning("Reward {Kind} by {User}: {Message}, refunding", p.Kind, p.User, message);
        Complete(p.Origin, outcome, result.Entity ?? p.Entity);
    }

    // End of a redemption: an event for the window, settling on Twitch, a chat answer and an in-game notification
    // (only on success; for a real redemption only once Twitch accepted FULFILLED, i.e. the points are taken).
    private void Complete(Origin origin, RewardOutcome outcome, long? entity = null)
    {
        Finished?.Invoke(outcome);
        if (origin.Redemption is { } r) _ = CloseAndNotifyAsync(r, outcome, entity);
        else if (outcome.Ok) Notify(outcome, entity, test: true);
        if (origin.Chat && Settings.ChatMessages) _ = SayAsync(outcome.ChatText);
    }

    private async Task CloseAndNotifyAsync(Redemption r, RewardOutcome outcome, long? entity)
    {
        if (await CloseAsync(r, outcome) && outcome.Ok) Notify(outcome, entity, test: false);
    }

    private void Notify(RewardOutcome outcome, long? entity, bool test)
    {
        if (!Settings.GameNotifications || outcome.Target is not { } target || !_queue.IsAvailable) return;
        var text = RewardMessages.Notification(Settings.Get(outcome.Kind), outcome.User, target, outcome.Until, outcome.Count, outcome.Total);
        try
        {
            _queue.Enqueue(ModCommand.Notify(RewardMessages.NotificationTitle(outcome.User, test), text, entity, Settings.NotificationDismissMs,
                test ? CommandSource.Test : CommandSource.ChannelPoints, outcome.User));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _log.LogWarning("Game notification not queued: {Message}", ex.Message);
        }
    }

    private async Task SayAsync(string text)
    {
        if (_mock || text.Length == 0) return; // the test server has no chat
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
        "expired" => Loc.T("the game didn't carry out the command in time"),
        // The game keeps the scripts loaded at startup: a new mod file only works after restarting TF3.
        _ when r.Error?.StartsWith("unknown kind ", StringComparison.Ordinal) == true
            => Loc.T("the game runs an older mod version (restart TF3)"),
        _ when r.Error?.StartsWith("already adopted by ", StringComparison.Ordinal) == true
            => Loc.T("already has an owner (") + r.Error["already adopted by ".Length..] + ")",
        "invalid" when r.Error == "line has no vehicles" => Loc.T("the line has no vehicles"),
        "invalid" when r.Error == "no industries" => Loc.T("those industries no longer exist"),
        "invalid" when r.Error == "unlimited money" => Loc.T("the game has unlimited money"),
        "invalid" when r.Error?.StartsWith("loan limit", StringComparison.Ordinal) == true
            => Loc.T("the company already has the most loans the game allows (4)"),
        // create.lua
        "invalid" when r.Error == "on water" => Loc.T("that spot is on water"),
        "invalid" when r.Error == "needs water" => Loc.T("this industry can only be placed on water"),
        "invalid" when r.Error == "outside of the map" => Loc.T("that spot is outside the map"),
        "invalid" when r.Error?.StartsWith("no room", StringComparison.Ordinal) == true => Loc.T("the game found no room there"),
        "invalid" when r.Error == "town with this name already exists" => Loc.T("a town with this name already exists"),
        "failed" when r.Error == "game rejected the command"
            => Loc.T("the game rejected the build (something is in the way: tracks, a road or buildings?)"),
        _ => r.Error ?? r.Status,
    };

    /// <returns>True when Twitch took the new status (for FULFILLED: the points are taken).</returns>
    private async Task<bool> CloseAsync(Redemption r, RewardOutcome outcome)
    {
        if (_mock) return true; // the test server has no real redemptions
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _api.SetRedemptionStatusAsync(r.RewardId, r.Id, outcome.Ok, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is TwitchAuthException or HttpRequestException or OperationCanceledException)
        {
            // It stays in the Twitch redemption queue; the streamer can settle it by hand in the dashboard.
            _log.LogWarning("Could not mark redemption {Id} as {Status}: {Message}", r.Id, outcome.Ok ? "fulfilled" : "canceled", ex.Message);
            return false;
        }
    }

    // Redemptions from before Tf3Bridge started: fresh ones are carried out, old ones refunded (except rewards the streamer carries out).
    private async Task ProcessBacklogAsync(CancellationToken ct)
    {
        foreach (var (kind, rewardId) in Settings.TwitchIds)
        {
            if (RewardKinds.Manual(kind)) continue;
            foreach (var r in await _api.GetUnfulfilledAsync(rewardId, ct))
            {
                var redemption = new ChannelPointsRedemption(r.Id, rewardId, "", 0, "", r.UserLogin, r.UserName, r.UserInput, r.RedeemedAt);
                if (DateTimeOffset.UtcNow - r.RedeemedAt > MaxRedemptionAge)
                {
                    _log.LogWarning("Refunding old redemption of {Kind} by {User} from {At:g}", kind, r.UserName, r.RedeemedAt.ToLocalTime());
                    Refuse(redemption, kind, Loc.T("the game wasn't running when the reward was bought"));
                    continue;
                }
                await HandleRedemptionAsync(redemption);
            }
        }
    }

    // Refund without sending anything to the game.
    private void Refuse(ChannelPointsRedemption r, RewardKind kind, string why)
    {
        var login = r.UserLogin.Length > 0 ? r.UserLogin : r.UserName;
        _log.LogWarning("Reward {Kind} by {User} ({Input}): refunding, {Why}", kind, r.UserName, r.UserInput, why);
        Complete(new Origin(new Redemption(r.RewardId, r.Id), login, Chat: true),
            new RewardOutcome(r.Id, kind, r.UserName.Length > 0 ? r.UserName : login, r.UserInput, false, why,
                ChatText: RewardMessages.Text(Settings.Get(kind), login, null, ok: false, until: null, reason: why)));
    }

    // ------------------------------------------------------------------ rewards on Twitch

    /// <summary>All rewards on the channel and the IDs of those this client ID created (only those it may change).</summary>
    private sealed record ChannelRewards(List<TwitchReward> All, HashSet<string> Mine);

    private async Task<ChannelRewards> GetChannelRewardsAsync(CancellationToken ct)
    {
        var mine = await _api.GetRewardsAsync(onlyManageable: true, ct);
        var all = await _api.GetRewardsAsync(onlyManageable: false, ct);
        return new([.. all.Concat(mine.Where(m => all.All(a => a.Id != m.Id)))], [.. mine.Select(m => m.Id)]);
    }

    private static bool SameTitle(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private async Task SyncAsync(string userId, bool active, CancellationToken ct)
    {
        var s = Settings;
        var sameChannel = s.ChannelUserId == userId;
        var ids = sameChannel ? new Dictionary<RewardKind, string>(s.TwitchIds) : [];
        var channel = await GetChannelRewardsAsync(ct);
        var existing = channel.All.Where(r => channel.Mine.Contains(r.Id)).ToList();
        foreach (var config in s.Rewards)
        {
            var id = ids.TryGetValue(config.Kind, out var known) && existing.Any(e => e.Id == known)
                ? known
                : existing.FirstOrDefault(e => e.Title == config.Title)?.Id; // e.g. after rewards.json was lost
            var body = Body(config, active, RewardMessages.SiteHost(_siteUrl));
            if (id is null)
            {
                ids.Remove(config.Kind);
                if (!config.Enabled) continue;
                // Twitch won't accept a second reward with the same title: the manual one has to be removed first.
                if (channel.All.Any(r => SameTitle(r.Title, config.Title)))
                {
                    _log.LogWarning("Reward \"{Title}\" was added by hand on Twitch: delete it there and Tf3Bridge will create its own", config.Title);
                    continue;
                }
                id = await _api.CreateRewardAsync(body, ct);
                _log.LogInformation("Reward created on Twitch: {Title}", config.Title);
            }
            else await _api.UpdateRewardAsync(id, body, ct);
            ids[config.Kind] = id;
        }
        lock (_gate)
        {
            _settings = _settings with { ChannelUserId = userId, TwitchIds = ids, Extras = sameChannel ? _settings.Extras : [] };
            SaveLocked();
        }
        _log.LogInformation("Rewards synced with Twitch ({Count} enabled)", s.Rewards.Count(r => r.Enabled));
        await SyncExtrasAsync(active, channel, ct);
    }

    /// <summary>
    /// The streamer's own rewards with the TF3 prefix (<see cref="ExtraReward"/>): switched on and off like ours.
    /// Each is set once here; one Twitch refuses (403, created by another app or by hand) is only reported.
    /// </summary>
    private async Task SyncExtrasAsync(bool active, ChannelRewards channel, CancellationToken ct)
    {
        var s = Settings;
        var kindIds = s.TwitchIds.Values.ToHashSet();
        var extras = new List<ExtraReward>();
        var locked = new List<string>();
        foreach (var r in channel.All)
        {
            if (kindIds.Contains(r.Id)) continue;
            if (s.Rewards.FirstOrDefault(c => SameTitle(c.Title, r.Title)) is { } kind)
            {
                if (kind.Enabled && !channel.Mine.Contains(r.Id)) locked.Add(r.Title); // blocks creating ours (SyncAsync)
                continue;
            }
            if (!ExtraReward.HasPrefix(r.Title)) continue;
            var enabled = s.Extras.FirstOrDefault(e => e.TwitchId == r.Id)?.Enabled ?? true;
            var switchable = await TrySwitchAsync(r.Id, r.Title, enabled && active, ct);
            if (!switchable) locked.Add(r.Title);
            extras.Add(new ExtraReward(r.Id, r.Title, r.Cost, enabled, switchable));
        }
        _locked = locked;
        lock (_gate)
        {
            var merged = KeepEnabled(extras, _settings.Extras); // the window may have saved something in the meantime
            if (merged.SequenceEqual(_settings.Extras)) return;
            _settings = _settings with { Extras = merged };
            SaveLocked();
        }
    }

    /// <summary>False when Twitch does not let this app change the reward.</summary>
    private async Task<bool> TrySwitchAsync(string rewardId, string title, bool enabled, CancellationToken ct)
    {
        try
        {
            await _api.UpdateRewardAsync(rewardId, new JsonObject { ["is_enabled"] = enabled }, ct);
            return true;
        }
        catch (TwitchAuthException ex) when (ex.HttpStatus == 403)
        {
            _log.LogWarning("Twitch does not let Tf3Bridge switch reward \"{Title}\": {Message}", title, ex.Message);
            return false;
        }
    }

    /// <summary><paramref name="extras"/> with <see cref="ExtraReward.Enabled"/> taken from the matching entry of <paramref name="from"/>.</summary>
    private static List<ExtraReward> KeepEnabled(IReadOnlyList<ExtraReward> extras, IReadOnlyList<ExtraReward> from) =>
        [.. extras.Select(e => from.FirstOrDefault(f => f.TwitchId == e.TwitchId) is { } f ? e with { Enabled = f.Enabled } : e)];

    private async Task SetActiveAsync(bool active, CancellationToken ct)
    {
        foreach (var config in Settings.Rewards.Where(r => r.Enabled))
        {
            if (Settings.TwitchIds.TryGetValue(config.Kind, out var id))
                await _api.UpdateRewardAsync(id, new JsonObject { ["is_enabled"] = active }, ct);
        }
        foreach (var extra in Settings.Extras.Where(e => e.Switchable && e.Enabled))
            await TrySwitchAsync(extra.TwitchId, extra.Title, active, ct);
    }

    /// <param name="active">false = hidden from viewers (is_enabled), so nobody buys while the game cannot handle it.</param>
    /// <param name="site">Fills {site} in the prompt.</param>
    internal static JsonObject Body(RewardConfig c, bool active, string site = "")
    {
        var prompt = c.TwitchPrompt.Replace("{site}", site);
        var body = new JsonObject
        {
            ["title"] = c.Title,
            ["prompt"] = prompt.Length <= RewardConfig.MaxPrompt ? prompt : prompt[..RewardConfig.MaxPrompt],
            ["cost"] = c.Cost,
            ["is_enabled"] = c.Enabled && active,
            ["is_paused"] = false, // older versions paused instead of disabling
            ["is_user_input_required"] = c.RequiresInput,
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
