using Microsoft.Win32;

namespace Tf3Bridge;

/// <summary>
/// Finds the folder the mod writes to: &lt;Steam&gt;\userdata\&lt;account id&gt;\3493540\local\tf3export.
/// Steam path and the logged-in account come from the registry; without a logged-in Steam the account
/// is the one whose TF3 folder was used most recently.
/// </summary>
public static class SteamLocator
{
    public const string Tf3AppId = "3493540";
    private const string DefaultSteamPath = @"C:\Program Files (x86)\Steam";

    /// <summary>Input folder for the current machine, or null when no Steam account has TF3 data.</summary>
    public static string? FindInputDir() => FindInputDir(ReadSteamPaths(), ReadActiveUser());

    /// <summary>
    /// First Steam install with a userdata\&lt;id&gt;\3493540 folder wins. Within it: the active
    /// account if it has TF3 data, otherwise the most recently used tf3export (or TF3 "local") folder.
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
                if (Directory.Exists(active)) return Path.Combine(active, "tf3export");
            }

            var best = locals
                .OrderByDescending(l => Directory.Exists(Path.Combine(l, "tf3export")))
                .ThenByDescending(l => Directory.GetLastWriteTimeUtc(
                    Directory.Exists(Path.Combine(l, "tf3export")) ? Path.Combine(l, "tf3export") : l))
                .First();
            return Path.Combine(best, "tf3export");
        }
        return null;
    }

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

    // Zalogowane konto Steam (account id = nazwa folderu w userdata); 0, gdy Steam nie działa.
    private static uint? ReadActiveUser() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", null) is int id
            ? unchecked((uint)id)
            : null;
}
