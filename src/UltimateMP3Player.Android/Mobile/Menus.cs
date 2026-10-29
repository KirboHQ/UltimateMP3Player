using Avalonia.Controls;
using Avalonia.Media;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// A menu of the phone: a sheet from the bottom with the song (or playlist, tag) on top and its actions, the same ones as the
// computer apps' context menus (Linux/macOS Views\Menus.cs). Built when opened, so it lists the current playlists.
public sealed class SheetMenu
{
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    // Shown next to the title: a song (its cover) or a playlist (its cover), a tag (its colour).
    public object? Cover { get; init; }
    public List<SheetEntry> Items { get; } = new();

    public SheetMenu Add(SheetEntry e)
    {
        Items.Add(e);
        return this;
    }

    public SheetMenu Line()
    {
        if (Items.Count > 0 && Items[^1] is not SheetLine) Items.Add(new SheetLine());
        return this;
    }
}

public class SheetEntry
{
    public SheetEntry() { }

    public SheetEntry(string text, string glyph, Action? click, bool enabled = true)
    {
        Text = text;
        Glyph = glyph;
        Click = click;
        Enabled = enabled;
    }

    public string Text { get; set; } = "";
    public string? Glyph { get; set; }
    public Action? Click { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Danger { get; init; }
    // A tick (toggles in place: the sheet stays open).
    public Func<bool>? Checked { get; init; }
    // Another sheet with more choices (the computer's submenus).
    public Func<SheetMenu>? Sub { get; init; }
    public IBrush? Dot { get; init; }
    // A small icon after the text (the phone's or the computer's, Connect phone with both devices' music shown).
    public string? Mark { get; init; }
    // A line of information, not a button.
    public bool Info { get; init; }
}

public sealed class SheetLine : SheetEntry { }

public static class Menus
{
    // Segoe icon codes (Icons.Map draws them with the bundled font, like everywhere in the app).
    private const string GPlay = "", GNext = "", GQueue = "", GRadio = "", GAdd = "", GRemove = "",
        GHeart = "", GHeartFill = "", GTag = "", GLyrics = "", GEdit = "", GPicture = "", GLink = "",
        GDelete = "", GOpen = "", GExport = "", GRename = "", GCheck = "", GSelect = "", GSearch = "",
        GInfo = "", GUp = "", GDown = "", GList = "", GRefresh = "", GSave = "", GVideo = "", GClose = "", GLibrary = "";

    public static void Open(SheetMenu menu) => App.Host.View?.Sheets.ShowMenu(menu);

    // What a long press on an element of a page opens. Where the page lets you choose several, "Select" comes first (like a
    // phone's gallery): back on the list, with this one ticked.
    public static SheetMenu? MenuFor(string? kind, object? dataContext, Control source)
    {
        var menu = (kind, dataContext) switch
        {
            ("track", SongItem s) => ForTrack(s.Track, s.Row.Owner),
            ("track", TrackRow r) => ForTrack(r.Track, r.Owner),
            ("card", TrackViewModel t) => ForTrack(t, null),
            ("hit", OnlineHitViewModel h) => ForHit(h),
            ("artist", ArtistCardViewModel a) => ForArtistCard(a),
            // A finished download: the song's menu (play similar songs, playlists…), as anywhere else.
            ("download", DownloadJobViewModel { Song: { } song }) => ForTrack(song, null),
            ("download", DownloadJobViewModel job) => ForJob(job),
            ("playlist", PlaylistViewModel p) => ForPlaylist(p),
            ("tag", TagViewModel t) => ForTag(t),
            ("queue", QueueRow q) => ForQueue(q),
            ("current", _) when App.Host.Session?.Player.Current is { } cur => ForTrack(cur, null),
            ("roomitem", RoomItemViewModel r) when App.Host.Session is { InRoom: true } s => ForRoomItem(r, s.Together),
            ("member", MemberViewModel m) when App.Host.Session is { InRoom: true } s => ForMembers(s.Together, m),
            _ => null,
        };
        if (menu != null && dataContext != null && Selection.Of(source) is { IsActive: false } sel && sel.Contains(dataContext))
        {
            menu.Items.Insert(0, new SheetEntry(L.T("Select"), GSelect, () => sel.Start(dataContext)));
            // a playlist's song: its rows get the handle (≡) to drag them to another place
            if (sel.Owner is PlaylistPageViewModel)
            {
                menu.Items.Insert(1, new SheetEntry(L.T("Move"), GMove, sel.RequestReorder));
                menu.Items.Insert(2, new SheetLine());
            }
            else menu.Items.Insert(1, new SheetLine());
        }
        return menu;
    }

    // The drag handle (three lines) drawn as an icon: "Move".
    private const string GMove = "";

    private static string Short(string s) => s.Length > 48 ? s[..48] + "…" : s;


    public static SheetMenu ForTrack(TrackViewModel t, ITrackList? owner)
    {
        var main = t.Main;
        if (Station.Is(t.Id)) return ForStation(t);
        // A song of the room itself (the one playing; a song that isn't in the library).
        if (main.InRoom && main.Together.ItemFor(t) is { } roomItem && (owner == null || main.Together.IsStandIn(t)))
            return ForRoomItem(roomItem, main.Together);
        // A suggested song that isn't in the library.
        if (main.Radio.Item(t.Id) is { } suggested) return ForRadioItem(suggested, main, null);
        var player = main.Player;
        var menu = new SheetMenu { Title = t.Title, Subtitle = t.Artist, Cover = t };
        if (main.InRoom) AddRoomItems(menu, new[] { t });
        else
        {
            if (owner != null && player.Current != t) menu.Add(new(L.T("Play"), GPlay, () => player.PlayFrom(owner, t)));
            else if (player.Current != t) menu.Add(new(L.T("Play"), GPlay, () => player.PlaySingle(t.T)));
            menu.Add(new(L.T("Play next"), GNext, () => player.PlayNext(t)));
            menu.Add(new(L.T("Add to queue"), GQueue, () => player.Enqueue(t)));
            menu.Add(new(L.T("Play similar songs"), GRadio, () => player.PlayRadio(t)));
            AddGoToArtist(menu, t.Main, t.T.Artist);
            menu.Add(new(L.T("Play the artist's songs"), "", () => player.PlayArtist(t), !string.IsNullOrWhiteSpace(t.T.Artist)));
        }
        menu.Line();
        AddSave(menu, new[] { t });
        menu.Add(AddToPlaylist(t));
        if (owner?.Playlist is { } pl && !pl.IsFavorites)
            menu.Add(new(L.F("Remove from “{0}”", Short(pl.Name)), GRemove, () => main.RemoveFromPlaylist(t, pl)));
        menu.Add(FavoriteItem(t));
        menu.Add(new SheetEntry { Text = L.T("Tags"), Glyph = GTag, Sub = () => TagMenu(new[] { t }) });
        menu.Add(new SheetEntry { Text = L.T("Lyrics"), Glyph = GLyrics, Sub = () => LyricsMenu(t) });
        if (Engines.VideoDownloads && t.IsSaved && !t.HasVideo && main.Library.Get(t.Id) == t.T)
            menu.Add(new(L.T("Find the video…"), GVideo, () => _ = main.FindVideo(t)));
        menu.Line();
        AddEditItems(menu, t);
        return menu;
    }

