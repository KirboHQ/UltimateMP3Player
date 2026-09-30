using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace UltimateMP3Player.Views;

// Menu entry with a submenu: leaving it on the way to its submenu doesn't close it (see MenuAim).
public sealed class SubmenuEntry : MenuItem
{
    // Same look as every other menu entry.
    public SubmenuEntry() => SetResourceReference(StyleProperty, typeof(MenuItem));

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        // WPF closes the submenu after Windows' MenuShowDelay as soon as the mouse is anywhere else in the menu,
        // even the thin gap before the submenu: with the delay at 0 it closed at once unless the mouse moved fast.
        if (IsSubmenuOpen && MenuAim.Keeps(this)) return;
        base.OnMouseLeave(e);
    }
}

// Right-click menus: while the mouse heads for an open submenu, nothing on the way closes it.
// The other entries stop being hit (so they don't take over) and the entry that opened it doesn't close it
// (SubmenuEntry); both come back as soon as the mouse goes elsewhere or rests on the way for a moment.
public static class MenuAim
{
    // How long the mouse may rest on the way before the entry under it takes over.
    private static readonly DispatcherTimer Linger = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private static readonly List<UIElement> Frozen = new();
    private static ContextMenu? _menu;
    private static MenuItem? _header;
    private static bool _holding;
    // Where the last judged move started (screen pixels).
    private static Point? _anchor;

    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(ContextMenu), UIElement.PreviewMouseMoveEvent, new MouseEventHandler(OnMove), true);
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler(OnClosed), true);
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent, new RoutedEventHandler(OnSubmenuOpened), true);
        Linger.Tick += (_, _) =>
        {
            // Resting on the entry that opened it is fine: it's still the one open.
            if (_header?.IsMouseOver == true) return;
            Release();
        };
    }

    // The open submenu of this entry is being aimed at.
    internal static bool Keeps(MenuItem header) => _holding && ReferenceEquals(header, _header);

    // Just opened under the mouse: hold at once, the very next move may already cross the other entries.
    private static void OnSubmenuOpened(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.OriginalSource) || sender is not MenuItem { IsMouseOver: true } header) return;
        if (ItemsControl.ItemsControlFromItemContainer(header) is not ContextMenu menu || PresentationSource.FromVisual(menu) == null) return;
        if (!ReferenceEquals(menu, _menu)) Release();
        _menu = menu;
        _anchor = menu.PointToScreen(Mouse.GetPosition(menu));
        Hold(menu, header);
    }

    private static void OnMove(object sender, MouseEventArgs e)
    {
        if (sender is not ContextMenu menu || !menu.IsOpen || PresentationSource.FromVisual(menu) == null) return;
        if (!ReferenceEquals(menu, _menu))
        {
            Release();
            _menu = menu;
            _anchor = null;
        }
        var header = menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.IsSubmenuOpen);
        if (header == null || SubmenuBounds(header) is not Rect sub)
        {
            Release();
            _anchor = null;
            return;
        }
        var p = menu.PointToScreen(e.GetPosition(menu));
        if (sub.Contains(p))
        {
            // Arrived.
            Release();
            _anchor = p;
            return;
        }
        bool heading = false;
        if (_anchor is Point from && ReferenceEquals(header, _header))
        {
            // Too short a move to tell the direction: nothing changes.
            if ((p - from).Length < 3) return;
            heading = Heading(from, p, sub);
        }
        _anchor = p;
        // Still on the entry that opened it: hold the others already, the next move may cross them.
        if (heading || header.IsMouseOver) Hold(menu, header);
        else Release();
    }

    private static void OnClosed(object sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, _menu)) return;
        Release();
        _menu = null;
        _header = null;
        _anchor = null;
    }

    // The submenu's panel on screen.
    private static Rect? SubmenuBounds(MenuItem header)
    {
        if (header.Template?.FindName("PART_Popup", header) is not Popup { IsOpen: true, Child: FrameworkElement panel }) return null;
        if (PresentationSource.FromVisual(panel) == null || panel.ActualWidth <= 0) return null;
        return new Rect(panel.PointToScreen(new Point(0, 0)), panel.PointToScreen(new Point(panel.ActualWidth, panel.ActualHeight)));
    }

    // The move from→to points at the submenu's near edge (with some room above and below).
    private static bool Heading(Point from, Point to, Rect sub)
    {
        const double slack = 32;
        bool right = sub.Left + sub.Width / 2 > from.X;
        double edge = right ? sub.Left : sub.Right, dx = to.X - from.X;
        if (right ? dx <= 0 || to.X >= edge : dx >= 0 || to.X <= edge) return false;
        double y = to.Y + (to.Y - from.Y) * (edge - to.X) / dx;
        return y >= sub.Top - slack && y <= sub.Bottom + slack;
    }

    private static void Hold(ContextMenu menu, MenuItem header)
    {
        if (!ReferenceEquals(header, _header)) Release();
        _header = header;
        _holding = true;
        if (Frozen.Count == 0)
            foreach (var item in menu.Items.OfType<UIElement>())
                if (!ReferenceEquals(item, header) && item.IsHitTestVisible)
                {
                    item.IsHitTestVisible = false;
                    Frozen.Add(item);
                }
        Linger.Stop();
        Linger.Start();
    }

    // The entries work again; the one under the mouse, if any, takes over as usual (and closes the submenu).
    private static void Release()
    {
        Linger.Stop();
        _holding = false;
        if (Frozen.Count == 0) return;
        foreach (var item in Frozen) item.IsHitTestVisible = true;
        Frozen.Clear();
        Mouse.Synchronize();
    }
}
