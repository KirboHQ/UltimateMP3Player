using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// The first row of a list: the page's header (it scrolls with the songs).
public sealed class ListHeader
{
    public ListHeader(object page) => Page = page;
    public object Page { get; }
}

// A row with a message (no songs, nothing found).
public sealed record ListNote(string Title, string Text);

public partial class SongsPage : UserControl, IPage
{
    public static readonly StyledProperty<bool> EmbeddedProperty = AvaloniaProperty.Register<SongsPage, bool>(nameof(Embedded));

    private INotifyPropertyChanged? _vm;
    private ListHeader? _header;
    private readonly DispatcherTimer _filterTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };

    public SongsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        List.Picks.ActiveChanged += OnPicking;
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            SetFilter(FilterBox.Text ?? "");
        };
        FilterBox.TextChanged += (_, _) =>
        {
            _filterTimer.Stop();
            _filterTimer.Start();
        };
        List.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => OnScrolled());
        List.AddHandler(PointerPressedEvent, DragPressed, RoutingStrategies.Tunnel);
        List.AddHandler(PointerMovedEvent, DragMoved, RoutingStrategies.Tunnel);
        List.AddHandler(PointerReleasedEvent, DragReleased, RoutingStrategies.Tunnel);
        List.PointerCaptureLost += (_, _) => EndDrag(false);
        List.Picks.ReorderRequested += StartReorder;
    }

    // Inside the library's tab "Brani": no title or back of its own (the tab has them).
    public bool Embedded
    {
        get => GetValue(EmbeddedProperty);
        set => SetValue(EmbeddedProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EmbeddedProperty)
        {
            BackButton.IsVisible = !Embedded;
            ShowTopPanel();
        }
    }

    // Inside the Library tab the bar on top is there only while searching in the list or choosing songs.
    private void ShowTopPanel()
    {
        TopPanel.IsVisible = !Embedded || FilterBox.IsVisible || List.Picks.IsActive || _reordering;
        InlineTools = Embedded && !TopPanel.IsVisible;
    }

    // Search and choose next to "12 songs" (inside the Library tab, while the bar on top is away).
    public static readonly StyledProperty<bool> InlineToolsProperty = AvaloniaProperty.Register<SongsPage, bool>(nameof(InlineTools));

    public bool InlineTools
    {
        get => GetValue(InlineToolsProperty);
        set => SetValue(InlineToolsProperty, value);
    }

    private void Attach()
    {
        if (_reordering) StopReorder();
        if (_vm != null) _vm.PropertyChanged -= OnVm;
        _vm = DataContext as INotifyPropertyChanged;
        _header = DataContext != null ? new ListHeader(DataContext) : null;
        if (_vm != null && IsAttachedToVisualTree()) _vm.PropertyChanged += OnVm;
        BarTitle.Text = DataContext switch
        {
            PlaylistPageViewModel p => p.Vm.Name,
            LibraryViewModel l => l.Title,
            _ => "",
        };
        Fill();
    }

    private bool IsAttachedToVisualTree() => this.GetVisualRoot() != null;

    // Listening to its list only while it's in the window: a page let go of doesn't stay alive (and busy) behind it.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_vm == null) return;
        _vm.PropertyChanged -= OnVm;
        _vm.PropertyChanged += OnVm;
        if (_stale) Refill();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_vm != null) _vm.PropertyChanged -= OnVm;
        _stale = true;
        if (_reordering) StopReorder();
    }

    // Shown again (a tab, back): what changed meanwhile.
    public void Shown()
    {
        if (_stale) Refill();
    }

    private bool _fillPending, _stale;

    // The list changed (a download added a song, a filter): once for all the changes together, and only while it's on
    // screen; hidden, it's done when it's shown again.
    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not ("Rows" or "IsEmpty" or "NoMatches")) return;
        if (!IsEffectivelyVisible)
        {
            _stale = true;
            return;
        }
        if (_fillPending) return;
        _fillPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _fillPending = false;
            Refill();
        }, DispatcherPriority.Background);
    }

    // Filled again where it was scrolled to.
    private void Refill()
    {
        _stale = false;
        var sv = List.FindDescendantOfType<ScrollViewer>();
        var offset = sv?.Offset ?? default;
        if (Fill() && sv != null && offset.Y > 0) Dispatcher.UIThread.Post(() => sv.Offset = offset, DispatcherPriority.Background);
    }

    private ITrackList? Owner => DataContext as ITrackList;

    private List<TrackRow> Rows => DataContext switch
    {
        PlaylistPageViewModel p => p.Rows,
        LibraryViewModel l => l.Rows,
        _ => new List<TrackRow>(),
    };

    // The header, then the songs (or why there are none).
    private int _filled = -1;
    // What the list shows now.
    private ObservableCollection<object>? _shown;

    // False when nothing changed (the same songs in the same order: a song just dragged to its new place is already there,
    // a cover or a title changes by itself): the list is left as it is. Filled again, a virtualized list starts over and
    // guesses where it was from estimated heights (the playlist's header is far taller than a row): it jumped to the top
    // or the bottom.
    private bool Fill()
    {
        if (_header == null) return false;
        var rows = Rows;
        var items = rows.Select(r => new SongItem(r)).ToList();
        var all = new List<object> { _header };
        all.AddRange(items);
        if (rows.Count == 0)
        {
            bool empty = DataContext switch { PlaylistPageViewModel p => p.IsEmpty, LibraryViewModel l => l.IsEmpty, _ => true };
            var storage = DataContext switch { PlaylistPageViewModel p => p.Storage, LibraryViewModel l => l.Storage, _ => null };
            if (!empty) all.Add(new ListNote(L.T("Nessun brano trovato"), L.T("Prova con altre parole o togli il filtro dei tag.")));
            // songs there, all hidden by the "on the phone / in the cloud" filter
            else if (storage is { HidesAll: true }) all.Add(new ListNote(storage.EmptyTitle, storage.EmptyText));
            else if (DataContext is PlaylistPageViewModel pp) all.Add(new ListNote(L.T("La playlist è vuota"), L.T("Aggiungi brani dal menu ⋮ di un brano, oppure scegli questa playlist quando scarichi un link.")));
            else if (DataContext is LibraryViewModel lib) all.Add(new ListNote(lib.EmptyTitle, lib.IsTagPage || lib.Unsorted ? lib.EmptyText
                : L.T("Condividi un link da un'altra app (YouTube, Spotify…) oppure incollalo nella scheda Download.")));
        }
        _filled = rows.Count;
        if (_shown != null && List.ItemsSource == _shown && Same(_shown, all))
        {
            List.Picks.SetItems(_shown.OfType<SongItem>().ToList(), Owner);
            return false;
        }
        List.Picks.SetItems(items, Owner);
        _shown = new ObservableCollection<object>(all);
        List.ItemsSource = _shown;
        return true;
    }

    private static bool Same(IList<object> shown, List<object> now)
    {
        if (shown.Count != now.Count) return false;
        for (int i = 0; i < shown.Count; i++)
        {
            bool same = (shown[i], now[i]) switch
            {
                (SongItem a, SongItem b) => a.Track == b.Track && a.Row.Owner == b.Row.Owner,
                var (a, b) => Equals(a, b),
            };
            if (!same) return false;
        }
        return true;
    }

    private void SetFilter(string text)
    {
        switch (DataContext)
        {
            case PlaylistPageViewModel p: p.Filter = text; break;
            case LibraryViewModel l: l.Filter = text; break;
        }
    }

    // The title in the bar shows once the big one has scrolled away (at every frame of a scroll: the list's own scroller,
    // found once).
    private ScrollViewer? _scroller;

    private void OnScrolled()
    {
        if (Embedded || FilterBox.IsVisible) return;
        if (_scroller?.GetVisualRoot() == null) _scroller = List.FindDescendantOfType<ScrollViewer>();
        double y = _scroller?.Offset.Y ?? 0;
        BarTitle.Opacity = Math.Clamp((y - 120) / 80, 0, 1);
    }

    private void OnPicking() => ShowBars();

    // On top: the usual bar, the one of the songs being chosen, or the one of a playlist being arranged.
    private void ShowBars()
    {
        bool picking = List.Picks.IsActive;
        SelectBar.IsVisible = picking;
        ReorderBar.IsVisible = _reordering && !picking;
        TopBar.IsVisible = !picking && !_reordering;
        SelectActions.IsVisible = picking;
        ShowTopPanel();
    }

    // ------------------------------------------------------------------ arranging a playlist ("Sposta")

    // Its own order by hand, like on the computer: every row gets the handle (≡); dragging one, a copy of the song follows
    // the finger and a line says where it lands; near the edges the list scrolls by itself. "Fatto" (or back) ends it.
    private bool _reordering;
    private SongItem? _dragged, _dropOn;
    private bool _dropAfter;
    private double _grabY, _fingerY;
    private Border? _ghost, _line;
    private DispatcherTimer? _edgeScroll;

    private void StartReorder()
    {
        if (DataContext is not PlaylistPageViewModel page) return;
        // (sorted by title, artist…: its own order is the one being arranged, so that's shown)
        if (!page.IsCustomOrder)
        {
            page.UseCustomOrder();
            App.Host.Session?.Toast(L.T("Ordine personalizzato della playlist"));
        }
        _reordering = true;
        List.Reordering = true;
        ShowBars();
    }

    private void StopReorder()
    {
        EndDrag(false);
        _reordering = false;
        List.Reordering = false;
        ShowBars();
    }

    private void ReorderDone_Click(object? sender, RoutedEventArgs e) => StopReorder();

    private static bool OnHandle(Visual v)
    {
        for (var x = v; x != null; x = x.GetVisualParent())
            if (x is Border { Classes: var c } && c.Contains("handle")) return true;
        return false;
    }

    private void DragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_reordering || _dragged != null || e.Source is not Visual v || !OnHandle(v)) return;
        if (v.FindAncestorOfType<ListBoxItem>() is not { DataContext: SongItem song } item) return;
        _dragged = song;
        _fingerY = e.GetPosition(DragLayer).Y;
        _grabY = _fingerY - (item.TranslatePoint(default, DragLayer)?.Y ?? _fingerY);
        song.IsMoving = true;
        _line = new Border { IsVisible = false };
        DragLayer.Children.Add(_line);
        // (over the line, a little see-through so the line shows under it)
        _ghost = Ghost(song, item.Bounds.Width);
        _ghost.Opacity = 0.9;
        DragLayer.Children.Add(_ghost);
        e.Pointer.Capture(List);
        // (the list must not scroll under the finger: the finger moves the song)
        e.PreventGestureRecognition();
        e.Handled = true;
        Haptics.LongPress();
        _edgeScroll ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => EdgeScroll());
        _edgeScroll.Start();
        Follow();
    }

    private void DragMoved(object? sender, PointerEventArgs e)
    {
        if (_dragged == null) return;
        _fingerY = e.GetPosition(DragLayer).Y;
        e.PreventGestureRecognition();
        e.Handled = true;
        Follow();
    }

    private void DragReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragged == null) return;
        e.PreventGestureRecognition();
        e.Handled = true;
        EndDrag(true);
        e.Pointer.Capture(null);
    }

    // The copy under the finger; the line before or after the song row nearest to it.
    private void Follow()
    {
        if (_ghost == null || _line == null) return;
        Canvas.SetTop(_ghost, _fingerY - _grabY);
        SongItem? on = null;
        double best = double.MaxValue, onTop = 0, onHeight = 0;
        foreach (var c in List.GetRealizedContainers())
        {
            if (c.DataContext is not SongItem s || !c.IsVisible || c.TranslatePoint(default, DragLayer) is not { } at) continue;
            double top = at.Y, h = c.Bounds.Height;
            double away = _fingerY < top ? top - _fingerY : _fingerY > top + h ? _fingerY - top - h : 0;
            if (away >= best) continue;
            best = away;
            on = s;
            onTop = top;
            onHeight = h;
        }
        _dropOn = on;
        if (on == null)
        {
            _line.IsVisible = false;
            return;
        }
        _dropAfter = _fingerY > onTop + onHeight / 2;
        // Where it would stay where it is (on itself, or right next to its place): a box over its own place, as on the
        // computer; elsewhere the line between the rows. One shape that glides and turns from one into the other.
        int d = _shown?.IndexOf(_dragged!) ?? -1, t = _shown?.IndexOf(on) ?? -1;
        bool stays = d >= 0 && (t == d || t == d - 1 && _dropAfter || t == d + 1 && !_dropAfter);
        if (stays)
        {
            var own = List.GetRealizedContainers().FirstOrDefault(c => c.DataContext == _dragged && c.IsVisible);
            if (own?.TranslatePoint(default, DragLayer) is not { } at)
            {
                _line.IsVisible = false;
                return;
            }
            Mark(at.Y, own.Bounds.Height, true);
        }
        else Mark((_dropAfter ? onTop + onHeight : onTop) - 1.5, 3, false);
    }

    // The line (accent, between two rows) or the box (outlined, a light fill, over a row); the first time it's put in
    // place at once, then it glides.
    private void Mark(double top, double height, bool box)
    {
        if (_line == null) return;
        var accent = Ui.Res("AccentBrush");
        double w = DragLayer.Bounds.Width;
        bool first = !_line.IsVisible;
        if (first) _line.Transitions = null;
        Canvas.SetLeft(_line, box ? 8 : 16);
        Canvas.SetTop(_line, top);
        _line.Width = Math.Max(0, w - (box ? 16 : 32));
        _line.Height = height;
        _line.CornerRadius = new CornerRadius(box ? 12 : 2);
        _line.BorderThickness = new Thickness(box ? 2 : 0);
        _line.BorderBrush = accent;
        _line.Background = box && accent is ISolidColorBrush s ? new SolidColorBrush(s.Color, 0.18) : accent;
        _line.IsVisible = true;
        if (first)
            _line.Transitions = new Transitions
            {
                new DoubleTransition { Property = Canvas.TopProperty, Duration = TimeSpan.FromMilliseconds(90) },
                new DoubleTransition { Property = Layoutable.HeightProperty, Duration = TimeSpan.FromMilliseconds(90) },
                new DoubleTransition { Property = Canvas.LeftProperty, Duration = TimeSpan.FromMilliseconds(90) },
                new DoubleTransition { Property = Layoutable.WidthProperty, Duration = TimeSpan.FromMilliseconds(90) },
            };
    }

    // Near the top or bottom edge the list scrolls, faster the closer the finger is.
    private void EdgeScroll()
    {
        if (_dragged == null) return;
        if (_scroller?.GetVisualRoot() == null) _scroller = List.FindDescendantOfType<ScrollViewer>();
        if (_scroller is not { } sv) return;
        const double edge = 72;
        double h = DragLayer.Bounds.Height;
        double push = _fingerY < edge ? _fingerY - edge : _fingerY > h - edge ? _fingerY - (h - edge) : 0;
        if (push == 0) return;
        double y = Math.Clamp(sv.Offset.Y + push * 0.35, 0, Math.Max(0, sv.Extent.Height - sv.Viewport.Height));
        if (Math.Abs(y - sv.Offset.Y) < 0.1) return;
        sv.Offset = new Vector(sv.Offset.X, y);
        Follow();
    }

    // The song lands where the line is (in the playlist's own order: next to the song it was dropped on).
    private void EndDrag(bool drop)
    {
        if (_dragged is not { } song) return;
        _dragged = null;
        _edgeScroll?.Stop();
        song.IsMoving = false;
        DragLayer.Children.Clear();
        _ghost = _line = null;
        List.SelectedItem = null;
        if (!drop || _dropOn is not { } on || on == song || DataContext is not PlaylistPageViewModel page || App.Host.Session is not { } main) return;
        if (!page.IsCustomOrder) page.UseCustomOrder();
        var p = page.Vm.P;
        int from = p.Tracks.IndexOf(song.Track.Id), at = p.Tracks.IndexOf(on.Track.Id);
        if (from < 0 || at < 0) return;
        int to = Math.Clamp(_dropAfter ? (at > from ? at : at + 1) : (at > from ? at - 1 : at), 0, p.Tracks.Count - 1);
        if (to == from) return;
        // The row goes to its new place in the list on screen right away (nothing scrolls); the playlist then changes the
        // same way, so the list isn't filled again (Fill: same order).
        int d = _shown?.IndexOf(song) ?? -1, t = _shown?.IndexOf(on) ?? -1;
        if (d >= 0 && t >= 0)
        {
            if (t > d) t--;
            _shown!.Move(d, _dropAfter ? t + 1 : t);
        }
        main.Profile.MoveTrack(p, from, to);
    }

    // The copy of the row in the finger: cover, title and artist, the handle, on a lifted card.
    private static Border Ghost(SongItem song, double width)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("52,*,44") };
        grid.Children.Add(new ContentControl { Width = 52, Height = 52, Content = song.Track, ContentTemplate = Ui.Find<IDataTemplate>("TrackCoverSmall") });
        var text = new StackPanel { Margin = new Thickness(14, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = song.Track.Title, FontSize = 15.5, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = song.Track.Artist, FontSize = 13.5, Foreground = Ui.Res("SubTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0),
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var bars = new StackPanel { Width = 18, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        for (int i = 0; i < 3; i++) bars.Children.Add(new Border { Height = 2, CornerRadius = new CornerRadius(1), Background = Ui.Res("AccentTextBrush") });
        Grid.SetColumn(bars, 2);
        grid.Children.Add(bars);
        var card = new Border
        {
            Width = Math.Max(0, width - 16), Padding = new Thickness(8, 7, 4, 7), CornerRadius = new CornerRadius(14), Child = grid,
            Background = Ui.Res("Surface2Brush"), BorderBrush = Ui.Res("AccentBrush"), BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 6 16 0 #80000000"),
        };
        Canvas.SetLeft(card, 8);
        return card;
    }

    // ------------------------------------------------------------------ the bar

    private void Back_Click(object? sender, RoutedEventArgs e) => App.Host.Session?.BackCommand.Execute(null);

    private void Search_Click(object? sender, RoutedEventArgs e)
    {
        bool open = !FilterBox.IsVisible;
        FilterBox.IsVisible = open;
        BarTitle.IsVisible = !open;
        ShowTopPanel();
        SearchButton.Content = Icons.Map(open ? "" : "");
        if (open) Dispatcher.UIThread.Post(() => FilterBox.Focus(), DispatcherPriority.Background);
        else
        {
            FilterBox.Text = "";
            SetFilter("");
        }
    }

    private void Select_Click(object? sender, RoutedEventArgs e)
    {
        if (List.Picks.Items.Count > 0) List.Picks.Start(null);
    }

    private void Sort_Click(object? sender, RoutedEventArgs e)
    {
        // (a playlist: its own order stays saved while it's sorted another way)
        var (sorts, current, pick) = DataContext switch
        {
            LibraryViewModel lib => (lib.Sorts, lib.Sort, (Action<Choice>)(c => lib.Sort = c)),
            PlaylistPageViewModel p => (p.Sorts, p.Sort, c => p.Sort = c),
            _ => ((List<Choice>?)null, (Choice?)null, (Action<Choice>?)null),
        };
        if (sorts == null || current == null || pick == null) return;
        var menu = new SheetMenu { Title = L.T("Ordina per") };
        foreach (var s in sorts)
        {
            var choice = s;
            menu.Add(new SheetEntry(s.Label, "", () => pick(choice)) { Checked = null });
        }
        foreach (var e2 in menu.Items) if (e2.Text == current.Label) e2.Glyph = "";
        Menus.Open(menu);
    }

    private void StorageFilter_Click(object? sender, RoutedEventArgs e)
    {
        var filter = DataContext switch { LibraryViewModel l => l.Storage, PlaylistPageViewModel p => p.Storage, _ => null };
        if (filter != null) Menus.Open(Menus.StorageFilterMenu(filter));
    }

    private void TagFilter_Click(object? sender, RoutedEventArgs e)
    {
        var filter = DataContext switch { LibraryViewModel l => l.TagFilter, PlaylistPageViewModel p => p.TagFilter, _ => null };
        if (filter != null && App.Host.Session is { } main) Menus.Open(Menus.TagFilterMenu(filter, main));
    }

    private void PlaylistMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PlaylistPageViewModel p) Menus.Open(Menus.ForPlaylist(p.Vm));
    }

    private void PlaylistTags_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PlaylistPageViewModel p) Menus.Open(Menus.PlaylistTagMenu(p.Vm));
    }

    // ------------------------------------------------------------------ IPage

    public bool Back()
    {
        if (_reordering)
        {
            StopReorder();
            return true;
        }
        if (List.Picks.IsActive)
        {
            List.Picks.Stop();
            return true;
        }
        if (FilterBox.IsVisible)
        {
            Search_Click(null, new RoutedEventArgs());
            return true;
        }
        return false;
    }

    public void ScrollToTop()
    {
        if (List.FindDescendantOfType<ScrollViewer>() is { } sv) sv.Offset = default;
    }
}
