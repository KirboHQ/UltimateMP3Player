using System.Windows.Input;
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

public sealed class SearchViewModel : Observable, ITrackList
{
    private readonly MainViewModel _main;
    private List<TrackViewModel> _order = new();

    public SearchViewModel(MainViewModel main) => _main = main;

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
        OnChanged(nameof(Query), nameof(Rows), nameof(PlaylistResults), nameof(HasPlaylists), nameof(NoResults), nameof(Title), nameof(ContextName));
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
            OnChanged(nameof(ShowVideo), nameof(VideoPath), nameof(ShowCover));
        }
    }

    public string? VideoPath => ShowVideo ? Player.Current?.T.VideoPath : null;
    public bool HasVideo => Player.Current?.HasVideo == true;
    public bool ShowCover => VideoPath == null;
    public bool CoverTilt => Main.Host.Settings.CoverTilt;

    public void Refresh() => OnChanged(nameof(VideoPath), nameof(HasVideo), nameof(ShowCover), nameof(CoverTilt));
}
