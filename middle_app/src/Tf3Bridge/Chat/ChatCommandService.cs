using System.Globalization;
using Microsoft.Extensions.Options;
using Tf3Bridge.Commands;
using Tf3Bridge.Processing;
using Tf3Bridge.Twitch;

namespace Tf3Bridge.Chat;

/// <summary>
/// Informational chat commands (prefix "!tf_"). They only read the latest dynamic.json and answer in chat;
/// they never queue game commands (the game changes only through channel points, see CommandSource).
/// <list type="bullet">
/// <item><c>!tf_majatek [widz]</c>: what the viewer owns (the asker without an argument) and a link to the Owners tab.</item>
/// </list>
/// </summary>
public sealed class ChatCommandService
{
    /// <summary>One viewer gets at most one answer per this time (chat spam).</summary>
    internal static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(10);

    private readonly ChatApi _chat;
    private readonly bool _mock;
    private readonly string _dynamicPath;
    private readonly string _siteUrl;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastAnswer = []; // id widza -> czas ostatniej odpowiedzi

    public ChatCommandService(TwitchSession session, EventSubClient eventSub, IOptions<BridgeOptions> options,
        IOptions<TwitchOptions> twitch, ILogger<ChatCommandService> log)
        : this(new ChatApi(session, new HttpClient { Timeout = TimeSpan.FromSeconds(20) }), eventSub.IsMock,
            options.Value.OutputDir, twitch.Value.SiteUrl, TimeProvider.System, log)
    {
        eventSub.ChatMessageReceived += m => _ = HandleAsync(m);
    }

    internal ChatCommandService(ChatApi chat, bool mock, string outputDir, string siteUrl, TimeProvider time, ILogger log)
    {
        _chat = chat;
        _mock = mock;
        _dynamicPath = Path.Combine(outputDir, ExportProcessor.DynamicPath.Replace('/', Path.DirectorySeparatorChar));
        _siteUrl = siteUrl;
        _time = time;
        _log = log;
    }

    /// <summary>Answers the message when it is one of our commands; returns the answer (null = not ours or on cooldown).</summary>
    internal async Task<string?> HandleAsync(ChatMessage m)
    {
        var answer = Answer(m);
        if (answer is null) return null;
        if (_mock)
        {
            _log.LogInformation("Chat (test server, not sent): {Text}", answer); // serwer testowy nie ma czatu
            return answer;
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _chat.SendAsync(answer, cts.Token);
            _log.LogInformation("Chat: {Text}", answer);
        }
        catch (Exception ex) when (ex is TwitchAuthException or HttpRequestException or OperationCanceledException)
        {
            _log.LogWarning("Chat answer not sent: {Message}", ex.Message);
        }
        return answer;
    }

    internal string? Answer(ChatMessage m)
    {
        var text = m.Text.Trim();
        var space = text.IndexOfAny([' ', '\t']);
        var command = (space < 0 ? text : text[..space]).ToLowerInvariant();
        var arg = space < 0 ? "" : text[(space + 1)..].Trim().TrimStart('@').Trim();
        if (command is not ("!tf_majatek" or "!tf_majątek")) return null;

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_lastAnswer.TryGetValue(m.ChatterId, out var last) && now - last < Cooldown) return null;
            _lastAnswer[m.ChatterId] = now;
        }
        return Assets(m, arg);
    }

    private string Assets(ChatMessage m, string arg)
    {
        var asker = "@" + (m.ChatterName.Length > 0 ? m.ChatterName : m.ChatterLogin);
        var owners = OwnerIndex.Load(_dynamicPath);
        if (owners is null) return $"{asker} nie mam jeszcze danych z gry.";

        var self = arg.Length == 0;
        var who = self ? m.ChatterName : arg;
        if (!owners.TryGetValue(OwnerIndex.Key(who), out var o) && !(self && owners.TryGetValue(OwnerIndex.Key(m.ChatterLogin), out o)))
            return self ? $"{asker} nie masz jeszcze nic w grze." : $"{asker} {who} nie ma jeszcze nic w grze.";
        return AssetsText(o, asker, OwnerLink(_siteUrl, o.Key));
    }

    /// <summary>"@ala estiv0: 3 miasta (Szczecin – główne, Barcin, Barczewo), 1 zakład (Browar), 2 linie, 16 pojazdów, zysk linii (12 mies.) $ 833 881. Mapa: …"</summary>
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

    /// <summary>"https://site/#owner=estiv0": the site opens the owner's assets (web/js/hash.js).</summary>
    internal static string? OwnerLink(string siteUrl, string key) =>
        string.IsNullOrWhiteSpace(siteUrl) ? null : $"{siteUrl.Trim().TrimEnd('/')}/#owner={Uri.EscapeDataString(key)}";

    // Pierwsze `max` nazw, reszta jako "i 4 inne".
    private static string Names(IReadOnlyList<string> names, int max) =>
        names.Count <= max ? string.Join(", ", names) : $"{string.Join(", ", names.Take(max))} i {names.Count - max} inne";

    /// <summary>Polish plural: 1 miasto, 2 miasta, 5 miast, 22 miasta.</summary>
    internal static string Count(int n, string one, string few, string many)
    {
        var word = n == 1 ? one : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? few : many;
        return $"{n} {word}";
    }

    // Jak na stronie: "$ 833 881", "$ -457 279".
    private static string Money(double v) =>
        "$ " + Math.Round(v).ToString("#,0", CultureInfo.GetCultureInfo("pl-PL")).Replace(' ', ' ');
}