    // A song's row pulled to the right (RowSwipe): its menu's "Add to queue" (after the songs added before it, ahead of the
    // list's, like Spotify's), where the menu has it (not a suggested song, not a song of the room itself, not without the
    // room's permission).
    public static Action? AddToQueue(TrackViewModel t, ITrackList? owner)
    {
        var main = t.Main;
        if (main.Radio.Item(t.Id) != null) return null;
        if (!main.InRoom) return () => main.Player.Enqueue(t);
        var room = main.Together;
        if (room.ItemFor(t) != null && (owner == null || room.IsStandIn(t)) || !room.CanAdd) return null;
        return () => room.Add(new[] { t });
    }

    // "Save on the device" for the songs not saved (or how far their save is).
    private static void AddSave(SheetMenu menu, IReadOnlyList<TrackViewModel> tracks, Action? then = null)
    {
        var main = tracks[0].Main;
        var cloud = tracks.Where(main.ToSave).ToList();
        if (cloud.Count > 0)
            menu.Add(new(tracks.Count == 1 ? L.T("Save to device") : L.F("Save to device ({0})", cloud.Count), GSave, () =>
            {
                _ = main.SaveToDevice(cloud);
                then?.Invoke();
            }));
        else if (tracks.Any(t => t.IsSaving))
            menu.Add(new SheetEntry(L.T("Saving to device…"), GSave, null) { Info = true });
        // The quality of the ones with a link: downloaded again in it (the saved ones), kept for when they're heard (the cloud).
        var linked = tracks.Where(t => t.T.HasLink && main.Library.Get(t.Id) == t.T).ToList();
        if (linked.Count > 0)
            menu.Add(new(linked.Any(t => t.T.HasSavedFile) ? L.T("Download again / quality…") : L.T("Listening quality…"), "", () =>
            {
                _ = main.ChangeQuality(linked);
                then?.Invoke();
            }));
    }

    // (connected: the device is asked, whichever music is shown)
    private static SheetEntry FavoriteItem(TrackViewModel t) => t.Main.IsFavorite(t.Id, menu: true)
        ? new SheetEntry(L.T("Remove from Favorites"), GHeartFill, () => t.Main.ToggleFavorite(t, menu: true))
        : new SheetEntry(L.T("Add to Favorites"), GHeart, () => t.Main.ToggleFavorite(t, menu: true));

    // Edit, cover, link, delete: the same everywhere.
    private static void AddEditItems(SheetMenu menu, TrackViewModel t)
    {
        var main = t.Main;
        menu.Add(new(L.T("Edit info…"), GEdit, () => _ = main.EditTrack(t)));
        menu.Add(new(L.T("Change cover…"), GPicture, () => _ = main.ChangeTrackCover(t)));
        if (t.T.SourceUrl is { } url && url.StartsWith("http"))
        {
            menu.Add(new(L.T("Copy original link"), GLink, () => { Ui.CopyText(url); main.Toast(L.T("Link copied")); }));
            menu.Add(new(L.T("Open the original link"), GOpen, () => MainViewModel.OpenUrl(url)));
        }
        menu.Line();
        menu.Add(new SheetEntry(L.T("Delete…"), GDelete, () => _ = main.DeleteTrack(t)) { Danger = true });
    }

    public static SheetMenu AddToPlaylistMenu(TrackViewModel t)
    {
        var main = t.Main;
        var menu = new SheetMenu { Title = L.T("Add to playlist"), Subtitle = t.Title, Cover = t };
        menu.Add(new(L.T("New playlist…"), GAdd, () => _ = main.NewPlaylist(t)));
        menu.Line();
        // (connected: both devices' playlists, whichever music is shown)
        foreach (var p in main.MenuPlaylists())
        {
            var pl = p.P;
            bool has = main.ProfileOf(pl).Contains(pl, t.Id);
            menu.Add(new SheetEntry(Short(p.Name), main.DeviceGlyph(p.Id, true) ?? (p.IsFavorites ? GHeartFill : GList), () => main.AddToPlaylist(t, pl), !has)
            {
                Checked = has ? () => true : null,
                // (the tick takes the icon's place: which device's list it is goes after the name)
                Mark = has ? main.DeviceGlyph(p.Id, true) : null,
            });
        }
        return menu;
    }

    private static SheetEntry AddToPlaylist(TrackViewModel t)
        => new() { Text = L.T("Add to playlist"), Glyph = GAdd, Sub = () => AddToPlaylistMenu(t) };

    // What the song has (with times, plain, nothing...), and the actions: show, search online (again), delete.
    public static SheetMenu LyricsMenu(TrackViewModel t)
    {
        var main = t.Main;
        bool searching = main.Host.Lyrics.IsSearching(t.T);
        var state = searching ? L.T("Searching…") : t.T.Lyrics switch
        {
            LyricsKind.Synced => L.T("Synced: lights up as you listen"),
            LyricsKind.Plain => L.T("Without timing"),
            LyricsKind.Instrumental => L.T("Instrumental song"),
            LyricsKind.None => L.T("Not found online"),
            _ => L.T("Not searched yet"),
        };
        var menu = new SheetMenu { Title = L.T("Lyrics"), Subtitle = t.Title, Cover = t };
        menu.Add(new SheetEntry(state, GInfo, null) { Info = true });
        menu.Line();
        if (t.HasLyrics && main.Player.Current == t) menu.Add(new(L.T("Show lyrics"), GLyrics, main.ShowLyrics));
        menu.Add(new(t.HasLyrics ? L.T("Search online again") : L.T("Search online"), GSearch, () => main.SearchLyrics(new[] { t }), !searching));
        menu.Add(new(L.T("Choose lyrics…"), GSelect, () => _ = main.ChooseLyrics(t), !searching));
        if (t.HasLyrics) menu.Add(new SheetEntry(L.T("Delete lyrics"), GDelete, () => main.DeleteLyrics(t)) { Danger = true });
        return menu;
    }

