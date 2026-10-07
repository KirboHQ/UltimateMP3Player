using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class Templates : ResourceDictionary
{
    private Point? _dragStart;

    public Templates() => InitializeComponent();

    private void Row_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TrackRow row) return;
        Menus.Open(MenuFor(row, (DependencyObject)sender), (UIElement)sender, false);
        e.Handled = true;
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TrackRow row) return;
        Menus.Open(MenuFor(row, (DependencyObject)sender), (UIElement)sender, true);
    }

    // A selected row acts on the whole selection.
    private static ContextMenu MenuFor(TrackRow row, DependencyObject source)
    {
        var item = Ui.FindAncestor<ListBoxItem>(source);
        if (item != null && ItemsControl.ItemsControlFromItemContainer(item) is ListBox list)
        {
            if (item.IsSelected && list.SelectedItems.Count > 1)
                return Menus.ForSelection(SelectionBar.Selected(list).Select(r => r.Track).ToList(), row.Owner);
            if (!item.IsSelected)
            {
                list.UnselectAll();
                item.IsSelected = true;
            }
        }
        return Menus.ForTrack(row.Track, row.Owner);
    }

    private void Card_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TrackViewModel t) return;
        Menus.Open(Menus.ForTrack(t, null), (UIElement)sender, false);
        e.Handled = true;
    }

    private void Playlist_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PlaylistViewModel p) return;
        Menus.Open(Menus.ForPlaylist(p), (UIElement)sender, false);
        e.Handled = true;
    }

    private void Row_MouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    // Rows can be dropped on a sidebar playlist or moved inside one.
    private void Row_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not Point start) return;
        if (e.OriginalSource is DependencyObject d && Ui.FindAncestor<ButtonBase>(d) != null) return;
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - start.X) < 6 && Math.Abs(p.Y - start.Y) < 6) return;
        _dragStart = null;
        if (sender is not FrameworkElement fe || fe.DataContext is not TrackRow row) return;
        DragVisuals.Run(fe, new DataObject(typeof(TrackRow), row), DragDropEffects.Copy | DragDropEffects.Move);
    }
}

// Context menus, built on open so they list the current playlists.
public static class Menus
{
    public static void Open(ContextMenu menu, UIElement target, bool below)
    {
        menu.PlacementTarget = target;
        menu.Placement = below ? PlacementMode.Bottom : PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, string glyph, Action click, bool enabled = true)
    {
        var m = new MenuItem { Header = header, IsEnabled = enabled };
        Ui.SetGlyph(m, glyph);
        m.Click += (_, _) => click();
        return m;
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
        menu.Items.Add(AddToPlaylist(t));
        if (owner?.Playlist is { } pl && !pl.IsFavorites)
            menu.Items.Add(Item(L.F("Rimuovi da «{0}»", Short(pl.Name)), "", () => main.RemoveFromPlaylist(t, pl)));
        menu.Items.Add(FavoriteItem(t));
        menu.Items.Add(TagSubmenu(new[] { t }));
        menu.Items.Add(LyricsSubmenu(t));
        if (!main.InRoom) menu.Items.Add(DjSubmenu(t));
        menu.Items.Add(new Separator());
        AddEditItems(menu, t);
        return menu;
    }

    private const string LyricsGlyph = "";

