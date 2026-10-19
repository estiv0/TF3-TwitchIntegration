using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tf3Bridge.Commands;
using Tf3Bridge.Processing;

namespace Tf3Bridge.Rewards;

/// <summary>
/// Channel points rewards Tf3Bridge manages. The only way viewers can change the game.
/// The last ones (<see cref="RewardKinds.Manual"/>) have no effect in the mod: the streamer carries them out.
/// </summary>
public enum RewardKind
{
    AdoptTown, BuyIndustry, BoostTown, BoostIndustry, StopLine, HoldLine, CreateTown, CreateIndustry,
    StrikeIndustry, TaxRaid, PremiumFuel, BadFuel, BrakeSabotage, Subsidy, Loan,
    Bomb, Line, Nature, RoadVehicle, SpecialVehicle,
}

/// <summary>
/// One reward as the streamer configured it. <see cref="Months"/> (game months) and <see cref="Factor"/>
/// only matter for timed effects (see <see cref="RewardKinds"/>); for the brake sabotage <see cref="Factor"/> is the
/// largest share of the line's vehicles that may be wrecked; for the loan <see cref="Months"/> is how long it is repaid and
/// <see cref="Factor"/> its interest in % (2 = 2%). Prompts may use {months}, {percent} (the change in %, without sign;
/// the share for the brake sabotage, the interest for the loan), {amount} (<see cref="Amount"/>) and {site}
/// (the map site, filled in when the reward is sent to Twitch).
/// Months, not days: a game day lasts only 2-3 s, so effects of a few days were over before anyone saw them.
/// </summary>
public sealed record RewardConfig(
    RewardKind Kind, bool Enabled, string Title, string Prompt, int Cost,
    int CooldownSec, int MaxPerStream, int MaxPerUserPerStream, int Months, double Factor)
{
    public const int MaxTitle = 45;

    /// <summary>
    /// The viewer types a text with the redemption. Always on for rewards the game carries out (the text names the target);
    /// the streamer's choice for <see cref="RewardKinds.Manual"/> ones.
    /// </summary>
    public bool UserInput { get; init; } = true;

    public bool RequiresInput => RewardKinds.Manual(Kind) ? UserInput : RewardKinds.HasTarget(Kind);
    public const int MaxPrompt = 200;
    public static readonly (int Min, int Max) MonthsRange = (1, 60);

    /// <summary>Money the dotacja / kredyt gives the streamer's company (<see cref="RewardKinds.HasAmount"/>).</summary>
    public long Amount { get; init; }

    public static readonly (long Min, long Max) AmountRange = (1_000, 100_000_000_000);

    /// <summary>Prompt shown on Twitch with the placeholders filled in.</summary>
    public string TwitchPrompt => Prompt
        .Replace("{months}", Months.ToString(CultureInfo.InvariantCulture))
        .Replace("{percent}", RewardKinds.Percent(Kind, Factor))
        .Replace("{amount}", RewardMessages.Money(Amount));

    /// <summary>Values clamped to what Twitch and the mod accept.</summary>
    public RewardConfig Normalized() => this with
    {
        Title = Cut(Title.Trim(), MaxTitle),
        Prompt = Cut(Prompt.Trim(), MaxPrompt),
        Cost = Math.Max(1, Cost),
        CooldownSec = Math.Clamp(CooldownSec, 0, 7 * 24 * 3600),
        MaxPerStream = Math.Max(0, MaxPerStream),
        MaxPerUserPerStream = Math.Max(0, MaxPerUserPerStream),
        Months = Math.Clamp(Months, RewardKinds.MonthsRange(Kind).Min, RewardKinds.MonthsRange(Kind).Max),
        Factor = Math.Round(Math.Clamp(Factor, RewardKinds.FactorRange(Kind).Min, RewardKinds.FactorRange(Kind).Max), 2),
        Amount = RewardKinds.HasAmount(Kind) ? Math.Clamp(Amount, AmountRange.Min, AmountRange.Max) : 0,
    };

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>What each reward targets and does in the game.</summary>
public static class RewardKinds
{
    public static GameObjectKind Target(RewardKind kind) => kind switch
    {
        RewardKind.AdoptTown or RewardKind.BoostTown or RewardKind.CreateTown => GameObjectKind.Town,
        RewardKind.BoostIndustry or RewardKind.CreateIndustry or RewardKind.BuyIndustry or RewardKind.StrikeIndustry
            or RewardKind.TaxRaid => GameObjectKind.Industry,
        _ => GameObjectKind.Line,
    };

    /// <summary>The viewer types the nick of another viewer, not an object name (all industries of that viewer).</summary>
    public static bool TargetsOwner(RewardKind kind) => kind is RewardKind.TaxRaid;

    /// <summary>
    /// Harms someone: allowed only on objects of another viewer (objects without an owner and the buyer's own are refused).
    /// </summary>
    public static bool Sabotage(RewardKind kind) =>
        kind is RewardKind.StrikeIndustry or RewardKind.TaxRaid or RewardKind.BadFuel or RewardKind.BrakeSabotage;

    /// <summary>
    /// Money for the streamer's company, no target: subsidy (booked as a subsidy) and loan (a loan in the game's own
    /// loan system, repaid by the game every month with interest).
    /// </summary>
    public static bool HasAmount(RewardKind kind) => kind is RewardKind.Subsidy or RewardKind.Loan;

    /// <summary>The viewer names a target (object, nick or map code); rewards for the company itself need no text.</summary>
    public static bool HasTarget(RewardKind kind) => !Manual(kind) && !HasAmount(kind);

    /// <summary>Allowed <see cref="RewardConfig.Months"/>: effects up to 5 years, loans up to 30 (the game's own up to 24).</summary>
    public static (int Min, int Max) MonthsRange(RewardKind kind) =>
        kind == RewardKind.Loan ? (1, 360) : RewardConfig.MonthsRange;

    /// <summary>
    /// Allowed <see cref="RewardConfig.Factor"/>: boosts above 1, sabotage below 1, brake sabotage a share 0..1,
    /// loan interest in % (the game's own loans: 3-12%).
    /// </summary>
    public static (double Min, double Max) FactorRange(RewardKind kind) => kind switch
    {
        RewardKind.Loan => (0, 50),
        RewardKind.StrikeIndustry or RewardKind.TaxRaid => (0.01, 0.95),
        RewardKind.BadFuel => (0.1, 0.95),
        RewardKind.PremiumFuel => (1.05, 3.0),
        RewardKind.BrakeSabotage => (0.05, 1.0),
        _ => (1.05, 5.0),
    };

    /// <summary>
    /// "50" for ×1.5 and for ×0.5 (the text says whether it is more or less); the share in % for the brake sabotage;
    /// the interest as set for the loan ("2", "1,5").
    /// </summary>
    public static string Percent(RewardKind kind, double factor) => kind == RewardKind.Loan
        ? Math.Round(factor, 2).ToString("0.##", RewardMessages.Polish)
        : Math.Round(Math.Abs(kind == RewardKind.BrakeSabotage ? factor : factor - 1) * 100).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Builds something new where the viewer pointed on the site map: the input is a code from the site
    /// (<see cref="BuildCode"/>), not the name of an existing object.
    /// </summary>
    public static bool Creates(RewardKind kind) => kind is RewardKind.CreateTown or RewardKind.CreateIndustry;

    /// <summary>Buying an existing town or industry: the viewer becomes its owner (adopt), only when it has none yet.</summary>
    public static bool Buys(RewardKind kind) => kind is RewardKind.AdoptTown or RewardKind.BuyIndustry;

    /// <summary>Effect name in the mod (effects.lua); null for buying (not timed).</summary>
    public static string? Effect(RewardKind kind) => kind switch
    {
        RewardKind.BoostTown => "townBoost",
        RewardKind.BoostIndustry => "industryBoost",
        RewardKind.StopLine => "lineStop",
        RewardKind.HoldLine => "lineHold",
        RewardKind.StrikeIndustry => "industryStrike",
        RewardKind.TaxRaid => "industryTaxRaid",
        RewardKind.PremiumFuel => "lineFuelPremium",
        RewardKind.BadFuel => "lineFuelBad",
        _ => null,
    };

    public static bool HasFactor(RewardKind kind) => kind is RewardKind.BoostTown or RewardKind.BoostIndustry or RewardKind.StrikeIndustry
        or RewardKind.TaxRaid or RewardKind.PremiumFuel or RewardKind.BadFuel or RewardKind.BrakeSabotage or RewardKind.Loan;

    public static bool HasMonths(RewardKind kind) => Effect(kind) is not null || kind == RewardKind.Loan;

    /// <summary>
    /// No action in the game: Tf3Bridge only creates the reward and switches it with the game (off when the mod does not run);
    /// redemptions stay in the Twitch queue and the streamer carries them out and accepts or refunds them.
    /// </summary>
    public static bool Manual(RewardKind kind) => kind >= RewardKind.Bomb;

    /// <summary>Starting values; costs and durations are guesses to be balanced on stream.</summary>
    public static IReadOnlyList<RewardConfig> Defaults { get; } =
    [
        new(RewardKind.AdoptTown, true, "TF3: kup miasto",
            "Wpisz nazwę miasta bez właściciela. Kupujesz je: zostaniesz jego właścicielem (widać to w grze i na mapie).",
            5000, 60, 0, 1, 0, 1),
        new(RewardKind.BuyIndustry, true, "TF3: kup zakład",
            "Wpisz nazwę zakładu bez właściciela (z mapy {site}). Kupujesz go: zostaniesz jego właścicielem (widać to w grze i na mapie).",
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
        new(RewardKind.CreateTown, true, "TF3: załóż miasto",
            "Na mapie {site} kliknij 🏗, wskaż miejsce, nadaj nazwę i wklej tu kod. Powstanie nowe miasto, a Ty będziesz jego właścicielem.",
            20000, 900, 0, 1, 0, 1),
        new(RewardKind.CreateIndustry, true, "TF3: zbuduj zakład",
            "Na mapie {site} kliknij 🏗, wskaż miejsce, wybierz zakład, nadaj nazwę i wklej tu kod. Zakład powstanie i będzie Twój.",
            15000, 600, 0, 1, 0, 1),
        new(RewardKind.StrikeIndustry, true, "TF3: strajk w zakładzie",
            "Wpisz nazwę zakładu innego widza. Załoga strajkuje: produkcja spada o {percent}% na {months} mies. gry.",
            8000, 600, 0, 1, 3, 0.01),
        new(RewardKind.TaxRaid, true, "TF3: nalot skarbówki",
            "Wpisz nick widza, który ma zakłady. Skarbówka blokuje wszystkie jego zakłady: produkcja spada o {percent}% na {months} mies. gry.",
            15000, 900, 0, 1, 3, 0.5),
        new(RewardKind.PremiumFuel, true, "TF3: paliwo premium",
            "Wpisz nazwę linii. Jej pojazdy jeżdżą o {percent}% szybciej przez {months} mies. gry.",
            4000, 300, 0, 0, 6, 1.2),
        new(RewardKind.BadFuel, true, "TF3: chrzczone paliwo",
            "Wpisz nazwę linii innego widza. Jej pojazdy jeżdżą o {percent}% wolniej przez {months} mies. gry.",
            6000, 600, 0, 1, 3, 0.8),
        new(RewardKind.BrakeSabotage, true, "TF3: sabotaż hamulców",
            "Wpisz nazwę linii innego widza. Od 1 do {percent}% jej pojazdów rozbije się i zniknie (nie do cofnięcia).",
            25000, 1800, 0, 1, 0, 0.5),
        new(RewardKind.Subsidy, true, "TF3: dotacja",
            "Przekazujesz firmie streamera dotację {amount} (bez zwrotu).",
            30000, 1800, 0, 1, 0, 1) { Amount = 10_000_000 },
        new(RewardKind.Loan, true, "TF3: kredyt",
            "Udzielasz firmie streamera kredytu {amount} na {percent}% na {months} mies. gry. Gra sama pobiera raty co miesiąc (zakładka Kredyty).",
            5000, 900, 0, 1, 60, 2) { Amount = 10_000_000 },
        // Carried out by the streamer: they set the description and limits themselves in the rewards window.
        new(RewardKind.Bomb, true, "TF3: bomba", "", 5000, 0, 0, 0, 0, 1) { UserInput = false },
        new(RewardKind.Line, true, "TF3: linia", "", 5000, 0, 0, 0, 0, 1) { UserInput = false },
        new(RewardKind.Nature, true, "TF3: natura", "", 5000, 0, 0, 0, 0, 1) { UserInput = false },
        new(RewardKind.RoadVehicle, true, "TF3: pojazd drogowy", "", 5000, 0, 0, 0, 0, 1) { UserInput = false },
        new(RewardKind.SpecialVehicle, true, "TF3: pojazd specjalny", "", 5000, 0, 0, 0, 0, 1) { UserInput = false },
    ];
}

/// <summary>
/// A reward of the streamer with the <see cref="Prefix"/> title prefix that is not one of <see cref="RewardKind"/>
/// (e.g. added by hand in the Twitch dashboard). Tf3Bridge switches it on and off with the game like its own rewards;
/// redemptions stay in the Twitch queue for the streamer. Found once when Tf3Bridge connects to Twitch.
/// <see cref="Enabled"/>: the streamer's choice; on Twitch only when also the game sends data.
/// <see cref="Switchable"/>: false when Twitch refused the change (403: it lets only the app that created a reward change it).
/// </summary>
public sealed record ExtraReward(string TwitchId, string Title, int Cost, bool Enabled, bool Switchable)
{
    public const string Prefix = "TF3";

    public static bool HasPrefix(string title) => title.TrimStart().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Rewards plus the Twitch reward IDs created for them on the streamer's channel.</summary>
public sealed record RewardSettings(IReadOnlyList<RewardConfig> Rewards, string ChannelUserId, IReadOnlyDictionary<RewardKind, string> TwitchIds)
{
    /// <summary>Answer the viewer in chat after a redemption (effect and its end date, or why the points came back).</summary>
    public bool ChatMessages { get; init; } = true;

    /// <summary>Show an in-game notification (Twitch icon) once a redemption is done and its points are taken.</summary>
    public bool GameNotifications { get; init; } = true;

    /// <summary>How long the in-game notification stays on screen; it stays in the game's notification log anyway.</summary>
    public int NotificationSeconds { get; init; } = DefaultNotificationSeconds;

    public const int DefaultNotificationSeconds = 30;
    public static readonly (int Min, int Max) NotificationSecondsRange = (3, 300);

    public int NotificationDismissMs => NotificationSeconds * 1000;

    /// <summary>Industry type ("coal_mine") -> where viewers may build it (<see cref="IndustryZones"/>); null = defaults.</summary>
    public IReadOnlyDictionary<string, IndustryZone>? Zones { get; init; }

    /// <summary>The streamer's own rewards with the TF3 prefix on <see cref="ChannelUserId"/>'s channel (<see cref="ExtraReward"/>).</summary>
    public IReadOnlyList<ExtraReward> Extras { get; init; } = [];

    public static RewardSettings Default { get; } = new(RewardKinds.Defaults, "", new Dictionary<RewardKind, string>());

    public RewardConfig Get(RewardKind kind) => Rewards.First(r => r.Kind == kind);

    // Old default "adoption" texts (before the switch to buying a town); ones changed by the streamer stay.
    private const string OldAdoptTitle = "TF3: adoptuj miasto";
    private const string OldAdoptPrompt = "Wpisz nazwę miasta bez właściciela. Zostaniesz jego właścicielem (pokaże się w grze i na mapie).";

    /// <summary>Every kind exactly once (missing ones from defaults), values normalized.</summary>
    public RewardSettings Normalized() => this with
    {
        Rewards = [.. RewardKinds.Defaults.Select(d => Rewards.FirstOrDefault(r => r.Kind == d.Kind) switch
        {
            null => d,
            { Months: <= 0 } r => Migrate(r with { Months = d.Months }, d), // a file from before days -> months
            var r => Migrate(r, d),
        }).Select(r => r.Normalized())],
        Zones = IndustryZones.Normalize(Zones),
        NotificationSeconds = NotificationSeconds <= 0 ? DefaultNotificationSeconds // a file from before this setting
            : Math.Clamp(NotificationSeconds, NotificationSecondsRange.Min, NotificationSecondsRange.Max),
    };

    private static RewardConfig Migrate(RewardConfig r, RewardConfig d) => r with
    {
        Amount = r.Amount > 0 ? r.Amount : d.Amount,
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
                Extras = s.Extras ?? [],
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
