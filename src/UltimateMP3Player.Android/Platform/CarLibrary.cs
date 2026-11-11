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
// "station|<id>", "artistpage|<n>". The keys: "all", "recent", "pl:<playlist>", "artist:<name, escaped>".
public static class CarLibrary
{
    public const string Root = "root";
    private const string HomeTab = "home", SongsTab = "songs", PlaylistsTab = "playlists", ArtistsTab = "artists";
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
            case SongsTab: return SongsItems(s);
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
            case "list" when parts.Length == 2: return ListItems(s, parts[1], -1);
            case "page" when parts.Length == 3 && int.TryParse(parts[2], out var n): return ListItems(s, parts[1], n);
        }
        return null;
    }

    private static List<MediaItem> Tabs() => new List<MediaItem>
    {
        Item(HomeTab, L.T("Home"), null, Icon("ic_car_home"), false, Style(PlayableHint, GridStyle)),
        Item(SongsTab, L.T("Songs"), null, Icon("ic_car_music"), false, Style(BrowsableHint, ListStyle)),
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
        var stations = s.Stations.Saved.Select(st => (st.S.Id, st.Name, st.Subtitle)).ToList();
        return () =>
        {
            var items = new List<MediaItem>();
            var group = Group(L.T("For you"));
            foreach (var (id, title, subtitle, cover) in mixes)
                items.Add(Item("mix|" + id, title, subtitle, CoverOf(cover) ?? Icon("ic_car_playlist"), true, group));
            group = Group(L.T("Recently played"));
            foreach (var t in recent) items.Add(SongItem("recent", t, group));
            group = Group(L.T("Radio"));
            foreach (var (id, name, subtitle) in stations)
                items.Add(Item("station|" + id, name, subtitle, FileUri(AppPaths.TrackCover(Station.Prefix + id)) ?? Icon("ic_car_radio"), true, group));
            // (nothing heard yet: not an empty first page)
            if (items.Count == 0) items.Add(Item("shuffle|all", L.T("Shuffle"), null, Icon("ic_car_shuffle"), true));
            return items;
        };
    }

    private static Func<List<MediaItem>> SongsItems(MainViewModel s)
    {
        var favorites = PlaylistRows(s, s.Playlists.Where(p => p.IsFavorites));
        s.LibraryPage.Rebuild();
        int count = s.LibraryPage.PlayOrder.Count;
        return () =>
        {
            var items = new List<MediaItem> { Item("shuffle|all", L.T("Shuffle"), null, Icon("ic_car_shuffle"), true) };
            items.AddRange(favorites.Select(PlaylistItem));
            items.Add(Item("list|all", L.T("All songs"), L.Count(count, "1 song", "{0} songs"), Icon("ic_car_music"), false));
            return items;
        };
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
        MediaItem Row((string Name, Track? Cover) a) => Item("list|artist:" + Uri.EscapeDataString(a.Name), a.Name, null, ArtistPicture(a.Name) ?? CoverOf(a.Cover) ?? Icon("ic_car_person"), false);
        if (page >= 0) return all.Skip(page * PageSize).Take(PageSize).Select(Row).ToList();
        if (all.Count <= PageSize) return all.Select(Row).ToList();
        return all.Chunk(PageSize).Select((piece, i) => Item("artistpage|" + i, Range(piece[0].Name, piece[^1].Name), null, Icon("ic_car_person"), false)).ToList();
    }

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
        if (!key.StartsWith("artist:", StringComparison.Ordinal)) return null;
        var name = Uri.UnescapeDataString(key[7..]);
        var stats = s.Profile.StatsSnapshot();
        // (in the order of the artist's page)
        var songs = s.Songs().Where(t => ArtistNames.Has(t.Artist, name))
            .OrderByDescending(t => stats.GetValueOrDefault(t.Id)?.Plays ?? 0).ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(s.Vm).ToList();
        return new TrackListSource("artistpage:" + Text.Normalize(name), name, songs);
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
        if (Open() is not { } s) return;
        // (in a room or driving a computer, Play is theirs: the library must not land in the room's queue)
        if (s.Player.InRoom && s.Together.Session?.Current == null) Media.SetError(L.T("The room's queue is empty."));
        else if (s.Player.Current != null || s.Player.InRoom || s.Player.IsRemote) s.SetPlaying(true);
        else ShuffleList(s, "all");
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

    // What the Assistant understood is in the extras, when it did; otherwise only the words. Only the library: something it doesn't
    // have is said, never replaced with something else.
    public static void Search(string? query, Bundle? extras)
    {
        SetActive(true);
        if (Open() is not { } s) return;
        query = query?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            PlayDefault();
            return;
        }
        var title = extras?.GetString(MediaStore.ExtraMediaTitle);
        var artist = extras?.GetString(MediaStore.ExtraMediaArtist);
        var playlist = extras?.GetString("android.intent.extra.playlist");
        if (!string.IsNullOrEmpty(title) && PlaySongs(s, title + " " + artist, query)) return;
        if (!string.IsNullOrEmpty(playlist) && PlayPlaylist(s, playlist)) return;
        if (!string.IsNullOrEmpty(artist) && string.IsNullOrEmpty(title) && PlayArtist(s, artist)) return;
        if (PlayPlaylist(s, query) || PlayArtist(s, query) || PlaySongs(s, query, query)) return;
        if (s.Stations.SavedMatching(query).FirstOrDefault() is { } station)
        {
            s.Stations.Play(station);
            return;
        }
        Media.SetError(L.F("I can't find “{0}” in your library.", query));
    }

    private static bool PlayPlaylist(MainViewModel s, string name)
    {
        var key = Text.Normalize(name);
        if (s.Playlists.FirstOrDefault(p => Text.Normalize(p.Name) == key) is not { } vm) return false;
        var page = new PlaylistPageViewModel(vm, s);
        if (page.PlayOrder.Count == 0) return false;
        s.Player.PlayAll(page, s.Player.Shuffle);
        return true;
    }

    private static bool PlayArtist(MainViewModel s, string name)
    {
        if (ListOf(s, "artist:" + Uri.EscapeDataString(name)) is not { PlayOrder.Count: > 0 } list) return false;
        s.Player.PlayAll(list, false);
        return true;
    }

    private static bool PlaySongs(MainViewModel s, string words, string name)
    {
        var q = new TrackQuery(words);
        if (q.IsEmpty) return false;
        var key = Text.Normalize(words);
        // (the one titled exactly that first)
        var found = s.AllVms().Where(q.Matches).OrderByDescending(t => Text.Normalize(t.Title) == key).ToList();
        if (found.Count == 0) return false;
        s.Player.PlayFrom(new TrackListSource("voice:" + key, name, found), found[0]);
        return true;
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
