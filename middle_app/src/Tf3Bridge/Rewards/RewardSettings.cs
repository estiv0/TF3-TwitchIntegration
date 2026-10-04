using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tf3Bridge.Commands;

namespace Tf3Bridge.Rewards;

/// <summary>Channel points rewards Tf3Bridge manages. The only way viewers can change the game.</summary>
public enum RewardKind { AdoptTown, BoostTown, BoostIndustry, StopLine, HoldLine }

/// <summary>
/// One reward as the streamer configured it. <see cref="Months"/> (game months) and <see cref="Factor"/>
/// only matter for timed effects (see <see cref="RewardKinds"/>). Prompts may use {months} and {percent}.
/// Months, not days: a game day lasts only 2-3 s, so effects of a few days were over before anyone saw them.
/// </summary>
public sealed record RewardConfig(
    RewardKind Kind, bool Enabled, string Title, string Prompt, int Cost,
    int CooldownSec, int MaxPerStream, int MaxPerUserPerStream, int Months, double Factor)
{
    public const int MaxTitle = 45;
    public const int MaxPrompt = 200;
    public static readonly (int Min, int Max) MonthsRange = (1, 60);
    public static readonly (double Min, double Max) FactorRange = (1.05, 5.0);

    /// <summary>Prompt shown on Twitch with the placeholders filled in.</summary>
    public string TwitchPrompt => Prompt
        .Replace("{months}", Months.ToString(CultureInfo.InvariantCulture))
        .Replace("{percent}", Math.Round((Factor - 1) * 100).ToString(CultureInfo.InvariantCulture));

    /// <summary>Values clamped to what Twitch and the mod accept.</summary>
    public RewardConfig Normalized() => this with
    {
        Title = Cut(Title.Trim(), MaxTitle),
        Prompt = Cut(Prompt.Trim(), MaxPrompt),
        Cost = Math.Max(1, Cost),
        CooldownSec = Math.Clamp(CooldownSec, 0, 7 * 24 * 3600),
        MaxPerStream = Math.Max(0, MaxPerStream),
        MaxPerUserPerStream = Math.Max(0, MaxPerUserPerStream),
        Months = Math.Clamp(Months, MonthsRange.Min, MonthsRange.Max),
        Factor = Math.Round(Math.Clamp(Factor, FactorRange.Min, FactorRange.Max), 2),
    };

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>What each reward targets and does in the game.</summary>
public static class RewardKinds
{
    public static GameObjectKind Target(RewardKind kind) => kind switch
    {
        RewardKind.AdoptTown or RewardKind.BoostTown => GameObjectKind.Town,
        RewardKind.BoostIndustry => GameObjectKind.Industry,
        _ => GameObjectKind.Line,
    };

    /// <summary>Effect name in the mod (effects.lua); null for adoption (not timed).</summary>
    public static string? Effect(RewardKind kind) => kind switch
    {
        RewardKind.BoostTown => "townBoost",
        RewardKind.BoostIndustry => "industryBoost",
        RewardKind.StopLine => "lineStop",
        RewardKind.HoldLine => "lineHold",
        _ => null,
    };

    public static bool HasFactor(RewardKind kind) => kind is RewardKind.BoostTown or RewardKind.BoostIndustry;

    public static bool HasMonths(RewardKind kind) => Effect(kind) is not null;

    /// <summary>Starting values; costs and durations are guesses to be balanced on stream.</summary>
    public static IReadOnlyList<RewardConfig> Defaults { get; } =
    [
        new(RewardKind.AdoptTown, true, "TF3: kup miasto",
            "Wpisz nazwę miasta bez właściciela. Kupujesz je: zostaniesz jego właścicielem (widać to w grze i na mapie).",
            5000, 60, 0, 1, 0, 1),
        new(RewardKind.BoostTown, true, "TF3: boost miasta",
            "Wpisz nazwę miasta. Przez {months} mies. gry jego docelowa wielkość rośnie o {percent}% (miasto się rozbudowuje).",
            3000, 120, 0, 0, 12, 1.5),
        new(RewardKind.BoostIndustry, true, "TF3: boost przemysłu",
            "Wpisz nazwę zakładu. Produkuje o {percent}% więcej przez {months} mies. gry.",
            3000, 120, 0, 0, 6, 1.5),
        new(RewardKind.StopLine, true, "TF3: zatrzymaj linię",
            "Wpisz nazwę linii. Jej pojazdy staną tam, gdzie są, na {months} mies. gry.",
            10000, 600, 0, 1, 1, 1),
        new(RewardKind.HoldLine, true, "TF3: linia czeka na stacjach",
            "Wpisz nazwę linii. Jej pojazdy czekają na przystankach przez {months} mies. gry.",
            8000, 600, 0, 1, 1, 1),
    ];
}

/// <summary>Rewards plus the Twitch reward IDs created for them on the streamer's channel.</summary>
public sealed record RewardSettings(IReadOnlyList<RewardConfig> Rewards, string ChannelUserId, IReadOnlyDictionary<RewardKind, string> TwitchIds)
{
    /// <summary>Answer the viewer in chat after a redemption (effect and its end date, or why the points came back).</summary>
    public bool ChatMessages { get; init; } = true;

    public static RewardSettings Default { get; } = new(RewardKinds.Defaults, "", new Dictionary<RewardKind, string>());

    public RewardConfig Get(RewardKind kind) => Rewards.First(r => r.Kind == kind);

    // Dawne domyślne teksty "adopcji" (przed zmianą na kupno miasta); zmienione przez streamera zostają.
    private const string OldAdoptTitle = "TF3: adoptuj miasto";
    private const string OldAdoptPrompt = "Wpisz nazwę miasta bez właściciela. Zostaniesz jego właścicielem (pokaże się w grze i na mapie).";

    /// <summary>Every kind exactly once (missing ones from defaults), values normalized.</summary>
    public RewardSettings Normalized() => this with
    {
        Rewards = [.. RewardKinds.Defaults.Select(d => Rewards.FirstOrDefault(r => r.Kind == d.Kind) switch
        {
            null => d,
            { Months: <= 0 } r => Migrate(r with { Months = d.Months }, d), // plik sprzed zmiany dni -> miesiące
            var r => Migrate(r, d),
        }).Select(r => r.Normalized())],
    };

    private static RewardConfig Migrate(RewardConfig r, RewardConfig d) => r with
    {
        Title = r.Title == OldAdoptTitle ? d.Title : r.Title,
        Prompt = r.Prompt == OldAdoptPrompt ? d.Prompt : r.Prompt,
    };
}

/// <summary>%LOCALAPPDATA%\Tf3Bridge\rewards.json (per streamer, not in git).</summary>
public static class RewardSettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FilePath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tf3Bridge", "rewards.json");

    public static RewardSettings Load(string? path = null)
    {
        path ??= FilePath;
        if (!File.Exists(path)) return RewardSettings.Default;
        try
        {
            var s = JsonSerializer.Deserialize<RewardSettings>(File.ReadAllText(path), Json);
            return s is null ? RewardSettings.Default : (s with
            {
                Rewards = s.Rewards ?? [],
                ChannelUserId = s.ChannelUserId ?? "",
                TwitchIds = s.TwitchIds ?? new Dictionary<RewardKind, string>(),
            }).Normalized();
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return RewardSettings.Default;
        }
    }

    public static void Save(RewardSettings settings, string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
