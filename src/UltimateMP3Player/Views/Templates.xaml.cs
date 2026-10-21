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
        var many = SelectionAround(sender);
        Menus.Open(many.Count > 1 ? Menus.ForSelection(SelectionBar.SongsOf(many), null) : Menus.ForTrack(t, null), (UIElement)sender, false);
        e.Handled = true;
    }

    private void Playlist_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PlaylistViewModel p) return;
        var many = SelectionAround(sender).OfType<PlaylistViewModel>().ToList();
        Menus.Open(many.Count > 1 ? Menus.ForPlaylists(many) : Menus.ForPlaylist(p), (UIElement)sender, false);
        e.Handled = true;
    }

    // Right click on an item of a list where several are selected: all of them (a click on one not selected selects only it).
    public static List<object> SelectionAround(object sender)
    {
        if (sender is not DependencyObject d || Ui.FindAncestor<ListBoxItem>(d) is not { } item ||
            ItemsControl.ItemsControlFromItemContainer(item) is not ListBox list) return new List<object>();
        if (item.IsSelected && list.SelectedItems.Count > 1) return SelectionBar.SelectedItems(list);
        if (!item.IsSelected && list.SelectedItems.Count > 0) list.UnselectAll();
        return new List<object>();
    }

    private void Row_MouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    // Rows can be dropped on a sidebar playlist or moved inside one; pulled to the right, they go into the queue (RowSwipe).
    private void Row_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not Point start) return;
        if (e.OriginalSource is DependencyObject d && Ui.FindAncestor<ButtonBase>(d) != null) return;
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - start.X) < 6 && Math.Abs(p.Y - start.Y) < 6) return;
        _dragStart = null;
        if (sender is not FrameworkElement fe || fe.DataContext is not TrackRow row) return;
        if (p.X - start.X > 1.5 * Math.Abs(p.Y - start.Y) && RowSwipe.StartDrag(fe, p)) return;
        DragVisuals.Run(fe, new DataObject(typeof(TrackRow), row), DragDropEffects.Copy | DragDropEffects.Move);
    }
}

// Context menus, built on open so they list the current playlists.
public static partial class Menus
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

    private static void Tip(Control c, string? text, bool onDisabled = false)
    {
        c.ToolTip = text;
        if (onDisabled) ToolTipService.SetShowOnDisabled(c, true);
    }
}
