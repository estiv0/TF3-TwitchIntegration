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
    private static (GameObjectKind Kind, string Label)[] Kinds =>
    [
        (GameObjectKind.Town, Loc.T("Towns", "Miasta")), (GameObjectKind.Industry, Loc.T("Industries", "Zakłady")),
        (GameObjectKind.Station, Loc.T("Stations", "Stacje")), (GameObjectKind.Line, Loc.T("Lines", "Linie")),
    ];

    // Rewards carried out by the streamer have nothing to simulate in the game.
    private static readonly RewardKind[] Simulated = [.. Enum.GetValues<RewardKind>().Where(k => !RewardKinds.Manual(k))];

    private readonly ModCommandQueue _queue;
    private readonly RewardService _rewards;
    private string? _simToken;
    private readonly string _dynamicPath;
    private List<GameObject> _objects = [];
    private string? _lastId;

    private readonly ComboBox _kind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly ComboBox _object = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
    private readonly TextBox _viewer = new() { Text = TestViewer, Width = 200 };
    private readonly RadioButton _adopt = new() { Text = Loc.T("Set owner (viewer)", "Ustaw właściciela (widz)"), AutoSize = true, Checked = true };
    private readonly RadioButton _release = new() { Text = Loc.T("Remove owner", "Usuń właściciela"), AutoSize = true };
    private readonly RadioButton _rename = new() { Text = Loc.T("New name:", "Nowa nazwa:"), AutoSize = true };
    private readonly TextBox _newName = new() { Width = 260, Enabled = false };
    private readonly Label _preview = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly Label _result = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly Button _send = new() { Text = Loc.T("Send to game", "Wyślij do gry"), AutoSize = true, MinimumSize = new Size(110, 28) };
    private readonly Button _notify = new() { Text = Loc.T("Test notification", "Testowe powiadomienie"), AutoSize = true, MinimumSize = new Size(110, 28) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly ComboBox _reward = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly TextBox _input = new() { Width = 260, PlaceholderText = Loc.T("what the viewer typed, e.g. kraków", "co wpisał widz, np. kraków") };
    private readonly TextBox _simViewer = new() { Text = TestViewer, Width = 200 };
    private readonly Label _simResult = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly Button _simulate = new() { Text = Loc.T("Simulate redemption", "Symuluj wykupienie"), AutoSize = true, MinimumSize = new Size(110, 28) };
    private readonly CheckBox _simChat = new() { Text = Loc.T("Also send the answer to chat (on your channel)", "Wyślij też odpowiedź na czat (na twoim kanale)"), AutoSize = true };

    public CommandTestDialog(ModCommandQueue queue, RewardService rewards, string outputDir)
    {
        _queue = queue;
        _rewards = rewards;
        _dynamicPath = Path.Combine(outputDir, ExportProcessor.DynamicPath.Replace('/', Path.DirectorySeparatorChar));
        Text = Loc.T("Tf3Bridge: viewer action test", "Tf3Bridge: test akcji widza");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        foreach (var (_, label) in Kinds) _kind.Items.Add(label);
        var refresh = new Button { Text = Loc.T("Refresh list", "Odśwież listę"), AutoSize = true, MinimumSize = new Size(0, 26) };

        var form = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.Controls.Add(RowLabel(Loc.T("Object:", "Obiekt:")), 0, 0);
        form.Controls.Add(Row(_kind, _object, refresh), 1, 0);
        form.Controls.Add(RowLabel(Loc.T("Viewer:", "Widz:")), 0, 1);
        form.Controls.Add(_viewer, 1, 1);
        form.Controls.Add(RowLabel(Loc.T("Action:", "Akcja:")), 0, 2);
        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = new Padding(0) };
        actions.Controls.AddRange([_adopt, _release, Row(_rename, _newName)]);
        form.Controls.Add(actions, 1, 2);

        var close = new Button { Text = Loc.T("Close", "Zamknij"), DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28) };
        var buttons = HelpDialog.ButtonRow(HelpDialog.Button(this, Loc.T("Action test: how it works", "Test akcji: jak to działa"), HelpSections), close, _send, _notify);
        CancelButton = close;

        foreach (var kind in Simulated) _reward.Items.Add(RewardsDialog.KindLabel(kind));
        _reward.SelectedIndex = 0;
        var sim = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
        sim.Controls.Add(RowLabel(Loc.T("Reward:", "Nagroda:")), 0, 0);
        sim.Controls.Add(_reward, 1, 0);
        sim.Controls.Add(RowLabel(Loc.T("Viewer text:", "Tekst widza:")), 0, 1);
        sim.Controls.Add(_input, 1, 1);
        sim.Controls.Add(RowLabel(Loc.T("Viewer:", "Widz:")), 0, 2);
        sim.Controls.Add(_simViewer, 1, 2);
        sim.Controls.Add(_simChat, 1, 3);
        sim.Controls.Add(_simulate, 1, 4);
        var simBox = new GroupBox { Text = Loc.T("Channel points reward (simulation, without Twitch)", "Nagroda za punkty kanału (symulacja, bez Twitcha)"), AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(10) };
        var simInner = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill };
        simInner.Controls.AddRange([sim, _simResult]);
        simBox.Controls.Add(simInner);

        var renameBox = new GroupBox { Text = Loc.T("Rename (without checking the owner)", "Zmiana nazwy (bez sprawdzania właściciela)"), AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(10) };
        var renameInner = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill };
        renameInner.Controls.AddRange([form, _preview, _result]);
        renameBox.Controls.Add(renameInner);

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(14), Dock = DockStyle.Fill };
        root.Controls.AddRange([simBox, renameBox, buttons]);
        Controls.Add(root);

        _kind.SelectedIndexChanged += (_, _) => FillObjects();
        _object.SelectedIndexChanged += (_, _) => UpdatePreview();
        _viewer.TextChanged += (_, _) => UpdatePreview();
        _newName.TextChanged += (_, _) => UpdatePreview();
        foreach (var r in new[] { _adopt, _release, _rename })
            r.CheckedChanged += (_, _) => { _newName.Enabled = _rename.Checked; UpdatePreview(); };
        refresh.Click += (_, _) => LoadObjects();
        _send.Click += (_, _) => Send();
        _notify.Click += (_, _) => SendNotification();
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

    // Explanations behind the "How does it work?" button.
    private static (string Heading, string Text)[] HelpSections =>
    [
        (Loc.T("What this window is for", "Do czego to okno"), Loc.T(
            "Tests without Twitch. Commands go to the game the same way the channel points rewards use. "
            + "TF3 must be running with the mod enabled and a save loaded; the result usually comes after 1–3 s.",
            "Testy bez Twitcha. Komendy idą do gry tą samą drogą, której używają nagrody za punkty kanału. "
            + "TF3 musi działać z włączonym modem i wczytanym zapisem; wynik przychodzi zwykle po 1–3 s.")),
        (Loc.T("Reward simulation", "Symulacja nagrody"), Loc.T(
            "Goes the whole way of a real redemption: matching the viewer text to an object, the command for the game, the result "
            + "and whether the points would come back. Nothing happens on Twitch unless you check sending the answer to chat: "
            + "then the answer really goes to your channel's chat. The in-game notification gets a [TEST] mark.",
            "Przechodzi całą drogę prawdziwego wykupienia: dopasowanie tekstu widza do obiektu, komenda dla gry, wynik "
            + "i informacja, czy punkty by wróciły. Na Twitchu nic się nie dzieje, chyba że zaznaczysz wysłanie odpowiedzi na czat: "
            + "wtedy odpowiedź naprawdę trafia na czat Twojego kanału. Powiadomienie w grze dostaje dopisek [TEST].")),
        (Loc.T("Rename", "Zmiana nazwy"), Loc.T(
            "Sets or removes the owner of an object (\"Name | viewer\") or gives it a new name, without checking whether the object already has an owner. "
            + "The object list comes from the last export; \"Refresh list\" reads it again.",
            "Ustawia albo usuwa właściciela obiektu („Nazwa | widz”) albo nadaje nową nazwę, bez sprawdzania, czy obiekt ma już właściciela. "
            + "Lista obiektów pochodzi z ostatniego eksportu; „Odśwież listę” wczytuje ją ponownie.")),
        (Loc.T("Test notification", Loc.T("Test notification", "Testowe powiadomienie")), Loc.T(
            "Shows a notification with the Twitch logo in the game, like after a fulfilled reward. Clicking it moves the camera to the selected object.",
            "Pokazuje w grze powiadomienie z logo Twitcha, tak jak po wykonanej nagrodzie. Kliknięcie w nie przenosi kamerę do wybranego obiektu.")),
    ];

    private static string TestViewer => Loc.T("test_viewer", "test_widz");

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
            ? (_objects.Count == 0 ? Loc.T("No data: start the game with the mod and wait for the first export.", "Brak danych: uruchom grę z modem i poczekaj na pierwszy eksport.") : Loc.T("No objects of this kind.", "Brak obiektów tego rodzaju."))
            : name is null ? Loc.T("Fill in the viewer or a new name.", "Uzupełnij widza albo nową nazwę.")
            : $"„{Selected.FullName}” → „{name}”";
        _send.Enabled = name is not null && name != Selected?.FullName && _queue.IsAvailable;
        _notify.Enabled = _queue.IsAvailable;
    }

    // What the in-game notification looks like without a reward; clicking it shows the selected object.
    private void SendNotification()
    {
        var viewer = OwnerName.Clean(_viewer.Text);
        if (viewer.Length == 0) viewer = TestViewer;
        var o = Selected;
        var command = _queue.Enqueue(ModCommand.Notify(RewardMessages.NotificationTitle(viewer, test: true),
            o is null ? Loc.T($"{viewer} is testing Tf3Bridge notifications.", $"{viewer} testuje powiadomienia Tf3Bridge.") : Loc.T($"{viewer} is testing Tf3Bridge notifications: {o.Name}.", $"{viewer} testuje powiadomienia Tf3Bridge: {o.Name}."),
            o?.Entity, _rewards.Settings.NotificationDismissMs, CommandSource.Test, viewer));
        _lastId = command.Id;
        _result.ForeColor = SystemColors.ControlText;
        _result.Text = Loc.T($"Sent notification #{command.Id}, waiting for the game…", $"Wysłano powiadomienie #{command.Id}, czekam na grę…");
    }

    private void Send()
    {
        if (Selected is not { } o || NewName() is not { } name) return;
        var viewer = OwnerName.Clean(_viewer.Text);
        var command = _queue.Enqueue(ModCommand.SetName(o.Entity, name, CommandSource.Test, viewer.Length > 0 ? viewer : "-"));
        _lastId = command.Id;
        _result.ForeColor = SystemColors.ControlText;
        _result.Text = Loc.T($"Sent #{command.Id}, waiting for the game…", $"Wysłano #{command.Id}, czekam na grę…");
    }

    private void ShowResult()
    {
        if (_lastId is null || _queue.ResultOf(_lastId) is not { } r) return;
        _lastId = null;
        _result.ForeColor = r.Ok ? Color.FromArgb(46, 160, 67) : Color.Firebrick;
        _result.Text = r.Status switch
        {
            "ok" => Loc.T("The game ran the command. The new name shows on the website after the next export (notification: right away in the game).", "Gra wykonała komendę. Nowa nazwa pojawi się na stronie po następnym eksporcie (powiadomienie: od razu w grze)."),
            "expired" => Loc.T($"The command expired: {r.Error}", $"Komenda wygasła: {r.Error}"),
            "invalid" => Loc.T($"The mod rejected the command: {r.Error}", $"Mod odrzucił komendę: {r.Error}"),
            _ => Loc.T($"Failed: {r.Error}", $"Nie udało się: {r.Error}"),
        };
    }

    private void Simulate()
    {
        var kind = Simulated[_reward.SelectedIndex];
        _simResult.ForeColor = SystemColors.ControlText;
        _simResult.Text = Loc.T("Sent to the game, waiting for the result…", "Wysłano do gry, czekam na wynik…");
        var rejected = _rewards.Simulate(kind, _input.Text, _simViewer.Text, _simChat.Checked, out var token);
        _simToken = token;
        if (rejected is not null) ShowOutcome(rejected);
    }

    // Finished comes from a background thread.
    private void OnRewardFinished(RewardOutcome o)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() => { if (!IsDisposed && o.Token == _simToken) ShowOutcome(o); });
    }

    private void ShowOutcome(RewardOutcome o)
    {
        _simToken = null;
        _simResult.ForeColor = o.Ok ? Color.FromArgb(46, 160, 67) : Color.Firebrick;
        var sent = _simChat.Checked && _rewards.Settings.ChatMessages;
        _simResult.Text = (o.Ok
                ? Loc.T($"Done ({o.Message}). On Twitch: fulfilled.", $"Wykonano ({o.Message}). Na Twitchu: zrealizowane.")
                : Loc.T($"Rejected: {o.Message}. On Twitch: points refunded.", $"Odrzucone: {o.Message}. Na Twitchu: zwrot punktów."))
            + "\n\n" + (sent ? Loc.T("In chat", "Na czacie") : Loc.T("In chat (not sent)", "Na czacie (niewysłane)")) + $": {o.ChatText}"
            + (o.Ok && _rewards.Settings.GameNotifications
                ? "\n\n" + Loc.T("In the game: a notification marked [TEST].", "W grze: powiadomienie z dopiskiem [TEST].") : "");
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
