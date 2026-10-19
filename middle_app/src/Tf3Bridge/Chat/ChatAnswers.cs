using System.Globalization;
using System.Text.Json;
using Tf3Bridge.Commands;
using Tf3Bridge.Rewards;

namespace Tf3Bridge.Chat;

/// <summary>
/// One chat command to answer. <see cref="Root"/>: the latest dynamic.json (default for commands without game data);
/// <see cref="DataAge"/>: how old that file is.
/// </summary>
internal sealed record ChatRequest(ChatAsker Asker, string Arg, string SiteUrl, JsonElement Root = default, TimeSpan? DataAge = null)
{
    public string Mention => "@" + Asker.Name;
}

/// <summary>Who asked: display name (fallback login) and login.</summary>
internal sealed record ChatAsker(string Name, string Login);

/// <summary>Texts of the informational chat commands, built from dynamic.json (Polish, like the rewards).</summary>
internal static class ChatAnswers
{
    /// <summary>Without a new export for this long the game is probably paused or closed (as RewardService).</summary>
    internal static readonly TimeSpan StaleData = TimeSpan.FromMinutes(2);

    private const int ListMax = 5;

    public static string Help(ChatRequest r) =>
        $"{r.Mention} Komendy TF3: !majatek [widz], !miasto <nazwa>, !zaklad <nazwa>, !linia <nazwa>, !ranking, "
        + "!wolne [miasta|zaklady], !najlepsza / !najgorsza [linia|firma], !firma, !data, !mapa. "
        + "Grę zmieniasz tylko punktami kanału.";

    public static string Map(ChatRequest r) =>
        string.IsNullOrWhiteSpace(r.SiteUrl) ? $"{r.Mention} mapa nie jest jeszcze ustawiona." : $"{r.Mention} mapa na żywo: {r.SiteUrl.Trim()}";

    /// <summary>"@ala estiv0: 3 miasta (Szczecin – główne, Barcin, Barczewo), 1 zakład (Browar), 2 linie, 16 pojazdów, zysk linii (12 mies.) $ 833 881. Mapa: …"</summary>
    public static string Assets(ChatRequest r)
    {
        var owners = OwnerIndex.Parse(r.Root);
        var self = r.Arg.Length == 0;
        var who = self ? r.Asker.Name : r.Arg;
        if (!owners.TryGetValue(OwnerIndex.Key(who), out var o) && !(self && owners.TryGetValue(OwnerIndex.Key(r.Asker.Login), out o)))
            return self ? $"{r.Mention} nie masz jeszcze nic w grze." : $"{r.Mention} {who} nie ma jeszcze nic w grze.";
        return AssetsText(o, r.Mention, RewardMessages.OwnerLink(r.SiteUrl, o.Key));
    }

    internal static string AssetsText(OwnerAssets o, string asker, string? link)
    {
        var parts = new List<string>();
        if (o.Towns.Count > 0)
            parts.Add($"{Count(o.Towns.Count, "miasto", "miasta", "miast")} ({Names([o.Towns[0] + " – główne", .. o.Towns.Skip(1)], 5)})");
        if (o.Industries.Count > 0)
            parts.Add($"{Count(o.Industries.Count, "zakład", "zakłady", "zakładów")} ({Names(o.Industries, 3)})");
        if (o.Stations > 0) parts.Add(Count(o.Stations, "stacja", "stacje", "stacji"));
        if (o.Lines > 0)
        {
            parts.Add(Count(o.Lines, "linia", "linie", "linii"));
            if (o.Vehicles > 0) parts.Add(Count(o.Vehicles, "pojazd", "pojazdy", "pojazdów"));
            if (o.Profit12m is { } p) parts.Add($"zysk linii (12 mies.) {Money(p)}");
        }
        var text = $"{asker} {o.Name}: {string.Join(", ", parts)}.";
        return link is null ? text : $"{text} Mapa: {link}";
    }

