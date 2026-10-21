using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// The actions on what's selected in a list (Ctrl or Shift + click): songs, songs of "next up", downloads or playlists,
// each with their own. Canc deletes (or takes out), Invio plays, Esc deselects. Compact: icons only (a narrow list).
public partial class SelectionBar : UserControl
{
    public static readonly DependencyProperty ListProperty = DependencyProperty.Register(nameof(List), typeof(ListBox), typeof(SelectionBar),
        new PropertyMetadata(null, (d, e) => ((SelectionBar)d).Attach(e.OldValue as ListBox, e.NewValue as ListBox)));

    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(nameof(Compact), typeof(bool), typeof(SelectionBar),
        new PropertyMetadata(false));

    public ListBox? List { get => (ListBox?)GetValue(ListProperty); set => SetValue(ListProperty, value); }
    public bool Compact { get => (bool)GetValue(CompactProperty); set => SetValue(CompactProperty, value); }

    private static readonly List<WeakReference<SelectionBar>> Bars = new();

    public SelectionBar()
    {
        InitializeComponent();
        lock (Bars) Bars.Add(new WeakReference<SelectionBar>(this));
    }

    private void Attach(ListBox? old, ListBox? list)
    {
        if (old != null)
        {
            old.SelectionChanged -= OnSelection;
            old.PreviewKeyDown -= OnKey;
        }
        if (list == null) return;
        list.SelectionMode = SelectionMode.Extended;
        list.SelectionChanged += OnSelection;
        list.PreviewKeyDown += OnKey;
    }

    // A click outside the rows (or outside the list) clears its selection.
    public static void ClearOnOutsideClick(DependencyObject? source)
    {
        if (source == null) return;
        List<SelectionBar> bars;
        lock (Bars)
        {
            Bars.RemoveAll(w => !w.TryGetTarget(out _));
            bars = Bars.Select(w => w.TryGetTarget(out var b) ? b : null).OfType<SelectionBar>().ToList();
        }
        foreach (var bar in bars)
        {
            var list = bar.List;
            if (list == null || list.SelectedItems.Count == 0 || !list.IsVisible) continue;
            if (Ui.IsInside(source, bar)) continue;
            if (Ui.IsInside(source, list) && (Ui.FindAncestor<ListBoxItem>(source) != null || Ui.FindAncestor<ScrollBar>(source) != null)) continue;
            // (a menu opened from the selection keeps it)
            if (Ui.FindAncestor<ContextMenu>(source) != null) continue;
            list.UnselectAll();
        }
    }

    // Selected rows of a song list, in list order.
    public static List<TrackRow> Selected(ListBox list)
        => list.SelectedItems.OfType<TrackRow>().OrderBy(r => list.Items.IndexOf(r)).ToList();

    // Whatever is selected, in list order.
    public static List<object> SelectedItems(ListBox list)
        => list.SelectedItems.Cast<object>().OrderBy(x => list.Items.IndexOf(x)).ToList();

    // The songs among them: rows, cards, songs of "next up".
    public static List<TrackViewModel> SongsOf(IEnumerable<object> items) => items.Select(x => x switch
    {
        TrackRow r => r.Track,
        TrackViewModel t => t,
        QueueRow q => q.Track,
        _ => null,
    }).OfType<TrackViewModel>().Distinct().ToList();

    private List<object> Items => List == null ? new List<object>() : SelectedItems(List);

    private void OnSelection(object sender, SelectionChangedEventArgs e) => Rebuild();

    // The songs of the selection changed state (saved, deleted...): the buttons follow.
    public void Rebuild()
    {
        var items = Items;
        bool show = items.Count >= 2;
        if (show && Visibility != Visibility.Visible && Ui.Animations)
        {
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            Shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        }
        Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Buttons.Children.Clear();
        if (!show) return;
        var lists = items.OfType<PlaylistViewModel>().ToList();
        var jobs = items.OfType<DownloadJobViewModel>().ToList();
        var queue = items.OfType<QueueRow>().ToList();
        if (lists.Count > 0) BuildPlaylists(lists);
        else if (jobs.Count > 0) BuildJobs(jobs);
        else if (queue.Count > 0) BuildQueue(queue);
        else BuildSongs(SongsOf(items), items.OfType<TrackRow>().FirstOrDefault()?.Owner.Playlist);
    }

