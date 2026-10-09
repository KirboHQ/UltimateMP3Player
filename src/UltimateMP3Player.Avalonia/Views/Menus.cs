using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.VisualTree;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// Context menus, built on open so they list the current playlists.
public static class Menus
{
    public static void Open(ContextMenu menu, Control target, bool below)
    {
        menu.PlacementTarget = target;
        menu.Placement = below ? PlacementMode.BottomEdgeAlignedLeft : PlacementMode.Pointer;
        menu.Open(target);
    }

    // The "…" of a song row (a command, the templates have no code behind): its menu under the button.
    public static readonly System.Windows.Input.ICommand MoreCommand = new RelayCommand(p =>
    {
        if (p is Control c && c.DataContext is TrackRow row) Open(MenuFor(row, c), c, true);
    });

    // A selected row acts on the whole selection.
    public static ContextMenu MenuFor(TrackRow row, Control source)
    {
        var item = source.FindAncestorOfType<ListBoxItem>(true);
        if (item != null && item.FindAncestorOfType<ListBox>() is { } list)
        {
            if (item.IsSelected && list.SelectedItems?.Count > 1)
                return ForSelection(SelectionBar.Selected(list).Select(r => r.Track).ToList(), row.Owner);
            if (!item.IsSelected)
            {
                list.UnselectAll();
                item.IsSelected = true;
            }
        }
        return ForTrack(row.Track, row.Owner);
    }

    private static MenuItem Item(string header, string glyph, Action click, bool enabled = true)
    {
        var m = new MenuItem { Header = header, IsEnabled = enabled };
        Ui.SetGlyph(m, glyph);
        m.Click += (_, _) => click();
        return m;
    }

