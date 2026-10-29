using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

public sealed class TrackListSource : ITrackList
{
    public TrackListSource(string id, string name, IReadOnlyList<TrackViewModel> tracks)
    {
        ContextId = id;
        ContextName = name;
        PlayOrder = tracks;
    }

    public string ContextId { get; }
    public string ContextName { get; }
    public IReadOnlyList<TrackViewModel> PlayOrder { get; }
    public Playlist? Playlist => null;
}

// Filters rows (text and tags) but keeps their numbers; playing still uses the whole list.
public static class TrackFilter
{
    public static List<TrackRow> Apply(List<TrackViewModel> order, string filter, TagFilter tags, ITrackList owner)
    {
        var query = new TrackQuery(filter);
        return order.Select((t, i) => new TrackRow(i + 1, t, owner))
            .Where(r => query.Matches(r.Track) && tags.Matches(r.Track))
            .ToList();
    }
}

// The orders of the song lists (a playlist's own order is "custom": left as it is).
public static class TrackSort
{
    public static IEnumerable<Track> Apply(IEnumerable<Track> all, string key) => key switch
    {
        "custom" => all,
        "title" => all.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
        // (no artist or album: at the end)
        "artist" => all.OrderBy(t => t.Artist ?? "￿", StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Album).ThenBy(t => t.Title),
        "album" => all.OrderBy(t => t.Album ?? "￿", StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Title),
        "duration" => all.OrderBy(t => t.Duration),
        _ => all.OrderByDescending(t => t.Added),
    };
}

// The "on the device / in the cloud" filter of a page of songs (the library pages, a playlist): all the songs, only the ones
// saved on the device, only the ones in the cloud. One choice for every page of the profile, and it stays; a page plays the
// songs it shows. The counts are the page's songs before the filter.
public sealed class StorageFilterViewModel : Observable
{
    private readonly MainViewModel _main;

    public StorageFilterViewModel(MainViewModel main) => _main = main;

    public StorageFilter Value
    {
        get => _main.Profile.Data.StorageFilter;
        set
        {
            if (_main.Profile.Data.StorageFilter == value) return;
            _main.Profile.Data.StorageFilter = value;
            _main.Profile.Save();
            _main.OnStorageFilterChanged();
        }
    }

    public int SavedCount { get; private set; }
    public int CloudCount { get; private set; }
    public int Total => SavedCount + CloudCount;
    public bool IsActive => Value != StorageFilter.All;
    // Only where it changes something: the page has songs in the cloud (or it's filtering already).
    public bool IsVisible => CloudCount > 0 || IsActive;
    public int HiddenCount => Value switch { StorageFilter.Device => CloudCount, StorageFilter.Cloud => SavedCount, _ => 0 };
    // The page has songs, and the filter hides all of them.
    public bool HidesAll => Total > 0 && HiddenCount == Total;

    // The pill above the list: what it shows now.
    public string Label => Value switch
    {
        StorageFilter.Device => L.T("On device"),
        StorageFilter.Cloud => L.T("Cloud only"),
        _ => L.T("Device and cloud"),
    };
    public string Glyph => GlyphOf(Value);
    public string Tip => L.T("Show all songs, only the ones saved on your device or only the ones in the cloud");
    // After the count of the songs: the ones it hides.
    public string HiddenText => HiddenCount == 0 ? ""
        : Value == StorageFilter.Device ? L.F("{0} in the cloud hidden", HiddenCount) : L.F("{0} on the device hidden", HiddenCount);

    // The choices of its menu: books (all the songs, like "All songs"), the download arrow (on the device), the cloud.
    public static readonly StorageFilter[] Choices = { StorageFilter.All, StorageFilter.Device, StorageFilter.Cloud };
    public static string GlyphOf(StorageFilter f) => f switch { StorageFilter.Device => "", StorageFilter.Cloud => "", _ => "" };
    public string NameOf(StorageFilter f) => f switch
    {
        StorageFilter.Device => L.F("On device · {0}", SavedCount),
        StorageFilter.Cloud => L.F("Cloud only · {0}", CloudCount),
        _ => L.F("All songs · {0}", Total),
    };

    // An empty page because of it: what to say.
    public string EmptyTitle => Value == StorageFilter.Device ? L.T("No songs saved on your device") : L.T("No songs in the cloud");
    public string EmptyText => Value == StorageFilter.Device
        ? L.T("They're all in the cloud here: pick “Device and cloud” in the filter to see them.")
        : L.T("They're all saved on your device here: pick “Device and cloud” in the filter to see them.");

