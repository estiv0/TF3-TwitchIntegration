using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Tf3Bridge.Cloud;
using Tf3Bridge.Processing;

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
    private readonly BridgeOptions _options;
    private readonly UiLogProvider _log;

    private readonly Label _status = new() { AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold) };
    private readonly Label _lastUpdate = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Label _azure = new() { AutoSize = true };
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

    public MainForm(BridgeRunner runner, AzureMirror mirror, BridgeOptions options, ModInstallResult mod, UiLogProvider log)
    {
        _runner = runner;
        _mirror = mirror;
        _options = options;
        _log = log;

        Text = "Tf3Bridge";
        ClientSize = new Size(720, 420);
        MinimumSize = new Size(520, 300);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        var header = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        var statusBox = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Margin = new Padding(0, 0, 16, 0) };
        statusBox.Controls.AddRange([_status, _lastUpdate, _azure]);
        var openOutput = new Button { Text = "Folder wyjściowy", AutoSize = true, MinimumSize = new Size(0, 30) };
        var openInput = new Button { Text = "Folder moda", AutoSize = true, MinimumSize = new Size(0, 30) };
        var azure = new Button { Text = "Azure…", AutoSize = true, MinimumSize = new Size(0, 30) };
        header.Controls.AddRange([statusBox, _toggle, openOutput, openInput, azure]);

        var paths = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 8, 0, 8) };
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        paths.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddPathRow(paths, "Wejście (mod):", _options.InputDir.Length > 0
            ? _options.InputDir
            : "(nie znaleziono folderu TF3 w Steam – ustaw --Bridge:InputDir=…)");
        AddPathRow(paths, "Wyjście (strona):", _options.OutputDir);
        AddPathRow(paths, "Mod w grze:", ModText(mod));

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 3, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(paths, 0, 1);
        root.Controls.Add(_logBox, 0, 2);
        Controls.Add(root);

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
    }

    private void EditAzure()
    {
        using var dialog = new AzureDialog(AzureSettingsStore.Load());
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        AzureSettingsStore.Save(dialog.Settings);
        _mirror.Configure(dialog.Settings);
        UpdateAzure();
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
