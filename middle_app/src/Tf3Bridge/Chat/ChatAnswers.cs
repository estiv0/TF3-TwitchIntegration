using System.Text.Json;
using Tf3Bridge.Commands;
using Tf3Bridge.Rewards;

namespace Tf3Bridge.Chat;

/// <summary>
/// One chat command to answer. <see cref="Root"/>: the latest dynamic.json (default for commands without game data);
/// <see cref="DataAge"/>: how old that file is. <see cref="Commands"/>: the configured names, for answers that point to other commands.
/// </summary>
internal sealed record ChatRequest(ChatAsker Asker, string Arg, string SiteUrl, JsonElement Root = default, TimeSpan? DataAge = null)
{
    public string Mention => "@" + Asker.Name;

    public ChatCommandSettings Commands { get; init; } = ChatCommandSettings.Default;

    /// <summary>"!miasto" (the configured name), also when the command is off: the asker just used it.</summary>
    public string Name(ChatCommandKind kind) => "!" + Commands.Get(kind).Name;
}

/// <summary>Who asked: display name (fallback login) and login.</summary>
internal sealed record ChatAsker(string Name, string Login);

/// <summary>Texts of the informational chat commands, built from dynamic.json, in the app language (<see cref="Loc"/>).</summary>
internal static class ChatAnswers
{
    /// <summary>Without a new export for this long the game is probably paused or closed (as RewardService).</summary>
    internal static readonly TimeSpan StaleData = TimeSpan.FromMinutes(2);

    private const int ListMax = 5;

    // Order of the list in !help (help itself is not listed).
    private static readonly ChatCommandKind[] HelpOrder =
    [
        ChatCommandKind.Assets, ChatCommandKind.Town, ChatCommandKind.Industry, ChatCommandKind.Line, ChatCommandKind.Ranking,
        ChatCommandKind.Free, ChatCommandKind.Best, ChatCommandKind.Worst, ChatCommandKind.Company, ChatCommandKind.Date, ChatCommandKind.Map,
    ];

    /// <summary>"@ala TF3 commands: !assets [viewer], !town &lt;name&gt;, …, !best / !worst [line|company], …": only the commands that are on, with their configured names.</summary>
    public static string Help(ChatRequest r)
    {
        var items = new List<string>();
        foreach (var kind in HelpOrder)
        {
            if (r.Commands.Shown(kind) is not { } name) continue;
            // "!best / !worst [line|company]": one entry when both are on.
            if (kind == ChatCommandKind.Worst && r.Commands.Shown(ChatCommandKind.Best) is not null) continue;
            if (kind == ChatCommandKind.Best && r.Commands.Shown(ChatCommandKind.Worst) is { } worst) name += " / " + worst;
            items.Add(name + ChatCommandKinds.Usage(kind));
        }
        var list = items.Count > 0 ? string.Join(", ", items) + ". " : "";
        return Loc.T($"{r.Mention} TF3 commands: {list}Only channel points change the game.",
            $"{r.Mention} Komendy TF3: {list}Grę zmieniasz tylko punktami kanału.");
    }

    public static string Map(ChatRequest r) =>
        string.IsNullOrWhiteSpace(r.SiteUrl)
            ? Loc.T($"{r.Mention} the map isn't set up yet.", $"{r.Mention} mapa nie jest jeszcze ustawiona.")
            : Loc.T($"{r.Mention} live map: {r.SiteUrl.Trim()}", $"{r.Mention} mapa na żywo: {r.SiteUrl.Trim()}");

