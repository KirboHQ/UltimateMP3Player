using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace UltimateMP3Player.Views;

public enum DragOutcome { None, Placed, Trashed }

// A reorder drag: the row lands where the line is, wherever the mouse is released.
public sealed class DragSession
{
    public DragSession(ListBox list, object source, FrameworkElement? trash = null)
    {
        List = list;
        Source = source;
        Trash = trash;
    }

    public ListBox List { get; }
    // The dragged item: dropping next to it means "stay here".
    public object Source { get; }
    public FrameworkElement? Trash { get; }
    public object? Target { get; internal set; }
    public bool After { get; internal set; }
    public bool OverTrash { get; internal set; }
    public bool Canceled { get; internal set; }
    // Mouse over the trash zone or not.
    public event Action<bool>? TrashHot;

    internal void SetOverTrash(bool over)
    {
        if (over == OverTrash) return;
        OverTrash = over;
        TrashHot?.Invoke(over);
    }
}

// Drag feedback: a floating copy of the row and an insertion line.
public static class DragVisuals
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    private static InsertionAdorner? _line;

    public static DragOutcome Run(FrameworkElement row, object data, DragDropEffects allowed, DragSession? session = null)
    {
        var root = Window.GetWindow(row)?.Content as FrameworkElement;
        var layer = root != null ? AdornerLayer.GetAdornerLayer(root) : null;
        if (root == null || layer == null)
        {
            DragDrop.DoDragDrop(row, data, allowed);
            return DragOutcome.None;
        }

        var ghost = new GhostAdorner(root, Snapshot(row), System.Windows.Input.Mouse.GetPosition(row));
        ghost.MoveTo(System.Windows.Input.Mouse.GetPosition(root));
        layer.Add(ghost);
        var last = TimeSpan.Zero;
        void Follow()
        {
            if (!GetCursorPos(out var p)) return;
            var screen = new Point(p.X, p.Y);
            ghost.MoveTo(root.PointFromScreen(screen));
            if (session != null) Track(session, screen);
        }
        // Every frame: the ghost follows, the list scrolls near its edges.
        EventHandler frame = (_, e) =>
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == last) return;
            last = now;
            Follow();
        };
        GiveFeedbackEventHandler feedback = (_, e) =>
        {
            Follow();
            e.UseDefaultCursors = true;
        };
        QueryContinueDragEventHandler escape = (_, e) => { if (e.EscapePressed && session != null) session.Canceled = true; };
        CompositionTarget.Rendering += frame;
        row.GiveFeedback += feedback;
        row.QueryContinueDrag += escape;
        var opacity = row.Opacity;
        row.Opacity = 0.35;
        try { DragDrop.DoDragDrop(row, data, allowed); }
        finally
        {
            CompositionTarget.Rendering -= frame;
            row.GiveFeedback -= feedback;
            row.QueryContinueDrag -= escape;
            row.Opacity = opacity;
            layer.Remove(ghost);
            HideLine();
        }
        if (session == null || session.Canceled) return DragOutcome.None;
        if (session.OverTrash) return DragOutcome.Trashed;
        return session.Target != null ? DragOutcome.Placed : DragOutcome.None;
    }

    // Line under the mouse (clamped to the list), trash hover and edge scrolling.
    private static void Track(DragSession s, Point screen)
    {
        var list = s.List;
        if (!list.IsVisible) return;
        if (s.Trash is { IsVisible: true } trash)
        {
            // Round target, a bit larger than it looks.
            var t = trash.PointFromScreen(screen);
            double rx = trash.ActualWidth / 2, ry = trash.ActualHeight / 2;
            double dx = (t.X - rx) / (rx + 16), dy = (t.Y - ry) / (ry + 16);
            s.SetOverTrash(dx * dx + dy * dy <= 1);
        }
        var p = list.PointFromScreen(screen);
        const double edge = 56;
        if (FindScrollViewer(list) is { } sv && !s.OverTrash)
        {
            double speed = 0;
            if (p.Y < edge) speed = -Math.Min(1, (edge - p.Y) / edge) * 22;
            else if (p.Y > list.ActualHeight - edge) speed = Math.Min(1, (p.Y - (list.ActualHeight - edge)) / edge) * 22;
            if (speed != 0) sv.ScrollToVerticalOffset(Math.Clamp(sv.VerticalOffset + speed, 0, sv.ScrollableHeight));
        }
        if (s.OverTrash)
        {
            HideLine();
            return;
        }
        double y = Math.Clamp(p.Y, 2, Math.Max(2, list.ActualHeight - 2));
        if (Nearest(list, y) is not { } item) return;
        var row = RowOf(item);
        s.After = list.TranslatePoint(new Point(0, y), row).Y > row.ActualHeight / 2;
        s.Target = item.DataContext;
        // Just above or below itself: the line becomes the slot the song already has.
        int si = list.Items.IndexOf(s.Source), ti = list.Items.IndexOf(s.Target);
        bool stays = si >= 0 && (ti == si || (ti == si - 1 && s.After) || (ti == si + 1 && !s.After));
        if (stays && list.ItemContainerGenerator.ContainerFromIndex(si) is ListBoxItem own) ShowSlot(list, RowOf(own));
        else ShowLine(list, row, s.After);
    }

    // The visible row at y, or the closest one (gaps, space below the last row).
    private static ListBoxItem? Nearest(ListBox list, double y)
    {
        ListBoxItem? best = null;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item || !item.IsVisible) continue;
            var row = RowOf(item);
            double top = row.TranslatePoint(new Point(0, 0), list).Y, bottom = top + row.ActualHeight;
            if (bottom < -200 || top > list.ActualHeight + 200) continue;
            double distance = y < top ? top - y : y > bottom ? y - bottom : 0;
            if (distance >= bestDistance) continue;
            best = item;
            bestDistance = distance;
            if (distance == 0) break;
        }
        return best;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is ScrollViewer sv) return sv;
            if (FindScrollViewer(c) is { } found) return found;
        }
        return null;
    }

    private static ImageSource Snapshot(FrameworkElement e)
    {
        var dpi = VisualTreeHelper.GetDpi(e);
        int w = Math.Max(1, (int)Math.Ceiling(e.ActualWidth * dpi.DpiScaleX)), h = Math.Max(1, (int)Math.Ceiling(e.ActualHeight * dpi.DpiScaleY));
        var bmp = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRoundedRectangle((Brush)Application.Current.Resources["Surface3Brush"], null, new Rect(0, 0, e.ActualWidth, e.ActualHeight), 8, 8);
            var box = new Rect(0, 0, e.ActualWidth, e.ActualHeight);
            // The brush keeps the row's offset in its parent (e.g. below the "Up next from" title): start the view there.
            var view = new Rect((Point)VisualTreeHelper.GetOffset(e), box.Size);
            dc.DrawRectangle(new VisualBrush(e) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = view }, null, box);
        }
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    // Where a drop lands: before or after the row under the mouse (section titles don't count).
    public static (object? Target, bool After) Hit(ListBox list, DragEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        var item = d != null ? Ui.FindAncestor<ListBoxItem>(d) : null;
        if (item == null || ItemsControl.ItemsControlFromItemContainer(item) != list) return (null, true);
        var row = RowOf(item);
        bool after = e.GetPosition(row).Y > row.ActualHeight / 2;
        ShowLine(list, row, after);
        return (item.DataContext, after);
    }

    // The element named "Row" in the item template, or the whole item.
    private static FrameworkElement RowOf(ListBoxItem item)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(item);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is FrameworkElement { Name: "Row" } fe) return fe;
            for (int i = VisualTreeHelper.GetChildrenCount(d) - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
        return item;
    }

    private static void ShowLine(ListBox list, FrameworkElement row, bool after)
    {
        double y = Math.Clamp(row.TranslatePoint(new Point(0, after ? row.ActualHeight : 0), list).Y, 0, list.ActualHeight);
        Show(list, y, y);
    }

    // A box over the row's own place.
    private static void ShowSlot(ListBox list, FrameworkElement row)
    {
        double top = row.TranslatePoint(new Point(0, 0), list).Y;
        Show(list, Math.Clamp(top, 0, list.ActualHeight), Math.Clamp(top + row.ActualHeight, 0, list.ActualHeight));
    }

    private static void Show(ListBox list, double top, double bottom)
    {
        if (_line == null || _line.AdornedElement != list)
        {
            HideLine();
            if (AdornerLayer.GetAdornerLayer(list) is not { } layer) return;
            _line = new InsertionAdorner(list, top, bottom);
            layer.Add(_line);
        }
        else _line.GoTo(top, bottom);
    }

    public static void HideLine()
    {
        if (_line == null) return;
        _line.Stop();
        AdornerLayer.GetAdornerLayer(_line.AdornedElement)?.Remove(_line);
        _line = null;
    }

    // Index for PlayQueue.Move / Profile.MoveTrack.
    public static int MoveIndex(int from, int target, bool after)
    {
        int to = after ? target + 1 : target;
        return from < to ? to - 1 : to;
    }

    private sealed class GhostAdorner : Adorner
    {
        private readonly ImageSource _image;
        private readonly TranslateTransform _at = new();
        private readonly ScaleTransform _scale = new(1, 1);
        // Pushed while drawing: the adorner layer overwrites RenderTransform on every arrange.
        private readonly TransformGroup _place = new();

        public GhostAdorner(UIElement root, ImageSource image, Point grab) : base(root)
        {
            _image = image;
            IsHitTestVisible = false;
            Opacity = 0.92;
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 6, Opacity = 0.55, Color = Colors.Black };
            _place.Children.Add(new TranslateTransform(-grab.X, -grab.Y));
            _place.Children.Add(_scale);
            _place.Children.Add(new RotateTransform(-2));
            _place.Children.Add(_at);
            if (Ui.Animations)
            {
                var pop = new DoubleAnimation(0.9, 1.03, TimeSpan.FromMilliseconds(160)) { EasingFunction = new BackEase { Amplitude = 0.4 } };
                _scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                _scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            }
        }

        public void MoveTo(Point p)
        {
            _at.X = p.X;
            _at.Y = p.Y;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var w = ((BitmapSource)_image).PixelWidth / VisualTreeHelper.GetDpi(this).DpiScaleX;
            var h = ((BitmapSource)_image).PixelHeight / VisualTreeHelper.GetDpi(this).DpiScaleY;
            dc.PushTransform(_place);
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), 8, 8));
            dc.DrawImage(_image, new Rect(0, 0, w, h));
            dc.Pop();
            dc.Pop();
        }
    }

    // A line between rows, or a box over a row; glides and morphs frame by frame.
    private sealed class InsertionAdorner : Adorner
    {
        private double _top, _bottom, _toTop, _toBottom;
        private bool _ticking;
        private TimeSpan _last;

        public InsertionAdorner(UIElement list, double top, double bottom) : base(list)
        {
            IsHitTestVisible = false;
            _top = _toTop = top;
            _bottom = _toBottom = bottom;
            if (Ui.Animations) BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100)));
        }

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
            CompositionTarget.Rendering += OnFrame;
        }

        public void Stop()
        {
            if (!_ticking) return;
            _ticking = false;
            CompositionTarget.Rendering -= OnFrame;
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == _last) return;
            double dt = _last == TimeSpan.Zero ? 1 / 60.0 : Math.Min(0.1, (now - _last).TotalSeconds);
            _last = now;
            double k = 1 - Math.Exp(-dt / 0.04);
            _top += (_toTop - _top) * k;
            _bottom += (_toBottom - _bottom) * k;
            if (Math.Abs(_toTop - _top) < 0.3 && Math.Abs(_toBottom - _bottom) < 0.3)
            {
                _top = _toTop;
                _bottom = _toBottom;
                Stop();
            }
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var accent = ((SolidColorBrush)Application.Current.Resources["AccentBrush"]).Color;
            double w = ((FrameworkElement)AdornedElement).ActualWidth;
            // 0 = line, 1 = box: everything in between is the morph.
            double box = Math.Clamp((_bottom - _top - 3) / 24, 0, 1);
            double height = Math.Max(3, _bottom - _top), mid = (_top + _bottom) / 2;
            double left = 10 - 6 * box, right = w - 14 + 6 * box, radius = 1.5 + 8.5 * box;
            var rect = new Rect(left, mid - height / 2, Math.Max(0, right - left), height);
            var fill = new SolidColorBrush(Color.FromArgb((byte)(255 - 210 * box), accent.R, accent.G, accent.B));
            dc.DrawRoundedRectangle(fill, box > 0 ? new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * box), accent.R, accent.G, accent.B)), 2) : null,
                rect, radius, radius);
            if (box < 1)
                dc.DrawEllipse(Brushes.Transparent, new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * (1 - box)), accent.R, accent.G, accent.B)), 2.5),
                    new Point(8, mid), 4.5, 4.5);
        }
    }
}