    // Counts the page's songs and keeps the ones to show.
    public List<T> Apply<T>(IReadOnlyCollection<T> all, Func<T, bool> isSaved)
    {
        SavedCount = all.Count(isSaved);
        CloudCount = all.Count - SavedCount;
        var shown = Value switch
        {
            StorageFilter.Device => all.Where(isSaved).ToList(),
            StorageFilter.Cloud => all.Where(t => !isSaved(t)).ToList(),
            _ => all.ToList(),
        };
        OnChanged(nameof(Value), nameof(SavedCount), nameof(CloudCount), nameof(Total), nameof(IsActive), nameof(IsVisible), nameof(HiddenCount),
            nameof(HidesAll), nameof(Label), nameof(Glyph), nameof(HiddenText), nameof(EmptyTitle), nameof(EmptyText));
        return shown;
    }
}

public sealed class HomeViewModel : Observable
{
    private readonly MainViewModel _main;

    // (the followed artists' new releases: a row of them)
    public ArtistsViewModel Artists => _main.Artists;
    // (the saved radios: a row of them)
    public StationsViewModel Stations => _main.Stations;

    public HomeViewModel(MainViewModel main)
    {
        _main = main;
        OpenRecapCommand = new RelayCommand(() => _main.OpenRecap(RecapYear, 0));
        ImportSpotifyCommand = new RelayCommand(() => _ = SpotifyImport.RunAsync(_main));
        ImportLastFmCommand = new RelayCommand(() => _ = LastFmImport.RunAsync(_main));
        Refresh();
    }

    public string Greeting
    {
        get
        {
            int h = DateTime.Now.Hour;
            var g = h is >= 5 and < 13 ? "Good morning" : h is >= 13 and < 18 ? "Good afternoon" : "Good evening";
            return $"{L.T(g)}, {_main.ProfileName}";
        }
    }

    public List<TrackViewModel> Recent { get; private set; } = new();
    public List<TrackRow> RecentlyAdded { get; private set; } = new();
    // The playlists, then the "New playlist" card, in the same grid.
    public List<object> PlaylistCards => [.. _main.Playlists, NewPlaylistCard.Instance];
    public bool HasRecent => Recent.Count > 0;
    public bool HasRecentlyAdded => RecentlyAdded.Count > 0;
    // For you: the mixes (Mixes.Build).
    public List<MixViewModel> ForYou { get; private set; } = new();
    private DateTime _forYouAt;
    private int _forYouSongs;
    public bool HasForYou => ForYou.Count > 0;
    // December to mid January: the year's recap, a card at the top.
    public int RecapYear { get; private set; }
    public bool HasRecap => RecapYear > 0;
    public string RecapTitle => L.F("Your {0} in music", RecapYear);
    public ICommand OpenRecapCommand { get; }
    // (the empty Home: coming from Spotify or Last.fm)
    public ICommand ImportSpotifyCommand { get; }
    public ICommand ImportLastFmCommand { get; }
    public bool IsEmpty { get; private set; }
    public TrackListSource RecentList { get; private set; } = new("recent", "", new List<TrackViewModel>());

    // Songs in "Recently played".
    public const int RecentCount = 12;

    // A song of the library (or of the connected device), or one heard without saving it (suggested, from a link, in a room:
    // RadioViewModel keeps them).
    private Track? Heard(string id) => _main.Get(id) ?? _main.Radio.Track(id);

    // (connected: the songs heard on both devices whose music is shown, the newest first)
    public void Refresh()
    {
        var profiles = _main.ShownProfiles().ToList();
        var ids = profiles.Count == 1 ? profiles[0].RecentTracks(RecentCount, id => Heard(id) != null)
            : profiles.SelectMany(p => p.HistorySnapshot(200)).OrderByDescending(h => h.At).Select(h => h.TrackId).Distinct()
                .Where(id => Heard(id) != null).Take(RecentCount).ToList();
        Recent = ids.Select(id => _main.Vm(Heard(id)!)).ToList();
        RecentList = new TrackListSource("recent", L.T("Recently played"), Recent);
        var songs = _main.Songs();
        IsEmpty = songs.Count == 0;
        var added = songs.OrderByDescending(t => t.Added).Take(8).Select(_main.Vm).ToList();
        var addedList = new TrackListSource("added", L.T("Recently added"), added);
        RecentlyAdded = added.Select((t, i) => new TrackRow(i + 1, t, addedList)).ToList();
        var now = DateTime.Now;
        int year = now.Month == 12 ? now.Year : now.Month == 1 && now.Day <= 15 ? now.Year - 1 : 0;
        RecapYear = year > 0 && _main.Profile.Log.Years().Contains(year) ? year : 0;
        // (made again now and then, not at every song played: the cards would change under the pointer)
        if (IsEmpty) ForYou = new();
        else if (ForYou.Count == 0 || songs.Count != _forYouSongs || DateTime.Now - _forYouAt > TimeSpan.FromMinutes(30))
        {
            ForYou = Mixes.Build(_main);
            _forYouAt = DateTime.Now;
            _forYouSongs = songs.Count;
        }
        OnChanged(nameof(Greeting), nameof(Recent), nameof(RecentlyAdded), nameof(PlaylistCards), nameof(HasRecent), nameof(HasRecentlyAdded),
            nameof(IsEmpty), nameof(RecentList), nameof(ForYou), nameof(HasForYou), nameof(RecapYear), nameof(HasRecap), nameof(RecapTitle));
    }