    /// <summary>"@ala estiv0: 3 towns (Szczecin – main, Barcin, Barczewo), 1 industry (Brewery), 2 lines, 16 vehicles, line profit (12 mo.) $833,881. Map: …"</summary>
    public static string Assets(ChatRequest r)
    {
        var owners = OwnerIndex.Parse(r.Root);
        var self = r.Arg.Length == 0;
        var who = self ? r.Asker.Name : r.Arg;
        if (!owners.TryGetValue(OwnerIndex.Key(who), out var o) && !(self && owners.TryGetValue(OwnerIndex.Key(r.Asker.Login), out o)))
            return self ? Loc.T($"{r.Mention} you don't own anything in the game yet.", $"{r.Mention} nie masz jeszcze nic w grze.")
                : Loc.T($"{r.Mention} {who} doesn't own anything in the game yet.", $"{r.Mention} {who} nie ma jeszcze nic w grze.");
        return AssetsText(o, r.Mention, RewardMessages.OwnerLink(r.SiteUrl, o.Key));
    }

    internal static string AssetsText(OwnerAssets o, string asker, string? link)
    {
        var parts = new List<string>();
        if (o.Towns.Count > 0)
            parts.Add($"{Towns(o.Towns.Count)} ({Names([o.Towns[0] + Loc.T(" – main", " – główne"), .. o.Towns.Skip(1)], 5)})");
        if (o.Industries.Count > 0)
            parts.Add($"{Industries(o.Industries.Count)} ({Names(o.Industries, 3)})");
        if (o.Stations > 0) parts.Add(Stations(o.Stations));
        if (o.Lines > 0)
        {
            parts.Add(Lines(o.Lines));
            if (o.Vehicles > 0) parts.Add(Vehicles(o.Vehicles));
            if (o.Profit12m is { } p) parts.Add(Loc.T($"line profit (12 mo.) {Money(p)}", $"zysk linii (12 mies.) {Money(p)}"));
        }
        var text = $"{asker} {o.Name}: {string.Join(", ", parts)}.";
        return link is null ? text : $"{text} {Loc.T("Map", "Mapa")}: {link}";
    }

    /// <summary>"@ala Szczecin (owner: estiv0): capacity 900, +25% since game start, needs met 80%, effects: town boost +50% until 12 May 1922 (from ola). link"</summary>
    public static string Town(ChatRequest r) => Describe(r, GameObjectKind.Town, r.Name(ChatCommandKind.Town) + " Szczecin", (x, parts) =>
    {
        var size = Sum(x, "capacities");
        parts.Add(Loc.T($"capacity {Int(size)}", $"pojemność {Int(size)}"));
        var start = Sum(x, "initialCapacities");
        if (start > 0) parts.Add(Loc.T($"{Signed(size / start - 1)} since game start", $"{Signed(size / start - 1)} od startu gry"));
        if (Supply(x) is { } s) parts.Add(Loc.T($"needs met {Percent(s)}", $"zaspokojenie potrzeb {Percent(s)}"));
    });

    /// <summary>"@ala Brewery (owner: estiv0): level 2/4, productivity 120%, production (year) 1,234, effects: …. link"</summary>
    public static string Industry(ChatRequest r) => Describe(r, GameObjectKind.Industry, r.Name(ChatCommandKind.Industry) + Loc.T(" Brewery", " Browar"), (x, parts) =>
    {
        if (Num(x, "level") is { } level)
        {
            var value = Num(x, "maxLevel") is { } max ? $"{Int(level)}/{Int(max)}" : Int(level);
            parts.Add(Loc.T($"level {value}", $"poziom {value}"));
        }
        if (Num(x, "productivity") is { } p) parts.Add(Loc.T($"productivity {Percent(p)}", $"produktywność {Percent(p)}"));
        if (x.TryGetProperty("year", out var y) && Num(y, "produced") is { } produced)
            parts.Add(Loc.T($"production (year) {Int(produced)}", $"produkcja (rok) {Int(produced)}"));
    });

