namespace Tf3Bridge;

/// <summary>Configuration section "Bridge" in appsettings.json.</summary>
public sealed class BridgeOptions
{
    public const string Section = "Bridge";

    /// <summary>
    /// Folder the mod writes to (app.saveUserdata "towns_industries", files tf3export_*).
    /// Empty = detected from Steam (see <see cref="ResolveInputDir"/>).
    /// </summary>
    public string InputDir { get; set; } = "";

    /// <summary>
    /// Local publish target; mirrors the planned Blob Storage layout (latest/*.json).
    /// Empty = web/data of the repository the exe sits in (see <see cref="ResolveOutputDir"/>).
    /// </summary>
    public string OutputDir { get; set; } = "";

    public int PollIntervalMs { get; set; } = 500;

    /// <summary>
    /// A file that still cannot be parsed after this long is treated as broken
    /// and moved to \local\tf3export\failed next to InputDir (otherwise it is assumed to be mid-write).
    /// </summary>
    public int IncompleteFileTimeoutSec { get; set; } = 30;

    /// <summary>Move processed input files to \local\tf3export\processed instead of deleting them (debugging).</summary>
    public bool KeepProcessedFiles { get; set; }

    /// <summary>
    /// Copy of the newest terrain export. The mod sends the terrain only when a save is loaded or the ground
    /// changes, so after a Tf3Bridge restart the build checks (water, slopes, regions) come from here.
    /// Empty = no cache (Program sets %LOCALAPPDATA%\Tf3Bridge\terrain-cache.json).
    /// </summary>
    public string TerrainCacheFile { get; set; } = "";

    /// <summary>Write indented JSON to the output (debugging; larger files).</summary>
    public bool IndentOutput { get; set; }

    /// <summary>
    /// A configured path wins (relative to the exe folder); empty = Steam's userdata folder of the
    /// account that plays TF3 (<see cref="SteamLocator"/>), or "" when none was found.
    /// </summary>
    public static string ResolveInputDir(string dir) =>
        !string.IsNullOrWhiteSpace(dir)
            ? Path.GetFullPath(dir, AppContext.BaseDirectory)
            : SteamLocator.FindInputDir() ?? "";

    /// <summary>
    /// Relative paths are resolved against the exe folder, not the working directory, so the app
    /// behaves the same from a shortcut, Explorer or `dotnet run`. Empty: walk up from the exe
    /// to the folder containing web/index.html and use its web/data (where the local site reads);
    /// outside the repository fall back to output/ next to the exe.
    /// </summary>
    public static string ResolveOutputDir(string dir)
    {
        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(dir)) return Path.GetFullPath(dir, baseDir);
        for (var d = new DirectoryInfo(baseDir); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "web", "index.html")))
                return Path.Combine(d.FullName, "web", "data");
        }
        return Path.Combine(baseDir, "output");
    }
}
