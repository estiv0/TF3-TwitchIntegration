using System.Text.Json;
using Microsoft.Extensions.Options;
using Tf3Bridge.Processing;
using Tf3Bridge.Rewards;
using Tf3Bridge.Twitch;

namespace Tf3Bridge.Chat;

/// <summary>
/// Informational chat commands (e.g. "!majatek"). They only read the latest dynamic.json and answer in chat;
/// they never queue game commands (the game changes only through channel points, see CommandSource).
/// Texts: <see cref="ChatAnswers"/>; list for viewers: <c>!pomoc</c>.
/// Two cooldowns, both silent (no answer): one viewer gets at most one answer per <see cref="Cooldown"/>,
/// and the same command with the same argument is answered at most once per its own cooldown for the whole chat.
/// </summary>
public sealed class ChatCommandService
{
    /// <summary>One viewer gets at most one answer per this time (chat spam).</summary>
    internal static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(10);

    private sealed record Command(string Name, TimeSpan Cooldown, bool NeedsData, Func<ChatRequest, string> Run);

    private static readonly Dictionary<string, Command> Commands = Build(
        (new("pomoc", TimeSpan.FromSeconds(30), false, ChatAnswers.Help), ["!pomoc", "!komendy", "!help"]),
        (new("mapa", TimeSpan.FromSeconds(30), false, ChatAnswers.Map), ["!mapa"]),
        (new("majatek", TimeSpan.FromSeconds(30), true, ChatAnswers.Assets), ["!majatek", "!majątek"]),
        (new("miasto", TimeSpan.FromSeconds(30), true, ChatAnswers.Town), ["!miasto"]),
        (new("zaklad", TimeSpan.FromSeconds(30), true, ChatAnswers.Industry), ["!zaklad", "!zakład"]),
        (new("linia", TimeSpan.FromSeconds(30), true, ChatAnswers.Line), ["!linia"]),
        (new("ranking", TimeSpan.FromSeconds(60), true, ChatAnswers.Ranking), ["!ranking", "!top"]),
        (new("wolne", TimeSpan.FromSeconds(60), true, ChatAnswers.Free), ["!wolne"]),
        (new("data", TimeSpan.FromSeconds(30), true, ChatAnswers.Date), ["!data"]),
        (new("firma", TimeSpan.FromSeconds(60), true, ChatAnswers.Company), ["!firma"]),
        (new("najlepsza", TimeSpan.FromSeconds(60), true, r => ChatAnswers.Extreme(r, best: true)), ["!najlepsza", "!najlepszy"]),
        (new("najgorsza", TimeSpan.FromSeconds(60), true, r => ChatAnswers.Extreme(r, best: false)), ["!najgorsza", "!najgorszy"]));

    private static Dictionary<string, Command> Build(params (Command Command, string[] Aliases)[] list) =>
        list.SelectMany(x => x.Aliases.Select(a => (Alias: a, x.Command))).ToDictionary(x => x.Alias, x => x.Command);

    private readonly ChatApi _chat;
    private readonly bool _mock;
    private readonly string _dynamicPath;
    private readonly string _siteUrl;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastAnswer = []; // viewer id -> time of the last answer
    private readonly Dictionary<string, DateTimeOffset> _lastCommand = []; // command + argument -> time of the last answer

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
            _log.LogInformation("Chat (test server, not sent): {Text}", answer); // the test server has no chat
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
        var name = (space < 0 ? text : text[..space]).ToLowerInvariant();
        var arg = space < 0 ? "" : text[(space + 1)..].Trim().TrimStart('@').Trim();
        if (!Commands.TryGetValue(name, out var command)) return null;

        // "!majatek" without an argument is the asker's estate: a different answer for each viewer.
        var key = command.Name + "|" + (arg.Length == 0 && command.Name == "majatek" ? "@" + m.ChatterLogin : TargetResolver.Normalize(arg));
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_lastAnswer.TryGetValue(m.ChatterId, out var last) && now - last < Cooldown) return null;
            if (_lastCommand.TryGetValue(key, out var lastCommand) && now - lastCommand < command.Cooldown) return null;
            _lastAnswer[m.ChatterId] = now;
            _lastCommand[key] = now;
        }

        var asker = new ChatAsker(m.ChatterName.Length > 0 ? m.ChatterName : m.ChatterLogin, m.ChatterLogin);
        if (!command.NeedsData) return command.Run(new ChatRequest(asker, arg, _siteUrl));
        using var doc = LoadDynamic();
        if (doc is null) return $"@{asker.Name} nie mam jeszcze danych z gry.";
        var age = _time.GetUtcNow() - new DateTimeOffset(File.GetLastWriteTimeUtc(_dynamicPath), TimeSpan.Zero);
        return command.Run(new ChatRequest(asker, arg, _siteUrl, doc.RootElement, age));
    }

    // The last published dynamic.json; null when it's missing or unreadable.
    private JsonDocument? LoadDynamic()
    {
        if (!File.Exists(_dynamicPath)) return null;
        try
        {
            var doc = JsonDocument.Parse(File.ReadAllBytes(_dynamicPath));
            if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc;
            doc.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
