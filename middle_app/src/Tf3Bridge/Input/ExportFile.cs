using System.Text.RegularExpressions;

namespace Tf3Bridge.Input;

public enum ExportKind
{
    Dynamic,
    Network,
    NetworkDelta,
    Terrain,
}

/// <summary>
/// A file written by the mod: <c>tf3export_&lt;kind&gt;_&lt;session&gt;_&lt;seq&gt;.lua</c>,
/// e.g. <c>tf3export_networkDelta_6abfd5c4d091_000004.lua</c>. Anything else in the folder
/// (the map editor's town/industry exports, the command queue, settings) is ignored.
/// </summary>
public sealed partial record ExportFile(string Path, ExportKind Kind, string Session, long Seq, DateTime LastWriteUtc)
{
    [GeneratedRegex(@"^tf3export_(dynamic|network|networkDelta|terrain)_([0-9a-f]+)_(\d+)\.lua$")]
    private static partial Regex NamePattern();

    public static ExportFile? TryParse(string path, DateTime lastWriteUtc)
    {
        var match = NamePattern().Match(System.IO.Path.GetFileName(path));
        if (!match.Success) return null;

        var kind = match.Groups[1].Value switch
        {
            "dynamic" => ExportKind.Dynamic,
            "network" => ExportKind.Network,
            "terrain" => ExportKind.Terrain,
            _ => ExportKind.NetworkDelta,
        };
        return new ExportFile(path, kind, match.Groups[2].Value, long.Parse(match.Groups[3].Value), lastWriteUtc);
    }
}