    public void PlayRecent(TrackViewModel t) => _main.Player.PlayFrom(RecentList, t);
}

public sealed class NewPlaylistCard
{
    public static readonly NewPlaylistCard Instance = new();
}

// Where a page sits among its neighbours (the sidebar's entries, a page's tabs): the page host slides the new one in from that side.
public interface IPageOrder
{
    int? OrderOf(object page);
}

// The computers' Library: its parts as tabs over them (All songs, Not in a playlist, Playlists, Artists, Radio). The tabs stay,
// the part under them changes sideways, in the tabs' order. Each part is still a page of its own (the history goes through them).
public sealed class LibraryHubViewModel : Observable, IPageOrder
{
    private readonly MainViewModel _main;

    public LibraryHubViewModel(MainViewModel main)
    {
        _main = main;
        _current = main.LibraryPage;
        AllCommand = new RelayCommand(() => _main.IsLibrary = true);
        UnsortedCommand = new RelayCommand(() => _main.IsUnsorted = true);
        PlaylistsCommand = new RelayCommand(() => _main.Navigate(_main.PlaylistsPage));
        ArtistsCommand = new RelayCommand(_main.OpenArtists);
        RadioCommand = new RelayCommand(_main.OpenStations);
    }

    public MainViewModel Main => _main;

    // The part shown (the last one seen while another page is on screen: the sidebar's Library opens it again).
    private object _current;
    public object Current
    {
        get => _current;
        set
        {
            if (Set(ref _current, value)) OnChanged(nameof(IsAll), nameof(IsUnsorted), nameof(IsPlaylists), nameof(IsArtists), nameof(IsRadio));
        }
    }

    public bool Has(object? page) => page == _main.LibraryPage || page == _main.UnsortedPage || page is PlaylistsPageViewModel or ArtistsViewModel or StationsViewModel;

    public int? OrderOf(object page) => page == _main.LibraryPage ? 0 : page == _main.UnsortedPage ? 1
        : page is PlaylistsPageViewModel ? 2 : page is ArtistsViewModel ? 3 : page is StationsViewModel ? 4 : null;

    public bool IsAll => _current == _main.LibraryPage;
    public bool IsUnsorted => _current == _main.UnsortedPage;
    public bool IsPlaylists => _current is PlaylistsPageViewModel;
    public bool IsArtists => _current is ArtistsViewModel;
    public bool IsRadio => _current is StationsViewModel;

    public ICommand AllCommand { get; }
    public ICommand UnsortedCommand { get; }
    public ICommand PlaylistsCommand { get; }
    public ICommand ArtistsCommand { get; }
    public ICommand RadioCommand { get; }

    // (Ctrl+←/→)
    public void Step(int dir)
    {
        var tabs = new[] { AllCommand, UnsortedCommand, PlaylistsCommand, ArtistsCommand, RadioCommand };
        if (OrderOf(_current) is int i && i + dir is var next and >= 0 and < 5) tabs[next].Execute(null);
    }
}

// Library › Playlists (computers): every playlist as a card, then the "New playlist" card.
public sealed class PlaylistsPageViewModel : Observable
{
    public PlaylistsPageViewModel(MainViewModel main) => Main = main;

    public MainViewModel Main { get; }
    public List<object> Cards => [.. Main.Playlists, NewPlaylistCard.Instance];
    public string CountText => L.Count(Main.Playlists.Count, "1 playlist", "{0} playlists");

    // (the playlists rebuilt: MainViewModel.RebuildPlaylists)
    public void Refresh() => OnChanged(nameof(Cards), nameof(CountText));
}

// "All songs"; with unsorted = the songs in no playlist; with a tag = the songs with that tag.
public sealed class LibraryViewModel : Observable, ITrackList
{
    private readonly MainViewModel _main;
    private List<TrackViewModel> _order = new();
    private bool _dirty;

