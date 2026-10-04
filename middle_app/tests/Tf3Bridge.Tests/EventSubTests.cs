using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Tf3Bridge.Twitch;

namespace Tf3Bridge.Tests;

public sealed class EventSubTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tf3bridge-eventsub-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHelix _helix = new();
    private readonly ConcurrentQueue<FakeSocket> _sockets = new();
    private readonly ConcurrentQueue<Uri> _connected = new();
    private readonly CancellationTokenSource _cts = new();

    public void Dispose()
    {
        _cts.Cancel();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private EventSubClient Client(TwitchOptions? options = null, bool loggedIn = true, bool retry = true)
    {
        var store = Path.Combine(_dir, "twitch.json");
        if (loggedIn)
            TwitchAccountStore.Save(new TwitchAccount("42", "streamer", "", TwitchAuthClient.Scopes, "ACCESS", "REFRESH", DateTimeOffset.UtcNow.AddHours(3)), store);
        var auth = new TwitchAuthClient(new HttpClient(new FakeHelix()), "cid");
        var session = new TwitchSession(auth, store, NullLogger.Instance);
        return new EventSubClient(session, new HttpClient(_helix), options ?? new TwitchOptions { ClientId = "cid" },
            (url, _) =>
            {
                _connected.Enqueue(url);
                return Task.FromResult<IEventSubSocket>(_sockets.TryDequeue(out var s) ? s : throw new IOException("no server"));
            },
            (_, ct) => Task.Delay(retry ? 10 : Timeout.Infinite, ct), NullLogger.Instance);
    }

    private FakeSocket Server()
    {
        var s = new FakeSocket();
        _sockets.Enqueue(s);
        return s;
    }

    private static string Welcome(string session, int keepalive = 30) =>
        "{\"metadata\":{\"message_id\":\"w-" + session + "\",\"message_type\":\"session_welcome\"},\"payload\":{\"session\":{\"id\":\"" + session + "\",\"status\":\"connected\",\"keepalive_timeout_seconds\":" + keepalive + "}}}";

    private static string Chat(string messageId, string text) =>
        "{\"metadata\":{\"message_id\":\"" + messageId + "\",\"message_type\":\"notification\",\"subscription_type\":\"channel.chat.message\"},\"payload\":{\"subscription\":{\"type\":\"channel.chat.message\"},\"event\":{\"broadcaster_user_id\":\"42\",\"chatter_user_id\":\"7\",\"chatter_user_login\":\"widz\",\"chatter_user_name\":\"Widz\",\"message_id\":\"m\",\"message\":{\"text\":\"" + text + "\"},\"badges\":[{\"set_id\":\"subscriber\",\"id\":\"0\"},{\"set_id\":\"vip\",\"id\":\"1\"}]}}}";

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task Subscribes_after_welcome_and_raises_chat_and_redemption_events()
    {
        var server = Server();
        var client = Client();
        var chat = new ConcurrentQueue<ChatMessage>();
        var redemptions = new ConcurrentQueue<ChannelPointsRedemption>();
        client.ChatMessageReceived += chat.Enqueue;
        client.RedemptionReceived += redemptions.Enqueue;
        _ = client.RunAsync(_cts.Token);

        server.Send(Welcome("S1"));
        await Until(() => client.Status.State == EventSubState.Connected);
        server.Send(Chat("n1", "!adoptuj Kraków"));
        server.Send(Chat("n1", "!adoptuj Kraków")); // ta sama wiadomość drugi raz
        server.Send("""{"metadata":{"message_id":"n2","message_type":"notification","subscription_type":"channel.channel_points_custom_reward_redemption.add"},"payload":{"event":{"id":"r1","user_id":"7","user_login":"widz","user_name":"Widz","user_input":"Kraków","status":"unfulfilled","reward":{"id":"rw","title":"Adoptuj miasto","cost":500},"redeemed_at":"2026-10-04T12:00:00Z"}}}""");
        await Until(() => !redemptions.IsEmpty);

        var m = Assert.Single(chat);
        Assert.Equal(("widz", "Widz", "!adoptuj Kraków"), (m.ChatterLogin, m.ChatterName, m.Text));
        Assert.Equal(["subscriber", "vip"], m.Badges);
        var r = Assert.Single(redemptions);
        Assert.Equal(("r1", "Adoptuj miasto", 500, "Kraków"), (r.Id, r.RewardTitle, r.Cost, r.UserInput));

        Assert.Equal(2, _helix.Requests.Count);
        Assert.All(_helix.Requests, req =>
        {
            Assert.Contains("\"session_id\":\"S1\"", req.Body);
            Assert.Contains("\"method\":\"websocket\"", req.Body);
            Assert.Equal("Bearer ACCESS", req.Authorization);
            Assert.Equal("cid", req.ClientId);
        });
        Assert.Contains(_helix.Requests, req => req.Body.Contains("\"type\":\"channel.chat.message\"") && req.Body.Contains("\"user_id\":\"42\""));
        Assert.Contains(_helix.Requests, req => req.Body.Contains("\"type\":\"channel.channel_points_custom_reward_redemption.add\""));
        Assert.Equal(new Uri(EventSubClient.TwitchUrl), Assert.Single(_connected));
    }

    [Fact]
    public async Task Moves_to_the_reconnect_url_without_subscribing_again()
    {
        var first = Server();
        var second = Server();
        var client = Client();
        var chat = new ConcurrentQueue<ChatMessage>();
        client.ChatMessageReceived += chat.Enqueue;
        _ = client.RunAsync(_cts.Token);

        first.Send(Welcome("S1"));
        await Until(() => client.Status.State == EventSubState.Connected);
        second.Send(Welcome("S2"));
        first.Send("""{"metadata":{"message_id":"x","message_type":"session_reconnect"},"payload":{"session":{"id":"S1","status":"reconnecting","reconnect_url":"wss://eventsub.wss.twitch.tv/ws?reconnect=1"}}}""");
        await Until(() => first.Disposed);
        second.Send(Chat("n1", "!test"));
        await Until(() => !chat.IsEmpty);

        Assert.Equal(2, _helix.Requests.Count); // tylko z pierwszego połączenia
        Assert.Equal("wss://eventsub.wss.twitch.tv/ws?reconnect=1", _connected.Last().ToString());
    }

    [Fact]
    public async Task Reconnects_and_subscribes_again_when_keepalives_stop()
    {
        var first = Server();
        var second = Server();
        var client = Client();
        client.KeepaliveGrace = TimeSpan.FromMilliseconds(50);
        _ = client.RunAsync(_cts.Token);

        first.Send(Welcome("S1", keepalive: 0));
        await Until(() => _connected.Count == 2);
        second.Send(Welcome("S2"));
        await Until(() => client.Status.State == EventSubState.Connected && _helix.Requests.Count == 4);

        Assert.True(first.Disposed);
        Assert.Contains("\"session_id\":\"S2\"", _helix.Requests.Last().Body);
    }

    [Fact]
    public async Task Keeps_the_connection_when_only_channel_points_are_rejected()
    {
        _helix.Reject("channel.channel_points_custom_reward_redemption.add", HttpStatusCode.Forbidden, "channel is not affiliate");
        var server = Server();
        var client = Client();
        _ = client.RunAsync(_cts.Token);

        server.Send(Welcome("S1"));
        await Until(() => client.Status.State == EventSubState.Connected);

        Assert.Equal("połączono (czat)", client.Status.Message);
    }

    [Fact]
    public async Task Fails_when_twitch_rejects_every_subscription()
    {
        _helix.Reject("channel.channel_points_custom_reward_redemption.add", HttpStatusCode.Forbidden, "nope");
        _helix.Reject("channel.chat.message", HttpStatusCode.Forbidden, "missing scope");
        var server = Server();
        var client = Client(retry: false);
        _ = client.RunAsync(_cts.Token);

        server.Send(Welcome("S1"));
        await Until(() => client.Status.State == EventSubState.Error && server.Disposed);

        Assert.Contains("missing scope", client.Status.Message);
    }

    [Fact]
    public async Task Test_server_works_without_subscriptions()
    {
        _helix.Reject("channel.channel_points_custom_reward_redemption.add", HttpStatusCode.NotFound, "");
        _helix.Reject("channel.chat.message", HttpStatusCode.NotFound, "");
        var server = Server();
        var client = Client(new TwitchOptions { ClientId = "cid", EventSubUrl = "ws://127.0.0.1:8080/ws", SubscriptionsUrl = "http://127.0.0.1:8080/eventsub/subscriptions" });
        _ = client.RunAsync(_cts.Token);

        server.Send(Welcome("S1"));
        await Until(() => client.Status.State == EventSubState.Connected);

        Assert.True(client.IsMock);
        Assert.Equal("ws://127.0.0.1:8080/ws", Assert.Single(_connected).ToString());
        Assert.All(_helix.Requests, r => Assert.Equal("http://127.0.0.1:8080/eventsub/subscriptions", r.Url));
    }

    [Fact]
    public async Task Waits_for_login()
    {
        var client = Client(loggedIn: false);
        _ = client.RunAsync(_cts.Token);

        await Until(() => client.Status.State == EventSubState.WaitingForLogin);
        Assert.Empty(_connected);
    }

    private sealed class FakeSocket : IEventSubSocket
    {
        private readonly Channel<string?> _messages = Channel.CreateUnbounded<string?>();

        public bool Disposed { get; private set; }

        public string? CloseDescription => null;

        public void Send(string? json) => _messages.Writer.TryWrite(json);

        public async Task<string?> ReceiveAsync(CancellationToken ct) => await _messages.Reader.ReadAsync(ct);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed record Request(string Url, string Body, string? Authorization, string? ClientId);

    private sealed class FakeHelix : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, (HttpStatusCode, string)> _rejected = new();

        public ConcurrentQueue<Request> Requests { get; } = new();

        public void Reject(string type, HttpStatusCode status, string message) => _rejected[type] = (status, message);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Enqueue(new Request(request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("Client-Id", out var v) ? v.Single() : null));
            foreach (var (type, (status, message)) in _rejected)
            {
                if (body.Contains($"\"type\":\"{type}\""))
                    return new HttpResponseMessage(status) { Content = new StringContent($$"""{"error":"x","status":{{(int)status}},"message":"{{message}}"}""", Encoding.UTF8, "application/json") };
            }
            return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("{}") };
        }
    }
}
