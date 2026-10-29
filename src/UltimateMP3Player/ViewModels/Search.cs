using System.Windows.Input;
using System.Windows.Threading;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// A heading of the search's results ("Songs", "Artists"…), with "Show all" leading to its chip.
public sealed class SearchSection
{
    public SearchSection(string title, int? count = null, ICommand? more = null)
    {
        Title = title;
        Count = count?.ToString() ?? "";
        MoreCommand = more;
    }

    public string Title { get; }
    public string Count { get; }
    public bool HasCount => Count.Length > 0;
    public ICommand? MoreCommand { get; }
    public bool HasMore => MoreCommand != null;
}

// Cards side by side (artists, albums, playlists): one row of them in "All", all of them under their own chip.
public sealed class CardShelf
{
    public CardShelf(IReadOnlyList<object> cards, bool wrap)
    {
        Cards = cards;
        Wrap = wrap;
    }

    public IReadOnlyList<object> Cards { get; }
    public bool Wrap { get; }
    public int MaxRows => Wrap ? 0 : 1;
}

// A line of the results that says something (searching, nothing found, Spotify without its key), maybe with a button.
public sealed class SearchNote
{
    public SearchNote(string title, string? text = null, bool busy = false, string? button = null, ICommand? command = null)
    {
        Title = title;
        Text = text ?? "";
        Busy = busy;
        ButtonText = button ?? "";
        Command = command;
    }

    public string Title { get; }
    public string Text { get; }
    public bool HasText => Text.Length > 0;
    public bool Busy { get; }
    public string ButtonText { get; }
    public ICommand? Command { get; }
    public bool HasButton => Command != null;
}

// The sites over the online songs: all of them or one.
public sealed class SiteChips
{
    public SiteChips(List<ServiceTab> tabs, ICommand select, ICommand settings)
    {
        Tabs = tabs;
        SelectCommand = select;
        SettingsCommand = settings;
    }

    public List<ServiceTab> Tabs { get; }
    public ICommand SelectCommand { get; }
    public ICommand SettingsCommand { get; }
}

// The artist the words name: first and big, like the album named so.
public sealed class TopArtistResult
{
    public TopArtistResult(ArtistCardViewModel artist) => Artist = artist;
    public ArtistCardViewModel Artist { get; }
}

// A chip over the results: All, Songs, Artists, Albums and EPs, Playlists.
public sealed class SearchFilter : Observable
{
    public SearchFilter(string key, string label)
    {
        Key = key;
        Label = label;
    }

    public string Key { get; }
    public string Label { get; }
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

// One chip's list (SearchViewModel.Results).
public sealed class SearchResults : Observable
{
    public SearchResults(string filter) => Filter = filter;

    public string Filter { get; }
    private List<object> _items = new();
    public List<object> Items { get => _items; set => Set(ref _items, value); }
}

// The search box's results, your library and the music sites together (Settings › Search online): songs (yours first, then
// the sites'), artists (yours, then Deezer's), albums and EPs, playlists (yours, then the sites'). "All" shows the best of
// each, its chip all of them. Built as one list of items (Items): headings, rows, shelves of cards, notes; each app draws
// each kind its way (the phones: artists, albums and playlists as rows under their chips).
public sealed class SearchViewModel : Observable, ITrackList, IPageOrder
{
    private const int PerService = 15;
    public const string All = "all", SongsKey = "songs", ArtistsKey = "artists", AlbumsKey = "albums", PlaylistsKey = "playlists", RadioKey = "radio";
#if ANDROID_APP || IOS_APP
    private const bool Phone = true;
#else
    private const bool Phone = false;
#endif
    private readonly MainViewModel _main;
    private List<TrackViewModel> _order = new();
    private readonly DispatcherTimer _onlineTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };

