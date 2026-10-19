using System.Globalization;

namespace Tf3Bridge;

public enum Lang { En, Pl }

/// <summary>
/// The app language: the windows, chat answers, in-game notifications and default reward texts all follow it.
/// Texts sit next to each other in the code: <c>Loc.T("English", "polski")</c>.
/// Default: Polish for a Polish Windows, English otherwise; the streamer's choice is kept in
/// %LOCALAPPDATA%\Tf3Bridge\language.txt (<see cref="Load"/>, <see cref="Save"/>).
/// </summary>
public static class Loc
{
    private static readonly AsyncLocal<Lang?> Scoped = new();
    private static volatile int _language = (int)FromCulture(CultureInfo.CurrentUICulture);

    private static readonly CultureInfo PlCulture = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly CultureInfo EnCulture = CultureInfo.GetCultureInfo("en-US");

    public static Lang Language
    {
        get => Scoped.Value ?? (Lang)_language;
        set => _language = (int)value;
    }

    public static bool Pl => Language == Lang.Pl;

    public static string T(string en, string pl) => Pl ? pl : en;

    /// <summary>Number and date formatting for the current language.</summary>
    public static CultureInfo Culture => Pl ? PlCulture : EnCulture;

    public static Lang Other(Lang lang) => lang == Lang.Pl ? Lang.En : Lang.Pl;

    /// <summary>Runs the current async flow in <paramref name="lang"/> until disposed (tests; other threads keep theirs).</summary>
    public static IDisposable Use(Lang lang)
    {
        var previous = Scoped.Value;
        Scoped.Value = lang;
        return new Restore(() => Scoped.Value = previous);
    }

    internal static Lang FromCulture(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName.Equals("pl", StringComparison.OrdinalIgnoreCase) ? Lang.Pl : Lang.En;

    public static string FilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tf3Bridge", "language.txt");

    /// <summary>The saved choice, or null when there is none (then the Windows language stays).</summary>
    public static Lang? Load(string? path = null)
    {
        try
        {
            var text = File.ReadAllText(path ?? FilePath).Trim();
            return Enum.TryParse<Lang>(text, ignoreCase: true, out var lang) && Enum.IsDefined(lang) ? lang : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void Save(Lang lang, string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, lang.ToString().ToLowerInvariant());
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
