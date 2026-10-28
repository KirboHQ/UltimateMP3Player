using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// Artists' pictures: one chosen by hand, else Deezer's, kept on the disk (covers/artists) once seen, so the lists show them
// offline too.
public static class ArtistPictures
{
    private static readonly HashSet<string> Looked = new();

    // Chosen on the artist's page (Change image), kept under its name: it wins over Deezer's.
    public static string Mine(string name) => AppPaths.ArtistPicture("my-" + Key(name));
    public static bool HasMine(string name) => File.Exists(Mine(name));
    // Deezer's for an artist known only by its name (the ones listened to most).
    private static string Named(string name) => AppPaths.ArtistPicture("name-" + Key(name));
    private static string Key(string name) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Text.Normalize(name))))[..16].ToLowerInvariant();

    public static async Task<ImageSource?> LoadAsync(string name, string? id, string? url, int size)
    {
        var mine = Mine(name);
        if (File.Exists(mine)) return await Images.LoadAsync(mine, size, (int)File.GetLastWriteTimeUtc(mine).Ticks);
        if (id == null && url == null)
        {
            var named = Named(name);
            if (!File.Exists(named))
            {
                // (looked for once a session: Deezer may not know it)
                if (!Looked.Add(Text.Normalize(name))) return null;
                try
                {
                    if (await OnlineSearchServices.ArtistInfoAsync(name, CancellationToken.None) is not { Picture: { } found }) return null;
                    await File.WriteAllBytesAsync(named, await Http.GetBytesAsync(found));
                }
                catch { return null; }
            }
            return await Images.LoadAsync(named, size, 0);
        }
        if (id == null) return await WebImages.LoadAsync(new[] { url! }, size);
        var path = AppPaths.ArtistPicture(id);
        if (!File.Exists(path))
        {
            if (url == null) return null;
            try
            {
                var bytes = await Http.GetBytesAsync(url);
                await File.WriteAllBytesAsync(path, bytes);
            }
            catch { return null; }
        }
        return await Images.LoadAsync(path, size, 0);
    }

    // A renamed artist keeps the picture chosen for it.
    public static void Rename(string from, string to)
    {
        try
        {
            if (File.Exists(Mine(from))) File.Move(Mine(from), Mine(to), true);
        }
        catch { }
    }

    // Who has no picture: the initial on a colour of its own (always the same for the same name).
    public static Brush BrushFor(string name) => Ui.BrushFrom(ColorFor(name));

    public static string ColorFor(string name)
    {
        int h = 0;
        foreach (var c in Text.Normalize(name)) h = h * 31 + c;
        var palette = ProfileInfo.Palette;
        return palette[(h & 0x7fffffff) % palette.Length];
    }

    public static string Initial(string name) => name.Trim() is { Length: > 0 } n ? char.ToUpper(n[0]).ToString() : "?";
}

// An artist in a grid (followed, suggested, found): its picture in a circle, its name; a tap opens its page.
public sealed class ArtistCardViewModel : Observable
{
    private readonly MainViewModel _main;
    private readonly string? _pictureUrl;
    private bool _requested;
    private ImageSource? _picture;

    public ArtistCardViewModel(MainViewModel main, string name, string? deezerId, string? pictureUrl, string? subtitle = null)
    {
        _main = main;
        Name = name;
        DeezerId = deezerId;
        _pictureUrl = pictureUrl;
        Subtitle = subtitle;
        OpenCommand = new RelayCommand(() => _main.OpenArtist(Name, DeezerId));
        FollowCommand = new RelayCommand(() => _ = _main.Artists.ToggleFollowAsync(Name, DeezerId, _pictureUrl));
    }

    public string Name { get; }
    public string? DeezerId { get; }
    public string? Subtitle { get; }
    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);
    public string Initial => ArtistPictures.Initial(Name);
    public Brush Brush => ArtistPictures.BrushFor(Name);
    public ICommand OpenCommand { get; }
    public ICommand FollowCommand { get; }
    public bool IsFollowed => _main.Profile.FollowedArtist(Name) != null;
    public string FollowGlyph => IsFollowed ? "\uE73E" : "\uE710";
    public string FollowTip => IsFollowed ? L.T("Stop following") : L.T("Follow");
    // A release of its, not seen yet.
    public bool HasNew => _main.Profile.ReleasesSnapshot().Any(r => !r.Seen && Text.Normalize(r.Artist) == Text.Normalize(Name));

    public ImageSource? Picture
    {
        get
        {
            if (!_requested)
            {
                _requested = true;
                _ = Load();
            }
            return _picture;
        }
    }

    private async Task Load()
    {
        _picture = await ArtistPictures.LoadAsync(Name, DeezerId, _pictureUrl, 320);
        OnChanged(nameof(Picture), nameof(HasPicture));
    }

    public bool HasPicture => _picture != null;
    public void Recheck() => OnChanged(nameof(IsFollowed), nameof(FollowGlyph), nameof(FollowTip), nameof(HasNew));
}

