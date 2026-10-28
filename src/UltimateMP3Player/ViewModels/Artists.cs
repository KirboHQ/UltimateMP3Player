using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// Artists' pictures: one chosen by hand, else its site's (ArtistSites: the artist the library's songs prove, or the one chosen),
// kept on the disk (covers/artists) once seen, so the lists show them offline too.
public static class ArtistPictures
{
    // Chosen on the artist's page (Change image), kept under its name: it wins over the sites'.
    public static string Mine(string name) => AppPaths.ArtistPicture("my-" + Key(name));
    public static bool HasMine(string name) => File.Exists(Mine(name));
    private static string Key(string name) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Text.Normalize(name))))[..16].ToLowerInvariant();

    // The library's songs of an artist (also where it's one of several): what proves who it is.
    public static List<Track> SongsOf(Library library, string name) => library.Snapshot().Where(t => ArtistNames.Has(t.Artist, name)).ToList();

    // id/url: the picture a card came with (a search's artist); without them, the artist the name is for the library.
    public static async Task<ImageSource?> LoadAsync(string name, string? id, string? url, int size, Library? library = null)
    {
        var mine = Mine(name);
        if (File.Exists(mine)) return await Images.LoadAsync(mine, size, (int)File.GetLastWriteTimeUtc(mine).Ticks);
        // (the artist chosen by hand wins over the one a card was made with)
        var site = ArtistSites.Get(name);
        if (site is { Mine: true } || id == null && url == null)
        {
            if (site == null && library != null) site = await ArtistSites.ResolveAsync(name, SongsOf(library, name));
            if (site == null) return null;
            (id, url) = (site.PictureKey, site.Picture);
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
        ChooseCommand = new RelayCommand(() => _ = _main.ChooseArtist(Name));
    }

    public MainViewModel Main => _main;
    public ICommand ChooseCommand { get; }

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
        _picture = await ArtistPictures.LoadAsync(Name, DeezerId, _pictureUrl, 320, _main.Library);
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
        // (heard without saving it, as a found album: its songs read from its link)
        PlayCommand = new RelayCommand(() => _ = _main.PlayLinkAsync(r.Url, r.Title));
    }

    public ICommand PlayCommand { get; }

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
        // (the artist the library's songs prove, or the one chosen: its new releases are looked for on its site)
        if (id == null && await ArtistSites.ResolveAsync(name, ArtistPictures.SongsOf(_main.Library, name)) is { } site)
            (id, picture) = (site.DeezerId, site.Picture);
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

// An artist's page: its picture and name, follow, its popular songs, new releases and albums (from its site: the artist the
// library's songs prove, or the one chosen by hand), and your songs of it (played like a list).
public sealed class ArtistPageViewModel : Observable, ITrackList
{
    private readonly MainViewModel _main;
    // A search's artist (its card had Deezer's id): that one, whatever the library's songs say.
    private readonly string? _given;
    private List<TrackViewModel> _order = new();
    private ImageSource? _picture;
    private CancellationTokenSource? _cts;

    public ArtistPageViewModel(MainViewModel main, string name, string? deezerId)
    {
        _main = main;
        Name = name;
        _given = deezerId;
        DeezerId = deezerId;
        PlayCommand = new RelayCommand(() => Play(false), () => _order.Count > 0 || Popular.Count > 0);
        ShuffleCommand = new RelayCommand(() => Play(true), () => _order.Count > 0 || Popular.Count > 0);
        FollowCommand = new RelayCommand(() => _ = Follow());
        RadioCommand = new RelayCommand(PlayRadio);
        OnlineCommand = new RelayCommand(() => _main.SearchText = Name);
        MoreCommand = new RelayCommand(() => { _showAll = true; OnChanged(nameof(Shown), nameof(HasMore)); });
        RenameCommand = new RelayCommand(() => _ = Rename());
        PictureCommand = new RelayCommand(() => _ = ChangePicture());
        RemovePictureCommand = new RelayCommand(() => _ = RemovePicture(), () => HasMyPicture);
        ChooseCommand = new RelayCommand(() => _ = _main.ChooseArtist(Name));
        SiteCommand = new RelayCommand(() => { if (Site?.Url is { } url) MainViewModel.OpenUrl(url); });
        Rebuild();
        _ = LoadAsync();
    }

    public string Name { get; private set; }
    public string? DeezerId { get; private set; }
    // Who it is on its site (null while looked for, or not found).
    public ArtistSite? Site { get; private set; }
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
    public ICommand ChooseCommand { get; }
    public ICommand SiteCommand { get; }