    // ------------------------------------------------------------------ songs found online

    // A song found online (search): heard without saving it, like a link's songs, or opened to download it. Not an album or a
    // playlist (a tap opens their card). The same as the computer's (Menus.Shared.cs).
    // An internet radio (the one playing): keep it, the other radios, its site.
    private static SheetMenu ForStation(TrackViewModel t)
    {
        var main = t.Main;
        bool saved = main.IsFavorite(t.Id);
        var menu = new SheetMenu { Title = t.Title, Subtitle = t.Artist, Cover = t };
        menu.Add(new(saved ? L.T("Remove from your radios") : L.T("Save to your radios"), saved ? "\uEB52" : "\uEB51", () => main.Stations.Toggle(t.Id)));
        menu.Add(new(L.T("All radios"), "\uE93E", main.OpenStations));
        if (main.Stations.HomepageOf(t.T) is { } site) menu.Add(new(L.T("The radio's website"), "\uE774", () => MainViewModel.OpenUrl(site)));
        return menu;
    }

    public static SheetMenu? ForHit(OnlineHitViewModel h)
    {
        if (!h.IsSong) return null;
        var main = h.Main;
        var menu = new SheetMenu { Title = h.Title, Subtitle = h.Subtitle };
        if (main.InRoom) menu.Add(new(L.T("Add to the room's queue"), GQueue, () => h.QueueAction?.Invoke(), h.QueueAction != null));
        else
        {
            menu.Add(new(L.T("Play"), GPlay, h.Play));
            menu.Add(new(L.T("Play next"), GNext, () => { if (h.Song() is { } t) main.Player.PlayNext(t); }));
            menu.Add(new(L.T("Add to queue"), GQueue, () => h.QueueAction?.Invoke()));
            menu.Add(new(L.T("Play similar songs"), GRadio, () => { if (h.Song() is { } t) main.Player.PlayRadio(t); }));
            AddGoToArtist(menu, main, h.Hit.Artist);
            menu.Add(new(L.T("Play the artist's songs"), "", () => { if (h.Song() is { } t) main.Player.PlayArtist(t); },
                !string.IsNullOrWhiteSpace(h.Hit.Artist)));
        }
        menu.Line();
        menu.Add(new(L.T("Download|verb"), GSave, () => h.DownloadCommand.Execute(null)));
        menu.Add(new(L.T("Copy link"), GLink, () => { Ui.CopyText(h.Hit.Url); main.Toast(L.T("Link copied")); }));
        return menu;
    }

    // ------------------------------------------------------------------ suggested songs

    public static SheetMenu ForRadioItem(RadioItemViewModel item, MainViewModel main, QueueRow? row)
    {
        var player = main.Player;
        var t = main.Vm(item.T);
        var menu = new SheetMenu { Title = t.Title, Subtitle = item.FromText, Cover = t };
        if (row != null)
        {
            int last = player.UpNext.LastOrDefault()?.Index ?? row.Index;
            menu.Add(new(L.T("Play now"), GPlay, () => _ = player.JumpTo(row.Index)));
            menu.Add(new(L.T("Move to top"), GUp, () => player.MoveUpcoming(row.Index, 0), row.Index > 0));
            menu.Add(new(L.T("Move to bottom"), GDown, () => player.MoveUpcoming(row.Index, last), row.Index < last));
            menu.Add(new(L.T("Remove from Up next"), GRemove, () => player.RemoveUpcoming(row.Index)));
        }
        else if (player.Current == t) menu.Add(new(L.T("Skip"), GNext, () => player.NextCommand.Execute(null)));
        // (a card of the recently played, a row of the statistics)
        else menu.Add(new(L.T("Play"), GPlay, () => player.PlaySingle(t.T)));
        if (item.IsFailed) menu.Add(new(L.T("Try downloading it again"), GRefresh, () => main.Radio.Prepare(item.Id)));
        menu.Line();
        // Saved on the device, or only kept in the library (a playlist, the favourites: in the cloud, nothing downloaded).
        menu.Add(new(L.T("Add to library"), GLibrary, () => main.AddToLibrary(t)));
        menu.Add(new(L.T("Save to device"), GSave, () =>
        {
            // (connected to the other device: it asks where)
            if (main.Shared is { Ready: true }) _ = main.SaveToDevice(new[] { t });
            else _ = main.Radio.Save(item, null);
        }));
        menu.Add(AddToPlaylist(t));
        menu.Add(FavoriteItem(t));
        menu.Add(new SheetEntry { Text = L.T("Lyrics"), Glyph = GLyrics, Sub = () => LyricsMenu(t) });
        menu.Add(new(L.T("Play similar songs"), GRadio, () => player.PlayRadio(t)));
        AddGoToArtist(menu, t.Main, t.T.Artist);
        menu.Add(new(L.T("Play the artist's songs"), "", () => player.PlayArtist(t), !string.IsNullOrWhiteSpace(t.T.Artist)));
        menu.Add(new(L.T("Copy original link"), GLink, () => { Ui.CopyText(item.Song.Url); main.Toast(L.T("Link copied")); }));
        if (row == null && player.Current != t)
        {
            menu.Line();
            menu.Add(new SheetEntry(L.T("Delete…"), GDelete, () => _ = main.DeleteTracks(new[] { t })) { Danger = true });
        }
        return menu;
    }

    // ------------------------------------------------------------------ "next up"

