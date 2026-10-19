using System.Diagnostics;
using Tf3Bridge.Twitch;

namespace Tf3Bridge.Ui;

/// <summary>
/// Twitch login with a device code: the app shows a short code and opens twitch.tv/activate,
/// the streamer confirms it there, and the dialog waits for Twitch to hand out the token.
/// </summary>
public sealed class TwitchDialog : Form
{
    private readonly TwitchSession _session;
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(480, 0) };
    private readonly Label _code = new() { AutoSize = true, Font = new Font("Consolas", 22f, FontStyle.Bold), Visible = false };
    private readonly LinkLabel _link = new() { AutoSize = true, MaximumSize = new Size(480, 0), Visible = false };
    private readonly Button _login = new() { Text = "Zaloguj przez Twitch", AutoSize = true, MinimumSize = new Size(0, 28) };
    private readonly Button _logout = new() { Text = "Wyloguj", AutoSize = true, MinimumSize = new Size(90, 28) };
    private CancellationTokenSource? _loginCts;

    public TwitchDialog(TwitchSession session)
    {
        _session = session;
        Text = "Tf3Bridge: Twitch";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var close = new Button { Text = "Zamknij", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28) };
        var buttons = HelpDialog.ButtonRow(HelpDialog.Button(this, "Twitch: jak to działa", HelpSections), close, _logout, _login);
        CancelButton = close;

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(14), Dock = DockStyle.Fill, MinimumSize = new Size(480, 0) };
        root.Controls.Add(_status);
        root.Controls.Add(_code);
        root.Controls.Add(_link);
        root.Controls.Add(buttons);
        Controls.Add(root);

        _login.Click += async (_, _) => await LoginAsync();
        _logout.Click += async (_, _) =>
        {
            _logout.Enabled = false;
            await _session.LogoutAsync();
            ShowSession();
        };
        _link.LinkClicked += (_, _) => OpenBrowser(_link.Text);
        ShowSession();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _loginCts?.Cancel();
        base.OnFormClosing(e);
    }

    private async Task LoginAsync()
    {
        _loginCts = new CancellationTokenSource();
        _login.Enabled = _logout.Enabled = false;
        _status.ForeColor = SystemColors.ControlText;
        _status.Text = "Łączę z Twitchem…";
        try
        {
            var code = await _session.BeginLoginAsync(_loginCts.Token);
            _status.Text = "Na otwartej stronie Twitcha sprawdź, czy kod się zgadza, i zatwierdź dostęp. "
                         + "Jeśli strona się nie otworzyła, kliknij link i wpisz kod ręcznie.";
            _code.Text = code.UserCode;
            _link.Text = code.VerificationUri.ToString();
            _code.Visible = _link.Visible = true;
            OpenBrowser(_link.Text);
            await _session.CompleteLoginAsync(code, _loginCts.Token);
        }
        catch (OperationCanceledException) when (_loginCts.IsCancellationRequested) { }
        catch (Exception ex) when (ex is TwitchAuthException or HttpRequestException or TaskCanceledException)
        {
            if (IsDisposed) return;
            _code.Visible = _link.Visible = false;
            _status.ForeColor = Color.Firebrick;
            _status.Text = $"Logowanie nie powiodło się: {ex.Message}";
            _login.Enabled = true;
            return;
        }
        finally
        {
            _loginCts.Dispose();
            _loginCts = null;
        }
        if (!IsDisposed) ShowSession();
    }

    private void ShowSession()
    {
        _code.Visible = _link.Visible = false;
        var s = _session.Status;
        _status.Text = s.Message;
        _status.ForeColor = s.State switch
        {
            TwitchState.Ok => Color.FromArgb(46, 160, 67),
            TwitchState.Warning => Color.DarkOrange,
            TwitchState.Error or TwitchState.NotConfigured => Color.Firebrick,
            _ => SystemColors.ControlText,
        };
        _login.Enabled = _session.IsConfigured && !_session.IsLoggedIn;
        _logout.Enabled = _session.IsLoggedIn;
    }

    // Explanations behind the "How does it work?" button.
    private static readonly (string Heading, string Text)[] HelpSections =
    [
        ("Którym kontem się logować",
            "Kontem kanału, na którym streamujesz: nagrody za punkty kanału powstają na tym kanale, a odpowiedzi na czacie idą od tego konta."),
        ("Jak przebiega logowanie",
            "Tf3Bridge pokazuje krótki kod i otwiera twitch.tv/activate. Sprawdź tam, czy kod się zgadza, i zatwierdź dostęp. "
            + "Hasło wpisujesz tylko na stronie Twitcha, Tf3Bridge go nie widzi."),
        ("Uprawnienia",
            "Tf3Bridge prosi tylko o odczyt i zarządzanie nagrodami za punkty kanału oraz o czytanie i pisanie na czacie (odpowiedzi dla widzów). "
            + "Gdy nowa wersja potrzebuje kolejnego uprawnienia, trzeba zalogować się ponownie."),
        ("Bezpieczeństwo",
            "Token jest zapisywany zaszyfrowany (DPAPI) tylko na tym koncie Windows, w %LOCALAPPDATA%\\Tf3Bridge. "
            + "Wylogowanie unieważnia go na Twitchu. Dostęp można też odebrać na Twitchu: Ustawienia → Połączenia."),
        ("Nagrody za punkty kanału",
            "Wymagają kanału ze statusem Affiliate albo Partner. Bez niego logowanie działa (czat, komendy), ale nagród nie da się utworzyć."),
    ];

    private static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { } // no browser: the link stays in the window
    }
}
