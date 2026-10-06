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

public sealed class HomeViewModel : Observable
{
    private readonly MainViewModel _main;

    public HomeViewModel(MainViewModel main)
    {
        _main = main;
        Refresh();
    }

    public string Greeting
    {
        get
        {
            int h = DateTime.Now.Hour;
            var g = h is >= 5 and < 13 ? "Buongiorno" : h is >= 13 and < 18 ? "Buon pomeriggio" : "Buonasera";
            return $"{L.T(g)}, {_main.ProfileName}";
        }
    }

    public List<TrackViewModel> Recent { get; private set; } = new();
    public List<TrackRow> RecentlyAdded { get; private set; } = new();
    // The playlists, then the "New playlist" card, in the same grid.
    public List<object> PlaylistCards => [.. _main.Playlists, NewPlaylistCard.Instance];
    public bool HasRecent => Recent.Count > 0;
    public bool HasRecentlyAdded => RecentlyAdded.Count > 0;
    public bool IsEmpty => _main.Library.Count == 0;
    public TrackListSource RecentList { get; private set; } = new("recent", "", new List<TrackViewModel>());

    public void Refresh()
    {
        Recent = _main.Profile.RecentTracks(12).Select(id => _main.Library.Get(id)).Where(t => t != null).Select(t => _main.Vm(t!)).ToList();
        RecentList = new TrackListSource("recent", L.T("Ascoltati di recente"), Recent);
        var added = _main.Library.Snapshot().OrderByDescending(t => t.Added).Take(8).Select(_main.Vm).ToList();
        var addedList = new TrackListSource("added", L.T("Aggiunti di recente"), added);
        RecentlyAdded = added.Select((t, i) => new TrackRow(i + 1, t, addedList)).ToList();
        OnChanged(nameof(Greeting), nameof(Recent), nameof(RecentlyAdded), nameof(PlaylistCards), nameof(HasRecent), nameof(HasRecentlyAdded),
            nameof(IsEmpty), nameof(RecentList));
    }

    public void PlayRecent(TrackViewModel t) => _main.Player.PlayFrom(RecentList, t);
}

