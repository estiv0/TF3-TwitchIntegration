using System.Globalization;
using Tf3Bridge.Commands;

namespace Tf3Bridge.Rewards;

/// <summary>Chat answers to viewers after a redemption (Polish, like the rewards).</summary>
public static class RewardMessages
{
    /// <summary>Decimal comma for amounts and interest ("2,5 mln", "1,5%").</summary>
    public static readonly CultureInfo Polish = CultureInfo.GetCultureInfo("pl-PL");

    /// <summary>"10 mln $", "2,5 mld $", "500 tys. $" (game money, for dotacja / kredyt).</summary>
    public static string Money(long amount)
    {
        var (value, unit) = Math.Abs(amount) switch
        {
            >= 1_000_000_000 => (amount / 1e9, " mld"),
            >= 1_000_000 => (amount / 1e6, " mln"),
            >= 1_000 => (amount / 1e3, " tys."),
            _ => (amount, ""),
        };
        return Math.Round(value, 2).ToString("0.##", Polish) + unit + " $";
    }

    private static readonly string[] MonthsGenitive =
        ["stycznia", "lutego", "marca", "kwietnia", "maja", "czerwca", "lipca", "sierpnia", "września", "października", "listopada", "grudnia"];

    /// <summary>"12 maja 1922"</summary>
    public static string Date(GameDate d) =>
        d.Month is >= 1 and <= 12 ? $"{d.Day} {MonthsGenitive[d.Month - 1]} {d.Year}" : $"{d.Day}.{d.Month}.{d.Year}";

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
        if (!ok) return $"{who} {Label(config.Kind)}: {reason ?? "nie udało się"}. Punkty wróciły do Ciebie.";

        var when = until is { } d ? $"do {Date(d)} (data w grze)" : $"przez {config.Months} mies. gry";
        var percent = RewardKinds.Percent(config.Kind, config.Factor);
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
        return string.IsNullOrEmpty(link) ? text : $"{text} {link}";
    }

    /// <summary>Title of the in-game notification after a redemption.</summary>
    public static string NotificationTitle(string user, bool test) => (test ? "[TEST] " : "") + "Twitch: " + user;

    /// <summary>
    /// Text of the in-game notification after a successful redemption ("kupił(a)": gender is not guessed from the nick).
    /// <paramref name="until"/>: end of a timed effect in the game calendar, if known.
    /// </summary>
    public static string Notification(RewardConfig config, string user, string target, GameDate? until, int? count = null, int? total = null)
    {
        var when = until is { } d ? $"do {Date(d)}" : $"na {config.Months} mies.";
        var percent = RewardKinds.Percent(config.Kind, config.Factor);
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

    /// <summary>"zniszczone pojazdy: 2 z 5" (count from the mod; unknown with an older mod).</summary>
    private static string Wrecked(int? count, int? total) =>
        count is not { } n ? "pojazdy zostały zniszczone"
        : total is { } t ? $"zniszczone pojazdy: {n} z {t}"
        : $"zniszczone pojazdy: {n}";

    /// <summary>"https://site/#town=123": the site opens the object from the address (web/js/hash.js).</summary>
    public static string? Link(string siteUrl, RewardKind kind, long entity) =>
        RewardKinds.TargetsOwner(kind) ? null // wiele zakładów: link do widza daje OwnerLink
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
