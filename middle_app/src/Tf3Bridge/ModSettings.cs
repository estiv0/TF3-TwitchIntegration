using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Tf3Bridge;

/// <summary>
/// Mod settings edited in the bridge window. The mod can only read files through
/// app.loadUserdata, so they are written as a userdata file in the export folder
/// (local\towns_industries\tf3export_settings.lua); the mod re-reads it every few seconds, so changes
/// apply without restarting the game. Keys match <c>cfg</c> in tf3export.script.lua, which
/// also clamps every value to the ranges below.
/// </summary>
public sealed record ModSettings(
    int DynamicEveryDays = 2,
    int MinDynamicWallSec = 10,
    int DynamicBudgetMs = 3,
    bool Vehicles = true,
    bool VehicleDetails = true,
    bool Terrain = true,
    int TerrainStepM = 20)
{
    public static readonly ModSettings Default = new();

    public static readonly (int Min, int Max) DynamicEveryDaysRange = (1, 30);
    public static readonly (int Min, int Max) MinDynamicWallSecRange = (2, 300);
    public static readonly (int Min, int Max) DynamicBudgetMsRange = (1, 10);
    public static readonly (int Min, int Max) TerrainStepMRange = (5, 100);
}

public static partial class ModSettingsStore
{
    public const string FileName = SteamLocator.FilePrefix + "settings";

    /// <summary>The settings file sits in the export folder; null when that folder is unknown.</summary>
    public static string? PathFor(string inputDir) =>
        string.IsNullOrWhiteSpace(inputDir) ? null : Path.Combine(Path.GetFullPath(inputDir), FileName + ".lua");

    /// <summary>Missing file or keys fall back to the defaults (the mod does the same).</summary>
    public static ModSettings Load(string path)
    {
        if (!File.Exists(path)) return ModSettings.Default;
        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException) { return ModSettings.Default; }
        return Parse(text);
    }

    public static ModSettings Parse(string lua)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Assignment().Matches(lua)) values[m.Groups[1].Value] = m.Groups[2].Value;

        var d = ModSettings.Default;
        int Int(string key, int fallback, (int Min, int Max) range) =>
            values.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? Math.Clamp(n, range.Min, range.Max) : fallback;
        bool Bool(string key, bool fallback) =>
            values.TryGetValue(key, out var v) && v is "true" or "false" ? v == "true" : fallback;

        return new ModSettings(
            Int("dynamicEveryDays", d.DynamicEveryDays, ModSettings.DynamicEveryDaysRange),
            Int("minDynamicWallSec", d.MinDynamicWallSec, ModSettings.MinDynamicWallSecRange),
            Int("dynamicBudgetMs", d.DynamicBudgetMs, ModSettings.DynamicBudgetMsRange),
            Bool("vehicles", d.Vehicles),
            Bool("vehicleDetails", d.VehicleDetails),
            Bool("terrain", d.Terrain),
            Int("terrainStepM", d.TerrainStepM, ModSettings.TerrainStepMRange));
    }

    /// <summary>Same layout app.saveUserdata produces, so app.loadUserdata reads it like its own file.</summary>
    public static string ToLua(ModSettings s)
    {
        static string B(bool v) => v ? "true" : "false";
        var sb = new StringBuilder();
        sb.Append("-- Written by Tf3Bridge (\"Ustawienia moda\"); the mod re-reads it every few seconds.\r\n");
        sb.Append("function data()\r\nreturn {\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"\tdynamicEveryDays = {s.DynamicEveryDays},\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"\tminDynamicWallSec = {s.MinDynamicWallSec},\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"\tdynamicBudgetMs = {s.DynamicBudgetMs},\r\n");
        sb.Append($"\tvehicles = {B(s.Vehicles)},\r\n");
        sb.Append($"\tvehicleDetails = {B(s.VehicleDetails)},\r\n");
        sb.Append($"\tterrain = {B(s.Terrain)},\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"\tterrainStepM = {s.TerrainStepM},\r\n");
        sb.Append("}\r\nend\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// Writes the defaults when there is no settings file yet, so the mod always finds one
    /// (a missing file makes the game log a userdata error). Best effort: failures are ignored.
    /// </summary>
    public static void EnsureExists(string inputDir)
    {
        var path = PathFor(inputDir);
        if (path is null || File.Exists(path)) return;
        try { Save(ModSettings.Default, path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Written to a temp file and moved over, so the game never reads half a file.</summary>
    public static void Save(ModSettings settings, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, ToLua(settings), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    [GeneratedRegex(@"^\s*(\w+)\s*=\s*(true|false|-?\d+)\s*,?\s*$", RegexOptions.Multiline)]
    private static partial Regex Assignment();
}
