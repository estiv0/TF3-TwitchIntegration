using System.Globalization;
using Tf3Bridge.Processing;
using Tf3Bridge.Rewards;

namespace Tf3Bridge.Ui;

/// <summary>
/// Channel points rewards: on/off, title, prompt, cost, limits and effect strength (for balancing),
/// where viewers may build each industry type, and which of the streamer's own TF3 rewards Tf3Bridge switches with the game.
/// </summary>
public sealed class RewardsDialog : Form
{
    private readonly DataGridView _grid = new()
    {
        Width = 1060, Height = 300, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.CellSelect,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, BackgroundColor = SystemColors.Window,
    };
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(1060, 0) };
    private readonly CheckBox _chat = new()
    {
        AutoSize = true, Text = Loc.T("Answer viewers in chat (e.g. \"@viewer town boost Szczecin runs until 12 May 1922\", or why the points came back)",
            "Odpowiadaj widzom na czacie (np. „@widz boost miasta Szczecin działa do 12 maja 1922”, albo dlaczego punkty wróciły)"),
    };
    private readonly CheckBox _notify = new()
    {
        AutoSize = true, Text = Loc.T("In-game notification with the Twitch logo after a reward is done (when the points were taken)",
            "Powiadomienie w grze z logo Twitcha po wykonanej nagrodzie (gdy punkty zostały pobrane)"),
    };
    private readonly NumericUpDown _notifySeconds = new()
    {
        Minimum = RewardSettings.NotificationSecondsRange.Min, Maximum = RewardSettings.NotificationSecondsRange.Max,
        Width = 60, TextAlign = HorizontalAlignment.Right, Margin = new Padding(12, 0, 4, 0),
    };
    private readonly DataGridView _zonesGrid = new()
    {
        Width = 420, Height = 230, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.CellSelect,
        BackgroundColor = SystemColors.Window, EditMode = DataGridViewEditMode.EditOnEnter,
    };
    private readonly DataGridView _extrasGrid = new()
    {
        Width = 1060, Height = 120, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.CellSelect,
        BackgroundColor = SystemColors.Window, EditMode = DataGridViewEditMode.EditOnEnter,
    };
    private readonly IReadOnlyList<RewardConfig> _original;

    private static readonly IndustryZone[] ZoneChoices =
        [IndustryZone.Any, IndustryZone.Mountains, IndustryZone.NearWater, IndustryZone.Lowland, IndustryZone.Inland];

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<RewardConfig> Rewards { get; private set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ChatMessages { get; private set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool GameNotifications { get; private set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int NotificationSeconds { get; private set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public IReadOnlyDictionary<string, IndustryZone> Zones { get; private set; }

    /// <summary>The streamer's own TF3 rewards; only <see cref="ExtraReward.Enabled"/> changes here.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<ExtraReward> Extras { get; private set; }

    public RewardsDialog(IReadOnlyList<RewardConfig> rewards, bool chatMessages, bool gameNotifications, int notificationSeconds,
        IReadOnlyDictionary<string, IndustryZone>? zones, IReadOnlyList<ExtraReward> extras, string status)
    {
        GameNotifications = _notify.Checked = gameNotifications;
        NotificationSeconds = notificationSeconds;
        _notifySeconds.Value = Math.Clamp(notificationSeconds, RewardSettings.NotificationSecondsRange.Min, RewardSettings.NotificationSecondsRange.Max);
        _notifySeconds.Enabled = gameNotifications;
        _notify.CheckedChanged += (_, _) => _notifySeconds.Enabled = _notify.Checked;
        Extras = extras;
        Zones = IndustryZones.Normalize(zones);
        _original = Rewards = rewards;
        ChatMessages = _chat.Checked = chatMessages;
        Text = Loc.T("Tf3Bridge: channel points rewards", "Tf3Bridge: nagrody za punkty kanału");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var statusLabel = new Label { AutoSize = true, MaximumSize = new Size(1060, 0), ForeColor = SystemColors.GrayText, Text = Loc.T("Status: ", "Stan: ") + status };

        AddText("title", Loc.T("Title (max 45)", "Tytuł (max 45)"), 170);
        AddText("prompt", Loc.T("Viewer description (max 200)", "Opis dla widza (max 200)"), 330);
        _grid.Columns["prompt"]!.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.Columns.Insert(0, new DataGridViewCheckBoxColumn { Name = "enabled", HeaderText = Loc.T("On", "Wł."), Width = 36 });
        AddText("cost", Loc.T("Cost", "Koszt"), 65);
        AddText("cooldown", "Cooldown (s)", 80);
        AddText("perStream", Loc.T("Limit / stream", "Limit / stream"), 80);
        AddText("perUser", Loc.T("Limit / viewer", "Limit / widz"), 75);
        AddText("months", Loc.T("Game months", "Miesiące gry"), 75);
        AddText("factor", Loc.T("Factor / %", "Mnożnik / %"), 65);
        AddText("amount", Loc.T("Amount ($)", "Kwota ($)"), 95);
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "input", HeaderText = Loc.T("Viewer text", "Tekst od widza"), Width = 80 });
        // Long headers ("Limit / stream", "Viewer text") wrap instead of being cut off.
        _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.Columns.Insert(1, new DataGridViewTextBoxColumn { Name = "kind", HeaderText = Loc.T("Action", "Akcja"), Width = 140, ReadOnly = true });

        foreach (var r in rewards)
        {
            var i = _grid.Rows.Add(r.Enabled, KindLabel(r.Kind), r.Title, r.Prompt, r.Cost, r.CooldownSec, r.MaxPerStream,
                r.MaxPerUserPerStream, RewardKinds.HasMonths(r.Kind) ? r.Months : "", RewardKinds.HasFactor(r.Kind) ? r.Factor.ToString("0.##", CultureInfo.CurrentCulture) : "",
                RewardKinds.HasAmount(r.Kind) ? r.Amount.ToString("N0", CultureInfo.CurrentCulture) : "", r.RequiresInput);
            var row = _grid.Rows[i];
            row.Tag = r.Kind;
            if (!RewardKinds.HasMonths(r.Kind)) Lock(row.Cells["months"]);
            if (!RewardKinds.HasFactor(r.Kind)) Lock(row.Cells["factor"]);
            if (!RewardKinds.HasAmount(r.Kind)) Lock(row.Cells["amount"]);
            if (!RewardKinds.Manual(r.Kind)) Lock(row.Cells["input"]); // the game needs a target name (subsidy and loan: none)
        }

        _zonesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "industry", HeaderText = Loc.T("Industry", "Zakład"), Width = 170, ReadOnly = true });
        var where = new DataGridViewComboBoxColumn
        {
            Name = "zone", HeaderText = Loc.T("Where a viewer may build it", "Gdzie widz może go zbudować"), Width = 230, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            FlatStyle = FlatStyle.Flat,
        };
        where.Items.AddRange([.. ZoneChoices.Select(ZoneLabel)]);
        _zonesGrid.Columns.Add(where);
        // Base game industries first, then types only the settings know (e.g. from mods).
        var types = IndustryZones.Known.Select(k => k.Type).Concat(Zones.Keys.Except(IndustryZones.Known.Select(k => k.Type)));
        foreach (var type in types)
        {
            var i = _zonesGrid.Rows.Add(IndustryZones.Name(type), ZoneLabel(Zones.GetValueOrDefault(type)));
            _zonesGrid.Rows[i].Tag = type;
        }
        var zonesTitle = SectionTitle(ZonesTitle);

        var extrasTitle = SectionTitle(Loc.T($"Your \"{ExtraReward.Prefix}…\" rewards enabled with the game", $"Twoje nagrody „{ExtraReward.Prefix}…” włączane razem z grą"));
        _extrasGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "enabled", HeaderText = Loc.T("On", "Wł."), Width = 36 });
        _extrasGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "title", HeaderText = Loc.T("Title", "Tytuł"), Width = 300, ReadOnly = true });
        _extrasGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "cost", HeaderText = Loc.T("Cost", "Koszt"), Width = 70, ReadOnly = true });
        _extrasGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "state", HeaderText = Loc.T("Status", "Stan"), Width = 630, ReadOnly = true });
        foreach (var e in extras)
        {
            var i = _extrasGrid.Rows.Add(e.Enabled, e.Title, e.Cost,
                e.Switchable
                    ? Loc.T("Tf3Bridge enables and disables it with the game", "Tf3Bridge włącza i wyłącza ją z grą")
                    : Loc.T("Twitch does not let Tf3Bridge change it (added by hand in the dashboard)", "Twitch nie pozwala Tf3Bridge jej zmieniać (dodana ręcznie w panelu)"));
            if (!e.Switchable) _extrasGrid.Rows[i].Cells["state"].Style.ForeColor = Color.DarkOrange;
        }
        // The section only matters when the channel has such rewards; an empty one just confuses.
        extrasTitle.Visible = _extrasGrid.Visible = extras.Count > 0;

        var ok = new Button { Text = Loc.T("Save and send to Twitch", "Zapisz i wyślij na Twitcha"), AutoSize = true, MinimumSize = new Size(90, 28) };
        var cancel = new Button { Text = Loc.T("Cancel", "Anuluj"), DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28) };
        var defaults = new Button { Text = Loc.T("Restore defaults", "Przywróć domyślne"), AutoSize = true, MinimumSize = new Size(90, 28) };
        var help = HelpDialog.Button(this, Loc.T("Channel points rewards: how it works", "Nagrody za punkty kanału: jak to działa"), HelpSections);
        var buttons = HelpDialog.ButtonRow(help, cancel, ok, defaults);
        CancelButton = cancel;

        var notifyRow = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, WrapContents = false };
        notifyRow.Controls.AddRange([_notify, _notifySeconds,
            new Label { AutoSize = true, Text = Loc.T("s on screen (then it stays in the game's notification log)", "s na ekranie (potem zostaje w dzienniku powiadomień gry)"), Margin = new Padding(0, 3, 0, 0) }]);
        _notify.Margin = new Padding(0, 3, 0, 0);

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(14), Dock = DockStyle.Fill };
        root.Controls.AddRange([statusLabel, _grid, _chat, notifyRow, zonesTitle, _zonesGrid, extrasTitle, _extrasGrid, _error, buttons]);
        Controls.Add(root);

        ok.Click += (_, _) =>
        {
            if (TryRead(out var list, out var error))
            {
                Rewards = list;
                ChatMessages = _chat.Checked;
                GameNotifications = _notify.Checked;
                NotificationSeconds = (int)_notifySeconds.Value;
                Zones = ReadZones();
                Extras = ReadExtras();
                DialogResult = DialogResult.OK;
            }
            else _error.Text = error;
        };
        defaults.Click += (_, _) =>
        {
            Rewards = RewardKinds.Defaults;
            ChatMessages = true;
            GameNotifications = true;
            NotificationSeconds = RewardSettings.DefaultNotificationSeconds;
            Zones = IndustryZones.Normalize(null);
            DialogResult = DialogResult.OK;
        };
    }

    private static Label SectionTitle(string text) =>
        new() { AutoSize = true, Text = text, Font = new Font("Segoe UI", 9f, FontStyle.Bold), Margin = new Padding(0, 14, 0, 4) };

    // Explanations behind the "How does it work?" button (they used to fill the dialog itself).
    private static (string Heading, string Text)[] HelpSections =>
    [
        (Loc.T("Channel points rewards", "Nagrody za punkty kanału"), Loc.T(
            "Only these rewards can change the game; the chat is informational only. Tf3Bridge creates them on Twitch itself, "
            + "enables them while the game sends data and disables them when the game is idle, stopped or Tf3Bridge closes.",
            "Tylko te nagrody mogą zmieniać grę; czat jest wyłącznie informacyjny. Tf3Bridge sam tworzy je na Twitchu, "
            + "włącza, gdy gra wysyła dane, i wyłącza, gdy gra stoi, jest zatrzymana albo Tf3Bridge się zamyka.")),
        (Loc.T("Viewer text and refunds", "Tekst od widza i zwrot punktów"), Loc.T(
            "The viewer types the name of a town, industry or line (for a new town or industry they paste a code from the map on the website). "
            + "When the target does not exist, is ambiguous, the place is not suitable or the game cannot do the action, the points go back to the viewer.",
            "Widz wpisuje nazwę miasta, zakładu albo linii (przy nowym mieście i zakładzie wkleja kod z mapy na stronie). "
            + "Gdy celu nie ma, jest niejednoznaczny, miejsce się nie nadaje albo gra nie wykona akcji, punkty wracają do widza.")),
        (Loc.T("Viewer description", "Opis dla widza"), Loc.T(
            "The description can use: {months} (effect duration), {percent} (effect strength), {site} (map address) and {amount} (subsidy or loan amount).",
            "W opisie można użyć: {months} (czas efektu), {percent} (siła efektu), {site} (adres mapy) i {amount} (kwota dotacji albo kredytu).")),
        (Loc.T("Sabotage", "Sabotaże"), Loc.T(
            "Strike, tax raid, watered-down fuel and brake sabotage only work on other viewers' objects. For the tax raid the viewer types the victim's nick.",
            "Strajk, skarbówka, chrzczone paliwo i sabotaż hamulców działają tylko na obiekty innych widzów. Przy skarbówce widz wpisuje nick ofiary.")),
        (Loc.T("Factor / %", "Mnożnik / %"), Loc.T(
            "A factor above 1 is an increase (1.5 = +50%), below 1 a decrease (0.5 = -50%). For brake sabotage it is the largest share of the line's vehicles "
            + "that disappears (0.5 = up to 50%, always at least 1 vehicle). For the loan this column is the interest rate in % (2 = 2%).",
            "Mnożnik powyżej 1 to wzrost (1,5 = +50%), poniżej 1 spadek (0,5 = -50%). Przy sabotażu hamulców to największa część pojazdów linii, "
            + "która zniknie (0,5 = do 50%, zawsze co najmniej 1 pojazd). Przy kredycie ta kolumna to oprocentowanie w % (2 = 2%).")),
        (Loc.T("Subsidy and loan", "Dotacja i kredyt"), Loc.T(
            "They give the company the \"Amount\", the viewer types nothing. The loan goes to the Loans tab in the game: the game repays it monthly over the given game months, "
            + "and TF3 interest is amount × %, spread over the installments. The game allows at most 4 loans at once (including yours).",
            "Dają firmie „Kwotę”, widz nic nie wpisuje. Kredyt trafia do zakładki Kredyty w grze: gra spłaca go co miesiąc przez podane miesiące gry, "
            + "a odsetki w TF3 to kwota × %, rozłożone na raty. Gra pozwala mieć najwyżej 4 kredyty naraz (razem z Twoimi).")),
        (Loc.T("Rewards done by the streamer", "Nagrody wykonywane przez streamera"), Loc.T(
            "Bomb, line, nature and vehicles do nothing in the game. Tf3Bridge only creates them and enables them with the game; redemptions stay in the Twitch queue, "
            + "where you do them yourself and accept or refund them. For these you also choose whether the viewer types text.",
            "Bomba, linia, natura i pojazdy nie robią nic w grze. Tf3Bridge tylko je tworzy i włącza razem z grą, a wykupienia zostają w kolejce Twitcha, "
            + "gdzie sam je wykonujesz i akceptujesz albo zwracasz. Przy nich wybierasz też, czy widz wpisuje tekst.")),
        (Loc.T("Limits and time", "Limity i czas"), Loc.T(
            "Limit 0 = no limit. Effect time is in game months (a month is about 1–1.5 min at 1× speed; while paused the effect waits).",
            "Limit 0 = bez limitu. Czas efektów jest w miesiącach gry (miesiąc to ok. 1–1,5 min przy prędkości 1×; na pauzie efekt stoi).")),
        (ZonesTitle, Loc.T(
            "Oil rigs, fisheries and dredgers stand only on water anyway, the rest only on land. Tf3Bridge computes the regions from the game terrain within about 1 km: "
            + "mountains are the most rugged land of the map (at most 15%), lowland is almost flat terrain. When the map has no such region "
            + "(e.g. a flat map without mountains), the restriction does not apply. On the website forbidden terrain is red, and a code from such a place is rejected "
            + "(points go back). How much of the map each region takes, Tf3Bridge writes to the log after the terrain export.",
            "Platformy, łowiska i pogłębiarki i tak stają tylko na wodzie, reszta tylko na lądzie. Regiony Tf3Bridge liczy z terenu gry w promieniu ok. 1 km: "
            + "góry to najbardziej pofałdowany ląd mapy (najwyżej 15%), nizina to prawie płaski teren. Gdy danego regionu na mapie nie ma "
            + "(np. płaska mapa bez gór), ograniczenie nie działa. Na stronie zakazany teren jest czerwony, a kod z takiego miejsca jest odrzucany "
            + "(punkty wracają). Ile mapy zajmuje każdy region, Tf3Bridge pisze w logu po eksporcie terenu.")),
        (Loc.T($"Your \"{ExtraReward.Prefix}…\" rewards", $"Twoje nagrody „{ExtraReward.Prefix}…”"), Loc.T(
            $"Rewards whose title starts with \"{ExtraReward.Prefix}\" and that are none of the Tf3Bridge actions. They do nothing in the game, and redemptions stay "
            + "in the Twitch queue for you. Tf3Bridge enables the checked ones with the game and disables them when the game sends no data or Tf3Bridge closes. "
            + "The list is read when connecting to Twitch (Tf3Bridge start) and after saving this window. "
            + "Tf3Bridge cannot change a reward added by hand in the Twitch dashboard (Twitch does not allow it).",
            $"Nagrody z tytułem zaczynającym się od „{ExtraReward.Prefix}”, które nie są żadną z akcji Tf3Bridge. Nie robią nic w grze, a wykupienia zostają "
            + "w kolejce Twitcha dla Ciebie. Zaznaczone Tf3Bridge włącza razem z grą i wyłącza, gdy gra nie wysyła danych albo Tf3Bridge się zamyka. "
            + "Lista jest odczytywana przy połączeniu z Twitchem (start Tf3Bridge) i po zapisaniu tego okna. "
            + "Nagrody dodanej ręcznie w panelu Twitcha Tf3Bridge nie może zmieniać (Twitch na to nie pozwala).")),
    ];

    private static string ZonesTitle => Loc.T("Where viewers may build new industries", "Gdzie widzowie mogą budować nowe zakłady");

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
            if (title.Length is 0 or > RewardConfig.MaxTitle) { error = $"{KindLabel(kind)}: " + Loc.T($"the title must have 1–{RewardConfig.MaxTitle} characters.", $"tytuł musi mieć 1–{RewardConfig.MaxTitle} znaków."); return false; }
            var prompt = Text("prompt");
            if (prompt.Length > RewardConfig.MaxPrompt) { error = $"{KindLabel(kind)}: " + Loc.T($"the description may have at most {RewardConfig.MaxPrompt} characters.", $"opis może mieć najwyżej {RewardConfig.MaxPrompt} znaków."); return false; }
            if (!Int("cost", 1, out var cost) || !Int("cooldown", 0, out var cooldown)
                || !Int("perStream", 0, out var perStream) || !Int("perUser", 0, out var perUser))
            {
                error = $"{KindLabel(kind)}: " + Loc.T("cost ≥ 1, cooldown and limits ≥ 0 (whole numbers).", "koszt ≥ 1, cooldown i limity ≥ 0 (liczby całkowite).");
                return false;
            }
            var months = old.Months;
            var monthsRange = RewardKinds.MonthsRange(kind);
            if (RewardKinds.HasMonths(kind) && (!int.TryParse(Text("months"), out months) || months < monthsRange.Min || months > monthsRange.Max))
            {
                error = $"{KindLabel(kind)}: " + Loc.T($"game months from {monthsRange.Min} to {monthsRange.Max}.", $"miesiące gry od {monthsRange.Min} do {monthsRange.Max}.");
                return false;
            }
            var factor = old.Factor;
            var range = RewardKinds.FactorRange(kind);
            if (RewardKinds.HasFactor(kind) && (!double.TryParse(Text("factor").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out factor)
                || factor < range.Min || factor > range.Max))
            {
                error = kind == RewardKind.Loan
                    ? string.Create(CultureInfo.CurrentCulture, $"{KindLabel(kind)}: ") + Loc.T(
                        string.Create(CultureInfo.CurrentCulture, $"interest from {range.Min} to {range.Max}% (e.g. 2 = 2%)."),
                        string.Create(CultureInfo.CurrentCulture, $"oprocentowanie od {range.Min} do {range.Max}% (np. 2 = 2%)."))
                    : $"{KindLabel(kind)}: " + Loc.T(
                        string.Create(CultureInfo.CurrentCulture, $"factor from {range.Min} to {range.Max} (e.g. {FactorExample(kind, range)})."),
                        string.Create(CultureInfo.CurrentCulture, $"mnożnik od {range.Min} do {range.Max} (np. {FactorExample(kind, range)})."));
                return false;
            }
            var amount = old.Amount;
            if (RewardKinds.HasAmount(kind) && (!TryParseAmount(Text("amount"), out amount)
                || amount < RewardConfig.AmountRange.Min || amount > RewardConfig.AmountRange.Max))
            {
                error = $"{KindLabel(kind)}: " + Loc.T(
                    string.Create(CultureInfo.CurrentCulture, $"amount from {RewardConfig.AmountRange.Min:N0} to {RewardConfig.AmountRange.Max:N0} $ (a whole number, e.g. 10,000,000)."),
                    string.Create(CultureInfo.CurrentCulture, $"kwota od {RewardConfig.AmountRange.Min:N0} do {RewardConfig.AmountRange.Max:N0} $ (liczba całkowita, np. 10 000 000)."));
                return false;
            }
            list.Add(new RewardConfig(kind, row.Cells["enabled"].Value is true, title, prompt, cost, cooldown, perStream, perUser, months, factor)
            {
                UserInput = row.Cells["input"].Value is true,
                Amount = amount,
            });
        }
        return true;
    }

    private static string FactorExample(RewardKind kind, (double Min, double Max) range) =>
        kind == RewardKind.BrakeSabotage ? Loc.T("0.5 = up to 50% of vehicles", "0,5 = do 50% pojazdów")
        : range.Max <= 1 ? Loc.T("0.5 = -50%", "0,5 = -50%") : Loc.T("1.5 = +50%", "1,5 = +50%");

    /// <summary>"10 000 000", "10.000.000" or "10000000": separators of thousands do not matter.</summary>
    internal static bool TryParseAmount(string text, out long amount)
    {
        var digits = new string([.. text.Where(c => !char.IsWhiteSpace(c) && c is not ('.' or ',' or '\''))]); // IsWhiteSpace also catches non-breaking spaces
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out amount);
    }

    private List<ExtraReward> ReadExtras()
    {
        _extrasGrid.EndEdit();
        return [.. Extras.Select((e, i) => e with { Enabled = _extrasGrid.Rows[i].Cells["enabled"].Value is true })];
    }

    private Dictionary<string, IndustryZone> ReadZones()
    {
        _zonesGrid.EndEdit();
        var zones = new Dictionary<string, IndustryZone>();
        foreach (DataGridViewRow row in _zonesGrid.Rows)
        {
            var label = Convert.ToString(row.Cells["zone"].Value, CultureInfo.CurrentCulture);
            var zone = ZoneChoices.FirstOrDefault(z => ZoneLabel(z) == label);
            if (zone != IndustryZone.Any) zones[(string)row.Tag!] = zone;
        }
        return zones;
    }

    private static string ZoneLabel(IndustryZone zone) => zone switch
    {
        IndustryZone.Mountains => Loc.T("only in the mountains", "tylko w górach"),
        IndustryZone.NearWater => Loc.T("only near water (up to 1 km)", "tylko przy wodzie (do 1 km)"),
        IndustryZone.Lowland => Loc.T("only on flat lowland", "tylko na płaskiej nizinie"),
        IndustryZone.Inland => Loc.T("only away from water (over 1 km)", "tylko z dala od wody (ponad 1 km)"),
        _ => Loc.T("anywhere", "wszędzie"),
    };

    public static string KindLabel(RewardKind kind) => kind switch
    {
        RewardKind.AdoptTown => Loc.T("Buy a town", "Kupno miasta"),
        RewardKind.BuyIndustry => Loc.T("Buy an industry", "Kupno zakładu"),
        RewardKind.BoostTown => Loc.T("Town boost", "Boost miasta"),
        RewardKind.BoostIndustry => Loc.T("Industry boost", "Boost przemysłu"),
        RewardKind.StopLine => Loc.T("Stop a line (now)", "Stop linii (od razu)"),
        RewardKind.HoldLine => Loc.T("Line waits at stations", "Linia czeka na stacjach"),
        RewardKind.CreateTown => Loc.T("New town (map code)", "Nowe miasto (kod z mapy)"),
        RewardKind.CreateIndustry => Loc.T("New industry (map code)", "Nowy zakład (kod z mapy)"),
        RewardKind.StrikeIndustry => Loc.T("Strike at an industry", "Strajk w zakładzie"),
        RewardKind.TaxRaid => Loc.T("Tax raid (nick)", "Nalot skarbówki (nick)"),
        RewardKind.PremiumFuel => Loc.T("Premium fuel", "Paliwo premium"),
        RewardKind.BadFuel => Loc.T("Watered-down fuel", "Chrzczone paliwo"),
        RewardKind.BrakeSabotage => Loc.T("Brake sabotage", "Sabotaż hamulców"),
        RewardKind.Subsidy => Loc.T("Company subsidy", "Dotacja dla firmy"),
        RewardKind.Loan => Loc.T("Company loan", "Kredyt dla firmy"),
        RewardKind.Bomb => Loc.T("Bomb (streamer)", "Bomba (streamer)"),
        RewardKind.Line => Loc.T("Line (streamer)", "Linia (streamer)"),
        RewardKind.Nature => Loc.T("Nature (streamer)", "Natura (streamer)"),
        RewardKind.RoadVehicle => Loc.T("Road vehicle (streamer)", "Pojazd drogowy (streamer)"),
        _ => Loc.T("Special vehicle (streamer)", "Pojazd specjalny (streamer)"),
    };
}
