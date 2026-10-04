using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Tf3Bridge.Commands;

/// <summary>
/// Who may change the game. Chat is deliberately missing: chat commands are informational only,
/// because the streamer has no control over them and they could break the game balance.
/// Game changes come only from channel points rewards (and the manual test in the bridge window).
/// </summary>
public enum CommandSource { Test, ChannelPoints }

/// <summary>
/// One command for the mod (lua_mod/content/tf3export/commands.lua). <see cref="Kind"/> and
/// <see cref="Args"/> go to the game; <see cref="Source"/> and <see cref="User"/> stay in the bridge.
/// </summary>
public sealed record ModCommand(
    string Id, string Kind, DateTimeOffset Created, IReadOnlyDictionary<string, object> Args, CommandSource Source, string User)
{
    /// <summary>Renames an entity (town, industry, station group, line, vehicle).</summary>
    public static ModCommand SetName(long entity, string name, CommandSource source, string user) =>
        new(NewId(), "setName", DateTimeOffset.UtcNow,
            new Dictionary<string, object> { ["entity"] = entity, ["name"] = name }, source, user);

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    public override string ToString() =>
        $"{Kind} {string.Join(", ", Args.Select(a => $"{a.Key}={a.Value}"))} ({Source}, {User}, #{Id})";
}

/// <summary>Result written by the mod. Statuses: ok, failed, invalid, expired.</summary>
public sealed record ModCommandResult(string Id, string Status, string? Error)
{
    public bool Ok => Status == "ok";
}

/// <summary>Writes the queue as the file app.saveUserdata would produce, so app.loadUserdata reads it.</summary>
public static partial class ModCommandLua
{
    public static string ToLua(IEnumerable<ModCommand> commands)
    {
        var sb = new StringBuilder();
        sb.Append("-- Written by Tf3Bridge: commands for the mod (commands.lua); removed once the mod reports a result.\r\n");
        sb.Append("function data()\r\nreturn {\r\n\tcommands = {\r\n");
        foreach (var c in commands)
        {
            sb.Append("\t\t{ id = ").Append(Str(c.Id))
              .Append(", kind = ").Append(Str(c.Kind))
              .Append(", created = ").Append(c.Created.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            foreach (var (key, value) in c.Args)
            {
                if (!Identifier().IsMatch(key) || key is "id" or "kind" or "created")
                    throw new ArgumentException($"Invalid command argument name: {key}");
                sb.Append(", ").Append(key).Append(" = ").Append(Value(value));
            }
            sb.Append(" },\r\n");
        }
        sb.Append("\t},\r\n}\r\nend\r\n");
        return sb.ToString();
    }

    private static string Value(object v) => v switch
    {
        string s => Str(s),
        bool b => b ? "true" : "false",
        int or long => Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        double d when double.IsFinite(d) => d.ToString("R", CultureInfo.InvariantCulture),
        _ => throw new ArgumentException($"Unsupported command argument type: {v.GetType().Name}"),
    };

    /// <summary>
    /// Lua string literal. Text from viewers ends up here, so everything that could end the
    /// literal or the line is escaped; other characters pass through as UTF-8.
    /// </summary>
    internal static string Str(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append(@"\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append(@"\n"); break;
                case '\r': sb.Append(@"\r"); break;
                case '\t': sb.Append(@"\t"); break;
                default:
                    if (ch < 0x20 || ch == 0x7f) sb.Append('\\').Append(((int)ch).ToString("D3", CultureInfo.InvariantCulture));
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Identifier();
}