public sealed class NewPlaylistCard
{
    public static readonly NewPlaylistCard Instance = new();
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
            new(L.T("Aggiunti di recente"), "added"), new(L.T("Titolo"), "title"), new(L.T("Artista"), "artist"),
            new(L.T("Album"), "album"), new(L.T("Durata"), "duration"),
        };
        _sort = Sorts.FirstOrDefault(s => (string)s.Value! == main.Profile.Data.LibrarySort) ?? Sorts[0];
        PlayCommand = new RelayCommand(() => _main.Player.PlayAll(this, false), () => _order.Count > 0);
        ShuffleCommand = new RelayCommand(() => _main.Player.PlayAll(this, true), () => _order.Count > 0);
        if (unsorted) _dirty = true;
        else Rebuild();
    }

    public bool Unsorted { get; }
    public TagViewModel? Tag { get; }
    public bool IsTagPage => Tag != null;
    // "Add files" only makes sense for the whole library.
    public bool CanImport => !Unsorted && Tag == null;
    public ICommand PlayCommand { get; }
    public ICommand ShuffleCommand { get; }

    public string ContextId => Tag != null ? "tag:" + Tag.Id : Unsorted ? "unsorted" : "library";
    public string ContextName => Tag?.Name ?? L.T(Unsorted ? "Senza playlist" : "Tutti i brani");
    public IReadOnlyList<TrackViewModel> PlayOrder => _order;
    public Playlist? Playlist => null;

    public string Title => ContextName;
    public string Kicker => Tag != null ? L.T("TAG") : L.T(Unsorted ? "DA SISTEMARE" : "LIBRERIA");
    public string Glyph => Tag != null ? "" : Unsorted ? "" : "";
    public string Hint => Tag != null
        ? L.T("Metti o togli il tag dal menu … di un brano, oppure selezionandone più di uno.")
        : L.T(Unsorted
            ? "Brani che non sono in nessuna playlist (né nei Preferiti): selezionali per aggiungerli a una playlist o eliminarli."
            : "Ctrl o Maiusc + clic per selezionare più brani (Ctrl+A tutti), poi Canc o tasto destro per eliminarli o spostarli.");
    public string EmptyTitle => Tag != null ? L.T("Nessun brano con questo tag") : L.T(Unsorted ? "Tutto in ordine" : "Nessun brano, per ora");
    public string EmptyText => Tag != null
        ? L.T("Tasto destro su un brano → Tag, oppure seleziona più brani e premi Tag.")
        : L.T(Unsorted
            ? "Ogni brano è in almeno una playlist."
            : "Incolla un link nella barra in alto per scaricare musica, oppure aggiungi file dal computer.");

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
        IEnumerable<Track> tracks = _main.Library.Snapshot();
        if (Unsorted)
        {
            var inPlaylists = _main.Profile.PlaylistsSnapshot().SelectMany(p => p.Tracks).ToHashSet();
            tracks = tracks.Where(t => !inPlaylists.Contains(t.Id));
        }
        if (Tag != null)
        {
            var id = Tag.Id;
            tracks = tracks.Where(t => _main.Profile.TagsOf(t.Id).Contains(id));
            TaggedPlaylists = _main.Playlists.Where(p => p.P.Tags.Contains(id)).ToList();
            OnChanged(nameof(TaggedPlaylists), nameof(HasTaggedPlaylists), nameof(Title), nameof(ContextName));
        }
        IEnumerable<Track> sorted = (string)_sort.Value! switch
        {
            "title" => tracks.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
            "artist" => tracks.OrderBy(t => t.Artist ?? "￿", StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Album).ThenBy(t => t.Title),
            "album" => tracks.OrderBy(t => t.Album ?? "￿", StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.Title),
            "duration" => tracks.OrderBy(t => t.Duration),
            _ => tracks.OrderByDescending(t => t.Added),
        };
        _order = sorted.Select(_main.Vm).ToList();
        var total = TimeSpan.FromSeconds(_order.Sum(t => t.T.Duration));
        CountText = _order.Count == 0 ? "" : L.Count(_order.Count, "1 brano", "{0} brani") + " · " +
                    (total.TotalHours >= 1 ? L.F("{0} h {1} min", (int)total.TotalHours, total.Minutes) : L.F("{0} min", total.Minutes));
        ApplyFilter();
        OnChanged(nameof(CountText), nameof(IsEmpty));
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
        RenameCommand = new RelayCommand(() => _main.RenamePlaylist(Vm), () => !Vm.IsFavorites);
        CoverCommand = new RelayCommand(() => _main.ChangePlaylistCover(Vm), () => !Vm.IsFavorites);
        RemoveCoverCommand = new RelayCommand(() => _main.RemovePlaylistCover(Vm), () => Vm.HasCustomCover);
        DeleteCommand = new RelayCommand(() => _main.DeletePlaylist(Vm), () => !Vm.IsFavorites);
        TagFilter.Changed += ApplyFilter;
        Rebuild();
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

    public string EmptyText => L.T(Vm.IsFavorites
        ? "Premi il cuore accanto a un brano per aggiungerlo ai preferiti."
        : "Aggiungi brani dal menu … di un brano, trascinandoli qui, oppure scegli questa playlist quando scarichi un link.");

    public void Rebuild()
    {
        _order = Vm.P.Tracks.ToList().Select(id => _main.Library.Get(id)).Where(t => t != null).Select(t => _main.Vm(t!)).ToList();
        ApplyFilter();
        Vm.Refresh();
        OnChanged(nameof(IsEmpty));
    }
}

// A song found online (search page, "Online" tab): a click opens it on the download page, as if its link was pasted.
public sealed class OnlineHitViewModel : Observable
{
    private readonly MainViewModel _main;

    public OnlineHitViewModel(SearchHit hit, MainViewModel main)
    {
        Hit = hit;
        _main = main;
        var lib = main.Library;
        InLibrary = Find();
        DownloadCommand = new RelayCommand(() => _main.StartDownload(Hit.Url, fromSearch: true));
    }

