namespace Tf3Bridge.Ui;

/// <summary>Mod settings (export frequency, vehicles, terrain). Result in <see cref="Settings"/>.</summary>
public sealed class ModSettingsDialog : Form
{
    private readonly NumericUpDown _everyDays = Number(ModSettings.DynamicEveryDaysRange);
    private readonly NumericUpDown _minWallSec = Number(ModSettings.MinDynamicWallSecRange);
    private readonly NumericUpDown _budgetMs = Number(ModSettings.DynamicBudgetMsRange);
    private readonly CheckBox _vehicles = new() { Text = Loc.T("Vehicles (traffic on the map, fleet, vehicle count on lines)"), AutoSize = true };
    private readonly CheckBox _vehicleDetails = new() { Text = Loc.T("Vehicle details (model, age, condition, capacity)"), AutoSize = true };
    private readonly CheckBox _terrain = new() { Text = Loc.T("Terrain export (map background)"), AutoSize = true };
    private readonly NumericUpDown _terrainStep = Number(ModSettings.TerrainStepMRange);

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ModSettings Settings { get; private set; }

    public ModSettingsDialog(ModSettings current)
    {
        Settings = current;
        Text = Loc.T("Tf3Bridge: mod settings");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var form = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddRow(form, Loc.T("Export data every:"), _everyDays, Loc.T("game days"));
        AddRow(form, Loc.T("…but not more often than every:"), _minWallSec, "s");
        AddRow(form, Loc.T("Work time per frame:"), _budgetMs, "ms");
        AddWide(form, _vehicles);
        AddWide(form, _vehicleDetails);
        AddWide(form, _terrain);
        AddRow(form, Loc.T("Terrain detail:"), _terrainStep, Loc.T("m between samples"));

        var ok = new Button { Text = Loc.T("Save"), DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(90, 28) };
        var cancel = new Button { Text = Loc.T("Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28) };
        var defaults = new Button { Text = Loc.T("Defaults"), AutoSize = true, MinimumSize = new Size(90, 28) };
        var buttons = HelpDialog.ButtonRow(HelpDialog.Button(this, Loc.T("Mod settings: how it works"), HelpSections), cancel, ok, defaults);
        AcceptButton = ok;
        CancelButton = cancel;

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(14), Dock = DockStyle.Fill };
        root.Controls.Add(form);
        root.Controls.Add(buttons);
        Controls.Add(root);

        Fill(current);
        _vehicles.CheckedChanged += (_, _) => _vehicleDetails.Enabled = _vehicles.Checked;
        _terrain.CheckedChanged += (_, _) => _terrainStep.Enabled = _terrain.Checked;
        defaults.Click += (_, _) => Fill(ModSettings.Default);
        ok.Click += (_, _) => Settings = new ModSettings(
            (int)_everyDays.Value, (int)_minWallSec.Value, (int)_budgetMs.Value,
            _vehicles.Checked, _vehicleDetails.Checked, _terrain.Checked, (int)_terrainStep.Value);
    }

    private void Fill(ModSettings s)
    {
        _everyDays.Value = s.DynamicEveryDays;
        _minWallSec.Value = s.MinDynamicWallSec;
        _budgetMs.Value = s.DynamicBudgetMs;
        _vehicles.Checked = s.Vehicles;
        _vehicleDetails.Checked = s.VehicleDetails;
        _vehicleDetails.Enabled = s.Vehicles;
        _terrain.Checked = s.Terrain;
        _terrainStep.Value = s.TerrainStepM;
        _terrainStep.Enabled = s.Terrain;
    }

    // Explanations behind the "How does it work?" button.
    private static (string Heading, string Text)[] HelpSections =>
    [
        (Loc.T("When changes take effect"), Loc.T(
            "The mod reads the settings every few seconds, so changes work without restarting the game. Without Tf3Bridge the mod uses the defaults.")),
        (Loc.T("Data export"), Loc.T(
            "The mod sends the game state (towns, industries, lines, vehicles) every given number of game days, but not more often than every given number of seconds. "
            + "There is no export while paused.")),
        (Loc.T("Work time per frame"), Loc.T(
            "The export is spread over many frames so the game does not stutter. If the game still stutters during the export, "
            + "lower this time or turn vehicles off.")),
        (Loc.T("Vehicles and their details"), Loc.T(
            "Vehicles give traffic on the map, the fleet and the vehicle count on lines. Details (model, age, condition, capacity) "
            + "are an extra cost with many vehicles.")),
        (Loc.T("Terrain"), Loc.T(
            "The terrain is the map background on the website, and Tf3Bridge uses it to check where viewers may build. A smaller gap between samples "
            + "gives a more detailed map, but a bigger file and a longer export (done in the background).")),
    ];

    private static NumericUpDown Number((int Min, int Max) range) =>
        new() { Minimum = range.Min, Maximum = range.Max, Width = 70, TextAlign = HorizontalAlignment.Right };

    private static void AddRow(TableLayoutPanel table, string label, Control input, string unit)
    {
        var row = table.RowCount++;
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) }, 0, row);
        table.Controls.Add(input, 1, row);
        table.Controls.Add(new Label { Text = unit, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 6, 0, 0) }, 2, row);
    }

    private static void AddWide(TableLayoutPanel table, Control control)
    {
        var row = table.RowCount++;
        control.Margin = new Padding(0, 8, 0, 0);
        table.Controls.Add(control, 0, row);
        table.SetColumnSpan(control, 3);
    }
}
