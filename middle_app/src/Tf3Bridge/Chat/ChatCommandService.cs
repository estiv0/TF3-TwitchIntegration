using System.Text.Json;
using Microsoft.Extensions.Options;
using Tf3Bridge.Processing;
using Tf3Bridge.Rewards;
using Tf3Bridge.Twitch;

namespace Tf3Bridge.Chat;

/// <summary>
/// Informational chat commands (e.g. "!majatek"). They only read the latest dynamic.json and answer in chat;
/// they never queue game commands (the game changes only through channel points, see CommandSource).
/// Texts: <see cref="ChatAnswers"/>; list for viewers: <c>!help</c>. Names, aliases, on/off and cooldowns come from
/// <see cref="ChatCommandSettings"/> (by default every command answers to its English and Polish name); commands that are off
/// are ignored silently. The answer is in the app language (<see cref="Loc"/>).
/// Two cooldowns, both silent (no answer): one viewer gets at most one answer per <see cref="Cooldown"/>,
/// and the same command with the same argument is answered at most once per its own cooldown for the whole chat.
/// </summary>
public sealed class ChatCommandService
{
    /// <summary>One viewer gets at most one answer per this time (chat spam).</summary>
    internal static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(10);

    private static string Run(ChatCommandKind kind, ChatRequest r) => kind switch
    {
        ChatCommandKind.Help => ChatAnswers.Help(r),
        ChatCommandKind.Map => ChatAnswers.Map(r),
        ChatCommandKind.Assets => ChatAnswers.Assets(r),
        ChatCommandKind.Town => ChatAnswers.Town(r),
        ChatCommandKind.Industry => ChatAnswers.Industry(r),
        ChatCommandKind.Line => ChatAnswers.Line(r),
        ChatCommandKind.Ranking => ChatAnswers.Ranking(r),
        ChatCommandKind.Free => ChatAnswers.Free(r),
        ChatCommandKind.Date => ChatAnswers.Date(r),
        ChatCommandKind.Company => ChatAnswers.Company(r),
        ChatCommandKind.Best => ChatAnswers.Extreme(r, best: true),
        _ => ChatAnswers.Extreme(r, best: false),
    };

    // "!name" -> command, only the commands that are on; the first command wins a name used twice (a hand-edited file).
    private static Dictionary<string, ChatCommandConfig> Build(ChatCommandSettings settings)
    {
        var map = new Dictionary<string, ChatCommandConfig>(StringComparer.Ordinal);
        foreach (var c in settings.Commands.Where(c => c.Enabled))
            foreach (var name in c.AllNames) map.TryAdd("!" + name, c);
        return map;
    }

    private readonly ChatApi _chat;
    private readonly bool _mock;
    private readonly string _dynamicPath;
    private readonly string _siteUrl;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly string? _settingsPath;
    private readonly Lock _gate = new();
    private ChatCommandSettings _settings;
    private Dictionary<string, ChatCommandConfig> _commands;
    private readonly Dictionary<string, DateTimeOffset> _lastAnswer = []; // viewer id -> time of the last answer
    private readonly Dictionary<string, DateTimeOffset> _lastCommand = []; // command + argument -> time of the last answer

    public ChatCommandService(TwitchSession session, EventSubClient eventSub, IOptions<BridgeOptions> options,
        IOptions<TwitchOptions> twitch, ILogger<ChatCommandService> log)
        : this(new ChatApi(session, new HttpClient { Timeout = TimeSpan.FromSeconds(20) }), eventSub.IsMock,
            options.Value.OutputDir, twitch.Value.SiteUrl, TimeProvider.System, log, ChatCommandSettingsStore.FilePath)
    {
        eventSub.ChatMessageReceived += m => _ = HandleAsync(m);
    }

    /// <param name="settingsPath">Where the command settings are loaded from and saved to; null = defaults, not saved (tests).</param>
    internal ChatCommandService(ChatApi chat, bool mock, string outputDir, string siteUrl, TimeProvider time, ILogger log, string? settingsPath = null)
    {
        _chat = chat;
        _mock = mock;
        _dynamicPath = Path.Combine(outputDir, ExportProcessor.DynamicPath.Replace('/', Path.DirectorySeparatorChar));
        _siteUrl = siteUrl;
        _time = time;
        _log = log;
        _settingsPath = settingsPath;
        _settings = settingsPath is null ? ChatCommandSettings.Default : ChatCommandSettingsStore.Load(settingsPath);
        _commands = Build(_settings);
    }

    public ChatCommandSettings Settings { get { lock (_gate) return _settings; } }

    /// <summary>New names / on-off / cooldowns: saved and used from the next chat message on.</summary>
    public void UpdateSettings(ChatCommandSettings settings)
    {
        lock (_gate)
        {
            _settings = settings.Normalized();
            _commands = Build(_settings);
            if (_settingsPath is null) return;
            try { ChatCommandSettingsStore.Save(_settings, _settingsPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning("Saving chat command settings failed: {Message}", ex.Message);
            }
        }
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
        ChatCommandConfig? command;
        ChatCommandSettings settings;
        lock (_gate)
        {
            settings = _settings;
            if (!_commands.TryGetValue(name, out command)) return null; // not ours, or off
        }

        // "!assets" without an argument is the asker's estate: a different answer for each viewer.
        var key = command.Kind + "|" + (arg.Length == 0 && command.Kind == ChatCommandKind.Assets ? "@" + m.ChatterLogin : TargetResolver.Normalize(arg));
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_lastAnswer.TryGetValue(m.ChatterId, out var last) && now - last < Cooldown) return null;
            if (_lastCommand.TryGetValue(key, out var lastCommand) && now - lastCommand < TimeSpan.FromSeconds(command.CooldownSec)) return null;
            _lastAnswer[m.ChatterId] = now;
            _lastCommand[key] = now;
        }

        var asker = new ChatAsker(m.ChatterName.Length > 0 ? m.ChatterName : m.ChatterLogin, m.ChatterLogin);
        if (!ChatCommandKinds.NeedsData(command.Kind)) return Run(command.Kind, new ChatRequest(asker, arg, _siteUrl) { Commands = settings });
        using var doc = LoadDynamic();
        if (doc is null) return Loc.T($"@{asker.Name} I have no game data yet.", $"@{asker.Name} nie mam jeszcze danych z gry.");
        var age = _time.GetUtcNow() - new DateTimeOffset(File.GetLastWriteTimeUtc(_dynamicPath), TimeSpan.Zero);
        return Run(command.Kind, new ChatRequest(asker, arg, _siteUrl, doc.RootElement, age) { Commands = settings });
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