    private static void Tip(Control c, string? text, bool onDisabled = false)
    {
        ToolTip.SetTip(c, text);
        if (onDisabled) ToolTip.SetShowOnDisabled(c, true);
    }

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
            if (owner != null) menu.Items.Add(Item(L.T("Riproduci"), "", () => main.Player.PlayFrom(owner, t)));
            else if (main.Player.Current != t) menu.Items.Add(Item(L.T("Riproduci"), "", () => main.Player.PlaySingle(t.T)));
            menu.Items.Add(Item(L.T("Riproduci dopo"), "", () => main.Player.PlayNext(t)));
            menu.Items.Add(Item(L.T("Aggiungi alla coda"), "", () => main.Player.Enqueue(t)));
            menu.Items.Add(Item(L.T("Riproduci brani simili"), RadioGlyph, () => main.Player.PlayRadio(t)));
        }
        menu.Items.Add(new Separator());
        AddSaveItems(menu, new[] { t });
        menu.Items.Add(AddToPlaylist(t));
        if (owner?.Playlist is { } pl && !pl.IsFavorites)
            menu.Items.Add(Item(L.F("Rimuovi da «{0}»", Short(pl.Name)), "", () => main.RemoveFromPlaylist(t, pl)));
        menu.Items.Add(FavoriteItem(t));
        menu.Items.Add(TagSubmenu(new[] { t }));
        menu.Items.Add(LyricsSubmenu(t));
        if (t.IsSaved && !t.HasVideo && main.Library.Get(t.Id) == t.T)
            menu.Items.Add(Item(L.T("Cerca il video…"), VideoGlyph, () => _ = main.FindVideo(t)));
        if (!main.InRoom) menu.Items.Add(DjSubmenu(t));
        menu.Items.Add(new Separator());
        AddEditItems(menu, t);
        return menu;
    }

    private const string SaveGlyph = "", VideoGlyph = "", DeleteGlyph = "", SelectGlyph = "", AddGlyph = "";

    // "Save on the device" for the songs not saved (or how far their save is).
    private static void AddSaveItems(ContextMenu menu, IReadOnlyList<TrackViewModel> tracks)
    {
        var main = tracks[0].Main;
        var cloud = tracks.Where(t => t.IsCloud && !t.IsSaving).ToList();
        if (cloud.Count > 0)
            menu.Items.Add(Item(tracks.Count == 1 ? L.T("Salva sul dispositivo") : L.F("Salva sul dispositivo ({0})", cloud.Count), SaveGlyph,
                () => _ = main.SaveToDevice(cloud)));
        else if (tracks.Any(t => t.IsSaving))
            menu.Items.Add(Item(L.T("Salvataggio sul dispositivo in corso…"), SaveGlyph, () => { }, false));
    }

    private const string LyricsGlyph = "";

    // What the song has (with times, plain, nothing...), and the actions: show, search online (again), delete.
    public static MenuItem LyricsSubmenu(TrackViewModel t)
    {
        var main = t.Main;
        var sub = new MenuItem { Header = L.T("Testo") };
        Ui.SetGlyph(sub, LyricsGlyph);
        bool searching = main.Host.Lyrics.IsSearching(t.T);
        var state = searching ? L.T("Ricerca in corso…") : t.T.Lyrics switch
        {
            LyricsKind.Synced => L.T("Con i tempi: si illumina mentre ascolti"),
            LyricsKind.Plain => L.T("Senza tempi"),
            LyricsKind.Instrumental => L.T("Brano strumentale"),
            LyricsKind.None => L.T("Non trovato online"),
            _ => L.T("Non ancora cercato"),
        };
        sub.Items.Add(Item(state, "", () => { }, false));
        sub.Items.Add(new Separator());
        if (t.HasLyrics && main.Player.Current == t)
            sub.Items.Add(Item(L.T("Mostra il testo"), "", main.ShowLyrics));
        sub.Items.Add(Item(t.HasLyrics ? L.T("Cerca di nuovo online") : L.T("Cerca online"), "", () => main.SearchLyrics(new[] { t }), !searching));
        if (t.HasLyrics) sub.Items.Add(Item(L.T("Elimina il testo"), "", () => main.DeleteLyrics(t)));
        return sub;
    }

    // In a room: songs go into the room's queue (greyed out without the host's permission).
    private static void AddRoomItems(ContextMenu menu, IReadOnlyList<TrackViewModel> tracks)
    {
        var room = tracks[0].Main.Together;
        var add = Item(tracks.Count == 1 ? L.T("Aggiungi alla coda della stanza") : L.F("Aggiungi {0} brani alla stanza", tracks.Count), "",
            () => room.Add(tracks), room.CanAdd);
        var next = Item(L.T("Metti come prossimo nella stanza"), "", () => room.Add(tracks, next: true), room.CanAdd && room.CanRemove);
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
            menu.Items.Add(Item(L.T("Fallo partire ora"), "", () => room.PlayNow(item.Id), room.CanRemove && room.CanSkip));
            menu.Items.Add(Item(L.T("Sposta in cima"), "", () => room.MoveTo(item, 0), room.CanRemove && index > 0));
            menu.Items.Add(Item(L.T("Sposta in fondo"), "", () => room.MoveTo(item, last), room.CanRemove && index < last));
            menu.Items.Add(Item(L.T("Togli dalla coda"), "", () => room.Remove(item), item.CanRemove));
        }
        else menu.Items.Add(Item(L.T("Salta"), "", room.Skip, room.CanSkip));
        menu.Items.Add(new Separator());
        if (item.InLibrary)
        {
            menu.Items.Add(AddToPlaylist(item.Track));
            menu.Items.Add(FavoriteItem(item.Track));
        }
        else
        {
            menu.Items.Add(Item(L.T("Salva nella libreria"), "", () => _ = room.Save(item, null), item.IsReady));
            var sub = new MenuItem { Header = L.T("Salva in una playlist"), IsEnabled = item.IsReady };
            Ui.SetGlyph(sub, "");
            sub.Items.Add(Item(L.T("Nuova playlist…"), "", () =>
            {
                var name = Dialogs.Prompt(L.T("Nuova playlist"), L.T("Nome della playlist"), main.NewPlaylistName());
                if (!string.IsNullOrWhiteSpace(name)) _ = room.Save(item, main.Profile.CreatePlaylist(name));
            }));
            sub.Items.Add(new Separator());
            foreach (var p in main.Playlists)
            {
                var pl = p.P;
                sub.Items.Add(Item(Short(p.Name), p.IsFavorites ? "" : "", () => _ = room.Save(item, pl)));
            }
            menu.Items.Add(sub);
        }
        if (item.Item.SourceUrl is { } url)
            menu.Items.Add(Item(L.T("Copia link originale"), "", () => { try { Ui.CopyText(url); main.Toast(L.T("Link copiato")); } catch { } }));
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
            menu.Items.Add(Item(L.T("Riproduci ora"), "", () => _ = player.JumpTo(row.Index)));
            menu.Items.Add(Item(L.T("Sposta in cima"), "", () => player.MoveUpcoming(row.Index, 0), row.Index > 0));
            menu.Items.Add(Item(L.T("Sposta in fondo"), "", () => player.MoveUpcoming(row.Index, last), row.Index < last));
            menu.Items.Add(Item(L.T("Togli dai successivi"), "", () => player.RemoveUpcoming(row.Index)));
        }
        else if (player.Current == t) menu.Items.Add(Item(L.T("Salta"), "", () => player.NextCommand.Execute(null)));
        // (a card of the recently played, a row of the statistics)
        else menu.Items.Add(Item(L.T("Riproduci"), "", () => player.PlaySingle(t.T)));
        if (item.IsFailed) menu.Items.Add(Item(L.T("Riprova a scaricarlo"), "", () => main.Radio.Prepare(item.Id)));
        menu.Items.Add(new Separator());
        // Only into the library (in the cloud, nothing downloaded: also a playlist, the favourites), or saved on the device too.
        menu.Items.Add(Item(L.T("Aggiungi alla libreria"), AddGlyph, () => main.AddToLibrary(t)));
        menu.Items.Add(Item(L.T("Salva sul dispositivo"), SaveGlyph, () => _ = main.Radio.Save(item, null)));
        menu.Items.Add(AddToPlaylist(t));
        menu.Items.Add(FavoriteItem(t));
        menu.Items.Add(LyricsSubmenu(t));
        menu.Items.Add(Item(L.T("Riproduci brani simili"), RadioGlyph, () => player.PlayRadio(t)));
        menu.Items.Add(Item(L.T("Copia link originale"), "", () => { try { Ui.CopyText(item.Song.Url); main.Toast(L.T("Link copiato")); } catch { } }));
        if (row == null && player.Current != t)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(L.T("Elimina…"), DeleteGlyph, () => _ = main.DeleteTracks(new[] { t })));
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
            menu.Items.Add(Item(room.IsHost ? L.T("Seleziona qualcuno nell'elenco") : L.T("Solo l'host può cambiare i permessi"), "", () => { }, false));
            return menu;
        }
        menu.Items.Add(Item(who.Count == 1 ? who[0].Name : L.F("{0} persone", who.Count), "", () => { }, false));
        menu.Items.Add(new Separator());
        foreach (var (perm, text) in new[]
                 {
                     (Core.Together.Perm.Add, L.T("Può aggiungere brani")),
                     (Core.Together.Perm.Remove, L.T("Può togliere e spostare brani")),
                     (Core.Together.Perm.Skip, L.T("Può saltare i brani")),
                     (Core.Together.Perm.Pause, L.T("Può mettere in pausa, andare avanti e indietro e ripetere il brano")),
                     (Core.Together.Perm.Speed, L.T("Può cambiare la velocità")),
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
        menu.Items.Add(Item(L.T("Dai tutti i permessi"), "", () => room.SetAllPerms(who, true)));
        menu.Items.Add(Item(L.T("Togli tutti i permessi"), "", () => room.SetAllPerms(who, false)));
        if (who.Count == 1 && !who[0].Away)
            menu.Items.Add(Item(L.T("Rendi host"), "", () => room.MakeHost(who[0])));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(who.Count == 1 ? L.T("Espelli dalla stanza…") : L.F("Espelli {0} persone…", who.Count), "", () => room.Kick(who)));
        return menu;
    }

    // ------------------------------------------------------------------ tags

    private const string Check = "", Some = "", TagGlyph = "";

    // Tick = every song has it, dash = only some. Stays open to tick several.
    private static IEnumerable<object> TagItems(IReadOnlyList<TrackViewModel> tracks)
    {
        var main = tracks[0].Main;
        foreach (var tag in main.Tags)
        {
            var tg = tag;
            var item = TagItem(tg, StateOf(tracks, tg));
            item.StaysOpenOnClick = true;
            item.Click += (_, _) =>
            {
                bool on = StateOf(tracks, tg) != Check;
                main.SetTag(tracks, tg, on);
                Ui.SetGlyph(item, on ? Check : null);
            };
            yield return item;
        }
        if (main.Tags.Count > 0) yield return new Separator();
        yield return Item(L.T("Nuovo tag…"), "", () => _ = main.NewTag(tracks));
    }

    private static string? StateOf(IReadOnlyList<TrackViewModel> tracks, TagViewModel tag)
    {
        int n = tracks.Count(t => t.TagIds.Contains(tag.Id));
        return n == 0 ? null : n == tracks.Count ? Check : Some;
    }

    // Coloured dot and name.
    private static MenuItem TagItem(TagViewModel tag, string? glyph)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = tag.Brush, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(new TextBlock { Text = Short(tag.Name), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var m = new MenuItem { Header = header };
        Ui.SetGlyph(m, glyph);
        return m;
    }

    public static MenuItem DjSubmenu(TrackViewModel t)
    {
        var sub = new MenuItem { Header = L.T("Carica nel DJ") };
        Ui.SetGlyph(sub, "");
        sub.Items.Add(Item(L.T("Traccia A"), "", () => t.Main.LoadInDj(t, false)));
        sub.Items.Add(Item(L.T("Traccia B"), "", () => t.Main.LoadInDj(t, true)));
        return sub;
    }

    public static MenuItem TagSubmenu(IReadOnlyList<TrackViewModel> tracks)
    {
        var sub = new MenuItem { Header = L.T("Tag") };
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
        foreach (var tag in main.Tags)
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
        yield return Item(L.T("Nuovo tag…"), "", () => _ = main.NewTag(playlist: p));
    }

    private static MenuItem PlaylistTagSubmenu(PlaylistViewModel p)
    {
        var sub = new MenuItem { Header = L.T("Tag") };
        Ui.SetGlyph(sub, TagGlyph);
        foreach (var i in PlaylistTagItems(p)) sub.Items.Add(i);
        return sub;
    }

    // Playlist header: its tags, and the way to put them on its songs.
    public static ContextMenu PlaylistTagMenu(PlaylistViewModel p)
    {
        var menu = new ContextMenu();
        foreach (var i in PlaylistTagItems(p)) menu.Items.Add(i);
        menu.Items.Add(Item(L.T("Metti i tag sui suoi brani…"), "", () => _ = App.Host.Session!.TagPlaylistSongs(p), p.Count > 0));
        return menu;
    }

    // Tick the tags to use (e.g. for the songs of a download); a new tag is ticked right away.
    public static ContextMenu TagPicker(ISet<string> chosen, MainViewModel main, Action changed)
    {
        var menu = new ContextMenu();
        foreach (var tag in main.Tags)
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
        menu.Items.Add(Item(L.T("Nuovo tag…"), "", async () =>
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
        menu.Items.Add(Item(L.T("Mostra i brani con:"), "", () => { }, false));
        var all = Item(L.T("tutti i tag scelti"), filter.MatchAll ? Check : "", () => { });
        var any = Item(L.T("almeno uno dei tag scelti"), filter.MatchAll ? "" : Check, () => { });
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
        menu.Items.Add(Item(L.T("Togli il filtro"), "", filter.Clear, filter.IsActive));
        return menu;
    }

    public static ContextMenu ForTag(TagViewModel t)
    {
        var main = App.Host.Session!;
        var menu = new ContextMenu();
        menu.Items.Add(Item(L.T("Apri"), "", () => main.OpenTag(t)));
        menu.Items.Add(Item(L.T("Modifica…"), "", () => _ = main.EditTag(t)));
        menu.Items.Add(Item(L.T("Esporta in un file .ump…"), "", () => main.ExportPack(tag: t)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("Elimina tag…"), "", () => _ = main.DeleteTag(t)));
        return menu;
    }

    private static MenuItem FavoriteItem(TrackViewModel t) => t.IsFavorite
        ? Item(L.T("Togli dai Preferiti"), "", () => t.Main.ToggleFavorite(t))
        : Item(L.T("Aggiungi ai Preferiti"), "", () => t.Main.ToggleFavorite(t));

    // Edit, cover, folder, link, delete: the same everywhere.
    private static void AddEditItems(ContextMenu menu, TrackViewModel t)
    {
        var main = t.Main;
        menu.Items.Add(Item(L.T("Modifica informazioni…"), "", () => _ = main.EditTrack(t)));
        menu.Items.Add(Item(L.T("Cambia copertina…"), "", () => _ = main.ChangeTrackCover(t)));
        if (t.IsSaved) menu.Items.Add(Item(L.T("Mostra nella cartella"), "", () => main.ShowInFolder(t)));
        if (t.T.SourceUrl is { } url && url.StartsWith("http"))
            menu.Items.Add(Item(L.T("Copia link originale"), "", () => { try { Ui.CopyText(url); main.Toast(L.T("Link copiato")); } catch { } }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("Elimina…"), DeleteGlyph, () => _ = main.DeleteTrack(t)));
    }

    // Right click on an item of a list where several are selected: all of them (a click on one not selected selects only it).
    public static List<object> SelectionAround(Control c)
    {
        if (c.FindAncestorOfType<ListBoxItem>(true) is not { } item || item.FindAncestorOfType<ListBox>() is not { } list) return new List<object>();
        if (item.IsSelected && list.SelectedItems?.Count > 1) return SelectionBar.SelectedItems(list);
        if (!item.IsSelected && list.SelectedItems?.Count > 0) list.UnselectAll();
        return new List<object>();
    }

    // Several selected playlists: what the selection bar does, from the right click.
    public static ContextMenu ForPlaylists(IReadOnlyList<PlaylistViewModel> lists)
    {
        var main = App.Host.Session!;
        var menu = new ContextMenu();
        menu.Items.Add(Item(L.Count(lists.Count, "1 playlist selezionata", "{0} playlist selezionate"), SelectGlyph, () => { }, false));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("Riproduci"), "", () => main.PlayPlaylists(lists)));
        menu.Items.Add(Item(L.T("Aggiungi alla coda"), "", () => main.EnqueuePlaylists(lists)));
        menu.Items.Add(new Separator());
        int cloud = main.SongsOf(lists).Count(t => t.IsCloud && !t.IsSaving);
        if (cloud > 0) menu.Items.Add(Item(L.F("Salva sul dispositivo ({0})", cloud), SaveGlyph, () => _ = main.SavePlaylists(lists)));
        var tags = new MenuItem { Header = L.T("Tag") };
        Ui.SetGlyph(tags, TagGlyph);
        foreach (var i in PlaylistsTagItems(lists)) tags.Items.Add(i);
        menu.Items.Add(tags);
        menu.Items.Add(Item(L.T("Esporta in un file .ump…"), "", () => main.ExportPlaylists(lists)));
        if (lists.Any(p => !p.IsFavorites))
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(L.Count(lists.Count(p => !p.IsFavorites), "Elimina la playlist…", "Elimina {0} playlist…"), DeleteGlyph, () => _ = main.DeletePlaylists(lists)));
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
        foreach (var tag in main.Tags)
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
        yield return Item(L.T("Nuovo tag…"), "", async () =>
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
        menu.Items.Add(Item(L.F("{0} brani selezionati", rows.Count), SelectGlyph, () => { }, false));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("Sposta in cima"), "", () => player.MoveUpcomingToTop(places)));
        menu.Items.Add(Item(L.T("Sposta in fondo"), "", () => player.MoveUpcomingToBottom(places)));
        menu.Items.Add(Item(L.T("Togli dai successivi"), "", () => player.RemoveUpcoming(places)));
        if (!player.InRoom)
        {
            menu.Items.Add(new Separator());
            AddSaveItems(menu, songs);
            var sub = new MenuItem { Header = L.T("Aggiungi a playlist") };
            Ui.SetGlyph(sub, "");
            foreach (var i in PlaylistItems(songs)) sub.Items.Add(i);
            menu.Items.Add(sub);
            menu.Items.Add(Item(L.T("Aggiungi ai Preferiti"), "", () => main.AddToFavorites(songs)));
        }
        return menu;
    }

    // Several selected downloads.
    public static ContextMenu ForJobs(IReadOnlyList<DownloadJobViewModel> jobs)
    {
        var queue = App.Host.Session!.Queue;
        var menu = new ContextMenu();
        menu.Items.Add(Item(L.Count(jobs.Count, "1 download selezionato", "{0} download selezionati"), SelectGlyph, () => { }, false));
        menu.Items.Add(new Separator());
        int failed = jobs.Count(j => j.CanRetry), youTube = jobs.Count(j => j.CanRetryYouTube), active = jobs.Count(j => j.IsActive);
        menu.Items.Add(Item(L.F("Riprova ({0})", failed), "", () => queue.Retry(jobs), failed > 0));
        menu.Items.Add(Item(L.F("Riprova su YouTube ({0})", youTube), "", () => queue.RetryOnYouTube(jobs), youTube > 0));
        menu.Items.Add(Item(L.F("Annulla ({0})", active), "", () => queue.Cancel(jobs), active > 0));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("Togli dalla lista"), DeleteGlyph, () => queue.Remove(jobs)));
        return menu;
    }

    public static ContextMenu ForSelection(IReadOnlyList<TrackViewModel> tracks, ITrackList? owner)
    {
        var main = tracks[0].Main;
        var menu = new ContextMenu();
        menu.Items.Add(Item(L.F("{0} brani selezionati", tracks.Count), "", () => { }, false));
        menu.Items.Add(new Separator());
        if (main.InRoom) AddRoomItems(menu, tracks);
        else
        {
            menu.Items.Add(Item(L.T("Riproduci"), "\uE768", () => main.PlaySelection(tracks)));
            menu.Items.Add(Item(L.T("Aggiungi alla coda"), "\uE8FD", () => main.Enqueue(tracks)));
        }
        menu.Items.Add(new Separator());
        AddSaveItems(menu, tracks);
        var sub = new MenuItem { Header = L.T("Aggiungi a playlist") };
        Ui.SetGlyph(sub, "");
        foreach (var i in PlaylistItems(tracks)) sub.Items.Add(i);
        menu.Items.Add(sub);
        if (owner?.Playlist is { } pl)
            menu.Items.Add(Item(L.F("Togli da «{0}»", Short(PlaylistViewModel.DisplayName(pl))), "", () => main.RemoveFromPlaylist(tracks, pl)));
        menu.Items.Add(Item(L.T("Aggiungi ai Preferiti"), "", () => main.AddToFavorites(tracks)));
        menu.Items.Add(TagSubmenu(tracks));
        menu.Items.Add(Item(L.F("Cerca i testi online ({0})", tracks.Count), LyricsGlyph, () => main.SearchLyrics(tracks)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.F("Elimina {0} brani…", tracks.Count), DeleteGlyph, () => _ = main.DeleteTracks(tracks)));
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
        yield return Item(L.T("Nuova playlist…"), "", () => _ = main.NewPlaylistWith(tracks));
        yield return new Separator();
        foreach (var p in main.Playlists)
        {
            var pl = p.P;
            yield return Item(Short(p.Name), p.IsFavorites ? "" : "", () => main.AddToPlaylist(tracks, pl));
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
        menu.Items.Add(Item(L.T("Riproduci ora"), "", () => _ = player.JumpTo(row.Index)));
        menu.Items.Add(Item(L.T("Sposta in cima"), "", () => player.MoveUpcoming(row.Index, 0), row.Index > 0));
        menu.Items.Add(Item(L.T("Sposta in fondo"), "", () => player.MoveUpcoming(row.Index, last), row.Index < last));
        menu.Items.Add(Item(L.T("Togli dai successivi"), "", () => player.RemoveUpcoming(row.Index)));
        menu.Items.Add(Item(L.T("Riproduci brani simili"), RadioGlyph, () => player.PlayRadio(t)));
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
        var sub = new MenuItem { Header = L.T("Aggiungi a playlist") };
        Ui.SetGlyph(sub, "");
        sub.Items.Add(Item(L.T("Nuova playlist…"), "", () => _ = main.NewPlaylist(t)));
        sub.Items.Add(new Separator());
        foreach (var p in main.Playlists)
        {
            var pl = p.P;
            bool has = main.Profile.Contains(pl, t.Id);
            sub.Items.Add(Item(Short(p.Name) + (has ? "  ✓" : ""), p.IsFavorites ? "" : "", () => main.AddToPlaylist(t, pl), !has));
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
        menu.Items.Add(Item(L.T("Riproduci"), "", () => main.PlayPlaylistCommand.Execute(p)));
        menu.Items.Add(Item(L.T("Apri"), "", () => main.OpenPlaylist(p)));
        menu.Items.Add(new Separator());
        menu.Items.Add(PlaylistTagSubmenu(p));
        menu.Items.Add(Item(L.T("Metti i tag sui suoi brani…"), "\uE8B3", () => _ = main.TagPlaylistSongs(p), p.Count > 0));
        menu.Items.Add(Item(L.T("Esporta in un file .ump…"), "", () => main.ExportPack(p)));
        // Its songs in the cloud saved on the device; or the space its songs take, freed (the window says how).
        if (p.CloudCount > 0) menu.Items.Add(Item(L.F("Salva sul dispositivo ({0})", p.CloudCount), SaveGlyph, () => _ = main.SavePlaylists(new[] { p })));
        if (p.Count > p.CloudCount)
            menu.Items.Add(Item(L.T("Libera spazio…"), DeleteGlyph, () => _ = main.DeleteTracks(p.Songs.Where(t => t.IsSaved).ToList())));
        if (!p.IsFavorites)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(L.T("Rinomina…"), "", () => _ = main.RenamePlaylist(p)));
            menu.Items.Add(Item(L.T("Cambia immagine…"), "", () => _ = main.ChangePlaylistCover(p)));
            if (p.HasCustomCover) menu.Items.Add(Item(L.T("Rimuovi immagine"), "", () => main.RemovePlaylistCover(p)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(L.T("Elimina playlist…"), "", () => _ = main.DeletePlaylist(p)));
        }
        return menu;
    }

    private static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;
}
