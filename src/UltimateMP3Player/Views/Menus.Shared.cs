#if AVALONIA
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using SubmenuEntry = Avalonia.Controls.MenuItem;
#else
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Ellipse = System.Windows.Shapes.Ellipse;
#endif
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// The context menus' entries, the same on Windows and on Linux/macOS (linked into the Avalonia app). How a menu opens and
// its entries are made: Templates.xaml.cs (Windows), Views/Menus.cs (Avalonia).
public static partial class Menus
{
    public static ContextMenu ForTrack(TrackViewModel t, ITrackList? owner)
    {
        var main = t.Main;
        // A song of the room itself (the one playing, from the player bar; a song that isn't in the library).
        if (main.InRoom && main.Together.ItemFor(t) is { } roomItem && (owner == null || main.Together.IsStandIn(t)))
            return ForRoomItem(roomItem, main.Together);
        // A suggested song that isn't in the library.
        if (!main.InRoom && main.Radio.Item(t.Id) is { } suggested) return ForRadioItem(suggested, main, null);
        var menu = new ContextMenu();
        if (main.InRoom) AddRoomItems(menu, new[] { t });
        else
        {
            if (owner != null) menu.Items.Add(Item(L.T("Play"), "", () => main.Player.PlayFrom(owner, t)));
            else if (main.Player.Current != t) menu.Items.Add(Item(L.T("Play"), "", () => main.Player.PlaySingle(t.T)));
            menu.Items.Add(Item(L.T("Play next"), "", () => main.Player.PlayNext(t)));
            menu.Items.Add(Item(L.T("Add to queue"), "", () => main.Player.Enqueue(t)));
            menu.Items.Add(Item(L.T("Play similar songs"), RadioGlyph, () => main.Player.PlayRadio(t)));
            menu.Items.Add(Item(L.T("Play the artist's songs"), "", () => main.Player.PlayArtist(t), !string.IsNullOrWhiteSpace(t.T.Artist)));
        }
        menu.Items.Add(new Separator());
        AddSaveItems(menu, new[] { t });
        menu.Items.Add(AddToPlaylist(t));
        if (owner?.Playlist is { } pl && !pl.IsFavorites)
            menu.Items.Add(Item(L.F("Remove from “{0}”", Short(pl.Name)), "", () => main.RemoveFromPlaylist(t, pl)));
        menu.Items.Add(FavoriteItem(t));
        menu.Items.Add(TagSubmenu(new[] { t }));
        menu.Items.Add(LyricsSubmenu(t));
        if (t.IsSaved && !t.HasVideo && main.Library.Get(t.Id) == t.T)
            menu.Items.Add(Item(L.T("Find the video…"), VideoGlyph, () => _ = main.FindVideo(t)));
        if (!main.InRoom && !main.IsTheirs(t)) menu.Items.Add(DjSubmenu(t));
        menu.Items.Add(new Separator());
        AddEditItems(menu, t);
        return menu;
    }

    // A song's row pulled to the right (RowSwipe): its menu's "Add to queue", where the menu has it (not a suggested song,
    // not a song of the room itself, not without the room's permission). The same as the phones' (Mobile/Menus.cs).
    public static Action? AddToQueue(TrackViewModel t, ITrackList? owner)
    {
        var main = t.Main;
        if (main.Radio.Item(t.Id) != null) return null;
        if (!main.InRoom) return () => main.Player.Enqueue(t);
        var room = main.Together;
        if (room.ItemFor(t) != null && (owner == null || room.IsStandIn(t)) || !room.CanAdd) return null;
        return () => room.Add(new[] { t });
    }

    private const string SaveGlyph = "", VideoGlyph = "", DeleteGlyph = "", SelectGlyph = "", LibraryGlyph = "";

    // "Save on the device" for the songs not saved (or how far their save is).
    private static void AddSaveItems(ContextMenu menu, IReadOnlyList<TrackViewModel> tracks)
    {
        var main = tracks[0].Main;
        var cloud = tracks.Where(t => main.ToSave(t)).ToList();
        if (cloud.Count > 0)
            menu.Items.Add(Item(tracks.Count == 1 ? L.T("Save to device") : L.F("Save to device ({0})", cloud.Count), SaveGlyph,
                () => _ = main.SaveToDevice(cloud)));
        else if (tracks.Any(t => t.IsSaving))
            menu.Items.Add(Item(L.T("Saving to device…"), SaveGlyph, () => { }, false));
        // The quality of the ones with a link: downloaded again in it (the saved ones), kept for when they're heard (the cloud).
        var linked = tracks.Where(t => t.T.HasLink && main.Library.Get(t.Id) == t.T).ToList();
        if (linked.Count > 0)
            menu.Items.Add(Item(linked.Any(t => t.T.HasSavedFile) ? L.T("Download again / quality…") : L.T("Listening quality…"), "", () => _ = main.ChangeQuality(linked)));
    }