    /// <summary>"@ala Szczecin (właściciel: estiv0): pojemność 900, +25% od startu gry, zaspokojenie potrzeb 80%, efekty: boost miasta +50% do 12 maja 1922 (od ola). link"</summary>
    public static string Town(ChatRequest r) => Describe(r, GameObjectKind.Town, "!miasto Szczecin", (x, parts) =>
    {
        var size = Sum(x, "capacities");
        parts.Add($"pojemność {Int(size)}");
        var start = Sum(x, "initialCapacities");
        if (start > 0) parts.Add($"{Signed(size / start - 1)} od startu gry");
        if (Supply(x) is { } s) parts.Add($"zaspokojenie potrzeb {Percent(s)}");
    });

    /// <summary>"@ala Browar (właściciel: estiv0): poziom 2/4, produktywność 120%, produkcja (rok) 1 234, efekty: …. link"</summary>
    public static string Industry(ChatRequest r) => Describe(r, GameObjectKind.Industry, "!zaklad Browar", (x, parts) =>
    {
        if (Num(x, "level") is { } level)
            parts.Add(Num(x, "maxLevel") is { } max ? $"poziom {Int(level)}/{Int(max)}" : $"poziom {Int(level)}");
        if (Num(x, "productivity") is { } p) parts.Add($"produktywność {Percent(p)}");
        if (x.TryGetProperty("year", out var y) && Num(y, "produced") is { } produced) parts.Add($"produkcja (rok) {Int(produced)}");
    });

    /// <summary>"@ala Linia 1 (właściciel: estiv0): 10 pojazdów (zatrzymane: 2), zysk (12 mies.) $ 653 957, zapełnienie 64%, przewiezione (rok) 12 345. link"</summary>
    public static string Line(ChatRequest r) => Describe(r, GameObjectKind.Line, "!linia Linia 1", (x, parts) =>
    {
        if (Num(x, "vehicleCount") is { } n)
        {
            var id = Long(x, "id");
            var stopped = Items(r.Root, "vehicles").Count(v => Long(v, "line") == id && v.TryGetProperty("stopped", out var s) && s.ValueKind == JsonValueKind.True);
            parts.Add(Count((int)n, "pojazd", "pojazdy", "pojazdów") + (stopped > 0 ? $" (zatrzymane: {stopped})" : ""));
        }
        if (!x.TryGetProperty("stats", out var st) || st.ValueKind != JsonValueKind.Object) return;
        if (Num(st, "profit12m") is { } profit) parts.Add($"zysk (12 mies.) {Money(profit)}");
        if (Num(st, "load") is { } load) parts.Add($"zapełnienie {Percent(load)}");
        if (Num(st, "transported12m") is { } t) parts.Add($"przewiezione (rok) {Int(t)}");
    });

    // Shared by town, industry and line: name matching as in rewards, owner, effects, link.
    private static string Describe(ChatRequest r, GameObjectKind kind, string example, Action<JsonElement, List<string>> details)
    {
        if (r.Arg.Length == 0) return $"{r.Mention} podaj nazwę, np. {example}";
        var found = TargetResolver.Resolve(GameObjects.Parse(r.Root), kind, r.Arg);
        if (found.Target is not { } o) return $"{r.Mention} {found.Error}.";

        var key = kind switch { GameObjectKind.Town => "towns", GameObjectKind.Industry => "industries", _ => "lines" };
        var x = Items(r.Root, key).FirstOrDefault(i => Long(i, "id") == o.Entity);
        var parts = new List<string>();
        if (x.ValueKind == JsonValueKind.Object) details(x, parts);
        var effects = Effects(r.Root, o.Entity);
        if (effects.Count > 0) parts.Add("efekty: " + string.Join("; ", effects));

        var owner = o.Owner is { } who ? $" (właściciel: {who})" : " (bez właściciela)";
        var text = parts.Count > 0 ? $"{r.Mention} {o.Name}{owner}: {string.Join(", ", parts)}." : $"{r.Mention} {o.Name}{owner}.";
        return RewardMessages.Link(r.SiteUrl, kind, o.Entity) is { } link ? $"{text} {link}" : text;
    }