    public static SheetMenu ForQueue(QueueRow row)
    {
        var t = row.Track;
        var player = row.Player;
        if (player.InRoom && t.Main.Together.ItemFor(t) is { } roomItem) return ForRoomItem(roomItem, t.Main.Together);
        if (row.Radio is { } suggested) return ForRadioItem(suggested, t.Main, row);
        int last = player.UpNext.LastOrDefault()?.Index ?? row.Index;
        var menu = new SheetMenu { Title = t.Title, Subtitle = t.Artist, Cover = t };
        menu.Add(new(L.T("Play now"), GPlay, () => _ = player.JumpTo(row.Index)));
        menu.Add(new(L.T("Move to top"), GUp, () => player.MoveUpcoming(row.Index, 0), row.Index > 0));
        menu.Add(new(L.T("Move to bottom"), GDown, () => player.MoveUpcoming(row.Index, last), row.Index < last));
        menu.Add(new(L.T("Remove from Up next"), GRemove, () => player.RemoveUpcoming(row.Index)));
        menu.Add(new(L.T("Play similar songs"), GRadio, () => player.PlayRadio(t)));
        AddGoToArtist(menu, t.Main, t.T.Artist);
        menu.Add(new(L.T("Play the artist's songs"), "", () => player.PlayArtist(t), !string.IsNullOrWhiteSpace(t.T.Artist)));
        menu.Line();
        AddSave(menu, new[] { t });
        menu.Add(AddToPlaylist(t));
        menu.Add(FavoriteItem(t));
        menu.Add(new SheetEntry { Text = L.T("Tags"), Glyph = GTag, Sub = () => TagMenu(new[] { t }) });
        menu.Add(new SheetEntry { Text = L.T("Lyrics"), Glyph = GLyrics, Sub = () => LyricsMenu(t) });
        menu.Line();
        AddEditItems(menu, t);
        return menu;
    }

    // A download still going, or one that failed (one that worked has its song's menu).
    public static SheetMenu? ForJob(DownloadJobViewModel job)
    {
        var menu = new SheetMenu { Title = job.Title, Subtitle = job.StatusText };
        if (job.CanRetry) menu.Add(new(L.T("Retry"), GRefresh, () => job.RetryCommand.Execute(null)));
        if (job.CanRetryYouTube) menu.Add(new(L.T("Retry with YouTube"), GSearch, () => job.RetryYouTubeCommand.Execute(null)));
        if (job.IsActive) menu.Add(new(L.T("Cancel"), GClose, () => job.CancelCommand.Execute(null)));
        if (job.IsFinished) menu.Add(new SheetEntry(L.T("Remove from list"), GDelete, () => job.RemoveCommand.Execute(null)) { Danger = true });
        return menu.Items.Count > 0 ? menu : null;
    }

    // ------------------------------------------------------------------ several things chosen

    public static SheetMenu? ForSelection(Selection sel) => sel.Kind switch
    {
        "queue" => ForQueueRows(sel),
        "download" => ForJobs(sel),
        "playlist" => ForPlaylists(sel),
        _ => ForSongs(sel),
    };

    private static SheetMenu? ForSongs(Selection sel)
    {
        var tracks = sel.Tracks;
        if (tracks.Count == 0) return null;
        var main = tracks[0].Main;
        var menu = new SheetMenu { Title = L.F("{0} songs selected", tracks.Count), Cover = tracks[0] };
        menu.Add(new(L.T("Play"), GPlay, () => { main.PlaySelection(tracks); sel.Stop(); }));
        menu.Add(new(L.T("Add to queue"), GQueue, () => { main.Enqueue(tracks); sel.Stop(); }));
        menu.Line();
        AddSave(menu, tracks, sel.Stop);
        menu.Add(new SheetEntry { Text = L.T("Add to playlist"), Glyph = GAdd, Sub = () => AddManyToPlaylist(tracks, sel) });
        if (sel.Owner?.Playlist is { } pl)
            menu.Add(new(L.F("Remove from “{0}”", Short(PlaylistViewModel.DisplayName(pl))), GRemove, () => { main.RemoveFromPlaylist(tracks, pl); sel.Stop(); }));
        menu.Add(new(L.T("Add to Favorites"), GHeart, () => { main.AddToFavorites(tracks); sel.Stop(); }));
        menu.Add(new SheetEntry { Text = L.T("Tags"), Glyph = GTag, Sub = () => TagMenu(tracks) });
        menu.Add(new(L.F("Search lyrics online ({0})", tracks.Count), GLyrics, () => main.SearchLyrics(tracks)));
        menu.Line();
        menu.Add(new SheetEntry(L.F("Delete {0} songs…", tracks.Count), GDelete, () => _ = DeleteSongs(sel, tracks)) { Danger = true });
        return menu;
    }

    // The choosing ends only once something was deleted: closing the sheet without choosing keeps what was chosen.
    private static async Task DeleteSongs(Selection sel, List<TrackViewModel> tracks)
    {
        var main = tracks[0].Main;
        if (await main.DeleteTracks(tracks)) sel.Stop();
    }

    private static async Task DeletePlaylists(Selection sel, List<PlaylistViewModel> lists)
    {
        if (App.Host.Session is { } main && await main.DeletePlaylists(lists)) sel.Stop();
    }

    // The export sheet: the choosing ends once the pack is made (closed before: still chosen).
    private static void ExportPlaylists(Selection sel, List<PlaylistViewModel> lists)
    {
        if (App.Host.Session is { } main) PackDialogs.Export(main, many: lists, done: sel.Stop);
    }

    private static SheetMenu? ForQueueRows(Selection sel)
    {
        var rows = sel.QueueRows;
        if (rows.Count == 0) return null;
        var player = rows[0].Player;
        var main = rows[0].Track.Main;
        var places = rows.Select(r => r.Index).ToList();
        var songs = rows.Select(r => r.Track).Distinct().ToList();
        var menu = new SheetMenu { Title = L.F("{0} songs selected", rows.Count), Cover = songs[0] };
        menu.Add(new(L.T("Move to top"), GUp, () => { player.MoveUpcomingToTop(places); sel.Stop(); }));
        menu.Add(new(L.T("Move to bottom"), GDown, () => { player.MoveUpcomingToBottom(places); sel.Stop(); }));
        menu.Add(new(L.T("Remove from Up next"), GRemove, () => { player.RemoveUpcoming(places); sel.Stop(); }));
        // (songs of a room: kept one at a time, from their own menu)
        if (player.InRoom) return menu;
        menu.Line();
        AddSave(menu, songs, sel.Stop);
        menu.Add(new SheetEntry { Text = L.T("Add to playlist"), Glyph = GAdd, Sub = () => AddManyToPlaylist(songs, sel) });
        menu.Add(new(L.T("Add to Favorites"), GHeart, () => { main.AddToFavorites(songs); sel.Stop(); }));
        return menu;
    }

