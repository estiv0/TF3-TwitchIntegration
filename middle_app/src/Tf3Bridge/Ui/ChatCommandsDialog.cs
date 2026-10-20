using System.Globalization;
using Tf3Bridge.Chat;

namespace Tf3Bridge.Ui;

/// <summary>Informational chat commands: on/off, name, extra names (aliases) and cooldown of each one.</summary>
public sealed class ChatCommandsDialog : Form
{
    private readonly DataGridView _grid = new()
    {
        Width = 860, Height = 268, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.CellSelect,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, BackgroundColor = SystemColors.Window,
    };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(860, 0) };

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ChatCommandSettings Settings { get; private set; }

    public ChatCommandsDialog(ChatCommandSettings settings)
    {
        Settings = settings;
        Text = Loc.T("Tf3Bridge: chat commands");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var intro = new Label
        {
            AutoSize = true, MaximumSize = new Size(860, 0), ForeColor = SystemColors.GrayText,
            Text = Loc.T("Viewers type \"!\" and the name or one of the extra names. Commands only answer in chat; the game changes only through channel points."),
        };

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "enabled", HeaderText = Loc.T("On"), Width = 36 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "kind", HeaderText = Loc.T("What it shows"), Width = 250, ReadOnly = true });
        AddText("name", Loc.T("Name"), 150);
        AddText("aliases", Loc.T("Extra names (comma-separated)"), 330);
        AddText("cooldown", "Cooldown (s)", 90);
        _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        Fill(settings);

        var ok = new Button { Text = Loc.T("Save"), AutoSize = true, MinimumSize = new Size(90, 28) };
        var cancel = new Button { Text = Loc.T("Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28) };
        var defaults = new Button { Text = Loc.T("Restore defaults"), AutoSize = true, MinimumSize = new Size(90, 28) };
        var help = HelpDialog.Button(this, Loc.T("Chat commands: how it works"), HelpSections);
        var buttons = HelpDialog.ButtonRow(help, cancel, ok, defaults);
        CancelButton = cancel;

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(14), Dock = DockStyle.Fill };
        root.Controls.AddRange([intro, _grid, _error, buttons]);
        Controls.Add(root);

        ok.Click += (_, _) =>
        {
            if (TryRead(out var read, out var error))
            {
                Settings = read;
                DialogResult = DialogResult.OK;
            }
            else _error.Text = error;
        };
        // As in the rewards window: the defaults are saved right away.
        defaults.Click += (_, _) =>
        {
            Settings = ChatCommandSettings.Default;
            DialogResult = DialogResult.OK;
        };
    }

    private static (string Heading, string Text)[] HelpSections =>
    [
        (Loc.T("Chat commands"), Loc.T(
            "Tf3Bridge answers these commands in chat with data from the game (owners, towns, lines, ranking, the map link). "
            + "They only read: nothing in the game changes. Viewers change the game only through channel points rewards.")),
        (Loc.T("Names"), Loc.F(
            "The name is shown in the list of commands (!help) and in hints; the extra names work the same way. Write them without \"!\", "
            + "separated by commas. Letters, digits and \"_\", up to {0} characters; upper and lower case are the same. "
            + "Each name may belong to only one command. By default every command has an English and a Polish name: "
            + "on an English stream you can remove the Polish ones.", ChatCommandConfig.MaxName)),
        (Loc.T("Off"), Loc.T(
            "Tf3Bridge ignores a command that is off (no answer at all), and it disappears from the list of commands, so another bot can use its name.")),
        (Loc.T("Cooldown"), Loc.F(
            "The same command with the same text (e.g. the same town) is answered at most once per cooldown for the whole chat. "
            + "Besides, one viewer gets at most one answer every {0} s. Commands on cooldown get no answer.", (int)ChatCommandService.Cooldown.TotalSeconds)),
        (Loc.T("Changes"), Loc.T(
            "Saved changes work from the next chat message on, without restarting Tf3Bridge. Unchanged default names follow the app language.")),
    ];

    private void AddText(string name, string header, int width) =>
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable });

    private void Fill(ChatCommandSettings settings)
    {
        foreach (var c in settings.Commands)
        {
            var i = _grid.Rows.Add(c.Enabled, ChatCommandKinds.Label(c.Kind), c.Name, string.Join(", ", c.Aliases), c.CooldownSec);
            _grid.Rows[i].Tag = c.Kind;
        }
    }

    private bool TryRead(out ChatCommandSettings settings, out string error)
    {
        _grid.EndEdit();
        settings = Settings;
        var list = new List<ChatCommandConfig>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var kind = (ChatCommandKind)row.Tag!;
            string Text(string col) => Convert.ToString(row.Cells[col].Value, CultureInfo.CurrentCulture)?.Trim() ?? "";
            if (!Parse(kind, Text("name"), Text("aliases"), Text("cooldown"), row.Cells["enabled"].Value is true, out var c, out error)) return false;
            list.Add(c!);
        }
        error = ChatCommandSettings.Validate(list) ?? "";
        if (error.Length > 0) return false;
        settings = new ChatCommandSettings(list);
        return true;
    }

    /// <summary>One row of the window: names checked (one name required, all valid), cooldown a whole number in range.</summary>
    internal static bool Parse(ChatCommandKind kind, string name, string aliases, string cooldown, bool enabled, out ChatCommandConfig? command, out string error)
    {
        command = null;
        error = "";
        var label = ChatCommandKinds.Label(kind);
        var main = ChatCommandConfig.CleanName(name);
        if (main.Length == 0) { error = $"{label}: " + Loc.T("the name is missing."); return false; }
        var extra = aliases.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(ChatCommandConfig.CleanName).Where(a => a.Length > 0).ToList();
        if (new[] { main }.Concat(extra).FirstOrDefault(n => !ChatCommandConfig.IsValidName(n)) is { } bad)
        {
            error = $"{label}: " + Loc.F("\"{0}\" is not a valid name (letters, digits and \"_\", up to {1} characters).", bad, ChatCommandConfig.MaxName);
            return false;
        }
        var range = ChatCommandConfig.CooldownRange;
        if (!int.TryParse(cooldown, NumberStyles.Integer, CultureInfo.CurrentCulture, out var seconds) || seconds < range.Min || seconds > range.Max)
        {
            error = $"{label}: " + Loc.F("cooldown from {0} to {1} s (a whole number).", range.Min, range.Max);
            return false;
        }
        command = new ChatCommandConfig(kind, enabled, main, [.. extra.Where(a => a != main).Distinct()], seconds);
        return true;
    }
}
