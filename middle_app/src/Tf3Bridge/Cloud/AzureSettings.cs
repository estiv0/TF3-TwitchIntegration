using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tf3Bridge.Cloud;

/// <summary>Where to send data in Azure. Set in the window, never in appsettings.json (that one is in git).</summary>
public sealed record AzureSettings(bool Enabled, string ContainerUrl, string Sas)
{
    public static readonly AzureSettings Empty = new(false, "", "");

    public bool IsComplete => TryGetContainerUri(out _);

    /// <summary>
    /// Container URL + SAS as one URI. Accepts the SAS with or without a leading '?', and a
    /// "SAS URL" pasted whole into the URL field (then the SAS field may stay empty).
    /// </summary>
    public bool TryGetContainerUri(out Uri uri)
    {
        uri = null!;
        var url = ContainerUrl.Trim();
        var sas = Sas.Trim().TrimStart('?');
        var q = url.IndexOf('?');
        if (q >= 0)
        {
            if (sas.Length == 0) sas = url[(q + 1)..];
            url = url[..q];
        }
        url = url.TrimEnd('/');
        if (sas.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
            return false;
        uri = new Uri($"{url}?{sas}");
        return true;
    }

    /// <summary>Public URL the site reads from (container + "/latest/").</summary>
    public string PublicLatestUrl
    {
        get
        {
            var url = ContainerUrl.Trim();
            var q = url.IndexOf('?');
            if (q >= 0) url = url[..q];
            return url.TrimEnd('/') + "/latest/";
        }
    }
}

/// <summary>
/// %LOCALAPPDATA%\Tf3Bridge\azure.json. The SAS token is encrypted with DPAPI (CurrentUser),
/// so the file is useless on another machine or Windows account.
/// </summary>
public static class AzureSettingsStore
{
    private static readonly byte[] Entropy = "Tf3Bridge.Azure"u8.ToArray();

    public static string FilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tf3Bridge", "azure.json");

    public static AzureSettings Load(string? path = null)
    {
        path ??= FilePath;
        if (!File.Exists(path)) return AzureSettings.Empty;
        try
        {
            var f = JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(path));
            if (f is null) return AzureSettings.Empty;
            var sas = string.IsNullOrEmpty(f.SasProtected)
                ? ""
                : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(f.SasProtected), Entropy, DataProtectionScope.CurrentUser));
            return new AzureSettings(f.Enabled, f.ContainerUrl ?? "", sas);
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or FormatException)
        {
            return AzureSettings.Empty; // corrupted or from another account - has to be entered again
        }
    }

    public static void Save(AzureSettings settings, string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var sas = settings.Sas.Length == 0
            ? ""
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(settings.Sas), Entropy, DataProtectionScope.CurrentUser));
        var json = JsonSerializer.Serialize(new StoredFile(settings.Enabled, settings.ContainerUrl, sas),
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private sealed record StoredFile(bool Enabled, string? ContainerUrl, string? SasProtected);
}
