using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Tf3Bridge;

/// <summary>
/// The app language: the windows, chat answers, in-game notifications and default reward texts all follow it.
/// The code holds only English, which is both the key and the fallback: <c>Loc.T("Save")</c>,
/// <c>Loc.F("Sent {0} to {1}", a, b)</c> (never <c>$"..."</c> inside), <c>Loc.P(n, "{0} town", "{0} towns")</c>.
/// Other languages are Locales\&lt;code&gt;.json files embedded in the exe (<c>{ "English": "translation" }</c>, an array
/// of plural forms for <see cref="P"/>, chat command aliases under <see cref="CommandsKey"/> and <see cref="ArgumentsKey"/>); a text missing there
/// stays English. Adding a language = adding a file (see middle_app/README.md).
/// Default: the Windows language when there is a file for it, English otherwise; the streamer's choice is kept in
/// %LOCALAPPDATA%\Tf3Bridge\language.txt (<see cref="Load"/>, <see cref="Save"/>).
/// </summary>
public static class Loc
{
    public const string English = "en";

    /// <summary>Locale file key with the chat command aliases: <c>{ "assets": ["!majatek"] }</c>.</summary>
    public const string CommandsKey = "_commands";

    /// <summary>Locale file key with prefixes of chat command arguments by their English prefix: <c>{ "town": ["miast"] }</c>.</summary>
    public const string ArgumentsKey = "_arguments";

    private sealed record Locale(
        IReadOnlyDictionary<string, string[]> Texts, IReadOnlyDictionary<string, string[]> Commands, IReadOnlyDictionary<string, string[]> Arguments);

    private static readonly IReadOnlyDictionary<string, Locale> Locales = LoadLocales();

    /// <summary>English first, then the locale files by code.</summary>
    public static IReadOnlyList<string> Languages { get; } = [English, .. Locales.Keys.Order(StringComparer.Ordinal)];

    private static readonly AsyncLocal<string?> Scoped = new();
    private static volatile string _language = FromCulture(CultureInfo.CurrentUICulture);

    /// <summary>Language code ("en", "pl"); unknown codes fall back to English.</summary>
    public static string Language
    {
        get => Scoped.Value ?? _language;
        set => _language = Known(value) ?? English;
    }

    /// <summary>The text in the current language (<paramref name="en"/> when it has no translation).</summary>
    public static string T(string en) => Lookup(Language, en)?[0] ?? en;

    /// <summary><see cref="T"/> in a given language (e.g. default texts of another language).</summary>
    public static string In(string language, string en) => Lookup(language, en)?[0] ?? en;

    /// <summary><see cref="string.Format(string, object?[])"/> of the translated format; a broken translation falls back to English.</summary>
    public static string F(string en, params object?[] args)
    {
        var format = T(en);
        try { return string.Format(CultureInfo.CurrentCulture, format, args); }
        catch (FormatException) when (!ReferenceEquals(format, en)) { return string.Format(CultureInfo.CurrentCulture, en, args); }
    }

    /// <summary>
    /// Count with the plural form of the current language, {0} = <paramref name="n"/>. English: <paramref name="one"/> /
    /// <paramref name="other"/>; a locale file gives an array with one form per <see cref="PluralForms"/> (key: <paramref name="one"/>).
    /// </summary>
    public static string P(long n, string one, string other)
    {
        var language = Language;
        var forms = Lookup(language, one);
        var index = PluralIndex(language, n);
        var format = forms is not null && index < forms.Length ? forms[index] : n == 1 ? one : other;
        return string.Format(CultureInfo.CurrentCulture, format, n);
    }

    /// <summary>Marks an English text for translation without translating it yet (<see cref="T"/> it where it is shown).</summary>
    public static string N(string en) => en;

    /// <summary>Chat command aliases of every locale file (all of them work whatever the app language).</summary>
    public static IEnumerable<string> CommandAliases(string command) =>
        Locales.Values.SelectMany(l => l.Commands.GetValueOrDefault(command) ?? []);