    /// <summary>"@ala ranking: 1. estiv0 (2 miasta, 1 zakład), 2. ola (1 linia). Wszyscy: link"</summary>
    public static string Ranking(ChatRequest r)
    {
        var owners = OwnerIndex.Parse(r.Root).Values
            .OrderByDescending(o => o.Score).ThenByDescending(o => o.Profit12m ?? double.MinValue)
            .ThenBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(ListMax).ToList();
        if (owners.Count == 0) return $"{r.Mention} nikt jeszcze nic nie kupił. Zobacz !wolne";

        static string Summary(OwnerAssets o)
        {
            var parts = new List<string>();
            if (o.Towns.Count > 0) parts.Add(Count(o.Towns.Count, "miasto", "miasta", "miast"));
            if (o.Industries.Count > 0) parts.Add(Count(o.Industries.Count, "zakład", "zakłady", "zakładów"));
            if (o.Lines > 0) parts.Add(Count(o.Lines, "linia", "linie", "linii"));
            if (parts.Count == 0 && o.Stations > 0) parts.Add(Count(o.Stations, "stacja", "stacje", "stacji"));
            return $"{o.Name} ({string.Join(", ", parts)})";
        }
        var text = $"{r.Mention} ranking: {string.Join(", ", owners.Select((o, i) => $"{i + 1}. {Summary(o)}"))}.";
        return string.IsNullOrWhiteSpace(r.SiteUrl) ? text : $"{text} Wszyscy: {r.SiteUrl.Trim()}";
    }

    /// <summary>"@ala wolne miasta (12): Łódź, Kraków, … i 7 inne; wolne zakłady (3): …. Kupisz je za punkty kanału."</summary>
    public static string Free(ChatRequest r)
    {
        var arg = TargetResolver.Normalize(r.Arg);
        bool towns = true, industries = true;
        if (arg.StartsWith("miast", StringComparison.Ordinal)) industries = false;
        else if (arg.StartsWith("zaklad", StringComparison.Ordinal)) towns = false;
        else if (arg.Length > 0) return $"{r.Mention} użyj: !wolne [miasta|zaklady]";

        static bool Free(JsonElement x) => Str(x, "name") is { } n && OwnerName.Split(n).Owner is null;
        static string Name(JsonElement x) => OwnerName.Split(Str(x, "name")!).Name;
        var parts = new List<string>();
        if (towns)
        {
            var list = Items(r.Root, "towns").Where(Free).OrderByDescending(x => Sum(x, "capacities")).Select(Name).ToList();
            parts.Add(list.Count == 0 ? "wszystkie miasta mają właściciela" : $"wolne miasta ({list.Count}): {Names(list, ListMax)}");
        }
        if (industries)
        {
            var list = Items(r.Root, "industries").Where(Free).OrderByDescending(x => Num(x, "level") ?? 0)
                .ThenBy(Name, StringComparer.CurrentCultureIgnoreCase).Select(Name).ToList();
            parts.Add(list.Count == 0 ? "wszystkie zakłady mają właściciela" : $"wolne zakłady ({list.Count}): {Names(list, ListMax)}");
        }
        return $"{r.Mention} {string.Join("; ", parts)}. Kupisz je za punkty kanału.";
    }

    /// <summary>"@ala w grze jest 12 maja 1922." (+ note when the data is old: paused or closed game)</summary>
    public static string Date(ChatRequest r)
    {
        var d = r.Root.TryGetProperty("date", out var date) ? Date(date) : null;
        var text = d is null ? $"{r.Mention} nie znam daty w grze." : $"{r.Mention} w grze jest {RewardMessages.Date(d)}.";
        if (r.DataAge is { } age && age >= StaleData)
            text += $" Ostatnie dane z gry sprzed {(int)age.TotalMinutes} min (pauza albo gra wyłączona).";
        return text;
    }