// A new album, EP or single: its cover; a tap opens it like its link (its songs, to add).
public sealed class ReleaseCardViewModel : Observable
{
    private readonly MainViewModel _main;
    private bool _requested;
    private ImageSource? _cover;

    public ReleaseCardViewModel(MainViewModel main, Release r)
    {
        _main = main;
        Release = r;
        OpenCommand = new RelayCommand(() => _main.OpenHit(new SearchHit
        {
            Service = OnlineSearchServices.Deezer, Title = r.Title, Artist = r.Artist, Url = r.Url, Thumb = r.Cover, Collection = "album", Year = r.Date.Year.ToString(),
        }));
        ArtistCommand = new RelayCommand(() => _main.OpenArtist(r.Artist, null));
    }

    public Release Release { get; }
    public string Title => Release.Title;
    public string Artist => Release.Artist;
    public string Kind => Release.Kind switch { "single" => L.T("Single"), "ep" => "EP", _ => L.T("Album") };
    public string When => StatsViewModel.AgoText(Release.Date);
    public string Subtitle => $"{Kind} · {When}";
    public bool IsNew => !Release.Seen;
    public ICommand OpenCommand { get; }
    public ICommand ArtistCommand { get; }

    public ImageSource? Cover
    {
        get
        {
            if (!_requested && Release.Cover != null)
            {
                _requested = true;
                _ = Load();
            }
            return _cover;
        }
    }

    private async Task Load()
    {
        _cover = await WebImages.LoadAsync(new[] { Release.Cover! }, 320);
        OnChanged(nameof(Cover));
    }
}

