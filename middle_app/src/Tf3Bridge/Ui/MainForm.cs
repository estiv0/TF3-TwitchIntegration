using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Tf3Bridge.Chat;
using Tf3Bridge.Cloud;
using Tf3Bridge.Commands;
using Tf3Bridge.Output;
using Tf3Bridge.Processing;
using Tf3Bridge.Rewards;
using Tf3Bridge.Twitch;

namespace Tf3Bridge.Ui;

/// <summary>
/// Small control window: status, start/stop, folders and log. Minimizing hides it to the
/// tray; closing the window stops the bridge cleanly and exits.
/// </summary>
public sealed class MainForm : Form
{
    private const int MaxLogLines = 500;

    private readonly BridgeRunner _runner;
    private readonly AzureMirror _mirror;
    private readonly TwitchSession _twitch;
    private readonly EventSubClient _eventSub;
    private readonly ModCommandQueue _commands;
    private readonly RewardService _rewards;
    private readonly ChatCommandService _chatCommands;
    private readonly LocalWebServer _web;
    private readonly CancellationTokenSource _backgroundCts = new();
    private readonly BridgeOptions _options;
    private readonly UiLogProvider _log;

    private readonly Label _status = new() { AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold) };
    private readonly Label _lastUpdate = StatusLine();
    private readonly Label _azure = StatusLine();
    private readonly Label _twitchStatus = StatusLine();
    private readonly Label _eventSubStatus = StatusLine();
    private readonly Label _rewardStatus = StatusLine();
    private readonly Button _toggle = new() { AutoSize = true, MinimumSize = new Size(110, 30) };
    private readonly TextBox _logBox = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true,
        Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), BackColor = SystemColors.Window,
    };
    private readonly NotifyIcon _tray = new() { Text = "Tf3Bridge" };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly Icon _iconOn = MakeIcon(Color.FromArgb(46, 160, 67));
    private readonly Icon _iconOff = MakeIcon(Color.FromArgb(140, 148, 160));
    private readonly List<string> _lines = [];
    private readonly ModInstallResult _mod;
    private bool _closing;

    public MainForm(BridgeRunner runner, AzureMirror mirror, TwitchSession twitch, EventSubClient eventSub, ModCommandQueue commands, RewardService rewards, ChatCommandService chatCommands, LocalWebServer web, BridgeOptions options, ModInstallResult mod, UiLogProvider log)
    {
        _runner = runner;
        _mirror = mirror;
        _twitch = twitch;
        _eventSub = eventSub;
        _commands = commands;
        _rewards = rewards;
        _chatCommands = chatCommands;
        _web = web;
        _options = options;
        _log = log;

        Text = "Tf3Bridge";
        ClientSize = new Size(940, 480);
        MinimumSize = new Size(560, 400);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        _toggle.Click += async (_, _) =>
        {
            _toggle.Enabled = false;
            if (_runner.IsRunning) await _runner.StopAsync();
            else _runner.Start();
            _toggle.Enabled = true;
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();

        _mod = mod;
        BuildLayout();

        _runner.StateChanged += () => BeginInvoke(UpdateState);
        _timer.Tick += (_, _) => OnTick();
    }

    /// <summary>
    /// Builds the window contents in the current language; called again after a language change.
    /// The long-lived controls (status lines, log, toggle) move into the new layout; the language list is new each time.
    /// </summary>
    private void BuildLayout()
    {
        SuspendLayout();
        var old = Controls.Count > 0 ? Controls[0] : null;
        foreach (var keep in new Control[] { _toggle, _status, _lastUpdate, _azure, _twitchStatus, _eventSubStatus, _rewardStatus, _logBox })
            keep.Parent?.Controls.Remove(keep);
        Controls.Clear();
        old?.Dispose();

        // Buttons in a wrapping row (narrow window = two rows), below them full-width status lines;
        // a status that is too long ends with "…", and the full text is in the tooltip (AutoEllipsis).
        var openSite = ToolButton(Loc.T("Website"));
        var openOutput = ToolButton(Loc.T("Output folder"));
        var openInput = ToolButton(Loc.T("Mod folder"));
        var modSettings = ToolButton(Loc.T("Mod settings…"));
        var azure = ToolButton("Azure…", groupStart: true);
        var twitchButton = ToolButton("Twitch…");
        var rewardsButton = ToolButton(Loc.T("Rewards…"));
        var chatButton = ToolButton(Loc.T("Chat commands…"));
        var commandTest = ToolButton(Loc.T("Test actions…"));
        _lastUpdate.ForeColor = SystemColors.GrayText;
        _toggle.Margin = new Padding(0, 3, 3, 3);
        // A new list per layout: moving the old one into the new layout from inside its own event broke later changes.
        var language = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90, Margin = new Padding(12, 6, 3, 3) };
        language.Items.AddRange([.. Loc.Languages.Select(Loc.NativeName)]); // English + one per Locales\*.json
        language.SelectedIndex = Math.Max(0, Loc.Languages.ToList().IndexOf(Loc.Language));
        // After the event: the rebuild disposes this list.
        language.SelectedIndexChanged += (_, _) =>
        {
            var chosen = Loc.Languages[language.SelectedIndex];
            BeginInvoke(() => ChangeLanguage(chosen));
        };
        openSite.Margin = GroupMargin;
        var toolbar = Toolbar(_toggle, openSite, openOutput, openInput, modSettings, azure, twitchButton, rewardsButton, chatButton, commandTest, language);
        var statusBox = StatusPanel(_status, _lastUpdate, _azure, _twitchStatus, _eventSubStatus, _rewardStatus);

        var paths = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 8, 0, 8) };
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddPathRow(paths, Loc.T("Input (mod):"), _options.InputDir.Length > 0
            ? _options.InputDir
            : Loc.T("(TF3 folder not found in Steam – set --Bridge:InputDir=…)"));
        AddPathRow(paths, Loc.T("Output (website):"), _options.OutputDir);
        AddPathRow(paths, Loc.T("Mod in game:"), ModText(_mod));
        AddPathRow(paths, Loc.T("Local website:"), WebText(_web));

        Controls.Add(Root(toolbar, statusBox, paths, _logBox));

        openSite.Click += (_, _) => OpenWebsite();
        openOutput.Click += (_, _) => OpenFolder(_options.OutputDir);
        openInput.Click += (_, _) => OpenFolder(_options.InputDir);
        azure.Click += (_, _) => EditAzure();
        modSettings.Click += (_, _) => EditModSettings();
        twitchButton.Click += (_, _) => EditTwitch();
        commandTest.Click += (_, _) => TestCommand();
        rewardsButton.Click += (_, _) => EditRewards();
        chatButton.Click += (_, _) => EditChatCommands();

        var oldMenu = _tray.ContextMenuStrip;
        var menu = new ContextMenuStrip();
        menu.Items.Add(Loc.T("Show"), null, (_, _) => RestoreFromTray());
        menu.Items.Add(Loc.T("Exit"), null, (_, _) => Close());
        _tray.ContextMenuStrip = menu;
        oldMenu?.Dispose();
        ResumeLayout(true);
    }

    // One setting for everything: windows, chat answers, in-game notifications and the default reward texts.
    private void ChangeLanguage(string lang)
    {
        if (lang == Loc.Language) return;
        Loc.Language = lang;
        try { Loc.Save(lang); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.CreateLogger(nameof(MainForm)).LogWarning(ex, "Could not save the language to {Path}", Loc.FilePath);
        }
        BuildLayout();
        UpdateState();
        OnTick();
        // Unchanged default reward titles and descriptions switch to the new language and go to Twitch.
        _rewards.UpdateSettings(_rewards.Settings.Normalized());
        _chatCommands.UpdateSettings(_chatCommands.Settings); // unchanged default command names too
        _log.CreateLogger(nameof(MainForm)).LogInformation("Language changed to {Language}", lang);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _tray.Visible = true;
        _timer.Start();
        _runner.Start(); // the bridge should run right after startup
        _ = _twitch.RunAsync(_backgroundCts.Token); // token validation at startup and every hour
        _ = _commands.RunAsync(_backgroundCts.Token); // command results from the game
        _ = _eventSub.RunAsync(_backgroundCts.Token); // chat and channel points
        _ = _rewards.RunAsync(_backgroundCts.Token); // rewards on Twitch
        UpdateState();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && !_closing)
        {
            Hide();
            _tray.ShowBalloonTip(2000, "Tf3Bridge", Loc.T("Running in the background. Double-click the icon to open."), ToolTipIcon.None);
        }
    }

    // Closing: first stop the loop (it finishes the current pass) and disable the rewards on Twitch, then close the window.
    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_closing)
        {
            e.Cancel = true;
            _closing = true;
            Enabled = false;
            _status.Text = Loc.T("Closing…");
            if (_runner.IsRunning) await _runner.StopAsync();
            _backgroundCts.Cancel();
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                await _rewards.DisableAsync(cts.Token); // without a running bridge nobody handles redemptions
            Close();
            return;
        }
        _backgroundCts.Cancel();
        _timer.Stop();
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _timer.Dispose();
            _backgroundCts.Dispose();
            _iconOn.Dispose();
            _iconOff.Dispose();
        }
        base.Dispose(disposing);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void UpdateState()
    {
        if (_closing) return;
        var on = _runner.IsRunning;
        _status.Text = on ? Loc.T("● Running") : Loc.T("● Stopped");
        _status.ForeColor = on ? Color.FromArgb(46, 160, 67) : SystemColors.GrayText;
        _toggle.Text = on ? Loc.T("Stop") : Loc.T("Start");
        Icon = on ? _iconOn : _iconOff;
        _tray.Icon = Icon;
        _tray.Text = on ? Loc.T("Tf3Bridge: running") : Loc.T("Tf3Bridge: stopped");
    }

    private void OnTick()
    {
        var added = false;
        while (_log.TryDequeue(out var line))
        {
            _lines.Add(line.ToString());
            added = true;
        }
        if (added)
        {
            if (_lines.Count > MaxLogLines) _lines.RemoveRange(0, _lines.Count - MaxLogLines);
            if (Visible)
            {
                _logBox.Lines = [.. _lines];
                _logBox.SelectionStart = _logBox.TextLength;
                _logBox.ScrollToCaret();
            }
        }
        else if (Visible && _logBox.Lines.Length != _lines.Count)
        {
            _logBox.Lines = [.. _lines]; // catching up after coming back from the tray
        }

        _lastUpdate.Text = LastUpdateText();
        UpdateAzure();
        UpdateTwitch();
    }

    private void EditAzure()
    {
        using var dialog = new AzureDialog(AzureSettingsStore.Load());
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        AzureSettingsStore.Save(dialog.Settings);
        _mirror.Configure(dialog.Settings);
        UpdateAzure();
    }

    private void EditTwitch()
    {
        using var dialog = new TwitchDialog(_twitch);
        dialog.ShowDialog(this);
        UpdateTwitch();
    }

    private void TestCommand()
    {
        if (!_commands.IsAvailable)
        {
            MessageBox.Show(Loc.T(
                "The TF3 folder was not found in Steam, so there is nowhere to write the command queue.\nStart the game once or set --Bridge:InputDir=…"), "Tf3Bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dialog = new CommandTestDialog(_commands, _rewards, _options.OutputDir);
        dialog.ShowDialog(this);
    }

    private void UpdateTwitch()
    {
        var s = _twitch.Status;
        (_twitchStatus.Text, _twitchStatus.ForeColor) = s.State switch
        {
            TwitchState.Ok => ($"Twitch: {s.Message}", Color.FromArgb(46, 160, 67)),
            TwitchState.Warning => ($"Twitch: {s.Message}", Color.DarkOrange),
            TwitchState.Error => ($"Twitch: {Loc.T("error")}: {s.Message}", Color.Firebrick),
            _ => ($"Twitch: {s.Message}", SystemColors.GrayText),
        };

        var e = _eventSub.Status;
        var where = _eventSub.IsMock ? $"EventSub (test: {_eventSub.Url})" : "EventSub";
        _eventSubStatus.Visible = _twitch.IsConfigured;
        (_eventSubStatus.Text, _eventSubStatus.ForeColor) = e.State switch
        {
            EventSubState.Connected => ($"{where}: {e.Message}", Color.FromArgb(46, 160, 67)),
            EventSubState.Error => ($"{where}: {Loc.T("error")}: {e.Message}", Color.Firebrick),
            EventSubState.Connecting => ($"{where}: {e.Message}", Color.DarkOrange),
            _ => ($"{where}: {e.Message}", SystemColors.GrayText),
        };

        var r = _rewards.Status;
        var rewards = Loc.T("Rewards");
        _rewardStatus.Visible = _twitch.IsConfigured;
        (_rewardStatus.Text, _rewardStatus.ForeColor) = r.State switch
        {
            RewardState.Ok => ($"{rewards}: {r.Message}", Color.FromArgb(46, 160, 67)),
            RewardState.Error => ($"{rewards}: {Loc.T("error")}: {r.Message}", Color.Firebrick),
            RewardState.Inactive or RewardState.NotAffiliate or RewardState.Warning => ($"{rewards}: {r.Message}", Color.DarkOrange),
            _ => ($"{rewards}: {r.Message}", SystemColors.GrayText),
        };
    }

    private void EditRewards()
    {
        using var dialog = new RewardsDialog(_rewards.Settings.Rewards, _rewards.Settings.ChatMessages, _rewards.Settings.GameNotifications,
            _rewards.Settings.NotificationSeconds, _rewards.Settings.Zones, _rewards.Settings.Extras, _rewards.Status.Message);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _rewards.UpdateSettings(_rewards.Settings with
        {
            Rewards = dialog.Rewards, ChatMessages = dialog.ChatMessages, GameNotifications = dialog.GameNotifications,
            NotificationSeconds = dialog.NotificationSeconds, Zones = dialog.Zones, Extras = dialog.Extras,
        });
        _log.CreateLogger(nameof(MainForm)).LogInformation("Reward settings saved");
    }

    private void EditChatCommands()
    {
        using var dialog = new ChatCommandsDialog(_chatCommands.Settings);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _chatCommands.UpdateSettings(dialog.Settings);
        _log.CreateLogger(nameof(MainForm)).LogInformation("Chat command settings saved");
    }

    private void EditModSettings()
    {
        var path = ModSettingsStore.PathFor(_options.InputDir);
        if (path is null)
        {
            MessageBox.Show(Loc.T(
                "The TF3 folder was not found in Steam, so there is nowhere to save the mod settings.\nStart the game once or set --Bridge:InputDir=…"), "Tf3Bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dialog = new ModSettingsDialog(ModSettingsStore.Load(path));
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            ModSettingsStore.Save(dialog.Settings, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Loc.T("Could not save the settings:") + $"\n{ex.Message}", "Tf3Bridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _log.CreateLogger(nameof(MainForm)).LogInformation("Mod settings saved to {Path}: {Settings}", path, dialog.Settings);
    }

    private void UpdateAzure()
    {
        var s = _mirror.Status;
        var last = s.LastUploadUtc is { } t ? $", {Loc.T("last")} {t.ToLocalTime():HH:mm:ss}" : "";
        (_azure.Text, _azure.ForeColor) = s.State switch
        {
            AzureState.Ok => ($"Azure: {s.Message}{last}", Color.FromArgb(46, 160, 67)),
            AzureState.Error => ($"Azure: {Loc.T("error")}: {s.Message}{last}", Color.Firebrick),
            AzureState.Incomplete => ($"Azure: {s.Message}", Color.DarkOrange),
            _ => (Loc.T("Azure: off (local only)"), SystemColors.GrayText),
        };
    }

    // Time of the last publish = write time of meta.json (published on every change).
    private string LastUpdateText()
    {
        var meta = Path.Combine(_options.OutputDir, ExportProcessor.MetaPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(meta)) return Loc.T("No published data yet");
        var at = File.GetLastWriteTime(meta);
        var ago = DateTime.Now - at;
        var agoText = ago.TotalSeconds < 60 ? Loc.F("{0} s ago", Math.Max(0, (int)ago.TotalSeconds))
            : ago.TotalMinutes < 60 ? Loc.F("{0} min ago", (int)ago.TotalMinutes)
            : at.ToString("d MMM HH:mm", Loc.Culture);
        return Loc.T("Last update") + $": {at:HH:mm:ss} ({agoText})";
    }

    // ---------------------------------------------------------------- layout (separate, so it can be tested)

    private static readonly Padding GroupMargin = new(16, 3, 3, 3); // gap before a group of buttons

    internal static Button ToolButton(string text, bool groupStart = false) =>
        new() { Text = text, AutoSize = true, MinimumSize = new Size(0, 30), Margin = groupStart ? GroupMargin : new Padding(3) };

    internal static FlowLayoutPanel Toolbar(params Control[] buttons)
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true, Margin = new Padding(0, 0, 0, 6),
        };
        bar.Controls.AddRange(buttons);
        return bar;
    }

    internal static Label StatusLine() =>
        new() { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, Margin = new Padding(0), TextAlign = ContentAlignment.MiddleLeft };

    /// <summary>Title (bold) and status lines, one fixed-height row each, full width.</summary>
    internal static TableLayoutPanel StatusPanel(Label title, params Label[] lines)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        title.Margin = new Padding(0, 0, 0, 2);
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.Controls.Add(title, 0, 0);
        foreach (var line in lines)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, line.Font.Height + 4));
            panel.Controls.Add(line, 0, panel.RowStyles.Count - 1);
        }
        return panel;
    }

    internal static TableLayoutPanel Root(Control toolbar, Control status, Control paths, Control log)
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 4, ColumnCount = 1 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(toolbar, 0, 0);
        root.Controls.Add(status, 0, 1);
        root.Controls.Add(paths, 0, 2);
        root.Controls.Add(log, 0, 3);
        return root;
    }

    private static void AddPathRow(TableLayoutPanel table, string label, string path)
    {
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) });
        table.Controls.Add(new TextBox { Text = path, ReadOnly = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle });
    }

    private static string ModText(ModInstallResult mod) => mod.Status switch
    {
        ModInstallStatus.Installed => $"{mod.ModDir}  " + Loc.T("(installed – enable it when creating/loading a game)"),
        ModInstallStatus.Updated => $"{mod.ModDir}  " + Loc.T("(updated)"),
        ModInstallStatus.UpToDate => $"{mod.ModDir}  " + Loc.T("(up to date)"),
        ModInstallStatus.DevCopy => $"{mod.ModDir}  " + Loc.T("(developer copy – left as is)"),
        ModInstallStatus.NoSteam => Loc.T("(not installed – no TF3 folder in Steam, start the game once)"),
        _ => Loc.T("(installation error – details in the log)"),
    };

    private static string WebText(LocalWebServer web) => web.State switch
    {
        WebServerState.Running => $"{web.Url}  " + Loc.F("(files from {0})", web.WebDir),
        WebServerState.PortTaken => Loc.F("(off – port {0} is taken; close the other server or set --Bridge:WebPort=…)", web.Port),
        WebServerState.NoWebDir => Loc.T("(off – no website folder with index.html; set --Bridge:WebDir=…)"),
        _ => Loc.T("(off – Bridge:WebPort is 0)"),
    };

    private void OpenWebsite()
    {
        if (_web.Url is not { } url)
        {
            MessageBox.Show(Loc.T("The local website is not running:") + $"\n{WebText(_web)}", "Tf3Bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            MessageBox.Show(Loc.T("The folder does not exist yet:") + $"\n{path}", "Tf3Bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    // Window/tray icon: a circle in the state color (no .ico file in the repo).
    private static Icon MakeIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, 3, 3, 26, 26);
        }
        var handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