    public SearchViewModel(MainViewModel main)
    {
        _main = main;
        _onlineTimer.Tick += (_, _) => RunOnlineNow();
        SelectServiceCommand = new RelayCommand(p => { if (p is ServiceTab t) SelectService(t.Service); });
        SelectFilterCommand = new RelayCommand(p => { if (p is SearchFilter f) Filter = f.Key; else if (p is string k) Filter = k; });
        OnlineSettingsCommand = new RelayCommand(() => _main.GoSettings());
        SpotifySettingsCommand = new RelayCommand(() => _main.SearchSettings("Spotify", fromSearch: true));
        Filters = new List<SearchFilter>
        {
            new(All, L.T("All")), new(SongsKey, L.T("Songs")), new(ArtistsKey, L.T("Artists")), new(AlbumsKey, L.T("Albums and EPs")), new(PlaylistsKey, L.T("Playlists")),
            new(RadioKey, L.T("Radio")),
        };
        Filters[0].IsSelected = true;
    }

    // The computers' page shows the downloads part (a link open, a song found online opened: the words stay in the box, Back
    // goes to the results), not the results. Set by MainViewModel while this is the page.
    private bool _onDownloads;
    public bool OnDownloads
    {
        get => _onDownloads;
        set
        {
            if (!Set(ref _onDownloads, value)) return;
            OnChanged(nameof(ShowResults), nameof(ShowDownloads));
            _main.RefreshDownloadPanel();
        }
    }
    public bool ShowResults => HasQuery && !OnDownloads;
    public bool ShowDownloads => !ShowResults;

    // A follow changed somewhere: the artists' cards say so at once.
    public void RecheckArtists()
    {
        foreach (var a in _localArtists.Concat(_onlineArtists)) a.Recheck();
    }

    public string ContextId => "search";
    public string ContextName => L.F("Search “{0}”", Query);
    public IReadOnlyList<TrackViewModel> PlayOrder => _order;
    public Playlist? Playlist => null;

    public string Query { get; private set; } = "";
    public List<TrackRow> Rows { get; private set; } = new();
    public List<PlaylistViewModel> PlaylistResults { get; private set; } = new();
    public string Title => L.F("Results for “{0}”", Query);
    public bool HasQuery => Query.Length > 0;
    public MainViewModel Main => _main;

    public ICommand SelectServiceCommand { get; }
    public ICommand SelectFilterCommand { get; }
    public ICommand OnlineSettingsCommand { get; }
    public ICommand SpotifySettingsCommand { get; }

    // ------------------------------------------------------------------ the chips and the list

    public List<SearchFilter> Filters { get; }
    private string _filter = All;
    public string Filter
    {
        get => _filter;
        set
        {
            if (!Set(ref _filter, value)) return;
            foreach (var f in Filters) f.IsSelected = f.Key == value;
            Rebuild();
        }
    }

    // (Ctrl+←/→)
    public void StepFilter(int dir)
    {
        int i = Filters.FindIndex(f => f.Key == _filter) + dir;
        if (i >= 0 && i < Filters.Count) Filter = Filters[i].Key;
    }

    // What the page shows, top to bottom.
    public List<object> Items { get; private set; } = new();

    // The same, as the chip's own page: the computers slide from one chip's to the next (AnimatedContent, OrderOf); new
    // results under the same chip stay on its page.
    public SearchResults Results { get; private set; } = new(All);

    public int? OrderOf(object page) => page is SearchResults r && Filters.FindIndex(f => f.Key == r.Filter) is var i and >= 0 ? i : null;

    private void SetItems(List<object> items)
    {
        Items = items;
        if (Results.Filter == _filter) Results.Items = items;
        else
        {
            Results = new SearchResults(_filter) { Items = items };
            OnChanged(nameof(Results));
        }
        OnChanged(nameof(Items), nameof(NoResults));
    }

