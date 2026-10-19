using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Tf3Bridge.Twitch;

/// <summary>Code the streamer confirms on twitch.tv/activate. <see cref="Code"/> is secret, <see cref="UserCode"/> is shown.</summary>
public sealed record DeviceCode(string Code, string UserCode, Uri VerificationUri, DateTimeOffset ExpiresAt, TimeSpan Interval);

public sealed record TokenResponse(string AccessToken, string RefreshToken, TimeSpan ExpiresIn, string[] Scopes);

/// <summary>Result of /oauth2/validate: who the token belongs to.</summary>
public sealed record TokenInfo(string ClientId, string UserId, string Login, string[] Scopes, TimeSpan ExpiresIn);

/// <summary>Twitch answered with an error. <see cref="Rejected"/>: it refused the token/grant itself, so retrying won't help.</summary>
public sealed class TwitchAuthException(string message, bool rejected, int? httpStatus = null) : Exception(message)
{
    public bool Rejected { get; } = rejected;

    /// <summary>HTTP status of a rejected Helix call (e.g. 403); null otherwise.</summary>
    public int? HttpStatus { get; } = httpStatus;
}

/// <summary>
/// Twitch OAuth for a public client (no client secret): Device Code Grant Flow, refresh,
/// validate, revoke. Only user access tokens: EventSub over WebSocket and the channel points
/// endpoints do not accept app access tokens.
/// </summary>
public sealed class TwitchAuthClient
{
    /// <summary>Channel points (read + manage rewards), reading chat through EventSub and answering viewers in chat.</summary>
    public static readonly string[] Scopes = ["channel:read:redemptions", "channel:manage:redemptions", "user:read:chat", "user:write:chat"];

    private const string IdBase = "https://id.twitch.tv/oauth2/";
    private const string HelixBase = "https://api.twitch.tv/helix/";

    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public string ClientId { get; }

    /// <param name="delay">Wait between device code polls; replaced in tests.</param>
    public TwitchAuthClient(HttpClient http, string clientId, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        ClientId = clientId;
        _delay = delay ?? Task.Delay;
    }

    public async Task<DeviceCode> StartDeviceAsync(CancellationToken ct)
    {
        using var json = await SendAsync(Form("device", new()
        {
            ["client_id"] = ClientId,
            ["scopes"] = string.Join(' ', Scopes),
        }), ct);
        var r = json.RootElement;
        return new DeviceCode(
            r.GetProperty("device_code").GetString()!,
            r.GetProperty("user_code").GetString()!,
            new Uri(r.GetProperty("verification_uri").GetString()!),
            DateTimeOffset.UtcNow.AddSeconds(r.GetProperty("expires_in").GetInt32()),
            TimeSpan.FromSeconds(Math.Max(1, r.TryGetProperty("interval", out var i) ? i.GetInt32() : 5)));
    }

    /// <summary>Polls until the streamer confirms the code (or it expires / is denied).</summary>
    public async Task<TokenResponse> PollDeviceAsync(DeviceCode code, CancellationToken ct)
    {
        var interval = code.Interval;
        while (true)
        {
            if (DateTimeOffset.UtcNow >= code.ExpiresAt)
                throw new TwitchAuthException("Kod wygasł, zaloguj się jeszcze raz.", rejected: true);
            await _delay(interval, ct);
            try
            {
                return await TokenAsync(new()
                {
                    ["client_id"] = ClientId,
                    ["scopes"] = string.Join(' ', Scopes),
                    ["device_code"] = code.Code,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                }, ct);
            }
            catch (TwitchAuthException ex) when (ex.Message == "authorization_pending") { }
            catch (TwitchAuthException ex) when (ex.Message == "slow_down") { interval += TimeSpan.FromSeconds(5); }
        }
    }

    /// <summary>Refresh tokens of public clients are single-use: always store the returned one.</summary>
    public Task<TokenResponse> RefreshAsync(string refreshToken, CancellationToken ct) => TokenAsync(new()
    {
        ["client_id"] = ClientId,
        ["grant_type"] = "refresh_token",
        ["refresh_token"] = refreshToken,
    }, ct);

    /// <summary>Null when the token is no longer valid (401).</summary>
    public async Task<TokenInfo?> ValidateAsync(string accessToken, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, IdBase + "validate");
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", accessToken);
        try
        {
            using var json = await SendAsync(request, ct);
            var r = json.RootElement;
            return new TokenInfo(
                r.GetProperty("client_id").GetString()!,
                r.GetProperty("user_id").GetString()!,
                r.GetProperty("login").GetString()!,
                ReadScopes(r, "scopes"),
                TimeSpan.FromSeconds(r.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 0));
        }
        catch (TwitchAuthException ex) when (ex.Rejected) { return null; }
    }

    /// <summary>"affiliate", "partner" or "" (channel points need one of the first two).</summary>
    public async Task<string> GetBroadcasterTypeAsync(string accessToken, string userId, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, HelixBase + "users?id=" + Uri.EscapeDataString(userId));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Client-Id", ClientId);
        using var json = await SendAsync(request, ct);
        var data = json.RootElement.GetProperty("data");
        return data.GetArrayLength() > 0 && data[0].TryGetProperty("broadcaster_type", out var t) ? t.GetString() ?? "" : "";
    }

    public async Task RevokeAsync(string accessToken, CancellationToken ct)
    {
        using var _ = await SendAsync(Form("revoke", new() { ["client_id"] = ClientId, ["token"] = accessToken }), ct, expectJson: false);
    }

    private async Task<TokenResponse> TokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var json = await SendAsync(Form("token", form), ct);
        var r = json.RootElement;
        var expires = r.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 0;
        return new TokenResponse(
            r.GetProperty("access_token").GetString()!,
            r.GetProperty("refresh_token").GetString()!,
            TimeSpan.FromSeconds(expires > 0 ? expires : 3600),
            ReadScopes(r, "scope"));
    }

    private static HttpRequestMessage Form(string endpoint, Dictionary<string, string> values) =>
        new(HttpMethod.Post, IdBase + endpoint) { Content = new FormUrlEncodedContent(values) };

    // Twitch errors look like {"status":400,"message":"authorization_pending"}; message goes into the exception.
    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken ct, bool expectJson = true)
    {
        using (request)
        using (var response = await _http.SendAsync(request, ct))
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var message = $"HTTP {(int)response.StatusCode}";
                try
                {
                    using var err = JsonDocument.Parse(body);
                    if (err.RootElement.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } text) message = text;
                }
                catch (JsonException) { }
                var rejected = response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
                throw new TwitchAuthException(message, rejected);
            }
            return expectJson ? JsonDocument.Parse(body) : JsonDocument.Parse("{}");
        }
    }

    private static string[] ReadScopes(JsonElement root, string name) =>
        root.TryGetProperty(name, out var s) && s.ValueKind == JsonValueKind.Array
            ? [.. s.EnumerateArray().Select(x => x.GetString() ?? "")]
            : [];
}