    private bool Find()
    {
        var lib = _main.Library;
        return lib.FindByKeys(SourceKeys.ForItem(new MediaItem { Url = Hit.Url, PageUrl = Hit.Url })) != null ||
               lib.FindSimilar(Hit.Title, Hit.Artist, Hit.Duration) != null;
    }

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
    public string Subtitle => string.Join(" · ", new[] { Hit.Artist, Hit.Album }.Where(s => !string.IsNullOrWhiteSpace(s)));
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

    private async Task LoadThumb()
    {
        _thumb = await WebImages.LoadAsync(new[] { Hit.Thumb! }, 112);
        OnChanged(nameof(Thumb));
    }
}

// A filter over the online results: all of them, or one site.
public sealed class ServiceTab : Observable
{
    public ServiceTab(string? service, string label, int count)
    {
        Service = service;
        Label = label;
        Count = count;
        Brush = service != null ? Ui.BrushFrom(Sites.ColorFor(service)) : null;
    }

    public string? Service { get; }
    public string Label { get; }
    public int Count { get; }
    public Brush? Brush { get; }
    public bool HasDot => Brush != null;

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

public sealed class SearchViewModel : Observable, ITrackList
{
    private const int PerService = 15;
    private readonly MainViewModel _main;
    private List<TrackViewModel> _order = new();
    private readonly DispatcherTimer _onlineTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };

    public SearchViewModel(MainViewModel main)
    {
        _main = main;
        _onlineTimer.Tick += (_, _) => RunOnlineNow();
        ShowLocalCommand = new RelayCommand(() => PickTab(false));
        ShowOnlineCommand = new RelayCommand(() => PickTab(true));
        SelectServiceCommand = new RelayCommand(p => { if (p is ServiceTab t) SelectService(t.Service); });
        OnlineSettingsCommand = new RelayCommand(() => _main.GoSettings());
    }

    public string ContextId => "search";
    public string ContextName => L.F("Ricerca «{0}»", Query);
    public IReadOnlyList<TrackViewModel> PlayOrder => _order;
    public Playlist? Playlist => null;

    public string Query { get; private set; } = "";
    public List<TrackRow> Rows { get; private set; } = new();
    public List<PlaylistViewModel> PlaylistResults { get; private set; } = new();
    public bool HasPlaylists => PlaylistResults.Count > 0;
    public bool NoResults => Rows.Count == 0 && PlaylistResults.Count == 0;
    public string Title => L.F("Risultati per «{0}»", Query);

    public ICommand ShowLocalCommand { get; }
    public ICommand ShowOnlineCommand { get; }
    public ICommand SelectServiceCommand { get; }
    public ICommand OnlineSettingsCommand { get; }

    public void Run(string query)
    {
        Query = query.Trim();
        var q = new TrackQuery(Query);
        _order = _main.AllVms()
            .Where(q.Matches)
            .OrderByDescending(t => Text.Normalize(t.Title).StartsWith(q.FirstWord) ? 1 : 0)
            .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(300)
            .ToList();
        Rows = _order.Select((t, i) => new TrackRow(i + 1, t, this)).ToList();
        PlaylistResults = _main.Playlists.Where(q.Matches).ToList();
        OnChanged(nameof(Query), nameof(Rows), nameof(PlaylistResults), nameof(HasPlaylists), nameof(NoResults), nameof(Title), nameof(ContextName),
            nameof(LocalTabText), nameof(ShowLocalEmpty));
        ScheduleOnline();
    }

    // ------------------------------------------------------------------ online

    // The search box also looks on the music sites (Settings > Search online): the results are in the "Online" tab,
    // which opens by itself when none of your songs match (until you pick a tab yourself).
    private AppSettings S => _main.Host.Settings;
    private List<string> Services => OnlineSearchServices.Ordered(S.SearchServices, S.SearchServicesOff);
    public bool OnlineEnabled => S.OnlineSearch && Services.Count > 0;
    // A search by tag (#rock) or a word too short isn't sent to the sites.
    private bool Searchable(string q) => q.Length >= 2 && !q.StartsWith('#');