    public void Run(string query)
    {
        _libraryChanged = false;
        var words = query.Trim();
        // (new words: back to "All")
        if (words != Query && _filter != All)
        {
            _filter = All;
            foreach (var f in Filters) f.IsSelected = f.Key == All;
            OnChanged(nameof(Filter));
        }
        Query = words;
        var q = new TrackQuery(Query);
        _order = _main.AllVms()
            .Where(q.Matches)
            .OrderByDescending(t => Text.Normalize(t.Title).StartsWith(q.FirstWord) ? 1 : 0)
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(300)
            .ToList();
        Rows = _order.Select((t, i) => new TrackRow(i + 1, t, this)).ToList();
        PlaylistResults = _main.Playlists.Where(q.Matches).ToList();
        _localArtists = LocalArtists(Query);
        _savedStations = Query.StartsWith('#') ? new() : _main.Stations.SavedMatching(Query);
        OnChanged(nameof(Query), nameof(HasQuery), nameof(Rows), nameof(PlaylistResults), nameof(Title), nameof(ContextName), nameof(ShowResults), nameof(ShowDownloads));
        _main.RefreshDownloadPanel();
        if (Query != _onlineQuery)
        {
            // (the sites' results of other words are gone at once, not shown under these; their searches stop)
            _onlineCts?.Cancel();
            _byService.Clear();
            _allHits = new();
            _onlineArtists = new();
            _onlineStations = new();
            _collections.Clear();
            _albumHits = null;
            _featured = null;
        }
        Rebuild();
        ScheduleOnline();
    }

