using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using UltimateMP3Player.Core;

namespace UltimateMP3Player;

// "What's new" at the first start after an update (ReleaseNotes): a single version is shown open, several are cards to open
// one by one, the newest first. Avalonia's twin: Avalonia/Views/WhatsNew.cs.
public static partial class Dialogs
{
    public static Task WhatsNewAsync(IReadOnlyList<ReleaseNote> notes, AppSettings settings)
    {
        WhatsNew(notes, settings);
        return Task.CompletedTask;
    }

    // "Don't show again" under the notes turns AppSettings.ShowWhatsNew off.
    public static void WhatsNew(IReadOnlyList<ReleaseNote> notes, AppSettings settings)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        if (notes.Count == 1)
        {
            stack.Children.Add(NoteTitle(notes[0]));
            var body = NoteBody(notes[0]);
            body.Margin = new Thickness(0, 12, 0, 0);
            stack.Children.Add(body);
        }
        else
        {
            stack.Children.Add(new TextBlock
            {
                Text = L.F("{0} new versions: open one to see what changed.", notes.Count), TextWrapping = TextWrapping.Wrap,
                Foreground = Res("SubTextBrush"), Margin = new Thickness(0, 0, 0, 12),
            });
            foreach (var n in notes) stack.Children.Add(NoteCard(n));
        }
        var scroll = new ScrollViewer
        {
            Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height * 0.62,
        };
        var hide = new CheckBox { Content = L.T("Don't show again"), IsChecked = !settings.ShowWhatsNew, Margin = new Thickness(0, 16, 0, 0) };
        var all = new StackPanel();
        all.Children.Add(scroll);
        all.Children.Add(hide);
        var close = Button(L.T("Close"), "PrimaryButton", isDefault: true, isCancel: true);
        var win = Frame(L.T("What's new"), all, 540, close);
        close.Click += (_, _) => win.DialogResult = true;
        win.ShowDialog();
        settings.ShowWhatsNew = hide.IsChecked != true;
        settings.Save();
    }

    // "v4.0.0" in the accent's colour, the release's title under it.
    private static StackPanel NoteTitle(ReleaseNote n)
    {
        var s = new StackPanel();
        s.Children.Add(new TextBlock { Text = n.VersionText, Foreground = Res("AccentTextBrush"), FontWeight = FontWeights.SemiBold, FontSize = 12.5 });
        if (n.Title.Length > 0)
            s.Children.Add(new TextBlock
            {
                Text = n.Title, Foreground = Res("TextBrush"), FontWeight = FontWeights.Bold, FontSize = 16, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });
        return s;
    }

    private static StackPanel NoteBody(ReleaseNote n)
    {
        var s = new StackPanel();
        var blocks = n.Blocks;
        if (blocks.Count == 0) blocks.Add(new NoteBlock(NoteKind.Text, new() { new NoteRun(L.T("Small fixes and improvements."), false, false) }));
        foreach (var b in blocks)
        {
            bool heading = b.Kind == NoteKind.Heading;
            var text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, LineHeight = 20, FontSize = heading ? 15 : 14,
                Foreground = Res(heading ? "TextBrush" : b.Kind == NoteKind.Quote ? "MutedBrush" : "SubTextBrush"),
                FontWeight = heading ? FontWeights.Bold : FontWeights.Normal,
            };
            foreach (var r in b.Runs)
            {
                var run = new Run(r.Text) { FontStyle = r.Italic ? FontStyles.Italic : FontStyles.Normal };
                if (r.Bold && !heading)
                {
                    run.FontWeight = FontWeights.SemiBold;
                    run.Foreground = Res("TextBrush");
                }
                text.Inlines.Add(run);
            }
            FrameworkElement e = text;
            if (b.Kind == NoteKind.Bullet)
            {
                var row = new DockPanel();
                var dot = new TextBlock { Text = "•", Foreground = Res("AccentTextBrush"), FontWeight = FontWeights.Bold, Margin = new Thickness(2, 0, 10, 0) };
                DockPanel.SetDock(dot, Dock.Left);
                row.Children.Add(dot);
                row.Children.Add(text);
                e = row;
            }
            else if (b.Kind == NoteKind.Quote)
                e = new Border { BorderBrush = Res("AccentBrush"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 2, 0, 2), Child = text };
            e.Margin = new Thickness(0, s.Children.Count == 0 ? 0 : heading ? 14 : 6, 0, heading ? 2 : 0);
            s.Children.Add(e);
        }
        return s;
    }

    // A version among several: its title opens and closes its notes.
    private static Border NoteCard(ReleaseNote n)
    {
        var body = NoteBody(n);
        body.Margin = new Thickness(14, 0, 14, 14);
        body.Visibility = Visibility.Collapsed;
        var chevron = new TextBlock
        {
            Text = "\uE70D", Style = (Style)Application.Current.Resources["Glyph"], FontSize = 11, Foreground = Res("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
        };
        var head = new DockPanel();
        DockPanel.SetDock(chevron, Dock.Right);
        head.Children.Add(chevron);
        head.Children.Add(NoteTitle(n));
        var button = new Button
        {
            Content = head, Style = (Style)Application.Current.Resources["SubtleButton"], HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14, 10, 14, 10),
        };
        button.Click += (_, _) =>
        {
            bool open = body.Visibility != Visibility.Visible;
            body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = open ? "\uE70E" : "\uE70D";
        };
        var stack = new StackPanel();
        stack.Children.Add(button);
        stack.Children.Add(body);
        return new Border { Background = Res("Surface2Brush"), CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 0, 8), Child = stack };
    }
}