    private static SheetMenu? ForJobs(Selection sel)
    {
        var jobs = sel.Jobs;
        if (jobs.Count == 0 || App.Host.Session is not { } main) return null;
        var queue = main.Queue;
        int failed = jobs.Count(j => j.CanRetry), youTube = jobs.Count(j => j.CanRetryYouTube), active = jobs.Count(j => j.IsActive);
        var menu = new SheetMenu { Title = L.Count(jobs.Count, "1 download selected", "{0} downloads selected") };
        menu.Add(new(L.F("Retry ({0})", failed), GRefresh, () => { queue.Retry(jobs); sel.Stop(); }, failed > 0));
        menu.Add(new(L.F("Retry on YouTube ({0})", youTube), GSearch, () => { queue.RetryOnYouTube(jobs); sel.Stop(); }, youTube > 0));
        menu.Add(new(L.F("Cancel ({0})", active), GClose, () => { queue.Cancel(jobs); sel.Stop(); }, active > 0));
        menu.Line();
        menu.Add(new SheetEntry(L.T("Remove from list"), GDelete, () => { queue.Remove(jobs); sel.Stop(); }) { Danger = true });
        return menu;
    }

    private static SheetMenu? ForPlaylists(Selection sel)
    {
        var lists = sel.Playlists;
        if (lists.Count == 0 || App.Host.Session is not { } main) return null;
        var menu = new SheetMenu { Title = L.Count(lists.Count, "1 playlist selected", "{0} playlists selected"), Cover = lists[0] };
        menu.Add(new(L.T("Play"), GPlay, () => { main.PlayPlaylists(lists); sel.Stop(); }));
        menu.Add(new(L.T("Add to queue"), GQueue, () => { main.EnqueuePlaylists(lists); sel.Stop(); }));
        menu.Line();
        int cloud = main.SongsOf(lists).Count(main.ToSave);
        if (cloud > 0) menu.Add(new(L.F("Save to device ({0})", cloud), GSave, () => { _ = main.SavePlaylists(lists); sel.Stop(); }));
        menu.Add(new SheetEntry { Text = L.T("Tags"), Glyph = GTag, Sub = () => PlaylistsTagMenu(lists) });
        if (lists.All(p => !p.IsOther)) menu.Add(new(L.T("Export to a .ump file…"), GExport, () => ExportPlaylists(sel, lists)));
        if (lists.Any(p => !p.IsFavorites))
        {
            menu.Line();
            menu.Add(new SheetEntry(L.Count(lists.Count(p => !p.IsFavorites), "Delete playlist…", "Delete {0} playlists…"), GDelete,
                () => _ = DeletePlaylists(sel, lists)) { Danger = true });
        }
        return menu;
    }

    // The buttons of the bar at the bottom while choosing: the main actions for what's chosen (the rest in the "⋮" on top).
    public static List<PickAction> SelectionActions(Selection sel)
    {
        var list = new List<PickAction>();
        var main = App.Host.Session;
        if (main == null) return list;
        switch (sel.Kind)
        {
            case "queue":
            {
                var rows = sel.QueueRows;
                var places = rows.Select(r => r.Index).ToList();
                var player = main.Player;
                list.Add(new(GUp, L.T("To top"), () => { if (places.Count > 0) { player.MoveUpcomingToTop(places); sel.Stop(); } }));
                list.Add(new(GDown, L.T("To bottom"), () => { if (places.Count > 0) { player.MoveUpcomingToBottom(places); sel.Stop(); } }));
                var cloud = player.InRoom ? new List<TrackViewModel>() : rows.Select(r => r.Track).Where(main.ToSave).Distinct().ToList();
                if (cloud.Count > 0) list.Add(new(GSave, L.T("Save"), () => { _ = main.SaveToDevice(cloud); sel.Stop(); }));
                list.Add(new(GRemove, L.T("Remove"), () => { if (places.Count > 0) { player.RemoveUpcoming(places); sel.Stop(); } }, danger: true));
                break;
            }
            case "download":
            {
                var jobs = sel.Jobs;
                if (jobs.Any(j => j.CanRetry)) list.Add(new(GRefresh, L.T("Retry"), () => { main.Queue.Retry(jobs); sel.Stop(); }));
                if (jobs.Any(j => j.CanRetryYouTube)) list.Add(new(GSearch, "YouTube", () => { main.Queue.RetryOnYouTube(jobs); sel.Stop(); }));
                if (jobs.Any(j => j.IsActive)) list.Add(new(GClose, L.T("Cancel"), () => { main.Queue.Cancel(jobs); sel.Stop(); }));
                list.Add(new(GDelete, L.T("Remove"), () => { main.Queue.Remove(jobs); sel.Stop(); }, danger: true));
                break;
            }
            case "playlist":
            {
                var lists = sel.Playlists;
                list.Add(new(GPlay, L.T("Play"), () => { if (lists.Count > 0) { main.PlayPlaylists(lists); sel.Stop(); } }));
                if (main.SongsOf(lists).Any(main.ToSave)) list.Add(new(GSave, L.T("Save"), () => { _ = main.SavePlaylists(lists); sel.Stop(); }));
                if (lists.All(p => !p.IsOther)) list.Add(new(GExport, L.T("Export"), () => { if (lists.Count > 0) ExportPlaylists(sel, lists); }));
                if (lists.Any(p => !p.IsFavorites))
                    list.Add(new(GDelete, L.T("Delete"), () => _ = DeletePlaylists(sel, lists), danger: true));
                break;
            }
            default:
            {
                var tracks = sel.Tracks;
                list.Add(new(GPlay, L.T("Play"), () => { if (tracks.Count > 0) { main.PlaySelection(tracks); sel.Stop(); } }));
                // a playlist: its rows get the handle (≡) to drag them to another place
                if (sel.Owner is PlaylistPageViewModel) list.Add(new(GMove, L.T("Move"), sel.RequestReorder));
                list.Add(new(GQueue, L.T("Queue"), () => { if (tracks.Count > 0) { main.Enqueue(tracks); sel.Stop(); } }));
                list.Add(new(GAdd, "Playlist", () => { if (tracks.Count > 0) Open(AddManyToPlaylist(tracks, sel)); }));
                var cloud = tracks.Where(main.ToSave).ToList();
                if (cloud.Count > 0) list.Add(new(GSave, L.T("Save"), () => { _ = main.SaveToDevice(cloud); sel.Stop(); }));
                list.Add(new(GDelete, L.T("Delete"), () => { if (tracks.Count > 0) _ = DeleteSongs(sel, tracks); }, danger: true));
                break;
            }
        }
        return list;
    }