    /// <summary>Argument prefixes of every locale file for an English one ("town" -> "miast").</summary>
    public static IEnumerable<string> ArgumentAliases(string english) =>
        Locales.Values.SelectMany(l => l.Arguments.GetValueOrDefault(english) ?? []);

    /// <summary>Number and date formatting for the current language.</summary>
    public static CultureInfo Culture => CultureFor(Language);

    /// <summary>"English", "Polski": the language's own name, for the selector.</summary>
    public static string NativeName(string language)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(language);
            return culture.TextInfo.ToUpper(culture.NativeName[0]) + culture.NativeName[1..];
        }
        catch (CultureNotFoundException) { return language; }
    }

    /// <summary>Runs the current async flow in <paramref name="language"/> until disposed (tests; other threads keep theirs).</summary>
    public static IDisposable Use(string language)
    {
        var previous = Scoped.Value;
        Scoped.Value = Known(language) ?? throw new ArgumentException($"No locale file for '{language}'.", nameof(language));
        return new Restore(() => Scoped.Value = previous);
    }

    internal static string FromCulture(CultureInfo culture) => Known(culture.TwoLetterISOLanguageName) ?? English;

    /// <summary>Plural forms a locale file needs for <paramref name="language"/> (CLDR cardinal categories used with integers).</summary>
    public static int PluralForms(string language) => Rule(language).Forms;

    internal static int PluralIndex(string language, long n) => Rule(language).Index(Math.Abs(n));

    // Integer plural rules by language family; a language not listed uses English rules (1 / other).
    private static (int Forms, Func<long, int> Index) Rule(string language) => language switch
    {
        "pl" => (3, n => n == 1 ? 0 : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? 1 : 2),
        "ru" or "uk" or "be" or "sr" or "hr" or "bs" =>
            (3, n => n % 10 == 1 && n % 100 != 11 ? 0 : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? 1 : 2),
        "cs" or "sk" => (3, n => n == 1 ? 0 : n is >= 2 and <= 4 ? 1 : 2),
        "fr" => (2, n => n <= 1 ? 0 : 1),
        "ja" or "zh" or "ko" or "vi" or "th" or "id" or "ms" => (1, _ => 0),
        _ => (2, n => n == 1 ? 0 : 1),
    };

    public static string FilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tf3Bridge", "language.txt");

    /// <summary>The saved choice, or null when there is none or its locale is gone (then the Windows language stays).</summary>
    public static string? Load(string? path = null)
    {
        try { return Known(File.ReadAllText(path ?? FilePath).Trim()); } // also "En" / "Pl" from the enum days
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void Save(string language, string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, language);
    }

    private static string? Known(string? code) =>
        Languages.FirstOrDefault(l => string.Equals(l, code?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string[]? Lookup(string language, string en) =>
        Locales.TryGetValue(language, out var locale) && locale.Texts.TryGetValue(en, out var t) ? t : null;

    private static CultureInfo CultureFor(string language)
    {
        try { return CultureInfo.CreateSpecificCulture(language); } // "en" -> en-US, "pl" -> pl-PL
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    /// <summary>Embedded "locales/&lt;code&gt;.json" files by code (Locales\*.json in the project).</summary>
    private static Dictionary<string, Locale> LoadLocales()
    {
        var assembly = typeof(Loc).Assembly;
        var result = new Dictionary<string, Locale>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("locales/", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            result[Path.GetFileNameWithoutExtension(name)] = Parse(stream);
        }
        return result;
    }

    private static Locale Parse(Stream json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var texts = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var commands = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var arguments = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Name is CommandsKey or ArgumentsKey)
                foreach (var c in p.Value.EnumerateObject())
                    (p.Name == CommandsKey ? commands : arguments)[c.Name] = Strings(c.Value);
            else if (Strings(p.Value) is { Length: > 0 } values && values.All(v => v.Length > 0))
                texts[p.Name] = values; // empty = not translated yet
        }
        return new(texts, commands, arguments);
    }

    private static string[] Strings(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => [e.GetString()!],
        JsonValueKind.Array => [.. e.EnumerateArray().Select(x => x.GetString() ?? "")],
        _ => [],
    };

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
