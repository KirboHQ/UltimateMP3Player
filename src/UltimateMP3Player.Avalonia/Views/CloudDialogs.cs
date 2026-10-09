using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;
using UltimateMP3Player.Views;

namespace UltimateMP3Player;

// "Delete…" (what to delete of the songs) and "Find the video" (one of the videos found on YouTube), like the Windows app's.
public static partial class Dialogs
{
    private static readonly Cursor Hand = new(StandardCursorType.Hand);

    // ------------------------------------------------------------------ what to delete

    public static Task<RemoveKind?> RemoveAsync(RemovePlan plan) => Task.FromResult(Remove(plan));

    private static RemoveKind? Remove(RemovePlan plan)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = plan.Subtitle, Foreground = Res("SubTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, -4, 0, 12),
        });
        var ok = Button(plan.ButtonText(plan.Chosen), "PrimaryButton", isDefault: true);
        var cards = new List<(RemoveOption Option, Border Card, Border Dot)>();

        void Pick(RemoveOption o)
        {
            if (!o.Enabled) return;
            foreach (var x in plan.Options) x.IsChosen = x == o;
            Update();
        }

        void Update()
        {
            foreach (var (o, card, dot) in cards)
            {
                bool on = o.IsChosen && o.Enabled;
                var accent = Res(o.Danger ? "ErrorBrush" : "AccentBrush");
                card.BorderBrush = on ? accent : Res("BorderBrush");
                card.Background = on ? Res(o.Danger ? "ErrorSoftBrush" : "AccentSoftBrush") : Res("Surface2Brush");
                dot.BorderBrush = on ? accent : Res("MutedBrush");
                dot.Child = on ? new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = accent } : null;
            }
            var chosen = plan.Chosen;
            ok.Content = plan.ButtonText(chosen);
            ok.Theme = Res<Avalonia.Styling.ControlTheme>(chosen?.Danger == true ? "DangerButton" : "PrimaryButton");
            ok.IsEnabled = chosen != null;
        }

        foreach (var o in plan.Options)
        {
            var dot = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(2), VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 12, 0),
            };
            var head = new DockPanel();
            if (o.HasSize)
            {
                var size = new TextBlock { Text = o.Size, Foreground = Res("MutedBrush"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                DockPanel.SetDock(size, Dock.Right);
                head.Children.Add(size);
            }
            head.Children.Add(new TextBlock { Text = o.Title, FontWeight = FontWeight.SemiBold, FontSize = 14.5, Foreground = Res(o.Danger ? "ErrorBrush" : "TextBrush") });
            var text = new StackPanel();
            text.Children.Add(head);
            text.Children.Add(new TextBlock { Text = o.Detail, TextWrapping = TextWrapping.Wrap, Foreground = Res("SubTextBrush"), FontSize = 12.5, LineHeight = 18, Margin = new Thickness(0, 3, 0, 0) });
            var row = new DockPanel();
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            row.Children.Add(text);
            var card = new Border
            {
                CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1.5), Padding = new Thickness(14, 11, 14, 12), Margin = new Thickness(0, 0, 0, 8),
                Child = row, Opacity = o.Enabled ? 1 : 0.45, Cursor = o.Enabled ? Hand : null,
            };
            var option = o;
            card.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Left) Pick(option);
            };
            card.PointerEntered += (_, _) => { if (option.Enabled && !option.IsChosen) card.Background = Res("Surface3Brush"); };
            card.PointerExited += (_, _) => Update();
            cards.Add((o, card, dot));
            body.Children.Add(card);
        }
        Update();

        var win = Frame(plan.Title, body, 460, Button(L.T("Annulla"), "GhostButton", isCancel: true), ok);
        RemoveKind? result = null;
        ok.Click += (_, _) =>
        {
            result = plan.Chosen?.Kind;
            win.DialogResult = true;
        };
        // Arrows move the choice, Enter confirms.
        win.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Up or Key.Down)) return;
            var usable = plan.Options.Where(x => x.Enabled).ToList();
            int i = usable.IndexOf(plan.Chosen!);
            i = Math.Clamp(i + (e.Key == Key.Down ? 1 : -1), 0, usable.Count - 1);
            Pick(usable[i]);
            e.Handled = true;
        };
        win.Opened += (_, _) => ok.Focus();
        return win.ShowDialog() == true ? result : null;
    }

    // ------------------------------------------------------------------ a video for a song

    // The videos on YouTube that look like the song, the closest first (already chosen); the ones of another length are
    // marked (the video plays in time with the audio). Null: closed, or nothing found.
    public static Task<VideoCandidate?> PickVideoAsync(MainViewModel main, TrackViewModel song)
    {
        var t = song.T;
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = $"{t.Title} · {t.DisplayArtist}" + (t.Duration > 0 ? " · " + Text.Duration(t.Duration) : ""),
            Foreground = Res("SubTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, -4, 0, 12),
        });
        var status = new TextBlock { Text = L.T("Cerco su YouTube…"), Foreground = Res("MutedBrush"), Margin = new Thickness(2, 6, 0, 6), TextWrapping = TextWrapping.Wrap };
        body.Children.Add(status);
        var list = new StackPanel();
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 420, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });

        var ok = Button(L.T("Scarica il video"), "PrimaryButton", isDefault: true);
        ok.IsEnabled = false;
        var win = Frame(L.T("Scegli il video"), body, 520, Button(L.T("Annulla"), "GhostButton", isCancel: true), ok);
        VideoCandidate? chosen = null;
        var rows = new List<(VideoCandidate C, Border Row)>();

        void Pick(VideoCandidate c)
        {
            chosen = c;
            ok.IsEnabled = true;
            foreach (var (cand, row) in rows)
            {
                bool on = cand == c;
                row.BorderBrush = on ? Res("AccentBrush") : Brushes.Transparent;
                row.Background = on ? Res("AccentSoftBrush") : Res("Surface2Brush");
            }
        }

        async void Search()
        {
            List<VideoCandidate> found;
            try { found = await Task.Run(() => VideoFinder.SearchAsync(t.Title, t.Artist, t.Duration, CancellationToken.None)); }
            catch
            {
                status.Text = L.T("La ricerca non è riuscita: controlla la connessione e riprova.");
                return;
            }
            if (!win.IsVisible) return;
            if (found.Count == 0)
            {
                status.Text = L.T("Nessun video trovato su YouTube per questo brano.");
                return;
            }
            status.Text = L.T("Scegli quello giusto: il video va a tempo con l'audio, quindi deve essere la stessa versione.");
            foreach (var c in found.Take(8))
            {
                var thumb = new CoverImage { Width = 120, Height = 68, CornerRadius = new CornerRadius(8) };
                var frame = new Border { Width = 120, Height = 68, CornerRadius = new CornerRadius(8), Background = Res("Surface3Brush"), Child = thumb };
                if (c.Thumb != null) _ = LoadThumb(thumb, c.Thumb);
                var info = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock { Text = c.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
                var meta = string.Join(" · ", new[] { c.Channel, c.Duration is double d ? Text.Duration(d) : null }.Where(s => !string.IsNullOrEmpty(s)));
                info.Children.Add(new TextBlock { Text = meta, Foreground = Res("SubTextBrush"), FontSize = 12.5, Margin = new Thickness(0, 3, 0, 0) });
                if (c.LengthDiffers(t.Duration))
                    info.Children.Add(new TextBlock
                    {
                        Text = "⚠ " + L.T("Durata diversa dal brano: potrebbe andare fuori tempo"), Foreground = Res("WarningBrush"), FontSize = 12,
                        Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap,
                    });
                var dock = new DockPanel();
                DockPanel.SetDock(frame, Dock.Left);
                dock.Children.Add(frame);
                dock.Children.Add(info);
                var row = new Border
                {
                    CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1.5), BorderBrush = Brushes.Transparent, Background = Res("Surface2Brush"),
                    Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 8), Child = dock, Cursor = Hand,
                };
                var cand = c;
                row.PointerPressed += (_, e) =>
                {
                    Pick(cand);
                    if (e.ClickCount == 2) win.DialogResult = true;
                };
                rows.Add((c, row));
                list.Children.Add(row);
            }
            Pick(found[0]);
        }

        static async Task LoadThumb(CoverImage img, string url)
        {
            if (await WebImages.LoadAsync(new[] { url }, 240) is { } src) img.Source = src;
        }

        ok.Click += (_, _) => win.DialogResult = true;
        win.Opened += (_, _) => Search();
        return Task.FromResult(win.ShowDialog() == true ? chosen : null);
    }
}
