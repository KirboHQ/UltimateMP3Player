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
        GInfo = "", GUp = "", GDown = "", GList = "", GRefresh = "";

    public static void Open(SheetMenu menu) => App.Host.View?.Sheets.ShowMenu(menu);

    // What a long press on an element of a page opens.
    public static SheetMenu? MenuFor(string? kind, object? dataContext, Control source) => (kind, dataContext) switch
    {
        ("track", SongItem s) => ForTrack(s.Track, s.Row.Owner, Selection.Of(source)),
        ("track", TrackRow r) => ForTrack(r.Track, r.Owner),
        ("card", TrackViewModel t) => ForTrack(t, null),
        // A finished download: the song's menu (play similar songs, playlists…), as anywhere else.
        ("download", DownloadJobViewModel { Song: { } song }) => ForTrack(song, null),
        ("playlist", PlaylistViewModel p) => ForPlaylist(p),
        ("tag", TagViewModel t) => ForTag(t),
        ("queue", QueueRow q) => ForQueue(q),
        ("current", _) when App.Host.Session?.Player.Current is { } cur => ForTrack(cur, null),
        _ => null,
    };

    private static string Short(string s) => s.Length > 48 ? s[..48] + "…" : s;

    public static SheetMenu ForTrack(TrackViewModel t, ITrackList? owner, Selection? selection = null)
    {
        var main = t.Main;
        // A suggested song that isn't in the library.
        if (main.Radio.Item(t.Id) is { } suggested) return ForRadioItem(suggested, main, null);
        var menu = new SheetMenu { Title = t.Title, Subtitle = t.Artist, Cover = t };
        var player = main.Player;
        if (owner != null && player.Current != t) menu.Add(new(L.T("Riproduci"), GPlay, () => player.PlayFrom(owner, t)));
        else if (player.Current != t) menu.Add(new(L.T("Riproduci"), GPlay, () => player.PlaySingle(t.T)));
        menu.Add(new(L.T("Riproduci dopo"), GNext, () => player.PlayNext(t)));
        menu.Add(new(L.T("Aggiungi alla coda"), GQueue, () => player.Enqueue(t)));
        menu.Add(new(L.T("Riproduci brani simili"), GRadio, () => player.PlayRadio(t)));
        menu.Line();
        menu.Add(AddToPlaylist(t));
        if (owner?.Playlist is { } pl && !pl.IsFavorites)
            menu.Add(new(L.F("Rimuovi da «{0}»", Short(pl.Name)), GRemove, () => main.RemoveFromPlaylist(t, pl)));
        menu.Add(FavoriteItem(t));
        menu.Add(new SheetEntry { Text = L.T("Tag"), Glyph = GTag, Sub = () => TagMenu(new[] { t }) });
        menu.Add(new SheetEntry { Text = L.T("Testo"), Glyph = GLyrics, Sub = () => LyricsMenu(t) });
        if (selection != null && !selection.IsActive)
            menu.Add(new(L.T("Seleziona più brani"), GSelect, () => selection.Start(selection.Find(t))));
        menu.Line();
        AddEditItems(menu, t);
        return menu;
    }

    private static SheetEntry FavoriteItem(TrackViewModel t) => t.IsFavorite
        ? new SheetEntry(L.T("Togli dai Preferiti"), GHeartFill, () => t.Main.ToggleFavorite(t))
        : new SheetEntry(L.T("Aggiungi ai Preferiti"), GHeart, () => t.Main.ToggleFavorite(t));

    // Edit, cover, link, delete: the same everywhere.
    private static void AddEditItems(SheetMenu menu, TrackViewModel t)
    {
        var main = t.Main;
        menu.Add(new(L.T("Modifica informazioni…"), GEdit, () => _ = main.EditTrack(t)));
        menu.Add(new(L.T("Cambia copertina…"), GPicture, () => _ = main.ChangeTrackCover(t)));
        if (t.T.SourceUrl is { } url && url.StartsWith("http"))
        {
            menu.Add(new(L.T("Copia link originale"), GLink, () => { Ui.CopyText(url); main.Toast(L.T("Link copiato")); }));
            menu.Add(new(L.T("Apri il link originale"), GOpen, () => MainViewModel.OpenUrl(url)));
        }
        menu.Line();
        menu.Add(new SheetEntry(L.T("Elimina brano…"), GDelete, () => _ = main.DeleteTrack(t)) { Danger = true });
    }

    public static SheetMenu AddToPlaylistMenu(TrackViewModel t)
    {
        var main = t.Main;
        var menu = new SheetMenu { Title = L.T("Aggiungi a playlist"), Subtitle = t.Title, Cover = t };
        menu.Add(new(L.T("Nuova playlist…"), GAdd, () => _ = main.NewPlaylist(t)));
        menu.Line();
        foreach (var p in main.Playlists)
        {
            var pl = p.P;
            bool has = main.Profile.Contains(pl, t.Id);
            menu.Add(new SheetEntry(Short(p.Name), p.IsFavorites ? GHeartFill : GList, () => main.AddToPlaylist(t, pl), !has)
            {
                Checked = has ? () => true : null,
            });
        }
        return menu;
    }

    private static SheetEntry AddToPlaylist(TrackViewModel t)
        => new() { Text = L.T("Aggiungi a playlist"), Glyph = GAdd, Sub = () => AddToPlaylistMenu(t) };

    // What the song has (with times, plain, nothing...), and the actions: show, search online (again), delete.
    public static SheetMenu LyricsMenu(TrackViewModel t)
    {
        var main = t.Main;
        bool searching = main.Host.Lyrics.IsSearching(t.T);
        var state = searching ? L.T("Ricerca in corso…") : t.T.Lyrics switch
        {
            LyricsKind.Synced => L.T("Con i tempi: si illumina mentre ascolti"),
            LyricsKind.Plain => L.T("Senza tempi"),
            LyricsKind.Instrumental => L.T("Brano strumentale"),
            LyricsKind.None => L.T("Non trovato online"),
            _ => L.T("Non ancora cercato"),
        };
        var menu = new SheetMenu { Title = L.T("Testo"), Subtitle = t.Title, Cover = t };
        menu.Add(new SheetEntry(state, GInfo, null) { Info = true });
        menu.Line();
        if (t.HasLyrics && main.Player.Current == t) menu.Add(new(L.T("Mostra il testo"), GLyrics, main.ShowLyrics));
        menu.Add(new(t.HasLyrics ? L.T("Cerca di nuovo online") : L.T("Cerca online"), GSearch, () => main.SearchLyrics(new[] { t }), !searching));
        if (t.HasLyrics) menu.Add(new SheetEntry(L.T("Elimina il testo"), GDelete, () => main.DeleteLyrics(t)) { Danger = true });
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
            menu.Add(new(L.T("Riproduci ora"), GPlay, () => _ = player.JumpTo(row.Index)));
            menu.Add(new(L.T("Sposta in cima"), GUp, () => player.MoveUpcoming(row.Index, 0), row.Index > 0));
            menu.Add(new(L.T("Sposta in fondo"), GDown, () => player.MoveUpcoming(row.Index, last), row.Index < last));
            menu.Add(new(L.T("Togli dai successivi"), GRemove, () => player.RemoveUpcoming(row.Index)));
        }
        else if (player.Current == t) menu.Add(new(L.T("Salta"), GNext, () => player.NextCommand.Execute(null)));
        if (item.IsFailed) menu.Add(new(L.T("Riprova a scaricarlo"), GRefresh, () => main.Radio.Prepare(item.Id)));
        menu.Line();
        menu.Add(new(L.T("Salva nella libreria"), GAdd, () => _ = main.Radio.Save(item, null), item.IsReady));
        menu.Add(new SheetEntry
        {
            Text = item.IsReady ? L.T("Salva in una playlist") : L.T("Si può salvare quando è stato scaricato"),
            Glyph = GList,
            Enabled = item.IsReady,
            Sub = () =>
            {
                var sub = new SheetMenu { Title = L.T("Salva in una playlist"), Subtitle = t.Title, Cover = t };
                sub.Add(new(L.T("Nuova playlist…"), GAdd, async () =>
                {
                    var name = await Dialogs.PromptAsync(L.T("Nuova playlist"), L.T("Nome della playlist"), main.NewPlaylistName());
                    if (!string.IsNullOrWhiteSpace(name)) _ = main.Radio.Save(item, main.Profile.CreatePlaylist(name));
                }));
                sub.Line();
                foreach (var p in main.Playlists)
                {
                    var pl = p.P;
                    sub.Add(new(Short(p.Name), p.IsFavorites ? GHeartFill : GList, () => _ = main.Radio.Save(item, pl)));
                }
                return sub;
            },
        });
        menu.Add(new SheetEntry { Text = L.T("Testo"), Glyph = GLyrics, Sub = () => LyricsMenu(t) });
        menu.Add(new(L.T("Riproduci brani simili"), GRadio, () => player.PlayRadio(t)));
        menu.Add(new(L.T("Copia link originale"), GLink, () => { Ui.CopyText(item.Song.Url); main.Toast(L.T("Link copiato")); }));
        return menu;
    }

    // ------------------------------------------------------------------ "next up"

    public static SheetMenu ForQueue(QueueRow row)
    {
        var t = row.Track;
        var player = row.Player;
        if (row.Radio is { } suggested) return ForRadioItem(suggested, t.Main, row);
        int last = player.UpNext.LastOrDefault()?.Index ?? row.Index;
        var menu = new SheetMenu { Title = t.Title, Subtitle = t.Artist, Cover = t };
        menu.Add(new(L.T("Riproduci ora"), GPlay, () => _ = player.JumpTo(row.Index)));
        menu.Add(new(L.T("Sposta in cima"), GUp, () => player.MoveUpcoming(row.Index, 0), row.Index > 0));
        menu.Add(new(L.T("Sposta in fondo"), GDown, () => player.MoveUpcoming(row.Index, last), row.Index < last));
        menu.Add(new(L.T("Togli dai successivi"), GRemove, () => player.RemoveUpcoming(row.Index)));
        menu.Add(new(L.T("Riproduci brani simili"), GRadio, () => player.PlayRadio(t)));
        menu.Line();
        menu.Add(AddToPlaylist(t));
        menu.Add(FavoriteItem(t));
        menu.Add(new SheetEntry { Text = L.T("Tag"), Glyph = GTag, Sub = () => TagMenu(new[] { t }) });
        menu.Add(new SheetEntry { Text = L.T("Testo"), Glyph = GLyrics, Sub = () => LyricsMenu(t) });
        menu.Line();
        AddEditItems(menu, t);
        return menu;
    }

    // ------------------------------------------------------------------ several songs

    public static SheetMenu? ForSelection(Selection sel)
    {
        var tracks = sel.Tracks;
        if (tracks.Count == 0) return null;
        var main = tracks[0].Main;
        var menu = new SheetMenu { Title = L.F("{0} brani selezionati", tracks.Count), Cover = tracks[0] };
        menu.Add(new(L.T("Riproduci"), GPlay, () => { main.PlaySelection(tracks); sel.Stop(); }));
        menu.Add(new(L.T("Aggiungi alla coda"), GQueue, () => { main.Enqueue(tracks); sel.Stop(); }));
        menu.Line();
        menu.Add(new SheetEntry { Text = L.T("Aggiungi a playlist"), Glyph = GAdd, Sub = () => AddManyToPlaylist(tracks, sel) });
        if (sel.Owner?.Playlist is { } pl)
            menu.Add(new(L.F("Togli da «{0}»", Short(PlaylistViewModel.DisplayName(pl))), GRemove, () => { main.RemoveFromPlaylist(tracks, pl); sel.Stop(); }));
        menu.Add(new(L.T("Aggiungi ai Preferiti"), GHeart, () => { main.AddToFavorites(tracks); sel.Stop(); }));
        menu.Add(new SheetEntry { Text = L.T("Tag"), Glyph = GTag, Sub = () => TagMenu(tracks) });
        menu.Add(new(L.F("Cerca i testi online ({0})", tracks.Count), GLyrics, () => main.SearchLyrics(tracks)));
        menu.Line();
        menu.Add(new SheetEntry(L.F("Elimina {0} brani…", tracks.Count), GDelete, async () =>
        {
            await main.DeleteTracks(tracks);
            if (tracks.All(t => main.Library.Get(t.Id) == null)) sel.Stop();
        }) { Danger = true });
        return menu;
    }

    public static SheetMenu AddManyToPlaylist(IReadOnlyList<TrackViewModel> tracks, Selection? sel = null)
    {
        var main = tracks[0].Main;
        var menu = new SheetMenu { Title = L.T("Aggiungi a playlist"), Subtitle = L.Count(tracks.Count, "1 brano", "{0} brani"), Cover = tracks[0] };
        menu.Add(new(L.T("Nuova playlist…"), GAdd, async () => { await main.NewPlaylistWith(tracks); sel?.Stop(); }));
        menu.Line();
        foreach (var p in main.Playlists)
        {
            var pl = p.P;
            menu.Add(new(Short(p.Name), p.IsFavorites ? GHeartFill : GList, () => { main.AddToPlaylist(tracks, pl); sel?.Stop(); }));
        }
        return menu;
    }

    // ------------------------------------------------------------------ tags

    // Tick = every song has it; the sheet stays open to tick several.
    public static SheetMenu TagMenu(IReadOnlyList<TrackViewModel> tracks)
    {
        var main = tracks[0].Main;
        var menu = new SheetMenu { Title = L.T("Tag"), Subtitle = tracks.Count == 1 ? tracks[0].Title : L.Count(tracks.Count, "1 brano", "{0} brani"), Cover = tracks[0] };
        foreach (var tag in main.Tags)
        {
            var tg = tag;
            menu.Add(new SheetEntry
            {
                Text = Short(tg.Name), Dot = tg.Brush,
                Checked = () => tracks.All(t => t.TagIds.Contains(tg.Id)),
                Click = () => main.SetTag(tracks, tg, !tracks.All(t => t.TagIds.Contains(tg.Id))),
            });
        }
        menu.Line();
        menu.Add(new(L.T("Nuovo tag…"), GAdd, () => _ = main.NewTag(tracks)));
        return menu;
    }

    public static SheetMenu PlaylistTagMenu(PlaylistViewModel p)
    {
        var main = App.Host.Session!;
        var menu = new SheetMenu { Title = L.T("Tag"), Subtitle = p.Name, Cover = p };
        foreach (var tag in main.Tags)
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
        menu.Add(new(L.T("Nuovo tag…"), GAdd, () => _ = main.NewTag(playlist: p)));
        menu.Add(new(L.T("Metti i tag sui suoi brani…"), GTag, () => _ = main.TagPlaylistSongs(p), p.Count > 0));
        return menu;
    }

    // Tick the tags to use (the songs of a download get them); a new tag is ticked right away.
    public static SheetMenu TagPicker(ISet<string> chosen, MainViewModel main, Action changed)
    {
        var menu = new SheetMenu { Title = L.T("Tag dei brani scaricati") };
        foreach (var tag in main.Tags)
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
        menu.Add(new(L.T("Nuovo tag…"), GAdd, async () =>
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
    public static SheetMenu TagFilterMenu(TagFilter filter, MainViewModel main)
    {
        var menu = new SheetMenu { Title = L.T("Filtra per tag") };
        menu.Add(new SheetEntry { Text = L.T("Brani con tutti i tag scelti"), Glyph = GCheck, Checked = () => filter.MatchAll, Click = () => filter.MatchAll = true });
        menu.Add(new SheetEntry { Text = L.T("Brani con almeno uno dei tag scelti"), Glyph = GCheck, Checked = () => !filter.MatchAll, Click = () => filter.MatchAll = false });
        menu.Line();
        foreach (var tag in main.Tags)
        {
            var tg = tag;
            menu.Add(new SheetEntry { Text = Short(tg.Name), Dot = tg.Brush, Checked = () => filter.Contains(tg.Id), Click = () => filter.Toggle(tg.Id) });
        }
        menu.Line();
        menu.Add(new(L.T("Togli il filtro"), GRemove, filter.Clear, filter.IsActive));
        return menu;
    }

    public static SheetMenu ForTag(TagViewModel t)
    {
        var main = App.Host.Session!;
        var menu = new SheetMenu { Title = t.Name, Subtitle = t.CountText, Cover = t };
        menu.Add(new(L.T("Apri"), GOpen, () => main.OpenTag(t)));
        menu.Add(new(L.T("Modifica…"), GEdit, () => _ = main.EditTag(t)));
        menu.Add(new(L.T("Esporta in un file .ump…"), GExport, () => main.ExportPack(tag: t)));
        menu.Line();
        menu.Add(new SheetEntry(L.T("Elimina tag…"), GDelete, () => _ = main.DeleteTag(t)) { Danger = true });
        return menu;
    }

    // ------------------------------------------------------------------ playlists

    public static SheetMenu ForPlaylist(PlaylistViewModel p)
    {
        var main = App.Host.Session!;
        var menu = new SheetMenu { Title = p.Name, Subtitle = p.TotalText, Cover = p };
        menu.Add(new(L.T("Riproduci"), GPlay, () => main.PlayPlaylistCommand.Execute(p)));
        menu.Add(new(L.T("Apri"), GOpen, () => main.OpenPlaylist(p)));
        menu.Line();
        menu.Add(new SheetEntry { Text = L.T("Tag"), Glyph = GTag, Sub = () => PlaylistTagMenu(p) });
        menu.Add(new(L.T("Esporta in un file .ump…"), GExport, () => main.ExportPack(p)));
        if (!p.IsFavorites)
        {
            menu.Line();
            menu.Add(new(L.T("Rinomina…"), GRename, () => _ = main.RenamePlaylist(p)));
            menu.Add(new(L.T("Cambia immagine…"), GPicture, () => _ = main.ChangePlaylistCover(p)));
            if (p.HasCustomCover) menu.Add(new(L.T("Rimuovi immagine"), GRemove, () => main.RemovePlaylistCover(p)));
            menu.Line();
            menu.Add(new SheetEntry(L.T("Elimina playlist…"), GDelete, () => _ = main.DeletePlaylist(p)) { Danger = true });
        }
        return menu;
    }

    // ------------------------------------------------------------------ Listen together (not on the phone: only so the
    // shared view model compiles)

    public static SheetMenu? ForMembers(TogetherViewModel room, MemberViewModel? clicked = null) => null;
    public static SheetMenu? ForRoomItem(RoomItemViewModel item, TogetherViewModel room) => null;
    public static void Open(SheetMenu? menu, Control target, bool below)
    {
        if (menu != null) Open(menu);
    }
}
