using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;
using UltimateMP3Player.Views;

namespace UltimateMP3Player;

// "Delete…" (what to delete of the songs) and "Find the video" (one of the videos found on YouTube), as sheets from the
// bottom like the other dialogs of the phone.
public static partial class Dialogs
{
    // A card of a choice, tapped to pick it (not when the tap only stopped a moving list).
    private static void OnTap(Control c, Action tapped)
    {
        c.Tapped += (_, e) =>
        {
            if (FlingScroll.Caught) return;
            tapped();
            e.Handled = true;
        };
    }

    private static Button Wide(string text, string theme) => new()
    {
        Content = text, Theme = Ui.Theme(theme), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
    };

    // ------------------------------------------------------------------ what to delete

    public static async Task<RemoveKind?> RemoveAsync(RemovePlan plan)
    {
        var sheets = Sheets;
        if (sheets == null) return null;
        var body = new StackPanel { Margin = new Thickness(20, 4, 20, 18) };
        body.Children.Add(new TextBlock { Text = plan.Title, FontSize = 20, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 4) });
        body.Children.Add(new TextBlock { Text = plan.Subtitle, Foreground = Ui.Res("SubTextBrush"), FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 4, 14) });
        var ok = Wide(plan.ButtonText(plan.Chosen), "TouchPrimary");
        var cards = new List<(RemoveOption Option, Border Card, Border Dot)>();

        void Update()
        {
            foreach (var (o, card, dot) in cards)
            {
                bool on = o.IsChosen && o.Enabled;
                var accent = Ui.Res(o.Danger ? "ErrorBrush" : "AccentBrush");
                card.BorderBrush = on ? accent : Ui.Res("BorderBrush");
                card.Background = on ? Ui.Res(o.Danger ? "ErrorSoftBrush" : "AccentSoftBrush") : Ui.Res("Surface2Brush");
                dot.BorderBrush = on ? accent : Ui.Res("MutedBrush");
                dot.Child = on ? new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = accent } : null;
            }
            var chosen = plan.Chosen;
            ok.Content = plan.ButtonText(chosen);
            ok.Theme = Ui.Theme(chosen?.Danger == true ? "TouchDanger" : "TouchPrimary");
            ok.IsEnabled = chosen != null;
        }

        foreach (var o in plan.Options)
        {
            var dot = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(2), VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 14, 0),
            };
            var head = new DockPanel();
            if (o.HasSize)
            {
                var size = new TextBlock { Text = o.Size, Foreground = Ui.Res("MutedBrush"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                DockPanel.SetDock(size, Dock.Right);
                head.Children.Add(size);
            }
            head.Children.Add(new TextBlock { Text = o.Title, FontWeight = FontWeight.SemiBold, FontSize = 16, Foreground = Ui.Res(o.Danger ? "ErrorBrush" : "TextBrush") });
            var text = new StackPanel();
            text.Children.Add(head);
            text.Children.Add(new TextBlock { Text = o.Detail, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("SubTextBrush"), FontSize = 13.5, LineHeight = 19, Margin = new Thickness(0, 4, 0, 0) });
            var row = new DockPanel();
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            row.Children.Add(text);
            var card = new Border
            {
                CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1.5), Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, 10),
                Child = row, Opacity = o.Enabled ? 1 : 0.45,
            };
            var option = o;
            OnTap(card, () =>
            {
                if (!option.Enabled) return;
                foreach (var x in plan.Options) x.IsChosen = x == option;
                Haptics.Tick();
                Update();
            });
            cards.Add((o, card, dot));
            body.Children.Add(card);
        }
        Update();

        RemoveKind? result = null;
        var no = Wide(L.T("Annulla"), "TouchGhost");
        no.Click += (_, _) => sheets.Close(body);
        ok.Click += (_, _) =>
        {
            result = plan.Chosen?.Kind;
            sheets.Close(body);
        };
        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*"), Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(no);
        Grid.SetColumn(ok, 2);
        buttons.Children.Add(ok);
        body.Children.Add(buttons);
        await sheets.Show(new ScrollViewer { Content = body }, maxHeightShare: 0.92);
        return result;
    }

    // ------------------------------------------------------------------ a video for a song

    // The videos on YouTube that look like the song, the closest first (already chosen); the ones of another length are
    // marked (the video plays in time with the audio). Null: closed, or nothing found.
    public static async Task<VideoCandidate?> PickVideoAsync(MainViewModel main, TrackViewModel song)
    {
        var sheets = Sheets;
        if (sheets == null) return null;
        var t = song.T;
        var body = new StackPanel { Margin = new Thickness(20, 4, 20, 18) };
        body.Children.Add(new TextBlock { Text = L.T("Scegli il video"), FontSize = 20, FontWeight = FontWeight.Bold, Margin = new Thickness(4, 0, 4, 4) });
        body.Children.Add(new TextBlock
        {
            Text = $"{t.Title} · {t.DisplayArtist}" + (t.Duration > 0 ? " · " + Text.Duration(t.Duration) : ""),
            Foreground = Ui.Res("SubTextBrush"), FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 4, 10),
        });
        var status = new TextBlock { Text = L.T("Cerco su YouTube…"), Foreground = Ui.Res("MutedBrush"), FontSize = 14, Margin = new Thickness(4, 4, 4, 10), TextWrapping = TextWrapping.Wrap };
        body.Children.Add(status);
        var list = new StackPanel();
        body.Children.Add(list);
        var ok = Wide(L.T("Scarica il video"), "TouchPrimary");
        ok.IsEnabled = false;
        var no = Wide(L.T("Annulla"), "TouchGhost");
        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*"), Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(no);
        Grid.SetColumn(ok, 2);
        buttons.Children.Add(ok);
        body.Children.Add(buttons);

        VideoCandidate? chosen = null, result = null;
        var rows = new List<(VideoCandidate C, Border Row)>();
        bool open = true;

        void Pick(VideoCandidate c)
        {
            chosen = c;
            ok.IsEnabled = true;
            foreach (var (cand, row) in rows)
            {
                bool on = cand == c;
                row.BorderBrush = on ? Ui.Res("AccentBrush") : Brushes.Transparent;
                row.Background = on ? Ui.Res("AccentSoftBrush") : Ui.Res("Surface2Brush");
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
            if (!open) return;
            if (found.Count == 0)
            {
                status.Text = L.T("Nessun video trovato su YouTube per questo brano.");
                return;
            }
            status.Text = L.T("Scegli quello giusto: il video va a tempo con l'audio, quindi deve essere la stessa versione.");
            foreach (var c in found.Take(8))
            {
                var thumb = new CoverImage { Width = 112, Height = 63, CornerRadius = new CornerRadius(8) };
                var frame = new Border { Width = 112, Height = 63, CornerRadius = new CornerRadius(8), Background = Ui.Res("Surface3Brush"), Child = thumb, VerticalAlignment = VerticalAlignment.Top };
                if (c.Thumb != null) _ = LoadThumb(thumb, c.Thumb);
                var info = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock { Text = c.Title, FontWeight = FontWeight.SemiBold, FontSize = 14.5, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
                var meta = string.Join(" · ", new[] { c.Channel, c.Duration is double d ? Text.Duration(d) : null }.Where(s => !string.IsNullOrEmpty(s)));
                info.Children.Add(new TextBlock { Text = meta, Foreground = Ui.Res("SubTextBrush"), FontSize = 13, Margin = new Thickness(0, 3, 0, 0) });
                if (c.LengthDiffers(t.Duration))
                    info.Children.Add(new TextBlock
                    {
                        Text = "⚠ " + L.T("Durata diversa dal brano: potrebbe andare fuori tempo"), Foreground = Ui.Res("WarningBrush"), FontSize = 12.5,
                        Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap,
                    });
                var dock = new DockPanel();
                DockPanel.SetDock(frame, Dock.Left);
                dock.Children.Add(frame);
                dock.Children.Add(info);
                var row = new Border
                {
                    CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1.5), BorderBrush = Brushes.Transparent, Background = Ui.Res("Surface2Brush"),
                    Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 10), Child = dock,
                };
                var cand = c;
                OnTap(row, () => Pick(cand));
                rows.Add((c, row));
                list.Children.Add(row);
            }
            Pick(found[0]);
        }

        static async Task LoadThumb(CoverImage img, string url)
        {
            if (await WebImages.LoadAsync(new[] { url }, 240) is { } src) img.Source = src;
        }

        no.Click += (_, _) => sheets.Close(body);
        ok.Click += (_, _) =>
        {
            result = chosen;
            sheets.Close(body);
        };
        var shown = sheets.Show(new ScrollViewer { Content = body }, maxHeightShare: 0.92);
        Search();
        await shown;
        open = false;
        return result;
    }
}
