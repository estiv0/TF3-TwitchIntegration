using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Tf3Bridge;

public enum ModInstallStatus { Installed, Updated, UpToDate, DevCopy, NoSteam, Failed }

public sealed record ModInstallResult(ModInstallStatus Status, string? ModDir, int ChangedFiles = 0);

/// <summary>
/// Keeps the mod embedded in the exe (lua_mod\ at build time) installed in
/// &lt;Steam&gt;\userdata\&lt;account&gt;\3493540\local\staging_area\&lt;modId&gt;: new or changed files are written,
/// .lua files that are no longer part of the mod are removed. A new Tf3Bridge.exe therefore also
/// updates the mod. A dev copy (junction to the repo) is left alone.
/// </summary>
public static class ModInstaller
{
    private const string ResourcePrefix = "mod/";

    /// <summary>Mod files embedded in the exe: path relative to the mod folder ("/" separators) → content.</summary>
    public static IReadOnlyDictionary<string, byte[]> EmbeddedFiles { get; } = LoadEmbedded();

    /// <summary>Syncs the embedded mod into the TF3 "local" folder that is the parent of <paramref name="inputDir"/>.</summary>
    public static ModInstallResult Sync(string inputDir, ILogger logger)
    {
        var local = string.IsNullOrEmpty(inputDir) ? null : Path.GetDirectoryName(inputDir);
        if (local is null || !Directory.Exists(local)
            || Path.GetFileName(Path.GetDirectoryName(local)) != SteamLocator.Tf3AppId)
        {
            logger.LogWarning("Mod not installed: no Transport Fever 3 folder in Steam (run the game once)");
            return new ModInstallResult(ModInstallStatus.NoSteam, null);
        }

        try
        {
            var result = Sync(local, EmbeddedFiles);
            switch (result.Status)
            {
                case ModInstallStatus.DevCopy:
                    logger.LogInformation("Mod: dev copy in {Dir}, not installing", result.ModDir);
                    break;
                case ModInstallStatus.UpToDate:
                    logger.LogInformation("Mod up to date: {Dir}", result.ModDir);
                    break;
                case ModInstallStatus.Installed:
                    logger.LogInformation("Mod installed: {Dir}. Enable \"TF3 Data Export\" when creating or loading a game", result.ModDir);
                    break;
                case ModInstallStatus.Updated:
                    logger.LogInformation("Mod updated ({Count} files): {Dir}", result.ChangedFiles, result.ModDir);
                    break;
            }
            if (result.Status is ModInstallStatus.Installed or ModInstallStatus.Updated && IsGameRunning())
                logger.LogWarning("Transport Fever 3 is running - reload the save to use the new mod version");
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Mod install failed");
            return new ModInstallResult(ModInstallStatus.Failed, null);
        }
    }

    /// <summary>Core of <see cref="Sync(string, ILogger)"/>: <paramref name="local"/> = ...\3493540\local.</summary>
    public static ModInstallResult Sync(string local, IReadOnlyDictionary<string, byte[]> files)
    {
        // staging_area, not mods: from mods\ the game only shows Workshop/verified mods
        var target = Path.Combine(local, "staging_area", ReadModId(files));
        var existed = Directory.Exists(target);
        if (existed && new DirectoryInfo(target).Attributes.HasFlag(FileAttributes.ReparsePoint))
            return new ModInstallResult(ModInstallStatus.DevCopy, target);

        var changed = 0;
        foreach (var (rel, bytes) in files)
        {
            var dest = Path.Combine(target, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(dest) && File.ReadAllBytes(dest).AsSpan().SequenceEqual(bytes)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            // through a temporary file: an interrupted write won't leave a truncated script
            var tmp = dest + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, dest, overwrite: true);
            changed++;
        }

        // Removed modules: only .lua in content\, the rest of the folder is left alone.
        var content = Path.Combine(target, "content");
        if (Directory.Exists(content))
        {
            var wanted = files.Keys.Select(k => Path.Combine(target, k.Replace('/', Path.DirectorySeparatorChar)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(content, "*.lua", SearchOption.AllDirectories).ToList())
            {
                if (wanted.Contains(f)) continue;
                File.Delete(f);
                changed++;
            }
        }

        var status = !existed ? ModInstallStatus.Installed
            : changed > 0 ? ModInstallStatus.Updated
            : ModInstallStatus.UpToDate;
        return new ModInstallResult(status, target, changed);
    }

    public static string ReadModId(IReadOnlyDictionary<string, byte[]> files)
    {
        using var doc = JsonDocument.Parse(files["mod.json"]);
        return doc.RootElement.GetProperty("modId").GetString()
            ?? throw new InvalidDataException("mod.json has no modId");
    }

    private static bool IsGameRunning()
    {
        var processes = Process.GetProcessesByName("TransportFever3");
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    private static Dictionary<string, byte[]> LoadEmbedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal)) continue;
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            // %(RecursiveDir) from MSBuild has "\"
            files[name[ResourcePrefix.Length..].Replace('\\', '/')] = ms.ToArray();
        }
        return files;
    }
}