    public static SheetMenu AddManyToPlaylist(IReadOnlyList<TrackViewModel> tracks, Selection? sel = null)
    {
        var main = tracks[0].Main;
        var menu = new SheetMenu { Title = L.T("Add to playlist"), Subtitle = L.Count(tracks.Count, "1 song", "{0} songs"), Cover = tracks[0] };
        menu.Add(new(L.T("New playlist…"), GAdd, async () => { if (await main.NewPlaylistWith(tracks)) sel?.Stop(); }));
        menu.Line();
        foreach (var p in main.MenuPlaylists())
        {
            var pl = p.P;
            menu.Add(new(Short(p.Name), main.DeviceGlyph(p.Id, true) ?? (p.IsFavorites ? GHeartFill : GList), () => { main.AddToPlaylist(tracks, pl); sel?.Stop(); }));
        }
        return menu;
    }

    // ------------------------------------------------------------------ tags

    // Tick = every song has it; the sheet stays open to tick several.
    public static SheetMenu TagMenu(IReadOnlyList<TrackViewModel> tracks)
    {
        var main = tracks[0].Main;
        var menu = new SheetMenu { Title = L.T("Tags"), Subtitle = tracks.Count == 1 ? tracks[0].Title : L.Count(tracks.Count, "1 song", "{0} songs"), Cover = tracks[0] };
        // (connected: both devices' tags, whichever music is shown)
        foreach (var tag in main.MenuTags())
        {
            var tg = tag;
            menu.Add(new SheetEntry
            {
                Text = Short(tg.Name), Dot = tg.Brush, Mark = main.DeviceGlyph(tg.Id, true),
                Checked = () => tracks.All(t => main.HasTag(t, tg)),
                Click = () => main.SetTag(tracks, tg, !tracks.All(t => main.HasTag(t, tg))),
            });
        }
        menu.Line();
        menu.Add(new(L.T("New tag…"), GAdd, () => _ = main.NewTag(tracks)));
        return menu;
    }

    public static SheetMenu PlaylistTagMenu(PlaylistViewModel p)
    {
        var main = App.Host.Session!;
        var menu = new SheetMenu { Title = L.T("Tags"), Subtitle = p.Name, Cover = p };
        foreach (var tag in main.TagsFor(p))
        {
            var tg = tag;
            menu.Add(new SheetEntry
            {
                Text = Short(tg.Name), Dot = tg.Brush,
                Checked = () => p.P.Tags.Contains(tg.Id),
                Click = () => main.SetPlaylistTag(p, tg, !p.P.Tags.Contains(tg.Id)),
            });
        }
        menu.Line();
        menu.Add(new(L.T("New tag…"), GAdd, () => _ = main.NewTag(playlist: p)));
        menu.Add(new(L.T("Put its tags on its songs…"), GTag, () => _ = main.TagPlaylistSongs(p), p.Count > 0));
        return menu;
    }

    // The tags of several playlists at once: tick = all of them have it.
    public static SheetMenu PlaylistsTagMenu(IReadOnlyList<PlaylistViewModel> lists)
    {
        var main = App.Host.Session!;
        var menu = new SheetMenu { Title = L.T("Tags"), Subtitle = L.Count(lists.Count, "1 playlist", "{0} playlists"), Cover = lists[0] };
        foreach (var tag in main.Tags.Where(t => lists.Any(p => main.ProfileOf(p.P) == main.ProfileOf(t.T))))
        {
            var tg = tag;
            menu.Add(new SheetEntry
            {
                Text = Short(tg.Name), Dot = tg.Brush, Mark = tg.DeviceGlyph,
                Checked = () => lists.All(p => p.P.Tags.Contains(tg.Id)),
                Click = () => main.SetPlaylistsTag(lists, tg, !lists.All(p => p.P.Tags.Contains(tg.Id))),
            });
        }
        menu.Line();
        menu.Add(new(L.T("New tag…"), GAdd, async () =>
        {
            if (await main.NewTag() is { } t) main.SetPlaylistsTag(lists, t, true);
        }));
        return menu;
    }

    // Tick the tags to use (the songs of a download get them); a new tag is ticked right away.
    public static SheetMenu TagPicker(ISet<string> chosen, MainViewModel main, Action changed)
    {
        var menu = new SheetMenu { Title = L.T("Tags for the downloaded songs") };
        foreach (var tag in main.Tags.Where(t => !main.IsTheirs(t.Id)))
        {
            var tg = tag;
            menu.Add(new SheetEntry
            {
                Text = Short(tg.Name), Dot = tg.Brush,
                Checked = () => chosen.Contains(tg.Id),
                Click = () =>
                {
                    if (!chosen.Remove(tg.Id)) chosen.Add(tg.Id);
                    changed();
                },
            });
        }
        menu.Line();
        menu.Add(new(L.T("New tag…"), GAdd, async () =>
        {
            if (await main.NewTag() is { } t)
            {
                chosen.Add(t.Id);
                changed();
            }
        }));
        return menu;
    }

    // The playlists the songs of a download go into: a new one (named in the card) and any of yours, as many as wanted.
    public static SheetMenu PlaylistPicker(LinkViewModel link)
    {
        var menu = new SheetMenu { Title = L.T("Add to playlists"), Subtitle = L.T("Tick as many as you like") };
        menu.Add(new SheetEntry { Text = L.T("New playlist"), Checked = () => link.MakeNew, Click = () => link.MakeNew = !link.MakeNew });
        if (link.PlaylistChoices.Count > 0) menu.Line();
        foreach (var c in link.PlaylistChoices)
        {
            var id = (string)c.Value!;
            menu.Add(new SheetEntry
            {
                Text = Short(c.Label),
                Checked = () => link.TargetIds.Contains(id),
                Click = () =>
                {
                    if (!link.TargetIds.Remove(id)) link.TargetIds.Add(id);
                    link.OnTargetsChanged();
                },
            });
        }
        return menu;
    }

