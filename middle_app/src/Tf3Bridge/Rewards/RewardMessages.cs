using System.Globalization;
using Tf3Bridge.Commands;

namespace Tf3Bridge.Rewards;

/// <summary>Chat answers and in-game notifications after a redemption, in the app language (<see cref="Loc"/>).</summary>
public static class RewardMessages
{
    /// <summary>"$10M", "$2.5B", "$500k" / "10 mln $", "2,5 mld $", "500 tys. $" (game money, for the subsidy / loan).</summary>
    public static string Money(long amount)
    {
        var (value, unit) = Math.Abs(amount) switch
        {
            >= 1_000_000_000 => (amount / 1e9, Loc.T("B")),
            >= 1_000_000 => (amount / 1e6, Loc.T("M")),
            >= 1_000 => (amount / 1e3, Loc.T("k")),
            _ => (amount, ""),
        };
        var number = Math.Round(value, 2).ToString("0.##", Loc.Culture);
        return Loc.F("${0}{1}", number, unit);
    }

    // {0} = day, {1} = year (a language may need its own word order or grammatical case).
    private static readonly string[] Months =
    [
        Loc.N("{0} January {1}"),
        Loc.N("{0} February {1}"),
        Loc.N("{0} March {1}"),
        Loc.N("{0} April {1}"),
        Loc.N("{0} May {1}"),
        Loc.N("{0} June {1}"),
        Loc.N("{0} July {1}"),
        Loc.N("{0} August {1}"),
        Loc.N("{0} September {1}"),
        Loc.N("{0} October {1}"),
        Loc.N("{0} November {1}"),
        Loc.N("{0} December {1}"),
    ];

    /// <summary>"12 May 1922" / "12 maja 1922"</summary>
    public static string Date(GameDate d) =>
        d.Month is >= 1 and <= 12 ? Loc.F(Months[d.Month - 1], d.Day, d.Year) : $"{d.Day}.{d.Month}.{d.Year}";

    /// <param name="login">Viewer to mention (Twitch login).</param>
    /// <param name="target">Object name, or null when the reward was refused before a target was found.</param>
    /// <param name="until">End of a timed effect in the game calendar (from the mod), if known.</param>
    /// <param name="reason">Why the points were refunded (only when <paramref name="ok"/> is false).</param>
    /// <param name="link">Link to the object on the site, added after a successful redemption.</param>
    /// <param name="count">Brake sabotage: vehicles destroyed (from the mod), of <paramref name="total"/> on the line.</param>
    public static string Text(RewardConfig config, string login, string? target, bool ok, GameDate? until, string? reason, string? link = null,
        int? count = null, int? total = null)
    {
        var who = "@" + login.TrimStart('@');
        if (!ok)
            return Loc.F("{0} {1}: {2}. Your points have been refunded.", who, Label(config.Kind), reason ?? Loc.T("it didn't work"));

        var percent = RewardKinds.Percent(config.Kind, config.Factor);
        var when = until is { } d ? Loc.F("until {0} (in-game date)", Date(d)) : Loc.F("for {0} game months", config.Months);
        var text = config.Kind switch
        {
            RewardKind.AdoptTown => Loc.F("{0} the town {1} is yours now!", who, target),
            RewardKind.BuyIndustry => Loc.F("{0} the industry {1} is yours now!", who, target),
            RewardKind.BoostTown => Loc.F("{0} town boost {1} (+{2}%) runs {3}.", who, target, percent, when),
            RewardKind.BoostIndustry => Loc.F("{0} industry boost {1} (+{2}%) runs {3}.", who, target, percent, when),
            RewardKind.StopLine => Loc.F("{0} line {1} is stopped {2}.", who, target, when),
            RewardKind.HoldLine => Loc.F("{0} the vehicles of line {1} wait at the stops {2}.", who, target, when),
            RewardKind.CreateTown => Loc.F("{0} the town {1} has been founded and it's yours!", who, target),
            RewardKind.CreateIndustry => Loc.F("{0} the industry {1} has been built and it's yours!", who, target),
            RewardKind.StrikeIndustry => Loc.F("{0} the strike at {1} (-{2}% production) lasts {3}.", who, target, percent, when),
            RewardKind.TaxRaid => Loc.F("{0} the tax raid on {1} (-{2}% production) lasts {3}.", who, target, percent, when),
            RewardKind.PremiumFuel => Loc.F("{0} premium fuel on line {1} (+{2}% speed) runs {3}.", who, target, percent, when),
            RewardKind.BadFuel => Loc.F("{0} watered-down fuel on line {1} (-{2}% speed) runs {3}.", who, target, percent, when),
            RewardKind.BrakeSabotage => Loc.F("{0} brake sabotage on line {1}: {2}.", who, target, Wrecked(count, total)),
            RewardKind.Subsidy => Loc.F("{0} the subsidy of {1} has reached the company's account. Thanks!", who, target),
            RewardKind.Loan => Loc.F("{0} loan of {1} at {2}% granted: the game repays it in installments over {3} game months.", who, target, percent, config.Months),
            _ => throw new ArgumentOutOfRangeException(nameof(config)),
        };
        return Join(text, link);
    }

