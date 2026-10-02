namespace Tf3Bridge;

/// <summary>Configuration section "Bridge" in appsettings.json.</summary>
public sealed class BridgeOptions
{
    public const string Section = "Bridge";

    /// <summary>Folder the mod writes to (app.saveUserdata "tf3export").</summary>
    public string InputDir { get; set; } = "";

    /// <summary>Local publish target; mirrors the planned Blob Storage layout (latest/*.json).</summary>
    public string OutputDir { get; set; } = "output";

    public int PollIntervalMs { get; set; } = 500;

    /// <summary>
    /// A file that still cannot be parsed after this long is treated as broken
    /// and moved to InputDir/failed (otherwise it is assumed to be mid-write).
    /// </summary>
    public int IncompleteFileTimeoutSec { get; set; } = 30;

    /// <summary>Move processed input files to InputDir/processed instead of deleting them (debugging).</summary>
    public bool KeepProcessedFiles { get; set; }

    /// <summary>Write indented JSON to the output (debugging; larger files).</summary>
    public bool IndentOutput { get; set; }
}
