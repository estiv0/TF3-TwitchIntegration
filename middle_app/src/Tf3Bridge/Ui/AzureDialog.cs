using Tf3Bridge.Cloud;

namespace Tf3Bridge.Ui;

/// <summary>Azure target: container URL + SAS token, with a connection test. Result in <see cref="Settings"/>.</summary>
public sealed class AzureDialog : Form
{
    private readonly CheckBox _enabled = new() { Text = Loc.T("Send data to Azure", "Wysyłaj dane do Azure"), AutoSize = true };
    private readonly TextBox _url = new() { Dock = DockStyle.Fill, PlaceholderText = Loc.T("https://<account>.blob.core.windows.net/live", "https://<konto>.blob.core.windows.net/live") };
    private readonly TextBox _sas = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, PlaceholderText = "sv=…&sp=cw&se=…&sig=…" };
    private readonly CheckBox _showSas = new() { Text = Loc.T("show", "pokaż"), AutoSize = true };
    private readonly Label _result = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly Button _test = new() { Text = Loc.T("Test connection", "Testuj połączenie"), AutoSize = true, MinimumSize = new Size(0, 28) };

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public AzureSettings Settings { get; private set; }

    public AzureDialog(AzureSettings current)
    {
        Settings = current;
        Text = "Tf3Bridge: Azure";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _enabled.Checked = current.Enabled;
        _url.Text = current.ContainerUrl;
        _sas.Text = current.Sas;

        var form = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Fill };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.Controls.Add(_enabled, 0, 0);
        form.SetColumnSpan(_enabled, 3);
        form.Controls.Add(RowLabel(Loc.T("Container URL:", "Adres kontenera:")), 0, 1);
        form.Controls.Add(_url, 1, 1);
        form.Controls.Add(RowLabel(Loc.T("SAS token:", "Token SAS:")), 0, 2);
        form.Controls.Add(_sas, 1, 2);
        form.Controls.Add(_showSas, 2, 2);

        var ok = new Button { Text = Loc.T("Save", "Zapisz"), DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(90, 28) };
        var cancel = new Button { Text = Loc.T("Cancel", "Anuluj"), DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28) };
        var buttons = HelpDialog.ButtonRow(HelpDialog.Button(this, Loc.T("Azure: how it works", "Azure: jak to działa"), HelpSections), cancel, ok, _test);
        AcceptButton = ok;
        CancelButton = cancel;

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(14), Dock = DockStyle.Fill };
        root.Controls.Add(form);
        root.Controls.Add(_result);
        root.Controls.Add(buttons);
        Controls.Add(root);

        _showSas.CheckedChanged += (_, _) => _sas.UseSystemPasswordChar = !_showSas.Checked;
        _test.Click += async (_, _) => await TestAsync();
        ok.Click += (_, _) => Settings = Current();
    }

    private AzureSettings Current() => new(_enabled.Checked, _url.Text.Trim(), _sas.Text.Trim());

    private async Task TestAsync()
    {
        _test.Enabled = false;
        _result.ForeColor = SystemColors.ControlText;
        _result.Text = Loc.T("Checking…", "Sprawdzam…");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var error = await AzureMirror.TestAsync(Current(), cts.Token);
        _result.ForeColor = error is null ? Color.FromArgb(46, 160, 67) : Color.Firebrick;
        _result.Text = error is null
            ? Loc.T("Works: bridge-test.txt written. The website should read from:", "Działa: zapisano bridge-test.txt. Strona ma czytać z:")
                + $"\n{Current().PublicLatestUrl}"
            : Loc.T("Does not work: ", "Nie działa: ") + error;
        _test.Enabled = true;
    }

    // Explanations behind the "How does it work?" button.
    private static (string Heading, string Text)[] HelpSections =>
    [
        (Loc.T("Why Azure", "Po co Azure"), Loc.T(
            "Tf3Bridge always saves the data locally (web\\data). When sending is on, changed files also go to an "
            + "Azure Blob Storage container, which the website for viewers reads from.",
            "Tf3Bridge zawsze zapisuje dane lokalnie (web\\data). Gdy wysyłka jest włączona, zmienione pliki trafiają też do kontenera "
            + "Azure Blob Storage, z którego czyta strona dostępna dla widzów.")),
        (Loc.T("Where to get the URL and token", "Skąd wziąć adres i token"), Loc.T(
            "The script infra\\azure-setup.ps1 prints the container URL and the SAS token (the token also goes to the clipboard). "
            + "A new token, e.g. when the old one expires: infra\\azure-setup.ps1 -Name <account> -SasOnly. "
            + "You can also paste the whole \"SAS URL\" into the URL field and leave the token field empty.",
            "Adres kontenera i token SAS wypisuje skrypt infra\\azure-setup.ps1 (token trafia też do schowka). "
            + "Nowy token, np. gdy stary wygasa: infra\\azure-setup.ps1 -Name <konto> -SasOnly. "
            + "Można też wkleić cały „SAS URL” w pole adresu, a pole tokenu zostawić puste.")),
        (Loc.T("Security", "Bezpieczeństwo"), Loc.T(
            "The token is saved encrypted (DPAPI) for this Windows account only, in %LOCALAPPDATA%\\Tf3Bridge, never in the repository "
            + "or in appsettings.json. It only needs the right to create and write files (create + write).",
            "Token jest zapisywany zaszyfrowany (DPAPI) tylko na tym koncie Windows, w %LOCALAPPDATA%\\Tf3Bridge, nigdy w repozytorium "
            + "ani w appsettings.json. Wystarczy mu prawo tworzenia i zapisu plików (create + write).")),
        (Loc.T("Test connection", Loc.T("Test connection", "Testuj połączenie")), Loc.T(
            "Sends a small bridge-test.txt file and shows the address the website should read from (dataUrl in web/site.json).",
            "Wysyła mały plik bridge-test.txt i pokazuje adres, z którego ma czytać strona (dataUrl w web/site.json).")),
    ];

    private static Label RowLabel(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) };
}