    /// <summary>"@ala firma streamera: saldo $ 1 234 567, wartość firmy $ 9 876 543, zysk w 1923 $ 120 000 (1922: $ -50 000), 12 linii, 80 pojazdów."</summary>
    public static string Company(ChatRequest r)
    {
        var parts = new List<string>();
        if (r.Root.TryGetProperty("player", out var player))
            parts.Add(Num(player, "balance") is { } b ? $"saldo {Money(b)}" : "saldo bez limitu");
        if (r.Root.TryGetProperty("company", out var company) && company.ValueKind == JsonValueKind.Object)
        {
            if (company.TryGetProperty("history", out var h) && h.ValueKind == JsonValueKind.Object
                && h.TryGetProperty("companyTotalValue", out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0
                && value[value.GetArrayLength() - 1] is { ValueKind: JsonValueKind.Array } last && last.GetArrayLength() >= 2
                && last[1].TryGetDouble(out var v))
                parts.Add($"wartość firmy {Money(v)}");
            if (company.TryGetProperty("finance", out var f) && f.ValueKind == JsonValueKind.Object)
            {
                var years = Strings(f, "header");
                var total = Numbers(f, "total");
                var n = Math.Min(years.Count, total.Count);
                if (n > 0)
                    parts.Add($"zysk w {years[n - 1]} {Money(total[n - 1])}" + (n > 1 ? $" ({years[n - 2]}: {Money(total[n - 2])})" : ""));
            }
        }
        var lines = Items(r.Root, "lines").Count;
        if (lines > 0) parts.Add(Count(lines, "linia", "linie", "linii"));
        if (r.Root.TryGetProperty("vehicles", out var vs) && vs.ValueKind == JsonValueKind.Array)
            parts.Add(Count(vs.GetArrayLength(), "pojazd", "pojazdy", "pojazdów"));
        return parts.Count == 0 ? $"{r.Mention} nie mam jeszcze danych o firmie." : $"{r.Mention} firma streamera: {string.Join(", ", parts)}.";
    }

    /// <summary>
    /// "!najlepsza [linia|firma]" / "!najgorsza": the line, or the viewer's company (all lines of one owner),
    /// with the highest / lowest profit over 12 months.
    /// </summary>
    public static string Extreme(ChatRequest r, bool best)
    {
        var word = best ? "najlepsza" : "najgorsza";
        var arg = TargetResolver.Normalize(r.Arg);
        if (arg.Length == 0 || arg.StartsWith("lini", StringComparison.Ordinal))
        {
            var lines = Items(r.Root, "lines")
                .Select(l => (Line: l, Profit: l.TryGetProperty("stats", out var st) && st.ValueKind == JsonValueKind.Object ? Num(st, "profit12m") : null))
                .Where(x => x.Profit is not null && Str(x.Line, "name") is not null).ToList();
            if (lines.Count == 0) return $"{r.Mention} nie mam jeszcze wyników linii.";
            var (l, profit) = best ? lines.MaxBy(x => x.Profit) : lines.MinBy(x => x.Profit);
            var (name, owner) = OwnerName.Split(Str(l, "name")!);
            var text = $"{r.Mention} {word} linia: {name}{(owner is null ? "" : $" ({owner})")}, zysk (12 mies.) {Money(profit!.Value)}.";
            return Long(l, "id") is { } id && RewardMessages.Link(r.SiteUrl, GameObjectKind.Line, id) is { } link ? $"{text} {link}" : text;
        }
        if (arg.StartsWith("firm", StringComparison.Ordinal) || arg.StartsWith("widz", StringComparison.Ordinal))
        {
            var owners = OwnerIndex.Parse(r.Root).Values.Where(o => o.Profit12m is not null).ToList();
            if (owners.Count == 0) return $"{r.Mention} żaden widz nie ma jeszcze linii z wynikiem.";
            var o = best ? owners.MaxBy(x => x.Profit12m) : owners.MinBy(x => x.Profit12m);
            var text = $"{r.Mention} {word} firma widza: {o!.Name}, zysk linii (12 mies.) {Money(o.Profit12m!.Value)} ({Count(o.Lines, "linia", "linie", "linii")}).";
            return RewardMessages.OwnerLink(r.SiteUrl, o.Key) is { } link ? $"{text} {link}" : text;
        }
        return $"{r.Mention} użyj: !{word} [linia|firma]";
    }

    // Active reward effects on the object: "boost miasta +50% do 12 maja 1922 (od ola)".
    private static List<string> Effects(JsonElement root, long entity) =>
        [.. Items(root, "effects").Where(e => Long(e, "entity") == entity).Select(e =>
        {
            var text = Str(e, "type") switch
            {
                "townBoost" => "boost miasta",
                "industryBoost" => "boost przemysłu",
                "lineStop" => "linia zatrzymana",
                "lineHold" => "linia czeka na stacjach",
                "industryStrike" => "strajk",
                "industryTaxRaid" => "nalot skarbówki",
                "lineFuelPremium" => "paliwo premium",
                "lineFuelBad" => "chrzczone paliwo",
                var t => t ?? "efekt",
            };
            if (Num(e, "factor") is { } f && f > 0) text += " " + Signed(f - 1);
            if (e.TryGetProperty("until", out var u) && Date(u) is { } d) text += $" do {RewardMessages.Date(d)}";
            if (Str(e, "user") is { } user) text += $" (od {user})";
            return text;
        })];

    // Town needs satisfaction as on the website (townSupply in web/js/sidebar.js).
    private static double? Supply(JsonElement town)
    {
        double supplied = 0, demand = 0;
        foreach (var s in Items(town, "supply"))
            if (Num(s, "demand") is { } d && d > 0 && Num(s, "supply") is { } v)
            {
                supplied += Math.Min(v, d);
                demand += d;
            }
        return demand > 0 ? supplied / demand : null;
    }

    // The first `max` names, the rest as "i 4 inne" ("and 4 others").
    internal static string Names(IReadOnlyList<string> names, int max) =>
        names.Count <= max ? string.Join(", ", names) : $"{string.Join(", ", names.Take(max))} i {names.Count - max} inne";

    /// <summary>Polish plural: 1 miasto, 2 miasta, 5 miast, 22 miasta.</summary>
    internal static string Count(int n, string one, string few, string many)
    {
        var word = n == 1 ? one : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? few : many;
        return $"{n} {word}";
    }

    private static readonly CultureInfo Pl = CultureInfo.GetCultureInfo("pl-PL");

    // As on the website: "1 234 567" with a plain space (pl-PL gives a non-breaking one), halves rounded up like Math.round in JS.
    private static string Int(double v) => Math.Round(v, MidpointRounding.AwayFromZero).ToString("#,0", Pl).Replace(' ', ' ');

    // As on the website: "$ 833 881", "$ -457 279".
    internal static string Money(double v) => "$ " + Int(v);

    private static string Percent(double v) => $"{Int(v * 100)}%";

    private static string Signed(double v) => (v >= 0 ? "+" : "-") + Percent(Math.Abs(v));

    private static GameDate? Date(JsonElement d) =>
        d.ValueKind == JsonValueKind.Object && Num(d, "year") is { } y && Num(d, "month") is { } m && Num(d, "day") is { } day
            ? new GameDate((int)y, (int)m, (int)day) : null;

    private static List<JsonElement> Items(JsonElement x, string key) =>
        x.ValueKind == JsonValueKind.Object && x.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array
            ? [.. a.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object)] : [];

    private static double Sum(JsonElement x, string key) =>
        x.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Number).Sum(v => v.GetDouble()) : 0;

    private static List<double> Numbers(JsonElement x, string key) =>
        x.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array
            ? [.. a.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0)] : [];

    private static List<string> Strings(JsonElement x, string key) =>
        x.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array ? [.. a.EnumerateArray().Select(v => v.ToString())] : [];

    private static string? Str(JsonElement x, string key) =>
        x.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static double? Num(JsonElement x, string key) =>
        x.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static long? Long(JsonElement x, string key) =>
        x.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var id) ? id : null;
}
