namespace Tf3Bridge.Ui;

/// <summary>
/// A read-only help window opened from a dialog's "How does it work?" button, so long explanations
/// don't crowd the dialog itself. Sections are a heading and a paragraph each.
/// </summary>
public sealed class HelpDialog : Form
{
    public HelpDialog(string title, IReadOnlyList<(string Heading, string Text)> sections)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(620, 520);
        MinimumSize = new Size(420, 300);

        var body = new RichTextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Window,
            ScrollBars = RichTextBoxScrollBars.Vertical, DetectUrls = false, TabStop = false,
        };
        using var bold = new Font(Font, FontStyle.Bold);
        foreach (var (heading, text) in sections)
        {
            if (body.TextLength > 0) body.AppendText("\n\n");
            body.SelectionFont = bold;
            body.AppendText(heading + "\n");
            body.SelectionFont = Font;
            body.AppendText(text);
        }
        body.Select(0, 0);

        var close = new Button { Text = Loc.T("Close"), DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(90, 28) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(close);
        CancelButton = close;

        var frame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14) };
        frame.Controls.Add(body);
        frame.Controls.Add(buttons);
        Controls.Add(frame);
    }

    /// <summary>A button that opens the help window over <paramref name="owner"/>.</summary>
    public static Button Button(Form owner, string title, IReadOnlyList<(string Heading, string Text)> sections)
    {
        var button = new Button { Text = Loc.T("How does it work?"), AutoSize = true, MinimumSize = new Size(90, 28) };
        button.Click += (_, _) =>
        {
            using var help = new HelpDialog(title, sections);
            help.ShowDialog(owner);
        };
        return button;
    }

    /// <summary>
    /// The dialog's bottom row: the help button on the left, <paramref name="right"/> on the right
    /// (the first one rightmost, like a right-to-left flow).
    /// </summary>
    public static TableLayoutPanel ButtonRow(Button help, params Control[] right)
    {
        var row = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 10, 0, 0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var flow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = Padding.Empty, WrapContents = false };
        flow.Controls.AddRange(right);
        help.Anchor = AnchorStyles.Left;
        row.Controls.Add(help, 0, 0);
        row.Controls.Add(flow, 1, 0);
        return row;
    }
}
