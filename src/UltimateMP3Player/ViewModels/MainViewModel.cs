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
        host.Downloads.SavingChanged += OnSaving;
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

    // Connect phone: on the computer the phone driving it, on the phone the computer it drives.
    private PhoneLinkViewModel? _phoneLink;
    public PhoneLinkViewModel PhoneLink => _phoneLink ??= new PhoneLinkViewModel(this);
    private ComputerLinkViewModel? _computerLink;
    public ComputerLinkViewModel ComputerLink => _computerLink ??= new ComputerLinkViewModel(this);
    public bool PhoneLinked => _phoneLink?.HasPhone == true;
    public bool ComputerLinked => _computerLink?.IsLinked == true;

    public void OnPhoneLinkChanged()
    {
        OnChanged(nameof(PhoneLinked));
        Player.OnPhoneLink();
    }

    // The DJ, a room: they can't work while a phone drives the player, so disconnecting it is asked first.
    public bool ConfirmUnlinkPhone(string why, string action)
    {
        if (!PhoneLinked) return true;
        if (!Dialogs.Confirm(L.T("Disconnect the phone?"), why, action, true)) return false;
        _ = _phoneLink!.Disconnect();
        return true;
    }

    public async Task<bool> ConfirmUnlinkPhoneAsync(string why, string action)
    {
        if (!PhoneLinked) return true;
        if (!await Dialogs.ConfirmAsync(L.T("Disconnect the phone?"), why, action, true)) return false;
        if (_phoneLink?.HasPhone == true) _ = _phoneLink.Disconnect();
        return true;
    }

    private static string DjUnlinkText() => L.T("The DJ section uses the audio on its own: opening it disconnects the phone.");

    // In a room of "Listen together": the play buttons of the lists become "+" (add to the room).
    public bool InRoom => _together?.InRoom == true;
    public bool CanAddToRoom => _together?.CanAdd == true;

    public void OnRoomChanged() => OnChanged(nameof(InRoom), nameof(CanAddToRoom), nameof(ShowPlayerBar));

    // The DJ, another profile, another language: they can't work while in a room, so leaving it is asked first.
    public bool ConfirmLeaveRoom(string why, string action) => _together == null || _together.ConfirmLeave(why, action);
    public Task<bool> ConfirmLeaveRoomAsync(string why, string action) => _together == null ? Task.FromResult(true) : _together.ConfirmLeaveAsync(why, action);

    // Phones, before the screen is rebuilt or another profile opens: the room and the computer this phone drives end with
    // this session, so both are asked first.
    public async Task<bool> ConfirmLeaveAllAsync(string roomWhy, string action)
    {
        if (!await ConfirmLeaveRoomAsync(roomWhy, action)) return false;
        if (_computerLink?.IsLinked != true) return true;
        return await Dialogs.ConfirmAsync(L.T("Disconnect from the computer?"), L.F("This phone will be disconnected from {0}.", _computerLink.ComputerName),
            L.T("Disconnect and continue"), true);
    }

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
        if (!ConfirmLeaveRoom(DjLeaveText(), L.T("Leave and open the DJ"))) return;
        if (!ConfirmUnlinkPhone(DjUnlinkText(), L.T("Disconnect and open the DJ"))) return;
        Dj.Load(t, deckB ? Dj.B : Dj.A);
        Navigate(Dj);
    }

    private string DjLeaveText() => L.F("The DJ section uses the audio on its own: opening it will make you leave “{0}”.", Together.RoomName);
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
        _phoneLink?.Shutdown();
        _computerLink?.Shutdown();
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
        Host.Downloads.SavingChanged -= OnSaving;
    }

    // Songs deleted by another profile leave this one too.
    private void CleanProfile()
    {
        var gone = new HashSet<string>();
        foreach (var p in Profile.PlaylistsSnapshot())
            foreach (var id in p.Tracks)
                if (Library.Get(id) == null) gone.Add(id);
        // (the songs heard without saving them stay in the history and in the statistics: RadioViewModel keeps the recent
        // ones and the ones heard enough)
        var heard = Profile.Data.Radio.Select(s => s.Id).ToHashSet();
        foreach (var h in Profile.Data.History.ToList())
            if (!RadioSong.IsRadio(h.TrackId) && Library.Get(h.TrackId) == null) gone.Add(h.TrackId);
        foreach (var id in Profile.StatsSnapshot().Keys)
            if (Library.Get(id) == null && !heard.Contains(id)) gone.Add(id);
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
        if (page is DjViewModel && (InRoom && !ConfirmLeaveRoom(DjLeaveText(), L.T("Leave and open the DJ"))
                                    || !ConfirmUnlinkPhone(DjUnlinkText(), L.T("Disconnect and open the DJ"))))
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

    // Where "back" goes (the phone shows it under the finger that swipes back).
    public object? BackPage => _back.FirstOrDefault(p => p is not PlaylistPageViewModel pp || Profile.GetPlaylist(pp.Vm.Id) != null);

    private void GoBack()
    {
        while (_back.Count > 0)
        {
            var p = _back.Pop();
            if (p is PlaylistPageViewModel pp && Profile.GetPlaylist(pp.Vm.Id) == null) continue;
            if (p is HomeViewModel) Home.Refresh();
            // (a filter changed meanwhile)
            if (p is PlaylistPageViewModel playlist) playlist.EnsureFresh();
            else if (p is LibraryViewModel lib) lib.EnsureFresh();
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

    // The settings showing only what these words find (a download that needs a login: "cookie", the browser's cookies).
    public void SearchSettings(string words)
    {
        Settings.StartSearch = words;
        GoSettings();
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

    // "Download again / quality…" on songs with a link: the saved ones are downloaded again in the quality chosen (new files
    // in place of theirs), the ones in the cloud keep it for when they're heard and saved. The choice stays theirs.
    public async Task ChangeQuality(IReadOnlyList<TrackViewModel> songs)
    {
        var tracks = songs.Select(v => v.T).Where(t => t.HasLink && Library.Get(t.Id) == t).ToList();
        if (tracks.Count == 0)
        {
            Toast(L.T("These songs have no link to download them again from."));
            return;
        }
        var s = Host.Settings;
        var first = tracks[0];
        bool saved = tracks.Any(t => t.HasSavedFile);
        var plan = new QualityPlan(
            saved ? L.T("Download again") : L.T("Listening quality"),
            tracks.Count == 1 ? first.Title + (string.IsNullOrWhiteSpace(first.Artist) ? "" : " · " + first.Artist) : L.F("{0} songs", tracks.Count),
            Quality.For(first, s.AudioFormat), Quality.VideoFor(first, s.VideoMaxRes),
            canVideo: Engines.VideoDownloads && saved && tracks.Any(t => t.HasVideo || VideoFinder.OwnVideo(t) != null), withVideo: tracks.Any(t => t.HasVideo), saved);
        if (!await Dialogs.QualityAsync(plan)) return;
        string? audio = Equals(plan.Audio.Value, s.AudioFormat) ? null : plan.Audio.Value as string;
        int? video = Equals(plan.Video.Value, s.VideoMaxRes) ? null : plan.Video.Value as int? ?? 0;
        foreach (var t in tracks)
        {
            t.AudioQuality = audio;
            if (plan.CanVideo && plan.WithVideo) t.VideoRes = video;
            Library.Changed(t);
            if (!t.HasSavedFile) Radio.Restream(t);
        }
        var again = tracks.Where(t => t.HasSavedFile).ToList();
        int n = again.Count > 0 ? Host.Downloads.Redownload(Profile, again, plan.CanVideo && plan.WithVideo) : 0;
        Toast(n > 0 ? L.Count(n, "The song is being downloaded again: see Downloads", "{0} songs are being downloaded again: see Downloads") : L.T("Quality saved"));
    }

    // A result of the online search: in a room it goes straight into the room's queue (like a double click on a song of
    // yours there), otherwise its card on the download page, ready at once.
    public void OpenHit(SearchHit hit)
    {
        if (InRoom && Together.CanAdd)
        {
            Together.AddFromLink(new[] { OnlineSearchServices.ToItem(hit) });
            Toast(L.F("“{0}” goes into the room's queue", hit.Title));
            return;
        }
        // (from the search page it's like StartDownload: the search stays as it was, to go back to it)
        if (Page == Search)
        {
            _back.Push(Search);
            Page = Downloads;
            OnNavChanged();
            CommandManager.InvalidateRequerySuggested();
        }
        else Navigate(Downloads);
        Downloads.ShowHit(hit);
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

    // Its songs the "on the device / in the cloud" filter shows (the same as on its page).
    private void PlayPlaylist(PlaylistViewModel vm)
    {
        var page = new PlaylistPageViewModel(vm, this);
        if (page.Storage.HidesAll) Toast(page.Storage.EmptyTitle);
        else Player.PlayAll(page, Player.Shuffle);
    }

    public string NewPlaylistName() => L.F("My playlist #{0}", Playlists.Count);

    // The dialogs are awaited: on Android they can't hold the code up (on Windows they're the usual modal ones).
    public async Task<Playlist?> NewPlaylist(TrackViewModel? with)
    {
        var name = await Dialogs.PromptAsync(L.T("New playlist"), L.T("Playlist name"), NewPlaylistName());
        if (string.IsNullOrWhiteSpace(name)) return null;
        var p = Profile.CreatePlaylist(name);
        if (with != null && SaveRoomSong(with, p)) return p;
        if (with != null)
        {
            Profile.AddTrack(p, KeepInLibrary(with).Id);
            Toast(L.F("Added to “{0}”", p.Name));
        }
        else if (Playlists.FirstOrDefault(x => x.Id == p.Id) is { } vm) OpenPlaylist(vm);
        return p;
    }

    public async Task RenamePlaylist(PlaylistViewModel vm)
    {
        var name = await Dialogs.PromptAsync(L.T("Rename playlist"), L.T("New name"), vm.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        Profile.Rename(vm.P, name);
    }

    // True once deleted (false: cancelled).
    public async Task<bool> DeletePlaylist(PlaylistViewModel vm)
    {
        if (vm.IsFavorites) return false;
        if (!await Dialogs.ConfirmAsync(L.T("Delete this playlist?"), L.F("The playlist “{0}” will be deleted. The songs stay in your library.", vm.Name), L.T("Delete"), true)) return false;
        Profile.DeletePlaylist(vm.P);
        return true;
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
        else Toast(L.T("Invalid or unreadable image."));
    }

    // ------------------------------------------------------------------ track actions

    // A song of a room that isn't in the library: saved there first (its file is already here), then used as asked.
    private bool SaveRoomSong(TrackViewModel t, Playlist? p)
    {
        if (_together == null || !_together.IsStandIn(t)) return false;
        if (_together.ItemFor(t) is { } item) _ = _together.Save(item, p);
        return true;
    }

    // A suggested song (or one of a link heard without saving it) goes into the library before going into a playlist, the
    // favourites or getting a tag: in the cloud, nothing downloaded. The library song (the same one for the others).
    public TrackViewModel KeepInLibrary(TrackViewModel t)
    {
        if (Radio.Item(t.Id) is not { } suggested) return t;
        var kept = Vm(Radio.Keep(suggested, null));
        // A file of a phone's own (Connect phone), with no link to hear it from again: saved on this computer at once.
        if (!kept.T.HasLink && !kept.T.IsSaved) _ = SaveToDevice(new[] { kept }, quiet: true);
        return kept;
    }

    private List<TrackViewModel> KeepInLibrary(IReadOnlyList<TrackViewModel> tracks)
        => tracks.Where(t => _together == null || !_together.IsStandIn(t)).Select(KeepInLibrary).Distinct().ToList();

    // The library icon of a song not in the library (next to the heart, in its menu): into it, in the cloud, and said so.
    public TrackViewModel AddToLibrary(TrackViewModel t)
    {
        var kept = KeepInLibrary(t);
        if (kept != t) Toast(L.F("“{0}” added to your library, in the cloud: save it on your device whenever you like", kept.Title));
        return kept;
    }

    public void ToggleFavorite(TrackViewModel t)
    {
        if (SaveRoomSong(t, Profile.Favorites)) return;
        t = KeepInLibrary(t);
        var fav = Profile.Favorites;
        if (Profile.Contains(fav, t.Id))
        {
            Profile.RemoveTrack(fav, t.Id);
            Toast(L.T("Removed from Favorites"));
        }
        else
        {
            Profile.AddTrack(fav, t.Id);
            Toast(L.T("Added to Favorites ♥"));
        }
        t.RefreshFavorite();
    }

    public void AddToPlaylist(TrackViewModel t, Playlist p)
    {
        if (SaveRoomSong(t, p)) return;
        t = KeepInLibrary(t);
        var name = PlaylistViewModel.DisplayName(p);
        Toast(Profile.AddTrack(p, t.Id) ? L.F("Added to “{0}”", name) : L.F("Already in “{0}”", name));
    }

    public void RemoveFromPlaylist(TrackViewModel t, Playlist p)
    {
        Profile.RemoveTrack(p, t.Id);
        Toast(L.F("Removed from “{0}”", PlaylistViewModel.DisplayName(p)));
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
        tracks = KeepInLibrary(tracks);
        int added = Profile.AddTracks(p, tracks.Select(t => t.Id));
        var name = PlaylistViewModel.DisplayName(p);
        Toast(added == 0 ? L.F("They were all already in “{0}”", name) : added == 1 ? L.F("1 song added to “{0}”", name) : L.F("{0} songs added to “{1}”", added, name));
    }

    // True once made (false: cancelled).
    public async Task<bool> NewPlaylistWith(IReadOnlyList<TrackViewModel> tracks)
    {
        var name = await Dialogs.PromptAsync(L.T("New playlist"), L.T("Playlist name"), NewPlaylistName());
        if (string.IsNullOrWhiteSpace(name)) return false;
        var p = Profile.CreatePlaylist(name);
        AddToPlaylist(tracks, p);
        return true;
    }

    public void RemoveFromPlaylist(IReadOnlyList<TrackViewModel> tracks, Playlist p)
    {
        Profile.RemoveTracks(p, tracks.Select(t => t.Id));
        var name = PlaylistViewModel.DisplayName(p);
        Toast(tracks.Count == 1 ? L.F("Removed from “{0}”", name) : L.F("{0} songs removed from “{1}”", tracks.Count, name));
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
        Toast(tracks.Count == 1 ? L.F("“{0}” added to queue", tracks[0].Title) : L.F("{0} songs added to queue", tracks.Count));
    }

    public void PlaySelection(IReadOnlyList<TrackViewModel> tracks)
    {
        if (tracks.Count == 0) return;
        if (InRoom)
        {
            Together.Add(tracks);
            return;
        }
        Player.PlayFrom(new TrackListSource("selection", L.T("Selected songs"), tracks), tracks[0], alwaysQueue: true);
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
        if (!t.T.IsSaved) { Toast(L.F("“{0}” isn't saved on your device: you listen to it from its link.", t.Title)); return; }
        if (!File.Exists(t.T.Path)) { Toast(L.T("The file no longer exists.")); return; }
        Ui.ShowInFolder(t.T.Path);
    }

    // ------------------------------------------------------------------ saved on the device or in the cloud

    // The cover of a song added to the cloud, from the site's pictures (in the background).
    public async Task FetchCover(Track t, IReadOnlyList<string> pictures)
    {
        if (t.HasCover || pictures.Count == 0) return;
        if (await CloudSongs.MakeCoverAsync(t.Id, pictures) == null || Library.Get(t.Id) != t) return;
        t.HasCover = true;
        t.CoverVersion++;
        Library.Changed(t);
    }

    // Saves the songs on the device: a suggested one goes into the library first; one just heard is copied from the
    // cache at once; the others are downloaded (their rows show how far). quiet: no message when there's nothing to do.
    public async Task SaveToDevice(IReadOnlyList<TrackViewModel> tracks, bool quiet = false)
    {
        var todo = new List<Track>();
        foreach (var vm in tracks)
        {
            if (SaveRoomSong(vm, null)) continue;
            var t = KeepInLibrary(vm).T;
            if (Library.Get(t.Id) == t && !t.HasSavedFile && t.HasLink && !todo.Contains(t)) todo.Add(t);
        }
        if (todo.Count == 0)
        {
            if (!quiet && tracks.Count > 0)
                Toast(tracks.Count == 1 ? L.F("“{0}” is already saved on your device", tracks[0].Title) : L.T("They're all already saved on your device"));
            return;
        }
        int copied = 0;
        var download = new List<Track>();
        foreach (var t in todo)
        {
            var cached = t.CachePath is { } c && File.Exists(c) ? c : Host.SongCache.Find(t.Keys);
            if (cached != null && await SaveFromCache(t, cached)) copied++;
            else download.Add(t);
        }
        int queued = download.Count > 0 ? Queue.Save(Profile, download) : 0;
        if (quiet && queued == 0) return;
        if (todo.Count == 1)
            Toast(copied == 1 ? L.F("“{0}” saved on your device", todo[0].Title) : L.F("Saving “{0}” on your device…", todo[0].Title));
        else if (queued == 0) Toast(L.F("{0} songs saved on your device", copied));
        else Toast(copied > 0 ? L.F("{0} songs saved, {1} downloading", copied, queued) : L.F("Saving {0} songs on your device…", queued));
    }

    // The copy heard from the cache becomes the song's saved file (cover and analysis only if it lacks them).
    internal async Task<bool> SaveFromCache(Track t, string cached)
    {
        try
        {
            var dir = Host.Settings.MusicDir;
            Directory.CreateDirectory(dir);
            var name = Text.SafeFileName(string.IsNullOrWhiteSpace(t.Artist) ? t.Title : $"{t.Artist.Split(',')[0].Trim()} - {t.Title}");
            var target = Text.UniquePath(dir, name, Path.GetExtension(cached).TrimStart('.'));
            await Task.Run(() => File.Copy(cached, target));
            if (!t.HasCover && await AudioAnalysis.ExtractCoverAsync(target, AppPaths.TrackCover(t.Id), false, CancellationToken.None))
            {
                t.HasCover = true;
                t.CoverVersion++;
            }
            if (t.Wave == null || t.Loudness == null)
            {
                try
                {
                    var a = await AudioAnalysis.AnalyzeAsync(target, CancellationToken.None);
                    t.Wave = a.Wave;
                    t.Loudness = a.Loudness;
                    t.Peak = a.Peak;
                    if (a.Duration > 0) t.Duration = a.Duration;
                }
                catch { }
            }
            t.Path = target;
            t.Unavailable = false;
            Library.Changed(t);
            // (it plays on from the cache's copy if it's playing: that one goes once it's no longer open)
            if (Host.SongCache.Contains(cached)) Host.SongCache.Forget(cached);
            t.CachePath = null;
            return true;
        }
        catch { return false; }
    }

    // The audio (and video) of the songs leaves the device: they stay in the library, the playlists and the statistics, in
    // the cloud, heard from their link. Songs without a link, or files of the computer's own, aren't touched. The song
    // playing goes on from a copy in the cache.
    public async Task RemoveFromDevice(IReadOnlyList<TrackViewModel> tracks)
    {
        var files = new List<string>();
        long bytes = 0;
        var done = new List<TrackViewModel>();
        foreach (var vm in tracks)
        {
            var t = vm.T;
            if (Library.Get(t.Id) != t || !t.CanUnsave) continue;
            if (Player.OpenPath is { } open && string.Equals(open, t.Path, StringComparison.OrdinalIgnoreCase) && File.Exists(t.Path))
            {
                try
                {
                    var tmp = Path.Combine(Host.SongCache.TempDir, Ids.New() + Path.GetExtension(t.Path));
                    await Task.Run(() => File.Copy(t.Path, tmp));
                    var cached = Host.SongCache.Adopt(t.Keys, t.Title, t.Artist, tmp);
                    t.CachePath = cached;
                    await Player.SwapToCache(t, cached);
                }
                catch { }
            }
            bytes += SizeOf(t.Path);
            files.Add(t.Path);
            if (t.HasOwnVideo)
            {
                bytes += SizeOf(t.VideoPath);
                files.Add(t.VideoPath!);
            }
            t.Path = "";
            t.VideoPath = null;
            Library.Changed(t);
            done.Add(vm);
        }
        if (done.Count == 0) return;
        Toast(done.Count == 1
            ? L.F("“{0}” removed from your device: it stays in your library, in the cloud", done[0].Title)
            : L.F("{0} songs removed from your device ({1} freed): they stay in your library, in the cloud", done.Count, Text.Size(bytes)));
        await DeleteFiles(files);
    }

    // Only the videos: the songs stay saved, with their audio.
    public async Task RemoveVideos(IReadOnlyList<TrackViewModel> tracks)
    {
        var files = new List<string>();
        int n = 0;
        foreach (var vm in tracks)
        {
            var t = vm.T;
            if (Library.Get(t.Id) != t || !t.HasOwnVideo) continue;
            if (!t.IsLocal) files.Add(t.VideoPath!);
            t.VideoPath = null;
            Library.Changed(t);
            n++;
        }
        if (n == 0) return;
        NowPlaying.Refresh();
        Toast(n == 1 ? L.T("Video deleted: the audio stays") : L.F("Videos of {0} songs deleted: the audio stays", n));
        await DeleteFiles(files);
    }

    public void DeleteLyrics(IReadOnlyList<TrackViewModel> tracks)
    {
        var with = tracks.Where(t => t.HasLyrics).ToList();
        if (with.Count == 1) DeleteLyrics(with[0]);
        else if (with.Count > 1)
        {
            foreach (var t in with) Host.Lyrics.Delete(t.T);
            Toast(L.F("Lyrics of {0} songs deleted", with.Count));
        }
    }

    // A video for a song saved without one: from its own link when that's a YouTube video, otherwise chosen among the
    // videos found on YouTube.
    public async Task FindVideo(TrackViewModel vm)
    {
        var t = vm.T;
        if (Library.Get(t.Id) != t || t.HasVideo) return;
        if (!t.IsSaved)
        {
            Toast(L.F("Save “{0}” on your device first: the video goes next to the audio.", t.Title));
            return;
        }
        if (VideoFinder.OwnVideo(t) is { } own)
        {
            Queue.AddVideo(Profile, t, new MediaItem { Url = own, PageUrl = own, Title = t.Title, Artist = t.Artist, Duration = t.Duration, Source = SourceKind.YtDlp, SiteName = Sites.NameFor(own) });
            Toast(L.F("Downloading the video of “{0}” from its link…", t.Title));
            return;
        }
        if (await Dialogs.PickVideoAsync(this, vm) is not { } pick) return;
        Queue.AddVideo(Profile, t, new MediaItem
        {
            Url = pick.Url, PageUrl = pick.Url, Title = t.Title, Artist = t.Artist, Duration = pick.Duration, Thumbnails = pick.Thumb != null ? new() { pick.Thumb } : new(),
            Source = SourceKind.YtDlp, SiteName = Sites.NameFor(pick.Url),
        });
        Toast(L.F("Downloading the video for “{0}”…", t.Title));
    }

    private static long SizeOf(string? path)
    {
        try { return path != null && File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    // The player may still hold a file: tried again for a few seconds.
    private static Task DeleteFiles(List<string> files) => Task.Run(async () =>
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

    // ------------------------------------------------------------------ "Delete…"

    public Task DeleteTrack(TrackViewModel t) => DeleteTracks(new[] { t });

    // What to delete of these songs: only the video, only the lyrics, the audio from the device (they stay in the cloud),
    // or everything (out of playlists and statistics too). Chosen in one window. True once done (false: the window was
    // closed without choosing, so a selection on the phone stays as it was).
    public async Task<bool> DeleteTracks(IReadOnlyList<TrackViewModel> tracks)
    {
        // The song the room is playing stays until it's over (its file is open); the room's own songs aren't yours.
        if (InRoom && Player.Current is { } playing && tracks.Contains(playing))
        {
            Toast(L.F("“{0}” is playing in the room: delete it once it's over.", playing.Title));
            tracks = tracks.Where(t => t != playing).ToList();
        }
        tracks = tracks.Where(t => Library.Get(t.Id) != null || Radio.Has(t.Id)).Distinct().ToList();
        if (tracks.Count == 0) return false;
        var plan = new RemovePlan(tracks);
        if (await Dialogs.RemoveAsync(plan) is not { } kind) return false;
        switch (kind)
        {
            case RemoveKind.Video:
                await RemoveVideos(tracks);
                break;
            case RemoveKind.Lyrics:
                DeleteLyrics(tracks);
                break;
            case RemoveKind.Device:
                await RemoveFromDevice(tracks);
                break;
            case RemoveKind.Everything:
                var heard = tracks.Where(t => Radio.Has(t.Id)).Select(t => t.Id).ToList();
                if (heard.Count > 0) await Radio.Forget(heard);
                var lib = tracks.Where(t => Library.Get(t.Id) != null).ToList();
                if (lib.Count > 0) await Delete(lib);
                else Toast(heard.Count == 1 ? L.F("“{0}” removed from your listening history", tracks[0].Title) : L.F("{0} songs removed from your listening history", heard.Count));
                break;
        }
        return true;
    }

    // Removes the song everywhere: files, cover, playlists, history, statistics, queue.
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
            if (!t.T.IsLocal) files.AddRange(new[] { t.T.Path, t.T.VideoPath }.OfType<string>().Where(f => f.Length > 0));
        }
        Toast(tracks.Count == 1 ? L.F("“{0}” deleted", tracks[0].Title) : L.F("{0} songs deleted", tracks.Count));
        await DeleteFiles(files);
    }

    private void OnSaving(string id)
    {
        if (_vms.TryGetValue(id, out var vm)) vm.RefreshSaving();
    }

    // Bulk edit: one artist renamed on every song.
    public void RenameArtist(string from, string to)
    {
        var changed = Library.RenameArtist(from, to);
        Toast(L.Count(changed.Count, "1 song updated", "{0} songs updated"));
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

    // The "on the device / in the cloud" filter changed: every library page and playlist follows (the hidden ones when shown).
    public void OnStorageFilterChanged()
    {
        LibraryPage.Rebuild();
        if (Page == UnsortedPage) UnsortedPage.Rebuild();
        else UnsortedPage.MarkDirty();
        foreach (var page in _tagPages.Values)
            if (page == Page) page.Rebuild();
            else page.MarkDirty();
        if (Page is PlaylistPageViewModel current) current.Rebuild();
        foreach (var behind in _back.OfType<PlaylistPageViewModel>()) behind.MarkDirty();
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
        if (songs == null && playlist == null) Toast(L.F("Tag “{0}” created", vm.Name));
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
            ? L.T("No song uses it.")
            : L.F("It will be removed from {0} and {1}.", L.Count(songs, "1 song", "{0} songs"), L.Count(lists, "1 playlist", "{0} playlists"));
        if (!await Dialogs.ConfirmAsync(L.T("Delete the tag?"), L.F("The tag “{0}” will be deleted.", t.Name) + " " + used, L.T("Delete"), true)) return;
        Profile.DeleteTag(t.T);
    }

    public void SetTag(IReadOnlyList<TrackViewModel> tracks, TagViewModel tag, bool on)
    {
        if (on) tracks = KeepInLibrary(tracks);
        int n = Profile.SetTag(tracks.Select(t => t.Id), tag.Id, on);
        if (tracks.Count == 1) Toast(on ? L.F("Tag “{0}” added", tag.Name) : L.F("Tag “{0}” removed", tag.Name));
        else if (n == 0) Toast(on ? L.F("They all had the tag “{0}” already", tag.Name) : L.F("None of them had the tag “{0}”", tag.Name));
        else Toast(on ? L.F("Tag “{0}” added to {1} songs", tag.Name, n) : L.F("Tag “{0}” removed from {1} songs", tag.Name, n));
    }

    public void SetPlaylistTag(PlaylistViewModel p, TagViewModel tag, bool on) => Profile.SetPlaylistTag(p.P, tag.Id, on);

    // A playlist's tags onto its songs (all, or the ones left ticked).
    public async Task TagPlaylistSongs(PlaylistViewModel p)
    {
        var songs = p.P.Tracks.Select(id => Library.Get(id)).Where(t => t != null).Select(t => Vm(t!)).ToList();
        if (songs.Count == 0) { Toast(L.T("This playlist is empty.")); return; }
        if (!HasTags && await NewTag() == null) return;
        var r = await Views.TagDialogs.ApplyToSongsAsync(p, songs);
        if (r == null) return;
        int changed = 0;
        foreach (var tag in r.Value.Tags) changed += Profile.SetTag(r.Value.Songs.Select(t => t.Id), tag.Id, r.Value.Add);
        Toast(changed == 0 ? L.T("No songs to change")
            : r.Value.Add ? L.F("Tags added: {0} changes", changed) : L.F("Tags removed: {0} changes", changed));
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
        var vm = Vm(t);
        vm.Refresh();
        if (Player.Current?.Id == t.Id)
        {
            Host.OnTrackChanged();
            Player.RefreshSaved();
            NowPlaying.Refresh();
        }
        foreach (var p in Playlists) if (p.P.Tracks.Contains(t.Id)) p.Refresh();
        // Saved, or back in the cloud: the counts of the lists and the ones showing only the device's songs follow.
        if (vm.WasSaved != t.IsSaved && Library.Get(t.Id) == t)
        {
            vm.WasSaved = t.IsSaved;
            _libraryDirty = true;
            ScheduleRefresh();
        }
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
        // Not on screen: the totals aren't counted again every second (PlayerViewModel.SetVisible catches up).
        if (Player is { Shown: false }) return;
        if (_stats != null && Page == _stats) _stats.OnListened(reorder);
        if (_settings != null && Page == _settings) _settings.OnListened(reorder);
    }

    public void OnCurrentChanged()
    {
        OnContextChanged();
        // A song never searched gets its lyrics while it plays (Settings > Lyrics).
        if (Player?.Current is { } c) Host.Lyrics.Auto(c.T, true);
    }

    // The list being played changed (also with the same song going on).
    public void OnContextChanged()
    {
        foreach (var p in Playlists) p.IsPlayingFrom = Player?.ContextId == "playlist:" + p.Id;
        NowPlaying?.Refresh();
    }

    // ------------------------------------------------------------------ lyrics

    public void SearchLyrics(IReadOnlyList<TrackViewModel> tracks)
    {
        if (tracks.Count == 0) return;
        Host.Lyrics.Search(tracks.Select(t => t.T).ToList());
        Toast(tracks.Count == 1 ? L.F("Looking for the lyrics of “{0}”…", tracks[0].Title) : L.F("Looking for the lyrics of {0} songs…", tracks.Count));
    }

    public void DeleteLyrics(TrackViewModel t)
    {
        Host.Lyrics.Delete(t.T);
        Toast(L.F("Lyrics of “{0}” deleted", t.Title));
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
            null => L.T("LRCLIB isn't answering: check your connection and try again."),
            LyricsKind.Synced => L.F("Lyrics found for “{0}”", t.Title),
            LyricsKind.Plain => L.F("Lyrics found for “{0}” (without timing)", t.Title),
            LyricsKind.Instrumental => L.F("“{0}” is instrumental: no lyrics", t.Title),
            _ when t.HasLyrics => L.F("No other lyrics found for “{0}”: the previous ones stay", t.Title),
            _ => L.F("No lyrics found for “{0}”", t.Title),
        });
    }

    private void OnLyricsBatchDone(Services.LyricsBatch b)
        => Toast(b.Failed == b.Total ? L.T("LRCLIB isn't answering: check your connection and try again.")
            : L.F("Lyrics: {0} found, {1} without lyrics", b.Found, b.Missing));

    // ------------------------------------------------------------------ import

    public async Task ImportDialog(bool folder)
    {
        List<string> paths;
        if (folder)
        {
            if (await Dialogs.PickFolderAsync(L.T("Choose a folder with your music")) is not { } dir) return;
            paths = new List<string> { dir };
        }
        else
        {
            if (await Dialogs.PickFilesAsync(L.T("Add songs from your computer"), true, (L.T("Audio and video"), Importer.Extensions)) is not { Length: > 0 } files) return;
            paths = files.ToList();
        }
        await Import(paths, false);
    }

    public async Task Import(IEnumerable<string> paths, bool playFirst)
    {
        var files = await Task.Run(() => Importer.Expand(paths));
        if (files.Count == 0)
        {
            Toast(L.T("No audio files found."));
            return;
        }
        Toast(files.Count == 1 ? L.T("Adding song…") : L.F("Adding {0} songs…", files.Count));
        int ok = 0;
        Track? first = null;
        foreach (var f in files)
        {
            try
            {
                if (AudioAnalysis.Native == null && Engines.Missing().Contains(Path.GetFileName(Engines.Ffmpeg))) await Host.EnsureEnginesAsync();
                var t = await Task.Run(() => Importer.ImportAsync(Library, f, CancellationToken.None));
                first ??= t;
                ok++;
            }
            catch { }
        }
        Toast(ok == files.Count ? L.Count(ok, "Song added to your library", "{0} songs added to your library")
                                : L.F("{0} of {1} songs added (the others can't be read)", ok, files.Count));
        // In a room a dropped file doesn't start: it's just in the library now.
        if (playFirst && first != null && !InRoom) Player.PlaySingle(first);
    }

    // ------------------------------------------------------------------ .ump packs

    private readonly Queue<object> _packs = new();
    private bool _packOpen;

    // A pack opened (double click, dragged in, Settings), or sent by the connected computer: one dialog at a time.
    public void OpenPack(PackFile pack) => OpenPack((object)pack);
    public void OpenPack(string path) => OpenPack((object)path);

    private async void OpenPack(object what)
    {
        _packs.Enqueue(what);
        if (_packOpen) return;
        _packOpen = true;
        try
        {
            // After the window has appeared: a double click on a pack can be what started the app.
            await _ui.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            while (_packs.TryDequeue(out var next)) await (next is PackFile p ? Views.PackDialogs.Import(this, p) : Views.PackDialogs.Import(this, (string)next));
        }
        finally { _packOpen = false; }
    }

    public async Task PickPack()
    {
        if (await Dialogs.PickFileAsync(L.T("Import a pack"), (L.T("Ultimate MP3 Player pack") + " (*" + Pack.Extension + ")", new[] { Pack.Extension })) is { } file)
            OpenPack(file);
    }

    public void ExportPack(PlaylistViewModel? playlist = null, TagViewModel? tag = null) => Views.PackDialogs.Export(this, playlist, tag);

    // ------------------------------------------------------------------ several playlists at once

    // Their songs, in order, each once.
    public List<TrackViewModel> SongsOf(IEnumerable<PlaylistViewModel> lists) => lists.SelectMany(p => p.Songs).Distinct().ToList();

    public void PlayPlaylists(IReadOnlyList<PlaylistViewModel> lists)
    {
        var songs = SongsOf(lists);
        if (songs.Count == 0) Toast(L.T("These playlists are empty."));
        else PlaySelection(songs);
    }

    public void EnqueuePlaylists(IReadOnlyList<PlaylistViewModel> lists)
    {
        var songs = SongsOf(lists);
        if (songs.Count > 0) Enqueue(songs);
    }

    public void ExportPlaylists(IReadOnlyList<PlaylistViewModel> lists) => Views.PackDialogs.Export(this, many: lists);

    // Their songs in the cloud saved on the device.
    public Task SavePlaylists(IReadOnlyList<PlaylistViewModel> lists) => SaveToDevice(SongsOf(lists).Where(t => t.IsCloud).ToList());

    // True once deleted (false: cancelled).
    public async Task<bool> DeletePlaylists(IReadOnlyList<PlaylistViewModel> lists)
    {
        var del = lists.Where(p => !p.IsFavorites).ToList();
        if (del.Count == 0) return false;
        if (del.Count == 1) return await DeletePlaylist(del[0]);
        var names = string.Join(", ", del.Take(4).Select(p => "«" + p.Name + "»")) + (del.Count > 4 ? "…" : "");
        if (!await Dialogs.ConfirmAsync(L.F("Delete {0} playlists?", del.Count), L.F("{0} will be deleted. The songs stay in your library.", names), L.T("Delete"), true)) return false;
        foreach (var p in del) Profile.DeletePlaylist(p.P);
        Toast(L.F("{0} playlists deleted", del.Count));
        return true;
    }

    // A tag on (or off) every one of these playlists.
    public void SetPlaylistsTag(IReadOnlyList<PlaylistViewModel> lists, TagViewModel tag, bool on)
    {
        foreach (var p in lists) Profile.SetPlaylistTag(p.P, tag.Id, on);
    }

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
#if ANDROID_APP || IOS_APP
        Platform.Files.OpenUrl(url);
#else
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
#endif
    }
}
