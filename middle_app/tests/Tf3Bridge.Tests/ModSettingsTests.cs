namespace Tf3Bridge.Tests;

public sealed class ModSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tf3bridge-modsettings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Store_round_trips()
    {
        var path = Path.Combine(_dir, "settings.lua");
        var settings = new ModSettings(5, 30, 2, Vehicles: false, Terrain: false, TerrainStepM: 40);

        ModSettingsStore.Save(settings, path);

        Assert.Equal(settings, ModSettingsStore.Load(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Writes_a_data_function_like_saveUserdata()
    {
        var lua = ModSettingsStore.ToLua(ModSettings.Default);

        Assert.Contains("function data()\r\nreturn {\r\n", lua);
        Assert.Contains("\tvehicles = true,\r\n", lua);
        Assert.EndsWith("}\r\nend\r\n", lua);
    }

    [Fact]
    public void Missing_file_gives_defaults() =>
        Assert.Equal(ModSettings.Default, ModSettingsStore.Load(Path.Combine(_dir, "none.lua")));

    [Fact]
    public void Missing_or_invalid_keys_fall_back_and_numbers_are_clamped()
    {
        var s = ModSettingsStore.Parse("return {\n\tdynamicEveryDays = 999,\n\tterrain = maybe,\n\tterrainStepM = 1,\n}");

        Assert.Equal(ModSettings.DynamicEveryDaysRange.Max, s.DynamicEveryDays);
        Assert.Equal(ModSettings.Default.Terrain, s.Terrain);
        Assert.Equal(ModSettings.TerrainStepMRange.Min, s.TerrainStepM);
        Assert.Equal(ModSettings.Default.MinDynamicWallSec, s.MinDynamicWallSec);
    }

    [Fact]
    public void Settings_file_sits_next_to_the_export_folder()
    {
        var local = Path.Combine(_dir, "local");

        Assert.Equal(Path.Combine(local, "tf3export_settings", "settings.lua"),
            ModSettingsStore.PathFor(Path.Combine(local, "tf3export") + Path.DirectorySeparatorChar));
        Assert.Null(ModSettingsStore.PathFor(""));
    }
}
