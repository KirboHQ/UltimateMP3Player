using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// Everything the window shows for one profile.
public sealed class MainViewModel : Observable
{
    private readonly Dictionary<string, TrackViewModel> _vms = new();
    private readonly Stack<object> _back = new();
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly Dispatcher _ui;

    public MainViewModel(AppHost host, Profile profile, bool adopt = false)
    {
        Host = host;
        Profile = profile;
        _ui = Application.Current.Dispatcher;
        CleanProfile();
        SyncTags();

        Radio = new RadioViewModel(this);
        Player = new PlayerViewModel(this, host.Audio, adopt);
        LibraryPage = new LibraryViewModel(this, unsorted: false);
        UnsortedPage = new LibraryViewModel(this, unsorted: true);
        Home = new HomeViewModel(this);
        Search = new SearchViewModel(this);
        Downloads = new DownloadsPageViewModel(this);
        NowPlaying = new NowPlayingViewModel(this);
        RebuildPlaylists();
        Home.Refresh();
        _page = Home;

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RunSearch(); };
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastText = null; };
        _refreshTimer.Tick += (_, _) => DoRefresh();

        GoHomeCommand = new RelayCommand(() => Navigate(Home));
        GoLibraryCommand = new RelayCommand(() => Navigate(LibraryPage));
        GoDownloadsCommand = new RelayCommand(() => Navigate(Downloads));
        GoSettingsCommand = new RelayCommand(GoSettings);
        GoTogetherCommand = new RelayCommand(() => Navigate(Together));
        GoNowPlayingCommand = new RelayCommand(() => { if (Page == NowPlaying) GoBack(); else Navigate(NowPlaying); });
        BackCommand = new RelayCommand(GoBack, () => _back.Count > 0);
        NewPlaylistCommand = new RelayCommand(() => _ = NewPlaylist(null));
        OpenPlaylistCommand = new RelayCommand(p => { if (p is PlaylistViewModel vm) OpenPlaylist(vm); });
        PlayPlaylistCommand = new RelayCommand(p => { if (p is PlaylistViewModel vm) PlayPlaylist(vm); });
        // A song card: the one already playing pauses and resumes. In a room: into the room's queue.
        PlayTrackCommand = new RelayCommand(p =>
        {
            if (p is not TrackViewModel t) return;
            if (InRoom) Together.Add(new[] { t });
            else if (t.IsCurrent) Player.PlayPause();
            else Home.PlayRecent(t);
        });
        SubmitSearchCommand = new RelayCommand(SubmitSearch);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        PasteLinkCommand = new RelayCommand(() => _ = PasteLink());
        SwitchProfileCommand = new RelayCommand(() => Host.SwitchProfile());
        ImportCommand = new RelayCommand(() => _ = ImportDialog(false));
        NewTagCommand = new RelayCommand(() => _ = NewTag());
        OpenTagCommand = new RelayCommand(t => { if (t is TagViewModel tag) OpenTag(tag); });
        EditTagCommand = new RelayCommand(t => { if (t is TagViewModel tag) _ = EditTag(tag); });
        DeleteTagCommand = new RelayCommand(t => { if (t is TagViewModel tag) _ = DeleteTag(tag); });

        _added = t => _ui.BeginInvoke(() => OnTrackAdded(t));
        _changed = t => _ui.BeginInvoke(() => OnTrackChanged(t));
        _removed = t => _ui.BeginInvoke(() => OnTrackRemoved(t));
        _playlistChanged = p => _ui.BeginInvoke(() => OnPlaylistChanged(p));
        _playlistsChanged = () => _ui.BeginInvoke(RebuildPlaylists);
        // Tags only change from the UI: handled right away, so a new tag can be used at once.
        _tagsChanged = () => { if (_ui.CheckAccess()) OnTagsChanged(); else _ui.BeginInvoke(OnTagsChanged); };
        _trackTagsChanged = ids => { if (_ui.CheckAccess()) OnTrackTagsChanged(ids); else _ui.BeginInvoke(() => OnTrackTagsChanged(ids)); };
        host.Library.TrackAdded += _added;
        host.Library.TrackChanged += _changed;
        host.Library.TrackRemoved += _removed;
        profile.PlaylistChanged += _playlistChanged;
        profile.PlaylistsChanged += _playlistsChanged;
        profile.TagsChanged += _tagsChanged;
        profile.TrackTagsChanged += _trackTagsChanged;
        host.Lyrics.TrackDone += OnLyricsDone;
        host.Lyrics.BatchDone += OnLyricsBatchDone;
        if (Player.Current is { } playing) host.Lyrics.Auto(playing.T, true);
        Radio.Refresh();
    }

    private readonly Action<Track> _added, _changed, _removed;
    private readonly Action<Playlist> _playlistChanged;
    private readonly Action _playlistsChanged, _tagsChanged;
    private readonly Action<IReadOnlyCollection<string>> _trackTagsChanged;

    public AppHost Host { get; }
    public Profile Profile { get; }
    public Library Library => Host.Library;
    public RadioViewModel Radio { get; }
    public PlayerViewModel Player { get; }
    public HomeViewModel Home { get; }
    public LibraryViewModel LibraryPage { get; }
    public LibraryViewModel UnsortedPage { get; }
    public SearchViewModel Search { get; }
    public DownloadsPageViewModel Downloads { get; }
    public NowPlayingViewModel NowPlaying { get; }
    private SettingsViewModel? _settings;
    public SettingsViewModel Settings => _settings ??= new SettingsViewModel(this);
    private DjViewModel? _dj;
    public DjViewModel Dj => _dj ??= new DjViewModel(this);
    private TogetherViewModel? _together;
    public TogetherViewModel Together => _together ??= new TogetherViewModel(this);
    private StatsViewModel? _stats;
    public StatsViewModel Stats => _stats ??= new StatsViewModel(this);

    // Opened from the settings, always up to date.
    public void OpenStats()
    {
        Stats.Rebuild();
        Navigate(Stats);
    }

    // In a room of "Listen together": the play buttons of the lists become "+" (add to the room).
    public bool InRoom => _together?.InRoom == true;
    public bool CanAddToRoom => _together?.CanAdd == true;

    public void OnRoomChanged() => OnChanged(nameof(InRoom), nameof(CanAddToRoom), nameof(ShowPlayerBar));

    // The DJ, another profile, another language: they can't work while in a room, so leaving it is asked first.
    public bool ConfirmLeaveRoom(string why, string action) => _together == null || _together.ConfirmLeave(why, action);

    // A stand-in for a room song that isn't in the library: the TrackViewModel cache forgets it.
    public void ForgetVm(string id) => _vms.Remove(id);

    // Space, media keys, tray: on the DJ page they drive both decks, elsewhere the player.
    public void TogglePlay()
    {
        if (Page is DjViewModel dj) dj.ToggleAll();
        else Player.PlayPause();
    }

    public void SetPlaying(bool on)
    {
        if (Page is DjViewModel dj) { if (dj.AnyPlaying != on) dj.ToggleAll(); }
        else if (on) Player.Play();
        else Player.Pause();
    }

    // "Load in the DJ" from a song menu: opens the page with the song on that deck.
    public void LoadInDj(TrackViewModel t, bool deckB)
    {
        if (!ConfirmLeaveRoom(DjLeaveText(), L.T("Esci e apri il DJ"))) return;
        Dj.Load(t, deckB ? Dj.B : Dj.A);
        Navigate(Dj);
    }

    private string DjLeaveText() => L.F("La sezione DJ usa l'audio per conto suo: aprendola uscirai dalla stanza «{0}».", Together.RoomName);
    public DownloadQueue Queue => Host.Downloads;

    public string ProfileName => Profile.Info.Name;
    public string ProfileInitial => Profile.Info.Initial;
    public Brush ProfileBrush => Ui.BrushFrom(Profile.Info.Color);
    public ImageSource? ProfileAvatar =>
        Profile.Info.HasAvatar ? Images.Decode(Profile.Info.AvatarPath, 96) : null;
    public bool MultipleProfiles => Host.Profiles.Profiles.Count > 1;

    public ICommand GoHomeCommand { get; }
    public ICommand GoLibraryCommand { get; }
    public ICommand GoDownloadsCommand { get; }
    public ICommand GoSettingsCommand { get; }
    public ICommand GoTogetherCommand { get; }
    public ICommand GoNowPlayingCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand NewPlaylistCommand { get; }
    public ICommand OpenPlaylistCommand { get; }
    public ICommand PlayPlaylistCommand { get; }
    public ICommand PlayTrackCommand { get; }
    public ICommand SubmitSearchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand PasteLinkCommand { get; }
    public ICommand SwitchProfileCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand NewTagCommand { get; }
    public ICommand OpenTagCommand { get; }
    public ICommand EditTagCommand { get; }
    public ICommand DeleteTagCommand { get; }

    public TrackViewModel Vm(Track t)
    {
        if (!_vms.TryGetValue(t.Id, out var vm))
        {
            vm = new TrackViewModel(t, this);
            vm.UpdateSearchText();
            _vms[t.Id] = vm;
        }
        return vm;
    }

    public IEnumerable<TrackViewModel> AllVms() => Library.Snapshot().Select(Vm);

    public void Detach()
    {
        _together?.Shutdown();
        Player.Detach();
        Radio.Detach();
        Downloads.Detach();
        _dj?.Detach();
        _refreshTimer.Stop();
        _searchTimer.Stop();
        _toastTimer.Stop();
        Host.Library.TrackAdded -= _added;
        Host.Library.TrackChanged -= _changed;
        Host.Library.TrackRemoved -= _removed;
        Profile.PlaylistChanged -= _playlistChanged;
        Profile.PlaylistsChanged -= _playlistsChanged;
        Profile.TagsChanged -= _tagsChanged;
        Profile.TrackTagsChanged -= _trackTagsChanged;
        Host.Lyrics.TrackDone -= OnLyricsDone;
        Host.Lyrics.BatchDone -= OnLyricsBatchDone;
    }

    // Songs deleted by another profile leave this one too.
    private void CleanProfile()
    {
        var gone = new HashSet<string>();
        foreach (var p in Profile.PlaylistsSnapshot())
            foreach (var id in p.Tracks)
                if (Library.Get(id) == null) gone.Add(id);
        foreach (var h in Profile.Data.History.ToList())
            if (Library.Get(h.TrackId) == null) gone.Add(h.TrackId);
        foreach (var id in Profile.StatsSnapshot().Keys)
            if (Library.Get(id) == null) gone.Add(id);
        if (gone.Count > 0) Profile.ForgetTracks(gone);
    }

    // ------------------------------------------------------------------ navigation

    private object _page;
    public object Page
    {
        get => _page;
        private set
        {
            var old = _page;
            if (!Set(ref _page, value)) return;
            if (old is DjViewModel leaving) leaving.Leave();
            if (value is DjViewModel entering) entering.Enter();
            if (old is TogetherViewModel hidden) hidden.Hidden();
            if (value is TogetherViewModel shown) shown.Shown();
            OnChanged(nameof(ShowPlayerBar));
            Player.UpNextVisible = value == NowPlaying;
            foreach (var p in Playlists) p.IsSelected = value is PlaylistPageViewModel pp && pp.Vm == p;
            foreach (var t in Tags) t.IsSelected = value is LibraryViewModel { Tag: { } tag } && tag == t;
            OnChanged(nameof(Section), nameof(IsNowPlaying));
            if (value == NowPlaying) NowPlaying.Refresh();
        }
    }

    public string Section => Page switch
    {
        HomeViewModel => "home",
        LibraryViewModel { IsTagPage: true } => "tag",
        LibraryViewModel { Unsorted: true } => "unsorted",
        LibraryViewModel => "library",
        DownloadsPageViewModel => "downloads",
        DjViewModel => "dj",
        TogetherViewModel => "together",
        // Reached from the settings.
        SettingsViewModel or StatsViewModel => "settings",
        NowPlayingViewModel => "nowplaying",
        SearchViewModel => "search",
        _ => "",
    };

    public bool IsHome { get => Section == "home"; set { if (value) Navigate(Home); } }
    public bool IsLibrary { get => Section == "library"; set { if (value) Navigate(LibraryPage); } }
    public bool IsUnsorted { get => Section == "unsorted"; set { if (value) Navigate(UnsortedPage); } }
    public bool IsDownloads { get => Section == "downloads"; set { if (value) Navigate(Downloads); } }
    public bool IsDj { get => Section == "dj"; set { if (value) Navigate(Dj); } }
    public bool IsTogether { get => Section == "together"; set { if (value) Navigate(Together); } }
    // On the DJ page the decks are the player.
    public bool ShowPlayerBar => Page is not DjViewModel;
    public bool IsSettings { get => Section == "settings"; set { if (value) GoSettings(); } }
    public bool IsNowPlaying => Section == "nowplaying";

    public void Navigate(object page)
    {
        if (page == Page) return;
        // The DJ has its own audio: in a room it would mean leaving it, so it's asked first.
        if (page is DjViewModel && InRoom && !ConfirmLeaveRoom(DjLeaveText(), L.T("Esci e apri il DJ")))
        {
            OnNavChanged();
            return;
        }
        if (page is HomeViewModel) Home.Refresh();
        if (page == UnsortedPage) UnsortedPage.Rebuild();
        else if (page is LibraryViewModel lib) lib.EnsureFresh();
        if (Page is not SearchViewModel || page is SearchViewModel) _back.Push(Page);
        if (_back.Count > 30) { var keep = _back.Take(30).Reverse().ToList(); _back.Clear(); foreach (var k in keep) _back.Push(k); }
        Page = page;
        OnNavChanged();
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnNavChanged() => OnChanged(nameof(IsHome), nameof(IsLibrary), nameof(IsUnsorted), nameof(IsDownloads), nameof(IsDj), nameof(IsTogether),
        nameof(IsSettings));

    private void GoBack()
    {
        while (_back.Count > 0)
        {
            var p = _back.Pop();
            if (p is PlaylistPageViewModel pp && Profile.GetPlaylist(pp.Vm.Id) == null) continue;
            if (p is HomeViewModel) Home.Refresh();
            Page = p;
            break;
        }
        OnNavChanged();
    }

    public void GoSettings()
    {
        Settings.Refresh();
        Navigate(Settings);
    }

    // ------------------------------------------------------------------ search box

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            OnChanged(nameof(IsLink), nameof(HasSearchText));
            _searchTimer.Stop();
            if (IsLink) return;
            if (string.IsNullOrWhiteSpace(value))
            {
                Search.Reset();
                if (Page == Search) GoBack();
                return;
            }
            _searchTimer.Start();
        }
    }

    public bool HasSearchText => SearchText.Length > 0;
    public bool IsLink => LooksLikeLink(SearchText);

    private static bool LooksLikeLink(string s)
    {
        s = s.Trim();
        return Regex.IsMatch(s, @"^(https?://|spotify:|www\.)\S+$", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(s, @"^[\w-]+(\.[\w-]+)*\.(com|it|be|tv|net|org|app|co|link|fm|me|ly|gl|to|cc)/\S*$", RegexOptions.IgnoreCase);
    }

    private void RunSearch()
    {
        if (string.IsNullOrWhiteSpace(SearchText) || IsLink) return;
        Search.Run(SearchText);
        if (Page != Search) Navigate(Search);
    }

    private void SubmitSearch()
    {
        if (IsLink) StartDownload(SearchText.Trim());
        else
        {
            _searchTimer.Stop();
            RunSearch();
            // Enter: the online search starts right away too.
            Search.RunOnlineNow();
        }
    }

    // fromSearch: a song found online. The search stays (text and results), so Back returns to it.
    public void StartDownload(string url, bool fromSearch = false)
    {
        if (fromSearch && Page == Search)
        {
            _back.Push(Search);
            Page = Downloads;
            OnNavChanged();
            CommandManager.InvalidateRequerySuggested();
            _ = Downloads.AnalyzeAsync(url);
            return;
        }
        Navigate(Downloads);
        _ = Downloads.AnalyzeAsync(url);
        _searchText = "";
        OnChanged(nameof(SearchText), nameof(IsLink), nameof(HasSearchText));
    }

    private async Task PasteLink()
    {
        var text = (await Ui.ClipboardTextAsync())?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        if (LooksLikeLink(text)) StartDownload(text);
        else SearchText = text;
    }

    // ------------------------------------------------------------------ toast

    private string? _toastText;
    public string? ToastText { get => _toastText; private set => Set(ref _toastText, value); }

    public void Toast(string text)
    {
        ToastText = text;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ------------------------------------------------------------------ playlists

    public ObservableCollection<PlaylistViewModel> Playlists { get; } = new();

    private void RebuildPlaylists()
    {
        var current = Profile.PlaylistsSnapshot();
        var old = Playlists.ToDictionary(p => p.Id);
        Playlists.Clear();
        foreach (var p in current.OrderBy(p => p.IsFavorites ? 0 : 1))
        {
            var vm = old.TryGetValue(p.Id, out var o) ? o : new PlaylistViewModel(p, this);
            vm.Refresh();
            vm.IsSelected = Page is PlaylistPageViewModel pp && pp.Vm.Id == p.Id;
            vm.IsPlayingFrom = Player.ContextId == "playlist:" + p.Id;
            Playlists.Add(vm);
        }
        if (Page is PlaylistPageViewModel page && Profile.GetPlaylist(page.Vm.Id) == null) Navigate(Home);
        UnsortedPage.MarkDirty();
        Home.Refresh();
    }

    private void OnPlaylistChanged(Playlist p)
    {
        _dirtyPlaylists.Add(p.Id);
        if (p.IsFavorites) _favoritesDirty = true;
        ScheduleRefresh();
    }

    // Bursts of changes (downloads, bulk deletes) rebuild the lists once.
    private readonly HashSet<string> _dirtyPlaylists = new();
    private readonly HashSet<string> _removedTracks = new();
    private bool _libraryDirty, _favoritesDirty;

    private void ScheduleRefresh()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private void DoRefresh()
    {
        _refreshTimer.Stop();
        if (_removedTracks.Count > 0)
        {
            var removed = _removedTracks.ToList();
            _removedTracks.Clear();
            foreach (var id in removed) _vms.Remove(id);
            foreach (var info in Host.Profiles.Profiles) Core.Profile.Get(info).ForgetTracks(removed);
        }
        bool library = _libraryDirty;
        var dirty = _dirtyPlaylists.ToHashSet();
        bool favorites = _favoritesDirty;
        _libraryDirty = _favoritesDirty = false;
        _dirtyPlaylists.Clear();

        if (library)
        {
            LibraryPage.Rebuild();
            if (Page == Search) Search.Run(Search.Query);
            if (Page == Home) Home.Refresh();
            if (Page == _stats) _stats!.Rebuild();
        }
        if (library || dirty.Count > 0)
        {
            if (Page == UnsortedPage) UnsortedPage.Rebuild();
            else UnsortedPage.MarkDirty();
        }
        foreach (var p in Playlists)
            if (library || dirty.Contains(p.Id)) p.Refresh();
        if (Page is PlaylistPageViewModel page && (library || dirty.Contains(page.Vm.Id))) page.Rebuild();
        if (favorites)
            foreach (var t in _vms.Values) t.RefreshFavorite();
        // Tag pages also list playlists: a playlist's tags may have changed.
        if (_trackTagsDirty || library || dirty.Count > 0)
        {
            _trackTagsDirty = false;
            RefreshTaggedPages();
        }
    }

    public void OpenPlaylist(PlaylistViewModel vm)
    {
        if (Page is PlaylistPageViewModel current && current.Vm.Id == vm.Id) return;
        Navigate(new PlaylistPageViewModel(vm, this));
    }

    private void PlayPlaylist(PlaylistViewModel vm) => Player.PlayAll(new PlaylistPageViewModel(vm, this), Player.Shuffle);

    public string NewPlaylistName() => L.F("La mia playlist n. {0}", Playlists.Count);

    // The dialogs are awaited: on Android they can't hold the code up (on Windows they're the usual modal ones).
    public async Task<Playlist?> NewPlaylist(TrackViewModel? with)
    {
        var name = await Dialogs.PromptAsync(L.T("Nuova playlist"), L.T("Nome della playlist"), NewPlaylistName());
        if (string.IsNullOrWhiteSpace(name)) return null;
        var p = Profile.CreatePlaylist(name);
        if (with != null && SaveRoomSong(with, p)) return p;
        if (with != null)
        {
            Profile.AddTrack(p, with.Id);
            Toast(L.F("Aggiunto a «{0}»", p.Name));
        }
        else if (Playlists.FirstOrDefault(x => x.Id == p.Id) is { } vm) OpenPlaylist(vm);
        return p;
    }

    public async Task RenamePlaylist(PlaylistViewModel vm)
    {
        var name = await Dialogs.PromptAsync(L.T("Rinomina playlist"), L.T("Nuovo nome"), vm.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        Profile.Rename(vm.P, name);
    }

    public async Task DeletePlaylist(PlaylistViewModel vm)
    {
        if (vm.IsFavorites) return;
        if (!await Dialogs.ConfirmAsync(L.T("Eliminare la playlist?"), L.F("La playlist «{0}» verrà eliminata. I brani restano nella libreria.", vm.Name), L.T("Elimina"), true)) return;
        Profile.DeletePlaylist(vm.P);
    }

    public async Task ChangePlaylistCover(PlaylistViewModel vm)
    {
        var file = await Dialogs.PickImageAsync();
        if (file == null) return;
        await SetCover(file, Profile.PlaylistCover(vm.P), () => Profile.SetPlaylistCover(vm.P, true));
    }

    public void RemovePlaylistCover(PlaylistViewModel vm)
    {
        try { File.Delete(Profile.PlaylistCover(vm.P)); } catch { }
        Profile.SetPlaylistCover(vm.P, false);
    }

    private async Task SetCover(string image, string target, Action done)
    {
        bool ok = await Task.Run(() => AudioAnalysis.MakeCoverFromImageAsync(image, target));
        if (ok) done();
        else Toast(L.T("Immagine non valida o non leggibile."));
    }

    // ------------------------------------------------------------------ track actions

    // A song heard in a room or suggested online that isn't in the library yet: saved there first, then used as asked.
    private bool SaveRoomSong(TrackViewModel t, Playlist? p)
    {
        if (Radio.Item(t.Id) is { } suggested)
        {
            _ = Radio.Save(suggested, p);
            return true;
        }
        if (_together == null || !_together.IsStandIn(t)) return false;
        if (_together.ItemFor(t) is { } item) _ = _together.Save(item, p);
        return true;
    }

    public void ToggleFavorite(TrackViewModel t)
    {
        if (SaveRoomSong(t, Profile.Favorites)) return;
        var fav = Profile.Favorites;
        if (Profile.Contains(fav, t.Id))
        {
            Profile.RemoveTrack(fav, t.Id);
            Toast(L.T("Rimosso dai Preferiti"));
        }
        else
        {
            Profile.AddTrack(fav, t.Id);
            Toast(L.T("Aggiunto ai Preferiti ♥"));
        }
        t.RefreshFavorite();
    }

    public void AddToPlaylist(TrackViewModel t, Playlist p)
    {
        if (SaveRoomSong(t, p)) return;
        var name = PlaylistViewModel.DisplayName(p);
        Toast(Profile.AddTrack(p, t.Id) ? L.F("Aggiunto a «{0}»", name) : L.F("È già in «{0}»", name));
    }

    public void RemoveFromPlaylist(TrackViewModel t, Playlist p)
    {
        Profile.RemoveTrack(p, t.Id);
        Toast(L.F("Rimosso da «{0}»", PlaylistViewModel.DisplayName(p)));
    }

    // A song played outside a list (downloads page, search): songs like it follow, or all your songs (Settings).
    public void PlayInLibrary(TrackViewModel t)
    {
        if (Player.RadioAfterSingle && !InRoom)
        {
            Player.PlayRadio(t, false);
            return;
        }
        if (!LibraryPage.PlayOrder.Contains(t)) LibraryPage.Rebuild();
        Player.PlayFrom(LibraryPage, t);
    }

    // ------------------------------------------------------------------ several selected tracks

    public void AddToPlaylist(IReadOnlyList<TrackViewModel> tracks, Playlist p)
    {
        int added = Profile.AddTracks(p, tracks.Select(t => t.Id));
        var name = PlaylistViewModel.DisplayName(p);
        Toast(added == 0 ? L.F("Erano già tutti in «{0}»", name) : added == 1 ? L.F("1 brano aggiunto a «{0}»", name) : L.F("{0} brani aggiunti a «{1}»", added, name));
    }

    public async Task NewPlaylistWith(IReadOnlyList<TrackViewModel> tracks)
    {
        var name = await Dialogs.PromptAsync(L.T("Nuova playlist"), L.T("Nome della playlist"), NewPlaylistName());
        if (string.IsNullOrWhiteSpace(name)) return;
        var p = Profile.CreatePlaylist(name);
        AddToPlaylist(tracks, p);
    }

    public void RemoveFromPlaylist(IReadOnlyList<TrackViewModel> tracks, Playlist p)
    {
        Profile.RemoveTracks(p, tracks.Select(t => t.Id));
        var name = PlaylistViewModel.DisplayName(p);
        Toast(tracks.Count == 1 ? L.F("Rimosso da «{0}»", name) : L.F("{0} brani rimossi da «{1}»", tracks.Count, name));
    }

    public void AddToFavorites(IReadOnlyList<TrackViewModel> tracks) => AddToPlaylist(tracks, Profile.Favorites);

    public void Enqueue(IReadOnlyList<TrackViewModel> tracks)
    {
        if (InRoom)
        {
            Together.Add(tracks);
            return;
        }
        foreach (var t in tracks) Player.Enqueue(t, quiet: true);
        Toast(tracks.Count == 1 ? L.F("«{0}» aggiunto alla coda", tracks[0].Title) : L.F("{0} brani aggiunti alla coda", tracks.Count));
    }

    public void PlaySelection(IReadOnlyList<TrackViewModel> tracks)
    {
        if (tracks.Count == 0) return;
        if (InRoom)
        {
            Together.Add(tracks);
            return;
        }
        Player.PlayFrom(new TrackListSource("selection", L.T("Brani selezionati"), tracks), tracks[0], alwaysQueue: true);
    }

    public async Task EditTrack(TrackViewModel t)
    {
        var r = await Dialogs.EditTrackAsync(t.T);
        if (r == null) return;
        t.T.Title = string.IsNullOrWhiteSpace(r.Value.Title) ? t.T.Title : r.Value.Title.Trim();
        t.T.Artist = string.IsNullOrWhiteSpace(r.Value.Artist) ? null : r.Value.Artist.Trim();
        t.T.Album = string.IsNullOrWhiteSpace(r.Value.Album) ? null : r.Value.Album.Trim();
        t.T.Bpm = double.TryParse(r.Value.Bpm.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var bpm) && bpm is > 20 and < 400 ? Math.Round(bpm, 2) : null;
        Library.Changed(t.T);
    }

    public async Task ChangeTrackCover(TrackViewModel t)
    {
        var file = await Dialogs.PickImageAsync();
        if (file == null) return;
        await SetCover(file, AppPaths.TrackCover(t.Id), () =>
        {
            t.T.HasCover = true;
            t.T.CoverVersion++;
            Library.Changed(t.T);
        });
    }

    public void ShowInFolder(TrackViewModel t)
    {
        if (!File.Exists(t.T.Path)) { Toast(L.T("Il file non esiste più.")); return; }
        Ui.ShowInFolder(t.T.Path);
    }

    public Task DeleteTrack(TrackViewModel t) => DeleteTracks(new[] { t });

    public async Task DeleteTracks(IReadOnlyList<TrackViewModel> tracks)
    {
        // The song the room is playing stays until it's over (its file is open).
        if (InRoom && Player.Current is { } playing && tracks.Contains(playing))
        {
            Toast(L.F("«{0}» sta suonando nella stanza: eliminalo quando è finito.", playing.Title));
            tracks = tracks.Where(t => t != playing).ToList();
        }
        tracks = tracks.Where(t => Library.Get(t.Id) != null).ToList();
        if (tracks.Count == 0) return;
        string msg;
        if (tracks.Count == 1)
        {
            var t = tracks[0];
            msg = t.T.IsLocal
                ? L.F("«{0}» verrà tolto dalla libreria di tutti i profili. Il file sul computer non viene toccato.", t.Title)
                : L.F("«{0}» verrà eliminato dal computer e tolto dalle playlist di tutti i profili.", t.Title);
        }
        else
        {
            int local = tracks.Count(t => t.T.IsLocal);
            msg = L.F("{0} brani verranno eliminati dal computer e tolti dalle playlist di tutti i profili.", tracks.Count) +
                  (local > 0 ? "\n\n" + L.F("{0} di questi erano già sul computer prima: vengono solo tolti dalla libreria, i file restano.", local) : "");
        }
        if (!await Dialogs.ConfirmAsync(tracks.Count == 1 ? L.T("Eliminare il brano?") : L.F("Eliminare {0} brani?", tracks.Count), msg, L.T("Elimina"), true)) return;
        await Delete(tracks.ToList());
    }

    // Removes the song everywhere: files, cover, playlists, history, queue.
    private async Task Delete(List<TrackViewModel> tracks)
    {
        // A song suggested online, saved and deleted while it plays: this listen goes on (from the cache) instead of
        // jumping to the next song.
        if (Player.Current is { } playing && tracks.Contains(playing)) await Player.KeepAsSuggestion(playing.T);
        await Player.RemoveTracks(tracks.Select(t => t.Id).ToHashSet());
        var files = new List<string>();
        foreach (var t in tracks)
        {
            Library.Remove(t.T);
            files.Add(AppPaths.TrackCover(t.Id));
            files.Add(LyricsStore.PathFor(t.Id, true));
            files.Add(LyricsStore.PathFor(t.Id, false));
            if (!t.T.IsLocal) files.AddRange(new[] { t.T.Path, t.T.VideoPath }.OfType<string>());
        }
        if (tracks.Count > 1) Toast(L.F("{0} brani eliminati", tracks.Count));
        // The player may still hold the file: retry.
        await Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 20 && files.Count > 0; attempt++)
            {
                foreach (var f in files.ToList())
                {
                    try
                    {
                        if (File.Exists(f)) File.Delete(f);
                        files.Remove(f);
                    }
                    catch { }
                }
                if (files.Count > 0) await Task.Delay(150);
            }
        });
    }

    // Bulk edit: one artist renamed on every song.
    public void RenameArtist(string from, string to)
    {
        var changed = Library.RenameArtist(from, to);
        Toast(L.Count(changed.Count, "1 brano aggiornato", "{0} brani aggiornati"));
    }

    // ------------------------------------------------------------------ tags

    public ObservableCollection<TagViewModel> Tags { get; } = new();
    public bool HasTags => Tags.Count > 0;
    private readonly Dictionary<string, LibraryViewModel> _tagPages = new();
    private bool _trackTagsDirty;

    private void SyncTags()
    {
        var old = Tags.ToDictionary(t => t.Id);
        Tags.Clear();
        foreach (var t in Profile.TagsSnapshot())
        {
            var vm = old.TryGetValue(t.Id, out var o) ? o : new TagViewModel(t, this);
            vm.Refresh();
            vm.IsSelected = _page is LibraryViewModel { Tag: { } tag } && tag.Id == t.Id;
            Tags.Add(vm);
        }
        // The "Tag" column of the song lists only exists once there are tags.
        Ui.SetTagColumn(Tags.Count > 0);
        OnChanged(nameof(HasTags));
    }

    private void OnTagsChanged()
    {
        SyncTags();
        var ids = Tags.Select(t => t.Id).ToHashSet();
        foreach (var gone in _tagPages.Keys.Where(k => !ids.Contains(k)).ToList()) _tagPages.Remove(gone);
        foreach (var vm in _vms.Values) vm.RefreshTags();
        foreach (var p in Playlists) p.Refresh();
        foreach (var f in TagFilters()) f.Keep(ids);
        if (Page is LibraryViewModel { Tag: { } tag } && !ids.Contains(tag.Id)) Navigate(Home);
        else RefreshTaggedPages();
    }

    private void OnTrackTagsChanged(IReadOnlyCollection<string> ids)
    {
        foreach (var id in ids)
            if (_vms.TryGetValue(id, out var vm)) vm.RefreshTags();
        foreach (var t in Tags) t.RefreshCount();
        _trackTagsDirty = true;
        ScheduleRefresh();
    }

    private IEnumerable<TagFilter> TagFilters()
    {
        yield return LibraryPage.TagFilter;
        yield return UnsortedPage.TagFilter;
        foreach (var p in _tagPages.Values) yield return p.TagFilter;
        if (Page is PlaylistPageViewModel pp) yield return pp.TagFilter;
        if (_stats != null) yield return _stats.TagFilter;
    }

    // Lists that depend on tags follow the changes (without jumping back to the top when nothing is filtered).
    private void RefreshTaggedPages()
    {
        foreach (var page in _tagPages.Values)
            if (page == Page) page.Rebuild();
            else page.MarkDirty();
        foreach (var lib in new[] { LibraryPage, UnsortedPage })
            if (lib.TagFilter.IsActive || lib.Filter.Length > 0) lib.ApplyFilter();
        if (Page is PlaylistPageViewModel pp && (pp.TagFilter.IsActive || pp.Filter.Length > 0)) pp.ApplyFilter();
        if (Page == Search && Search.Query.Contains('#')) Search.Run(Search.Query);
    }

    public void OpenTag(TagViewModel t)
    {
        if (!_tagPages.TryGetValue(t.Id, out var page)) _tagPages[t.Id] = page = new LibraryViewModel(this, false, t);
        Navigate(page);
    }

    // Creates a tag (and puts it on these songs or this playlist, if given).
    public async Task<TagViewModel?> NewTag(IReadOnlyList<TrackViewModel>? songs = null, PlaylistViewModel? playlist = null)
    {
        var r = await Views.TagDialogs.EditAsync(null, Tag.Palette[Tags.Count % Tag.Palette.Length]);
        if (r == null) return null;
        var tag = Profile.CreateTag(r.Value.Name, r.Value.Color);
        var vm = Tags.FirstOrDefault(t => t.Id == tag.Id);
        if (vm == null) return null;
        if (songs is { Count: > 0 }) SetTag(songs, vm, true);
        if (playlist != null) SetPlaylistTag(playlist, vm, true);
        if (songs == null && playlist == null) Toast(L.F("Tag «{0}» creato", vm.Name));
        return vm;
    }

    public async Task EditTag(TagViewModel t)
    {
        var r = await Views.TagDialogs.EditAsync(t.Name, t.Color);
        if (r != null) Profile.UpdateTag(t.T, r.Value.Name, r.Value.Color);
    }

    public async Task DeleteTag(TagViewModel t)
    {
        int songs = t.Count, lists = Profile.PlaylistsSnapshot().Count(p => p.Tags.Contains(t.Id));
        var used = songs == 0 && lists == 0
            ? L.T("Non è usato da nessun brano.")
            : L.F("Verrà tolto da {0} e da {1}.", L.Count(songs, "1 brano", "{0} brani"), L.Count(lists, "1 playlist", "{0} playlist"));
        if (!await Dialogs.ConfirmAsync(L.T("Eliminare il tag?"), L.F("Il tag «{0}» verrà eliminato.", t.Name) + " " + used, L.T("Elimina"), true)) return;
        Profile.DeleteTag(t.T);
    }

    public void SetTag(IReadOnlyList<TrackViewModel> tracks, TagViewModel tag, bool on)
    {
        int n = Profile.SetTag(tracks.Select(t => t.Id), tag.Id, on);
        if (tracks.Count == 1) Toast(on ? L.F("Tag «{0}» aggiunto", tag.Name) : L.F("Tag «{0}» tolto", tag.Name));
        else if (n == 0) Toast(on ? L.F("Avevano già tutti il tag «{0}»", tag.Name) : L.F("Nessuno aveva il tag «{0}»", tag.Name));
        else Toast(on ? L.F("Tag «{0}» aggiunto a {1} brani", tag.Name, n) : L.F("Tag «{0}» tolto da {1} brani", tag.Name, n));
    }

    public void SetPlaylistTag(PlaylistViewModel p, TagViewModel tag, bool on) => Profile.SetPlaylistTag(p.P, tag.Id, on);

    // A playlist's tags onto its songs (all, or the ones left ticked).
    public async Task TagPlaylistSongs(PlaylistViewModel p)
    {
        var songs = p.P.Tracks.Select(id => Library.Get(id)).Where(t => t != null).Select(t => Vm(t!)).ToList();
        if (songs.Count == 0) { Toast(L.T("La playlist è vuota.")); return; }
        if (!HasTags && await NewTag() == null) return;
        var r = await Views.TagDialogs.ApplyToSongsAsync(p, songs);
        if (r == null) return;
        int changed = 0;
        foreach (var tag in r.Value.Tags) changed += Profile.SetTag(r.Value.Songs.Select(t => t.Id), tag.Id, r.Value.Add);
        Toast(changed == 0 ? L.T("Nessun brano da cambiare")
            : r.Value.Add ? L.F("Tag aggiunti: {0} modifiche", changed) : L.F("Tag tolti: {0} modifiche", changed));
    }

    // ------------------------------------------------------------------ library events

    private void OnTrackAdded(Track t)
    {
        Vm(t);
        _libraryDirty = true;
        ScheduleRefresh();
    }

    private void OnTrackChanged(Track t)
    {
        Vm(t).Refresh();
        if (Player.Current?.Id == t.Id) Host.OnTrackChanged();
        foreach (var p in Playlists) if (p.P.Tracks.Contains(t.Id)) p.Refresh();
    }

    private void OnTrackRemoved(Track t)
    {
        _removedTracks.Add(t.Id);
        _libraryDirty = true;
        ScheduleRefresh();
    }

    public void OnPlayed()
    {
        if (Page == Home) Home.Refresh();
    }

    // Another second of a song heard: the statistics on screen follow it (sorted again only when the order can change:
    // a play counted, another song started).
    public void OnListened(string id, bool reorder)
    {
        if (_vms.TryGetValue(id, out var vm)) vm.RefreshStats();
        if (_stats != null && Page == _stats) _stats.OnListened(reorder);
        if (_settings != null && Page == _settings) _settings.OnListened(reorder);
    }

    public void OnCurrentChanged()
    {
        foreach (var p in Playlists) p.IsPlayingFrom = Player?.ContextId == "playlist:" + p.Id;
        NowPlaying?.Refresh();
        // A song never searched gets its lyrics while it plays (Settings > Lyrics).
        if (Player?.Current is { } c) Host.Lyrics.Auto(c.T, true);
    }

    // ------------------------------------------------------------------ lyrics

    public void SearchLyrics(IReadOnlyList<TrackViewModel> tracks)
    {
        if (tracks.Count == 0) return;
        Host.Lyrics.Search(tracks.Select(t => t.T).ToList());
        Toast(tracks.Count == 1 ? L.F("Cerco il testo di «{0}»…", tracks[0].Title) : L.F("Cerco i testi di {0} brani…", tracks.Count));
    }

    public void DeleteLyrics(TrackViewModel t)
    {
        Host.Lyrics.Delete(t.T);
        Toast(L.F("Testo di «{0}» eliminato", t.Title));
    }

    // "Show lyrics" from a song's menu: the song page, with the lyrics on.
    public void ShowLyrics()
    {
        NowPlaying.ShowLyrics = true;
        if (Page != NowPlaying) Navigate(NowPlaying);
    }

    private void OnLyricsDone(Track t, LyricsKind? kind, bool manual, Services.LyricsBatch? batch)
    {
        if (_vms.TryGetValue(t.Id, out var vm)) vm.Refresh();
        if (Player.Current?.Id == t.Id) NowPlaying.Refresh();
        _settings?.RefreshLyricsStats();
        if (!manual || batch != null) return;
        Toast(kind switch
        {
            null => L.T("LRCLIB non risponde: controlla la connessione e riprova."),
            LyricsKind.Synced => L.F("Testo trovato per «{0}»", t.Title),
            LyricsKind.Plain => L.F("Testo trovato per «{0}» (senza tempi)", t.Title),
            LyricsKind.Instrumental => L.F("«{0}» è strumentale: niente testo", t.Title),
            _ when t.HasLyrics => L.F("Nessun altro testo trovato per «{0}»: resta quello di prima", t.Title),
            _ => L.F("Nessun testo trovato per «{0}»", t.Title),
        });
    }

    private void OnLyricsBatchDone(Services.LyricsBatch b)
        => Toast(b.Failed == b.Total ? L.T("LRCLIB non risponde: controlla la connessione e riprova.")
            : L.F("Testi: {0} trovati, {1} senza testo", b.Found, b.Missing));

    // ------------------------------------------------------------------ import

    public async Task ImportDialog(bool folder)
    {
        List<string> paths;
        if (folder)
        {
            if (await Dialogs.PickFolderAsync(L.T("Scegli una cartella con la tua musica")) is not { } dir) return;
            paths = new List<string> { dir };
        }
        else
        {
            if (await Dialogs.PickFilesAsync(L.T("Aggiungi brani dal computer"), true, (L.T("Audio e video"), Importer.Extensions)) is not { Length: > 0 } files) return;
            paths = files.ToList();
        }
        await Import(paths, false);
    }

    public async Task Import(IEnumerable<string> paths, bool playFirst)
    {
        var files = await Task.Run(() => Importer.Expand(paths));
        if (files.Count == 0)
        {
            Toast(L.T("Nessun file audio trovato."));
            return;
        }
        Toast(files.Count == 1 ? L.T("Aggiunta del brano…") : L.F("Aggiunta di {0} brani…", files.Count));
        int ok = 0;
        Track? first = null;
        foreach (var f in files)
        {
            try
            {
                if (Engines.Missing().Contains(Path.GetFileName(Engines.Ffmpeg))) await Host.EnsureEnginesAsync();
                var t = await Task.Run(() => Importer.ImportAsync(Library, f, CancellationToken.None));
                first ??= t;
                ok++;
            }
            catch { }
        }
        Toast(ok == files.Count ? L.Count(ok, "Brano aggiunto alla libreria", "{0} brani aggiunti alla libreria")
                                : L.F("{0} di {1} brani aggiunti (gli altri non sono leggibili)", ok, files.Count));
        // In a room a dropped file doesn't start: it's just in the library now.
        if (playFirst && first != null && !InRoom) Player.PlaySingle(first);
    }

    // ------------------------------------------------------------------ .ump packs

    private readonly Queue<string> _packs = new();
    private bool _packOpen;

    // A pack opened (double click, dragged in, Settings): one dialog at a time.
    public async void OpenPack(string path)
    {
        _packs.Enqueue(path);
        if (_packOpen) return;
        _packOpen = true;
        try
        {
            // After the window has appeared: a double click on a pack can be what started the app.
            await _ui.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            while (_packs.TryDequeue(out var next)) await Views.PackDialogs.Import(this, next);
        }
        finally { _packOpen = false; }
    }

    public async Task PickPack()
    {
        if (await Dialogs.PickFileAsync(L.T("Importa un pacchetto"), (L.T("Pacchetto di Ultimate MP3 Player") + " (*" + Pack.Extension + ")", new[] { Pack.Extension })) is { } file)
            OpenPack(file);
    }

    public void ExportPack(PlaylistViewModel? playlist = null, TagViewModel? tag = null) => Views.PackDialogs.Export(this, playlist, tag);

    // Songs of a pack that travelled as links: downloaded like any other, then into their playlists and tags.
    public void QueuePackDownloads(PackImportResult r)
    {
        if (r.Downloads.Count == 0) return;
        var profile = Profile;
        var map = new Dictionary<string, string>(r.Map);
        var batch = new DownloadBatch(profile, null, r.Downloads.Count)
        {
            Placed = (i, id) =>
            {
                var d = r.Downloads[i];
                map[d.Track.Id] = id;
                if (Library.Get(id) is { } t) PackImporter.Place(d, t, Library, profile, map);
            },
        };
        Queue.Add(batch, r.Downloads.Select((d, i) => (PackImporter.ToMediaItem(d.Track), i)), false);
    }

    public static void OpenFolder(string dir) => Ui.OpenFolder(dir);

    // A web page in the default browser.
    public static void OpenUrl(string url)
    {
#if ANDROID_APP
        Platform.Files.OpenUrl(url);
#else
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
#endif
    }
}
