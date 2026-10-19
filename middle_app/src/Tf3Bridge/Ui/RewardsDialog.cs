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
        AutoSize = true, Text = "Odpowiadaj widzom na czacie (np. „@widz boost miasta Szczecin działa do 12 maja 1922”, albo dlaczego punkty wróciły)",
    };
    private readonly CheckBox _notify = new()
    {
        AutoSize = true, Text = "Powiadomienie w grze z logo Twitcha po wykonanej nagrodzie (gdy punkty zostały pobrane)",
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
        Text = "Tf3Bridge: nagrody za punkty kanału";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var statusLabel = new Label { AutoSize = true, MaximumSize = new Size(1060, 0), ForeColor = SystemColors.GrayText, Text = "Stan: " + status };

        AddText("title", "Tytuł (max 45)", 170);
        AddText("prompt", "Opis dla widza (max 200)", 330);
        _grid.Columns["prompt"]!.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.Columns.Insert(0, new DataGridViewCheckBoxColumn { Name = "enabled", HeaderText = "Wł.", Width = 36 });
        AddText("cost", "Koszt", 65);
        AddText("cooldown", "Cooldown (s)", 80);
        AddText("perStream", "Limit / stream", 80);
        AddText("perUser", "Limit / widz", 75);
        AddText("months", "Miesiące gry", 75);
        AddText("factor", "Mnożnik / %", 65);
        AddText("amount", "Kwota ($)", 95);
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "input", HeaderText = "Tekst od widza", Width = 80 });
        // Long headers ("Limit / stream", "Tekst od widza") wrap instead of being cut off.
        _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.Columns.Insert(1, new DataGridViewTextBoxColumn { Name = "kind", HeaderText = "Akcja", Width = 140, ReadOnly = true });

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

        _zonesGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "industry", HeaderText = "Zakład", Width = 170, ReadOnly = true });
        var where = new DataGridViewComboBoxColumn
        {
            Name = "zone", HeaderText = "Gdzie widz może go zbudować", Width = 230, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
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
        var zonesTitle = SectionTitle("Gdzie widzowie mogą budować nowe zakłady");

        var extrasTitle = SectionTitle($"Twoje nagrody „{ExtraReward.Prefix}…” włączane razem z grą");
        _extrasGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "enabled", HeaderText = "Wł.", Width = 36 });
        _extrasGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "title", HeaderText = "Tytuł", Width = 300, ReadOnly = true });
        _extrasGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "cost", HeaderText = "Koszt", Width = 70, ReadOnly = true });
        _extrasGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "state", HeaderText = "Stan", Width = 630, ReadOnly = true });
        foreach (var e in extras)
        {
            var i = _extrasGrid.Rows.Add(e.Enabled, e.Title, e.Cost,
                e.Switchable ? "Tf3Bridge włącza i wyłącza ją z grą" : "Twitch nie pozwala Tf3Bridge jej zmieniać (dodana ręcznie w panelu)");
            if (!e.Switchable) _extrasGrid.Rows[i].Cells["state"].Style.ForeColor = Color.DarkOrange;
        }
        // The section only matters when the channel has such rewards; an empty one just confuses.
        extrasTitle.Visible = _extrasGrid.Visible = extras.Count > 0;

        var ok = new Button { Text = "Zapisz i wyślij na Twitcha", AutoSize = true, MinimumSize = new Size(90, 28) };
        var cancel = new Button { Text = "Anuluj", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28) };
        var defaults = new Button { Text = "Przywróć domyślne", AutoSize = true, MinimumSize = new Size(90, 28) };
        var help = HelpDialog.Button(this, "Nagrody za punkty kanału: jak to działa", HelpSections);
        var buttons = HelpDialog.ButtonRow(help, cancel, ok, defaults);
        CancelButton = cancel;

        var notifyRow = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty, WrapContents = false };
        notifyRow.Controls.AddRange([_notify, _notifySeconds,
            new Label { AutoSize = true, Text = "s na ekranie (potem zostaje w dzienniku powiadomień gry)", Margin = new Padding(0, 3, 0, 0) }]);
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
    private static readonly (string Heading, string Text)[] HelpSections =
    [
        ("Nagrody za punkty kanału",
            "Tylko te nagrody mogą zmieniać grę; czat jest wyłącznie informacyjny. Tf3Bridge sam tworzy je na Twitchu, "
            + "włącza, gdy gra wysyła dane, i wyłącza, gdy gra stoi, jest zatrzymana albo Tf3Bridge się zamyka."),
        ("Tekst od widza i zwrot punktów",
            "Widz wpisuje nazwę miasta, zakładu albo linii (przy nowym mieście i zakładzie wkleja kod z mapy na stronie). "
            + "Gdy celu nie ma, jest niejednoznaczny, miejsce się nie nadaje albo gra nie wykona akcji, punkty wracają do widza."),
        ("Opis dla widza",
            "W opisie można użyć: {months} (czas efektu), {percent} (siła efektu), {site} (adres mapy) i {amount} (kwota dotacji albo kredytu)."),
        ("Sabotaże",
            "Strajk, skarbówka, chrzczone paliwo i sabotaż hamulców działają tylko na obiekty innych widzów. Przy skarbówce widz wpisuje nick ofiary."),
        ("Mnożnik / %",
            "Mnożnik powyżej 1 to wzrost (1,5 = +50%), poniżej 1 spadek (0,5 = -50%). Przy sabotażu hamulców to największa część pojazdów linii, "
            + "która zniknie (0,5 = do 50%, zawsze co najmniej 1 pojazd). Przy kredycie ta kolumna to oprocentowanie w % (2 = 2%)."),
        ("Dotacja i kredyt",
            "Dają firmie „Kwotę”, widz nic nie wpisuje. Kredyt trafia do zakładki Kredyty w grze: gra spłaca go co miesiąc przez podane miesiące gry, "
            + "a odsetki w TF3 to kwota × %, rozłożone na raty. Gra pozwala mieć najwyżej 4 kredyty naraz (razem z Twoimi)."),
        ("Nagrody wykonywane przez streamera",
            "Bomba, linia, natura i pojazdy nie robią nic w grze. Tf3Bridge tylko je tworzy i włącza razem z grą, a wykupienia zostają w kolejce Twitcha, "
            + "gdzie sam je wykonujesz i akceptujesz albo zwracasz. Przy nich wybierasz też, czy widz wpisuje tekst."),
        ("Limity i czas",
            "Limit 0 = bez limitu. Czas efektów jest w miesiącach gry (miesiąc to ok. 1–1,5 min przy prędkości 1×; na pauzie efekt stoi)."),
        ("Gdzie widzowie mogą budować nowe zakłady",
            "Platformy, łowiska i pogłębiarki i tak stają tylko na wodzie, reszta tylko na lądzie. Regiony Tf3Bridge liczy z terenu gry w promieniu ok. 1 km: "
            + "góry to najbardziej pofałdowany ląd mapy (najwyżej 15%), nizina to prawie płaski teren. Gdy danego regionu na mapie nie ma "
            + "(np. płaska mapa bez gór), ograniczenie nie działa. Na stronie zakazany teren jest czerwony, a kod z takiego miejsca jest odrzucany "
            + "(punkty wracają). Ile mapy zajmuje każdy region, Tf3Bridge pisze w logu po eksporcie terenu."),
        ($"Twoje nagrody „{ExtraReward.Prefix}…”",
            $"Nagrody z tytułem zaczynającym się od „{ExtraReward.Prefix}”, które nie są żadną z akcji Tf3Bridge. Nie robią nic w grze, a wykupienia zostają "
            + "w kolejce Twitcha dla Ciebie. Zaznaczone Tf3Bridge włącza razem z grą i wyłącza, gdy gra nie wysyła danych albo Tf3Bridge się zamyka. "
            + "Lista jest odczytywana przy połączeniu z Twitchem (start Tf3Bridge) i po zapisaniu tego okna. "
            + "Nagrody dodanej ręcznie w panelu Twitcha Tf3Bridge nie może zmieniać (Twitch na to nie pozwala)."),
    ];

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
            var months = old.Months;
            var monthsRange = RewardKinds.MonthsRange(kind);
            if (RewardKinds.HasMonths(kind) && (!int.TryParse(Text("months"), out months) || months < monthsRange.Min || months > monthsRange.Max))
            {
                error = $"{KindLabel(kind)}: miesiące gry od {monthsRange.Min} do {monthsRange.Max}.";
                return false;
            }
            var factor = old.Factor;
            var range = RewardKinds.FactorRange(kind);
            if (RewardKinds.HasFactor(kind) && (!double.TryParse(Text("factor").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out factor)
                || factor < range.Min || factor > range.Max))
            {
                error = kind == RewardKind.Loan
                    ? string.Create(CultureInfo.CurrentCulture, $"{KindLabel(kind)}: oprocentowanie od {range.Min} do {range.Max}% (np. 2 = 2%).")
                    : string.Create(CultureInfo.CurrentCulture, $"{KindLabel(kind)}: mnożnik od {range.Min} do {range.Max} (np. {FactorExample(kind, range)}).");
                return false;
            }
            var amount = old.Amount;
            if (RewardKinds.HasAmount(kind) && (!TryParseAmount(Text("amount"), out amount)
                || amount < RewardConfig.AmountRange.Min || amount > RewardConfig.AmountRange.Max))
            {
                error = string.Create(CultureInfo.CurrentCulture,
                    $"{KindLabel(kind)}: kwota od {RewardConfig.AmountRange.Min:N0} do {RewardConfig.AmountRange.Max:N0} $ (liczba całkowita, np. 10 000 000).");
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
        kind == RewardKind.BrakeSabotage ? "0,5 = do 50% pojazdów" : range.Max <= 1 ? "0,5 = -50%" : "1,5 = +50%";

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
        IndustryZone.Mountains => "tylko w górach",
        IndustryZone.NearWater => "tylko przy wodzie (do 1 km)",
        IndustryZone.Lowland => "tylko na płaskiej nizinie",
        IndustryZone.Inland => "tylko z dala od wody (ponad 1 km)",
        _ => "wszędzie",
    };

    public static string KindLabel(RewardKind kind) => kind switch
    {
        RewardKind.AdoptTown => "Kupno miasta",
        RewardKind.BuyIndustry => "Kupno zakładu",
        RewardKind.BoostTown => "Boost miasta",
        RewardKind.BoostIndustry => "Boost przemysłu",
        RewardKind.StopLine => "Stop linii (od razu)",
        RewardKind.HoldLine => "Linia czeka na stacjach",
        RewardKind.CreateTown => "Nowe miasto (kod z mapy)",
        RewardKind.CreateIndustry => "Nowy zakład (kod z mapy)",
        RewardKind.StrikeIndustry => "Strajk w zakładzie",
        RewardKind.TaxRaid => "Nalot skarbówki (nick)",
        RewardKind.PremiumFuel => "Paliwo premium",
        RewardKind.BadFuel => "Chrzczone paliwo",
        RewardKind.BrakeSabotage => "Sabotaż hamulców",
        RewardKind.Subsidy => "Dotacja dla firmy",
        RewardKind.Loan => "Kredyt dla firmy",
        RewardKind.Bomb => "Bomba (streamer)",
        RewardKind.Line => "Linia (streamer)",
        RewardKind.Nature => "Natura (streamer)",
        RewardKind.RoadVehicle => "Pojazd drogowy (streamer)",
        _ => "Pojazd specjalny (streamer)",
    };
}
