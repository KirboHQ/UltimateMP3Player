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
        var menu = new ContextMenu();
        if (owner != null) menu.Items.Add(Item(L.T("Riproduci"), "", () => main.Player.PlayFrom(owner, t)));
        else if (main.Player.Current != t) menu.Items.Add(Item(L.T("Riproduci"), "", () => main.Player.PlaySingle(t.T)));
        menu.Items.Add(Item(L.T("Riproduci dopo"), "", () => main.Player.PlayNext(t)));
        menu.Items.Add(Item(L.T("Aggiungi alla coda"), "", () => main.Player.Enqueue(t)));
        menu.Items.Add(new Separator());
        menu.Items.Add(AddToPlaylist(t));
        if (owner?.Playlist is { } pl && !pl.IsFavorites)
            menu.Items.Add(Item(L.F("Rimuovi da «{0}»", Short(pl.Name)), "", () => main.RemoveFromPlaylist(t, pl)));
        menu.Items.Add(FavoriteItem(t));
        menu.Items.Add(new Separator());
        AddEditItems(menu, t);
        return menu;
    }

    private static MenuItem FavoriteItem(TrackViewModel t) => t.IsFavorite
        ? Item(L.T("Togli dai Preferiti"), "", () => t.Main.ToggleFavorite(t))
        : Item(L.T("Aggiungi ai Preferiti"), "", () => t.Main.ToggleFavorite(t));

    // Edit, cover, folder, link, delete: the same everywhere.
    private static void AddEditItems(ContextMenu menu, TrackViewModel t)
    {
        var main = t.Main;
        menu.Items.Add(Item(L.T("Modifica informazioni…"), "", () => main.EditTrack(t)));
        menu.Items.Add(Item(L.T("Cambia copertina…"), "", () => main.ChangeTrackCover(t)));
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
        menu.Items.Add(Item(L.T("Riproduci"), "", () => main.PlaySelection(tracks)));
        menu.Items.Add(Item(L.T("Aggiungi alla coda"), "", () => main.Enqueue(tracks)));
        menu.Items.Add(new Separator());
        var sub = new MenuItem { Header = L.T("Aggiungi a playlist") };
        Ui.SetGlyph(sub, "");
        foreach (var i in PlaylistItems(tracks)) sub.Items.Add(i);
        menu.Items.Add(sub);
        if (owner.Playlist is { } pl)
            menu.Items.Add(Item(L.F("Togli da «{0}»", Short(PlaylistViewModel.DisplayName(pl))), "", () => main.RemoveFromPlaylist(tracks, pl)));
        menu.Items.Add(Item(L.T("Aggiungi ai Preferiti"), "", () => main.AddToFavorites(tracks)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.F("Elimina {0} brani…", tracks.Count), "", () => main.DeleteTracks(tracks)));
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
        yield return Item(L.T("Nuova playlist…"), "", () => main.NewPlaylistWith(tracks));
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
        int last = player.UpNext.LastOrDefault()?.Index ?? row.Index;
        var menu = new ContextMenu();
        menu.Items.Add(Item(L.T("Riproduci ora"), "", () => _ = player.JumpTo(row.Index)));
        menu.Items.Add(Item(L.T("Sposta in cima"), "", () => player.MoveUpcoming(row.Index, 0), row.Index > 0));
        menu.Items.Add(Item(L.T("Sposta in fondo"), "", () => player.MoveUpcoming(row.Index, last), row.Index < last));
        menu.Items.Add(Item(L.T("Togli dai successivi"), "", () => player.RemoveUpcoming(row.Index)));
        menu.Items.Add(new Separator());
        menu.Items.Add(AddToPlaylist(t));
        menu.Items.Add(FavoriteItem(t));
        menu.Items.Add(new Separator());
        AddEditItems(menu, t);
        return menu;
    }

    public static MenuItem AddToPlaylist(TrackViewModel t)
    {
        var main = t.Main;
        var sub = new MenuItem { Header = L.T("Aggiungi a playlist") };
        Ui.SetGlyph(sub, "");
        sub.Items.Add(Item(L.T("Nuova playlist…"), "", () => main.NewPlaylist(t)));
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
        if (!p.IsFavorites)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(L.T("Rinomina…"), "", () => main.RenamePlaylist(p)));
            menu.Items.Add(Item(L.T("Cambia immagine…"), "", () => main.ChangePlaylistCover(p)));
            if (p.HasCustomCover) menu.Items.Add(Item(L.T("Rimuovi immagine"), "", () => main.RemovePlaylistCover(p)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(L.T("Elimina playlist…"), "", () => main.DeletePlaylist(p)));
        }
        return menu;
    }

    private static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;
}
