using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tf3Bridge.Twitch;

/// <summary>Configuration section "Twitch" in appsettings.json. The client ID is public (no secret: public client).</summary>
public sealed class TwitchOptions
{
    public const string Section = "Twitch";

    public string ClientId { get; set; } = "";

    /// <summary>Public site; chat answers link to the object there (#town=…, #industry=…, #line=…). Empty = no links.</summary>
    public string SiteUrl { get; set; } = "";

    /// <summary>EventSub WebSocket; empty = Twitch. For the Twitch CLI mock server: ws://127.0.0.1:8080/ws.</summary>
    public string EventSubUrl { get; set; } = "";

    /// <summary>Where subscriptions are created; empty = Helix. Twitch CLI: http://127.0.0.1:8080/eventsub/subscriptions.</summary>
    public string SubscriptionsUrl { get; set; } = "";
}

/// <summary>The logged-in streamer and their user access token.</summary>
public sealed record TwitchAccount(
    string UserId, string Login, string BroadcasterType, string[] Scopes,
    string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt)
{
    /// <summary>Custom rewards can only be created on Affiliate/Partner channels.</summary>
    public bool CanUseChannelPoints => BroadcasterType is "affiliate" or "partner";

    public bool HasScopes(IEnumerable<string> required) => required.All(Scopes.Contains);

    // Domyślny ToString rekordu wypisałby tokeny (np. do logu).
    public override string ToString() => $"{Login} ({UserId}, {BroadcasterType})";
}

/// <summary>
/// %LOCALAPPDATA%\Tf3Bridge\twitch.json. Both tokens are encrypted with DPAPI (CurrentUser),
/// like the SAS in azure.json; the rest (login, ID, scopes) is plain text.
/// </summary>
public static class TwitchAccountStore
{
    private static readonly byte[] Entropy = "Tf3Bridge.Twitch"u8.ToArray();

    public static string FilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tf3Bridge", "twitch.json");

    public static TwitchAccount? Load(string? path = null)
    {
        path ??= FilePath;
        if (!File.Exists(path)) return null;
        try
        {
            var f = JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(path));
            if (f is null || string.IsNullOrEmpty(f.TokensProtected)) return null;
            var raw = ProtectedData.Unprotect(Convert.FromBase64String(f.TokensProtected), Entropy, DataProtectionScope.CurrentUser);
            var t = JsonSerializer.Deserialize<StoredTokens>(Encoding.UTF8.GetString(raw));
            if (t is null) return null;
            return new TwitchAccount(f.UserId, f.Login, f.BroadcasterType ?? "", f.Scopes ?? [], t.Access, t.Refresh, f.ExpiresAt);
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or FormatException or IOException)
        {
            return null; // uszkodzony albo z innego konta - trzeba zalogować się od nowa
        }
    }

    public static void Save(TwitchAccount account, string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tokens = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new StoredTokens(account.AccessToken, account.RefreshToken)));
        var file = new StoredFile(account.UserId, account.Login, account.BroadcasterType, account.Scopes, account.ExpiresAt,
            Convert.ToBase64String(ProtectedData.Protect(tokens, Entropy, DataProtectionScope.CurrentUser)));
        // Najpierw plik tymczasowy: refresh token jest jednorazowy, więc urwany zapis = ponowne logowanie.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }

    public static void Delete(string? path = null)
    {
        path ??= FilePath;
        if (File.Exists(path)) File.Delete(path);
    }

    private sealed record StoredFile(string UserId, string Login, string? BroadcasterType, string[]? Scopes,
        DateTimeOffset ExpiresAt, string? TokensProtected);

    private sealed record StoredTokens(string Access, string Refresh);
}
