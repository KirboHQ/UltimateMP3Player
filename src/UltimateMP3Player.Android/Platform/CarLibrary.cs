using System.Collections.Specialized;
using System.ComponentModel;
using Android.Content;
using Android.Media;
using Android.OS;
using Android.Provider;
using Avalonia.Threading;
using UltimateMP3Player.Core;
using UltimateMP3Player.Services;
using UltimateMP3Player.ViewModels;
using MediaItem = Android.Media.Browse.MediaBrowser.MediaItem;
using MediaItemFlags = Android.Media.Browse.MediaItemFlags;
using QueueItem = Android.Media.Session.MediaSession.QueueItem;

namespace UltimateMP3Player.Platform;

// The library as the car's screen shows it, and what its taps and the voice ask for. Everything plays through the player's own
// PlayFrom, PlayAll, PlayMix, PlayStations, which already know about rooms and a linked computer, and nothing here ever changes
// the profile of a session already open.
//
// The ids: "list|<key>" a list of songs, "song|<key>|<song>", "page|<key>|<n>" a piece of a long list, "shuffle|<key>", "mix|<id>",
// "station|<id>", "artistpage|<n>", "albumpage|<n>", "profile|<id>". The keys: "all", "recent", "pl:<playlist>", "artist:<name, escaped>",
// "album:<name, escaped>:<artist, escaped>", "tag:<id>". No search on the car's screen: the framework's MediaBrowserService has none
// (only androidx's), the car's microphone goes to the voice (Search).
public static class CarLibrary
{
    public const string Root = "root";
    // (the library's tab keeps the id it had when it was "Songs")
    private const string HomeTab = "home", LibraryTab = "songs", PlaylistsTab = "playlists", ArtistsTab = "artists",
        AlbumsFolder = "albums", TagsFolder = "tags", StationsFolder = "stations", ProfilesFolder = "profiles";
    // Android Auto has no pages (the whole list is sent at once): a long list becomes folders of this many, by title.
    private const int PageSize = 200;
    private const int ListStyle = 1, GridStyle = 2;
    private const string BrowsableHint = "android.media.browse.CONTENT_STYLE_BROWSABLE_HINT", PlayableHint = "android.media.browse.CONTENT_STYLE_PLAYABLE_HINT",
        GroupHint = "android.media.browse.CONTENT_STYLE_GROUP_TITLE_HINT", TabLimitHint = "androidx.media.MediaBrowserCompat.Extras.KEY_ROOT_CHILDREN_LIMIT";

    private static readonly Handler Main = new(Looper.MainLooper!);
    private static Context Ctx => Android.App.Application.Context;
    private static MediaControls Media => MediaControls.Shared;

    private static readonly HashSet<string> Clients = new();
    private static readonly HashSet<string> Loaded = new();
    private static int _tabLimit = 4;
    private static int _queueVersion;
    private static bool _notifying;
    private static MainViewModel? _watched;
    private static bool _refused;

    public static Android.Service.Media.MediaBrowserService? Service { get; set; }
    // A car is browsing, or the voice asked for music: its screen shows the queue and the errors. (Not the lock screen's or the
    // headphones' Play, which come through here too: nobody reads an error there.)
    public static bool Active { get; private set; }

    public static Bundle RootExtras()
    {
        var extras = new Bundle();
        extras.PutBoolean("android.media.browse.CONTENT_STYLE_SUPPORTED", true);
        extras.PutInt(BrowsableHint, GridStyle);
        extras.PutInt(PlayableHint, ListStyle);
        return extras;
    }

    // A client that passed CarClients: it may read the covers from now on.
    public static void AddClient(string package, Bundle? hints)
    {
        lock (Clients) Clients.Add(package);
        if (hints != null && hints.GetInt(TabLimitHint) is > 0 and var limit) _tabLimit = limit;
        // (the song already playing gets its cover's address too)
        Media.PublishTrack();
    }

    public static void SetActive(bool on)
    {
        if (Active == on) return;
        Active = on;
        Media.PublishTrack();
        if (_watched is not { } s) return;
        s.Player.UpNextWanted = on;
        if (on) s.Player.RefreshUpNext();
    }

