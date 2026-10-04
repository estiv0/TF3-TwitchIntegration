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
    public static string Text(RewardConfig config, string login, string? target, bool ok, GameDate? until, string? reason)
    {
        var who = "@" + login.TrimStart('@');
        if (!ok) return $"{who} {Label(config.Kind)}: {reason ?? "nie udało się"}. Punkty wróciły do Ciebie.";

        var when = until is { } d ? $"do {Date(d)} (data w grze)" : $"przez {config.Months} mies. gry";
        var percent = Math.Round((config.Factor - 1) * 100).ToString(CultureInfo.InvariantCulture);
        return config.Kind switch
        {
            RewardKind.AdoptTown => $"{who} miasto {target} jest teraz Twoje! Widać to w grze i na mapie.",
            RewardKind.BoostTown => $"{who} boost miasta {target} (+{percent}%) działa {when}.",
            RewardKind.BoostIndustry => $"{who} boost przemysłu {target} (+{percent}%) działa {when}.",
            RewardKind.StopLine => $"{who} linia {target} stoi {when}.",
            _ => $"{who} pojazdy linii {target} czekają na przystankach {when}.",
        };
    }

    private static string Label(RewardKind kind) => kind switch
    {
        RewardKind.AdoptTown => "adopcja miasta",
        RewardKind.BoostTown => "boost miasta",
        RewardKind.BoostIndustry => "boost przemysłu",
        RewardKind.StopLine => "zatrzymanie linii",
        _ => "linia czeka na stacjach",
    };
}
