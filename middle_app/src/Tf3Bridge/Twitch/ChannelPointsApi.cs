using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tf3Bridge.Twitch;

/// <summary>A custom reward on the channel as Twitch reports it.</summary>
public sealed record TwitchReward(string Id, string Title, bool IsEnabled, bool IsPaused, int Cost = 0);

public sealed record PendingRedemption(string Id, string RewardId, string UserLogin, string UserName, string UserInput, DateTimeOffset RedeemedAt);

/// <summary>Helix channel points: managing our rewards and closing redemptions (FULFILLED / CANCELED = refund).</summary>
public sealed class ChannelPointsApi(TwitchSession session, HttpClient http)
{
    private const string Base = "https://api.twitch.tv/helix/channel_points/";

    /// <param name="onlyManageable">true = only rewards created by this client ID (the only ones it may change);
    /// false = also the ones the streamer made by hand in the Twitch dashboard.</param>
    public async Task<List<TwitchReward>> GetRewardsAsync(bool onlyManageable, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get,
            $"custom_rewards?broadcaster_id={Broadcaster}" + (onlyManageable ? "&only_manageable_rewards=true" : ""), null, ct);
        return [.. json["data"]!.AsArray().Select(r => new TwitchReward(
            (string)r!["id"]!, (string)r["title"]!, (bool?)r["is_enabled"] ?? true, (bool?)r["is_paused"] ?? false, (int?)r["cost"] ?? 0))];
    }

    /// <summary>Creates a reward; returns its ID.</summary>
    public async Task<string> CreateRewardAsync(JsonObject body, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post, $"custom_rewards?broadcaster_id={Broadcaster}", body, ct);
        return (string)json["data"]![0]!["id"]!;
    }

    public Task UpdateRewardAsync(string rewardId, JsonObject body, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"custom_rewards?broadcaster_id={Broadcaster}&id={Uri.EscapeDataString(rewardId)}", body, ct);

    public async Task<List<PendingRedemption>> GetUnfulfilledAsync(string rewardId, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get,
            $"custom_rewards/redemptions?broadcaster_id={Broadcaster}&reward_id={Uri.EscapeDataString(rewardId)}&status=UNFULFILLED&first=50", null, ct);
        return [.. json["data"]!.AsArray().Select(r => new PendingRedemption(
            (string)r!["id"]!, rewardId, (string?)r["user_login"] ?? "", (string?)r["user_name"] ?? "", (string?)r["user_input"] ?? "",
            DateTimeOffset.TryParse((string?)r["redeemed_at"], out var at) ? at : DateTimeOffset.UtcNow))];
    }

    /// <param name="fulfilled">true = FULFILLED, false = CANCELED (Twitch refunds the points).</param>
    public Task SetRedemptionStatusAsync(string rewardId, string redemptionId, bool fulfilled, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch,
            $"custom_rewards/redemptions?broadcaster_id={Broadcaster}&reward_id={Uri.EscapeDataString(rewardId)}&id={Uri.EscapeDataString(redemptionId)}",
            new JsonObject { ["status"] = fulfilled ? "FULFILLED" : "CANCELED" }, ct);

    private string Broadcaster => Uri.EscapeDataString(session.UserId ?? throw new TwitchAuthException("niezalogowano", rejected: false));

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await session.GetAccessTokenAsync(ct, forceRefresh: attempt > 0)
                ?? throw new TwitchAuthException("niezalogowano", rejected: false);
            using var request = new HttpRequestMessage(method, Base + path);
            if (body is not null) request.Content = JsonContent.Create(body);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Client-Id", session.ClientId);
            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode) return text.Length > 0 ? JsonNode.Parse(text) ?? new JsonObject() : new JsonObject();
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt == 0) continue;
            var message = text;
            try { message = (string?)JsonNode.Parse(text)?["message"] ?? text; }
            catch (JsonException) { }
            throw new TwitchAuthException($"HTTP {(int)response.StatusCode}: {message}", rejected: false, (int)response.StatusCode);
        }
    }
}
