using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Tf3Bridge.Twitch;

namespace Tf3Bridge.Tests;

public sealed class TwitchAuthTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tf3bridge-twitch-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTwitch _twitch = new();

    private string StorePath => Path.Combine(_dir, "twitch.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private TwitchAuthClient Client() => new(new HttpClient(_twitch), "cid", (_, _) => Task.CompletedTask);

    private static TwitchAccount Account(DateTimeOffset expiresAt, string type = "affiliate") =>
        new("42", "streamer", type, TwitchAuthClient.Scopes, "ACCESS-OLD", "REFRESH-OLD", expiresAt);

    private const string Validate = """{"client_id":"cid","login":"streamer","user_id":"42","scopes":["channel:read:redemptions","channel:manage:redemptions","user:read:chat"],"expires_in":14000}""";
    private const string Tokens = """{"access_token":"ACCESS-NEW","refresh_token":"REFRESH-NEW","expires_in":14000,"scope":["channel:read:redemptions","channel:manage:redemptions","user:read:chat"],"token_type":"bearer"}""";
    private const string Users = """{"data":[{"id":"42","login":"streamer","broadcaster_type":"affiliate"}]}""";

    [Fact]
    public async Task Device_flow_waits_while_authorization_is_pending()
    {
        _twitch.On("device", 200, """{"device_code":"DEV","user_code":"ABCD1234","verification_uri":"https://www.twitch.tv/activate?device-code=ABCD1234","expires_in":1800,"interval":5}""");
        _twitch.On("token", 400, """{"status":400,"message":"authorization_pending"}""");
        _twitch.On("token", 400, """{"status":400,"message":"authorization_pending"}""");
        _twitch.On("token", 200, Tokens);
        var client = Client();

        var code = await client.StartDeviceAsync(default);
        var tokens = await client.PollDeviceAsync(code, default);

        Assert.Equal("ABCD1234", code.UserCode);
        Assert.Equal(TimeSpan.FromSeconds(5), code.Interval);
        Assert.Equal("ACCESS-NEW", tokens.AccessToken);
        Assert.Equal("REFRESH-NEW", tokens.RefreshToken);
        Assert.Equal(TwitchAuthClient.Scopes, tokens.Scopes);
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", _twitch.Bodies.Last());
        Assert.DoesNotContain(_twitch.Bodies, b => b.Contains("client_secret"));
    }

    [Fact]
    public async Task Device_flow_stops_on_other_errors()
    {
        _twitch.On("token", 400, """{"status":400,"message":"invalid device code"}""");
        var code = new DeviceCode("DEV", "X", new Uri("https://www.twitch.tv/activate"), DateTimeOffset.UtcNow.AddMinutes(30), TimeSpan.FromSeconds(5));

        var ex = await Assert.ThrowsAsync<TwitchAuthException>(() => Client().PollDeviceAsync(code, default));

        Assert.Equal("invalid device code", ex.Message);
        Assert.True(ex.Rejected);
    }

    [Fact]
    public async Task Validate_returns_null_for_rejected_token()
    {
        _twitch.On("validate", 401, """{"status":401,"message":"invalid access token"}""");

        Assert.Null(await Client().ValidateAsync("bad", default));
    }

    [Fact]
    public void Store_round_trips_and_does_not_keep_tokens_in_plain_text()
    {
        var account = Account(DateTimeOffset.Parse("2026-10-04T12:00:00Z"));

        TwitchAccountStore.Save(account, StorePath);

        var text = File.ReadAllText(StorePath);
        Assert.DoesNotContain("ACCESS-OLD", text);
        Assert.DoesNotContain("REFRESH-OLD", text);
        var loaded = TwitchAccountStore.Load(StorePath)!;
        Assert.Equal(account.AccessToken, loaded.AccessToken);
        Assert.Equal(account.RefreshToken, loaded.RefreshToken);
        Assert.Equal(account.Scopes, loaded.Scopes);
        Assert.Equal(account.ExpiresAt, loaded.ExpiresAt);
        Assert.DoesNotContain("ACCESS", account.ToString());
    }

    [Fact]
    public async Task Check_refreshes_expired_token_and_stores_the_new_refresh_token()
    {
        TwitchAccountStore.Save(Account(DateTimeOffset.UtcNow.AddMinutes(-1)), StorePath);
        _twitch.On("validate", 401, """{"status":401,"message":"invalid access token"}""");
        _twitch.On("token", 200, Tokens);
        _twitch.On("users", 200, Users);
        var session = new TwitchSession(Client(), StorePath, NullLogger.Instance);

        await session.CheckAsync(default);

        Assert.Equal(TwitchState.Ok, session.Status.State);
        Assert.Contains("refresh_token=REFRESH-OLD", _twitch.Bodies.Single(b => b.Contains("grant_type=refresh_token")));
        Assert.Equal("REFRESH-NEW", TwitchAccountStore.Load(StorePath)!.RefreshToken);
        Assert.Equal("ACCESS-NEW", await session.GetAccessTokenAsync(default));
    }

    [Fact]
    public async Task Rejected_refresh_logs_out_and_deletes_the_file()
    {
        TwitchAccountStore.Save(Account(DateTimeOffset.UtcNow.AddMinutes(-1)), StorePath);
        _twitch.On("validate", 401, """{"status":401,"message":"invalid access token"}""");
        _twitch.On("token", 400, """{"status":400,"message":"Invalid refresh token"}""");
        var session = new TwitchSession(Client(), StorePath, NullLogger.Instance);

        await session.CheckAsync(default);

        Assert.Equal(TwitchState.LoggedOut, session.Status.State);
        Assert.False(session.IsLoggedIn);
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public async Task Network_error_keeps_the_account()
    {
        TwitchAccountStore.Save(Account(DateTimeOffset.UtcNow.AddHours(3)), StorePath);
        _twitch.On("validate", 503, "");
        var session = new TwitchSession(Client(), StorePath, NullLogger.Instance);

        await session.CheckAsync(default);

        Assert.Equal(TwitchState.Error, session.Status.State);
        Assert.True(session.IsLoggedIn);
        Assert.True(File.Exists(StorePath));
    }

    [Fact]
    public async Task Channel_without_affiliate_gets_a_warning()
    {
        TwitchAccountStore.Save(Account(DateTimeOffset.UtcNow.AddHours(3), type: ""), StorePath);
        _twitch.On("validate", 200, Validate);
        _twitch.On("users", 200, """{"data":[{"id":"42","broadcaster_type":""}]}""");
        var session = new TwitchSession(Client(), StorePath, NullLogger.Instance);

        await session.CheckAsync(default);

        Assert.Equal(TwitchState.Warning, session.Status.State);
    }

    [Fact]
    public void Missing_client_id_is_reported()
    {
        var session = new TwitchSession(null, StorePath, NullLogger.Instance);

        Assert.Equal(TwitchState.NotConfigured, session.Status.State);
        Assert.False(session.IsConfigured);
    }

    /// <summary>Answers queued per endpoint (last path segment), in order; records request bodies.</summary>
    private sealed class FakeTwitch : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<(int Status, string Body)>> _responses = [];

        public List<string> Bodies { get; } = [];

        public void On(string endpoint, int status, string body)
        {
            if (!_responses.TryGetValue(endpoint, out var q)) _responses[endpoint] = q = new();
            q.Enqueue((status, body));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            var endpoint = request.RequestUri!.AbsolutePath.Split('/').Last();
            if (!_responses.TryGetValue(endpoint, out var q) || q.Count == 0)
                throw new InvalidOperationException($"Unexpected request to {request.RequestUri}");
            var (status, body) = q.Dequeue();
            return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