    private Button Add(string glyph, string text, string tip, Action<Button> click, bool danger = false, bool enabled = true)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), FontSize = 13 });
        if (!Compact) content.Children.Add(new TextBlock { Text = text, Margin = new Thickness(7, 0, 0, 0) });
        var b = new Button
        {
            Style = (Style)FindResource(danger ? "DangerButton" : "SubtleButton"),
            Padding = Compact ? new Thickness(9, 6, 9, 6) : new Thickness(danger ? 12 : 10, 6, danger ? 12 : 10, 6),
            Margin = new Thickness(danger ? 6 : 0, 0, 0, 0),
            Content = content,
            ToolTip = Compact ? text + (tip != text ? " — " + tip : "") : tip,
            IsEnabled = enabled,
        };
        AutomationProperties.SetName(b, text);
        ToolTipService.SetShowOnDisabled(b, true);
        b.Click += (_, _) => click(b);
        Buttons.Children.Add(b);
        return b;
    }

    // ------------------------------------------------------------------ songs

    private void BuildSongs(List<TrackViewModel> songs, Playlist? owner)
    {
        if (songs.Count == 0) return;
        var main = songs[0].Main;
        CountText.Text = L.F("{0} selected", songs.Count);
        if (main.InRoom)
            Add("", L.T("Add to the room"), main.CanAddToRoom ? L.T("Add the selected songs to the room's queue") : main.Together.Denied(Core.Together.Perm.Add),
                _ => main.Together.Add(songs), enabled: main.CanAddToRoom);
        else
        {
            Add("", L.T("Play"), L.T("Play selected songs"), _ => main.PlaySelection(songs));
            Add("", L.T("Queued"), L.T("Add to queue"), _ => main.Enqueue(songs));
        }
        AddSave(main, songs);
        Add("", "Playlist", L.T("Add to a playlist"), b => Menus.Open(Menus.AddManyToPlaylist(songs), b, true));
        Add("", "Tag", L.T("Add or remove a tag on the selected songs"), b => Menus.Open(Menus.TagMenu(songs), b, true));
        if (owner != null)
            Add("", L.T("Remove from playlist"), L.T("Remove from playlist (songs stay in your library)"), _ => main.RemoveFromPlaylist(songs, owner));
        Add("", L.T("Delete"), L.T("Delete… (Del): the video, the lyrics, the audio from the device or everything"), b => _ = main.DeleteTracks(songs), danger: true);
    }

    // The ones in the cloud saved on the device (all, or how many of them).
    private void AddSave(MainViewModel main, List<TrackViewModel> songs)
    {
        var cloud = songs.Where(main.ToSave).ToList();
        if (cloud.Count == 0) return;
        Add("", cloud.Count == songs.Count ? L.T("Save") : L.F("Save ({0})", cloud.Count),
            L.Count(cloud.Count, "Save the song in the cloud on your device", "Save the {0} songs in the cloud on your device"), b =>
            {
                _ = main.SaveToDevice(cloud);
                Rebuild();
            });
    }

    // ------------------------------------------------------------------ songs of "next up"

    private void BuildQueue(List<QueueRow> rows)
    {
        var player = rows[0].Player;
        var main = rows[0].Track.Main;
        var places = rows.Select(r => r.Index).ToList();
        var songs = rows.Select(r => r.Track).Distinct().ToList();
        CountText.Text = L.F("{0} selected", rows.Count);
        Add("", L.T("To top"), L.T("Move to the top of Up next"), _ => { player.MoveUpcomingToTop(places); List?.UnselectAll(); });
        Add("", L.T("To bottom"), L.T("Move to the bottom of Up next"), _ => { player.MoveUpcomingToBottom(places); List?.UnselectAll(); });
        if (!player.InRoom)
        {
            AddSave(main, songs);
            Add("", "Playlist", L.T("Add to a playlist"), b => Menus.Open(Menus.AddManyToPlaylist(songs), b, true));
        }
        Add("", L.T("Remove"), L.T("Remove from Up next (Del)"), _ => { player.RemoveUpcoming(places); List?.UnselectAll(); }, danger: true);
    }

    // ------------------------------------------------------------------ downloads

    private void BuildJobs(List<DownloadJobViewModel> jobs)
    {
        if (App.Host?.Session is not { } main) return;
        var queue = main.Queue;
        CountText.Text = L.Count(jobs.Count, "1 download", "{0} downloads");
        int failed = jobs.Count(j => j.CanRetry), youTube = jobs.Count(j => j.CanRetryYouTube), active = jobs.Count(j => j.IsActive);
        if (failed > 0) Add("", L.F("Retry ({0})", failed), L.T("Retry the failed downloads"), _ => { queue.Retry(jobs); Rebuild(); });
        if (youTube > 0)
            Add("", L.F("On YouTube ({0})", youTube), L.T("Look up the failed songs on YouTube Music and download them from there"), _ => { queue.RetryOnYouTube(jobs); Rebuild(); });
        if (active > 0) Add("", L.F("Cancel ({0})", active), L.T("Stop the running or queued downloads"), _ => { queue.Cancel(jobs); Rebuild(); });
        Add("", L.T("Remove from list"), L.T("Remove from the download list (Del): songs already downloaded stay"), _ => queue.Remove(jobs), danger: true);
    }

    // ------------------------------------------------------------------ playlists

    private void BuildPlaylists(List<PlaylistViewModel> lists)
    {
        if (App.Host?.Session is not { } main) return;
        CountText.Text = L.Count(lists.Count, "1 playlist", "{0} playlists");
        Add("", L.T("Play"), L.T("Play all their songs"), _ => main.PlayPlaylists(lists));
        Add("", L.T("Queued"), L.T("Add all their songs to the queue"), _ => main.EnqueuePlaylists(lists));
        int cloud = main.SongsOf(lists).Count(main.ToSave);
        if (cloud > 0)
            Add("", L.F("Save ({0})", cloud), L.Count(cloud, "Save their song in the cloud on your device", "Save their {0} songs in the cloud on your device"),
                b => { _ = main.SavePlaylists(lists); Rebuild(); });
        Add("", "Tag", L.T("Add or remove a tag on the selected playlists"), b => Menus.Open(Menus.PlaylistsTagMenu(lists), b, true));
        Add("", L.T("Export"), L.T("Export to a .ump file"), _ => main.ExportPlaylists(lists));
        if (lists.Any(p => !p.IsFavorites))
            Add("", L.T("Delete"), L.T("Delete the selected playlists (Del): the songs stay in your library"), b => _ = main.DeletePlaylists(lists), danger: true);
    }

    // ------------------------------------------------------------------ keys

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (List == null || Keyboard.FocusedElement is TextBox) return;
        var items = Items;
        if (items.Count == 0) return;
        var main = App.Host?.Session;
        switch (e.Key)
        {
            case Key.Delete:
                if (items.OfType<PlaylistViewModel>().ToList() is { Count: > 0 } lists) _ = main?.DeletePlaylists(lists);
                else if (items.OfType<DownloadJobViewModel>().ToList() is { Count: > 0 } jobs) main?.Queue.Remove(jobs);
                else if (items.OfType<QueueRow>().ToList() is { Count: > 0 } rows)
                {
                    rows[0].Player.RemoveUpcoming(rows.Select(r => r.Index).ToList());
                    List.UnselectAll();
                }
                else if (SongsOf(items) is { Count: > 0 } songs) _ = songs[0].Main.DeleteTracks(songs);
                else return;
                e.Handled = true;
                break;
            case Key.Enter:
                if (items.OfType<PlaylistViewModel>().ToList() is { Count: > 0 } play) main?.PlayPlaylists(play);
                else if (items.FirstOrDefault() is QueueRow q) q.PlayCommand.Execute(null);
                else if (items.FirstOrDefault() is TrackRow r)
                {
                    if (r.Track.Main.InRoom) r.Track.Main.Together.Add(SongsOf(items));
                    else r.PlayCommand.Execute(null);
                }
                else if (SongsOf(items) is { Count: > 0 } songs) songs[0].Main.PlaySelection(songs);
                else return;
                e.Handled = true;
                break;
            case Key.Escape:
                List.UnselectAll();
                e.Handled = true;
                break;
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => List?.UnselectAll();
}