    // ------------------------------------------------------------------ the app, from AppHost

    public static void Attach(AppHost host)
    {
        host.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppHost.Session)) Watch(host.Session);
        };
        host.Library.TrackAdded += _ => Changed();
        host.Library.TrackRemoved += _ => Changed();
    }

    private static void Watch(MainViewModel? s)
    {
        bool replaced = _watched != null;
        if (_watched is { } old)
        {
            old.Player.PropertyChanged -= OnPlayer;
            old.Player.Problem -= OnProblem;
            old.Playlists.CollectionChanged -= OnPlaylists;
        }
        _watched = s;
        if (s == null) return;
        s.Player.PropertyChanged += OnPlayer;
        s.Player.Problem += OnProblem;
        s.Playlists.CollectionChanged += OnPlaylists;
        s.Player.UpNextWanted = Active;
        if (Active) s.Player.RefreshUpNext();
        Media.SetModes(s.Player.Shuffle, s.Player.Repeat);
        // (another profile, or the first one after the car found the app not ready: the first opening is for the list being asked for)
        if (replaced || _refused) Changed();
        _refused = false;
        Media.ClearError();
    }

    private static void OnPlaylists(object? sender, NotifyCollectionChangedEventArgs e) => Changed();

    private static void OnProblem(string text)
    {
        PlaybackService.Release();
        if (Active) Media.SetError(text);
    }

    private static void OnPlayer(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not PlayerViewModel p) return;
        if (e.PropertyName == nameof(PlayerViewModel.UpNext)) PublishQueue(p);
        else if (e.PropertyName == nameof(PlayerViewModel.IsWaiting)) Media.SetWaiting(p.IsWaiting);
        else if (e.PropertyName is nameof(PlayerViewModel.Shuffle) or nameof(PlayerViewModel.Repeat)) Media.SetModes(p.Shuffle, p.Repeat);
    }

    // What the car has on screen changed: it reads it again, a moment later (a whole playlist may be coming in).
    public static void Changed()
    {
        if (Service == null || _notifying) return;
        _notifying = true;
        Dispatcher.UIThread.Post(() => DispatcherTimer.RunOnce(() =>
        {
            _notifying = false;
            foreach (var id in Loaded) Service?.NotifyChildrenChanged(id);
        }, TimeSpan.FromSeconds(2)));
    }

    // ------------------------------------------------------------------ opening

    // The session, opened now if the app was closed. Null with the terms still to accept: the car says to open the app.
    public static MainViewModel? Open()
    {
        if (PhoneBoot.Ensure() && App.Host.OpenWithoutAsking() is { } s) return s;
        Media.SetError(L.T("Open Ultimate MP3 Player on your phone to start."), openApp: true);
        return null;
    }

    // ------------------------------------------------------------------ the lists

    // On the main thread; whatever takes long goes away from it, and the answer comes back to it.
    public static void Load(string parent, Action<List<MediaItem>> send)
    {
        Loaded.Add(parent);
        SetActive(true);
        var s = Open();
        _refused = s == null;
        var build = s != null ? Browse(s, parent) : null;
        if (build == null)
        {
            send(new List<MediaItem>());
            return;
        }
        Task.Run(() =>
        {
            List<MediaItem> items;
            try { items = build(); }
            catch (Exception ex)
            {
                App.Log(ex);
                items = new List<MediaItem>();
            }
            Main.Post(() => send(items));
        });
    }

    // What a list needs from the app's objects is taken here, on the UI thread; the function returned does the rest away from it.
    private static Func<List<MediaItem>>? Browse(MainViewModel s, string parent)
    {
        var parts = parent.Split('|');
        switch (parts[0])
        {
            case Root: return Tabs;
            case HomeTab: return HomeItems(s);
            case LibraryTab: return LibraryItems(s);
            case PlaylistsTab: return PlaylistItems(s);
            case ArtistsTab:
            {
                var songs = s.Songs();
                return () => ArtistItems(songs, -1);
            }
            case "artistpage" when parts.Length == 2 && int.TryParse(parts[1], out var n):
            {
                var songs = s.Songs();
                return () => ArtistItems(songs, n);
            }
            case AlbumsFolder:
            {
                var songs = s.Songs();
                return () => AlbumItems(songs, -1);
            }
            case "albumpage" when parts.Length == 2 && int.TryParse(parts[1], out var n):
            {
                var songs = s.Songs();
                return () => AlbumItems(songs, n);
            }
            case TagsFolder: return TagItems(s);
            case StationsFolder:
            {
                var stations = StationRows(s);
                return () => stations.Select(st => StationItem(st)).ToList();
            }
            case ProfilesFolder: return ProfileItems(s);
            case "list" when parts.Length == 2: return ListItems(s, parts[1], -1);
            case "page" when parts.Length == 3 && int.TryParse(parts[2], out var n): return ListItems(s, parts[1], n);
        }
        return null;
    }

    private static List<MediaItem> Tabs() => new List<MediaItem>
    {
        Item(HomeTab, L.T("Home"), null, Icon("ic_car_home"), false, Style(PlayableHint, GridStyle)),
        Item(LibraryTab, L.T("Library"), null, Icon("ic_car_library"), false, Style(BrowsableHint, ListStyle)),
        Item(PlaylistsTab, L.T("Playlists"), null, Icon("ic_car_playlist"), false),
        Item(ArtistsTab, L.T("Artists"), null, Icon("ic_car_person"), false),
    }.Take(_tabLimit).ToList();

    private static Bundle Style(string hint, int style)
    {
        var extras = new Bundle();
        extras.PutInt(hint, style);
        return extras;
    }

    private static Bundle Group(string title)
    {
        var extras = new Bundle();
        extras.PutString(GroupHint, title);
        return extras;
    }

    private static Func<List<MediaItem>> HomeItems(MainViewModel s)
    {
        s.Home.Refresh();
        var mixes = s.Home.ForYou.Select(m => (m.Id, m.Title, m.Subtitle, m.Cover.T)).ToList();
        var recent = s.Home.Recent.Take(30).Select(t => t.T).ToList();
        var stations = StationRows(s);
        return () =>
        {
            var items = new List<MediaItem>();
            var group = Group(L.T("For you"));
            foreach (var (id, title, subtitle, cover) in mixes)
                items.Add(Item("mix|" + id, title, subtitle, CoverOf(cover) ?? Icon("ic_car_playlist"), true, group));
            group = Group(L.T("Recently played"));
            foreach (var t in recent) items.Add(SongItem("recent", t, group));
            group = Group(L.T("Radio"));
            foreach (var st in stations) items.Add(StationItem(st, group));
            // (nothing heard yet: not an empty first page)
            if (items.Count == 0) items.Add(Item("shuffle|all", L.T("Shuffle"), null, Icon("ic_car_shuffle"), true));
            return items;
        };
    }

    private static List<(string Id, string Name, string? Subtitle)> StationRows(MainViewModel s)
        => s.Stations.Saved.Select(st => (st.S.Id, st.Name, (string?)st.Subtitle)).ToList();

    private static MediaItem StationItem((string Id, string Name, string? Subtitle) st, Bundle? extras = null)
        => Item("station|" + st.Id, st.Name, st.Subtitle, FileUri(AppPaths.TrackCover(Station.Prefix + st.Id)) ?? Icon("ic_car_radio"), true, extras);

    // Like the app's Library: everything shuffled, the Favorites, all the songs, and the folders the other tabs have no room for.
    private static Func<List<MediaItem>> LibraryItems(MainViewModel s)
    {
        var favorites = PlaylistRows(s, s.Playlists.Where(p => p.IsFavorites));
        s.LibraryPage.Rebuild();
        int count = s.LibraryPage.PlayOrder.Count;
        bool tags = s.Tags.Count > 0, stations = s.Stations.Saved.Count > 0, profiles = App.Host.Profiles.Profiles.Count > 1;
        return () =>
        {
            var items = new List<MediaItem> { Item("shuffle|all", L.T("Shuffle"), null, Icon("ic_car_shuffle"), true) };
            items.AddRange(favorites.Select(PlaylistItem));
            items.Add(Item("list|all", L.T("All songs"), L.Count(count, "1 song", "{0} songs"), Icon("ic_car_music"), false));
            items.Add(Item(AlbumsFolder, L.T("Albums"), null, Icon("ic_car_album"), false));
            if (tags) items.Add(Item(TagsFolder, L.T("Tags"), null, Icon("ic_car_tag"), false, Style(BrowsableHint, ListStyle)));
            if (stations) items.Add(Item(StationsFolder, L.T("Radio"), null, Icon("ic_car_radio"), false, Style(PlayableHint, GridStyle)));
            if (profiles) items.Add(Item(ProfilesFolder, L.T("Profiles"), null, Icon("ic_car_profile"), false));
            return items;
        };
    }

    private static Func<List<MediaItem>> TagItems(MainViewModel s)
    {
        var tags = s.Tags.Select(t => (t.Id, t.Name, t.Count)).ToList();
        return () => tags.Select(t => Item("list|tag:" + t.Id, t.Name, L.Count(t.Count, "1 song", "{0} songs"), Icon("ic_car_tag"), false)).ToList();
    }

    // A tap plays the profile's music from where it was; the one open says so.
    private static Func<List<MediaItem>> ProfileItems(MainViewModel s)
    {
        var current = s.Profile.Info.Id;
        var profiles = App.Host.Profiles.Profiles.Select(p => (p.Id, p.Name)).ToList();
        return () => profiles.Select(p => Item("profile|" + p.Id, p.Name, p.Id == current ? L.T("Listening now") : null, Icon("ic_car_profile"), true)).ToList();
    }

    private static Func<List<MediaItem>> PlaylistItems(MainViewModel s)
    {
        var rows = PlaylistRows(s, s.Playlists.Where(p => !p.IsFavorites));
        return () => rows.Select(PlaylistItem).ToList();
    }

    private static List<(string Id, string Name, string Count, string? Cover, Track? First)> PlaylistRows(MainViewModel s, IEnumerable<PlaylistViewModel> playlists)
        => playlists.Select(p => (p.Id, p.Name, p.CountText, p.HasCustomCover ? s.ProfileOf(p.P).PlaylistCover(p.P) : null,
            p.P.Tracks.Select(s.Get).FirstOrDefault(t => t is { HasCover: true }))).ToList();

    private static MediaItem PlaylistItem((string Id, string Name, string Count, string? Cover, Track? First) p)
        => Item("list|pl:" + p.Id, p.Name, p.Count, (p.Cover != null ? FileUri(p.Cover) : null) ?? CoverOf(p.First) ?? Icon("ic_car_playlist"), false);

    private static List<MediaItem> ArtistItems(List<Track> songs, int page)
    {
        var map = new Dictionary<string, (string Name, Track? Cover)>();
        foreach (var t in songs)
            foreach (var name in ArtistNames.Of(t.Artist))
            {
                var key = Text.Normalize(name);
                if (key.Length == 0) continue;
                if (!map.TryGetValue(key, out var a)) map[key] = (name, t.HasCover ? t : null);
                else if (a.Cover == null && t.HasCover) map[key] = (a.Name, t);
            }
        var all = map.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return Paged(all, page, a => a.Name, a => ArtistItem(a.Name, a.Cover), "artistpage", "ic_car_person");
    }

    private static MediaItem ArtistItem(string name, Track? cover)
        => Item("list|artist:" + Uri.EscapeDataString(name), name, null, ArtistPicture(name) ?? CoverOf(cover) ?? Icon("ic_car_person"), false);

    // Android Auto has no pages: over PageSize, folders of PageSize by name ("A – D"), each opening its piece.
    private static List<MediaItem> Paged<T>(List<T> all, int page, Func<T, string> name, Func<T, MediaItem> row, string pageKind, string icon)
    {
        if (page >= 0) return all.Skip(page * PageSize).Take(PageSize).Select(row).ToList();
        if (all.Count <= PageSize) return all.Select(row).ToList();
        return all.Chunk(PageSize).Select((piece, i) => Item(pageKind + "|" + i, Range(name(piece[0]), name(piece[^1])), null, Icon(icon), false)).ToList();
    }

    // The library's albums: the same name and first artist, two songs at least (a single's album is the song itself).
    private static List<(string Name, string? Artist, Track? Cover, int Count)> AlbumRows(List<Track> songs)
    {
        var map = new Dictionary<string, (string Name, string? Artist, Track? Cover, int Count)>();
        foreach (var t in songs)
        {
            if (string.IsNullOrWhiteSpace(t.Album)) continue;
            var artist = ArtistNames.Of(t.Artist).FirstOrDefault();
            var key = Text.Normalize(t.Album) + "\n" + Text.Normalize(artist);
            if (!map.TryGetValue(key, out var a)) map[key] = (t.Album.Trim(), artist, t.HasCover ? t : null, 1);
            else map[key] = (a.Name, a.Artist, a.Cover ?? (t.HasCover ? t : null), a.Count + 1);
        }
        return map.Values.Where(a => a.Count >= 2).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static MediaItem AlbumItem((string Name, string? Artist, Track? Cover, int Count) a)
        => Item("list|album:" + Uri.EscapeDataString(a.Name) + ":" + Uri.EscapeDataString(a.Artist ?? ""), a.Name, a.Artist,
            CoverOf(a.Cover) ?? Icon("ic_car_album"), false);

    private static List<MediaItem> AlbumItems(List<Track> songs, int page)
        => Paged(AlbumRows(songs), page, a => a.Name, AlbumItem, "albumpage", "ic_car_album");

    private static Android.Net.Uri? ArtistPicture(string name)
    {
        if (ArtistPictures.HasMine(name)) return FileUri(ArtistPictures.Mine(name));
        return ArtistSites.Get(name) is { } site ? FileUri(AppPaths.ArtistPicture(site.PictureKey)) : null;
    }

    private static Func<List<MediaItem>>? ListItems(MainViewModel s, string key, int page)
    {
        if (ListOf(s, key) is not { } list) return null;
        var songs = list.PlayOrder.Select(t => t.T).ToList();
        return () =>
        {
            var ordered = songs.Count > PageSize ? songs.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase).ToList() : songs;
            if (page >= 0) return ordered.Skip(page * PageSize).Take(PageSize).Select(t => SongItem(key, t)).ToList();
            var items = new List<MediaItem>();
            if (songs.Count > 1) items.Add(Item("shuffle|" + key, L.T("Shuffle"), null, Icon("ic_car_shuffle"), true));
            if (songs.Count <= PageSize) items.AddRange(songs.Select(t => SongItem(key, t)));
            else
                items.AddRange(ordered.Chunk(PageSize).Select((piece, i) =>
                    Item($"page|{key}|{i}", Range(piece[0].Title, piece[^1].Title), L.Count(piece.Length, "1 song", "{0} songs"), Icon("ic_car_music"), false)));
            return items;
        };
    }

    // On the UI thread: a list as it plays.
    private static ITrackList? ListOf(MainViewModel s, string key)
    {
        if (key == "all")
        {
            s.LibraryPage.Rebuild();
            return s.LibraryPage;
        }
        if (key == "recent") return s.Home.RecentList;
        if (key.StartsWith("pl:", StringComparison.Ordinal))
            return s.Playlists.FirstOrDefault(p => p.Id == key[3..]) is { } vm ? new PlaylistPageViewModel(vm, s) : null;
        if (key.StartsWith("artist:", StringComparison.Ordinal)) return VoicePlay.ArtistList(s, Uri.UnescapeDataString(key[7..]));
        if (key.StartsWith("album:", StringComparison.Ordinal))
        {
            var parts = key[6..].Split(':');
            var artist = parts.Length > 1 && parts[1].Length > 0 ? Uri.UnescapeDataString(parts[1]) : null;
            return VoicePlay.AlbumList(s, Uri.UnescapeDataString(parts[0]), artist);
        }
        if (key.StartsWith("tag:", StringComparison.Ordinal))
            return s.Tags.FirstOrDefault(t => t.Id == key[4..]) is { } tag ? VoicePlay.TagList(s, tag) : null;
        return null;
    }

    // "A – C", or "Sa – Sh" inside one letter.
    private static string Range(string first, string last)
    {
        static string Head(string text, int n)
        {
            text = text.Trim();
            return text.Length == 0 ? "?" : char.ToUpper(text[0]) + text[1..Math.Min(n, text.Length)];
        }
        var (a, b) = (Head(first, 1), Head(last, 1));
        if (a == b) (a, b) = (Head(first, 2), Head(last, 2));
        return a == b ? a : a + " – " + b;
    }

    private static MediaItem SongItem(string key, Track t, Bundle? extras = null)
        => Item($"song|{key}|{t.Id}", t.Title, t.DisplayArtist, CoverOf(t) ?? Icon("ic_car_music"), true, extras);

    private static MediaItem Item(string id, string title, string? subtitle, Android.Net.Uri? icon, bool playable, Bundle? extras = null)
    {
        var description = new MediaDescription.Builder().SetMediaId(id)!.SetTitle(title)!.SetSubtitle(subtitle)!.SetIconUri(icon)!.SetExtras(extras)!.Build()!;
        return new MediaItem(description, playable ? MediaItemFlags.Playable : MediaItemFlags.Browsable);
    }

    // ------------------------------------------------------------------ pictures

    private static Android.Net.Uri Icon(string name) => Android.Net.Uri.Parse($"android.resource://{Ctx.PackageName}/drawable/{name}")!;

    private static Android.Net.Uri? CoverOf(Track? t) => t is { HasCover: true } ? FileUri(AppPaths.TrackCover(t.Id)) : null;

    public static Android.Net.Uri? CoverUri(string? path) => path != null && Clients.Count > 0 ? FileUri(path) : null;

    // Android Auto reads pictures by address, from its own process: the app's FileProvider, not open to anyone, with the permission
    // given to the clients checked by CarClients, one picture at a time.
    private static Android.Net.Uri? FileUri(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(Ctx, Ctx.PackageName + ".files", new Java.IO.File(path))!;
            lock (Clients)
                foreach (var client in Clients) Ctx.GrantUriPermission(client, uri, ActivityFlags.GrantReadUriPermission);
            return uri;
        }
        // (the covers of the other device, while linked: outside the provider's folders, their place shows an icon)
        catch (Java.Lang.IllegalArgumentException) { return null; }
    }

    // ------------------------------------------------------------------ playing

    // Play with nothing asked for ("Ok Google, play music on…", the car just connected): the last song from where it was, or the
    // whole library shuffled.
    public static void PlayDefault()
    {
        if (Open() is { } s && VoicePlay.PlayDefault(s) is { } problem) Media.SetError(problem);
    }

    private static void ShuffleList(MainViewModel s, string key)
    {
        if (ListOf(s, key) is { PlayOrder.Count: > 0 } list) s.Player.PlayAll(list, true);
        else Media.SetError(L.T("No songs yet"));
    }

    public static void PlayId(string? id)
    {
        SetActive(true);
        if (Open() is not { } s) return;
        var parts = (id ?? "").Split('|');
        switch (parts[0])
        {
            case "shuffle" when parts.Length == 2:
                ShuffleList(s, parts[1]);
                return;
            case "mix" when parts.Length == 2 && s.Home.ForYou.FirstOrDefault(m => m.Id == parts[1]) is { } mix:
                mix.PlayCommand.Execute(null);
                return;
            case "station" when parts.Length == 2 && s.Stations.Saved.FirstOrDefault(st => st.S.Id == parts[1]) is { } station:
                s.Stations.Play(station);
                return;
            case "profile" when parts.Length == 2 && App.Host.Profiles.Profiles.FirstOrDefault(p => p.Id == parts[1]) is { } profile:
                if (profile.Id != s.Profile.Info.Id) App.Host.OpenProfile(profile);
                PlayDefault();
                return;
            case "song" when parts.Length == 3 && ListOf(s, parts[1]) is { } list && list.PlayOrder.FirstOrDefault(t => t.Id == parts[2]) is { } song:
                s.Player.PlayFrom(InOrder(list), song);
                return;
            // (still a song, no longer in the list the car has on screen: the recently played move on)
            case "song" when parts.Length == 3 && s.Get(parts[2]) is { } alone:
                s.Player.PlaySingle(alone);
                return;
        }
        Media.SetError(L.T("This is no longer in your library."));
    }

    // A long list is shown by title: what follows the song tapped is what the driver saw after it.
    private static ITrackList InOrder(ITrackList list) => list.PlayOrder.Count <= PageSize ? list
        : new TrackListSource(list.ContextId, list.ContextName, list.PlayOrder.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase).ToList());

    public static void SkipTo(long queueId)
    {
        if (queueId > 0 && Open() is { } s) _ = s.Player.JumpTo((int)queueId - 1);
    }

    // ------------------------------------------------------------------ the voice

    // "Ok Google, play … on Ultimate MP3 Player", in the car or on the phone (VoiceActivity): what the Assistant understood is in the
    // extras, when it did (VoicePlay). What isn't found is said on the car's screen, and in a toast for the phone.
    public static void Search(string? query, Bundle? extras)
    {
        SetActive(true);
        if (Open() is not { } s) return;
        var request = new VoiceRequest(query ?? "", FocusOf(extras?.GetString("android.intent.extra.focus")),
            extras?.GetString(MediaStore.ExtraMediaTitle), extras?.GetString(MediaStore.ExtraMediaArtist), extras?.GetString("android.intent.extra.album"),
            extras?.GetString("android.intent.extra.playlist"), extras?.GetString("android.intent.extra.genre"));
        _ = Say(VoicePlay.PlayAsync(s, request, Media.SetSearching));
    }

    private static VoiceFocus FocusOf(string? focus) => focus switch
    {
        "vnd.android.cursor.item/audio" => VoiceFocus.Song,
        "vnd.android.cursor.item/artist" => VoiceFocus.Artist,
        "vnd.android.cursor.item/album" => VoiceFocus.Album,
        "vnd.android.cursor.item/playlist" => VoiceFocus.Playlist,
        "vnd.android.cursor.item/genre" => VoiceFocus.Genre,
        _ => VoiceFocus.Any,
    };

    private static async Task Say(Task<string?> asked)
    {
        try
        {
            if (await asked is not { } problem) return;
            Media.SetError(problem);
            Android.Widget.Toast.MakeText(Ctx, problem, Android.Widget.ToastLength.Long)?.Show();
        }
        catch (Exception ex) { App.Log(ex); }
    }

    // The car's buttons (null: the other way round) and the Assistant's "shuffle", "repeat".
    public static void SetShuffle(bool? on)
    {
        if (Open() is { } s) s.Player.Shuffle = on ?? !s.Player.Shuffle;
    }

    public static void SetRepeat(RepeatMode? mode)
    {
        if (Open() is not { } s) return;
        if (mode is { } m && !s.Player.InRoom) s.Player.Repeat = m;
        else s.Player.CycleRepeatCommand.Execute(null);
    }

    // ------------------------------------------------------------------ the queue

    private static void PublishQueue(PlayerViewModel p)
    {
        if (!Active) return;
        var rows = p.UpNext.Select(r => (Id: (long)r.Index + 1, r.Track.T)).ToList();
        var current = p.Current?.T;
        var name = p.ContextName;
        int version = ++_queueVersion;
        Task.Run(() =>
        {
            var items = new List<QueueItem>();
            if (current != null) items.Add(Queued(0, current));
            items.AddRange(rows.Select(r => Queued(r.Id, r.T)));
            Main.Post(() =>
            {
                if (version == _queueVersion) Media.SetQueue(items, name);
            });
        });
    }

    private static QueueItem Queued(long id, Track t)
    {
        var description = new MediaDescription.Builder().SetTitle(t.Title)!.SetSubtitle(t.DisplayArtist)!.SetIconUri(CoverOf(t) ?? Icon("ic_car_music"))!.Build()!;
        return new QueueItem(description, id);
    }
}
