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
    private readonly Button _login = new() { Text = Loc.T("Log in with Twitch"), AutoSize = true, MinimumSize = new Size(0, 28) };
    private readonly Button _logout = new() { Text = Loc.T("Log out"), AutoSize = true, MinimumSize = new Size(90, 28) };
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

        var close = new Button { Text = Loc.T("Close"), DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28) };
        var buttons = HelpDialog.ButtonRow(HelpDialog.Button(this, Loc.T("Twitch: how it works"), HelpSections), close, _logout, _login);
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
        _status.Text = Loc.T("Connecting to Twitch…");
        try
        {
            var code = await _session.BeginLoginAsync(_loginCts.Token);
            _status.Text = Loc.T(
                "On the opened Twitch page check that the code matches and approve access. "
                + "If the page did not open, click the link and type the code by hand.");
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
            _status.Text = Loc.T("Login failed: ") + ex.Message;
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
    private static (string Heading, string Text)[] HelpSections =>
    [
        (Loc.T("Which account to log in with"), Loc.T(
            "The account of the channel you stream on: the channel points rewards are created on this channel, and chat answers come from this account.")),
        (Loc.T("How the login works"), Loc.T(
            "Tf3Bridge shows a short code and opens twitch.tv/activate. Check there that the code matches and approve access. "
            + "You type your password only on the Twitch page; Tf3Bridge never sees it.")),
        (Loc.T("Permissions"), Loc.T(
            "Tf3Bridge only asks to read and manage channel points rewards and to read and write chat (answers for viewers). "
            + "When a new version needs another permission, you have to log in again.")),
        (Loc.T("Security"), Loc.T(
            "The token is saved encrypted (DPAPI) for this Windows account only, in %LOCALAPPDATA%\\Tf3Bridge. "
            + "Logging out revokes it on Twitch. You can also revoke access on Twitch: Settings → Connections.")),
        (Loc.T("Channel points rewards"), Loc.T(
            "They need an Affiliate or Partner channel. Without it the login works (chat, commands), but rewards cannot be created.")),
    ];

    private static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { } // no browser: the link stays in the window
    }
}
