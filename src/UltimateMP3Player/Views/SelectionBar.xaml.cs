using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// Actions on the selected songs; Canc deletes, Invio plays, Esc deselects.
public partial class SelectionBar : UserControl
{
    public static readonly DependencyProperty ListProperty = DependencyProperty.Register(nameof(List), typeof(ListBox), typeof(SelectionBar),
        new PropertyMetadata(null, (d, e) => ((SelectionBar)d).Attach(e.OldValue as ListBox, e.NewValue as ListBox)));

    public ListBox? List { get => (ListBox?)GetValue(ListProperty); set => SetValue(ListProperty, value); }

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
            list.UnselectAll();
        }
    }

    // Selected rows in list order.
    public static List<TrackRow> Selected(ListBox list)
        => list.SelectedItems.OfType<TrackRow>().OrderBy(r => list.Items.IndexOf(r)).ToList();

    private List<TrackRow> Rows => List == null ? new List<TrackRow>() : Selected(List);
    private List<TrackViewModel> Tracks => Rows.Select(r => r.Track).ToList();

    private void OnSelection(object sender, SelectionChangedEventArgs e)
    {
        var rows = Rows;
        bool show = rows.Count >= 2;
        if (show && Visibility != Visibility.Visible && Ui.Animations)
        {
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            Shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        }
        Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = L.F("{0} selezionati", rows.Count);
        RemoveButton.Visibility = rows.FirstOrDefault()?.Owner.Playlist != null ? Visibility.Visible : Visibility.Collapsed;
        // In a room: "add to the room" instead of play and queue.
        var main = rows.FirstOrDefault()?.Track.Main;
        bool room = main?.InRoom == true;
        PlayGlyph.Text = room ? "" : "";
        PlayLabel.Text = room ? L.T("Aggiungi alla stanza") : L.T("Riproduci");
        PlayButton.ToolTip = !room ? L.T("Riproduci i brani selezionati")
            : main!.CanAddToRoom ? L.T("Aggiungi i brani selezionati alla coda della stanza") : main.Together.Denied(Core.Together.Perm.Add);
        PlayButton.IsEnabled = !room || main!.CanAddToRoom;
        QueueButton.Visibility = room ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (List == null || Keyboard.FocusedElement is TextBox) return;
        var rows = Rows;
        switch (e.Key)
        {
            case Key.Delete when rows.Count > 0:
                rows[0].Track.Main.DeleteTracks(rows.Select(r => r.Track).ToList());
                e.Handled = true;
                break;
            case Key.Enter when rows.Count > 0:
                if (rows[0].Track.Main.InRoom) rows[0].Track.Main.Together.Add(rows.Select(r => r.Track).ToList());
                else rows[0].PlayCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape when rows.Count > 0:
                List.UnselectAll();
                e.Handled = true;
                break;
        }
    }

    private MainViewModel? Main => Rows.FirstOrDefault()?.Track.Main;

    private void Play_Click(object sender, RoutedEventArgs e) => Main?.PlaySelection(Tracks);

    private void Queue_Click(object sender, RoutedEventArgs e) => Main?.Enqueue(Tracks);

    private void Playlist_Click(object sender, RoutedEventArgs e)
    {
        var tracks = Tracks;
        if (tracks.Count > 0) Menus.Open(Menus.AddManyToPlaylist(tracks), (UIElement)sender, true);
    }

    private void Tag_Click(object sender, RoutedEventArgs e)
    {
        var tracks = Tracks;
        if (tracks.Count > 0) Menus.Open(Menus.TagMenu(tracks), (UIElement)sender, true);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var rows = Rows;
        if (rows.FirstOrDefault()?.Owner.Playlist is { } p) rows[0].Track.Main.RemoveFromPlaylist(rows.Select(r => r.Track).ToList(), p);
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => Main?.DeleteTracks(Tracks);

    private void Clear_Click(object sender, RoutedEventArgs e) => List?.UnselectAll();
}