    // Your artists whose name the words start (every word the start of one of theirs: "env" finds Envy), the ones with more
    // songs first.
    private List<ArtistCardViewModel> LocalArtists(string query)
    {
        var words = Text.Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || query.StartsWith('#')) return new();
        var found = new Dictionary<string, (string Name, int Songs)>();
        foreach (var t in _main.Songs())
            foreach (var name in ArtistNames.Of(t.Artist))
            {
                var key = Text.Normalize(name);
                if (found.TryGetValue(key, out var had)) found[key] = (had.Name, had.Songs + 1);
                else
                {
                    var parts = key.Split(' ');
                    if (words.All(w => parts.Any(p => p.StartsWith(w)))) found[key] = (name, 1);
                }
            }
        return found.Values.OrderByDescending(a => Text.Normalize(a.Name) == Text.Normalize(query)).ThenByDescending(a => a.Songs).Take(24)
            .Select(a => new ArtistCardViewModel(_main, a.Name, null, null, L.Count(a.Songs, "1 song", "{0} songs"))).ToList();
    }

    private List<ArtistCardViewModel> _localArtists = new(), _onlineArtists = new();

    // Yours, then Deezer's ones with another name.
    private List<ArtistCardViewModel> Artists()
    {
        var mine = _localArtists.Select(a => Text.Normalize(a.Name)).ToHashSet();
        return _localArtists.Concat(_onlineArtists.Where(a => !mine.Contains(Text.Normalize(a.Name)))).ToList();
    }

    // The artist's albums when the words name one, then the albums and EPs the words name (each once).
    private List<OnlineHitViewModel> Albums()
    {
        var list = new List<SearchHit>();
        var seen = new HashSet<string>();
        // (the artist's own albums come without its name: it's the one the words name, so the same album found by the sites is one)
        string Who(SearchHit h) => Text.Normalize(string.IsNullOrWhiteSpace(h.Artist) ? Query : h.Artist);
        foreach (var h in (_albumHits ?? new()).Concat(_playlistHits?.Where(h => h.Collection is "album" or "ep") ?? Enumerable.Empty<SearchHit>()))
            if (seen.Add(Text.Normalize(h.Title) + "|" + Who(h))) list.Add(h);
        return list.Select(Card).ToList();
    }

    private List<OnlineHitViewModel> OnlinePlaylists() => (_playlistHits ?? new()).Where(h => h.Collection == "playlist").Select(Card).ToList();

    // An album or a playlist as a card (a bigger cover than a row's).
    private readonly Dictionary<SearchHit, FeaturedHitViewModel> _cards = new();
    private OnlineHitViewModel Card(SearchHit h) => _cards.TryGetValue(h, out var c) ? c : _cards[h] = new FeaturedHitViewModel(h, _main);

    private void Rebuild()
    {
        var items = new List<object>();
        var artists = Artists();
        var albums = Albums();
        var lists = PlaylistResults.Cast<object>().Concat(OnlinePlaylists()).ToList();
        var stations = _savedStations.Concat(_onlineStations).ToList();
        bool searching = OnlineBusy || _findingArtists;
        switch (_filter)
        {
            case All:
                {
                    var key = Text.Normalize(Query);
                    if (artists.FirstOrDefault(a => Text.Normalize(a.Name) == key) is { } named) items.Add(new TopArtistResult(named));
                    else if (_featured != null) items.Add(_featured);
                    var online = _allHits.Where(h => !h.InLibrary).Take(Math.Max(4, 8 - Math.Min(4, Rows.Count))).ToList();
                    if (Rows.Count > 0 || online.Count > 0)
                    {
                        items.Add(new SearchSection(L.T("Songs"), null, FilterCommand(SongsKey)));
                        items.AddRange(Rows.Take(4));
                        items.AddRange(online);
                    }
                    if (artists.Count > 0) AddShelf(items, L.T("Artists"), artists, ArtistsKey);
                    if (albums.Count > 0) AddShelf(items, L.T("Albums and EPs"), albums, AlbumsKey);
                    if (lists.Count > 0) AddShelf(items, L.T("Playlists"), lists, PlaylistsKey);
                    if (stations.Count > 0) AddShelf(items, L.T("Radio"), stations, RadioKey);
                    if (searching) items.Add(new SearchNote(L.T("Searching online…"), null, busy: true));
                    else if (items.Count == 0) items.Add(new SearchNote(L.T("No results"), OnlineEnabled ? NoOnlineHint : LocalEmptyHint));
                    break;
                }
            case SongsKey:
                if (Rows.Count > 0)
                {
                    items.Add(new SearchSection(L.T("In your library"), Rows.Count));
                    items.AddRange(Rows);
                }
                if (OnlineEnabled && Searchable(Query))
                {
                    items.Add(new SearchSection(L.T("Online"), _allHits.Count > 0 ? _allHits.Count : null));
                    if (HasServiceTabs) items.Add(new SiteChips(ServiceTabs, SelectServiceCommand, OnlineSettingsCommand));
                    if (ShowSpotifySetup) items.Add(new SearchNote(L.T("Search Spotify too"), SpotifySetupText, button: L.T("Set it up in Settings"), command: SpotifySettingsCommand));
                    else items.AddRange(OnlineHits);
                    if (OnlineBusy && OnlineHits.Count == 0 && !ShowSpotifySetup) items.Add(new SearchNote(L.T("Searching online…"), null, busy: true));
                    else if (NoOnlineResults) items.Add(new SearchNote(NoOnlineText, L.T("Try the title and the artist, or paste the song's link directly.")));
                }
                else if (Rows.Count == 0) items.Add(new SearchNote(L.T("No results"), LocalEmptyHint));
                break;
            case ArtistsKey:
                AddAll(items, artists, searching, L.T("No artist found with this name."));
                break;
            case AlbumsKey:
                AddAll(items, albums, searching, OnlineEnabled ? L.T("No albums found online.") : L.T("Turn on the online search in Settings to find albums."));
                break;
            case PlaylistsKey:
                AddAll(items, lists, searching, L.T("No playlists found."));
                break;
            case RadioKey:
                AddAll(items, stations, _findingStations, OnlineEnabled ? L.T("No radios found.") : L.T("None of your radios has this name."));
                break;
        }
        // (the sites' songs numbered on from yours under the same heading: one list)
        int n = 0;
        foreach (var item in items)
            if (item is SearchSection) n = 0;
            else if (item is TrackRow) n++;
            else if (item is OnlineHitViewModel hit and not FeaturedHitViewModel) hit.Number = (++n).ToString();
        SetItems(items);
    }

    // The box emptied, or the page opened before any search: what it's for, no results (the sites' ones stay for the same words).
    public void Clear()
    {
        Reset();
        Query = "";
        _order = new();
        Rows = new();
        PlaylistResults = new();
        _localArtists = new();
        _savedStations = new();
        SetItems(new List<object> { new SearchNote(L.T("Search your music and online"), L.T("Songs, artists, albums, playlists and radios. To download a song, paste its link here.")) });
        OnChanged(nameof(Query), nameof(HasQuery), nameof(Rows), nameof(PlaylistResults), nameof(Title), nameof(ContextName), nameof(ShowResults), nameof(ShowDownloads));
        _main.RefreshDownloadPanel();
    }

    private ICommand FilterCommand(string key) => new RelayCommand(() => Filter = key);

    private void AddShelf(List<object> items, string title, IEnumerable<object> cards, string key)
    {
        var list = cards.ToList();
        items.Add(new SearchSection(title, null, FilterCommand(key)));
        items.Add(new CardShelf(list.Take(Phone ? 12 : 8).ToList(), false));
    }

    // A chip's whole list: cards in a grid on the computers, rows on the phones (an album or a playlist as a song's row).
    private void AddAll(List<object> items, IEnumerable<object> cards, bool searching, string none)
    {
        var list = cards.ToList();
        if (list.Count > 0)
        {
            if (Phone) items.AddRange(list.Select(c => c is FeaturedHitViewModel f ? Row(f.Hit) : c));
            else items.Add(new CardShelf(list, true));
        }
        if (searching) items.Add(new SearchNote(L.T("Searching online…"), null, busy: true));
        else if (list.Count == 0) items.Add(new SearchNote(none));
    }

    public bool NoResults => Rows.Count == 0 && PlaylistResults.Count == 0 && _allHits.Count == 0 && Artists().Count == 0 && _savedStations.Count == 0
                             && _onlineStations.Count == 0;

    // Your radios whose name has the words, then radio-browser's (the sites' search on: it's online too).
    private List<StationViewModel> _savedStations = new(), _onlineStations = new();
    private bool _findingStations;

    private async Task FindStations(string query, CancellationTokenSource cts)
    {
        _findingStations = true;
        try
        {
            var found = await _main.Stations.FindAsync(query, cts.Token);
            if (_onlineCts != cts) return;
            _onlineStations = found;
        }
        catch { }
        finally
        {
            if (_onlineCts == cts)
            {
                _findingStations = false;
                Rebuild();
            }
        }
    }

    // ------------------------------------------------------------------ online

    private AppSettings S => _main.Host.Settings;
    private List<string> Services => OnlineSearchServices.Ordered(S.SearchServices, S.SearchServicesOff);
    public bool OnlineEnabled => S.OnlineSearch && Services.Count > 0;
    // A search by tag (#rock) or a word too short isn't sent to the sites.
    private bool Searchable(string q) => q.Length >= 2 && !q.StartsWith('#');

    public string LocalEmptyHint => OnlineEnabled
        ? L.T("Search by title, artist or album: the sites' songs show under yours. To download one, pick it or paste its link.")
        : L.T("Search by title, artist or album. To download a new song, paste its link.");
    private string NoOnlineHint => !Searchable(Query) ? L.T("Type at least two letters to search online.")
        : _failed.Count > 0 && _byService.Count == 0 ? L.T("The sites didn't answer: check your connection and try again.")
        : L.T("Try the title and the artist, or paste the song's link directly.");

    // The search box was emptied: what was going on online stops.
    public void Reset()
    {
        _onlineTimer.Stop();
        // A search stopped halfway is done again if the same words come back.
        if (OnlineBusy || _findingArtists) _onlineQuery = "";
        _onlineCts?.Cancel();
    }

    private CancellationTokenSource? _onlineCts;
    private string _onlineQuery = "";
    private readonly Dictionary<string, List<SearchHit>> _byService = new();
    private readonly List<string> _failed = new();
    private List<OnlineHitViewModel> _allHits = new();
    // Each result's row, made once per search (every site that answers rebuilds the lists; a row checks the library and
    // loads its picture).
    private readonly Dictionary<SearchHit, OnlineHitViewModel> _rows = new();
    private OnlineHitViewModel Row(SearchHit h) => _rows.TryGetValue(h, out var row) ? row : _rows[h] = new OnlineHitViewModel(h, _main);
    private string? _serviceFilter;

    public List<OnlineHitViewModel> OnlineHits { get; private set; } = new();
    public List<ServiceTab> ServiceTabs { get; private set; } = new();
    public bool HasServiceTabs => ServiceTabs.Count > 2 || SpotifyLocked;

    private bool _onlineBusy, _findingArtists;
    public bool OnlineBusy { get => _onlineBusy; private set => Set(ref _onlineBusy, value); }
    private string? _onlineStatus;
    public string? OnlineStatus { get => _onlineStatus; private set => Set(ref _onlineStatus, value); }
    public bool NoOnlineResults => !OnlineBusy && _allHits.Count == 0 && _onlineQuery.Length > 0 && !ShowSpotifySetup;

    // Spotify on but without a key (or with one Spotify refuses): its chip is there anyway, to say what it takes and lead
    // to Settings, where the key goes.
    private bool _spotifyRefused;
    private bool SpotifyLocked => Services.Contains(OnlineSearchServices.Spotify) && (OnlineSearchServices.SpotifyKey == null || _spotifyRefused);
    public bool ShowSpotifySetup => _serviceFilter == OnlineSearchServices.Spotify && SpotifyLocked;
    public string SpotifySetupText => OnlineSearchServices.SpotifyKey == null
        ? L.T("Spotify only answers apps with a key. Make yours once in Settings, free and in a few minutes (your Spotify account needs Premium): then Spotify's songs and artists are found here too.")
        : L.T("Spotify refused your key: check the Client ID and the secret in Settings. Spotify also wants the account that made the key to have Premium.");
    public string NoOnlineText => !Searchable(Query) ? L.T("Type at least two letters to search online.")
        : _failed.Count > 0 && _byService.Count == 0 ? L.T("The sites didn't answer: check your connection and try again.")
        : L.T("No songs found online.");

    private void ScheduleOnline()
    {
        _onlineTimer.Stop();
        if (!OnlineEnabled) return;
        if (Query == _onlineQuery)
        {
            foreach (var h in _rows.Values) h.RefreshLibrary();
            Rebuild();
            return;
        }
        _onlineTimer.Start();
    }

    // Enter in the search box: no wait.
    public void RunOnlineNow()
    {
        _onlineTimer.Stop();
        if (!OnlineEnabled || Query == _onlineQuery && (OnlineBusy || _allHits.Count > 0)) return;
        _onlineCts?.Cancel();
        _onlineQuery = Query;
        _byService.Clear();
        _rows.Clear();
        _cards.Clear();
        _failed.Clear();
        _serviceFilter = null;
        _albumHits = _playlistHits = null;
        _onlineArtists = new();
        _onlineStations = new();
        _albumsBySite.Clear();
        _collections.Clear();
        _featured = null;
        _spotifyRefused = false;
        RebuildOnline();
        if (!Searchable(Query))
        {
            Rebuild();
            return;
        }
        var cts = _onlineCts = new CancellationTokenSource();
        // (Spotify without a key isn't asked)
        var asked = Services.Where(s => s != OnlineSearchServices.Spotify || OnlineSearchServices.SpotifyKey != null).ToList();
        if (asked.Count > 0) _ = SearchOnline(Query, asked, S.SearchParallel, cts);
        _ = FindArtists(Query, cts);
        _ = FindCollections(Query, cts);
        _ = FindStations(Query, cts);
    }

    // Your songs changed while another page was shown (a song downloaded from these results): looked for again when shown.
    private bool _libraryChanged;
    public void MarkDirty() => _libraryChanged = true;

    // Shown again (Back from a download, or from Settings, where a Spotify key may have been entered): your songs and the
    // results' "already have it" marks again, and what the settings dropped searched again.
    public void EnsureFresh()
    {
        if (Query.Length == 0) return;
        if (_libraryChanged) Run(Query);
        else if (_onlineQuery.Length == 0) RunOnlineNow();
    }

    // The albums the words name, every site's (AlbumSites), when they name an artist; the album or playlist named exactly as the
    // words: first in "All" (not when the words are an artist's name: the artist is).
    private List<SearchHit>? _albumHits, _playlistHits;
    private FeaturedHitViewModel? _featured;

    // Deezer's artists for the words (the cards), and the albums of the one they name, from every site.
    private async Task FindArtists(string query, CancellationTokenSource cts)
    {
        _findingArtists = true;
        try
        {
            List<ArtistInfo> found;
            try { found = await Task.Run(() => OnlineSearchServices.SearchArtistsAsync(query, 12, cts.Token)); }
            catch { found = new(); }
            if (cts.IsCancellationRequested || _onlineCts != cts) return;
            _onlineArtists = found.Select(a => new ArtistCardViewModel(_main, a.Name, a.Id, a.Picture, a.Fans > 0 ? L.F("{0} fans", a.Fans.ToString("N0")) : null)).ToList();
            Rebuild();
            var key = Text.Normalize(query);
            var named = _localArtists.FirstOrDefault(a => Text.Normalize(a.Name) == key)?.Name ?? found.FirstOrDefault(a => Text.Normalize(a.Name) == key)?.Name;
            if (named != null) await FindArtistAlbums(named, cts);
        }
        finally
        {
            if (_onlineCts == cts)
            {
                _findingArtists = false;
                Rebuild();
            }
        }
    }

    // Each site's albums of the artist, growing as they answer (one album on several sites once, the first site's: MergeAlbums).
    private readonly Dictionary<string, List<SearchHit>> _albumsBySite = new();

    private Task FindArtistAlbums(string name, CancellationTokenSource cts)
    {
        var sites = AlbumSites();
        return Task.WhenAll(sites.Select(async site =>
        {
            (string Name, List<SearchHit> Hits)? found;
            try { found = await Task.Run(() => OnlineSearchServices.ArtistAlbumsAsync(site, name, cts.Token)); }
            catch { return; }
            if (cts.IsCancellationRequested || _onlineCts != cts || found is not { } f) return;
            _albumsBySite[site] = f.Hits;
            _albumHits = OnlineSearchServices.MergeAlbums(sites.Where(_albumsBySite.ContainsKey).Select(s => _albumsBySite[s]));
            Rebuild();
        }));
    }

    // Where an artist's albums are looked for, the one kept for an album on several sites first: YouTube Music (its albums
    // play straight away; YouTube's are the same), the catalogues (their songs are found on YouTube Music: Deezer anyway, as
    // before, Spotify with its key, Apple Music with no switch of its own), SoundCloud last (the artist's uploads). The sites
    // switched off in the settings aren't asked.
    private List<string> AlbumSites()
    {
        var on = Services;
        var sites = new List<string>();
        if (on.Contains(OnlineSearchServices.YouTubeMusic) || on.Contains(OnlineSearchServices.YouTube)) sites.Add(OnlineSearchServices.YouTubeMusic);
        sites.Add(OnlineSearchServices.Deezer);
        if (on.Contains(OnlineSearchServices.Spotify) && OnlineSearchServices.SpotifyKey != null) sites.Add(OnlineSearchServices.Spotify);
        sites.Add(OnlineSearchServices.AppleMusic);
        if (on.Contains(OnlineSearchServices.SoundCloud)) sites.Add(OnlineSearchServices.SoundCloud);
        return sites;
    }

    // Each site's albums and playlists for the words, mixed like the songs (best of each first, in the settings' order).
    private readonly Dictionary<string, List<SearchHit>> _collections = new();

    private Task FindCollections(string query, CancellationTokenSource cts) => Task.WhenAll(Services.Select(async service =>
    {
        List<SearchHit> hits;
        try { hits = await Task.Run(() => OnlineSearchServices.CollectionsAsync(service, query, PerService, cts.Token)); }
        catch { return; }
        if (cts.IsCancellationRequested || _onlineCts != cts || hits.Count == 0) return;
        _collections[service] = hits;
        var order = Services.Where(_collections.ContainsKey).ToList();
        _playlistHits = order.SelectMany(s => _collections[s]).OrderBy(h => h.Rank).ThenBy(h => order.IndexOf(h.Service)).ToList();
        var words = Text.Tokens(query);
        var named = _playlistHits.FirstOrDefault(h => Text.Tokens(h.Title).SetEquals(words));
        _featured = named == null ? null : _featured?.Hit == named ? _featured : (FeaturedHitViewModel)Card(named);
        Rebuild();
    }));

    private async Task SearchOnline(string query, List<string> services, bool parallel, CancellationTokenSource cts)
    {
        OnlineBusy = true;
        OnlineStatus = L.F("Searching {0}…", parallel ? string.Join(", ", services) : services[0]);
        try
        {
            if (parallel) await Task.WhenAll(services.Select(s => SearchOne(s, query, cts)));
            else
                // One site after the other: the next one only if the one before found nothing (or didn't answer).
                foreach (var s in services)
                {
                    if (cts.IsCancellationRequested) return;
                    OnlineStatus = L.F("Searching {0}…", s);
                    await SearchOne(s, query, cts);
                    if (_byService.TryGetValue(s, out var got) && got.Count > 0) break;
                }
        }
        finally
        {
            if (_onlineCts == cts)
            {
                OnlineBusy = false;
                OnlineStatus = _failed.Count > 0 ? L.F("No answer from: {0}", string.Join(", ", _failed)) : null;
                RebuildOnline();
            }
        }
    }

    private async Task SearchOne(string service, string query, CancellationTokenSource cts)
    {
        List<SearchHit> hits;
        try { hits = await Task.Run(() => OnlineSearchServices.SearchAsync(service, query, PerService, cts.Token)); }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested || _onlineCts != cts) return;
            // (a key Spotify refuses: its chip says so, it's not "no answer")
            if (service == OnlineSearchServices.Spotify && OnlineSearchServices.SpotifyRefused(ex))
            {
                _spotifyRefused = true;
                RebuildOnline();
            }
            else _failed.Add(service);
            return;
        }
        if (cts.IsCancellationRequested || _onlineCts != cts) return;
        _byService[service] = hits;
        RebuildOnline();
    }

    // All the sites mixed, best of each first (in the order chosen in the settings), or one site.
    private void RebuildOnline()
    {
        var order = Services.Where(_byService.ContainsKey).ToList();
        _allHits = order.SelectMany(s => _byService[s]).OrderBy(h => h.Rank).ThenBy(h => order.IndexOf(h.Service))
            .Select(Row).ToList();
        bool locked = SpotifyLocked;
        if (_serviceFilter != null && !_byService.ContainsKey(_serviceFilter) && !(_serviceFilter == OnlineSearchServices.Spotify && locked)) _serviceFilter = null;
        var tabs = new List<ServiceTab> { new(null, L.T("All the sites"), _allHits.Count) };
        tabs.AddRange(Services.Where(s => _byService.TryGetValue(s, out var got) && got.Count > 0 || s == OnlineSearchServices.Spotify && locked)
            .Select(s => new ServiceTab(s, s, _byService.TryGetValue(s, out var got) ? got.Count : null)));
        foreach (var t in tabs) t.IsSelected = t.Service == _serviceFilter;
        ServiceTabs = tabs;
        OnlineHits = _serviceFilter == null ? _allHits : _allHits.Where(h => h.Service == _serviceFilter).ToList();
        OnChanged(nameof(OnlineHits), nameof(ServiceTabs), nameof(HasServiceTabs), nameof(NoOnlineResults), nameof(NoOnlineText), nameof(ShowSpotifySetup),
            nameof(SpotifySetupText));
        Rebuild();
    }

    private void SelectService(string? service)
    {
        _serviceFilter = service;
        RebuildOnline();
    }

    // The settings changed (sites, order, on/off).
    public void OnSettingsChanged()
    {
        _onlineQuery = "";
        _allHits = new();
        _rows.Clear();
        OnlineHits = new();
        _spotifyRefused = false;
        OnChanged(nameof(OnlineEnabled), nameof(OnlineHits), nameof(LocalEmptyHint), nameof(NoOnlineResults), nameof(ShowSpotifySetup));
        Rebuild();
    }
}
