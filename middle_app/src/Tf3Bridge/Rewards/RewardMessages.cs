using System.Globalization;
using Tf3Bridge.Commands;

namespace Tf3Bridge.Rewards;

/// <summary>Chat answers to viewers after a redemption (Polish, like the rewards).</summary>
public static class RewardMessages
{
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
    public static string Text(RewardConfig config, string login, string? target, bool ok, GameDate? until, string? reason, string? link = null)
    {
        var who = "@" + login.TrimStart('@');
        if (!ok) return $"{who} {Label(config.Kind)}: {reason ?? "nie udało się"}. Punkty wróciły do Ciebie.";

        var when = until is { } d ? $"do {Date(d)} (data w grze)" : $"przez {config.Months} mies. gry";
        var percent = Math.Round((config.Factor - 1) * 100).ToString(CultureInfo.InvariantCulture);
        var text = config.Kind switch
        {
            RewardKind.AdoptTown => $"{who} miasto {target} jest teraz Twoje!",
            RewardKind.BoostTown => $"{who} boost miasta {target} (+{percent}%) działa {when}.",
            RewardKind.BoostIndustry => $"{who} boost przemysłu {target} (+{percent}%) działa {when}.",
            RewardKind.StopLine => $"{who} linia {target} stoi {when}.",
            RewardKind.HoldLine => $"{who} pojazdy linii {target} czekają na przystankach {when}.",
            RewardKind.CreateTown => $"{who} miasto {target} zostało założone i jest Twoje!",
            _ => $"{who} zakład {target} został zbudowany i jest Twój!",
        };
        return string.IsNullOrEmpty(link) ? text : $"{text} {link}";
    }

    /// <summary>"https://site/#town=123": the site opens the object from the address (web/js/hash.js).</summary>
    public static string? Link(string siteUrl, RewardKind kind, long entity)
    {
        if (string.IsNullOrWhiteSpace(siteUrl)) return null;
        var key = RewardKinds.Target(kind) switch
        {
            GameObjectKind.Town => "town",
            GameObjectKind.Industry => "industry",
            _ => "line",
        };
        return $"{siteUrl.Trim().TrimEnd('/')}/#{key}={entity.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>"https://site/#pin=x,y&amp;note=Name": a pin where something new was built (its id is not known yet).</summary>
    public static string? PlaceLink(string siteUrl, double x, double y, string name)
    {
        if (string.IsNullOrWhiteSpace(siteUrl)) return null;
        return string.Create(CultureInfo.InvariantCulture,
            $"{siteUrl.Trim().TrimEnd('/')}/#pin={Math.Round(x)},{Math.Round(y)}&note={Uri.EscapeDataString(name)}");
    }

    /// <summary>"gin.estfosoft.com" from "https://gin.estfosoft.com/" (for {site} in reward prompts).</summary>
    public static string SiteHost(string siteUrl) =>
        Uri.TryCreate(siteUrl.Trim(), UriKind.Absolute, out var u) ? u.Host + u.AbsolutePath.TrimEnd('/') : siteUrl.Trim();

    private static string Label(RewardKind kind) => kind switch
    {
        RewardKind.AdoptTown => "kupno miasta",
        RewardKind.BoostTown => "boost miasta",
        RewardKind.BoostIndustry => "boost przemysłu",
        RewardKind.StopLine => "zatrzymanie linii",
        RewardKind.HoldLine => "linia czeka na stacjach",
        RewardKind.CreateTown => "nowe miasto",
        _ => "nowy zakład",
    };
}