    // Filter a list by tags: all of the chosen ones, or at least one.
    public static SheetMenu TagFilterMenu(TagFilter filter, MainViewModel main)
    {
        var menu = new SheetMenu { Title = L.T("Filter by tag") };
        menu.Add(new SheetEntry { Text = L.T("Songs with all the chosen tags"), Glyph = GCheck, Checked = () => filter.MatchAll, Click = () => filter.MatchAll = true });
        menu.Add(new SheetEntry { Text = L.T("Songs with at least one of the chosen tags"), Glyph = GCheck, Checked = () => !filter.MatchAll, Click = () => filter.MatchAll = false });
        menu.Line();
        foreach (var tag in main.Tags)
        {
            var tg = tag;
            menu.Add(new SheetEntry { Text = Short(tg.Name), Dot = tg.Brush, Mark = tg.DeviceGlyph, Checked = () => filter.Contains(tg.Id), Click = () => filter.Toggle(tg.Id) });
        }
        menu.Line();
        menu.Add(new(L.T("Clear the filter"), GRemove, filter.Clear, filter.IsActive));
        return menu;
    }

    // Which songs the lists show: all of them, only the ones saved on the phone, only the ones in the cloud (one choice for
    // every library page and playlist).
    public static SheetMenu StorageFilterMenu(StorageFilterViewModel filter)
    {
        var menu = new SheetMenu { Title = L.T("Show") };
        foreach (var choice in StorageFilterViewModel.Choices)
        {
            var c = choice;
            menu.Add(new SheetEntry(filter.NameOf(c), filter.Value == c ? GCheck : "", () => filter.Value = c));
        }
        return menu;
    }

    public static SheetMenu ForTag(TagViewModel t)
    {
        var main = App.Host.Session!;
        var menu = new SheetMenu { Title = t.Name, Subtitle = t.CountText, Cover = t };
        menu.Add(new(L.T("Open"), GOpen, () => main.OpenTag(t)));
        menu.Add(new(L.T("Add songs…"), GAdd, () => _ = TagDialogs.ChooseSongsAsync(t)));
        menu.Add(new(L.T("Edit…"), GEdit, () => _ = main.EditTag(t)));
        if (!main.IsTheirs(t.Id)) menu.Add(new(L.T("Export to a .ump file…"), GExport, () => main.ExportPack(tag: t)));
        menu.Line();
        menu.Add(new SheetEntry(L.T("Delete tag…"), GDelete, () => _ = main.DeleteTag(t)) { Danger = true });
        return menu;
    }

    // ------------------------------------------------------------------ playlists

    public static SheetMenu ForPlaylist(PlaylistViewModel p)
    {
        var main = App.Host.Session!;
        var menu = new SheetMenu { Title = p.Name, Subtitle = p.TotalText, Cover = p };
        menu.Add(new(L.T("Play"), GPlay, () => main.PlayPlaylistCommand.Execute(p)));
        menu.Add(new(L.T("Open"), GOpen, () => main.OpenPlaylist(p)));
        menu.Line();
        menu.Add(new SheetEntry { Text = L.T("Tags"), Glyph = GTag, Sub = () => PlaylistTagMenu(p) });
        if (!p.IsOther) menu.Add(new(L.T("Export to a .ump file…"), GExport, () => main.ExportPack(p)));
        // Its songs in the cloud saved on the device; or the space its songs take, freed (the sheet says how).
        int toSave = main.SongsOf(new[] { p }).Count(main.ToSave);
        if (toSave > 0) menu.Add(new(L.F("Save to device ({0})", toSave), GSave, () => _ = main.SavePlaylists(new[] { p })));
        if (p.Count > p.CloudCount) menu.Add(new(L.T("Free up space…"), GDelete, () => _ = main.DeleteTracks(p.Songs.Where(t => t.IsSaved).ToList())));
        if (!p.IsFavorites)
        {
            menu.Line();
            menu.Add(new(L.T("Rename…"), GRename, () => _ = main.RenamePlaylist(p)));
            menu.Add(new(L.T("Change image…"), GPicture, () => _ = main.ChangePlaylistCover(p)));
            if (p.HasCustomCover) menu.Add(new(L.T("Remove image"), GRemove, () => main.RemovePlaylistCover(p)));
            menu.Line();
            menu.Add(new SheetEntry(L.T("Delete playlist…"), GDelete, () => _ = main.DeletePlaylist(p)) { Danger = true });
        }
        return menu;
    }

    // "Go to the artist": its page; a song of several artists ("A feat. B") gets one for each.
    private static void AddGoToArtist(SheetMenu menu, MainViewModel main, string? artist)
    {
        var names = ArtistNames.Of(artist).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (names.Count <= 1) menu.Add(new(L.T("Go to the artist"), "\uE77B", () => main.OpenArtist(names.FirstOrDefault() ?? "", null), names.Count == 1));
        else foreach (var n in names) menu.Add(new(L.F("Go to {0}", n), "\uE77B", () => main.OpenArtist(n, null)));
    }

    // A tap on the artist's name (the player): its page, or which one when the song has several.
    public static void OpenArtistsOf(MainViewModel main, string? artist)
    {
        var names = ArtistNames.Of(artist).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (names.Count <= 1)
        {
            if (names.Count == 1) main.OpenArtist(names[0], null);
            return;
        }
        var menu = new SheetMenu { Title = L.T("Go to the artist") };
        foreach (var n in names) menu.Add(new(n, "\uE77B", () => main.OpenArtist(n, null)));
        Open(menu);
    }

    // An artist of a list (Artists, the search): its page, following it, who it is.
    public static SheetMenu ForArtistCard(ArtistCardViewModel a)
    {
        var menu = new SheetMenu { Title = a.Name, Subtitle = a.Subtitle };
        menu.Add(new(L.T("Open"), "\uE77B", () => a.OpenCommand.Execute(null)));
        menu.Add(new(a.FollowTip, a.FollowGlyph, () => a.FollowCommand.Execute(null)));
        menu.Add(new(L.T("Choose the artist…"), "\uE748", () => a.ChooseCommand.Execute(null)));
        return menu;
    }

    // An artist's page: who it is, its name on your songs, its picture.
    public static SheetMenu ForArtist(ArtistPageViewModel a)
    {
        var menu = new SheetMenu { Title = a.Name, Subtitle = a.Subtitle };
        menu.Add(new(L.T("Choose the artist…"), "\uE748", () => a.ChooseCommand.Execute(null)));
        if (a.Site?.Url != null) menu.Add(new(L.F("Open on {0}", a.Site.Service), "\uE774", () => a.SiteCommand.Execute(null)));
        menu.Add(new(L.T("Rename…"), GRename, () => a.RenameCommand.Execute(null)));
        menu.Add(new(L.T("Change image…"), GPicture, () => a.PictureCommand.Execute(null)));
        if (a.HasMyPicture) menu.Add(new(L.T("Remove image"), GRemove, () => a.RemovePictureCommand.Execute(null)));
        return menu;
    }

