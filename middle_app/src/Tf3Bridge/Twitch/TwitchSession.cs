using Microsoft.Extensions.Options;

namespace Tf3Bridge.Twitch;

public enum TwitchState { NotConfigured, LoggedOut, Checking, Ok, Warning, Error }

/// <summary>A status whose <see cref="Message"/> is translated when shown, so it follows a language switch.</summary>
public sealed record TwitchStatus(TwitchState State, Func<string> Text)
{
    public TwitchStatus(TwitchState state, string message) : this(state, () => message) { }

    public string Message => Text();
}

/// <summary>
/// Keeps the streamer's user access token alive: validates it at start and every hour (Twitch
/// requires that), refreshes it before it expires and stores the rotated refresh token.
/// When Twitch rejects the refresh token the account is forgotten and the streamer logs in again.
/// </summary>
public sealed class TwitchSession
{
    private static readonly TimeSpan ValidateEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly TwitchAuthClient? _client;
    private readonly string _storePath;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _lock = new(1, 1); // the refresh token is single-use: one refresh at a time
    private TwitchAccount? _account;
    private volatile TwitchStatus _status;

    public TwitchSession(IOptions<TwitchOptions> options, ILogger<TwitchSession> log)
        : this(CreateClient(options.Value.ClientId), TwitchAccountStore.FilePath, log) { }

    internal TwitchSession(TwitchAuthClient? client, string storePath, ILogger log)
    {
        _client = client;
        _storePath = storePath;
        _log = log;
        _account = client is null ? null : TwitchAccountStore.Load(storePath);
        var login = _account?.Login;
        _status = client is null ? new(TwitchState.NotConfigured, () => Loc.T("no Twitch:ClientId in appsettings.json"))
            : _account is null ? new(TwitchState.LoggedOut, () => Loc.T("not logged in"))
            : new(TwitchState.Checking, () => Loc.F("checking account {0}…", login));
    }

    public TwitchStatus Status => _status;

    public bool IsConfigured => _client is not null;

    public bool IsLoggedIn => _account is not null;

    /// <summary>Twitch ID of the logged-in streamer (EventSub conditions), or null.</summary>
    public string? UserId => _account?.UserId;

    /// <summary>Affiliate/Partner: the channel can have custom channel points rewards.</summary>
    public bool CanUseChannelPoints => _account?.CanUseChannelPoints == true;

    public string ClientId => Client.ClientId;

