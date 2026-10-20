using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tf3Bridge.Chat;

/// <summary>The informational chat commands (<see cref="ChatCommandService"/>, texts in <see cref="ChatAnswers"/>).</summary>
public enum ChatCommandKind { Help, Map, Assets, Town, Industry, Line, Ranking, Free, Date, Company, Best, Worst }

/// <summary>
/// One chat command as the streamer configured it. Names are stored without "!" and in lower case;
/// viewers type "!" + <see cref="Name"/> or "!" + one of <see cref="Aliases"/>. <see cref="Name"/> is the one
/// <c>!help</c> and the answers show. <see cref="CooldownSec"/>: the same command with the same argument is answered
/// at most once per this time for the whole chat (silently).
/// </summary>
public sealed record ChatCommandConfig(ChatCommandKind Kind, bool Enabled, string Name, IReadOnlyList<string> Aliases, int CooldownSec)
{
    public const int MaxName = 25;
    public static readonly (int Min, int Max) CooldownRange = (0, 3600);

    /// <summary>The name and the aliases, each once.</summary>
    public IEnumerable<string> AllNames => new[] { Name }.Concat(Aliases).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal);

    /// <summary>Names cleaned (<see cref="CleanName"/>; invalid ones dropped), the cooldown clamped.</summary>
    public ChatCommandConfig Normalized()
    {
        var names = new[] { Name }.Concat(Aliases ?? []).Select(CleanName).Where(IsValidName).Distinct(StringComparer.Ordinal).ToList();
        return this with
        {
            Name = names.Count > 0 ? names[0] : "",
            Aliases = [.. names.Skip(1)],
            CooldownSec = Math.Clamp(CooldownSec, CooldownRange.Min, CooldownRange.Max),
        };
    }

    /// <summary>"!Majątek " -> "majątek".</summary>
    public static string CleanName(string? name) => (name ?? "").Trim().TrimStart('!').Trim().ToLowerInvariant();

    /// <summary>Letters (also Polish), digits and "_", 1–<see cref="MaxName"/> characters (after <see cref="CleanName"/>).</summary>
    public static bool IsValidName(string name) =>
        name.Length is > 0 and <= MaxName && name.All(c => char.IsLetterOrDigit(c) || c == '_');
}

/// <summary>Defaults and what each command shows.</summary>
public static class ChatCommandKinds
{
    /// <summary>Starting values in the current language (<see cref="Loc"/>).</summary>
    public static IReadOnlyList<ChatCommandConfig> Defaults => DefaultsFor(Loc.Language);

    /// <summary>
    /// Both languages answer by default (as before the settings existed): the current language's name first,
    /// the other names as aliases. An English stream can remove the Polish aliases in the window.
    /// </summary>
    public static IReadOnlyList<ChatCommandConfig> DefaultsFor(string language) =>
    [
        Make(language, ChatCommandKind.Help, 30, ["help", "commands"], ["pomoc", "komendy"]),
        Make(language, ChatCommandKind.Map, 30, ["map"], ["mapa"]),
        Make(language, ChatCommandKind.Assets, 30, ["assets"], ["majatek", "majątek"]),
        Make(language, ChatCommandKind.Town, 30, ["town"], ["miasto"]),
        Make(language, ChatCommandKind.Industry, 30, ["industry"], ["zaklad", "zakład"]),
        Make(language, ChatCommandKind.Line, 30, ["line"], ["linia"]),
        Make(language, ChatCommandKind.Ranking, 60, ["ranking", "top"], ["ranking", "top"]),
        Make(language, ChatCommandKind.Free, 60, ["free"], ["wolne"]),
        Make(language, ChatCommandKind.Date, 30, ["date"], ["data"]),
        Make(language, ChatCommandKind.Company, 60, ["company"], ["firma"]),
        Make(language, ChatCommandKind.Best, 60, ["best"], ["najlepsza", "najlepszy"]),
        Make(language, ChatCommandKind.Worst, 60, ["worst"], ["najgorsza", "najgorszy"]),
    ];

    private static ChatCommandConfig Make(string language, ChatCommandKind kind, int cooldown, string[] en, string[] pl)
    {
        var names = (language == "pl" ? pl.Concat(en) : en.Concat(pl)).Distinct().ToList();
        return new ChatCommandConfig(kind, true, names[0], [.. names.Skip(1)], cooldown);
    }

    /// <summary>Needs the latest dynamic.json (help and map do not).</summary>
    public static bool NeedsData(ChatCommandKind kind) => kind is not (ChatCommandKind.Help or ChatCommandKind.Map);

