using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tf3Bridge.Twitch;

/// <summary>
/// Sends chat messages on the streamer's channel as the streamer (Helix POST /chat/messages,
/// user token with user:write:chat). Used only to answer viewers; chat never changes the game.
/// </summary>
public sealed class ChatApi(TwitchSession session, HttpClient http)
{
    public const int MaxLength = 500;
    private const string Url = "https://api.twitch.tv/helix/chat/messages";

    public async Task SendAsync(string message, CancellationToken ct)
    {
        var userId = session.UserId ?? throw new TwitchAuthException("niezalogowano", rejected: false);
        if (message.Length > MaxLength) message = message[..(MaxLength - 1)] + "…";
        for (var attempt = 0; ; attempt++)
        {
            var token = await session.GetAccessTokenAsync(ct, forceRefresh: attempt > 0)
                ?? throw new TwitchAuthException("niezalogowano", rejected: false);
            using var request = new HttpRequestMessage(HttpMethod.Post, Url)
            {
                Content = JsonContent.Create(new { broadcaster_id = userId, sender_id = userId, message }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Client-Id", session.ClientId);
            using var response = await http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && attempt == 0) continue;
            JsonNode? json = null;
            try { json = JsonNode.Parse(text); } catch (JsonException) { }
            if (!response.IsSuccessStatusCode)
                throw new TwitchAuthException($"HTTP {(int)response.StatusCode}: {(string?)json?["message"] ?? text}", rejected: false);
            // Twitch przyjmuje żądanie, ale może odrzucić samą wiadomość (np. AutoMod).
            var sent = json?["data"]?[0];
            if (sent is not null && (bool?)sent["is_sent"] == false)
                throw new TwitchAuthException($"wiadomość odrzucona: {(string?)sent["drop_reason"]?["message"] ?? "bez powodu"}", rejected: false);
            return;
        }
    }
}