    // What the song has (with times, plain, nothing...), and the actions: show, search online (again), delete.
    public static MenuItem LyricsSubmenu(TrackViewModel t)
    {
        var main = t.Main;
        var sub = new SubmenuEntry { Header = L.T("Testo") };
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
            add.ToolTip = room.Denied(Core.Together.Perm.Add);
            ToolTipService.SetShowOnDisabled(add, true);
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
            var sub = new SubmenuEntry { Header = L.T("Salva in una playlist"), IsEnabled = item.IsReady };
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
            menu.Items.Add(Item(L.T("Copia link originale"), "", () => { try { Clipboard.SetText(url); main.Toast(L.T("Link copiato")); } catch { } }));
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
        if (item.IsFailed) menu.Items.Add(Item(L.T("Riprova a scaricarlo"), "", () => main.Radio.Prepare(item.Id)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("Salva nella libreria"), "", () => _ = main.Radio.Save(item, null), item.IsReady));
        var sub = new SubmenuEntry { Header = L.T("Salva in una playlist"), IsEnabled = item.IsReady };
        Ui.SetGlyph(sub, "");
        sub.Items.Add(Item(L.T("Nuova playlist…"), "", () =>
        {
            var name = Dialogs.Prompt(L.T("Nuova playlist"), L.T("Nome della playlist"), main.NewPlaylistName());
            if (!string.IsNullOrWhiteSpace(name)) _ = main.Radio.Save(item, main.Profile.CreatePlaylist(name));
        }));
        sub.Items.Add(new Separator());
        foreach (var p in main.Playlists)
        {
            var pl = p.P;
            sub.Items.Add(Item(Short(p.Name), p.IsFavorites ? "" : "", () => _ = main.Radio.Save(item, pl)));
        }
        menu.Items.Add(sub);
        if (!item.IsReady)
        {
            sub.ToolTip = L.T("Si può salvare quando è stato scaricato");
            ToolTipService.SetShowOnDisabled(sub, true);
        }
        menu.Items.Add(LyricsSubmenu(t));
        menu.Items.Add(Item(L.T("Riproduci brani simili"), RadioGlyph, () => player.PlayRadio(t)));
        menu.Items.Add(Item(L.T("Copia link originale"), "", () => { try { Clipboard.SetText(item.Song.Url); main.Toast(L.T("Link copiato")); } catch { } }));
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
        header.Children.Add(new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = tag.Brush, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(new TextBlock { Text = Short(tag.Name), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var m = new MenuItem { Header = header };
        Ui.SetGlyph(m, glyph);
        return m;
    }

    public static MenuItem DjSubmenu(TrackViewModel t)
    {
        var sub = new SubmenuEntry { Header = L.T("Carica nel DJ") };
        Ui.SetGlyph(sub, "");
        sub.Items.Add(Item(L.T("Traccia A"), "", () => t.Main.LoadInDj(t, false)));
        sub.Items.Add(Item(L.T("Traccia B"), "", () => t.Main.LoadInDj(t, true)));
        return sub;
    }

    public static MenuItem TagSubmenu(IReadOnlyList<TrackViewModel> tracks)
    {
        var sub = new SubmenuEntry { Header = L.T("Tag") };
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
        var sub = new SubmenuEntry { Header = L.T("Tag") };
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
        menu.Items.Add(Item(L.T("Mostra nella cartella"), "", () => main.ShowInFolder(t)));
        if (t.T.SourceUrl is { } url && url.StartsWith("http"))
            menu.Items.Add(Item(L.T("Copia link originale"), "", () => { try { Clipboard.SetText(url); main.Toast(L.T("Link copiato")); } catch { } }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("Elimina brano…"), "", () => main.DeleteTrack(t)));
    }

    public static ContextMenu ForSelection(IReadOnlyList<TrackViewModel> tracks, ITrackList owner)
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
        var sub = new SubmenuEntry { Header = L.T("Aggiungi a playlist") };
        Ui.SetGlyph(sub, "");
        foreach (var i in PlaylistItems(tracks)) sub.Items.Add(i);
        menu.Items.Add(sub);
        if (owner.Playlist is { } pl)
            menu.Items.Add(Item(L.F("Togli da «{0}»", Short(PlaylistViewModel.DisplayName(pl))), "", () => main.RemoveFromPlaylist(tracks, pl)));
        menu.Items.Add(Item(L.T("Aggiungi ai Preferiti"), "", () => main.AddToFavorites(tracks)));
        menu.Items.Add(TagSubmenu(tracks));
        menu.Items.Add(Item(L.F("Cerca i testi online ({0})", tracks.Count), LyricsGlyph, () => main.SearchLyrics(tracks)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.F("Elimina {0} brani…", tracks.Count), "", () => _ = main.DeleteTracks(tracks)));
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
        var sub = new SubmenuEntry { Header = L.T("Aggiungi a playlist") };
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
