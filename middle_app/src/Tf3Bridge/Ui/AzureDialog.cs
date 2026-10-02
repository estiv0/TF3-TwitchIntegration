using Tf3Bridge.Cloud;

namespace Tf3Bridge.Ui;

/// <summary>Azure target: container URL + SAS token, with a connection test. Result in <see cref="Settings"/>.</summary>
public sealed class AzureDialog : Form
{
    private readonly CheckBox _enabled = new() { Text = "Wysyłaj dane do Azure", AutoSize = true };
    private readonly TextBox _url = new() { Dock = DockStyle.Fill, PlaceholderText = "https://<konto>.blob.core.windows.net/live" };
    private readonly TextBox _sas = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, PlaceholderText = "sv=…&sp=cw&se=…&sig=…" };
    private readonly CheckBox _showSas = new() { Text = "pokaż", AutoSize = true };
    private readonly Label _result = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly Button _test = new() { Text = "Testuj połączenie", AutoSize = true, MinimumSize = new Size(0, 28) };

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

        var hint = new Label
        {
            AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = SystemColors.GrayText,
            Text = "Adres kontenera i token SAS wypisuje skrypt infra\\azure-setup.ps1. "
                 + "Token jest zapisywany zaszyfrowany (DPAPI) tylko na tym koncie Windows, nie w repozytorium. "
                 + "Można też wkleić cały „SAS URL” w pole adresu.",
        };

        var form = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Fill };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.Controls.Add(_enabled, 0, 0);
        form.SetColumnSpan(_enabled, 3);
        form.Controls.Add(RowLabel("Adres kontenera:"), 0, 1);
        form.Controls.Add(_url, 1, 1);
        form.Controls.Add(RowLabel("Token SAS:"), 0, 2);
        form.Controls.Add(_sas, 1, 2);
        form.Controls.Add(_showSas, 2, 2);

        var ok = new Button { Text = "Zapisz", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(90, 28) };
        var cancel = new Button { Text = "Anuluj", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28) };
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([cancel, ok, _test]);
        AcceptButton = ok;
        CancelButton = cancel;

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(14), Dock = DockStyle.Fill };
        root.Controls.Add(hint);
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
        _result.Text = "Sprawdzam…";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var error = await AzureMirror.TestAsync(Current(), cts.Token);
        _result.ForeColor = error is null ? Color.FromArgb(46, 160, 67) : Color.Firebrick;
        _result.Text = error is null
            ? $"Działa: zapisano bridge-test.txt. Strona ma czytać z:\n{Current().PublicLatestUrl}"
            : $"Nie działa: {error}";
        _test.Enabled = true;
    }

    private static Label RowLabel(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) };
}