    public LibraryViewModel(MainViewModel main, bool unsorted, TagViewModel? tag = null)
    {
        _main = main;
        Unsorted = unsorted;
        Tag = tag;
        TagFilter.Changed += ApplyFilter;
        Sorts = new List<Choice>
        {
            new(L.T("Recently added"), "added"), new(L.T("Title"), "title"), new(L.T("Artist"), "artist"),
            new(L.T("Album"), "album"), new(L.T("Duration"), "duration"),
        };
        _sort = Sorts.FirstOrDefault(s => (string)s.Value! == main.Profile.Data.LibrarySort) ?? Sorts[0];
        PlayCommand = new RelayCommand(() => _main.Player.PlayAll(this, false), () => _order.Count > 0);
        ShuffleCommand = new RelayCommand(() => _main.Player.PlayAll(this, true), () => _order.Count > 0);
        SaveAllCommand = new RelayCommand(() => _ = _main.SaveToDevice(_order.Where(t => t.IsCloud).ToList()), () => _order.Any(t => t.IsCloud));
        Storage = new StorageFilterViewModel(main);
        if (unsorted) _dirty = true;
        else Rebuild();
    }

    // Only the songs on the device, or only the ones in the cloud: the same for every page of the profile.
    public StorageFilterViewModel Storage { get; }
    // Every song of the page in the cloud saved on the device.
    public ICommand SaveAllCommand { get; }
    // Songs of the page in the cloud (all of them, also when hidden).
    public int CloudCount => Storage.CloudCount;
    public bool HasCloud => CloudCount > 0;
    // Next to the count of the songs, while some are in the cloud (and shown): saving them all.
    public bool ShowSaveAll => HasCloud && Storage.Value != StorageFilter.Device;
    public string SaveAllLabel => L.Count(CloudCount, "Save the one in the cloud", "Save all those in the cloud");

    public bool Unsorted { get; }
    public TagViewModel? Tag { get; }
    public bool IsTagPage => Tag != null;
    // The library's two tabs (the computers' sidebar has one entry for both): all the songs, the ones in no playlist.
    public bool ShowTabs => Tag == null;
    public bool IsAll => !Unsorted;
    public ICommand AllCommand => _all ??= new RelayCommand(() => _main.IsLibrary = true);
    public ICommand UnsortedCommand => _unsorted ??= new RelayCommand(() => _main.IsUnsorted = true);
    private ICommand? _all, _unsorted;
    // "Add files" only makes sense for the whole library.
    public bool CanImport => !Unsorted && Tag == null;
    public ICommand PlayCommand { get; }
    public ICommand ShuffleCommand { get; }

    public string ContextId => Tag != null ? "tag:" + Tag.Id : Unsorted ? "unsorted" : "library";
    public string ContextName => Tag?.Name ?? L.T(Unsorted ? "Not in a playlist" : "All songs");
    public IReadOnlyList<TrackViewModel> PlayOrder => _order;
    public Playlist? Playlist => null;

    public string Title => ContextName;
    public string Kicker => Tag != null ? L.T("TAGS") : L.T(Unsorted ? "TO SORT" : "LIBRARY");
    public string Glyph => Tag != null ? "" : Unsorted ? "" : "";
    public string Hint => Tag != null
        ? L.T("Add or remove the tag from a song's … menu, or by selecting several songs.")
        : L.T(Unsorted
            ? "Songs that aren't in any playlist (or in Favorites): select them to add them to a playlist or delete them."
            : "Ctrl or Shift + click to select several songs (Ctrl+A for all), then Del or right-click to delete or move them.");
    // (songs there, all hidden by the "on the device / in the cloud" filter: that's what it says)
    public string EmptyTitle => Storage.HidesAll ? Storage.EmptyTitle
        : Tag != null ? L.T("No songs with this tag") : L.T(Unsorted ? "All sorted" : "No songs yet");
    public string EmptyText => Storage.HidesAll ? Storage.EmptyText
        : Tag != null
        ? L.T("Right-click a song → Tags, or select several songs and press Tags.")
        : L.T(Unsorted
            ? "Every song is in at least one playlist."
            : "Search for a song or paste its link in Search to download music, or add files from your computer.");

    // A tag page also lists the playlists with that tag.
    public List<PlaylistViewModel> TaggedPlaylists { get; private set; } = new();
    public bool HasTaggedPlaylists => TaggedPlaylists.Count > 0;

    public TagFilter TagFilter { get; } = new();

    public List<Choice> Sorts { get; }
    private Choice _sort;
    public Choice Sort
    {
        get => _sort;
        set
        {
            if (value == null || !Set(ref _sort, value)) return;
            _main.Profile.Data.LibrarySort = (string)value.Value!;
            _main.Profile.Save();
            Rebuild();
        }
    }

    public List<TrackRow> Rows { get; private set; } = new();
    public string CountText { get; private set; } = "";
    public bool IsEmpty => _order.Count == 0;