// Library › Artists (the computers: a page of the sidebar): the followed artists, their new releases, the ones listened to most
// but not followed, and a box to find one. Also looks for the new releases now and then (AppHost's update tick).
public sealed class ArtistsViewModel : Observable
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _findCts;
    private bool _checking;

    public ArtistsViewModel(MainViewModel main)
    {
        _main = main;
        FindCommand = new RelayCommand(() => _ = FindAsync());
        ClearFindCommand = new RelayCommand(() => Query = "");
        SeeAllCommand = new RelayCommand(() => _main.Profile.SeeReleases());
        main.Profile.ArtistsChanged += () => Application.Current.Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    public ICommand FindCommand { get; }
    public ICommand ClearFindCommand { get; }
    public ICommand SeeAllCommand { get; }

    public List<ArtistCardViewModel> Followed { get; private set; } = new();
    public List<ReleaseCardViewModel> Releases { get; private set; } = new();
    public List<ArtistCardViewModel> Suggested { get; private set; } = new();
    public bool HasFollowed => Followed.Count > 0;
    public bool HasReleases => Releases.Count > 0;
    public bool HasSuggested => Suggested.Count > 0;
    public int NewCount { get; private set; }
    public bool HasNew => NewCount > 0;
    public string FollowedText => L.Count(Followed.Count, "1 artist followed", "{0} artists followed");

    public void Refresh()
    {
        var p = _main.Profile;
        Followed = p.ArtistsSnapshot().OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new ArtistCardViewModel(_main, a.Name, a.DeezerId, a.Picture)).ToList();
        Releases = p.ReleasesSnapshot().Select(r => new ReleaseCardViewModel(_main, r)).ToList();
        NewCount = p.UnseenReleases();
        foreach (var a in Found) a.Recheck();
        Suggested = TopArtists(12).Where(a => p.FollowedArtist(a.Name) == null).Take(8)
            .Select(a => new ArtistCardViewModel(_main, a.Name, null, null, L.Count(a.Plays, "1 play", "{0} plays"))).ToList();
        OnChanged(nameof(Followed), nameof(Releases), nameof(Suggested), nameof(HasFollowed), nameof(HasReleases), nameof(HasSuggested), nameof(NewCount),
            nameof(HasNew), nameof(FollowedText));
        _main.OnArtistsChanged();
    }

    // The artists of the songs played most (each name of a song with more than one artist counts).
    public List<(string Name, int Plays, double Seconds)> TopArtists(int count)
    {
        var totals = new Dictionary<string, (string Name, int Plays, double Seconds)>();
        foreach (var (id, s) in _main.Profile.StatsSnapshot())
        {
            if (s.Plays == 0 || _main.Get(id) is not { } t) continue;
            foreach (var name in ArtistNames.Of(t.Artist))
            {
                var key = Text.Normalize(name);
                if (key.Length == 0) continue;
                var had = totals.GetValueOrDefault(key, (name, 0, 0));
                totals[key] = (had.Name, had.Plays + s.Plays, had.Seconds + s.Seconds);
            }
        }
        return totals.Values.OrderByDescending(a => a.Plays).ThenByDescending(a => a.Seconds).Take(count).ToList();
    }

    public async Task ToggleFollowAsync(string name, string? id, string? picture)
    {
        var p = _main.Profile;
        if (p.FollowedArtist(name) != null)
        {
            p.Unfollow(name);
            _main.Toast(L.F("You no longer follow {0}", name));
            return;
        }
        // (Deezer's picture and id: its new releases are looked for there)
        if (id == null)
            try
            {
                if (await OnlineSearchServices.ArtistInfoAsync(name, CancellationToken.None) is { } info)
                    (id, picture, name) = (info.Id, info.Picture, info.Name);
            }
            catch { }
        p.Follow(new FollowedArtist { Name = name, DeezerId = id, Picture = picture });
        _main.Toast(id != null ? L.F("You follow {0}: their new releases show up here", name) : L.F("You follow {0}", name));
        _ = CheckAsync(true);
    }

    // ------------------------------------------------------------------ find an artist

    private string _query = "";
    public string Query
    {
        get => _query;
        set
        {
            if (!Set(ref _query, value)) return;
            if (string.IsNullOrWhiteSpace(value))
            {
                _findCts?.Cancel();
                Found = new();
                OnChanged(nameof(Found), nameof(HasFound));
            }
        }
    }

    public List<ArtistCardViewModel> Found { get; private set; } = new();
    public bool HasFound => Found.Count > 0;
    private bool _finding;
    public bool Finding { get => _finding; private set => Set(ref _finding, value); }

    public async Task FindAsync()
    {
        if (string.IsNullOrWhiteSpace(Query)) return;
        _findCts?.Cancel();
        var cts = _findCts = new CancellationTokenSource();
        Finding = true;
        try
        {
            var found = await OnlineSearchServices.SearchArtistsAsync(Query.Trim(), 12, cts.Token);
            if (cts.IsCancellationRequested) return;
            Found = found.Select(a => new ArtistCardViewModel(_main, a.Name, a.Id, a.Picture,
                a.Fans > 0 ? L.F("{0} fans", a.Fans.ToString("N0")) : null)).ToList();
            if (Found.Count == 0) _main.Toast(L.T("No artist found with this name."));
        }
        catch (OperationCanceledException) { return; }
        catch { _main.Toast(L.T("Couldn't search: check your connection.")); }
        finally { if (_findCts == cts) Finding = false; }
        OnChanged(nameof(Found), nameof(HasFound));
    }

    // ------------------------------------------------------------------ new releases

    // Now and then (every 12 hours, or now: forced), the followed artists' latest releases; the new ones are told.
    public async Task CheckAsync(bool force)
    {
        var p = _main.Profile;
        if (_checking || p.ArtistsSnapshot().Count == 0) return;
        if (!force && p.Data.ReleasesChecked is { } last && DateTime.Now - last < NewReleases.Every) return;
        _checking = true;
        try
        {
            var fresh = await Task.Run(() => NewReleases.CheckAsync(p, CancellationToken.None));
            if (fresh.Count > 0 && _main.Host.Settings.ReleaseNotifications && !force)
                _main.Host.Notify(fresh.Count == 1 ? L.F("New from {0}", fresh[0].Artist) : L.T("New releases"),
                    fresh.Count == 1 ? $"{fresh[0].Title} · {new ReleaseCardViewModel(_main, fresh[0]).Kind}"
                        : string.Join(", ", fresh.Select(r => r.Artist).Distinct().Take(4)) + (fresh.Select(r => r.Artist).Distinct().Count() > 4 ? "…" : ""));
        }
        catch { }
        finally { _checking = false; }
    }
}

// An artist's page: its picture and name, follow, its new releases and its albums (from the music sites, opened like their
// link), and your songs of it (played like a list).
public sealed class ArtistPageViewModel : Observable, ITrackList
{
    private readonly MainViewModel _main;
    private List<TrackViewModel> _order = new();
    private ImageSource? _picture;
    private CancellationTokenSource? _cts;

