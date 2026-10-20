using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Tf3Bridge;

/// <summary>
/// Finds the folder the mod writes to: &lt;Steam&gt;\userdata\&lt;account id&gt;\3493540\local\towns_industries.
/// Steam path and the logged-in account come from the registry; without a logged-in Steam the account
/// is the one whose TF3 folder was used most recently.
/// </summary>
public static class SteamLocator
{
    public const string Tf3AppId = "3493540";

    /// <summary>
    /// Since build 40420 the game allows app.saveUserdata/loadUserdata only in towns_industries, heightmaps
    /// and mod_presets, so the mod writes everything (exports, command queue, results, settings) to
    /// towns_industries, and our files are told apart from the map editor's by <see cref="FilePrefix"/>.
    /// Same values as util.USERDATA_DIR / util.FILE_PREFIX in the mod.
    /// </summary>
    public const string UserdataDir = "towns_industries";
    public const string FilePrefix = "tf3export_";

    private const string DefaultSteamPath = @"C:\Program Files (x86)\Steam";

    /// <summary>Input folder for the current machine, or null when no Steam account has TF3 data.</summary>
    public static string? FindInputDir() => FindInputDir(ReadSteamPaths(), ReadActiveUser());

    /// <summary>
    /// First Steam install with a userdata\&lt;id&gt;\3493540 folder wins. Within it: the active
    /// account if it has TF3 data, otherwise the most recently used towns_industries (or TF3 "local") folder.
    /// </summary>
    public static string? FindInputDir(IEnumerable<string> steamPaths, uint? activeUser)
    {
        foreach (var steam in steamPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var userdata = Path.Combine(steam, "userdata");
            if (!Directory.Exists(userdata)) continue;

            var locals = Directory.EnumerateDirectories(userdata)
                .Where(d => uint.TryParse(Path.GetFileName(d), out var id) && id != 0)
                .Select(d => Path.Combine(d, Tf3AppId, "local"))
                .Where(Directory.Exists)
                .ToList();
            if (locals.Count == 0) continue;

            if (activeUser is > 0)
            {
                var active = Path.Combine(userdata, activeUser.Value.ToString(), Tf3AppId, "local");
                if (Directory.Exists(active)) return Path.Combine(active, UserdataDir);
            }

            var best = locals
                .OrderByDescending(l => Directory.Exists(Path.Combine(l, UserdataDir)))
                .ThenByDescending(l => Directory.GetLastWriteTimeUtc(
                    Directory.Exists(Path.Combine(l, UserdataDir)) ? Path.Combine(l, UserdataDir) : l))
                .First();
            return Path.Combine(best, UserdataDir);
        }
        return null;
    }

    /// <summary>The game install (the folder with base\content), or null when no Steam library has TF3.</summary>
    public static string? FindGameDir() => FindGameDir(ReadSteamPaths());

    /// <summary>
    /// Every library from steamapps\libraryfolders.vdf of each Steam install; in it the folder named by
    /// appmanifest_3493540.acf ("installdir"), or "Transport Fever 3".
    /// </summary>
    public static string? FindGameDir(IEnumerable<string> steamPaths)
    {
        var libraries = new List<string>();
        foreach (var steam in steamPaths)
        {
            libraries.Add(steam);
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf)) libraries.AddRange(VdfValues(File.ReadAllText(vdf), "path"));
        }
        foreach (var library in libraries.Select(l => Path.GetFullPath(l)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(library, "steamapps");
            var manifest = Path.Combine(apps, $"appmanifest_{Tf3AppId}.acf");
            var installDir = File.Exists(manifest) ? VdfValues(File.ReadAllText(manifest), "installdir").FirstOrDefault() : null;
            var game = Path.Combine(apps, "common", installDir ?? "Transport Fever 3");
            if (Directory.Exists(Path.Combine(game, "base", "content"))) return game;
        }
        return null;
    }

    // Values of "key" "value" pairs in Valve's KeyValues text format (a backslash is written as two).
    internal static IEnumerable<string> VdfValues(string vdf, string key) =>
        Regex.Matches(vdf, $@"""{Regex.Escape(key)}""\s+""((?:[^""\\]|\\.)*)""", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value.Replace(@"\\", @"\"));

    private static List<string> ReadSteamPaths()
    {
        var paths = new List<string>();
        void Add(string? p)
        {
            if (!string.IsNullOrWhiteSpace(p)) paths.Add(Path.GetFullPath(p.Replace('/', '\\')));
        }
        Add(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string);
        Add(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string);
        Add(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string);
        Add(DefaultSteamPath);
        return paths;
    }

    // The logged-in Steam account (account id = folder name in userdata); 0 when Steam isn't running.
    private static uint? ReadActiveUser() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null) is int id
            ? unchecked((uint)id)
            : null;
}
