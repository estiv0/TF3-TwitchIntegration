using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Tf3Bridge.Cloud;
using Tf3Bridge.Commands;
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
    private bool _closing;

    public MainForm(BridgeRunner runner, AzureMirror mirror, TwitchSession twitch, EventSubClient eventSub, ModCommandQueue commands, RewardService rewards, BridgeOptions options, ModInstallResult mod, UiLogProvider log)
    {
        _runner = runner;
        _mirror = mirror;
        _twitch = twitch;
        _eventSub = eventSub;
        _commands = commands;
        _rewards = rewards;
        _options = options;
        _log = log;

        Text = "Tf3Bridge";
        ClientSize = new Size(900, 480);
        MinimumSize = new Size(560, 400);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        // Przyciski w rzędzie, który się zawija (wąskie okno = dwa rzędy), pod nimi linie stanu na całą
        // szerokość; za długi stan kończy się "…", a pełny tekst jest w podpowiedzi (AutoEllipsis).
        var openOutput = ToolButton("Folder wyjściowy");
        var openInput = ToolButton("Folder moda");
        var modSettings = ToolButton("Ustawienia moda…");
        var azure = ToolButton("Azure…", groupStart: true);
        var twitchButton = ToolButton("Twitch…");
        var rewardsButton = ToolButton("Nagrody…");
        var commandTest = ToolButton("Test akcji…");
        _lastUpdate.ForeColor = SystemColors.GrayText;
        _toggle.Margin = new Padding(0, 3, 3, 3);
        openOutput.Margin = GroupMargin;
        var toolbar = Toolbar(_toggle, openOutput, openInput, modSettings, azure, twitchButton, rewardsButton, commandTest);
        var statusBox = StatusPanel(_status, _lastUpdate, _azure, _twitchStatus, _eventSubStatus, _rewardStatus);

        var paths = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 8, 0, 8) };
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddPathRow(paths, "Wejście (mod):", _options.InputDir.Length > 0
            ? _options.InputDir
            : "(nie znaleziono folderu TF3 w Steam – ustaw --Bridge:InputDir=…)");
        AddPathRow(paths, "Wyjście (strona):", _options.OutputDir);
        AddPathRow(paths, "Mod w grze:", ModText(mod));

        Controls.Add(Root(toolbar, statusBox, paths, _logBox));

        _toggle.Click += async (_, _) =>
        {
            _toggle.Enabled = false;
            if (_runner.IsRunning) await _runner.StopAsync();
            else _runner.Start();
            _toggle.Enabled = true;
        };
        openOutput.Click += (_, _) => OpenFolder(_options.OutputDir);
        openInput.Click += (_, _) => OpenFolder(_options.InputDir);
        azure.Click += (_, _) => EditAzure();
        modSettings.Click += (_, _) => EditModSettings();
        twitchButton.Click += (_, _) => EditTwitch();
        commandTest.Click += (_, _) => TestCommand();
        rewardsButton.Click += (_, _) => EditRewards();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Pokaż", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Zamknij", null, (_, _) => Close());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();

        _runner.StateChanged += () => BeginInvoke(UpdateState);
        _timer.Tick += (_, _) => OnTick();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _tray.Visible = true;
        _timer.Start();
        _runner.Start(); // most ma działać od razu po uruchomieniu
        _ = _twitch.RunAsync(_backgroundCts.Token); // walidacja tokenu przy starcie i co godzinę
        _ = _commands.RunAsync(_backgroundCts.Token); // wyniki komend z gry
        _ = _eventSub.RunAsync(_backgroundCts.Token); // czat i punkty kanału
        _ = _rewards.RunAsync(_backgroundCts.Token); // nagrody na Twitchu
        UpdateState();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && !_closing)
        {
            Hide();
            _tray.ShowBalloonTip(2000, "Tf3Bridge", "Działa w tle. Kliknij dwukrotnie ikonę, żeby otworzyć.", ToolTipIcon.None);
        }
    }

    // Zamknięcie: najpierw zatrzymaj pętlę (dokończy bieżący przebieg), potem zamknij okno.
    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_closing && _runner.IsRunning)
        {
            e.Cancel = true;
            _closing = true;
            Enabled = false;
            _status.Text = "Zamykanie…";
            await _runner.StopAsync();
            Close();
            return;
        }
        _closing = true;
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
        _status.Text = on ? "● Działa" : "● Zatrzymany";
        _status.ForeColor = on ? Color.FromArgb(46, 160, 67) : SystemColors.GrayText;
        _toggle.Text = on ? "Zatrzymaj" : "Uruchom";
        Icon = on ? _iconOn : _iconOff;
        _tray.Icon = Icon;
        _tray.Text = on ? "Tf3Bridge: działa" : "Tf3Bridge: zatrzymany";
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
            _logBox.Lines = [.. _lines]; // nadrabianie po powrocie z zasobnika
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
            MessageBox.Show("Nie znaleziono folderu TF3 w Steam, więc nie ma gdzie zapisać kolejki komend.\n"
                + "Uruchom grę raz albo ustaw --Bridge:InputDir=…", "Tf3Bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            TwitchState.Error => ($"Twitch: błąd: {s.Message}", Color.Firebrick),
            _ => ($"Twitch: {s.Message}", SystemColors.GrayText),
        };

        var e = _eventSub.Status;
        var where = _eventSub.IsMock ? $"EventSub (test: {_eventSub.Url})" : "EventSub";
        _eventSubStatus.Visible = _twitch.IsConfigured;
        (_eventSubStatus.Text, _eventSubStatus.ForeColor) = e.State switch
        {
            EventSubState.Connected => ($"{where}: {e.Message}", Color.FromArgb(46, 160, 67)),
            EventSubState.Error => ($"{where}: błąd: {e.Message}", Color.Firebrick),
            EventSubState.Connecting => ($"{where}: {e.Message}", Color.DarkOrange),
            _ => ($"{where}: {e.Message}", SystemColors.GrayText),
        };

        var r = _rewards.Status;
        _rewardStatus.Visible = _twitch.IsConfigured;
        (_rewardStatus.Text, _rewardStatus.ForeColor) = r.State switch
        {
            RewardState.Ok => ($"Nagrody: {r.Message}", Color.FromArgb(46, 160, 67)),
            RewardState.Error => ($"Nagrody: błąd: {r.Message}", Color.Firebrick),
            RewardState.Paused or RewardState.NotAffiliate => ($"Nagrody: {r.Message}", Color.DarkOrange),
            _ => ($"Nagrody: {r.Message}", SystemColors.GrayText),
        };
    }

    private void EditRewards()
    {
        using var dialog = new RewardsDialog(_rewards.Settings.Rewards, _rewards.Settings.ChatMessages, _rewards.Status.Message);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _rewards.UpdateSettings(_rewards.Settings with { Rewards = dialog.Rewards, ChatMessages = dialog.ChatMessages });
        _log.CreateLogger(nameof(MainForm)).LogInformation("Reward settings saved");
    }

    private void EditModSettings()
    {
        var path = ModSettingsStore.PathFor(_options.InputDir);
        if (path is null)
        {
            MessageBox.Show("Nie znaleziono folderu TF3 w Steam, więc nie ma gdzie zapisać ustawień moda.\n"
                +"Uruchom grę raz albo ustaw --Bridge:InputDir=…", "Tf3Bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            MessageBox.Show($"Nie udało się zapisać ustawień:\n{ex.Message}", "Tf3Bridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _log.CreateLogger(nameof(MainForm)).LogInformation("Mod settings saved to {Path}: {Settings}", path, dialog.Settings);
    }

    private void UpdateAzure()
    {
        var s = _mirror.Status;
        var last = s.LastUploadUtc is { } t ? $", ostatnio {t.ToLocalTime():HH:mm:ss}" : "";
        (_azure.Text, _azure.ForeColor) = s.State switch
        {
            AzureState.Ok => ($"Azure: {s.Message}{last}", Color.FromArgb(46, 160, 67)),
            AzureState.Error => ($"Azure: błąd: {s.Message}{last}", Color.Firebrick),
            AzureState.Incomplete => ($"Azure: {s.Message}", Color.DarkOrange),
            _ => ("Azure: wyłączone (tylko lokalnie)", SystemColors.GrayText),
        };
    }

    // Czas ostatniej publikacji = data zapisu meta.json (publikowany przy każdej zmianie).
    private string LastUpdateText()
    {
        var meta = Path.Combine(_options.OutputDir, ExportProcessor.MetaPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(meta)) return "Brak opublikowanych danych";
        var at = File.GetLastWriteTime(meta);
        var ago = DateTime.Now - at;
        var agoText = ago.TotalSeconds < 60 ? $"{Math.Max(0, (int)ago.TotalSeconds)} s temu"
            : ago.TotalMinutes < 60 ? $"{(int)ago.TotalMinutes} min temu"
            : at.ToString("d MMM HH:mm");
        return $"Ostatnia aktualizacja: {at:HH:mm:ss} ({agoText})";
    }

    // ---------------------------------------------------------------- układ (osobno, żeby dało się go przetestować)

    private static readonly Padding GroupMargin = new(16, 3, 3, 3); // odstęp przed grupą przycisków

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
        ModInstallStatus.Installed => $"{mod.ModDir}  (zainstalowany – włącz go przy tworzeniu/wczytywaniu gry)",
        ModInstallStatus.Updated => $"{mod.ModDir}  (zaktualizowany)",
        ModInstallStatus.UpToDate => $"{mod.ModDir}  (aktualny)",
        ModInstallStatus.DevCopy => $"{mod.ModDir}  (kopia deweloperska – nie ruszam)",
        ModInstallStatus.NoSteam => "(nie zainstalowano – brak folderu TF3 w Steam, uruchom grę raz)",
        _ => "(błąd instalacji – szczegóły w logu)",
    };

    private static void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            MessageBox.Show($"Folder jeszcze nie istnieje:\n{path}", "Tf3Bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    // Ikona okna/zasobnika: kółko w kolorze stanu (bez pliku .ico w repo).
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