    private string _filter = "";
    public string Filter { get => _filter; set { if (Set(ref _filter, value)) ApplyFilter(); } }
    public bool NoMatches => Rows.Count == 0 && _order.Count > 0;

    public void ApplyFilter()
    {
        Rows = TrackFilter.Apply(_order, _filter, TagFilter, this);
        OnChanged(nameof(Rows), nameof(NoMatches));
    }

    // Rebuilt lazily when the page is opened.
    public void MarkDirty() => _dirty = true;

    public void Rebuild()
    {
        _dirty = false;
        IEnumerable<Track> tracks = _main.Songs();
        if (Unsorted)
        {
            var inPlaylists = _main.Playlists.SelectMany(p => p.P.Tracks).ToHashSet();
            tracks = tracks.Where(t => !inPlaylists.Contains(t.Id));
        }
        if (Tag != null)
        {
            var id = Tag.Id;
            var profile = _main.ProfileOf(Tag.T);
            tracks = tracks.Where(t => profile.TagsOf(t.Id).Contains(id));
            TaggedPlaylists = _main.Playlists.Where(p => p.P.Tags.Contains(id)).ToList();
            OnChanged(nameof(TaggedPlaylists), nameof(HasTaggedPlaylists), nameof(Title), nameof(ContextName));
        }
        var all = Storage.Apply(tracks.ToList(), t => t.IsSaved);
        _order = TrackSort.Apply(all, (string)_sort.Value!).Select(_main.Vm).ToList();
        var total = TimeSpan.FromSeconds(_order.Sum(t => t.T.Duration));
        // "12 songs · 40 min · 3 in the cloud", or with the filter on "· 3 in the cloud hidden" (only the hidden ones, when
        // it hides them all).
        var parts = new List<string>();
        if (_order.Count > 0)
            parts.Add(L.Count(_order.Count, "1 song", "{0} songs") + " · " +
                      (total.TotalHours >= 1 ? L.F("{0} h {1} min", (int)total.TotalHours, total.Minutes) : L.F("{0} min", total.Minutes)));
        if (Storage.IsActive) parts.Add(Storage.HiddenText);
        else if (CloudCount > 0) parts.Add(L.F("{0} in the cloud", CloudCount));
        CountText = string.Join(" · ", parts.Where(p => p.Length > 0));
        ApplyFilter();
        OnChanged(nameof(CountText), nameof(IsEmpty), nameof(CloudCount), nameof(HasCloud), nameof(ShowSaveAll), nameof(SaveAllLabel), nameof(EmptyTitle),
            nameof(EmptyText));
    }

    public void EnsureFresh()
    {
        if (_dirty) Rebuild();
    }
}

public sealed class PlaylistPageViewModel : Observable, ITrackList
{
    private readonly MainViewModel _main;
    private List<TrackViewModel> _order = new();

    public PlaylistPageViewModel(PlaylistViewModel vm, MainViewModel main)
    {
        Vm = vm;
        _main = main;
        PlayCommand = new RelayCommand(() => _main.Player.PlayAll(this, false), () => _order.Count > 0);
        ShuffleCommand = new RelayCommand(() => _main.Player.PlayAll(this, true), () => _order.Count > 0);
        RenameCommand = new RelayCommand(() => _ = _main.RenamePlaylist(Vm), () => !Vm.IsFavorites);
        CoverCommand = new RelayCommand(() => _ = _main.ChangePlaylistCover(Vm), () => !Vm.IsFavorites);
        RemoveCoverCommand = new RelayCommand(() => _main.RemovePlaylistCover(Vm), () => Vm.HasCustomCover);
        DeleteCommand = new RelayCommand(() => _ = _main.DeletePlaylist(Vm), () => !Vm.IsFavorites);
        SaveAllCommand = new RelayCommand(() => _ = _main.SaveToDevice(_order.Where(t => t.IsCloud).ToList()), () => _order.Any(t => t.IsCloud));
        Storage = new StorageFilterViewModel(main);
        Sorts = new List<Choice>
        {
            new(L.T("Custom order"), "custom"), new(L.T("Title"), "title"), new(L.T("Artist"), "artist"), new(L.T("Album"), "album"),
            new(L.T("Duration"), "duration"), new(L.T("Added to library"), "added"),
        };
        _sort = Sorts.FirstOrDefault(s => (string)s.Value! == (vm.P.Sort ?? "custom")) ?? Sorts[0];
        TagFilter.Changed += ApplyFilter;
        Rebuild();
    }