    private bool _onlineMode, _tabPicked;
    public bool OnlineMode
    {
        get => _onlineMode && OnlineEnabled;
        private set
        {
            if (!Set(ref _onlineMode, value)) return;
            OnChanged(nameof(LocalMode), nameof(ShowLocalEmpty));
        }
    }
    public bool LocalMode => !OnlineMode;
    public bool ShowLocalEmpty => LocalMode && NoResults;
    public string LocalEmptyHint => OnlineEnabled
        ? L.T("Cerca per titolo, artista o album. Per un brano che non hai guarda nella scheda Online, oppure incolla il suo link.")
        : L.T("Cerca per titolo, artista o album. Per scaricare un brano nuovo incolla il suo link.");

    private void PickTab(bool online)
    {
        _tabPicked = true;
        OnlineMode = online;
        if (online && _onlineQuery != Query) RunOnlineNow();
    }

    // The search box was emptied: the next search chooses its tab again.
    public void Reset()
    {
        _tabPicked = false;
        _tabQuery = null;
        _onlineTimer.Stop();
        // A search stopped halfway is done again if the same words come back.
        if (OnlineBusy) _onlineQuery = "";
        _onlineCts?.Cancel();
    }

    public string LocalTabText => L.F("Nei tuoi brani · {0}", Rows.Count);
    public string OnlineTabText => _allHits.Count > 0 ? L.F("Online · {0}", _allHits.Count) : L.T("Online");

    private CancellationTokenSource? _onlineCts;
    private string _onlineQuery = "";
    private readonly Dictionary<string, List<SearchHit>> _byService = new();
    private readonly List<string> _failed = new();
    private List<OnlineHitViewModel> _allHits = new();
    private string? _serviceFilter;

    public List<OnlineHitViewModel> OnlineHits { get; private set; } = new();
    public List<ServiceTab> ServiceTabs { get; private set; } = new();
    public bool HasServiceTabs => ServiceTabs.Count > 2;

    private bool _onlineBusy;
    public bool OnlineBusy { get => _onlineBusy; private set { if (Set(ref _onlineBusy, value)) OnChanged(nameof(NoOnlineResults)); } }
    private string? _onlineStatus;
    public string? OnlineStatus { get => _onlineStatus; private set => Set(ref _onlineStatus, value); }
    public bool NoOnlineResults => !OnlineBusy && _allHits.Count == 0 && _onlineQuery.Length > 0;
    public string NoOnlineText => !Searchable(Query) ? L.T("Scrivi almeno due lettere per cercare online.")
        : _failed.Count > 0 && _byService.Count == 0 ? L.T("I siti non hanno risposto: controlla la connessione e riprova.")
        : L.T("Nessun brano trovato online.");

    private void ScheduleOnline()
    {
        _onlineTimer.Stop();
        if (!OnlineEnabled) return;
        // Chosen once per search (not again when a finished download changes your songs under the same words).
        if (!_tabPicked && Query != _tabQuery)
        {
            _tabQuery = Query;
            OnlineMode = Rows.Count == 0 && PlaylistResults.Count == 0 && Searchable(Query);
        }
        if (Query == _onlineQuery)
        {
            foreach (var h in _allHits) h.RefreshLibrary();
            return;
        }
        _onlineTimer.Start();
    }

    private string? _tabQuery;

    // Enter in the search box: no wait.
    public void RunOnlineNow()
    {
        _onlineTimer.Stop();
        if (!OnlineEnabled || Query == _onlineQuery && (OnlineBusy || _allHits.Count > 0)) return;
        _onlineCts?.Cancel();
        _onlineQuery = Query;
        _byService.Clear();
        _failed.Clear();
        _serviceFilter = null;
        Rebuild();
        if (!Searchable(Query))
        {
            OnChanged(nameof(NoOnlineText));
            return;
        }
        var cts = _onlineCts = new CancellationTokenSource();
        _ = SearchOnline(Query, Services, S.SearchParallel, cts);
    }