    public string Initial => ArtistPictures.Initial(Name);
    public Brush Brush => ArtistPictures.BrushFor(Name);
    public ImageSource? Picture => _picture;
    public bool HasPicture => _picture != null;
    public bool HasMyPicture => ArtistPictures.HasMine(Name);
    public bool IsFollowed => _main.Profile.FollowedArtist(Name) != null;
    public string FollowText => IsFollowed ? L.T("Following") : L.T("Follow");
    public string FollowGlyph => IsFollowed ? "" : "";
    public bool HasSite => Site != null;
    public string SiteText => Site == null ? "" : L.F("On {0}", Site.Service);

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (Site is { Fans: > 0 } s) parts.Add(L.F("{0} fans on {1}", s.Fans.ToString("N0"), s.Service));
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
    // Its most listened songs on its site: a click plays them from there, heard without saving them.
    public List<OnlineHitViewModel> Popular { get; private set; } = new();
    public bool HasPopular => Popular.Count > 0;
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
        await _main.Artists.ToggleFollowAsync(Name, DeezerId, Site?.Picture);
        OnFollowChanged();
    }

    // Your songs of it; without any, its popular ones (heard without saving them).
    private void Play(bool shuffle)
    {
        if (_order.Count > 0) _main.Player.PlayAll(this, shuffle);
        else if (Popular.Count > 0) Popular[shuffle ? Random.Shared.Next(Popular.Count) : 0].PlayCommand.Execute(null);
    }

    // The artist's radio: one of your songs of it (the most played), or its best known online, then its songs found online.
    private void PlayRadio()
    {
        if ((_order.FirstOrDefault() ?? Popular.FirstOrDefault()?.Song()) is { } seed) _main.Player.PlayArtist(seed);
        else _main.Toast(L.T("No songs by the artist found online: try again in a moment."));
    }

    // Its name on your songs (also where it's one of several artists); the follow, the picture and the artist chosen go along.
    private async Task Rename()
    {
        var to = (await Dialogs.PromptAsync(L.T("Rename artist"), L.T("New name"), Name))?.Trim();
        if (string.IsNullOrEmpty(to) || to == Name) return;
        var changed = _main.Library.RenameArtist(Name, to, inLines: true);
        _main.Profile.RenameFollowed(Name, to);
        ArtistPictures.Rename(Name, to);
        ArtistSites.Rename(Name, to);
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
        _picture = Site != null ? await ArtistPictures.LoadAsync(Name, Site.PictureKey, Site.Picture, 600)
            : await ArtistPictures.LoadAsync(Name, null, null, 600, _main.Library);
        OnChanged(nameof(Picture), nameof(HasPicture), nameof(HasMyPicture));
        CommandManager.InvalidateRequerySuggested();
        _main.Artists.Refresh();
    }

    // Another artist chosen for the name: everything from its site again.
    public void Reload()
    {
        _cts?.Cancel();
        Site = null;
        DeezerId = _given;
        Popular = new();
        Releases = new();
        Albums = new();
        Loading = true;
        OnChanged(nameof(Site), nameof(HasSite), nameof(SiteText), nameof(Popular), nameof(HasPopular), nameof(Releases), nameof(HasReleases), nameof(Albums),
            nameof(HasAlbums), nameof(Subtitle));
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        try
        {
            // (the one chosen by hand at once, even for an artist no site knows)
            if (HasMyPicture)
            {
                _picture = await ArtistPictures.LoadAsync(Name, null, null, 600);
                OnChanged(nameof(Picture), nameof(HasPicture));
            }
            var site = ArtistSites.Get(Name) is { Mine: true } mine ? mine
                : _given != null ? await Task.Run(() => OnlineSearchServices.ArtistSiteByIdAsync(OnlineSearchServices.Deezer, _given, ct))
                : await ArtistSites.ResolveAsync(Name, ArtistPictures.SongsOf(_main.Library, Name));
            if (ct.IsCancellationRequested) return;
            if (site != null)
            {
                Site = site;
                DeezerId = site.DeezerId;
                OnChanged(nameof(Site), nameof(HasSite), nameof(SiteText), nameof(Subtitle));
                // (a follow keeps the site's artist: its releases are looked for there)
                if (_given == null && IsFollowed) _main.Profile.SetArtist(Name, site.DeezerId, site.Picture);
                _picture = await ArtistPictures.LoadAsync(Name, site.PictureKey, site.Picture, 600);
                OnChanged(nameof(Picture), nameof(HasPicture));
                try
                {
                    var popular = await Task.Run(() => ArtistSites.TopAsync(site, 10, 0, ct));
                    if (ct.IsCancellationRequested) return;
                    Popular = popular.Select(h => new OnlineHitViewModel(h, _main)).ToList();
                    for (int i = 0; i < Popular.Count; i++)
                    {
                        Popular[i].Siblings = Popular;
                        Popular[i].Number = (i + 1).ToString();
                    }
                    OnChanged(nameof(Popular), nameof(HasPopular));
                    CommandManager.InvalidateRequerySuggested();
                }
                catch (Exception) when (!ct.IsCancellationRequested) { }
                try
                {
                    var releases = await Task.Run(() => ArtistSites.ReleasesAsync(site, Name, 12, ct));
                    Releases = releases.Where(r => r.Date >= DateTime.Today.AddDays(-NewReleases.Days) && r.Date <= DateTime.Today)
                        .Select(r => new ReleaseCardViewModel(_main, r)).ToList();
                    OnChanged(nameof(Releases), nameof(HasReleases));
                }
                catch (Exception) when (!ct.IsCancellationRequested) { }
            }
            var albums = await AlbumsAsync(site, ct);
            if (ct.IsCancellationRequested) return;
            Albums = albums.Select(h => (OnlineHitViewModel)new FeaturedHitViewModel(h, _main)).ToList();
            OnChanged(nameof(Albums), nameof(HasAlbums));
        }
        catch { }
        finally { if (_cts == cts) Loading = false; }
    }

    // Its albums and EPs: its own site's, with the ones only YouTube Music or Apple Music have of a Deezer artist when they have
    // one of its albums too (a namesake has none in common). Found on no site: the sites' by its name, as before.
    private async Task<List<SearchHit>> AlbumsAsync(ArtistSite? site, CancellationToken ct)
    {
        async Task<List<SearchHit>> ByName(string service)
        {
            try { return (await Task.Run(() => OnlineSearchServices.ArtistAlbumsAsync(service, Name, ct)))?.Hits ?? new List<SearchHit>(); }
            catch { return new List<SearchHit>(); }
        }
        if (site == null)
            return OnlineSearchServices.MergeAlbums(await Task.WhenAll(new[] { OnlineSearchServices.Deezer, OnlineSearchServices.YouTubeMusic, OnlineSearchServices.AppleMusic }
                .Select(ByName)));
        List<SearchHit> own;
        try { own = await Task.Run(() => ArtistSites.AlbumsAsync(site, ct)); }
        catch { own = new List<SearchHit>(); }
        if (site.Service != OnlineSearchServices.Deezer || own.Count == 0) return OnlineSearchServices.MergeAlbums(new[] { own });
        var others = await Task.WhenAll(ByName(OnlineSearchServices.YouTubeMusic), ByName(OnlineSearchServices.AppleMusic));
        var titles = own.Select(h => Text.Normalize(h.Title)).ToHashSet();
        return OnlineSearchServices.MergeAlbums(new[] { own }.Concat(others.Where(o => o.Any(h => titles.Contains(Text.Normalize(h.Title))))));
    }
}