    // How the page shows it, kept in the playlist: its own order ("custom", the one made by dragging the songs) or sorted by
    // title, artist… The other orders don't touch its own: choosing "custom" again brings it back as it was.
    public List<Choice> Sorts { get; }
    private Choice _sort;
    public Choice Sort
    {
        get => _sort;
        set
        {
            if (value == null || !Set(ref _sort, value)) return;
            Vm.P.Sort = IsCustomOrder ? null : (string)value.Value!;
            // (the other device's playlist: its order is kept there too)
            var profile = _main.ProfileOf(Vm.P);
            if (profile.IsMirror) profile.Touch(Vm.P);
            else profile.Save();
            Rebuild();
            OnChanged(nameof(IsCustomOrder));
        }
    }

    // Songs can be moved by hand (dragged, "move to top") only in its own order.
    public bool IsCustomOrder => (string)_sort.Value! == "custom";

    // Back to its own order (moving a song while it's sorted another way).
    public void UseCustomOrder() => Sort = Sorts[0];

    // Only the songs on the device, or only the ones in the cloud (the same choice as the library pages).
    public StorageFilterViewModel Storage { get; }

    // Its songs in the cloud, saved on the device all at once.
    public ICommand SaveAllCommand { get; }
    public int CloudCount => Storage.CloudCount;
    public bool ShowSaveAll => CloudCount > 0 && Storage.Value != StorageFilter.Device;
    public string SaveAllLabel => L.Count(CloudCount, "Save the one in the cloud", "Save all those in the cloud");

    // Its songs and how long; with the filter hiding some, the ones shown and the hidden ones.
    public string TotalText
    {
        get
        {
            if (Storage.HiddenCount == 0) return Vm.TotalText;
            if (_order.Count == 0) return Storage.HiddenText;
            var t = TimeSpan.FromSeconds(_order.Sum(s => s.T.Duration));
            var dur = t.TotalHours >= 1 ? L.F("{0} h {1} min", (int)t.TotalHours, t.Minutes) : L.F("{0} min {1} s", t.Minutes, t.Seconds);
            return $"{L.Count(_order.Count, "1 song", "{0} songs")} · {dur} · {Storage.HiddenText}";
        }
    }

    public string EmptyTitle => Storage.HidesAll ? Storage.EmptyTitle : L.T("This playlist is empty");

    // Rebuilt when shown again after the filter changed elsewhere.
    private bool _dirty;
    public void MarkDirty() => _dirty = true;

    public void EnsureFresh()
    {
        if (_dirty) Rebuild();
    }

    public TagFilter TagFilter { get; } = new();

    public PlaylistViewModel Vm { get; }
    public ICommand PlayCommand { get; }
    public ICommand ShuffleCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand CoverCommand { get; }
    public ICommand RemoveCoverCommand { get; }
    public ICommand DeleteCommand { get; }

    public string ContextId => "playlist:" + Vm.Id;
    public string ContextName => Vm.Name;
    public IReadOnlyList<TrackViewModel> PlayOrder => _order;
    public Playlist? Playlist => Vm.P;

    public List<TrackRow> Rows { get; private set; } = new();
    public bool IsEmpty => _order.Count == 0;

    private string _filter = "";
    public string Filter { get => _filter; set { if (Set(ref _filter, value)) ApplyFilter(); } }
    public bool NoMatches => Rows.Count == 0 && _order.Count > 0;

    public void ApplyFilter()
    {
        Rows = TrackFilter.Apply(_order, _filter, TagFilter, this);
        OnChanged(nameof(Rows), nameof(NoMatches));
    }

    public string EmptyText => Storage.HidesAll ? Storage.EmptyText : L.T(Vm.IsFavorites
        ? "Click the heart next to a song to add it to your favorites."
        : "Add songs from a song's … menu, by dragging them here, or pick this playlist when you download a link.");

    public void Rebuild()
    {
        _dirty = false;
        var tracks = Vm.P.Tracks.ToList().Select(_main.Get).OfType<Track>();
        var songs = TrackSort.Apply(tracks, (string)_sort.Value!).Select(_main.Vm).ToList();
        _order = Storage.Apply(songs, t => t.IsSaved);
        ApplyFilter();
        Vm.Refresh();
        OnChanged(nameof(IsEmpty), nameof(CloudCount), nameof(ShowSaveAll), nameof(SaveAllLabel), nameof(TotalText), nameof(EmptyTitle), nameof(EmptyText));
    }
}

// A song found online (the search, an artist's popular songs): a click plays it without saving it, its button opens it on the
// download page as if its link was pasted. An album or a playlist the other way round: a click opens its card with its songs,
// its button plays them.
public class OnlineHitViewModel : Observable
{
    private readonly MainViewModel _main;

    public OnlineHitViewModel(SearchHit hit, MainViewModel main)
    {
        Hit = hit;
        _main = main;
        InLibrary = Find();
        DownloadCommand = new RelayCommand(() => _main.OpenHit(Hit));
        PlayCommand = new RelayCommand(PlayOrOpen);
    }

