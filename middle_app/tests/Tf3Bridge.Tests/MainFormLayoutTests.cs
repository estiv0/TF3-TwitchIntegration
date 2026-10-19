using Tf3Bridge.Ui;

namespace Tf3Bridge.Tests;

/// <summary>The main window must keep every button and status line inside the window at any allowed width.</summary>
public sealed class MainFormLayoutTests
{
    private static readonly string[] Buttons =
        ["Uruchom", "Folder wyjściowy", "Folder moda", "Ustawienia moda…", "Azure…", "Twitch…", "Nagrody…", "Test akcji…"];

    private const string LongStatus = "Nagrody: kanał bez Affiliate/Partner: nagrody wyłączone (zostaje „Symuluj nagrodę”) i jeszcze dużo dłuższy tekst, który na pewno się nie mieści";

    // WinForms needs an STA thread.
    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    private static (Form Form, Button[] Buttons, Label[] Lines) Build(int width)
    {
        var form = new Form { Font = new Font("Segoe UI", 9f), ClientSize = new Size(width, 480) };
        var buttons = Buttons.Select((t, i) => MainForm.ToolButton(t, groupStart: i == 4)).ToArray();
        var lines = Enumerable.Range(0, 5).Select(_ => MainForm.StatusLine()).ToArray();
        foreach (var l in lines) l.Text = LongStatus;
        var title = new Label { AutoSize = true, Text = "● Działa", Font = new Font("Segoe UI", 11f, FontStyle.Bold) };
        var log = new TextBox { Multiline = true, Dock = DockStyle.Fill };
        form.Controls.Add(MainForm.Root(MainForm.Toolbar(buttons), MainForm.StatusPanel(title, lines), new Panel { Height = 60, Dock = DockStyle.Fill }, log));
        form.PerformLayout();
        return (form, buttons, lines);
    }

    private static Rectangle InForm(Control c, Form f) => f.RectangleToClient(c.RectangleToScreen(c.ClientRectangle));

    [Theory]
    [InlineData(560)]  // minimum window width (MinimumSize 560 minus the frame is narrower anyway)
    [InlineData(700)]
    [InlineData(900)]  // default
    [InlineData(1400)]
    public void Buttons_and_status_lines_stay_inside_the_window(int width) => Sta(() =>
    {
        var (form, buttons, lines) = Build(width);
        using (form)
        {
            form.CreateControl();
            form.PerformLayout();
            foreach (var b in buttons)
            {
                var r = InForm(b, form);
                Assert.True(r.Right <= form.ClientSize.Width && r.Width > 0, $"{b.Text} at {r} outside {form.ClientSize} (width {width})");
            }
            foreach (var l in lines)
                Assert.True(InForm(l, form).Right <= form.ClientSize.Width, $"status line wider than the window at {width}");
            var log = form.Controls[0].Controls[3];
            Assert.True(log.Height > 50, $"log too small at {width}: {log.Height}");
        }
    });

    [Fact]
    public void Narrow_window_wraps_buttons_into_more_rows() => Sta(() =>
    {
        var (narrow, nb, _) = Build(560);
        var (wide, wb, _) = Build(1400);
        using (narrow)
        using (wide)
        {
            narrow.CreateControl(); narrow.PerformLayout();
            wide.CreateControl(); wide.PerformLayout();
            Assert.True(nb.Select(b => b.Top).Distinct().Count() > 1, "expected wrapped rows at 560 px");
            Assert.Single(wb.Select(b => b.Top).Distinct());
        }
    });
}
