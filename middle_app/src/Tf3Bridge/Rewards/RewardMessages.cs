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
            >= 1_000_000_000 => (amount / 1e9, Loc.T("B", " mld")),
            >= 1_000_000 => (amount / 1e6, Loc.T("M", " mln")),
            >= 1_000 => (amount / 1e3, Loc.T("k", " tys.")),
            _ => (amount, ""),
        };
        var number = Math.Round(value, 2).ToString("0.##", Loc.Culture);
        return Loc.Pl ? number + unit + " $" : "$" + number + unit;
    }

    private static readonly string[] MonthsGenitive =
        ["stycznia", "lutego", "marca", "kwietnia", "maja", "czerwca", "lipca", "sierpnia", "września", "października", "listopada", "grudnia"];

    private static readonly string[] MonthsEnglish =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    /// <summary>"12 May 1922" / "12 maja 1922"</summary>
    public static string Date(GameDate d) =>
        d.Month is >= 1 and <= 12 ? $"{d.Day} {(Loc.Pl ? MonthsGenitive : MonthsEnglish)[d.Month - 1]} {d.Year}" : $"{d.Day}.{d.Month}.{d.Year}";

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
            return Loc.T($"{who} {Label(config.Kind)}: {reason ?? "it didn't work"}. Your points have been refunded.",
                $"{who} {Label(config.Kind)}: {reason ?? "nie udało się"}. Punkty wróciły do Ciebie.");

        var percent = RewardKinds.Percent(config.Kind, config.Factor);
        if (!Loc.Pl) return Join(English(config, who, target, until, percent, count, total), link);
        var when = until is { } d ? $"do {Date(d)} (data w grze)" : $"przez {config.Months} mies. gry";
        var text = config.Kind switch
        {
            RewardKind.AdoptTown => $"{who} miasto {target} jest teraz Twoje!",
            RewardKind.BuyIndustry => $"{who} zakład {target} jest teraz Twój!",
            RewardKind.BoostTown => $"{who} boost miasta {target} (+{percent}%) działa {when}.",
            RewardKind.BoostIndustry => $"{who} boost przemysłu {target} (+{percent}%) działa {when}.",
            RewardKind.StopLine => $"{who} linia {target} stoi {when}.",
            RewardKind.HoldLine => $"{who} pojazdy linii {target} czekają na przystankach {when}.",
            RewardKind.CreateTown => $"{who} miasto {target} zostało założone i jest Twoje!",
            RewardKind.CreateIndustry => $"{who} zakład {target} został zbudowany i jest Twój!",
            RewardKind.StrikeIndustry => $"{who} strajk w zakładzie {target} (-{percent}% produkcji) trwa {when}.",
            RewardKind.TaxRaid => $"{who} nalot skarbówki na {target} (-{percent}% produkcji) trwa {when}.",
            RewardKind.PremiumFuel => $"{who} paliwo premium na linii {target} (+{percent}% prędkości) działa {when}.",
            RewardKind.BadFuel => $"{who} chrzczone paliwo na linii {target} (-{percent}% prędkości) działa {when}.",
            RewardKind.BrakeSabotage => $"{who} sabotaż hamulców na linii {target}: {Wrecked(count, total)}.",
            RewardKind.Subsidy => $"{who} dotacja {target} trafiła na konto firmy. Dzięki!",
            RewardKind.Loan => $"{who} kredyt {target} na {percent}% przyznany: gra spłaca go w ratach przez {config.Months} mies. gry.",
            _ => throw new ArgumentOutOfRangeException(nameof(config)),
        };
        return Join(text, link);
    }

    private static string Join(string text, string? link) => string.IsNullOrEmpty(link) ? text : $"{text} {link}";

    private static string English(RewardConfig config, string who, string? target, GameDate? until, string percent, int? count, int? total)
    {
        var when = until is { } d ? $"until {Date(d)} (in-game date)" : $"for {config.Months} game months";
        return config.Kind switch
        {
            RewardKind.AdoptTown => $"{who} the town {target} is yours now!",
            RewardKind.BuyIndustry => $"{who} the industry {target} is yours now!",
            RewardKind.BoostTown => $"{who} town boost {target} (+{percent}%) runs {when}.",
            RewardKind.BoostIndustry => $"{who} industry boost {target} (+{percent}%) runs {when}.",
            RewardKind.StopLine => $"{who} line {target} is stopped {when}.",
            RewardKind.HoldLine => $"{who} the vehicles of line {target} wait at the stops {when}.",
            RewardKind.CreateTown => $"{who} the town {target} has been founded and it's yours!",
            RewardKind.CreateIndustry => $"{who} the industry {target} has been built and it's yours!",
            RewardKind.StrikeIndustry => $"{who} the strike at {target} (-{percent}% production) lasts {when}.",
            RewardKind.TaxRaid => $"{who} the tax raid on {target} (-{percent}% production) lasts {when}.",
            RewardKind.PremiumFuel => $"{who} premium fuel on line {target} (+{percent}% speed) runs {when}.",
            RewardKind.BadFuel => $"{who} watered-down fuel on line {target} (-{percent}% speed) runs {when}.",
            RewardKind.BrakeSabotage => $"{who} brake sabotage on line {target}: {Wrecked(count, total)}.",
            RewardKind.Subsidy => $"{who} the subsidy of {target} has reached the company's account. Thanks!",
            RewardKind.Loan => $"{who} loan of {target} at {percent}% granted: the game repays it in installments over {config.Months} game months.",
            _ => throw new ArgumentOutOfRangeException(nameof(config)),
        };
    }

    /// <summary>Title of the in-game notification after a redemption.</summary>
    public static string NotificationTitle(string user, bool test) => (test ? "[TEST] " : "") + "Twitch: " + user;

    /// <summary>
    /// Text of the in-game notification after a successful redemption ("kupił(a)": gender is not guessed from the nick).
    /// <paramref name="until"/>: end of a timed effect in the game calendar, if known.
    /// </summary>
    public static string Notification(RewardConfig config, string user, string target, GameDate? until, int? count = null, int? total = null)
    {
        var percent = RewardKinds.Percent(config.Kind, config.Factor);
        if (!Loc.Pl) return EnglishNotification(config, user, target, until, percent, count, total);
        var when = until is { } d ? $"do {Date(d)}" : $"na {config.Months} mies.";
        return config.Kind switch
        {
            RewardKind.AdoptTown => $"{user} kupił(a) miasto {target}.",
            RewardKind.BuyIndustry => $"{user} kupił(a) zakład {target}.",
            RewardKind.BoostTown => $"{user} przyspieszył(a) wzrost miasta {target} o +{percent}% {when}.",
            RewardKind.BoostIndustry => $"{user} zwiększył(a) produkcję zakładu {target} o +{percent}% {when}.",
            RewardKind.StopLine => $"{user} zatrzymał(a) linię {target} {when}.",
            RewardKind.HoldLine => $"{user} kazał(a) pojazdom linii {target} czekać na przystankach {when}.",
            RewardKind.CreateTown => $"{user} założył(a) miasto {target}.",
            RewardKind.CreateIndustry => $"{user} zbudował(a) zakład {target}.",
            RewardKind.StrikeIndustry => $"{user} wywołał(a) strajk w zakładzie {target}: produkcja -{percent}% {when}.",
            RewardKind.TaxRaid => $"{user} nasłał(a) skarbówkę na {target}: produkcja -{percent}% {when}.",
            RewardKind.PremiumFuel => $"{user} zatankował(a) paliwo premium na linii {target}: +{percent}% prędkości {when}.",
            RewardKind.BadFuel => $"{user} ochrzcił(a) paliwo na linii {target}: -{percent}% prędkości {when}.",
            RewardKind.BrakeSabotage => $"Problemy techniczne na linii {target}: {Wrecked(count, total)} (sabotaż: {user}).",
            RewardKind.Subsidy => $"{user} przekazał(a) firmie dotację {target}.",
            RewardKind.Loan => $"{user} udzielił(a) firmie kredytu {target} na {percent}% na {config.Months} mies. (raty w zakładce Kredyty).",
            _ => throw new ArgumentOutOfRangeException(nameof(config)),
        };
    }

    private static string EnglishNotification(RewardConfig config, string user, string target, GameDate? until, string percent, int? count, int? total)
    {
        var when = until is { } d ? $"until {Date(d)}" : $"for {config.Months} months";
        return config.Kind switch
        {
            RewardKind.AdoptTown => $"{user} bought the town {target}.",
            RewardKind.BuyIndustry => $"{user} bought the industry {target}.",
            RewardKind.BoostTown => $"{user} sped up the growth of {target} by +{percent}% {when}.",
            RewardKind.BoostIndustry => $"{user} raised the production of {target} by +{percent}% {when}.",
            RewardKind.StopLine => $"{user} stopped the line {target} {when}.",
            RewardKind.HoldLine => $"{user} made the vehicles of line {target} wait at the stops {when}.",
            RewardKind.CreateTown => $"{user} founded the town {target}.",
            RewardKind.CreateIndustry => $"{user} built the industry {target}.",
            RewardKind.StrikeIndustry => $"{user} started a strike at {target}: production -{percent}% {when}.",
            RewardKind.TaxRaid => $"{user} sent the tax office after {target}: production -{percent}% {when}.",
            RewardKind.PremiumFuel => $"{user} filled up premium fuel on line {target}: +{percent}% speed {when}.",
            RewardKind.BadFuel => $"{user} watered down the fuel on line {target}: -{percent}% speed {when}.",
            RewardKind.BrakeSabotage => $"Technical problems on line {target}: {Wrecked(count, total)} (sabotage: {user}).",
            RewardKind.Subsidy => $"{user} gave the company a subsidy of {target}.",
            RewardKind.Loan => $"{user} gave the company a loan of {target} at {percent}% for {config.Months} months (installments in the Loans tab).",
            _ => throw new ArgumentOutOfRangeException(nameof(config)),
        };
    }

    /// <summary>"vehicles destroyed: 2 of 5" / "zniszczone pojazdy: 2 z 5" (count from the mod; unknown with an older mod).</summary>
    private static string Wrecked(int? count, int? total) =>
        count is not { } n ? Loc.T("vehicles were destroyed", "pojazdy zostały zniszczone")
        : total is { } t ? Loc.T($"vehicles destroyed: {n} of {t}", $"zniszczone pojazdy: {n} z {t}")
        : Loc.T($"vehicles destroyed: {n}", $"zniszczone pojazdy: {n}");

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

    private static string Label(RewardKind kind) => Loc.Pl ? PolishLabel(kind) : kind switch
    {
        RewardKind.AdoptTown => "buying a town",
        RewardKind.BuyIndustry => "buying an industry",
        RewardKind.BoostTown => "town boost",
        RewardKind.BoostIndustry => "industry boost",
        RewardKind.StopLine => "line stop",
        RewardKind.HoldLine => "line waits at stations",
        RewardKind.CreateTown => "new town",
        RewardKind.CreateIndustry => "new industry",
        RewardKind.StrikeIndustry => "strike",
        RewardKind.TaxRaid => "tax raid",
        RewardKind.PremiumFuel => "premium fuel",
        RewardKind.BadFuel => "watered-down fuel",
        RewardKind.BrakeSabotage => "brake sabotage",
        RewardKind.Subsidy => "subsidy",
        _ => "loan",
    };

    private static string PolishLabel(RewardKind kind) => kind switch
    {
        RewardKind.AdoptTown => "kupno miasta",
        RewardKind.BuyIndustry => "kupno zakładu",
        RewardKind.BoostTown => "boost miasta",
        RewardKind.BoostIndustry => "boost przemysłu",
        RewardKind.StopLine => "zatrzymanie linii",
        RewardKind.HoldLine => "linia czeka na stacjach",
        RewardKind.CreateTown => "nowe miasto",
        RewardKind.CreateIndustry => "nowy zakład",
        RewardKind.StrikeIndustry => "strajk",
        RewardKind.TaxRaid => "nalot skarbówki",
        RewardKind.PremiumFuel => "paliwo premium",
        RewardKind.BadFuel => "chrzczone paliwo",
        RewardKind.BrakeSabotage => "sabotaż hamulców",
        RewardKind.Subsidy => "dotacja",
        _ => "kredyt",
    };
}
