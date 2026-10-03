namespace Tf3Bridge.Tests;

public sealed class SteamLocatorTests : IDisposable
{
    private readonly string _steam = Path.Combine(Path.GetTempPath(), "tf3bridge-steam-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_steam)) Directory.Delete(_steam, recursive: true);
    }

    private string Local(string account) => Path.Combine(_steam, "userdata", account, SteamLocator.Tf3AppId, "local");

    private string MakeLocal(string account, bool withExport = false, DateTime? touched = null)
    {
        var local = Local(account);
        var dir = withExport ? Path.Combine(local, "tf3export") : local;
        Directory.CreateDirectory(dir);
        if (touched is { } t) Directory.SetLastWriteTimeUtc(dir, t);
        return local;
    }

    [Fact]
    public void Returns_null_without_tf3_data()
    {
        Directory.CreateDirectory(Path.Combine(_steam, "userdata", "123", "730"));

        Assert.Null(SteamLocator.FindInputDir([_steam, Path.Combine(_steam, "missing")], null));
    }

    [Fact]
    public void Prefers_the_active_account()
    {
        MakeLocal("111", withExport: true);
        var active = MakeLocal("222");

        Assert.Equal(Path.Combine(active, "tf3export"), SteamLocator.FindInputDir([_steam], 222));
    }

    [Fact]
    public void Ignores_active_account_without_tf3_and_picks_the_one_with_mod_output()
    {
        MakeLocal("111", touched: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        var withExport = MakeLocal("222", withExport: true, touched: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.CreateDirectory(Path.Combine(_steam, "userdata", "333"));

        Assert.Equal(Path.Combine(withExport, "tf3export"), SteamLocator.FindInputDir([_steam], 333));
    }

    [Fact]
    public void Picks_the_most_recently_used_account()
    {
        MakeLocal("111", withExport: true, touched: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var recent = MakeLocal("222", withExport: true, touched: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(Path.Combine(recent, "tf3export"), SteamLocator.FindInputDir([_steam], null));
    }

    [Fact]
    public void Skips_non_account_folders()
    {
        Directory.CreateDirectory(Path.Combine(_steam, "userdata", "0", SteamLocator.Tf3AppId, "local"));
        Directory.CreateDirectory(Path.Combine(_steam, "userdata", "ac", SteamLocator.Tf3AppId, "local"));

        Assert.Null(SteamLocator.FindInputDir([_steam], 0));
    }
}
