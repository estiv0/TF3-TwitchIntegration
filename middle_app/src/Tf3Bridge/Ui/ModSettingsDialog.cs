namespace Tf3Bridge.Ui;

/// <summary>Mod settings (export frequency, vehicles, terrain). Result in <see cref="Settings"/>.</summary>
public sealed class ModSettingsDialog : Form
{
    private readonly NumericUpDown _everyDays = Number(ModSettings.DynamicEveryDaysRange);
    private readonly NumericUpDown _minWallSec = Number(ModSettings.MinDynamicWallSecRange);
    private readonly NumericUpDown _budgetMs = Number(ModSettings.DynamicBudgetMsRange);
    private readonly CheckBox _vehicles = new() { Text = "Pojazdy (ruch na mapie, flota, liczba pojazdów na liniach)", AutoSize = true };
    private readonly CheckBox _terrain = new() { Text = "Eksport terenu (tło mapy)", AutoSize = true };
    private readonly NumericUpDown _terrainStep = Number(ModSettings.TerrainStepMRange);

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ModSettings Settings { get; private set; }

    public ModSettingsDialog(ModSettings current)
    {
        Settings = current;
        Text = "Tf3Bridge: ustawienia moda";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var hint = new Label
        {
            AutoSize = true, MaximumSize = new Size(480, 0), ForeColor = SystemColors.GrayText,
            Text = "Mod wczytuje zmiany w ciągu kilku sekund, bez restartu gry. "
                 + "Jeśli gra zacina się przy eksporcie, zmniejsz czas pracy na klatkę albo wyłącz pojazdy.",
        };

        var form = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddRow(form, "Eksport danych co:", _everyDays, "dni gry");
        AddRow(form, "…ale nie częściej niż co:", _minWallSec, "s");
        AddRow(form, "Czas pracy na klatkę:", _budgetMs, "ms");
        AddWide(form, _vehicles);
        AddWide(form, _terrain);
        AddRow(form, "Dokładność terenu:", _terrainStep, "m między próbkami");

        var ok = new Button { Text = "Zapisz", DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(90, 28) };
        var cancel = new Button { Text = "Anuluj", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28) };
        var defaults = new Button { Text = "Domyślne", AutoSize = true, MinimumSize = new Size(90, 28) };
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([cancel, ok, defaults]);
        AcceptButton = ok;
        CancelButton = cancel;

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(14), Dock = DockStyle.Fill };
        root.Controls.Add(hint);
        root.Controls.Add(form);
        root.Controls.Add(buttons);
        Controls.Add(root);

        Fill(current);
        _terrain.CheckedChanged += (_, _) => _terrainStep.Enabled = _terrain.Checked;
        defaults.Click += (_, _) => Fill(ModSettings.Default);
        ok.Click += (_, _) => Settings = new ModSettings(
            (int)_everyDays.Value, (int)_minWallSec.Value, (int)_budgetMs.Value,
            _vehicles.Checked, _terrain.Checked, (int)_terrainStep.Value);
    }

    private void Fill(ModSettings s)
    {
        _everyDays.Value = s.DynamicEveryDays;
        _minWallSec.Value = s.MinDynamicWallSec;
        _budgetMs.Value = s.DynamicBudgetMs;
        _vehicles.Checked = s.Vehicles;
        _terrain.Checked = s.Terrain;
        _terrainStep.Value = s.TerrainStepM;
        _terrainStep.Enabled = s.Terrain;
    }

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