    private const string LyricsGlyph = "";

    // What the song has (with times, plain, nothing...), and the actions: show, search online (again), delete.
    public static MenuItem LyricsSubmenu(TrackViewModel t)
    {
        var main = t.Main;
        var sub = new SubmenuEntry { Header = L.T("Lyrics") };
        Ui.SetGlyph(sub, LyricsGlyph);
        bool searching = main.Host.Lyrics.IsSearching(t.T);
        var state = searching ? L.T("Searching…") : t.T.Lyrics switch
        {
            LyricsKind.Synced => L.T("Synced: lights up as you listen"),
            LyricsKind.Plain => L.T("Without timing"),
            LyricsKind.Instrumental => L.T("Instrumental song"),
            LyricsKind.None => L.T("Not found online"),
            _ => L.T("Not searched yet"),
        };
        sub.Items.Add(Item(state, "", () => { }, false));
        sub.Items.Add(new Separator());
        if (t.HasLyrics && main.Player.Current == t)
            sub.Items.Add(Item(L.T("Show lyrics"), "", main.ShowLyrics));
        sub.Items.Add(Item(t.HasLyrics ? L.T("Search online again") : L.T("Search online"), "", () => main.SearchLyrics(new[] { t }), !searching));
        if (t.HasLyrics) sub.Items.Add(Item(L.T("Delete lyrics"), "", () => main.DeleteLyrics(t)));
        return sub;
    }

    // In a room: songs go into the room's queue (greyed out without the host's permission).
    private static void AddRoomItems(ContextMenu menu, IReadOnlyList<TrackViewModel> tracks)
    {
        var room = tracks[0].Main.Together;
        var add = Item(tracks.Count == 1 ? L.T("Add to the room's queue") : L.F("Add {0} songs to the room", tracks.Count), "",
            () => room.Add(tracks), room.CanAdd);
        var next = Item(L.T("Play next in the room"), "", () => room.Add(tracks, next: true), room.CanAdd && room.CanRemove);
        if (!room.CanAdd)
        {
            Tip(add, room.Denied(Core.Together.Perm.Add), true);
        }
        menu.Items.Add(add);
        menu.Items.Add(next);
    }

    // A song of the room: its place in the queue, and keeping it.
    public static ContextMenu ForRoomItem(RoomItemViewModel item, TogetherViewModel room)
    {
        var menu = new ContextMenu();
        var main = room.Main;
        menu.Items.Add(Item(item.AddedByText, "", () => { }, false));
        menu.Items.Add(new Separator());
        if (!item.IsCurrent)
        {
            int index = room.Queue.IndexOf(item), last = room.Queue.Count - 1;
            menu.Items.Add(Item(L.T("Play it now"), "", () => room.PlayNow(item.Id), room.CanRemove && room.CanSkip));
            menu.Items.Add(Item(L.T("Move to top"), "", () => room.MoveTo(item, 0), room.CanRemove && index > 0));
            menu.Items.Add(Item(L.T("Move to bottom"), "", () => room.MoveTo(item, last), room.CanRemove && index < last));
            menu.Items.Add(Item(L.T("Remove from queue"), "", () => room.Remove(item), item.CanRemove));
        }
        else menu.Items.Add(Item(L.T("Skip"), "", room.Skip, room.CanSkip));
        menu.Items.Add(new Separator());
        // As for any song: into the library (in the cloud) with the library icon, onto the device with the download arrow.
        if (item.InLibrary)
        {
            menu.Items.Add(AddToPlaylist(item.Track));
            menu.Items.Add(FavoriteItem(item.Track));
        }
        else
        {
            if (TogetherViewModel.HasLink(item.Item))
                menu.Items.Add(Item(L.T("Add to library (without downloading it)"), "", () => room.AddToLibrary(item, null)));
            var sub = new SubmenuEntry { Header = L.T("Add to playlist"), IsEnabled = item.IsReady || TogetherViewModel.HasLink(item.Item) };
            Ui.SetGlyph(sub, "");
            sub.Items.Add(Item(L.T("New playlist…"), "", () =>
            {
                var name = Dialogs.Prompt(L.T("New playlist"), L.T("Playlist name"), main.NewPlaylistName());
                if (!string.IsNullOrWhiteSpace(name)) room.AddToPlaylist(item, main.Profile.CreatePlaylist(name));
            }));
            sub.Items.Add(new Separator());
            foreach (var p in main.Playlists)
            {
                var pl = p.P;
                sub.Items.Add(Item(Short(p.Name), p.IsFavorites ? "" : "", () => room.AddToPlaylist(item, pl)));
            }
            menu.Items.Add(sub);
        }
        if (!item.SavedOnDevice) menu.Items.Add(Item(L.T("Save to device"), "", () => _ = room.Save(item, null), item.IsReady));
        if (item.Item.SourceUrl is { } url)
            menu.Items.Add(Item(L.T("Copy original link"), "", () => { try { Ui.CopyText(url); main.Toast(L.T("Link copied")); } catch { } }));
        return menu;
    }