    private async Task SearchOnline(string query, List<string> services, bool parallel, CancellationTokenSource cts)
    {
        OnlineBusy = true;
        OnlineStatus = L.F("Cerco su {0}…", parallel ? string.Join(", ", services) : services[0]);
        try
        {
            if (parallel) await Task.WhenAll(services.Select(s => SearchOne(s, query, cts)));
            else
                // One site after the other: the next one only if the one before found nothing (or didn't answer).
                foreach (var s in services)
                {
                    if (cts.IsCancellationRequested) return;
                    OnlineStatus = L.F("Cerco su {0}…", s);
                    await SearchOne(s, query, cts);
                    if (_byService.TryGetValue(s, out var got) && got.Count > 0) break;
                }
        }
        finally
        {
            if (_onlineCts == cts)
            {
                OnlineBusy = false;
                OnlineStatus = _failed.Count > 0 ? L.F("Non hanno risposto: {0}", string.Join(", ", _failed)) : null;
                OnChanged(nameof(NoOnlineText));
            }
        }
    }

    private async Task SearchOne(string service, string query, CancellationTokenSource cts)
    {
        List<SearchHit> hits;
        try { hits = await Task.Run(() => OnlineSearchServices.SearchAsync(service, query, PerService, cts.Token)); }
        catch
        {
            if (!cts.IsCancellationRequested && _onlineCts == cts) _failed.Add(service);
            return;
        }
        if (cts.IsCancellationRequested || _onlineCts != cts) return;
        _byService[service] = hits;
        Rebuild();
    }

    // All the sites mixed, best of each first (in the order chosen in the settings), or one site.
    private void Rebuild()
    {
        var order = Services.Where(_byService.ContainsKey).ToList();
        _allHits = order.SelectMany(s => _byService[s]).OrderBy(h => h.Rank).ThenBy(h => order.IndexOf(h.Service))
            .Select(h => new OnlineHitViewModel(h, _main)).ToList();
        if (_serviceFilter != null && !_byService.ContainsKey(_serviceFilter)) _serviceFilter = null;
        var tabs = new List<ServiceTab> { new(null, L.T("Tutti"), _allHits.Count) };
        tabs.AddRange(order.Where(s => _byService[s].Count > 0).Select(s => new ServiceTab(s, s, _byService[s].Count)));
        foreach (var t in tabs) t.IsSelected = t.Service == _serviceFilter;
        ServiceTabs = tabs;
        OnlineHits = _serviceFilter == null ? _allHits : _allHits.Where(h => h.Service == _serviceFilter).ToList();
        OnChanged(nameof(OnlineHits), nameof(ServiceTabs), nameof(HasServiceTabs), nameof(OnlineTabText), nameof(NoOnlineResults), nameof(NoOnlineText));
    }

    private void SelectService(string? service)
    {
        _serviceFilter = service;
        Rebuild();
    }

    // The settings changed (sites, order, on/off).
    public void OnSettingsChanged()
    {
        _onlineQuery = "";
        _allHits = new();
        OnlineHits = new();
        OnChanged(nameof(OnlineEnabled), nameof(OnlineMode), nameof(LocalMode), nameof(OnlineHits), nameof(OnlineTabText), nameof(ShowLocalEmpty),
            nameof(LocalEmptyHint), nameof(NoOnlineResults));
    }
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

    public string? VideoPath => ShowVideo ? Player.Current?.T.VideoPath : null;
    public bool HasVideo => Player.Current?.HasVideo == true;
    public bool ShowCover => VideoPath == null && !LyricsVisible;
    public bool CoverTilt => Main.Host.Settings.CoverTilt;

    public void Refresh() => OnChanged(nameof(ShowVideo), nameof(VideoPath), nameof(HasVideo), nameof(ShowCover), nameof(CoverTilt), nameof(ShowLyrics),
        nameof(HasLyrics), nameof(LyricsVisible), nameof(IsVideo), nameof(Subtitles), nameof(LyricsPage), nameof(ShowVideoFrame), nameof(LyricsOverCover),
        nameof(LyricsOverVideo));
}
