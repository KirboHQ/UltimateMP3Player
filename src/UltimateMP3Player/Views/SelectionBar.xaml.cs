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
        CountText.Text = L.F("{0} selezionati", songs.Count);
        if (main.InRoom)
            Add("", L.T("Aggiungi alla stanza"), main.CanAddToRoom ? L.T("Aggiungi i brani selezionati alla coda della stanza") : main.Together.Denied(Core.Together.Perm.Add),
                _ => main.Together.Add(songs), enabled: main.CanAddToRoom);
        else
        {
            Add("", L.T("Riproduci"), L.T("Riproduci i brani selezionati"), _ => main.PlaySelection(songs));
            Add("", L.T("In coda"), L.T("Aggiungi alla coda"), _ => main.Enqueue(songs));
        }
        AddSave(main, songs);
        Add("", "Playlist", L.T("Aggiungi a una playlist"), b => Menus.Open(Menus.AddManyToPlaylist(songs), b, true));
        Add("", "Tag", L.T("Metti o togli un tag ai brani selezionati"), b => Menus.Open(Menus.TagMenu(songs), b, true));
        if (owner != null)
            Add("", L.T("Togli dalla playlist"), L.T("Togli dalla playlist (i brani restano nella libreria)"), _ => main.RemoveFromPlaylist(songs, owner));
        Add("", L.T("Elimina"), L.T("Elimina… (Canc): il video, il testo, l'audio dal dispositivo o tutto"), b => _ = main.DeleteTracks(songs), danger: true);
    }

    // The ones in the cloud saved on the device (all, or how many of them).
    private void AddSave(MainViewModel main, List<TrackViewModel> songs)
    {
        var cloud = songs.Where(t => t.IsCloud && !t.IsSaving).ToList();
        if (cloud.Count == 0) return;
        Add("", cloud.Count == songs.Count ? L.T("Salva") : L.F("Salva ({0})", cloud.Count),
            L.Count(cloud.Count, "Salva sul dispositivo il brano nel cloud", "Salva sul dispositivo i {0} brani nel cloud"), b =>
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
        CountText.Text = L.F("{0} selezionati", rows.Count);
        Add("", L.T("In cima"), L.T("Sposta in cima ai successivi"), _ => { player.MoveUpcomingToTop(places); List?.UnselectAll(); });
        Add("", L.T("In fondo"), L.T("Sposta in fondo ai successivi"), _ => { player.MoveUpcomingToBottom(places); List?.UnselectAll(); });
        if (!player.InRoom)
        {
            AddSave(main, songs);
            Add("", "Playlist", L.T("Aggiungi a una playlist"), b => Menus.Open(Menus.AddManyToPlaylist(songs), b, true));
        }
        Add("", L.T("Togli"), L.T("Togli dai successivi (Canc)"), _ => { player.RemoveUpcoming(places); List?.UnselectAll(); }, danger: true);
    }

    // ------------------------------------------------------------------ downloads

    private void BuildJobs(List<DownloadJobViewModel> jobs)
    {
        if (App.Host?.Session is not { } main) return;
        var queue = main.Queue;
        CountText.Text = L.Count(jobs.Count, "1 download", "{0} download");
        int failed = jobs.Count(j => j.CanRetry), youTube = jobs.Count(j => j.CanRetryYouTube), active = jobs.Count(j => j.IsActive);
        if (failed > 0) Add("", L.F("Riprova ({0})", failed), L.T("Riprova i download non riusciti"), _ => { queue.Retry(jobs); Rebuild(); });
        if (youTube > 0)
            Add("", L.F("Su YouTube ({0})", youTube), L.T("Cerca su YouTube Music i brani non riusciti e scaricali da lì"), _ => { queue.RetryOnYouTube(jobs); Rebuild(); });
        if (active > 0) Add("", L.F("Annulla ({0})", active), L.T("Ferma i download in corso o in coda"), _ => { queue.Cancel(jobs); Rebuild(); });
        Add("", L.T("Togli dalla lista"), L.T("Togli dalla lista dei download (Canc): i brani già scaricati restano"), _ => queue.Remove(jobs), danger: true);
    }

    // ------------------------------------------------------------------ playlists

    private void BuildPlaylists(List<PlaylistViewModel> lists)
    {
        if (App.Host?.Session is not { } main) return;
        CountText.Text = L.Count(lists.Count, "1 playlist", "{0} playlist");
        Add("", L.T("Riproduci"), L.T("Riproduci tutti i loro brani"), _ => main.PlayPlaylists(lists));
        Add("", L.T("In coda"), L.T("Aggiungi tutti i loro brani alla coda"), _ => main.EnqueuePlaylists(lists));
        int cloud = main.SongsOf(lists).Count(t => t.IsCloud && !t.IsSaving);
        if (cloud > 0)
            Add("", L.F("Salva ({0})", cloud), L.Count(cloud, "Salva sul dispositivo il loro brano nel cloud", "Salva sul dispositivo i loro {0} brani nel cloud"),
                b => { _ = main.SavePlaylists(lists); Rebuild(); });
        Add("", "Tag", L.T("Metti o togli un tag alle playlist selezionate"), b => Menus.Open(Menus.PlaylistsTagMenu(lists), b, true));
        Add("", L.T("Esporta"), L.T("Esporta in un file .ump"), _ => main.ExportPlaylists(lists));
        if (lists.Any(p => !p.IsFavorites))
            Add("", L.T("Elimina"), L.T("Elimina le playlist selezionate (Canc): i brani restano nella libreria"), b => _ = main.DeletePlaylists(lists), danger: true);
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
