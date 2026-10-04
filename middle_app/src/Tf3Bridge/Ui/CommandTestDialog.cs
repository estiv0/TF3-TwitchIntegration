using Tf3Bridge.Commands;
using Tf3Bridge.Processing;
using Tf3Bridge.Rewards;

namespace Tf3Bridge.Ui;

/// <summary>
/// Tests without Twitch: a channel points redemption simulated end to end (target lookup, command,
/// result, refund decision) and a plain rename. Objects come from the last published dynamic.json.
/// </summary>
public sealed class CommandTestDialog : Form
{
    private static readonly (GameObjectKind Kind, string Label)[] Kinds =
    [
        (GameObjectKind.Town, "Miasta"), (GameObjectKind.Industry, "Zakłady"),
        (GameObjectKind.Station, "Stacje"), (GameObjectKind.Line, "Linie"),
    ];

    private readonly ModCommandQueue _queue;
    private readonly RewardService _rewards;
    private string? _simToken;
    private readonly string _dynamicPath;
    private List<GameObject> _objects = [];
    private string? _lastId;

    private readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly ComboBox _object = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
    private readonly TextBox _viewer = new() { Text = "test_widz", Width = 200 };
    private readonly RadioButton _adopt = new() { Text = "Ustaw właściciela (widz)", AutoSize = true, Checked = true };
    private readonly RadioButton _release = new() { Text = "Usuń właściciela", AutoSize = true };
    private readonly RadioButton _rename = new() { Text = "Nowa nazwa:", AutoSize = true };
    private readonly TextBox _newName = new() { Width = 260, Enabled = false };
    private readonly Label _preview = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly Label _result = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly Button _send = new() { Text = "Wyślij do gry", AutoSize = true, MinimumSize = new Size(110, 28) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly ComboBox _reward = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly TextBox _input = new() { Width = 260, PlaceholderText = "co wpisał widz, np. kraków" };
    private readonly TextBox _simViewer = new() { Text = "test_widz", Width = 200 };
    private readonly Label _simResult = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly Button _simulate = new() { Text = "Symuluj wykupienie", AutoSize = true, MinimumSize = new Size(110, 28) };
    private readonly CheckBox _simChat = new() { Text = "Wyślij też odpowiedź na czat (na twoim kanale)", AutoSize = true };

    public CommandTestDialog(ModCommandQueue queue, RewardService rewards, string outputDir)
    {
        _queue = queue;
        _rewards = rewards;
        _dynamicPath = Path.Combine(outputDir, ExportProcessor.DynamicPath.Replace('/', Path.DirectorySeparatorChar));
        Text = "Tf3Bridge: test akcji widza";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var hint = new Label
        {
            AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = SystemColors.GrayText,
            Text = "Wysyła komendę do gry tą samą drogą, której później użyją czat i punkty kanału. "
                 + "TF3 musi działać z włączonym modem; wynik przychodzi zwykle po 1–3 s. "
                 + "Lista obiektów pochodzi z ostatniego eksportu.",
        };

        foreach (var (_, label) in Kinds) _kind.Items.Add(label);
        var refresh = new Button { Text = "Odśwież listę", AutoSize = true, MinimumSize = new Size(0, 26) };

        var form = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.Controls.Add(RowLabel("Obiekt:"), 0, 0);
        form.Controls.Add(Row(_kind, _object, refresh), 1, 0);
        form.Controls.Add(RowLabel("Widz:"), 0, 1);
        form.Controls.Add(_viewer, 1, 1);
        form.Controls.Add(RowLabel("Akcja:"), 0, 2);
        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0) };
        actions.Controls.AddRange([_adopt, _release, Row(_rename, _newName)]);
        form.Controls.Add(actions, 1, 2);