    /// <summary>"@ala Line 1 (owner: estiv0): 10 vehicles (stopped: 2), profit (12 mo.) $653,957, load 64%, transported (year) 12,345. link"</summary>
    public static string Line(ChatRequest r) => Describe(r, GameObjectKind.Line, r.Name(ChatCommandKind.Line) + Loc.T(" Line 1", " Linia 1"), (x, parts) =>
    {
        if (Num(x, "vehicleCount") is { } n)
        {
            var id = Long(x, "id");
            var stopped = Items(r.Root, "vehicles").Count(v => Long(v, "line") == id && v.TryGetProperty("stopped", out var s) && s.ValueKind == JsonValueKind.True);
            parts.Add(Vehicles((int)n) + (stopped > 0 ? Loc.T($" (stopped: {stopped})", $" (zatrzymane: {stopped})") : ""));
        }
        if (!x.TryGetProperty("stats", out var st) || st.ValueKind != JsonValueKind.Object) return;
        if (Num(st, "profit12m") is { } profit) parts.Add(Loc.T($"profit (12 mo.) {Money(profit)}", $"zysk (12 mies.) {Money(profit)}"));
        if (Num(st, "load") is { } load) parts.Add(Loc.T($"load {Percent(load)}", $"zapełnienie {Percent(load)}"));
        if (Num(st, "transported12m") is { } t) parts.Add(Loc.T($"transported (year) {Int(t)}", $"przewiezione (rok) {Int(t)}"));
    });

    // Shared by town, industry and line: name matching as in rewards, owner, effects, link.
    private static string Describe(ChatRequest r, GameObjectKind kind, string example, Action<JsonElement, List<string>> details)
    {
        if (r.Arg.Length == 0) return Loc.T($"{r.Mention} give a name, e.g. {example}", $"{r.Mention} podaj nazwę, np. {example}");
        var found = TargetResolver.Resolve(GameObjects.Parse(r.Root), kind, r.Arg);
        if (found.Target is not { } o) return $"{r.Mention} {found.Error}.";

        var key = kind switch { GameObjectKind.Town => "towns", GameObjectKind.Industry => "industries", _ => "lines" };
        var x = Items(r.Root, key).FirstOrDefault(i => Long(i, "id") == o.Entity);
        var parts = new List<string>();
        if (x.ValueKind == JsonValueKind.Object) details(x, parts);
        var effects = Effects(r.Root, o.Entity);
        if (effects.Count > 0) parts.Add(Loc.T("effects: ", "efekty: ") + string.Join("; ", effects));

        var owner = o.Owner is { } who ? Loc.T($" (owner: {who})", $" (właściciel: {who})") : Loc.T(" (no owner)", " (bez właściciela)");
        var text = parts.Count > 0 ? $"{r.Mention} {o.Name}{owner}: {string.Join(", ", parts)}." : $"{r.Mention} {o.Name}{owner}.";
        return RewardMessages.Link(r.SiteUrl, kind, o.Entity) is { } link ? $"{text} {link}" : text;
    }

    /// <summary>"@ala ranking: 1. estiv0 (2 towns, 1 industry), 2. ola (1 line). Everyone: link"</summary>
    public static string Ranking(ChatRequest r)
    {
        var owners = OwnerIndex.Parse(r.Root).Values
            .OrderByDescending(o => o.Score).ThenByDescending(o => o.Profit12m ?? double.MinValue)
            .ThenBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(ListMax).ToList();
        if (owners.Count == 0)
            return Loc.T($"{r.Mention} nobody has bought anything yet.", $"{r.Mention} nikt jeszcze nic nie kupił.")
                + (r.Commands.Shown(ChatCommandKind.Free) is { } free ? Loc.T($" See {free}", $" Zobacz {free}") : "");

        static string Summary(OwnerAssets o)
        {
            var parts = new List<string>();
            if (o.Towns.Count > 0) parts.Add(Towns(o.Towns.Count));
            if (o.Industries.Count > 0) parts.Add(Industries(o.Industries.Count));
            if (o.Lines > 0) parts.Add(Lines(o.Lines));
            if (parts.Count == 0 && o.Stations > 0) parts.Add(Stations(o.Stations));
            return $"{o.Name} ({string.Join(", ", parts)})";
        }
        var text = $"{r.Mention} ranking: {string.Join(", ", owners.Select((o, i) => $"{i + 1}. {Summary(o)}"))}.";
        return string.IsNullOrWhiteSpace(r.SiteUrl) ? text : $"{text} {Loc.T("Everyone", "Wszyscy")}: {r.SiteUrl.Trim()}";
    }

