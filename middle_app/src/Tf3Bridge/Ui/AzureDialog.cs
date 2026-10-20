using Tf3Bridge.Cloud;

namespace Tf3Bridge.Ui;

/// <summary>Azure target: container URL + SAS token, with a connection test. Result in <see cref="Settings"/>.</summary>
public sealed class AzureDialog : Form
{
    private readonly CheckBox _enabled = new() { Text = Loc.T("Send data to Azure"), AutoSize = true };
    private readonly TextBox _url = new() { Dock = DockStyle.Fill, PlaceholderText = Loc.T("https://<account>.blob.core.windows.net/live") };
    private readonly TextBox _sas = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, PlaceholderText = "sv=…&sp=cw&se=…&sig=…" };
    private readonly CheckBox _showSas = new() { Text = Loc.T("show"), AutoSize = true };
    private readonly Label _result = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly Button _test = new() { Text = Loc.T("Test connection"), AutoSize = true, MinimumSize = new Size(0, 28) };

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
        form.Controls.Add(RowLabel(Loc.T("Container URL:")), 0, 1);
        form.Controls.Add(_url, 1, 1);
        form.Controls.Add(RowLabel(Loc.T("SAS token:")), 0, 2);
        form.Controls.Add(_sas, 1, 2);
        form.Controls.Add(_showSas, 2, 2);

        var ok = new Button { Text = Loc.T("Save"), DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(90, 28) };
        var cancel = new Button { Text = Loc.T("Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28) };
        var buttons = HelpDialog.ButtonRow(HelpDialog.Button(this, Loc.T("Azure: how it works"), HelpSections), cancel, ok, _test);
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
        _result.Text = Loc.T("Checking…");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var error = await AzureMirror.TestAsync(Current(), cts.Token);
        _result.ForeColor = error is null ? Color.FromArgb(46, 160, 67) : Color.Firebrick;
        _result.Text = error is null
            ? Loc.T("Works: bridge-test.txt written. The website should read from:")
                + $"\n{Current().PublicLatestUrl}"
            : Loc.T("Does not work: ") + error;
        _test.Enabled = true;
    }

    // Explanations behind the "How does it work?" button.
    private static (string Heading, string Text)[] HelpSections =>
    [
        (Loc.T("Why Azure"), Loc.T(
            "Tf3Bridge always saves the data locally (web\\data). When sending is on, changed files also go to an "
            + "Azure Blob Storage container, which the website for viewers reads from.")),
        (Loc.T("Where to get the URL and token"), Loc.T(
            "The script infra\\azure-setup.ps1 prints the container URL and the SAS token (the token also goes to the clipboard). "
            + "A new token, e.g. when the old one expires: infra\\azure-setup.ps1 -Name <account> -SasOnly. "
            + "You can also paste the whole \"SAS URL\" into the URL field and leave the token field empty.")),
        (Loc.T("Security"), Loc.T(
            "The token is saved encrypted (DPAPI) for this Windows account only, in %LOCALAPPDATA%\\Tf3Bridge, never in the repository "
            + "or in appsettings.json. It only needs the right to create and write files (create + write).")),
        (Loc.T("Test connection"), Loc.T(
            "Sends a small bridge-test.txt file and shows the address the website should read from (dataUrl in web/site.json).")),
    ];

    private static Label RowLabel(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) };
}