    // A click: a song is heard without saving it (in a room it goes into the room's queue), an album or a playlist too, its songs
    // read from its link. Among an artist's popular songs (Siblings) they all play, from this one.
    public ICommand PlayCommand { get; }
    public IReadOnlyList<OnlineHitViewModel>? Siblings { get; set; }
    public ICommand TapCommand => IsSong ? PlayCommand : DownloadCommand;
    public ICommand SideCommand => IsSong ? DownloadCommand : PlayCommand;
    public string SideGlyph => IsSong ? "" : "";
    public string SideTip => IsSong ? L.T("Download|verb") : L.T("Play");

    private void PlayOrOpen()
    {
        if (!IsSong) _ = _main.PlayLinkAsync(Hit.Url, Hit.Title);
        else if (_main.InRoom) _main.OpenHit(Hit);
        else if (Siblings is { Count: > 1 } list && list.Contains(this))
            _main.Player.PlayStream(_main.Radio.AddFromLink(list.Select(h => OnlineSearchServices.ToItem(h.Hit))), Hit.Artist ?? Hit.Title, list.ToList().IndexOf(this));
        else Play();
    }

    private bool Find()
    {
        // (an album isn't a song of the same name)
        if (Hit.Collection != null) return false;
        var lib = _main.Library;
        return lib.FindByKeys(SourceKeys.ForItem(new MediaItem { Url = Hit.Url, PageUrl = Hit.Url })) != null ||
               lib.FindSimilar(Hit.Title, Hit.Artist, Hit.Duration) != null;
    }

    public MainViewModel Main => _main;
    public bool IsSong => Hit.Collection == null;

    // The ids to play: heard without saving it, like a link's songs (RadioViewModel.AddFromLink), the library's own if it has it.
    private List<string> PlayIds() => _main.Radio.AddFromLink(new[] { OnlineSearchServices.ToItem(Hit) });

    // The song to put in the queue (made only when it's asked for: a menu only opened leaves nothing behind).
    public TrackViewModel? Song() => PlayIds() is [var id] && (_main.Get(id) ?? _main.Radio.Track(id)) is { } t ? _main.Vm(t) : null;

    public void Play() => _main.Player.PlayStream(PlayIds(), Hit.Title);

    // The row pulled to the right, or its menu's "Add to queue" (in a room, the room's queue); null: an album or a playlist,
    // or a room that doesn't let you add songs.
    public Action? QueueAction => !IsSong ? null
        : _main.InRoom ? _main.Together.CanAdd ? () => _main.OpenHit(Hit) : null
        : () => { if (Song() is { } t) _main.Player.Enqueue(t); };

    // A download finished: it may be in the library now.
    public void RefreshLibrary()
    {
        bool now = Find();
        if (now == InLibrary) return;
        InLibrary = now;
        OnChanged(nameof(InLibrary));
    }

    public SearchHit Hit { get; }
    public string Title => Hit.Title;
    public string Subtitle => Kind == null ? Join(Hit.Artist, AlbumText) : Join(Kind, Details);
    // The computers' row: the second line (a song's album has its own column there, as your songs' have).
    public string Line => Kind == null ? Hit.Artist ?? "" : Join(Kind, Details);
    // (a single's album is the song itself: not said twice)
    public string AlbumText => Kind == null && Hit.Album is { Length: > 0 } a && Text.Normalize(a) != Text.Normalize(Hit.Title) ? a : "";
    // Its place in the list it's in (in the search the sites' songs go on from yours).
    private string _number = "";
    public string Number { get => _number; set => Set(ref _number, value); }
    // Not a song: what it is, and its owner, year and size.
    public string? Kind => Hit.Collection switch { "album" => L.T("Album"), "ep" => "EP", "playlist" => L.T("Playlist"), _ => null };
    public string Details => Join(Hit.Artist, Hit.Year, Hit.Songs is { } n ? L.Count(n, "1 song", "{0} songs") : null);
    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string DurationText => Hit.Duration is > 0 and var d ? Text.Duration(d) : "";
    public string Service => Hit.Service;
    public Brush ServiceBrush => Ui.BrushFrom(Sites.ColorFor(Hit.Service));
    // Already downloaded (same link, or the same song from another site).
    public bool InLibrary { get; private set; }
    public ICommand DownloadCommand { get; }

    private ImageSource? _thumb;
    private bool _thumbRequested;
    public ImageSource? Thumb
    {
        get
        {
            if (!_thumbRequested && Hit.Thumb != null)
            {
                _thumbRequested = true;
                _ = LoadThumb();
            }
            return _thumb;
        }
    }

    protected virtual int ThumbSize => 112;

