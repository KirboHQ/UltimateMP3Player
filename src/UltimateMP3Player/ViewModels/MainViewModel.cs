using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
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
        GoNowPlayingCommand = new RelayCommand(() => { if (Page == NowPlaying) GoBack(); else Navigate(NowPlaying); });
        BackCommand = new RelayCommand(GoBack, () => _back.Count > 0);
        NewPlaylistCommand = new RelayCommand(() => NewPlaylist(null));
        OpenPlaylistCommand = new RelayCommand(p => { if (p is PlaylistViewModel vm) OpenPlaylist(vm); });
        PlayPlaylistCommand = new RelayCommand(p => { if (p is PlaylistViewModel vm) PlayPlaylist(vm); });
        PlayTrackCommand = new RelayCommand(p => { if (p is TrackViewModel t) Home.PlayRecent(t); });
        SubmitSearchCommand = new RelayCommand(SubmitSearch);
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        PasteLinkCommand = new RelayCommand(PasteLink);
        SwitchProfileCommand = new RelayCommand(() => Host.SwitchProfile());
        ImportCommand = new RelayCommand(() => ImportDialog(false));

        _added = t => _ui.BeginInvoke(() => OnTrackAdded(t));
        _changed = t => _ui.BeginInvoke(() => OnTrackChanged(t));
        _removed = t => _ui.BeginInvoke(() => OnTrackRemoved(t));
        _playlistChanged = p => _ui.BeginInvoke(() => OnPlaylistChanged(p));
        _playlistsChanged = () => _ui.BeginInvoke(RebuildPlaylists);
        host.Library.TrackAdded += _added;
        host.Library.TrackChanged += _changed;
        host.Library.TrackRemoved += _removed;
        profile.PlaylistChanged += _playlistChanged;
        profile.PlaylistsChanged += _playlistsChanged;
    }

    private readonly Action<Track> _added, _changed, _removed;
    private readonly Action<Playlist> _playlistChanged;
    private readonly Action _playlistsChanged;

    public AppHost Host { get; }
    public Profile Profile { get; }
    public Library Library => Host.Library;
    public PlayerViewModel Player { get; }
    public HomeViewModel Home { get; }
    public LibraryViewModel LibraryPage { get; }
    public LibraryViewModel UnsortedPage { get; }
    public SearchViewModel Search { get; }
    public DownloadsPageViewModel Downloads { get; }
    public NowPlayingViewModel NowPlaying { get; }
    private SettingsViewModel? _settings;
    public SettingsViewModel Settings => _settings ??= new SettingsViewModel(this);
    public DownloadQueue Queue => Host.Downloads;

    public string ProfileName => Profile.Info.Name;
    public string ProfileInitial => Profile.Info.Initial;
    public System.Windows.Media.Brush ProfileBrush => Ui.BrushFrom(Profile.Info.Color);
    public System.Windows.Media.ImageSource? ProfileAvatar =>
        Profile.Info.HasAvatar ? Images.Decode(Profile.Info.AvatarPath, 96) : null;
    public bool MultipleProfiles => Host.Profiles.Profiles.Count > 1;

    public ICommand GoHomeCommand { get; }
    public ICommand GoLibraryCommand { get; }
    public ICommand GoDownloadsCommand { get; }
    public ICommand GoSettingsCommand { get; }
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
        Player.Detach();
        Downloads.Detach();
        _refreshTimer.Stop();
        _searchTimer.Stop();
        _toastTimer.Stop();
        Host.Library.TrackAdded -= _added;
        Host.Library.TrackChanged -= _changed;
        Host.Library.TrackRemoved -= _removed;
        Profile.PlaylistChanged -= _playlistChanged;
        Profile.PlaylistsChanged -= _playlistsChanged;
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
        if (gone.Count > 0) Profile.ForgetTracks(gone);
    }

    // ------------------------------------------------------------------ navigation

    private object _page;
    public object Page
    {
        get => _page;
        private set
        {
            if (!Set(ref _page, value)) return;
            Player.UpNextVisible = value == NowPlaying;
            foreach (var p in Playlists) p.IsSelected = value is PlaylistPageViewModel pp && pp.Vm == p;
            OnChanged(nameof(Section), nameof(IsNowPlaying));
            if (value == NowPlaying) NowPlaying.Refresh();
        }
    }

    public string Section => Page switch
    {
        HomeViewModel => "home",
        LibraryViewModel { Unsorted: true } => "unsorted",
        LibraryViewModel => "library",
        DownloadsPageViewModel => "downloads",
        SettingsViewModel => "settings",
        NowPlayingViewModel => "nowplaying",
        SearchViewModel => "search",
        _ => "",
    };

    public bool IsHome { get => Section == "home"; set { if (value) Navigate(Home); } }
    public bool IsLibrary { get => Section == "library"; set { if (value) Navigate(LibraryPage); } }
    public bool IsUnsorted { get => Section == "unsorted"; set { if (value) Navigate(UnsortedPage); } }
    public bool IsDownloads { get => Section == "downloads"; set { if (value) Navigate(Downloads); } }
    public bool IsSettings { get => Section == "settings"; set { if (value) GoSettings(); } }
    public bool IsNowPlaying => Section == "nowplaying";

    public void Navigate(object page)
    {
        if (page == Page) return;
        if (page is HomeViewModel) Home.Refresh();
        if (page == UnsortedPage) UnsortedPage.Rebuild();
        if (Page is not SearchViewModel || page is SearchViewModel) _back.Push(Page);
        if (_back.Count > 30) { var keep = _back.Take(30).Reverse().ToList(); _back.Clear(); foreach (var k in keep) _back.Push(k); }
        Page = page;
        OnNavChanged();
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnNavChanged() => OnChanged(nameof(IsHome), nameof(IsLibrary), nameof(IsUnsorted), nameof(IsDownloads), nameof(IsSettings));

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
        else RunSearch();
    }

    public void StartDownload(string url)
    {
        Navigate(Downloads);
        _ = Downloads.AnalyzeAsync(url);
        _searchText = "";
        OnChanged(nameof(SearchText), nameof(IsLink), nameof(HasSearchText));
    }

    private void PasteLink()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText().Trim();
            if (LooksLikeLink(text)) StartDownload(text);
            else SearchText = text;
        }
        catch { }
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
    }

    public void OpenPlaylist(PlaylistViewModel vm)
    {
        if (Page is PlaylistPageViewModel current && current.Vm.Id == vm.Id) return;
        Navigate(new PlaylistPageViewModel(vm, this));
    }

    private void PlayPlaylist(PlaylistViewModel vm) => Player.PlayAll(new PlaylistPageViewModel(vm, this), Player.Shuffle);

    private string NewPlaylistName() => L.F("La mia playlist n. {0}", Playlists.Count);

    public Playlist? NewPlaylist(TrackViewModel? with)
    {
        var name = Dialogs.Prompt(L.T("Nuova playlist"), L.T("Nome della playlist"), NewPlaylistName());
        if (string.IsNullOrWhiteSpace(name)) return null;
        var p = Profile.CreatePlaylist(name);
        if (with != null)
        {
            Profile.AddTrack(p, with.Id);
            Toast(L.F("Aggiunto a «{0}»", p.Name));
        }
        else if (Playlists.FirstOrDefault(x => x.Id == p.Id) is { } vm) OpenPlaylist(vm);
        return p;
    }

    public void RenamePlaylist(PlaylistViewModel vm)
    {
        var name = Dialogs.Prompt(L.T("Rinomina playlist"), L.T("Nuovo nome"), vm.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        Profile.Rename(vm.P, name);
    }

    public void DeletePlaylist(PlaylistViewModel vm)
    {
        if (vm.IsFavorites) return;
        if (!Dialogs.Confirm(L.T("Eliminare la playlist?"), L.F("La playlist «{0}» verrà eliminata. I brani restano nella libreria.", vm.Name), L.T("Elimina"), true)) return;
        Profile.DeletePlaylist(vm.P);
    }

    public void ChangePlaylistCover(PlaylistViewModel vm)
    {
        var file = PickImage();
        if (file == null) return;
        _ = SetCover(file, Profile.PlaylistCover(vm.P), () => Profile.SetPlaylistCover(vm.P, true));
    }

    public void RemovePlaylistCover(PlaylistViewModel vm)
    {
        try { File.Delete(Profile.PlaylistCover(vm.P)); } catch { }
        Profile.SetPlaylistCover(vm.P, false);
    }

    private static string? PickImage()
    {
        var dlg = new OpenFileDialog
        {
            Title = L.T("Scegli un'immagine"),
            Filter = L.T("Immagini") + "|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif;*.jfif;*.avif|" + L.T("Tutti i file") + "|*.*",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private async Task SetCover(string image, string target, Action done)
    {
        bool ok = await Task.Run(() => AudioAnalysis.MakeCoverFromImageAsync(image, target));
        if (ok) done();
        else Toast(L.T("Immagine non valida o non leggibile."));
    }

    // ------------------------------------------------------------------ track actions

    public void ToggleFavorite(TrackViewModel t)
    {
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
        var name = PlaylistViewModel.DisplayName(p);
        Toast(Profile.AddTrack(p, t.Id) ? L.F("Aggiunto a «{0}»", name) : L.F("È già in «{0}»", name));
    }

    public void RemoveFromPlaylist(TrackViewModel t, Playlist p)
    {
        Profile.RemoveTrack(p, t.Id);
        Toast(L.F("Rimosso da «{0}»", PlaylistViewModel.DisplayName(p)));
    }

    // Plays a song within "All songs" (downloads page).
    public void PlayInLibrary(TrackViewModel t)
    {
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

    public void NewPlaylistWith(IReadOnlyList<TrackViewModel> tracks)
    {
        var name = Dialogs.Prompt(L.T("Nuova playlist"), L.T("Nome della playlist"), NewPlaylistName());
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
        foreach (var t in tracks) Player.Enqueue(t, quiet: true);
        Toast(tracks.Count == 1 ? L.F("«{0}» aggiunto alla coda", tracks[0].Title) : L.F("{0} brani aggiunti alla coda", tracks.Count));
    }

    public void PlaySelection(IReadOnlyList<TrackViewModel> tracks)
    {
        if (tracks.Count == 0) return;
        Player.PlayFrom(new TrackListSource("selection", L.T("Brani selezionati"), tracks), tracks[0]);
    }

    public void EditTrack(TrackViewModel t)
    {
        var r = Dialogs.EditTrack(t.T);
        if (r == null) return;
        t.T.Title = string.IsNullOrWhiteSpace(r.Value.Title) ? t.T.Title : r.Value.Title.Trim();
        t.T.Artist = string.IsNullOrWhiteSpace(r.Value.Artist) ? null : r.Value.Artist.Trim();
        t.T.Album = string.IsNullOrWhiteSpace(r.Value.Album) ? null : r.Value.Album.Trim();
        Library.Changed(t.T);
    }

    public void ChangeTrackCover(TrackViewModel t)
    {
        var file = PickImage();
        if (file == null) return;
        _ = SetCover(file, AppPaths.TrackCover(t.Id), () =>
        {
            t.T.HasCover = true;
            t.T.CoverVersion++;
            Library.Changed(t.T);
        });
    }

    public void ShowInFolder(TrackViewModel t)
    {
        if (!File.Exists(t.T.Path)) { Toast(L.T("Il file non esiste più.")); return; }
        try { Process.Start("explorer.exe", $"/select,\"{t.T.Path}\""); } catch { }
    }

    public void DeleteTrack(TrackViewModel t) => DeleteTracks(new[] { t });

    public void DeleteTracks(IReadOnlyList<TrackViewModel> tracks)
    {
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
        if (!Dialogs.Confirm(tracks.Count == 1 ? L.T("Eliminare il brano?") : L.F("Eliminare {0} brani?", tracks.Count), msg, L.T("Elimina"), true)) return;
        _ = Delete(tracks.ToList());
    }

    // Removes the song everywhere: files, cover, playlists, history, queue.
    private async Task Delete(List<TrackViewModel> tracks)
    {
        await Player.RemoveTracks(tracks.Select(t => t.Id).ToHashSet());
        var files = new List<string>();
        foreach (var t in tracks)
        {
            Library.Remove(t.T);
            files.Add(AppPaths.TrackCover(t.Id));
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

    public void OnCurrentChanged()
    {
        foreach (var p in Playlists) p.IsPlayingFrom = Player?.ContextId == "playlist:" + p.Id;
        NowPlaying?.Refresh();
    }

    // ------------------------------------------------------------------ import

    public void ImportDialog(bool folder)
    {
        List<string> paths;
        if (folder)
        {
            var dlg = new OpenFolderDialog { Title = L.T("Scegli una cartella con la tua musica") };
            if (dlg.ShowDialog() != true) return;
            paths = new List<string> { dlg.FolderName };
        }
        else
        {
            var dlg = new OpenFileDialog
            {
                Title = L.T("Aggiungi brani dal computer"),
                Multiselect = true,
                Filter = L.T("Audio e video") + "|" + string.Join(";", Importer.Extensions.Select(e => "*" + e)) + "|" + L.T("Tutti i file") + "|*.*",
            };
            if (dlg.ShowDialog() != true) return;
            paths = dlg.FileNames.ToList();
        }
        _ = Import(paths, false);
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
                if (Engines.Missing().Contains("ffmpeg.exe")) await Host.EnsureEnginesAsync();
                var t = await Task.Run(() => Importer.ImportAsync(Library, f, CancellationToken.None));
                first ??= t;
                ok++;
            }
            catch { }
        }
        Toast(ok == files.Count ? L.Count(ok, "Brano aggiunto alla libreria", "{0} brani aggiunti alla libreria")
                                : L.F("{0} di {1} brani aggiunti (gli altri non sono leggibili)", ok, files.Count));
        if (playFirst && first != null) Player.PlaySingle(first);
    }

    public static void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch { }
    }
}
