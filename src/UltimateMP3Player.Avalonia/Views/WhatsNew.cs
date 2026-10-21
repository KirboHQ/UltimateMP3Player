using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Views;

// "What's new" at the first start after an update (ReleaseNotes): a single version is shown open, several are cards to open
// one by one, the newest first. The content of the dialog (Linux/macOS) and of the phones' sheet (touch: bigger text);
// linked into the Android and iPhone apps. WPF's twin: Views/WhatsNew.cs.
public static class WhatsNew
{
    public static Control Build(IReadOnlyList<ReleaseNote> notes, bool touch)
    {
        var stack = new StackPanel();
        if (notes.Count == 1)
        {
            stack.Children.Add(Title(notes[0], touch));
            var body = Body(notes[0], touch);
            body.Margin = new Thickness(0, 12, 0, 0);
            stack.Children.Add(body);
            return stack;
        }
        stack.Children.Add(new TextBlock
        {
            Text = L.F("{0} new versions: open one to see what changed.", notes.Count), TextWrapping = TextWrapping.Wrap,
            Foreground = Ui.Res("SubTextBrush"), FontSize = touch ? 14.5 : 14, Margin = new Thickness(0, 0, 0, 12),
        });
        foreach (var n in notes) stack.Children.Add(Card(n, touch));
        return stack;
    }

    // "v4.0.0" in the accent's colour, the release's title under it.
    private static StackPanel Title(ReleaseNote n, bool touch)
    {
        var s = new StackPanel();
        s.Children.Add(new TextBlock { Text = n.VersionText, Foreground = Ui.Res("AccentTextBrush"), FontWeight = FontWeight.SemiBold, FontSize = touch ? 13 : 12.5 });
        if (n.Title.Length > 0)
            s.Children.Add(new TextBlock
            {
                Text = n.Title, Foreground = Ui.Res("TextBrush"), FontWeight = FontWeight.Bold, FontSize = touch ? 17 : 16,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
            });
        return s;
    }

    private static StackPanel Body(ReleaseNote n, bool touch)
    {
        var s = new StackPanel();
        var blocks = n.Blocks;
        if (blocks.Count == 0) blocks.Add(new NoteBlock(NoteKind.Text, new() { new NoteRun(L.T("Small fixes and improvements."), false, false) }));
        double size = touch ? 14.5 : 14;
        foreach (var b in blocks)
        {
            bool heading = b.Kind == NoteKind.Heading;
            var text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, LineHeight = touch ? 21 : 20, FontSize = heading ? size + 1 : size,
                Foreground = Ui.Res(heading ? "TextBrush" : b.Kind == NoteKind.Quote ? "MutedBrush" : "SubTextBrush"),
                FontWeight = heading ? FontWeight.Bold : FontWeight.Normal,
            };
            text.Inlines ??= new InlineCollection();
            foreach (var r in b.Runs)
            {
                var run = new Run(r.Text) { FontStyle = r.Italic ? FontStyle.Italic : FontStyle.Normal };
                if (r.Bold && !heading)
                {
                    run.FontWeight = FontWeight.SemiBold;
                    run.Foreground = Ui.Res("TextBrush");
                }
                text.Inlines.Add(run);
            }
            Control e = text;
            if (b.Kind == NoteKind.Bullet)
            {
                var row = new DockPanel();
                var dot = new TextBlock { Text = "•", Foreground = Ui.Res("AccentTextBrush"), FontWeight = FontWeight.Bold, FontSize = size, Margin = new Thickness(2, 0, 10, 0) };
                DockPanel.SetDock(dot, Dock.Left);
                row.Children.Add(dot);
                row.Children.Add(text);
                e = row;
            }
            else if (b.Kind == NoteKind.Quote)
                e = new Border { BorderBrush = Ui.Res("AccentBrush"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 2, 0, 2), Child = text };
            e.Margin = new Thickness(0, s.Children.Count == 0 ? 0 : heading ? 14 : touch ? 8 : 6, 0, heading ? 2 : 0);
            s.Children.Add(e);
        }
        return s;
    }

    // A version among several: its title opens and closes its notes.
    private static Border Card(ReleaseNote n, bool touch)
    {
        var body = Body(n, touch);
        body.Margin = new Thickness(14, 0, 14, 14);
        body.IsVisible = false;
        var chevron = new TextBlock
        {
            Text = Icons.Map("\uE70D"), FontSize = touch ? 13 : 11, Foreground = Ui.Res("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
        };
        chevron.Classes.Add("glyph");
        var head = new DockPanel();
        DockPanel.SetDock(chevron, Dock.Right);
        head.Children.Add(chevron);
        head.Children.Add(Title(n, touch));
        var button = new Button
        {
            Content = head, Theme = Ui.Theme("SubtleButton"), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = touch ? new Thickness(14, 12) : new Thickness(14, 10),
        };
        button.Click += (_, _) =>
        {
            body.IsVisible = !body.IsVisible;
            chevron.Text = Icons.Map(body.IsVisible ? "\uE70E" : "\uE70D");
        };
        var stack = new StackPanel();
        stack.Children.Add(button);
        stack.Children.Add(body);
        return new Border { Background = Ui.Res("Surface2Brush"), CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 0, 8), Child = stack };
    }
}