    /// <summary>"@ala free towns (12): Łódź, Kraków, … and 7 more; free industries (3): …. Buy them with channel points."</summary>
    public static string Free(ChatRequest r)
    {
        var arg = TargetResolver.Normalize(r.Arg);
        bool towns = true, industries = true;
        if (arg.StartsWith("miast", StringComparison.Ordinal) || arg.StartsWith("town", StringComparison.Ordinal)) industries = false;
        else if (arg.StartsWith("zaklad", StringComparison.Ordinal) || arg.StartsWith("industr", StringComparison.Ordinal)) towns = false;
        else if (arg.Length > 0) return Loc.T($"{r.Mention} use: ", $"{r.Mention} użyj: ") + r.Name(ChatCommandKind.Free) + ChatCommandKinds.Usage(ChatCommandKind.Free);

        static bool Free(JsonElement x) => Str(x, "name") is { } n && OwnerName.Split(n).Owner is null;
        static string Name(JsonElement x) => OwnerName.Split(Str(x, "name")!).Name;
        var parts = new List<string>();
        if (towns)
        {
            var list = Items(r.Root, "towns").Where(Free).OrderByDescending(x => Sum(x, "capacities")).Select(Name).ToList();
            parts.Add(list.Count == 0 ? Loc.T("all towns have an owner", "wszystkie miasta mają właściciela")
                : Loc.T($"free towns ({list.Count}): {Names(list, ListMax)}", $"wolne miasta ({list.Count}): {Names(list, ListMax)}"));
        }
        if (industries)
        {
            var list = Items(r.Root, "industries").Where(Free).OrderByDescending(x => Num(x, "level") ?? 0)
                .ThenBy(Name, StringComparer.CurrentCultureIgnoreCase).Select(Name).ToList();
            parts.Add(list.Count == 0 ? Loc.T("all industries have an owner", "wszystkie zakłady mają właściciela")
                : Loc.T($"free industries ({list.Count}): {Names(list, ListMax)}", $"wolne zakłady ({list.Count}): {Names(list, ListMax)}"));
        }
        return $"{r.Mention} {string.Join("; ", parts)}. {Loc.T("Buy them with channel points.", "Kupisz je za punkty kanału.")}";
    }

    /// <summary>"@ala in the game it's 12 May 1922." (+ note when the data is old: paused or closed game)</summary>
    public static string Date(ChatRequest r)
    {
        var d = r.Root.TryGetProperty("date", out var date) ? Date(date) : null;
        var text = d is null ? Loc.T($"{r.Mention} I don't know the in-game date.", $"{r.Mention} nie znam daty w grze.")
            : Loc.T($"{r.Mention} in the game it's {RewardMessages.Date(d)}.", $"{r.Mention} w grze jest {RewardMessages.Date(d)}.");
        if (r.DataAge is { } age && age >= StaleData)
            text += Loc.T($" The last game data is {(int)age.TotalMinutes} min old (paused or the game is off).",
                $" Ostatnie dane z gry sprzed {(int)age.TotalMinutes} min (pauza albo gra wyłączona).");
        return text;
    }

