using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Tf3Bridge.Twitch;

/// <summary>A viewer redeemed a custom channel points reward.</summary>
public sealed record ChannelPointsRedemption(
    string Id, string RewardId, string RewardTitle, int Cost,
    string UserId, string UserLogin, string UserName, string UserInput, DateTimeOffset RedeemedAt);

/// <summary>A chat message. <see cref="Badges"/> are badge set IDs, e.g. broadcaster, moderator, vip, subscriber.</summary>
public sealed record ChatMessage(
    string MessageId, string ChatterId, string ChatterLogin, string ChatterName, string Text, string[] Badges);

public enum EventSubState { Off, WaitingForLogin, Connecting, Connected, Error }

public sealed record EventSubStatus(EventSubState State, string Message);

/// <summary>One WebSocket connection, reduced to what EventSub needs (replaced in tests).</summary>
public interface IEventSubSocket : IAsyncDisposable
{
    /// <summary>Next text message, or null when the server closed the connection.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);

    /// <summary>Close code and reason sent by the server (e.g. "4003 connection unused"), if any.</summary>
    string? CloseDescription { get; }
}

public sealed class EventSubException(string message) : Exception(message);

/// <summary>
/// EventSub over WebSocket with the streamer's user token (no public server needed). Subscribes to
/// channel points redemptions and chat messages, keeps the connection alive (keepalive timeout,
/// session_reconnect, reconnect with backoff) and raises events. Twitch may deliver a message twice,
/// so notifications are de-duplicated by message ID.
/// </summary>
public sealed class EventSubClient
{
    public const string TwitchUrl = "wss://eventsub.wss.twitch.tv/ws?keepalive_timeout_seconds=30";
    public const string HelixSubscriptionsUrl = "https://api.twitch.tv/helix/eventsub/subscriptions";

    public const string RedemptionType = "channel.channel_points_custom_reward_redemption.add";
    public const string ChatMessageType = "channel.chat.message";