    private async Task LoadThumb()
    {
        _thumb = await WebImages.LoadAsync(new[] { Hit.Thumb! }, ThumbSize);
        OnChanged(nameof(Thumb));
    }
}

// The playlist the words name, first in "All": a big card, so it's clear it's a whole playlist and not a song.
public sealed class FeaturedHitViewModel : OnlineHitViewModel
{
    public FeaturedHitViewModel(SearchHit hit, MainViewModel main) : base(hit, main) { }

    protected override int ThumbSize => 300;
}

// A filter over the online results: all of them, or one site.
public sealed class ServiceTab : Observable
{
    public ServiceTab(string? service, string label, int? count)
    {
        Service = service;
        Label = label;
        // (none for Spotify without its key: nothing was searched there)
        Count = count?.ToString() ?? "";
        // (no dot for the artist's tab: it's not a site)
        Brush = service != null && !service.StartsWith('\0') ? Ui.BrushFrom(Sites.ColorFor(service)) : null;
    }

    public string? Service { get; }
    public string Label { get; }
    public string Count { get; }
    public Brush? Brush { get; }
    public bool HasDot => Brush != null;

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

public sealed class NowPlayingViewModel : Observable
{
    public NowPlayingViewModel(MainViewModel main)
    {
        Main = main;
        ToggleVideoCommand = new RelayCommand(() => ShowVideo = !ShowVideo);
        ToggleQueueCommand = new RelayCommand(() => QueueHidden = !QueueHidden);
    }

    public MainViewModel Main { get; }
    public PlayerViewModel Player => Main.Player;
    public ICommand ToggleVideoCommand { get; }
    public ICommand ToggleQueueCommand { get; }

    // The lyrics instead of the cover (or over the video, blurred behind): the button only exists when the song has them.
    public bool ShowLyrics
    {
        get => Main.Profile.Data.ShowLyrics;
        set
        {
            if (Main.Profile.Data.ShowLyrics == value) return;
            Main.Profile.Data.ShowLyrics = value;
            Main.Profile.Save();
            Refresh();
        }
    }

    public bool HasLyrics => Player.Current?.T.HasLyrics == true;
    public bool LyricsVisible => ShowLyrics && HasLyrics;
    public bool IsVideo => VideoPath != null;
    // With the video, timed lyrics are subtitles on it (the line being sung). Plain ones take the stage as text and the
    // video keeps playing behind them (the blurred backdrop), its frame hidden.
    public bool Subtitles => LyricsVisible && IsVideo && Player.Current?.T.Lyrics == LyricsKind.Synced;
    public bool LyricsPage => LyricsVisible && !Subtitles;
    public bool ShowVideoFrame => IsVideo && !LyricsPage;
    public bool LyricsOverCover => LyricsVisible && !IsVideo;
    public bool LyricsOverVideo => LyricsPage && IsVideo;

    // "Next up" folded away to the side: the song gets the whole page.
    public bool QueueHidden
    {
        get => Main.Profile.Data.QueueHidden;
        set
        {
            if (Main.Profile.Data.QueueHidden == value) return;
            Main.Profile.Data.QueueHidden = value;
            Main.Profile.Save();
            OnChanged();
            // (the player bar's queue button lights with it)
            Main.RefreshQueueShown();
        }
    }

    public bool ShowVideo
    {
        get => Main.Profile.Data.ShowVideo;
        set
        {
            Main.Profile.Data.ShowVideo = value;
            Main.Profile.Save();
            Refresh();
        }
    }

    // (driving a computer the video would be this phone's, out of step: the cover instead)
    public string? VideoPath => ShowVideo && !Player.IsRemote ? Player.Current?.T.VideoPath : null;
    public bool HasVideo => Player.Current?.HasVideo == true && !Player.IsRemote;
    public bool ShowCover => VideoPath == null && !LyricsVisible;
    public bool CoverTilt => Main.Host.Settings.CoverTilt;

    // The page over the whole screen (MainViewModel.FullScreen): its button, by the lyrics'.
    public bool FullScreen
    {
        get => Main.FullScreen;
        set => Main.FullScreen = value;
    }
    public string FullScreenTip => FullScreen ? L.T("Exit full screen (Esc)") : L.T("Full screen");
    internal void OnFullScreen() => OnChanged(nameof(FullScreen), nameof(FullScreenTip));

    public void Refresh() => OnChanged(nameof(ShowVideo), nameof(VideoPath), nameof(HasVideo), nameof(ShowCover), nameof(CoverTilt), nameof(ShowLyrics),
        nameof(HasLyrics), nameof(LyricsVisible), nameof(IsVideo), nameof(Subtitles), nameof(LyricsPage), nameof(ShowVideoFrame), nameof(LyricsOverCover),
        nameof(LyricsOverVideo));
}
