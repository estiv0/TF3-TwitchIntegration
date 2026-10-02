using Tf3Bridge.Cloud;

namespace Tf3Bridge.Tests;

public sealed class AzureSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tf3bridge-azure-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData("https://acc.blob.core.windows.net/live", "sv=1&sig=x")]
    [InlineData("https://acc.blob.core.windows.net/live/", "?sv=1&sig=x")]
    [InlineData("https://acc.blob.core.windows.net/live?sv=1&sig=x", "")] // cały "SAS URL" w polu adresu
    public void Builds_container_uri_from_url_and_sas(string url, string sas)
    {
        var s = new AzureSettings(true, url, sas);

        Assert.True(s.TryGetContainerUri(out var uri));
        Assert.Equal("https://acc.blob.core.windows.net/live?sv=1&sig=x", uri.ToString());
        Assert.Equal("https://acc.blob.core.windows.net/live/latest/", s.PublicLatestUrl);
    }

    [Theory]
    [InlineData("", "sv=1")]
    [InlineData("https://acc.blob.core.windows.net/live", "")]
    [InlineData("http://acc.blob.core.windows.net/live", "sv=1")] // SAS tylko po HTTPS
    [InlineData("nie-adres", "sv=1")]
    public void Rejects_incomplete_settings(string url, string sas) =>
        Assert.False(new AzureSettings(true, url, sas).IsComplete);

    [Fact]
    public void Store_round_trips_and_does_not_keep_sas_in_plain_text()
    {
        var path = Path.Combine(_dir, "azure.json");
        var settings = new AzureSettings(true, "https://acc.blob.core.windows.net/live", "sv=2025&sp=cw&sig=SECRET");

        AzureSettingsStore.Save(settings, path);

        Assert.DoesNotContain("SECRET", File.ReadAllText(path));
        Assert.Equal(settings, AzureSettingsStore.Load(path));
    }

    [Fact]
    public void Store_returns_empty_for_missing_or_broken_file()
    {
        Assert.Equal(AzureSettings.Empty, AzureSettingsStore.Load(Path.Combine(_dir, "missing.json")));

        Directory.CreateDirectory(_dir);
        var broken = Path.Combine(_dir, "broken.json");
        File.WriteAllText(broken, "{ not json");
        Assert.Equal(AzureSettings.Empty, AzureSettingsStore.Load(broken));
    }
}