    /// <summary>What the viewer may type after the name, as shown in <c>!help</c> (" [viewer]"); "" for none.</summary>
    public static string Usage(ChatCommandKind kind) => kind switch
    {
        ChatCommandKind.Assets => Loc.T(" [viewer]"),
        ChatCommandKind.Town or ChatCommandKind.Industry or ChatCommandKind.Line => Loc.T(" <name>"),
        ChatCommandKind.Free => Loc.T(" [towns|industries]"),
        ChatCommandKind.Best or ChatCommandKind.Worst => Loc.T(" [line|company]"),
        _ => "",
    };

    /// <summary>What the command answers, for the settings window.</summary>
    public static string Label(ChatCommandKind kind) => kind switch
    {
        ChatCommandKind.Help => Loc.T("List of commands"),
        ChatCommandKind.Map => Loc.T("Link to the live map"),
        ChatCommandKind.Assets => Loc.T("What a viewer owns"),
        ChatCommandKind.Town => Loc.T("Town: owner, size, effects"),
        ChatCommandKind.Industry => Loc.T("Industry: owner, level, production"),
        ChatCommandKind.Line => Loc.T("Line: owner, vehicles, profit"),
        ChatCommandKind.Ranking => Loc.T("Viewer ranking"),
        ChatCommandKind.Free => Loc.T("Towns and industries without an owner"),
        ChatCommandKind.Date => Loc.T("In-game date"),
        ChatCommandKind.Company => Loc.T("The streamer's company"),
        ChatCommandKind.Best => Loc.T("Best line / viewer company"),
        _ => Loc.T("Worst line / viewer company"),
    };
}

/// <summary>All chat commands (every kind once, see <see cref="Normalized"/>).</summary>
public sealed record ChatCommandSettings(IReadOnlyList<ChatCommandConfig> Commands)
{
    /// <summary>Defaults in the current language: the commands as they were before they could be configured.</summary>
    public static ChatCommandSettings Default => new(ChatCommandKinds.Defaults);

    public ChatCommandConfig Get(ChatCommandKind kind) => Commands.First(c => c.Kind == kind);

    /// <summary>"!majatek" when the command is on, otherwise null (answers then don't point to it).</summary>
    public string? Shown(ChatCommandKind kind) => Get(kind) is { Enabled: true, Name.Length: > 0 } c ? "!" + c.Name : null;

    /// <summary>
    /// Every kind exactly once (missing ones from defaults), names cleaned; a command left without a valid name gets its default
    /// name back. Names of another language's defaults follow a language switch; the streamer's own names stay.
    /// </summary>
    public ChatCommandSettings Normalized()
    {
        var others = Loc.Languages.Where(l => l != Loc.Language).Select(ChatCommandKinds.DefaultsFor).ToList();
        return new([.. ChatCommandKinds.Defaults.Select(d =>
        {
            var c = (Commands ?? []).FirstOrDefault(x => x is not null && x.Kind == d.Kind);
            if (c is null) return d;
            c = (c with { Aliases = c.Aliases ?? [] }).Normalized();
            var isOtherDefault = others.Select(o => o.First(x => x.Kind == d.Kind)).Any(o => c.Name == o.Name && c.Aliases.SequenceEqual(o.Aliases));
            if (isOtherDefault) c = c with { Name = d.Name, Aliases = d.Aliases };
            return c.Name.Length == 0 ? c with { Name = d.Name, Aliases = d.Aliases } : c;
        })]);
    }

    /// <summary>
    /// Why the commands can't be saved (a name used by two commands), or null. Names are compared after <see cref="ChatCommandConfig.CleanName"/>;
    /// off commands count too, so switching one on later can't clash.
    /// </summary>
    public static string? Validate(IEnumerable<ChatCommandConfig> commands)
    {
        var owner = new Dictionary<string, ChatCommandKind>(StringComparer.Ordinal);
        foreach (var c in commands)
            foreach (var name in c.AllNames)
            {
                if (owner.TryGetValue(name, out var first) && first != c.Kind)
                    return Loc.F("!{0} is used by two commands: \"{1}\" and \"{2}\".", name, ChatCommandKinds.Label(first), ChatCommandKinds.Label(c.Kind));
                owner[name] = c.Kind;
            }
        return null;
    }
}

/// <summary>%LOCALAPPDATA%\Tf3Bridge\chat-commands.json (per streamer, not in git). No file = defaults.</summary>
public static class ChatCommandSettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // "majątek", not "majątek"
    };

    public static string FilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tf3Bridge", "chat-commands.json");

    public static ChatCommandSettings Load(string? path = null)
    {
        path ??= FilePath;
        if (!File.Exists(path)) return ChatCommandSettings.Default;
        try
        {
            var s = JsonSerializer.Deserialize<ChatCommandSettings>(File.ReadAllText(path), Json);
            return s is null ? ChatCommandSettings.Default : s.Normalized();
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return ChatCommandSettings.Default;
        }
    }

    public static void Save(ChatCommandSettings settings, string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
