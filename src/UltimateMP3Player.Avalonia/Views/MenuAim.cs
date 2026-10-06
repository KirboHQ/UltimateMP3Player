using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace UltimateMP3Player.Views;

// Right-click menus (the Windows app's MenuAim): while the mouse heads for an open submenu, nothing on the way closes
// it. The other entries stop being hit (so they don't take over); they come back as soon as the mouse goes elsewhere
// or rests on the way for a moment.
public static class MenuAim
{
    // How long the mouse may rest on the way before the entry under it takes over.
    private static readonly DispatcherTimer Linger = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private static readonly List<Control> Frozen = new();
    private static ContextMenu? _menu;
    private static MenuItem? _header;
    // Where the last judged move started (screen pixels).
    private static PixelPoint? _anchor;

    public static void Register()
    {
        InputElement.PointerMovedEvent.AddClassHandler<ContextMenu>(OnMove, RoutingStrategies.Tunnel, true);
        MenuItem.SubmenuOpenedEvent.AddClassHandler<MenuItem>(OnSubmenuOpened, RoutingStrategies.Bubble, true);
        Linger.Tick += (_, _) =>
        {
            // Resting on the entry that opened it is fine: it's still the one open.
            if (_header?.IsPointerOver == true) return;
            Release();
        };
    }

    // Just opened under the mouse: hold at once, the very next move may already cross the other entries.
    private static void OnSubmenuOpened(MenuItem header, RoutedEventArgs e)
    {
        if (!ReferenceEquals(header, e.Source) || !header.IsPointerOver) return;
        if (header.Parent is not ContextMenu menu) return;
        if (!ReferenceEquals(menu, _menu)) Release();
        _menu = menu;
        _anchor = null;
        Hold(menu, header);
    }

    private static void OnMove(ContextMenu menu, PointerEventArgs e)
    {
        if (!menu.IsOpen)
        {
            Release();
            return;
        }
        if (!ReferenceEquals(menu, _menu))
        {
            Release();
            _menu = menu;
            _anchor = null;
        }
        var header = menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.IsSubMenuOpen);
        if (header == null || SubmenuBounds(header) is not PixelRect sub)
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
        if (_anchor is PixelPoint from && ReferenceEquals(header, _header))
        {
            // Too short a move to tell the direction: nothing changes.
            double dx = p.X - from.X, dy = p.Y - from.Y;
            if (Math.Sqrt(dx * dx + dy * dy) < 3) return;
            heading = Heading(from, p, sub);
        }
        _anchor = p;
        // Still on the entry that opened it: hold the others already, the next move may cross them.
        if (heading || header.IsPointerOver) Hold(menu, header);
        else Release();
    }

    // The submenu's panel on screen.
    private static PixelRect? SubmenuBounds(MenuItem header)
    {
        var popup = header.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
        if (popup is not { IsOpen: true, Child: Control panel } || panel.Bounds.Width <= 0 || TopLevel.GetTopLevel(panel) == null) return null;
        var a = panel.PointToScreen(new Point(0, 0));
        var b = panel.PointToScreen(new Point(panel.Bounds.Width, panel.Bounds.Height));
        return new PixelRect(a, b);
    }

    // The move from→to points at the submenu's near edge (with some room above and below).
    private static bool Heading(PixelPoint from, PixelPoint to, PixelRect sub)
    {
        const double slack = 32;
        bool right = sub.X + sub.Width / 2.0 > from.X;
        double edge = right ? sub.X : sub.Right, dx = to.X - from.X;
        if (right ? dx <= 0 || to.X >= edge : dx >= 0 || to.X <= edge) return false;
        double y = to.Y + (to.Y - from.Y) * (edge - to.X) / dx;
        return y >= sub.Y - slack && y <= sub.Bottom + slack;
    }

    private static void Hold(ContextMenu menu, MenuItem header)
    {
        if (!ReferenceEquals(header, _header)) Release();
        _header = header;
        if (Frozen.Count == 0)
            foreach (var item in menu.Items.OfType<Control>())
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
        if (Frozen.Count == 0) return;
        foreach (var item in Frozen) item.IsHitTestVisible = true;
        Frozen.Clear();
    }
}