    public ArtistPageViewModel(MainViewModel main, string name, string? deezerId)
    {
        _main = main;
        Name = name;
        DeezerId = deezerId ?? main.Profile.FollowedArtist(name)?.DeezerId;
        PlayCommand = new RelayCommand(() => _main.Player.PlayAll(this, false), () => _order.Count > 0);
        ShuffleCommand = new RelayCommand(() => _main.Player.PlayAll(this, true), () => _order.Count > 0);
        FollowCommand = new RelayCommand(() => _ = Follow());
        RadioCommand = new RelayCommand(PlayRadio);
        OnlineCommand = new RelayCommand(() => _main.SearchText = Name);
        MoreCommand = new RelayCommand(() => { _showAll = true; OnChanged(nameof(Shown), nameof(HasMore)); });
        RenameCommand = new RelayCommand(() => _ = Rename());
        PictureCommand = new RelayCommand(() => _ = ChangePicture());
        RemovePictureCommand = new RelayCommand(() => _ = RemovePicture(), () => HasMyPicture);
        Rebuild();
        _ = LoadAsync();
    }

    public string Name { get; private set; }
    public string? DeezerId { get; private set; }
    public string ContextId => "artistpage:" + Text.Normalize(Name);
    public string ContextName => Name;
    public IReadOnlyList<TrackViewModel> PlayOrder => _order;
    public Playlist? Playlist => null;

    public ICommand PlayCommand { get; }
    public ICommand ShuffleCommand { get; }
    public ICommand FollowCommand { get; }
    public ICommand RadioCommand { get; }
    public ICommand OnlineCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand PictureCommand { get; }
    public ICommand RemovePictureCommand { get; }

    public string Initial => ArtistPictures.Initial(Name);
    public Brush Brush => ArtistPictures.BrushFor(Name);
    public ImageSource? Picture => _picture;
    public bool HasPicture => _picture != null;
    public bool HasMyPicture => ArtistPictures.HasMine(Name);
    public bool IsFollowed => _main.Profile.FollowedArtist(Name) != null;
    public string FollowText => IsFollowed ? L.T("Following") : L.T("Follow");
    public string FollowGlyph => IsFollowed ? "\uE73E" : "\uE710";

