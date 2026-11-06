using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using UltimateMP3Player.Core;
using Avalonia.Interactivity;

namespace UltimateMP3Player.Views;

public partial class LibraryPage : UserControl, IPage
{
    private LibraryHub? _hub;
    private bool _syncing;
    private bool _artistsShown;

    // Choosing several playlists.
    public Selection ListPicks { get; } = new();

    // The panes in the order of their chips, left to right (the tabs' numbers are older), and each tab's place there.
    private readonly Control[] _panes;
    private static readonly int[] PlaceOfTab = { 0, 1, 4, 2, 3 };
    private int _tabShown = 1;

    public LibraryPage()
    {
        InitializeComponent();
        _panes = new Control[] { Songs, ListsPane, ArtistsPane, RadioPane, TagsPane };
        foreach (var pane in _panes) Pane.SetShown(pane, pane.IsVisible);
        Selection.SetOwner(PlaylistsList, ListPicks);
        DataContextChanged += (_, _) => Attach();
        TabSongs.IsCheckedChanged += (_, _) => Pick(0, TabSongs);
        TabLists.IsCheckedChanged += (_, _) => Pick(1, TabLists);
        TabTags.IsCheckedChanged += (_, _) => Pick(2, TabTags);
        TabArtists.IsCheckedChanged += (_, _) => Pick(3, TabArtists);
        TabRadio.IsCheckedChanged += (_, _) => Pick(4, TabRadio);
        RadioFind.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter || _hub == null) return;
            _hub.Main.Stations.SearchCommand.Execute(null);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        ArtistFind.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter || _hub == null) return;
            _hub.Main.Artists.FindCommand.Execute(null);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    private void Attach()
    {
        if (_hub != null) _hub.PropertyChanged -= OnHub;
        _hub = DataContext as LibraryHub;
        if (_hub == null) return;
        _hub.PropertyChanged += OnHub;
        Songs.DataContext = _hub.Main.LibraryPage;
        var lists = _hub.Main.Playlists;
        lists.CollectionChanged -= OnPlaylists;
        lists.CollectionChanged += OnPlaylists;
        ListPicks.SetItems(lists);
        Show();
    }

    private void OnPlaylists(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_hub != null) ListPicks.SetItems(_hub.Main.Playlists);
    }

    private void OnHub(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryHub.Tab)) Show();
    }

    private void Pick(int tab, RadioButton b)
    {
        if (_syncing || b.IsChecked != true || _hub == null) return;
        _hub.Tab = tab;
    }

    private void Show()
    {
        int tab = _hub?.Tab ?? 1;
        // (leaving the artists: their new releases have been seen)
        if (_artistsShown && tab != 3) _hub?.Main.Profile.SeeReleases();
        if (!_artistsShown && tab == 3) _hub?.Main.Artists.Refresh();
        _artistsShown = tab == 3;
        // (the radios' first list comes when they're first shown)
        if (tab == 4) _hub?.Main.Stations.Opened();
        _syncing = true;
        TabSongs.IsChecked = tab == 0;
        TabLists.IsChecked = tab == 1;
        TabTags.IsChecked = tab == 2;
        TabArtists.IsChecked = tab == 3;
        TabRadio.IsChecked = tab == 4;
        _syncing = false;
        Pane.Between(_panes, PlaceOfTab[_tabShown], PlaceOfTab[tab]);
        _tabShown = tab;
        if (tab != 1) ListPicks.Stop();
        if (tab == 0)
        {
            _hub?.Main.LibraryPage.EnsureFresh();
            Songs.Shown();
        }
    }

    public void Shown()
    {
        if (Pane.GetShown(Songs)) Songs.Shown();
    }

    // "+": a playlist, a tag, or songs from the phone.
    private void Add_Click(object? sender, RoutedEventArgs e)
    {
        if (_hub?.Main is not { } main) return;
        var menu = new SheetMenu { Title = L.T("Add") };
        menu.Add(new(L.T("New playlist…"), "", () => _ = main.NewPlaylist(null)));
        menu.Add(new(L.T("New tag…"), "", () => _ = main.NewTag()));
        menu.Line();
        menu.Add(new(L.T("Songs from your phone…"), "", () => _ = main.ImportDialog(false)));
        menu.Add(new(L.T("A music folder…"), "", () => _ = main.ImportDialog(true)));
        menu.Add(new(L.T("Import a .ump pack…"), "", () => _ = main.PickPack()));
        Menus.Open(menu);
    }

    private void Unsorted_Tapped(object? sender, TappedEventArgs e)
    {
        if (_hub?.Main is { } main) main.Navigate(main.UnsortedPage);
    }

    public bool Back()
    {
        if (ListPicks.IsActive)
        {
            ListPicks.Stop();
            return true;
        }
        return Pane.GetShown(Songs) && Songs.Back();
    }

    public void ScrollToTop()
    {
        if (Pane.GetShown(Songs)) Songs.ScrollToTop();
        ListsPane.Offset = default;
        TagsPane.Offset = default;
        ArtistsPane.Offset = default;
        RadioPane.Offset = default;
    }
}