    private const string RadioGlyph = "";

    // A suggested song that isn't in the library: its place in the queue (row) or skipping it (the one playing), keeping it,
    // more songs like it.
    public static ContextMenu ForRadioItem(RadioItemViewModel item, MainViewModel main, QueueRow? row)
    {
        var menu = new ContextMenu();
        var player = main.Player;
        var t = main.Vm(item.T);
        menu.Items.Add(Item(item.FromText, RadioGlyph, () => { }, false));
        menu.Items.Add(new Separator());
        if (row != null)
        {
            int last = player.UpNext.LastOrDefault()?.Index ?? row.Index;
            menu.Items.Add(Item(L.T("Play now"), "", () => _ = player.JumpTo(row.Index)));
            menu.Items.Add(Item(L.T("Move to top"), "", () => player.MoveUpcoming(row.Index, 0), row.Index > 0));
            menu.Items.Add(Item(L.T("Move to bottom"), "", () => player.MoveUpcoming(row.Index, last), row.Index < last));
            menu.Items.Add(Item(L.T("Remove from Up next"), "", () => player.RemoveUpcoming(row.Index)));
        }
        else if (player.Current == t) menu.Items.Add(Item(L.T("Skip"), "", () => player.NextCommand.Execute(null)));
        // (a card of the recently played, a row of the statistics)
        else menu.Items.Add(Item(L.T("Play"), "", () => player.PlaySingle(t.T)));
        if (item.IsFailed) menu.Items.Add(Item(L.T("Try downloading it again"), "", () => main.Radio.Prepare(item.Id)));
        menu.Items.Add(new Separator());
        // Only into the library (in the cloud, nothing downloaded: also a playlist, the favourites), or saved on the device too.
        menu.Items.Add(Item(L.T("Add to library"), LibraryGlyph, () => main.AddToLibrary(t)));
        menu.Items.Add(Item(L.T("Save to device"), SaveGlyph, () =>
        {
            // (connected to the other device: it asks where)
            if (main.Shared is { Ready: true }) _ = main.SaveToDevice(new[] { t });
            else _ = main.Radio.Save(item, null);
        }));
        menu.Items.Add(AddToPlaylist(t));
        menu.Items.Add(FavoriteItem(t));
        menu.Items.Add(LyricsSubmenu(t));
        menu.Items.Add(Item(L.T("Play similar songs"), RadioGlyph, () => player.PlayRadio(t)));
        menu.Items.Add(Item(L.T("Play the artist's songs"), "", () => player.PlayArtist(t), !string.IsNullOrWhiteSpace(t.T.Artist)));
        menu.Items.Add(Item(L.T("Copy original link"), "", () => { try { Ui.CopyText(item.Song.Url); main.Toast(L.T("Link copied")); } catch { } }));
        if (row == null && player.Current != t)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(L.T("Delete…"), DeleteGlyph, () => _ = main.DeleteTracks(new[] { t })));
        }
        return menu;
    }

    // The host on the people of the room (the one clicked, or the selected ones): permissions, kick, hand over.
    public static ContextMenu ForMembers(TogetherViewModel room, MemberViewModel? clicked = null)
    {
        var menu = new ContextMenu();
        var who = room.SelectedMembers;
        if (clicked != null && !who.Contains(clicked)) who = new List<MemberViewModel> { clicked };
        who = who.Where(m => !m.IsMe).ToList();
        if (!room.IsHost || who.Count == 0)
        {
            menu.Items.Add(Item(room.IsHost ? L.T("Select someone in the list") : L.T("Only the host can change permissions"), "", () => { }, false));
            return menu;
        }
        menu.Items.Add(Item(who.Count == 1 ? who[0].Name : L.F("{0} people", who.Count), "", () => { }, false));
        menu.Items.Add(new Separator());
        foreach (var (perm, text) in new[]
                 {
                     (Core.Together.Perm.Add, L.T("Can add songs")),
                     (Core.Together.Perm.Remove, L.T("Can remove and move songs")),
                     (Core.Together.Perm.Skip, L.T("Can skip songs")),
                     (Core.Together.Perm.Pause, L.T("Can pause, move forward and back and loop the song")),
                     (Core.Together.Perm.Speed, L.T("Can change the speed")),
                 })
        {
            int n = who.Count(m => (m.M.Perms & perm) != 0);
            var item = Item(text, n == 0 ? "" : n == who.Count ? Check : Some, () => { });
            item.StaysOpenOnClick = true;
            var p = perm;
            item.Click += (_, _) =>
            {
                bool on = who.Any(m => (m.M.Perms & p) == 0);
                room.TogglePerm(who, p);
                Ui.SetGlyph(item, on ? Check : "");
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("Give every permission"), "", () => room.SetAllPerms(who, true)));
        menu.Items.Add(Item(L.T("Remove every permission"), "", () => room.SetAllPerms(who, false)));
        if (who.Count == 1 && !who[0].Away)
            menu.Items.Add(Item(L.T("Make host"), "", () => _ = room.MakeHost(who[0])));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(who.Count == 1 ? L.T("Kick from the room…") : L.F("Kick {0} people…", who.Count), "", () => _ = room.Kick(who)));
        return menu;
    }

    // ------------------------------------------------------------------ tags

    private const string Check = "", Some = "", TagGlyph = "";

    // Tick = every song has it, dash = only some. Stays open to tick several.
    private static IEnumerable<object> TagItems(IReadOnlyList<TrackViewModel> tracks)
    {
        var main = tracks[0].Main;
        var tags = main.MenuTags();
        foreach (var tag in tags)
        {
            var tg = tag;
            var item = TagItem(tg, StateOf(tracks, tg), main.DeviceGlyph(tg.Id, true));
            item.StaysOpenOnClick = true;
            item.Click += (_, _) =>
            {
                bool on = StateOf(tracks, tg) != Check;
                main.SetTag(tracks, tg, on);
                Ui.SetGlyph(item, on ? Check : null);
            };
            yield return item;
        }
        if (tags.Count > 0) yield return new Separator();
        yield return Item(L.T("New tag…"), "", () => _ = main.NewTag(tracks));
    }

    private static string? StateOf(IReadOnlyList<TrackViewModel> tracks, TagViewModel tag)
    {
        int n = tracks.Count(t => t.Main.HasTag(t, tag));
        return n == 0 ? null : n == tracks.Count ? Check : Some;
    }

    // Coloured dot and name (device: the device's icon to show, if not the tag's own).
    private static MenuItem TagItem(TagViewModel tag, string? glyph, string? device = null)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = tag.Brush, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(new TextBlock { Text = Short(tag.Name), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        if ((device ?? tag.DeviceGlyph) is { } mark) header.Children.Add(DeviceMark(mark));
        var m = new MenuItem { Header = header };
        Ui.SetGlyph(m, glyph);
        return m;
    }

    // The phone's or the computer's icon after a name (Connect phone, both devices' music shown).
    private static TextBlock DeviceMark(string glyph)
    {
#if AVALONIA
        var t = new TextBlock { Text = Icons.Map(glyph), FontSize = 11, Opacity = 0.6, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        t.Classes.Add("glyph");
        return t;
#else
        return new TextBlock
        {
            Text = glyph, Style = (Style)Application.Current.Resources["Glyph"], FontSize = 11, Opacity = 0.6, Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
#endif
    }

    public static MenuItem DjSubmenu(TrackViewModel t)
    {
        var sub = new SubmenuEntry { Header = L.T("Load in DJ") };
        Ui.SetGlyph(sub, "");
        sub.Items.Add(Item(L.T("Deck A"), "", () => t.Main.LoadInDj(t, false)));
        sub.Items.Add(Item(L.T("Deck B"), "", () => t.Main.LoadInDj(t, true)));
        return sub;
    }

    public static MenuItem TagSubmenu(IReadOnlyList<TrackViewModel> tracks)
    {
        var sub = new SubmenuEntry { Header = L.T("Tags") };
        Ui.SetGlyph(sub, TagGlyph);
        foreach (var i in TagItems(tracks)) sub.Items.Add(i);
        return sub;
    }

    public static ContextMenu TagMenu(IReadOnlyList<TrackViewModel> tracks)
    {
        var menu = new ContextMenu();
        foreach (var i in TagItems(tracks)) menu.Items.Add(i);
        return menu;
    }

    // The tags of a playlist itself.
    private static IEnumerable<object> PlaylistTagItems(PlaylistViewModel p)
    {
        var main = App.Host.Session!;
        foreach (var tag in main.TagsFor(p))
        {
            var tg = tag;
            var item = TagItem(tg, p.P.Tags.Contains(tg.Id) ? Check : null);
            item.StaysOpenOnClick = true;
            item.Click += (_, _) =>
            {
                bool on = !p.P.Tags.Contains(tg.Id);
                main.SetPlaylistTag(p, tg, on);
                Ui.SetGlyph(item, on ? Check : null);
            };
            yield return item;
        }
        if (main.Tags.Count > 0) yield return new Separator();
        yield return Item(L.T("New tag…"), "", () => _ = main.NewTag(playlist: p));
    }

    private static MenuItem PlaylistTagSubmenu(PlaylistViewModel p)
    {
        var sub = new SubmenuEntry { Header = L.T("Tags") };
        Ui.SetGlyph(sub, TagGlyph);
        foreach (var i in PlaylistTagItems(p)) sub.Items.Add(i);
        return sub;
    }

    // Playlist header: its tags, and the way to put them on its songs.
    public static ContextMenu PlaylistTagMenu(PlaylistViewModel p)
    {
        var menu = new ContextMenu();
        foreach (var i in PlaylistTagItems(p)) menu.Items.Add(i);
        menu.Items.Add(Item(L.T("Put its tags on its songs…"), "", () => _ = App.Host.Session!.TagPlaylistSongs(p), p.Count > 0));
        return menu;
    }

    // Tick the tags to use (e.g. for the songs of a download); a new tag is ticked right away.
    public static ContextMenu TagPicker(ISet<string> chosen, MainViewModel main, Action changed)
    {
        var menu = new ContextMenu();
        foreach (var tag in main.Tags.Where(t => !main.IsTheirs(t.Id)))
        {
            var tg = tag;
            var item = TagItem(tg, chosen.Contains(tg.Id) ? Check : null);
            item.StaysOpenOnClick = true;
            item.Click += (_, _) =>
            {
                if (!chosen.Remove(tg.Id)) chosen.Add(tg.Id);
                Ui.SetGlyph(item, chosen.Contains(tg.Id) ? Check : null);
                changed();
            };
            menu.Items.Add(item);
        }
        if (main.Tags.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("New tag…"), "", async () =>
        {
            if (await main.NewTag() is { } t)
            {
                chosen.Add(t.Id);
                changed();
            }
        }));
        return menu;
    }

    // Filter a list by tags: all of the chosen ones, or at least one.
    public static ContextMenu TagFilterMenu(TagFilter filter, MainViewModel main)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item(L.T("Show songs with:"), "", () => { }, false));
        var all = Item(L.T("all the chosen tags"), filter.MatchAll ? Check : "", () => { });
        var any = Item(L.T("at least one of the chosen tags"), filter.MatchAll ? "" : Check, () => { });
        all.StaysOpenOnClick = any.StaysOpenOnClick = true;
        all.Click += (_, _) => { filter.MatchAll = true; Ui.SetGlyph(all, Check); Ui.SetGlyph(any, null); };
        any.Click += (_, _) => { filter.MatchAll = false; Ui.SetGlyph(any, Check); Ui.SetGlyph(all, null); };
        menu.Items.Add(all);
        menu.Items.Add(any);
        menu.Items.Add(new Separator());
        foreach (var tag in main.Tags)
        {
            var tg = tag;
            var item = TagItem(tg, filter.Contains(tg.Id) ? Check : null);
            item.StaysOpenOnClick = true;
            item.Click += (_, _) =>
            {
                filter.Toggle(tg.Id);
                Ui.SetGlyph(item, filter.Contains(tg.Id) ? Check : null);
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("Clear the filter"), "", filter.Clear, filter.IsActive));
        return menu;
    }

    // Which songs the lists show: all of them, only the ones saved on the device, only the ones in the cloud (one choice for
    // every library page and playlist).
    public static ContextMenu StorageFilterMenu(StorageFilterViewModel filter)
    {
        var menu = new ContextMenu();
        foreach (var choice in StorageFilterViewModel.Choices)
        {
            var c = choice;
            menu.Items.Add(Item(filter.NameOf(c), filter.Value == c ? Check : "", () => filter.Value = c));
        }
        return menu;
    }

    public static ContextMenu ForTag(TagViewModel t)
    {
        var main = App.Host.Session!;
        var menu = new ContextMenu();
        menu.Items.Add(Item(L.T("Open"), "", () => main.OpenTag(t)));
        menu.Items.Add(Item(L.T("Edit…"), "", () => _ = main.EditTag(t)));
        if (!main.IsTheirs(t.Id)) menu.Items.Add(Item(L.T("Export to a .ump file…"), "", () => main.ExportPack(tag: t)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("Delete tag…"), "", () => _ = main.DeleteTag(t)));
        return menu;
    }

    // (connected: the device is asked, whichever music is shown)
    private static MenuItem FavoriteItem(TrackViewModel t) => t.Main.IsFavorite(t.Id, menu: true)
        ? Item(L.T("Remove from Favorites"), "", () => t.Main.ToggleFavorite(t, menu: true))
        : Item(L.T("Add to Favorites"), "", () => t.Main.ToggleFavorite(t, menu: true));

    // Edit, cover, folder, link, delete: the same everywhere.
    private static void AddEditItems(ContextMenu menu, TrackViewModel t)
    {
        var main = t.Main;
        menu.Items.Add(Item(L.T("Edit info…"), "", () => _ = main.EditTrack(t)));
        menu.Items.Add(Item(L.T("Change cover…"), "", () => _ = main.ChangeTrackCover(t)));
        if (t.T.HasSavedFile) menu.Items.Add(Item(L.T("Show in folder"), "", () => main.ShowInFolder(t)));
        if (t.T.SourceUrl is { } url && url.StartsWith("http"))
            menu.Items.Add(Item(L.T("Copy original link"), "", () => { try { Ui.CopyText(url); main.Toast(L.T("Link copied")); } catch { } }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("Delete…"), DeleteGlyph, () => _ = main.DeleteTrack(t)));
    }

    public static ContextMenu ForSelection(IReadOnlyList<TrackViewModel> tracks, ITrackList? owner)
    {
        var main = tracks[0].Main;
        var menu = new ContextMenu();
        menu.Items.Add(Item(L.F("{0} songs selected", tracks.Count), "", () => { }, false));
        menu.Items.Add(new Separator());
        if (main.InRoom) AddRoomItems(menu, tracks);
        else
        {
            menu.Items.Add(Item(L.T("Play"), "\uE768", () => main.PlaySelection(tracks)));
            menu.Items.Add(Item(L.T("Add to queue"), "\uE8FD", () => main.Enqueue(tracks)));
        }
        menu.Items.Add(new Separator());
        AddSaveItems(menu, tracks);
        var sub = new SubmenuEntry { Header = L.T("Add to playlist") };
        Ui.SetGlyph(sub, "");
        foreach (var i in PlaylistItems(tracks)) sub.Items.Add(i);
        menu.Items.Add(sub);
        if (owner?.Playlist is { } pl)
            menu.Items.Add(Item(L.F("Remove from “{0}”", Short(PlaylistViewModel.DisplayName(pl))), "", () => main.RemoveFromPlaylist(tracks, pl)));
        menu.Items.Add(Item(L.T("Add to Favorites"), "", () => main.AddToFavorites(tracks)));
        menu.Items.Add(TagSubmenu(tracks));
        menu.Items.Add(Item(L.F("Search lyrics online ({0})", tracks.Count), LyricsGlyph, () => main.SearchLyrics(tracks)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.F("Delete {0} songs…", tracks.Count), DeleteGlyph, () => _ = main.DeleteTracks(tracks)));
        return menu;
    }

    // Several selected playlists: what the selection bar does, from the right click.
    public static ContextMenu ForPlaylists(IReadOnlyList<PlaylistViewModel> lists)
    {
        var main = App.Host.Session!;
        var menu = new ContextMenu();
        menu.Items.Add(Item(L.Count(lists.Count, "1 playlist selected", "{0} playlists selected"), SelectGlyph, () => { }, false));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("Play"), "", () => main.PlayPlaylists(lists)));
        menu.Items.Add(Item(L.T("Add to queue"), "", () => main.EnqueuePlaylists(lists)));
        menu.Items.Add(new Separator());
        int cloud = main.SongsOf(lists).Count(main.ToSave);
        if (cloud > 0) menu.Items.Add(Item(L.F("Save to device ({0})", cloud), SaveGlyph, () => _ = main.SavePlaylists(lists)));
        var tags = new SubmenuEntry { Header = L.T("Tags") };
        Ui.SetGlyph(tags, TagGlyph);
        foreach (var i in PlaylistsTagItems(lists)) tags.Items.Add(i);
        menu.Items.Add(tags);
        if (lists.All(p => !p.IsOther)) menu.Items.Add(Item(L.T("Export to a .ump file…"), "", () => main.ExportPlaylists(lists)));
        if (lists.Any(p => !p.IsFavorites))
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(L.Count(lists.Count(p => !p.IsFavorites), "Delete playlist…", "Delete {0} playlists…"), DeleteGlyph, () => _ = main.DeletePlaylists(lists)));
        }
        return menu;
    }

    public static ContextMenu PlaylistsTagMenu(IReadOnlyList<PlaylistViewModel> lists)
    {
        var menu = new ContextMenu();
        foreach (var i in PlaylistsTagItems(lists)) menu.Items.Add(i);
        return menu;
    }

    // Tick = every selected playlist has it, dash = only some.
    private static IEnumerable<object> PlaylistsTagItems(IReadOnlyList<PlaylistViewModel> lists)
    {
        var main = App.Host.Session!;
        foreach (var tag in main.Tags.Where(t => lists.Any(p => main.ProfileOf(p.P) == main.ProfileOf(t.T))))
        {
            var tg = tag;
            string? State() => lists.Count(p => p.P.Tags.Contains(tg.Id)) is var n && n == 0 ? null : n == lists.Count ? Check : Some;
            var item = TagItem(tg, State());
            item.StaysOpenOnClick = true;
            item.Click += (_, _) =>
            {
                bool on = State() != Check;
                main.SetPlaylistsTag(lists, tg, on);
                Ui.SetGlyph(item, on ? Check : null);
            };
            yield return item;
        }
        if (main.Tags.Count > 0) yield return new Separator();
        yield return Item(L.T("New tag…"), "", async () =>
        {
            if (await main.NewTag() is { } t) main.SetPlaylistsTag(lists, t, true);
        });
    }

    // Several selected songs of "next up".
    public static ContextMenu ForQueueRows(IReadOnlyList<QueueRow> rows)
    {
        var player = rows[0].Player;
        var main = rows[0].Track.Main;
        var places = rows.Select(r => r.Index).ToList();
        var songs = rows.Select(r => r.Track).Distinct().ToList();
        var menu = new ContextMenu();
        menu.Items.Add(Item(L.F("{0} songs selected", rows.Count), SelectGlyph, () => { }, false));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("Move to top"), "", () => player.MoveUpcomingToTop(places)));
        menu.Items.Add(Item(L.T("Move to bottom"), "", () => player.MoveUpcomingToBottom(places)));
        menu.Items.Add(Item(L.T("Remove from Up next"), "", () => player.RemoveUpcoming(places)));
        if (!player.InRoom)
        {
            menu.Items.Add(new Separator());
            AddSaveItems(menu, songs);
            var sub = new SubmenuEntry { Header = L.T("Add to playlist") };
            Ui.SetGlyph(sub, "");
            foreach (var i in PlaylistItems(songs)) sub.Items.Add(i);
            menu.Items.Add(sub);
            menu.Items.Add(Item(L.T("Add to Favorites"), "", () => main.AddToFavorites(songs)));
        }
        return menu;
    }

    // Several selected downloads.
    public static ContextMenu ForJobs(IReadOnlyList<DownloadJobViewModel> jobs)
    {
        var queue = App.Host.Session!.Queue;
        var menu = new ContextMenu();
        menu.Items.Add(Item(L.Count(jobs.Count, "1 download selected", "{0} downloads selected"), SelectGlyph, () => { }, false));
        menu.Items.Add(new Separator());
        int failed = jobs.Count(j => j.CanRetry), youTube = jobs.Count(j => j.CanRetryYouTube), active = jobs.Count(j => j.IsActive);
        menu.Items.Add(Item(L.F("Retry ({0})", failed), "", () => queue.Retry(jobs), failed > 0));
        menu.Items.Add(Item(L.F("Retry on YouTube ({0})", youTube), "", () => queue.RetryOnYouTube(jobs), youTube > 0));
        menu.Items.Add(Item(L.F("Cancel ({0})", active), "", () => queue.Cancel(jobs), active > 0));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("Remove from list"), DeleteGlyph, () => queue.Remove(jobs)));
        return menu;
    }

    public static ContextMenu AddManyToPlaylist(IReadOnlyList<TrackViewModel> tracks)
    {
        var menu = new ContextMenu();
        foreach (var i in PlaylistItems(tracks)) menu.Items.Add(i);
        return menu;
    }

    private static IEnumerable<object> PlaylistItems(IReadOnlyList<TrackViewModel> tracks)
    {
        var main = tracks[0].Main;
        yield return Item(L.T("New playlist…"), "", () => _ = main.NewPlaylistWith(tracks));
        yield return new Separator();
        foreach (var p in main.MenuPlaylists())
        {
            var pl = p.P;
            yield return Item(Short(p.Name), main.DeviceGlyph(p.Id, true) ?? (p.IsFavorites ? "" : ""), () => main.AddToPlaylist(tracks, pl));
        }
    }

    // "Next up" rows: queue actions plus the usual song ones.
    public static ContextMenu ForQueue(QueueRow row)
    {
        var t = row.Track;
        var player = row.Player;
        if (player.InRoom && t.Main.Together.ItemFor(t) is { } roomItem) return ForRoomItem(roomItem, t.Main.Together);
        if (row.Radio is { } suggested) return ForRadioItem(suggested, t.Main, row);
        int last = player.UpNext.LastOrDefault()?.Index ?? row.Index;
        var menu = new ContextMenu();
        menu.Items.Add(Item(L.T("Play now"), "", () => _ = player.JumpTo(row.Index)));
        menu.Items.Add(Item(L.T("Move to top"), "", () => player.MoveUpcoming(row.Index, 0), row.Index > 0));
        menu.Items.Add(Item(L.T("Move to bottom"), "", () => player.MoveUpcoming(row.Index, last), row.Index < last));
        menu.Items.Add(Item(L.T("Remove from Up next"), "", () => player.RemoveUpcoming(row.Index)));
        menu.Items.Add(Item(L.T("Play similar songs"), RadioGlyph, () => player.PlayRadio(t)));
        menu.Items.Add(Item(L.T("Play the artist's songs"), "", () => player.PlayArtist(t), !string.IsNullOrWhiteSpace(t.T.Artist)));
        menu.Items.Add(new Separator());
        AddSaveItems(menu, new[] { t });
        menu.Items.Add(AddToPlaylist(t));
        menu.Items.Add(FavoriteItem(t));
        menu.Items.Add(TagSubmenu(new[] { t }));
        menu.Items.Add(LyricsSubmenu(t));
        menu.Items.Add(new Separator());
        AddEditItems(menu, t);
        return menu;
    }

    public static MenuItem AddToPlaylist(TrackViewModel t)
    {
        var main = t.Main;
        var sub = new SubmenuEntry { Header = L.T("Add to playlist") };
        Ui.SetGlyph(sub, "");
        sub.Items.Add(Item(L.T("New playlist…"), "", () => _ = main.NewPlaylist(t)));
        sub.Items.Add(new Separator());
        // (connected: both devices' playlists, whichever music is shown)
        foreach (var p in main.MenuPlaylists())
        {
            var pl = p.P;
            bool has = main.ProfileOf(pl).Contains(pl, t.Id);
            sub.Items.Add(Item(Short(p.Name) + (has ? "  ✓" : ""), main.DeviceGlyph(p.Id, true) ?? (p.IsFavorites ? "" : ""), () => main.AddToPlaylist(t, pl), !has));
        }
        return sub;
    }

    public static ContextMenu AddToPlaylistMenu(TrackViewModel t)
    {
        var menu = new ContextMenu();
        var sub = AddToPlaylist(t);
        var items = sub.Items.Cast<object>().ToList();
        sub.Items.Clear();
        foreach (var i in items) menu.Items.Add(i);
        return menu;
    }

    public static ContextMenu ForPlaylist(PlaylistViewModel p)
    {
        var main = App.Host.Session!;
        var menu = new ContextMenu();
        menu.Items.Add(Item(L.T("Play"), "", () => main.PlayPlaylistCommand.Execute(p)));
        menu.Items.Add(Item(L.T("Open"), "", () => main.OpenPlaylist(p)));
        menu.Items.Add(new Separator());
        menu.Items.Add(PlaylistTagSubmenu(p));
        menu.Items.Add(Item(L.T("Put its tags on its songs…"), "\uE8B3", () => _ = main.TagPlaylistSongs(p), p.Count > 0));
        if (!p.IsOther) menu.Items.Add(Item(L.T("Export to a .ump file…"), "", () => main.ExportPack(p)));
        // Its songs in the cloud saved on the device; or the space its songs take, freed (the window says how).
        int toSave = main.SongsOf(new[] { p }).Count(main.ToSave);
        if (toSave > 0) menu.Items.Add(Item(L.F("Save to device ({0})", toSave), SaveGlyph, () => _ = main.SavePlaylists(new[] { p })));
        if (p.Count > p.CloudCount)
            menu.Items.Add(Item(L.T("Free up space…"), DeleteGlyph, () => _ = main.DeleteTracks(p.Songs.Where(t => t.IsSaved).ToList())));
        if (!p.IsFavorites)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(L.T("Rename…"), "", () => _ = main.RenamePlaylist(p)));
            menu.Items.Add(Item(L.T("Change image…"), "", () => _ = main.ChangePlaylistCover(p)));
            if (p.HasCustomCover) menu.Items.Add(Item(L.T("Remove image"), "", () => main.RemovePlaylistCover(p)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(L.T("Delete playlist…"), "", () => _ = main.DeletePlaylist(p)));
        }
        return menu;
    }

    private static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;
}