    private static string Join(string text, string? link) => string.IsNullOrEmpty(link) ? text : $"{text} {link}";

    /// <summary>Title of the in-game notification after a redemption.</summary>
    public static string NotificationTitle(string user, bool test) => (test ? "[TEST] " : "") + "Twitch: " + user;

    /// <summary>
    /// Text of the in-game notification after a successful redemption ("kupił(a)": gender is not guessed from the nick).
    /// <paramref name="until"/>: end of a timed effect in the game calendar, if known.
    /// </summary>
    public static string Notification(RewardConfig config, string user, string target, GameDate? until, int? count = null, int? total = null)
    {
        var percent = RewardKinds.Percent(config.Kind, config.Factor);
        var when = until is { } d ? Loc.F("until {0}", Date(d)) : Loc.F("for {0} months", config.Months);
        return config.Kind switch
        {
            RewardKind.AdoptTown => Loc.F("{0} bought the town {1}.", user, target),
            RewardKind.BuyIndustry => Loc.F("{0} bought the industry {1}.", user, target),
            RewardKind.BoostTown => Loc.F("{0} sped up the growth of {1} by +{2}% {3}.", user, target, percent, when),
            RewardKind.BoostIndustry => Loc.F("{0} raised the production of {1} by +{2}% {3}.", user, target, percent, when),
            RewardKind.StopLine => Loc.F("{0} stopped the line {1} {2}.", user, target, when),
            RewardKind.HoldLine => Loc.F("{0} made the vehicles of line {1} wait at the stops {2}.", user, target, when),
            RewardKind.CreateTown => Loc.F("{0} founded the town {1}.", user, target),
            RewardKind.CreateIndustry => Loc.F("{0} built the industry {1}.", user, target),
            RewardKind.StrikeIndustry => Loc.F("{0} started a strike at {1}: production -{2}% {3}.", user, target, percent, when),
            RewardKind.TaxRaid => Loc.F("{0} sent the tax office after {1}: production -{2}% {3}.", user, target, percent, when),
            RewardKind.PremiumFuel => Loc.F("{0} filled up premium fuel on line {1}: +{2}% speed {3}.", user, target, percent, when),
            RewardKind.BadFuel => Loc.F("{0} watered down the fuel on line {1}: -{2}% speed {3}.", user, target, percent, when),
            RewardKind.BrakeSabotage => Loc.F("Technical problems on line {0}: {1} (sabotage: {2}).", target, Wrecked(count, total), user),
            RewardKind.Subsidy => Loc.F("{0} gave the company a subsidy of {1}.", user, target),
            RewardKind.Loan => Loc.F("{0} gave the company a loan of {1} at {2}% for {3} months (installments in the Loans tab).", user, target, percent, config.Months),
            _ => throw new ArgumentOutOfRangeException(nameof(config)),
        };
    }