    private static readonly TimeSpan WelcomeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StableConnection = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1)];
    private const int SeenMax = 500;

    private readonly TwitchSession _session;
    private readonly HttpClient _http;
    private readonly Uri _url;
    private readonly Uri _subscriptionsUrl;
    private readonly Func<Uri, CancellationToken, Task<IEventSubSocket>> _connect;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _log;
    private readonly HashSet<string> _seen = [];
    private readonly Queue<string> _seenOrder = new();
    private volatile EventSubStatus _status;

    public event Action<ChannelPointsRedemption>? RedemptionReceived;
    public event Action<ChatMessage>? ChatMessageReceived;

    public EventSubClient(TwitchSession session, IOptions<TwitchOptions> options, ILogger<EventSubClient> log)
        : this(session, new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, options.Value, WebSocketConnection.ConnectAsync, Task.Delay, log) { }

    internal EventSubClient(TwitchSession session, HttpClient http, TwitchOptions options,
        Func<Uri, CancellationToken, Task<IEventSubSocket>> connect, Func<TimeSpan, CancellationToken, Task> delay, ILogger log)
    {
        _session = session;
        _http = http;
        IsMock = !string.IsNullOrWhiteSpace(options.EventSubUrl);
        _url = new Uri(IsMock ? options.EventSubUrl.Trim() : TwitchUrl);
        _subscriptionsUrl = new Uri(string.IsNullOrWhiteSpace(options.SubscriptionsUrl) ? HelixSubscriptionsUrl : options.SubscriptionsUrl.Trim());
        _connect = connect;
        _delay = delay;
        _log = log;
        _status = new(EventSubState.Off, "wyłączone");
    }

    /// <summary>Connected to a test server (Twitch CLI) instead of Twitch.</summary>
    public bool IsMock { get; }

    public Uri Url => _url;

    public EventSubStatus Status => _status;

    /// <summary>Added to the keepalive timeout Twitch announces before the connection is considered dead.</summary>
    internal TimeSpan KeepaliveGrace { get; set; } = TimeSpan.FromSeconds(10);

    public async Task RunAsync(CancellationToken ct)
    {
        if (!_session.IsConfigured) return;
        var failures = 0;
        try
        {
            while (true)
            {
                if (_session.UserId is not { } userId)
                {
                    _status = new(EventSubState.WaitingForLogin, "czeka na zalogowanie");
                    await _delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }
                var started = DateTimeOffset.UtcNow;
                try
                {
                    await RunConnectionAsync(userId, ct);
                    failures = 0; // account change: reconnect right away
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    failures = DateTimeOffset.UtcNow - started > StableConnection ? 1 : failures + 1;
                    _status = new(EventSubState.Error, ex.Message);
                    _log.LogWarning("EventSub: {Message} (reconnecting in {Delay} s)", ex.Message, Backoff[Math.Min(failures, Backoff.Length - 1)].TotalSeconds);
                }
                await _delay(Backoff[Math.Min(failures, Backoff.Length - 1)], ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        _status = new(EventSubState.Off, "wyłączone");
    }

    // Returns normally only after an account change; a dropped connection is an exception.
    private async Task RunConnectionAsync(string userId, CancellationToken ct)
    {
        _status = new(EventSubState.Connecting, $"łączę z {_url.Host}…");
        var socket = await _connect(_url, ct);
        try
        {
            var (sessionId, keepalive) = await WaitForWelcomeAsync(socket, ct);
            var subscribed = await SubscribeAllAsync(sessionId, userId, ct);
            _status = new(EventSubState.Connected, $"połączono ({subscribed})");
            _log.LogInformation("EventSub: connected to {Host}, session {Session}, subscribed: {Subscribed}", _url.Host, sessionId, subscribed);

            while (_session.UserId == userId)
            {
                var root = await ReceiveAsync(socket, keepalive + KeepaliveGrace, ct);
                var meta = root.GetProperty("metadata");
                switch (Str(meta, "message_type"))
                {
                    case "notification":
                        Dispatch(meta, root.GetProperty("payload"));
                        break;
                    case "session_reconnect":
                        // Twitch moves the subscriptions to the new connection; the old one is closed only after the welcome.
                        var url = new Uri(Str(root.GetProperty("payload").GetProperty("session"), "reconnect_url"));
                        _log.LogInformation("EventSub: reconnect requested");
                        var next = await _connect(url, ct);
                        try { (_, keepalive) = await WaitForWelcomeAsync(next, ct); }
                        catch { await next.DisposeAsync(); throw; }
                        await socket.DisposeAsync();
                        socket = next;
                        break;
                    case "revocation":
                        var sub = root.GetProperty("payload").GetProperty("subscription");
                        _log.LogWarning("EventSub: subscription {Type} revoked ({Status})", Str(sub, "type"), Str(sub, "status"));
                        break;
                    // session_keepalive: receiving any message extends the connection time
                }
            }
            _log.LogInformation("EventSub: account changed, reconnecting");
        }
        finally
        {
            await socket.DisposeAsync();
        }
    }

    private async Task<(string SessionId, TimeSpan Keepalive)> WaitForWelcomeAsync(IEventSubSocket socket, CancellationToken ct)
    {
        var root = await ReceiveAsync(socket, WelcomeTimeout, ct);
        if (Str(root.GetProperty("metadata"), "message_type") != "session_welcome")
            throw new EventSubException("serwer nie przysłał powitania (session_welcome)");
        var session = root.GetProperty("payload").GetProperty("session");
        var keepalive = session.TryGetProperty("keepalive_timeout_seconds", out var k) && k.TryGetInt32(out var s) ? s : 10;
        return (Str(session, "id"), TimeSpan.FromSeconds(keepalive));
    }

    private async Task<JsonElement> ReceiveAsync(IEventSubSocket socket, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        string? text;
        try { text = await socket.ReceiveAsync(cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new EventSubException($"brak wiadomości od serwera przez {timeout.TotalSeconds:0} s");
        }
        if (text is null)
            throw new EventSubException($"serwer zamknął połączenie{(socket.CloseDescription is { } d ? $" ({d})" : "")}");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private async Task<string> SubscribeAllAsync(string sessionId, string userId, CancellationToken ct)
    {
        (string Type, string Label, Dictionary<string, string> Condition)[] subscriptions =
        [
            (ChatMessageType, "czat", new() { ["broadcaster_user_id"] = userId, ["user_id"] = userId }),
            (RedemptionType, "punkty kanału", new() { ["broadcaster_user_id"] = userId }),
        ];
        var ok = new List<string>();
        var errors = new List<string>();
        foreach (var (type, label, condition) in subscriptions)
        {
            var error = await SubscribeAsync(sessionId, type, condition, ct);
            if (error is null) ok.Add(label);
            else
            {
                errors.Add($"{label}: {error}");
                _log.LogWarning("EventSub: subscription {Type} failed: {Error}", type, error);
            }
        }
        // The Twitch CLI test server (without -S) forwards events even without subscriptions.
        if (ok.Count == 0 && !IsMock) throw new EventSubException($"Twitch nie przyjął subskrypcji ({string.Join("; ", errors)})");
        return ok.Count > 0 ? string.Join(", ", ok) : "bez subskrypcji";
    }

    private async Task<string?> SubscribeAsync(string sessionId, string type, Dictionary<string, string> condition, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await _session.GetAccessTokenAsync(ct, forceRefresh: attempt > 0)
                ?? throw new EventSubException("niezalogowano");
            using var request = new HttpRequestMessage(HttpMethod.Post, _subscriptionsUrl)
            {
                Content = JsonContent.Create(new
                {
                    type,
                    version = "1",
                    condition,
                    transport = new { method = "websocket", session_id = sessionId },
                }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Client-Id", _session.ClientId);
            using var response = await _http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return null;
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue; // token revoked: refresh and try once more
            var body = await response.Content.ReadAsStringAsync(ct);
            var message = body;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("message", out var m)) message = m.GetString() ?? body;
            }
            catch (JsonException) { }
            return $"HTTP {(int)response.StatusCode} {message}".Trim();
        }
    }

    private void Dispatch(JsonElement meta, JsonElement payload)
    {
        if (!Remember(Str(meta, "message_id"))) return; // Twitch may send the same event twice
        var ev = payload.GetProperty("event");
        try
        {
            switch (Str(meta, "subscription_type"))
            {
                case RedemptionType:
                    var reward = ev.GetProperty("reward");
                    var r = new ChannelPointsRedemption(
                        Str(ev, "id"), Str(reward, "id"), Str(reward, "title"),
                        reward.TryGetProperty("cost", out var c) && c.TryGetInt32(out var cost) ? cost : 0,
                        Str(ev, "user_id"), Str(ev, "user_login"), Str(ev, "user_name"), Str(ev, "user_input"),
                        DateTimeOffset.TryParse(Str(ev, "redeemed_at"), out var at) ? at : DateTimeOffset.UtcNow);
                    _log.LogInformation("Channel points: {User} redeemed \"{Reward}\" ({Cost}){Input}",
                        r.UserName, r.RewardTitle, r.Cost, r.UserInput.Length > 0 ? ": " + r.UserInput : "");
                    RedemptionReceived?.Invoke(r);
                    break;
                case ChatMessageType:
                    var m = new ChatMessage(
                        Str(ev, "message_id"), Str(ev, "chatter_user_id"), Str(ev, "chatter_user_login"), Str(ev, "chatter_user_name"),
                        ev.TryGetProperty("message", out var msg) ? Str(msg, "text") : "",
                        ev.TryGetProperty("badges", out var b) && b.ValueKind == JsonValueKind.Array
                            ? [.. b.EnumerateArray().Select(x => Str(x, "set_id")).Where(x => x.Length > 0)] : []);
                    if (m.Text.StartsWith('!')) _log.LogInformation("Chat: {User}: {Text}", m.ChatterName, m.Text);
                    ChatMessageReceived?.Invoke(m);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An error while handling one event must not drop the connection.
            _log.LogWarning("EventSub: handling {Type} failed: {Message}", Str(meta, "subscription_type"), ex.Message);
        }
    }

    private bool Remember(string messageId)
    {
        if (messageId.Length == 0) return true;
        if (!_seen.Add(messageId)) return false;
        _seenOrder.Enqueue(messageId);
        if (_seenOrder.Count > SeenMax) _seen.Remove(_seenOrder.Dequeue());
        return true;
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
}

/// <summary><see cref="IEventSubSocket"/> over <see cref="ClientWebSocket"/>.</summary>
internal sealed class WebSocketConnection : IEventSubSocket
{
    private const int MaxMessageBytes = 1 << 20;
    private readonly ClientWebSocket _ws = new();

    public static async Task<IEventSubSocket> ConnectAsync(Uri url, CancellationToken ct)
    {
        var connection = new WebSocketConnection();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        try { await connection._ws.ConnectAsync(url, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            connection._ws.Dispose();
            throw new EventSubException($"nie udało się połączyć z {url.Host} w 15 s");
        }
        catch
        {
            connection._ws.Dispose();
            throw;
        }
        return connection;
    }

    public string? CloseDescription => _ws.CloseStatus is { } s ? $"{(int)s} {_ws.CloseStatusDescription}".Trim() : null;

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            var r = await _ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, r.Count);
            if (message.Length > MaxMessageBytes) throw new EventSubException("za duża wiadomość od serwera");
            if (r.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token); }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        }
        _ws.Dispose();
    }
}