        var close = new Button { Text = "Zamknij", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28) };
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.AddRange([close, _send]);
        CancelButton = close;

        foreach (var kind in Enum.GetValues<RewardKind>()) _reward.Items.Add(RewardsDialog.KindLabel(kind));
        _reward.SelectedIndex = 0;
        var sim = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        sim.Controls.Add(RowLabel("Nagroda:"), 0, 0);
        sim.Controls.Add(_reward, 1, 0);
        sim.Controls.Add(RowLabel("Tekst widza:"), 0, 1);
        sim.Controls.Add(_input, 1, 1);
        sim.Controls.Add(RowLabel("Widz:"), 0, 2);
        sim.Controls.Add(_simViewer, 1, 2);
        sim.Controls.Add(_simChat, 1, 3);
        sim.Controls.Add(_simulate, 1, 4);
        var simBox = new GroupBox { Text = "Nagroda za punkty kanału (symulacja, bez Twitcha)", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(10) };
        var simInner = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill };
        simInner.Controls.AddRange([sim, _simResult]);
        simBox.Controls.Add(simInner);

        var renameBox = new GroupBox { Text = "Zmiana nazwy (bez sprawdzania właściciela)", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(10) };
        var renameInner = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill };
        renameInner.Controls.AddRange([form, _preview, _result]);
        renameBox.Controls.Add(renameInner);

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(14), Dock = DockStyle.Fill };
        root.Controls.AddRange([hint, simBox, renameBox, buttons]);
        Controls.Add(root);

        _kind.SelectedIndexChanged += (_, _) => FillObjects();
        _object.SelectedIndexChanged += (_, _) => UpdatePreview();
        _viewer.TextChanged += (_, _) => UpdatePreview();
        _newName.TextChanged += (_, _) => UpdatePreview();
        foreach (var r in new[] { _adopt, _release, _rename })
            r.CheckedChanged += (_, _) => { _newName.Enabled = _rename.Checked; UpdatePreview(); };
        refresh.Click += (_, _) => LoadObjects();
        _send.Click += (_, _) => Send();
        _timer.Tick += (_, _) => ShowResult();
        _simulate.Click += (_, _) => Simulate();
        _rewards.Finished += OnRewardFinished;

        LoadObjects();
        _kind.SelectedIndex = 0;
        _timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _rewards.Finished -= OnRewardFinished;
        }
        base.Dispose(disposing);
    }

    private GameObject? Selected => _object.SelectedItem is Item item ? item.Object : null;

    private void LoadObjects()
    {
        var keep = Selected;
        _objects = GameObjects.Load(_dynamicPath);
        FillObjects(keep?.Entity);
    }

    private void FillObjects(long? keepEntity = null)
    {
        var kind = Kinds[Math.Max(0, _kind.SelectedIndex)].Kind;
        _object.BeginUpdate();
        _object.Items.Clear();
        foreach (var o in _objects.Where(o => o.Kind == kind)) _object.Items.Add(new Item(o));
        _object.EndUpdate();
        var index = keepEntity is { } e ? _object.Items.Cast<Item>().ToList().FindIndex(i => i.Object.Entity == e) : -1;
        if (_object.Items.Count > 0) _object.SelectedIndex = Math.Max(0, index);
        UpdatePreview();
    }

    private string? NewName()
    {
        if (Selected is not { } o) return null;
        if (_adopt.Checked)
        {
            var viewer = OwnerName.Clean(_viewer.Text);
            return viewer.Length > 0 ? OwnerName.WithOwner(o.FullName, viewer) : null;
        }
        if (_release.Checked) return OwnerName.WithOwner(o.FullName, null);
        var name = OwnerName.Clean(_newName.Text);
        return name.Length > 0 ? name : null;
    }

    private void UpdatePreview()
    {
        var name = NewName();
        _preview.Text = Selected is null
            ? (_objects.Count == 0 ? "Brak danych: uruchom grę z modem i poczekaj na pierwszy eksport." : "Brak obiektów tego rodzaju.")
            : name is null ? "Uzupełnij widza albo nową nazwę."
            : $"„{Selected.FullName}” → „{name}”";
        _send.Enabled = name is not null && name != Selected?.FullName && _queue.IsAvailable;
    }

    private void Send()
    {
        if (Selected is not { } o || NewName() is not { } name) return;
        var viewer = OwnerName.Clean(_viewer.Text);
        var command = _queue.Enqueue(ModCommand.SetName(o.Entity, name, CommandSource.Test, viewer.Length > 0 ? viewer : "-"));
        _lastId = command.Id;
        _result.ForeColor = SystemColors.ControlText;
        _result.Text = $"Wysłano #{command.Id}, czekam na grę…";
    }

    private void ShowResult()
    {
        if (_lastId is null || _queue.ResultOf(_lastId) is not { } r) return;
        _lastId = null;
        _result.ForeColor = r.Ok ? Color.FromArgb(46, 160, 67) : Color.Firebrick;
        _result.Text = r.Status switch
        {
            "ok" => "Gra wykonała komendę. Nowa nazwa pojawi się na stronie po następnym eksporcie.",
            "expired" => $"Komenda wygasła: {r.Error}",
            "invalid" => $"Mod odrzucił komendę: {r.Error}",
            _ => $"Nie udało się: {r.Error}",
        };
    }

    private void Simulate()
    {
        var kind = Enum.GetValues<RewardKind>()[_reward.SelectedIndex];
        _simResult.ForeColor = SystemColors.ControlText;
        _simResult.Text = "Wysłano do gry, czekam na wynik…";
        var rejected = _rewards.Simulate(kind, _input.Text, _simViewer.Text, _simChat.Checked, out var token);
        _simToken = token;
        if (rejected is not null) ShowOutcome(rejected);
    }

    // Finished przychodzi z wątku w tle.
    private void OnRewardFinished(RewardOutcome o)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() => { if (!IsDisposed && o.Token == _simToken) ShowOutcome(o); });
    }

    private void ShowOutcome(RewardOutcome o)
    {
        _simToken = null;
        _simResult.ForeColor = o.Ok ? Color.FromArgb(46, 160, 67) : Color.Firebrick;
        _simResult.Text = (o.Ok ? $"Wykonano ({o.Message}). Na Twitchu: zrealizowane." : $"Odrzucone: {o.Message}. Na Twitchu: zwrot punktów.")
            + $"\n\nNa czacie{(_simChat.Checked && _rewards.Settings.ChatMessages ? "" : " (niewysłane)")}: {o.ChatText}";
    }

    private static Control Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        row.Controls.AddRange(controls);
        return row;
    }

    private static Label RowLabel(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) };

    private sealed record Item(GameObject Object)
    {
        public override string ToString() => Object.Owner is { } owner ? $"{Object.Name}  ({owner})" : Object.Name;
    }
}