    private int _fans;
    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (_fans > 0) parts.Add(L.F("{0} fans on Deezer", _fans.ToString("N0")));
            parts.Add(_order.Count == 0 ? L.T("no songs in your library") : L.Count(_order.Count, "1 song in your library", "{0} songs in your library"));
            return string.Join(" · ", parts);
        }
    }

    public List<TrackRow> Rows { get; private set; } = new();
    // The page shows the first ten; "Show all" the rest.
    private bool _showAll;
    public List<TrackRow> Shown => _showAll ? Rows : Rows.Take(TopCount).ToList();
    public const int TopCount = 10;
    public bool HasMore => !_showAll && Rows.Count > TopCount;
    public string MoreText => L.F("Show all {0}", Rows.Count);
    public ICommand MoreCommand { get; }
    public bool HasSongs => _order.Count > 0;
    public List<ReleaseCardViewModel> Releases { get; private set; } = new();
    public bool HasReleases => Releases.Count > 0;
    public List<OnlineHitViewModel> Albums { get; private set; } = new();
    public bool HasAlbums => Albums.Count > 0;
    private bool _loading = true;
    public bool Loading { get => _loading; private set => Set(ref _loading, value); }

    // Your songs of it (also where it's one of several artists), the most played first.
    public void Rebuild()
    {
        var stats = _main.Profile.StatsSnapshot();
        _order = _main.Songs().Where(t => ArtistNames.Has(t.Artist, Name))
            .OrderByDescending(t => stats.GetValueOrDefault(t.Id)?.Plays ?? 0).ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(_main.Vm).ToList();
        Rows = _order.Select((t, i) => new TrackRow(i + 1, t, this)).ToList();
        OnChanged(nameof(Rows), nameof(Shown), nameof(HasMore), nameof(MoreText), nameof(HasSongs), nameof(Subtitle));
        CommandManager.InvalidateRequerySuggested();
    }

    public void OnFollowChanged() => OnChanged(nameof(IsFollowed), nameof(FollowText), nameof(FollowGlyph));

    private async Task Follow()
    {
        await _main.Artists.ToggleFollowAsync(Name, DeezerId, null);
        OnFollowChanged();
    }

    // The artist's radio: one of your songs of it (the most played), or its best known online, then its songs found online.
    private void PlayRadio()
    {
        if ((_order.FirstOrDefault() ?? _top?.Song()) is { } seed) _main.Player.PlayArtist(seed);
        else _main.Toast(L.T("No songs by the artist found online: try again in a moment."));
    }

    private OnlineHitViewModel? _top;
    private string? _pictureUrl;

    // Its name on your songs (also where it's one of several artists); the follow and the picture chosen go along.
    private async Task Rename()
    {
        var to = (await Dialogs.PromptAsync(L.T("Rename artist"), L.T("New name"), Name))?.Trim();
        if (string.IsNullOrEmpty(to) || to == Name) return;
        var changed = _main.Library.RenameArtist(Name, to, inLines: true);
        _main.Profile.RenameFollowed(Name, to);
        ArtistPictures.Rename(Name, to);
        _main.Toast(L.Count(changed.Count, "1 song updated", "{0} songs updated"));
        Name = to;
        OnChanged(nameof(Name), nameof(ContextName), nameof(ContextId), nameof(Initial), nameof(Brush), nameof(HasMyPicture));
        Rebuild();
    }

    private async Task ChangePicture()
    {
        if (await Dialogs.PickImageAsync() is not { } file) return;
        if (!await Task.Run(() => AudioAnalysis.MakeCoverFromImageAsync(file, ArtistPictures.Mine(Name))))
        {
            _main.Toast(L.T("Invalid or unreadable image."));
            return;
        }
        await ShowPicture();
    }

    private async Task RemovePicture()
    {
        try { File.Delete(ArtistPictures.Mine(Name)); } catch { }
        await ShowPicture();
    }

    private async Task ShowPicture()
    {
        _picture = await ArtistPictures.LoadAsync(Name, DeezerId, _pictureUrl, 600);
        OnChanged(nameof(Picture), nameof(HasPicture), nameof(HasMyPicture));
        CommandManager.InvalidateRequerySuggested();
        _main.Artists.Refresh();
    }

    private async Task LoadAsync()
    {
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        try
        {
            // (the one chosen by hand at once, even for an artist Deezer doesn't know)
            if (HasMyPicture)
            {
                _picture = await ArtistPictures.LoadAsync(Name, null, null, 600);
                OnChanged(nameof(Picture), nameof(HasPicture));
            }
            var info = DeezerId != null ? await OnlineSearchServices.ArtistByIdAsync(DeezerId, ct) : await OnlineSearchServices.ArtistInfoAsync(Name, ct);
            if (info != null)
            {
                DeezerId = info.Id;
                _fans = info.Fans;
                OnChanged(nameof(Subtitle));
                if (_main.Profile.FollowedArtist(Name) != null) _main.Profile.SetArtistPicture(Name, info.Id, info.Picture);
                _pictureUrl = info.Picture;
                _picture = await ArtistPictures.LoadAsync(Name, info.Id, info.Picture, 600);
                OnChanged(nameof(Picture), nameof(HasPicture));
                var releases = await OnlineSearchServices.ReleasesAsync(info.Id, info.Name, 12, ct);
                Releases = releases.Where(r => r.Date >= DateTime.Today.AddDays(-NewReleases.Days) && r.Date <= DateTime.Today)
                    .Select(r => new ReleaseCardViewModel(_main, r)).ToList();
                OnChanged(nameof(Releases), nameof(HasReleases));
                if ((await OnlineSearchServices.ArtistTopAsync(info.Id, 1, 0, ct)).FirstOrDefault() is { } top) _top = new OnlineHitViewModel(top, _main);
            }
            // Its albums and EPs from the sites the search uses for "Albums by X" (merged, the newest first).
            var sites = new[] { OnlineSearchServices.Deezer, OnlineSearchServices.YouTubeMusic, OnlineSearchServices.AppleMusic };
            var found = await Task.WhenAll(sites.Select(async s =>
            {
                try { return (await OnlineSearchServices.ArtistAlbumsAsync(s, Name, ct))?.Hits ?? new List<SearchHit>(); }
                catch { return new List<SearchHit>(); }
            }));
            Albums = OnlineSearchServices.MergeAlbums(found).Select(h => (OnlineHitViewModel)new FeaturedHitViewModel(h, _main)).ToList();
            OnChanged(nameof(Albums), nameof(HasAlbums));
        }
        catch { }
        finally { if (_cts == cts) Loading = false; }
    }
}