    // ------------------------------------------------------------------ Listen together

    // In a room: songs go into the room's queue (greyed out without the host's permission).
    private static void AddRoomItems(SheetMenu menu, IReadOnlyList<TrackViewModel> tracks)
    {
        var room = tracks[0].Main.Together;
        menu.Add(new(tracks.Count == 1 ? L.T("Add to the room's queue") : L.F("Add {0} songs to the room", tracks.Count), GQueue, () => room.Add(tracks), room.CanAdd));
        menu.Add(new(L.T("Play next in the room"), GNext, () => room.Add(tracks, next: true), room.CanAdd && room.CanRemove));
        if (!room.CanAdd) menu.Add(new SheetEntry(room.Denied(Core.Together.Perm.Add), GInfo, null) { Info = true });
    }

    // A song of the room: its place in the queue, and keeping it.
    public static SheetMenu ForRoomItem(RoomItemViewModel item, TogetherViewModel room)
    {
        var main = room.Main;
        var t = item.Track;
        var menu = new SheetMenu { Title = t.Title, Subtitle = item.AddedByText, Cover = t };
        if (!item.IsCurrent)
        {
            int index = room.Queue.IndexOf(item), last = room.Queue.Count - 1;
            menu.Add(new(L.T("Play it now"), GPlay, () => room.PlayNow(item.Id), room.CanRemove && room.CanSkip));
            menu.Add(new(L.T("Move to top"), GUp, () => room.MoveTo(item, 0), room.CanRemove && index > 0));
            menu.Add(new(L.T("Move to bottom"), GDown, () => room.MoveTo(item, last), room.CanRemove && index < last));
            menu.Add(new(L.T("Remove from queue"), GRemove, () => room.Remove(item), item.CanRemove));
        }
        else menu.Add(new(room.CanVote ? (room.MyVote ? L.T("Take back your vote to skip") : L.T("Vote to skip")) : L.T("Skip"), GNext, room.Skip, room.CanSkip || room.CanVote));
        menu.Line();
        // As for any song: into the library (in the cloud), or onto the device.
        if (item.InLibrary)
        {
            menu.Add(AddToPlaylist(t));
            menu.Add(FavoriteItem(t));
        }
        else
        {
            if (TogetherViewModel.HasLink(item.Item))
                menu.Add(new(L.T("Add to library (without downloading it)"), GLibrary, () => room.AddToLibrary(item, null)));
            menu.Add(new SheetEntry
            {
                Text = L.T("Add to playlist"), Glyph = GAdd, Sub = () => RoomPlaylistMenu(item, room),
                Enabled = item.IsReady || TogetherViewModel.HasLink(item.Item),
            });
        }
        if (!item.SavedOnDevice) menu.Add(new(L.T("Save to device"), GSave, () => _ = room.Save(item, null), item.IsReady));
        if (item.Item.SourceUrl is { } url && url.StartsWith("http"))
            menu.Add(new(L.T("Copy original link"), GLink, () => { Ui.CopyText(url); main.Toast(L.T("Link copied")); }));
        return menu;
    }

    private static SheetMenu RoomPlaylistMenu(RoomItemViewModel item, TogetherViewModel room)
    {
        var main = room.Main;
        var menu = new SheetMenu { Title = L.T("Add to playlist"), Subtitle = item.Track.Title, Cover = item.Track };
        menu.Add(new(L.T("New playlist…"), GAdd, async () =>
        {
            var name = await Dialogs.PromptAsync(L.T("New playlist"), L.T("Playlist name"), main.NewPlaylistName());
            if (!string.IsNullOrWhiteSpace(name)) room.AddToPlaylist(item, main.Profile.CreatePlaylist(name));
        }));
        menu.Line();
        foreach (var p in main.Playlists)
        {
            var pl = p.P;
            menu.Add(new(Short(p.Name), p.IsFavorites ? GHeartFill : GList, () => room.AddToPlaylist(item, pl)));
        }
        return menu;
    }

    // The host on a person of the room: what they may do (ticks, the sheet stays open), handing over the room, a kick.
    public static SheetMenu? ForMembers(TogetherViewModel room, MemberViewModel? clicked = null)
    {
        var who = room.SelectedMembers;
        if (clicked != null && !who.Contains(clicked)) who = new List<MemberViewModel> { clicked };
        who = who.Where(m => !m.IsMe).ToList();
        if (!room.IsHost || who.Count == 0) return null;
        var menu = new SheetMenu { Title = who.Count == 1 ? who[0].Name : L.F("{0} people", who.Count), Subtitle = who.Count == 1 ? who[0].RoleText : null };
        foreach (var (perm, text) in new[]
                 {
                     (Core.Together.Perm.Add, L.T("Can add songs")),
                     (Core.Together.Perm.Remove, L.T("Can remove and move songs")),
                     (Core.Together.Perm.Skip, L.T("Can skip songs")),
                     (Core.Together.Perm.Pause, L.T("Can pause, move forward and back and loop the song")),
                     (Core.Together.Perm.Speed, L.T("Can change the speed")),
                 })
        {
            var p = perm;
            menu.Add(new SheetEntry
            {
                Text = text,
                Checked = () => who.All(m => (m.M.Perms & p) != 0),
                Click = () => room.TogglePerm(who, p),
            });
        }
        menu.Line();
        menu.Add(new(L.T("Give every permission"), GCheck, () => room.SetAllPerms(who, true)));
        menu.Add(new(L.T("Remove every permission"), GClose, () => room.SetAllPerms(who, false)));
        if (who.Count == 1 && !who[0].Away) menu.Add(new(L.T("Make host"), "\uE716", () => _ = room.MakeHost(who[0])));
        menu.Line();
        menu.Add(new SheetEntry(who.Count == 1 ? L.T("Kick from the room…") : L.F("Kick {0} people…", who.Count), "\uE8F8", () => _ = room.Kick(who)) { Danger = true });
        return menu;
    }

    public static void Open(SheetMenu? menu, Control target, bool below)
    {
        if (menu != null) Open(menu);
    }
}