// An artist the name may be ("Choose the artist…"): its picture, its site, how many fans and how many of your songs it has.
public sealed class ArtistChoice : Observable
{
    private bool _requested;
    private ImageSource? _picture;

    public ArtistChoice(ArtistSite site, int songs, bool current)
    {
        Site = site;
        IsCurrent = current;
        var parts = new List<string> { site.Service };
        if (site.Fans > 0) parts.Add(L.F("{0} fans", site.Fans.ToString("N0")));
        if (songs > 0) parts.Add(site.Songs > 0 ? L.Count(site.Songs, "has 1 of your songs", "has {0} of your songs") : L.T("none of your songs"));
        Details = string.Join(" · ", parts);
    }

    public ArtistSite Site { get; }
    public string Name => Site.Name;
    public string Details { get; }
    public bool IsCurrent { get; }
    public string Initial => ArtistPictures.Initial(Name);
    public Brush Brush => ArtistPictures.BrushFor(Name);

    public ImageSource? Picture
    {
        get
        {
            if (!_requested && Site.Picture != null)
            {
                _requested = true;
                _ = Load();
            }
            return _picture;
        }
    }

    private async Task Load()
    {
        _picture = await WebImages.LoadAsync(new[] { Site.Picture! }, 112);
        OnChanged(nameof(Picture));
    }
}
