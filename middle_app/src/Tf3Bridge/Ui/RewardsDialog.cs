using System.Globalization;
using Tf3Bridge.Rewards;

namespace Tf3Bridge.Ui;

/// <summary>Channel points rewards: on/off, title, prompt, cost, limits and effect strength (for balancing).</summary>
public sealed class RewardsDialog : Form
{
    private readonly DataGridView _grid = new()
    {
        Width = 1060, Height = 230, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.CellSelect,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, BackgroundColor = SystemColors.Window,
    };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(1060, 0) };
    private readonly IReadOnlyList<RewardConfig> _original;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<RewardConfig> Rewards { get; private set; }

    public RewardsDialog(IReadOnlyList<RewardConfig> rewards, string status)
    {
        _original = Rewards = rewards;
        Text = "Tf3Bridge: nagrody za punkty kanału";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var hint = new Label
        {
            AutoSize = true, MaximumSize = new Size(1060, 0), ForeColor = SystemColors.GrayText,
            Text = "Tylko te nagrody mogą zmieniać grę (czat jest wyłącznie informacyjny). Tf3Bridge tworzy je na Twitchu sam "
                 + "i pauzuje, gdy gra nie wysyła danych. Widz wpisuje nazwę miasta, zakładu albo linii; gdy jej nie ma, cel jest "
                 + "niejednoznaczny albo gra nie wykona akcji, punkty wracają. W opisie można użyć {days} i {percent}. "
                 + "Limit 0 = bez limitu. Dni to dni gry (efekt stoi, gdy gra jest na pauzie).\n\nStan: " + status,
        };

        AddText("title", "Tytuł (max 45)", 170);
        AddText("prompt", "Opis dla widza (max 200)", 330);
        _grid.Columns["prompt"]!.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.Columns.Insert(0, new DataGridViewCheckBoxColumn { Name = "enabled", HeaderText = "Wł.", Width = 36 });
        AddText("cost", "Koszt", 65);
        AddText("cooldown", "Cooldown (s)", 80);
        AddText("perStream", "Limit / stream", 80);
        AddText("perUser", "Limit / widz", 75);
        AddText("days", "Dni gry", 60);
        AddText("factor", "Mnożnik", 65);
        _grid.Columns.Insert(1, new DataGridViewTextBoxColumn { Name = "kind", HeaderText = "Akcja", Width = 140, ReadOnly = true });

        foreach (var r in rewards)
        {
            var i = _grid.Rows.Add(r.Enabled, KindLabel(r.Kind), r.Title, r.Prompt, r.Cost, r.CooldownSec, r.MaxPerStream,
                r.MaxPerUserPerStream, RewardKinds.HasDays(r.Kind) ? r.Days : "", RewardKinds.HasFactor(r.Kind) ? r.Factor.ToString("0.##", CultureInfo.CurrentCulture) : "");
            var row = _grid.Rows[i];
            row.Tag = r.Kind;
            if (!RewardKinds.HasDays(r.Kind)) Lock(row.Cells["days"]);
            if (!RewardKinds.HasFactor(r.Kind)) Lock(row.Cells["factor"]);
        }

        var ok = new Button { Text = "Zapisz i wyślij na Twitcha", AutoSize = true, MinimumSize = new Size(90, 28) };
        var cancel = new Button { Text = "Anuluj", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28) };
        var defaults = new Button { Text = "Przywróć domyślne", AutoSize = true, MinimumSize = new Size(90, 28) };
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([cancel, ok, defaults]);
        CancelButton = cancel;

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(14), Dock = DockStyle.Fill };
        root.Controls.AddRange([hint, _grid, _error, buttons]);
        Controls.Add(root);

        ok.Click += (_, _) =>
        {
            if (TryRead(out var list, out var error))
            {
                Rewards = list;
                DialogResult = DialogResult.OK;
            }
            else _error.Text = error;
        };
        defaults.Click += (_, _) =>
        {
            Rewards = RewardKinds.Defaults;
            DialogResult = DialogResult.OK;
        };
    }

    private void AddText(string name, string header, int width) =>
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable });

    private static void Lock(DataGridViewCell cell)
    {
        cell.ReadOnly = true;
        cell.Style.BackColor = SystemColors.Control;
    }

    private bool TryRead(out List<RewardConfig> list, out string error)
    {
        _grid.EndEdit();
        list = [];
        error = "";
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var kind = (RewardKind)row.Tag!;
            var old = _original.First(r => r.Kind == kind);
            string Text(string col) => Convert.ToString(row.Cells[col].Value, CultureInfo.CurrentCulture)?.Trim() ?? "";
            bool Int(string col, int min, out int value) =>
                int.TryParse(Text(col), NumberStyles.Integer, CultureInfo.CurrentCulture, out value) && value >= min;

            var title = Text("title");
            if (title.Length is 0 or > RewardConfig.MaxTitle) { error = $"{KindLabel(kind)}: tytuł musi mieć 1–{RewardConfig.MaxTitle} znaków."; return false; }
            var prompt = Text("prompt");
            if (prompt.Length > RewardConfig.MaxPrompt) { error = $"{KindLabel(kind)}: opis może mieć najwyżej {RewardConfig.MaxPrompt} znaków."; return false; }
            if (!Int("cost", 1, out var cost) || !Int("cooldown", 0, out var cooldown)
                || !Int("perStream", 0, out var perStream) || !Int("perUser", 0, out var perUser))
            {
                error = $"{KindLabel(kind)}: koszt ≥ 1, cooldown i limity ≥ 0 (liczby całkowite).";
                return false;
            }
            var days = old.Days;
            if (RewardKinds.HasDays(kind) && (!int.TryParse(Text("days"), out days) || days < RewardConfig.DaysRange.Min || days > RewardConfig.DaysRange.Max))
            {
                error = $"{KindLabel(kind)}: dni gry od {RewardConfig.DaysRange.Min} do {RewardConfig.DaysRange.Max}.";
                return false;
            }
            var factor = old.Factor;
            if (RewardKinds.HasFactor(kind) && (!double.TryParse(Text("factor").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out factor)
                || factor < RewardConfig.FactorRange.Min || factor > RewardConfig.FactorRange.Max))
            {
                error = $"{KindLabel(kind)}: mnożnik od {RewardConfig.FactorRange.Min} do {RewardConfig.FactorRange.Max} (np. 1,5 = +50%).";
                return false;
            }
            list.Add(new RewardConfig(kind, row.Cells["enabled"].Value is true, title, prompt, cost, cooldown, perStream, perUser, days, factor));
        }
        return true;
    }

    public static string KindLabel(RewardKind kind) => kind switch
    {
        RewardKind.AdoptTown => "Adopcja miasta",
        RewardKind.BoostTown => "Boost miasta",
        RewardKind.BoostIndustry => "Boost przemysłu",
        RewardKind.StopLine => "Stop linii (od razu)",
        _ => "Linia czeka na stacjach",
    };
}