    /// <summary>"vehicles destroyed: 2 of 5" / "zniszczone pojazdy: 2 z 5" (count from the mod; unknown with an older mod).</summary>
    private static string Wrecked(int? count, int? total) =>
        count is not { } n ? Loc.T("vehicles were destroyed")
        : total is { } t ? Loc.F("vehicles destroyed: {0} of {1}", n, t)
        : Loc.F("vehicles destroyed: {0}", n);

    /// <summary>"https://site/#town=123": the site opens the object from the address (web/js/hash.js).</summary>
    public static string? Link(string siteUrl, RewardKind kind, long entity) =>
        RewardKinds.TargetsOwner(kind) ? null // many industries: OwnerLink gives the link to the viewer
            : Link(siteUrl, RewardKinds.Target(kind), entity);

    /// <summary>"https://site/#industry=123" for any object kind (stations: the station construction id).</summary>
    public static string? Link(string siteUrl, GameObjectKind kind, long entity)
    {
        if (string.IsNullOrWhiteSpace(siteUrl)) return null;
        var key = kind switch
        {
            GameObjectKind.Town => "town",
            GameObjectKind.Industry => "industry",
            GameObjectKind.Station => "station",
            _ => "line",
        };
        return $"{siteUrl.Trim().TrimEnd('/')}/#{key}={entity.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>"https://site/#owner=ola": the viewer in the site's Owners tab (key as <see cref="OwnerIndex.Key"/>).</summary>
    public static string? OwnerLink(string siteUrl, string ownerKey) =>
        string.IsNullOrWhiteSpace(siteUrl) ? null : $"{siteUrl.Trim().TrimEnd('/')}/#owner={Uri.EscapeDataString(ownerKey)}";

    /// <summary>
    /// "https://site/#pin=x_y&amp;note=Name": a pin where something new is being built. Fallback for the chat answer
    /// when the mod did not report the id of the new object (older mod); otherwise <see cref="Link"/>.
    /// </summary>
    public static string? PlaceLink(string siteUrl, double x, double y, string name)
    {
        if (string.IsNullOrWhiteSpace(siteUrl)) return null;
        return string.Create(CultureInfo.InvariantCulture,
            $"{siteUrl.Trim().TrimEnd('/')}/#pin={Math.Round(x)}_{Math.Round(y)}&note={Uri.EscapeDataString(name)}");
    }

    /// <summary>"tf3.example.com" from "https://tf3.example.com/" (for {site} in reward prompts).</summary>
    public static string SiteHost(string siteUrl) =>
        Uri.TryCreate(siteUrl.Trim(), UriKind.Absolute, out var u) ? u.Host + u.AbsolutePath.TrimEnd('/') : siteUrl.Trim();

    private static string Label(RewardKind kind) => kind switch
    {
        RewardKind.AdoptTown => Loc.T("buying a town"),
        RewardKind.BuyIndustry => Loc.T("buying an industry"),
        RewardKind.BoostTown => Loc.T("town boost"),
        RewardKind.BoostIndustry => Loc.T("industry boost"),
        RewardKind.StopLine => Loc.T("line stop"),
        RewardKind.HoldLine => Loc.T("line waits at stations"),
        RewardKind.CreateTown => Loc.T("new town"),
        RewardKind.CreateIndustry => Loc.T("new industry"),
        RewardKind.StrikeIndustry => Loc.T("strike"),
        RewardKind.TaxRaid => Loc.T("tax raid"),
        RewardKind.PremiumFuel => Loc.T("premium fuel"),
        RewardKind.BadFuel => Loc.T("watered-down fuel"),
        RewardKind.BrakeSabotage => Loc.T("brake sabotage"),
        RewardKind.Subsidy => Loc.T("subsidy"),
        _ => Loc.T("loan"),
    };
}
