using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// Actions on the selected songs; Canc deletes, Invio plays, Esc deselects.
public partial class SelectionBar : UserControl
{
    public static readonly StyledProperty<ListBox?> ListProperty = AvaloniaProperty.Register<SelectionBar, ListBox?>(nameof(List));

    public ListBox? List { get => GetValue(ListProperty); set => SetValue(ListProperty, value); }

    private static readonly List<WeakReference<SelectionBar>> Bars = new();
    private readonly TranslateTransform _shift = new();

    public SelectionBar()
    {
        InitializeComponent();
        RenderTransform = _shift;
        lock (Bars) Bars.Add(new WeakReference<SelectionBar>(this));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ListProperty) Attach(change.OldValue as ListBox, change.NewValue as ListBox);
    }

    private void Attach(ListBox? old, ListBox? list)
    {
        if (old != null)
        {
            old.SelectionChanged -= OnSelection;
            old.RemoveHandler(KeyDownEvent, OnKey);
        }
        if (list == null) return;
        list.SelectionMode = SelectionMode.Multiple;
        list.SelectionChanged += OnSelection;
        list.AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    // A click outside the rows (or outside the list) clears its selection.
    public static void ClearOnOutsideClick(Visual? source)
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
            if (list == null || (list.SelectedItems?.Count ?? 0) == 0 || !list.IsEffectivelyVisible) continue;
            if (bar.IsVisualAncestorOf(source) || bar == source) continue;
            if (list.IsVisualAncestorOf(source) && (source.FindAncestorOfType<ListBoxItem>(true) != null || source.FindAncestorOfType<ScrollBar>(true) != null)) continue;
            // Menus are popups: a click there acts on the selection.
            if (source.FindAncestorOfType<PopupRoot>(true) != null) continue;
            list.UnselectAll();
        }
    }

    // Selected rows in list order.
    public static List<TrackRow> Selected(ListBox list)
    {
        var items = list.Items.Cast<object?>().ToList();
        return (list.SelectedItems?.OfType<TrackRow>() ?? Enumerable.Empty<TrackRow>()).OrderBy(r => items.IndexOf(r)).ToList();
    }

    private List<TrackRow> Rows => List == null ? new List<TrackRow>() : Selected(List);
    private List<TrackViewModel> Tracks => Rows.Select(r => r.Track).ToList();

    private void OnSelection(object? sender, SelectionChangedEventArgs e)
    {
        var rows = Rows;
        bool show = rows.Count >= 2;
        if (show && !IsVisible && Ui.Animations)
        {
            Ui.Tween(this, 160, Ui.Linear, v => Opacity = v);
            Ui.Tween(this, 260, Ui.BackOut, v => _shift.Y = 24 * (1 - v));
        }
        IsVisible = show;
        CountText.Text = L.F("{0} selezionati", rows.Count);
        RemoveButton.IsVisible = rows.FirstOrDefault()?.Owner.Playlist != null;
        // In a room: "add to the room" instead of play and queue.
        var main = rows.FirstOrDefault()?.Track.Main;
        bool room = main?.InRoom == true;
        PlayGlyph.Text = Icons.Map(room ? "" : "");
        PlayLabel.Text = room ? L.T("Aggiungi alla stanza") : L.T("Riproduci");
        ToolTip.SetTip(PlayButton, !room ? L.T("Riproduci i brani selezionati")
            : main!.CanAddToRoom ? L.T("Aggiungi i brani selezionati alla coda della stanza") : main.Together.Denied(Core.Together.Perm.Add));
        PlayButton.IsEnabled = !room || main!.CanAddToRoom;
        QueueButton.IsVisible = !room;
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (List == null || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox) return;
        var rows = Rows;
        switch (e.Key)
        {
            case Key.Delete when rows.Count > 0:
                _ = rows[0].Track.Main.DeleteTracks(rows.Select(r => r.Track).ToList());
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

    private void Play_Click(object? sender, RoutedEventArgs e) => Main?.PlaySelection(Tracks);

    private void Queue_Click(object? sender, RoutedEventArgs e) => Main?.Enqueue(Tracks);

    private void Playlist_Click(object? sender, RoutedEventArgs e)
    {
        var tracks = Tracks;
        if (tracks.Count > 0) Menus.Open(Menus.AddManyToPlaylist(tracks), (Control)sender!, true);
    }

    private void Tag_Click(object? sender, RoutedEventArgs e)
    {
        var tracks = Tracks;
        if (tracks.Count > 0) Menus.Open(Menus.TagMenu(tracks), (Control)sender!, true);
    }

    private void Remove_Click(object? sender, RoutedEventArgs e)
    {
        var rows = Rows;
        if (rows.FirstOrDefault()?.Owner.Playlist is { } p) rows[0].Track.Main.RemoveFromPlaylist(rows.Select(r => r.Track).ToList(), p);
    }

    private void Delete_Click(object? sender, RoutedEventArgs e) => Main?.DeleteTracks(Tracks);

    private void Clear_Click(object? sender, RoutedEventArgs e) => List?.UnselectAll();
}