    /// <summary>"@ala the streamer's company: balance $1,234,567, company value $9,876,543, profit in 1923 $120,000 (1922: $-50,000), 12 lines, 80 vehicles."</summary>
    public static string Company(ChatRequest r)
    {
        var parts = new List<string>();
        if (r.Root.TryGetProperty("player", out var player))
            parts.Add(Num(player, "balance") is { } b ? Loc.T($"balance {Money(b)}", $"saldo {Money(b)}") : Loc.T("unlimited balance", "saldo bez limitu"));
        if (r.Root.TryGetProperty("company", out var company) && company.ValueKind == JsonValueKind.Object)
        {
            if (company.TryGetProperty("history", out var h) && h.ValueKind == JsonValueKind.Object
                && h.TryGetProperty("companyTotalValue", out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0
                && value[value.GetArrayLength() - 1] is { ValueKind: JsonValueKind.Array } last && last.GetArrayLength() >= 2
                && last[1].TryGetDouble(out var v))
                parts.Add(Loc.T($"company value {Money(v)}", $"wartość firmy {Money(v)}"));
            if (company.TryGetProperty("finance", out var f) && f.ValueKind == JsonValueKind.Object)
            {
                var years = Strings(f, "header");
                var total = Numbers(f, "total");
                var n = Math.Min(years.Count, total.Count);
                if (n > 0)
                    parts.Add(Loc.T($"profit in {years[n - 1]} {Money(total[n - 1])}", $"zysk w {years[n - 1]} {Money(total[n - 1])}")
                        + (n > 1 ? $" ({years[n - 2]}: {Money(total[n - 2])})" : ""));
            }
        }
        var lines = Items(r.Root, "lines").Count;
        if (lines > 0) parts.Add(Lines(lines));
        if (r.Root.TryGetProperty("vehicles", out var vs) && vs.ValueKind == JsonValueKind.Array)
            parts.Add(Vehicles(vs.GetArrayLength()));
        return parts.Count == 0 ? Loc.T($"{r.Mention} I have no company data yet.", $"{r.Mention} nie mam jeszcze danych o firmie.")
            : Loc.T($"{r.Mention} the streamer's company: {string.Join(", ", parts)}.", $"{r.Mention} firma streamera: {string.Join(", ", parts)}.");
    }

    /// <summary>
    /// "!best [line|company]" / "!worst": the line, or the viewer's company (all lines of one owner),
    /// with the highest / lowest profit over 12 months.
    /// </summary>
    public static string Extreme(ChatRequest r, bool best)
    {
        var word = best ? Loc.T("best", "najlepsza") : Loc.T("worst", "najgorsza");
        var arg = TargetResolver.Normalize(r.Arg);
        if (arg.Length == 0 || arg.StartsWith("lini", StringComparison.Ordinal) || arg.StartsWith("line", StringComparison.Ordinal))
        {
            var lines = Items(r.Root, "lines")
                .Select(l => (Line: l, Profit: l.TryGetProperty("stats", out var st) && st.ValueKind == JsonValueKind.Object ? Num(st, "profit12m") : null))
                .Where(x => x.Profit is not null && Str(x.Line, "name") is not null).ToList();
            if (lines.Count == 0) return Loc.T($"{r.Mention} I have no line results yet.", $"{r.Mention} nie mam jeszcze wyników linii.");
            var (l, profit) = best ? lines.MaxBy(x => x.Profit) : lines.MinBy(x => x.Profit);
            var (name, owner) = OwnerName.Split(Str(l, "name")!);
            var by = owner is null ? "" : $" ({owner})";
            var text = Loc.T($"{r.Mention} {word} line: {name}{by}, profit (12 mo.) {Money(profit!.Value)}.",
                $"{r.Mention} {word} linia: {name}{by}, zysk (12 mies.) {Money(profit!.Value)}.");
            return Long(l, "id") is { } id && RewardMessages.Link(r.SiteUrl, GameObjectKind.Line, id) is { } link ? $"{text} {link}" : text;
        }
        if (arg.StartsWith("firm", StringComparison.Ordinal) || arg.StartsWith("widz", StringComparison.Ordinal)
            || arg.StartsWith("compan", StringComparison.Ordinal) || arg.StartsWith("viewer", StringComparison.Ordinal))
        {
            var owners = OwnerIndex.Parse(r.Root).Values.Where(o => o.Profit12m is not null).ToList();
            if (owners.Count == 0) return Loc.T($"{r.Mention} no viewer has a line with results yet.", $"{r.Mention} żaden widz nie ma jeszcze linii z wynikiem.");
            var o = best ? owners.MaxBy(x => x.Profit12m) : owners.MinBy(x => x.Profit12m);
            var text = Loc.T($"{r.Mention} {word} viewer company: {o!.Name}, line profit (12 mo.) {Money(o.Profit12m!.Value)} ({Lines(o.Lines)}).",
                $"{r.Mention} {word} firma widza: {o!.Name}, zysk linii (12 mies.) {Money(o.Profit12m!.Value)} ({Lines(o.Lines)}).");
            return RewardMessages.OwnerLink(r.SiteUrl, o.Key) is { } link ? $"{text} {link}" : text;
        }
        var kind = best ? ChatCommandKind.Best : ChatCommandKind.Worst;
        return Loc.T($"{r.Mention} use: ", $"{r.Mention} użyj: ") + r.Name(kind) + ChatCommandKinds.Usage(kind);
    }

    // Active reward effects on the object: "town boost +50% until 12 May 1922 (from ola)".
    private static List<string> Effects(JsonElement root, long entity) =>
        [.. Items(root, "effects").Where(e => Long(e, "entity") == entity).Select(e =>
        {
            var text = Str(e, "type") switch
            {
                "townBoost" => Loc.T("town boost", "boost miasta"),
                "industryBoost" => Loc.T("industry boost", "boost przemysłu"),
                "lineStop" => Loc.T("line stopped", "linia zatrzymana"),
                "lineHold" => Loc.T("line waits at stations", "linia czeka na stacjach"),
                "industryStrike" => Loc.T("strike", "strajk"),
                "industryTaxRaid" => Loc.T("tax raid", "nalot skarbówki"),
                "lineFuelPremium" => Loc.T("premium fuel", "paliwo premium"),
                "lineFuelBad" => Loc.T("watered-down fuel", "chrzczone paliwo"),
                var t => t ?? Loc.T("effect", "efekt"),
            };
            if (Num(e, "factor") is { } f && f > 0) text += " " + Signed(f - 1);
            if (e.TryGetProperty("until", out var u) && Date(u) is { } d) text += Loc.T($" until {RewardMessages.Date(d)}", $" do {RewardMessages.Date(d)}");
            if (Str(e, "user") is { } user) text += Loc.T($" (from {user})", $" (od {user})");
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

    // The first `max` names, the rest as "and 4 more" / "i 4 inne".
    internal static string Names(IReadOnlyList<string> names, int max) =>
        names.Count <= max ? string.Join(", ", names)
            : $"{string.Join(", ", names.Take(max))} {Loc.T($"and {names.Count - max} more", $"i {names.Count - max} inne")}";

    private static string Towns(int n) => Count(n, "town", "towns", "miasto", "miasta", "miast");
    private static string Industries(int n) => Count(n, "industry", "industries", "zakład", "zakłady", "zakładów");
    private static string Stations(int n) => Count(n, "station", "stations", "stacja", "stacje", "stacji");
    private static string Lines(int n) => Count(n, "line", "lines", "linia", "linie", "linii");
    private static string Vehicles(int n) => Count(n, "vehicle", "vehicles", "pojazd", "pojazdy", "pojazdów");

    /// <summary>"1 town", "2 towns" / Polish plural: 1 miasto, 2 miasta, 5 miast, 22 miasta.</summary>
    internal static string Count(int n, string one, string other, string plOne, string plFew, string plMany)
    {
        if (!Loc.Pl) return $"{n} {(n == 1 ? one : other)}";
        var word = n == 1 ? plOne : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? plFew : plMany;
        return $"{n} {word}";
    }

    // As on the website: "1,234,567" / "1 234 567" with a plain space (pl-PL gives a non-breaking one), halves rounded up like Math.round in JS.
    private static string Int(double v) => Math.Round(v, MidpointRounding.AwayFromZero).ToString("#,0", Loc.Culture).Replace(' ', ' ');

    // As on the website: "$833,881", "$-457,279" / "$ 833 881", "$ -457 279".
    internal static string Money(double v) => (Loc.Pl ? "$ " : "$") + Int(v);

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