    /// <summary>Validation loop for the lifetime of the app.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (_client is null) return;
        try
        {
            while (true)
            {
                await CheckAsync(ct);
                await Task.Delay(ValidateEvery, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>
    /// A valid access token for Helix/EventSub calls (refreshed when close to expiry), or null
    /// when nobody is logged in. Network errors are thrown to the caller.
    /// </summary>
    /// <param name="forceRefresh">Twitch rejected the current token (401): get a new one regardless of its expiry.</param>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct, bool forceRefresh = false)
    {
        if (_client is null) return null;
        await _lock.WaitAsync(ct);
        try
        {
            if (_account is null) return null;
            if ((forceRefresh || _account.ExpiresAt - DateTimeOffset.UtcNow < RefreshMargin) && !await RefreshLockedAsync(ct)) return null;
            return _account?.AccessToken;
        }
        finally { _lock.Release(); }
    }

    public Task<DeviceCode> BeginLoginAsync(CancellationToken ct) => Client.StartDeviceAsync(ct);

    /// <summary>Waits until the streamer confirms <paramref name="code"/> on Twitch, then stores the account.</summary>
    public async Task CompleteLoginAsync(DeviceCode code, CancellationToken ct)
    {
        var tokens = await Client.PollDeviceAsync(code, ct);
        var info = await Client.ValidateAsync(tokens.AccessToken, ct)
            ?? throw new TwitchAuthException(Loc.T("Twitch rejected the new token."), rejected: true);
        var type = await Client.GetBroadcasterTypeAsync(tokens.AccessToken, info.UserId, ct);
        await _lock.WaitAsync(ct);
        try
        {
            _account = new TwitchAccount(info.UserId, info.Login, type, tokens.Scopes,
                tokens.AccessToken, tokens.RefreshToken, DateTimeOffset.UtcNow + tokens.ExpiresIn);
            TwitchAccountStore.Save(_account, _storePath);
            SetLoggedIn();
        }
        finally { _lock.Release(); }
        _log.LogInformation("Twitch: logged in as {Account}", _account);
    }

    /// <summary>Forgets the account and revokes its token (best effort).</summary>
    public async Task LogoutAsync()
    {
        TwitchAccount? old;
        await _lock.WaitAsync();
        try
        {
            old = _account;
            Forget(() => Loc.T("logged out"));
        }
        finally { _lock.Release(); }
        if (old is null || _client is null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _client.RevokeAsync(old.AccessToken, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TwitchAuthException or OperationCanceledException)
        {
            _log.LogWarning("Twitch: token revoke failed: {Message}", ex.Message);
        }
        _log.LogInformation("Twitch: logged out {Login}", old.Login);
    }

    internal async Task CheckAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_account is null)
            {
                _status = new(TwitchState.LoggedOut, () => Loc.T("not logged in"));
                return;
            }
            if (!_account.HasScopes(TwitchAuthClient.Scopes))
            {
                Forget(() => Loc.T("permissions missing – log in again"));
                return;
            }
            var info = await Client.ValidateAsync(_account.AccessToken, ct);
            if ((info is null || info.ExpiresIn < RefreshMargin) && !await RefreshLockedAsync(ct)) return;
            var type = await Client.GetBroadcasterTypeAsync(_account.AccessToken, _account.UserId, ct);
            if (type != _account.BroadcasterType)
            {
                _account = _account with { BroadcasterType = type };
                TwitchAccountStore.Save(_account, _storePath);
            }
            SetLoggedIn();
        }
        catch (TwitchAuthException ex) when (ex.Rejected)
        {
            _log.LogWarning("Twitch: account rejected: {Message}", ex.Message);
            Forget(() => Loc.T("session expired – log in again"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // No network or an error on Twitch's side: the account stays, we'll try on the next check.
            _log.LogWarning("Twitch: validation failed: {Message}", ex.Message);
            var login = _account?.Login;
            _status = new(TwitchState.Error, () => Loc.F("{0}: couldn't check the account ({1})", login, ex.Message));
        }
        finally { _lock.Release(); }
    }

    // Call under _lock.
    private async Task<bool> RefreshLockedAsync(CancellationToken ct)
    {
        try
        {
            var t = await Client.RefreshAsync(_account!.RefreshToken, ct);
            _account = _account with
            {
                AccessToken = t.AccessToken,
                RefreshToken = t.RefreshToken,
                ExpiresAt = DateTimeOffset.UtcNow + t.ExpiresIn,
                Scopes = t.Scopes.Length > 0 ? t.Scopes : _account.Scopes,
            };
            TwitchAccountStore.Save(_account, _storePath);
            _log.LogInformation("Twitch: token refreshed for {Login}", _account.Login);
            return true;
        }
        catch (TwitchAuthException ex) when (ex.Rejected)
        {
            _log.LogWarning("Twitch: refresh rejected: {Message}", ex.Message);
            Forget(() => Loc.T("session expired – log in again"));
            return false;
        }
    }

    private void SetLoggedIn()
    {
        var login = _account!.Login;
        _status = _account.CanUseChannelPoints
            ? new(TwitchState.Ok, $"{login} ({_account.BroadcasterType})")
            : new(TwitchState.Warning, () => Loc.F("{0}: channel without Affiliate/Partner status, channel points rewards won't work", login));
    }

    private void Forget(Func<string> text)
    {
        _account = null;
        try { TwitchAccountStore.Delete(_storePath); }
        catch (IOException ex) { _log.LogWarning("Twitch: could not delete {Path}: {Message}", _storePath, ex.Message); }
        _status = new(TwitchState.LoggedOut, text);
    }

    private TwitchAuthClient Client => _client ?? throw new InvalidOperationException("Twitch:ClientId is not configured.");

    private static TwitchAuthClient? CreateClient(string clientId) => string.IsNullOrWhiteSpace(clientId)
        ? null
        : new TwitchAuthClient(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, clientId.Trim());
}
