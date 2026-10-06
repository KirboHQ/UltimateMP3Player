using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace UltimateMP3Player.Views;

public enum DragOutcome { None, Placed, Trashed }

// A reorder drag: the row lands where the line is.
public sealed class DragSession
{
    public DragSession(ListBox list, object source, Control? trash = null)
    {
        List = list;
        Source = source;
        Trash = trash;
    }

    public ListBox List { get; }
    // The dragged item: dropping next to it means "stay here".
    public object Source { get; }
    public Control? Trash { get; }
    public object? Target { get; internal set; }
    public bool After { get; internal set; }
    public bool OverTrash { get; internal set; }
    public bool Dropped { get; internal set; }
    // Mouse over the trash zone or not.
    public event Action<bool>? TrashHot;

    internal void SetOverTrash(bool over)
    {
        if (over == OverTrash) return;
        OverTrash = over;
        TrashHot?.Invoke(over);
    }
}

// Drag feedback (the Windows app's DragVisuals): a floating copy of the row and an insertion line.
public static class DragVisuals
{
    private static InsertionLine? _line;

    // Drags a row with its floating copy; a reorder session also gets the line and the trash zone.
    public static async Task<DragOutcome> RunAsync(Control row, PointerEventArgs start, IDataObject data, DragDropEffects allowed, DragSession? session = null)
    {
        var top = TopLevel.GetTopLevel(row);
        var root = top?.GetVisualChildren().OfType<Control>().FirstOrDefault() ?? top;
        var layer = root != null ? AdornerLayer.GetAdornerLayer(root) : null;
        if (top == null || root == null || layer == null)
        {
            try { await DragDrop.DoDragDrop(start, data, allowed); } catch { }
            return DragOutcome.None;
        }

        var ghost = new Ghost(Snapshot(row, top.RenderScaling), row.Bounds.Size, start.GetPosition(row));
        AdornerLayer.SetAdornedElement(ghost, root);
        layer.Children.Add(ghost);
        ghost.MoveTo(start.GetPosition(root));
        Point? last = null;
        void Follow(Point inRoot)
        {
            last = inRoot;
            ghost.MoveTo(inRoot);
            if (session != null && root.TranslatePoint(inRoot, session.List) is { } inList) Track(session, root, inRoot, inList);
        }
        EventHandler<DragEventArgs> over = (_, e) => Follow(e.GetPosition(root));
        EventHandler<DragEventArgs> drop = (_, e) =>
        {
            Follow(e.GetPosition(root));
            if (session != null) session.Dropped = true;
        };
        top.AddHandler(DragDrop.DragOverEvent, over, RoutingStrategies.Tunnel, true);
        top.AddHandler(DragDrop.DragEnterEvent, over, RoutingStrategies.Tunnel, true);
        top.AddHandler(DragDrop.DropEvent, drop, RoutingStrategies.Tunnel, true);
        // The list scrolls near its edges even while the mouse stands still.
        var scroll = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            if (last is { } p) Follow(p);
        });
        if (session != null) scroll.Start();
        var opacity = row.Opacity;
        row.Opacity = 0.35;
        try { await DragDrop.DoDragDrop(start, data, allowed); }
        catch { }
        finally
        {
            scroll.Stop();
            top.RemoveHandler(DragDrop.DragOverEvent, over);
            top.RemoveHandler(DragDrop.DragEnterEvent, over);
            top.RemoveHandler(DragDrop.DropEvent, drop);
            row.Opacity = opacity;
            layer.Children.Remove(ghost);
            HideLine();
        }
        if (session == null || !session.Dropped) return DragOutcome.None;
        if (session.OverTrash) return DragOutcome.Trashed;
        return session.Target != null ? DragOutcome.Placed : DragOutcome.None;
    }

    // Line under the mouse (clamped to the list), trash hover and edge scrolling.
    private static void Track(DragSession s, Control root, Point inRoot, Point p)
    {
        var list = s.List;
        if (!list.IsEffectivelyVisible) return;
        if (s.Trash is { IsEffectivelyVisible: true } trash && root.TranslatePoint(inRoot, trash) is { } t)
        {
            // Round target, a bit larger than it looks.
            double rx = trash.Bounds.Width / 2, ry = trash.Bounds.Height / 2;
            double dx = (t.X - rx) / (rx + 16), dy = (t.Y - ry) / (ry + 16);
            s.SetOverTrash(dx * dx + dy * dy <= 1);
        }
        const double edge = 56;
        double h = list.Bounds.Height;
        if (list.FindDescendantOfType<ScrollViewer>() is { } sv && !s.OverTrash)
        {
            double speed = 0;
            if (p.Y < edge) speed = -Math.Min(1, (edge - p.Y) / edge) * 22;
            else if (p.Y > h - edge) speed = Math.Min(1, (p.Y - (h - edge)) / edge) * 22;
            double max = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
            if (speed != 0) sv.Offset = new Vector(sv.Offset.X, Math.Clamp(sv.Offset.Y + speed, 0, max));
        }
        if (s.OverTrash)
        {
            HideLine();
            return;
        }
        double y = Math.Clamp(p.Y, 2, Math.Max(2, h - 2));
        if (Nearest(list, y) is not { } item) return;
        var row = RowOf(item);
        s.After = (list.TranslatePoint(new Point(0, y), row)?.Y ?? 0) > row.Bounds.Height / 2;
        s.Target = item.DataContext;
        // Just above or below itself: the line becomes the slot the song already has.
        var items = list.Items;
        int si = items.IndexOf(s.Source), ti = items.IndexOf(s.Target);
        bool stays = si >= 0 && (ti == si || (ti == si - 1 && s.After) || (ti == si + 1 && !s.After));
        if (stays && list.ContainerFromIndex(si) is ListBoxItem own) ShowSlot(list, RowOf(own));
        else ShowLine(list, row, s.After);
    }

    // The visible row at y, or the closest one (gaps, space below the last row).
    private static ListBoxItem? Nearest(ListBox list, double y)
    {
        ListBoxItem? best = null;
        double bestDistance = double.MaxValue;
        foreach (var c in list.GetRealizedContainers())
        {
            if (c is not ListBoxItem item || !item.IsVisible) continue;
            var row = RowOf(item);
            if (row.TranslatePoint(new Point(0, 0), list) is not { } at) continue;
            double top = at.Y, bottom = top + row.Bounds.Height;
            if (bottom < -200 || top > list.Bounds.Height + 200) continue;
            double distance = y < top ? top - y : y > bottom ? y - bottom : 0;
            if (distance >= bestDistance) continue;
            best = item;
            bestDistance = distance;
            if (distance == 0) break;
        }
        return best;
    }

    private static Bitmap? Snapshot(Control c, double scale)
    {
        try
        {
            var size = c.Bounds.Size;
            var bmp = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scale)), Math.Max(1, (int)Math.Ceiling(size.Height * scale))),
                new Vector(96 * scale, 96 * scale));
            bmp.Render(c);
            return bmp;
        }
        catch { return null; }
    }

    // Where a drop lands: before or after the row under the mouse (section titles don't count).
    public static (object? Target, bool After) Hit(ListBox list, DragEventArgs e)
    {
        var item = Ui.FindAncestor<ListBoxItem>(e.Source as Visual);
        if (item == null || list.IndexFromContainer(item) < 0) return (null, true);
        var row = RowOf(item);
        bool after = e.GetPosition(row).Y > row.Bounds.Height / 2;
        ShowLine(list, row, after);
        return (item.DataContext, after);
    }

    // The element named "Row" (or with the class "row") in the item template, or the whole item.
    private static Control RowOf(ListBoxItem item)
    {
        foreach (var v in item.GetVisualDescendants())
            if (v is Control c && (c.Name == "Row" || c.Classes.Contains("row"))) return c;
        return item;
    }

    private static void ShowLine(ListBox list, Control row, bool after)
    {
        double y = Math.Clamp(row.TranslatePoint(new Point(0, after ? row.Bounds.Height : 0), list)?.Y ?? 0, 0, list.Bounds.Height);
        Show(list, y, y);
    }

    // A box over the row's own place.
    private static void ShowSlot(ListBox list, Control row)
    {
        double top = row.TranslatePoint(new Point(0, 0), list)?.Y ?? 0;
        Show(list, Math.Clamp(top, 0, list.Bounds.Height), Math.Clamp(top + row.Bounds.Height, 0, list.Bounds.Height));
    }

    private static void Show(ListBox list, double top, double bottom)
    {
        if (_line == null || _line.List != list)
        {
            HideLine();
            if (AdornerLayer.GetAdornerLayer(list) is not { } layer) return;
            _line = new InsertionLine(list, top, bottom);
            AdornerLayer.SetAdornedElement(_line, list);
            layer.Children.Add(_line);
        }
        else _line.GoTo(top, bottom);
    }

    public static void HideLine()
    {
        if (_line == null) return;
        _line.Stop();
        (_line.Parent as Panel)?.Children.Remove(_line);
        _line = null;
    }

    // Index for PlayQueue.Move / Profile.MoveTrack.
    public static int MoveIndex(int from, int target, bool after)
    {
        int to = after ? target + 1 : target;
        return from < to ? to - 1 : to;
    }

    // The floating copy: tilted a little, with a shadow, popping up as it's picked.
    private sealed class Ghost : Canvas
    {
        private readonly Border _card;
        private readonly Point _grab;

        public Ghost(Bitmap? image, Size size, Point grab)
        {
            _grab = grab;
            IsHitTestVisible = false;
            ClipToBounds = false;
            _card = new Border
            {
                Width = size.Width,
                Height = size.Height,
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
                Background = Ui.Res("Surface3Brush"),
                Child = image == null ? null : new Image { Source = image, Width = size.Width, Height = size.Height },
                Opacity = 0.92,
                BoxShadow = BoxShadows.Parse("0 6 22 0 #8C000000"),
                RenderTransformOrigin = new RelativePoint(grab.X, grab.Y, RelativeUnit.Absolute),
            };
            var scale = new ScaleTransform(1, 1);
            _card.RenderTransform = new TransformGroup { Children = { scale, new RotateTransform(-2) } };
            Children.Add(_card);
            if (Ui.Animations)
            {
                // 0.9 → 1.03 with a little overshoot, like the Windows app.
                var ease = new BackEaseOut();
                var started = DateTime.UtcNow;
                DispatcherTimer.Run(() =>
                {
                    double t = Math.Min(1, (DateTime.UtcNow - started).TotalMilliseconds / 160);
                    double s = 0.9 + (1.03 - 0.9) * ease.Ease(t);
                    scale.ScaleX = scale.ScaleY = s;
                    return t < 1;
                }, TimeSpan.FromMilliseconds(15));
            }
        }

        public void MoveTo(Point p)
        {
            SetLeft(_card, p.X - _grab.X);
            SetTop(_card, p.Y - _grab.Y);
        }
    }

    // A line between rows, or a box over a row; glides and morphs frame by frame.
    private sealed class InsertionLine : Control
    {
        private double _top, _bottom, _toTop, _toBottom;
        private bool _ticking;
        private TimeSpan _last;

        public InsertionLine(ListBox list, double top, double bottom)
        {
            List = list;
            IsHitTestVisible = false;
            _top = _toTop = top;
            _bottom = _toBottom = bottom;
            if (Ui.Animations)
            {
                Opacity = 0;
                Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(100) } };
                Dispatcher.UIThread.Post(() => Opacity = 1, DispatcherPriority.Render);
            }
        }

        public ListBox List { get; }

        public void GoTo(double top, double bottom)
        {
            _toTop = top;
            _toBottom = bottom;
            if (!Ui.Animations)
            {
                _top = top;
                _bottom = bottom;
                InvalidateVisual();
                return;
            }
            if (_ticking) return;
            _ticking = true;
            _last = TimeSpan.Zero;
            Request();
        }

        private void Request() => TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);

        public void Stop() => _ticking = false;

        private void OnFrame(TimeSpan now)
        {
            if (!_ticking) return;
            double dt = _last == TimeSpan.Zero ? 1 / 60.0 : Math.Min(0.1, (now - _last).TotalSeconds);
            _last = now;
            double k = 1 - Math.Exp(-dt / 0.04);
            _top += (_toTop - _top) * k;
            _bottom += (_toBottom - _bottom) * k;
            if (Math.Abs(_toTop - _top) < 0.3 && Math.Abs(_toBottom - _bottom) < 0.3)
            {
                _top = _toTop;
                _bottom = _toBottom;
                _ticking = false;
            }
            InvalidateVisual();
            if (_ticking) Request();
        }

        public override void Render(DrawingContext dc)
        {
            var accent = (Ui.Res("AccentBrush") as ISolidColorBrush)?.Color ?? Colors.MediumPurple;
            double w = Bounds.Width;
            // 0 = line, 1 = box: everything in between is the morph.
            double box = Math.Clamp((_bottom - _top - 3) / 24, 0, 1);
            double height = Math.Max(3, _bottom - _top), mid = (_top + _bottom) / 2;
            double left = 10 - 6 * box, right = w - 14 + 6 * box, radius = 1.5 + 8.5 * box;
            var rect = new Rect(left, mid - height / 2, Math.Max(0, right - left), height);
            var fill = new SolidColorBrush(Color.FromArgb((byte)(255 - 210 * box), accent.R, accent.G, accent.B));
            dc.DrawRectangle(fill, box > 0 ? new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * box), accent.R, accent.G, accent.B)), 2) : null,
                rect, radius, radius);
            if (box < 1)
                dc.DrawEllipse(Brushes.Transparent, new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * (1 - box)), accent.R, accent.G, accent.B)), 2.5),
                    new Point(8, mid), 4.5, 4.5);
        }
    }
}
