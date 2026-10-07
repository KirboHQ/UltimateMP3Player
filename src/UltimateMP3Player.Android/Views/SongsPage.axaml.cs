using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
        TopPanel.IsVisible = !Embedded || FilterBox.IsVisible || List.Picks.IsActive;
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
        Fill();
        if (sv != null && offset.Y > 0) Dispatcher.UIThread.Post(() => sv.Offset = offset, DispatcherPriority.Background);
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

    private void Fill()
    {
        if (_header == null) return;
        var rows = Rows;
        var items = rows.Select(r => new SongItem(r)).ToList();
        List.Picks.SetItems(items, Owner);
        var all = new List<object> { _header };
        all.AddRange(items);
        if (rows.Count == 0)
        {
            bool empty = DataContext switch { PlaylistPageViewModel p => p.IsEmpty, LibraryViewModel l => l.IsEmpty, _ => true };
            if (!empty) all.Add(new ListNote(L.T("Nessun brano trovato"), L.T("Prova con altre parole o togli il filtro dei tag.")));
            else if (DataContext is PlaylistPageViewModel pp) all.Add(new ListNote(L.T("La playlist è vuota"), L.T("Aggiungi brani dal menu ⋮ di un brano, oppure scegli questa playlist quando scarichi un link.")));
            else if (DataContext is LibraryViewModel lib) all.Add(new ListNote(lib.EmptyTitle, lib.IsTagPage || lib.Unsorted ? lib.EmptyText
                : L.T("Condividi un link da un'altra app (YouTube, Spotify…) oppure incollalo nella scheda Download.")));
        }
        _filled = rows.Count;
        List.ItemsSource = all;
    }

    private void SetFilter(string text)
    {
        switch (DataContext)
        {
            case PlaylistPageViewModel p: p.Filter = text; break;
            case LibraryViewModel l: l.Filter = text; break;
        }
    }

    // The title in the bar shows once the big one has scrolled away.
    private void OnScrolled()
    {
        if (Embedded || FilterBox.IsVisible) return;
        var sv = List.FindDescendantOfType<ScrollViewer>();
        double y = sv?.Offset.Y ?? 0;
        BarTitle.Opacity = Math.Clamp((y - 120) / 80, 0, 1);
    }

    private void OnPicking()
    {
        bool on = List.Picks.IsActive;
        SelectBar.IsVisible = on;
        TopBar.IsVisible = !on;
        SelectActions.IsVisible = on;
        ShowTopPanel();
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
        if (DataContext is not LibraryViewModel lib) return;
        var menu = new SheetMenu { Title = L.T("Ordina per") };
        foreach (var s in lib.Sorts)
        {
            var choice = s;
            menu.Add(new SheetEntry(s.Label, "", () => lib.Sort = choice) { Checked = null });
        }
        foreach (var e2 in menu.Items) if (e2.Text == lib.Sort.Label) e2.Glyph = "";
        Menus.Open(menu);
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
