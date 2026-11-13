using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace UltimateMP3Player.Views;

// Moving a row of a list with the finger, by its handle (an element with the "handle" class): a copy of the row follows
// the finger, the accent line between the rows says where it lands (a box over its own place where it would stay put), and
// the list scrolls near its top and bottom edges. The playlists' own reorder (SongsPage) looks the same; this one is for the
// other lists ("Next up", the search's sites). The list must not scroll under the finger: the gesture is taken away from
// the ScrollViewer (otherwise a long press on the handle would end in a scroll, the move lost).
public sealed class TouchReorder
{
    private readonly Control _host;
    private readonly Canvas _layer;
    private readonly Func<IReadOnlyList<Control>> _rows;
    private readonly Func<ScrollViewer?> _scroll;
    private readonly Action<int, int, bool> _drop;
    private Control? _dragged;
    private double _draggedOpacity = 1;
    private int _from, _on = -1;
    private bool _after, _stays;
    private double _grabY, _fingerY;
    private Border? _line;
    private Control? _ghost;
    private DispatcherTimer? _edge;
    // The mark where it is and where it's going (top, height); the clock of its glide.
    private double _top, _height, _toTop, _toHeight;
    private readonly System.Diagnostics.Stopwatch _clock = new();

    // host: has the rows inside and gets the finger first; layer: a Canvas over the rows, not hit-testable; rows: the rows in
    // their order; drop(from, onto, after): the dragged row's index, the row it was dropped on and on which side.
    public TouchReorder(Control host, Canvas layer, Func<IReadOnlyList<Control>> rows, Func<ScrollViewer?> scroll, Action<int, int, bool> drop)
    {
        _host = host;
        _layer = layer;
        _rows = rows;
        _scroll = scroll;
        _drop = drop;
        host.AddHandler(InputElement.PointerPressedEvent, Pressed, RoutingStrategies.Tunnel);
        host.AddHandler(InputElement.PointerMovedEvent, Moved, RoutingStrategies.Tunnel);
        host.AddHandler(InputElement.PointerReleasedEvent, Released, RoutingStrategies.Tunnel);
        host.PointerCaptureLost += (_, _) => End(false);
    }

    // Where a row dropped onto another one goes (the index for PlayQueue.Move / Profile.MoveTrack).
    public static int MoveIndex(int from, int target, bool after)
    {
        int to = after ? target + 1 : target;
        return from < to ? to - 1 : to;
    }

    private static bool OnHandle(Visual v)
    {
        for (var x = v; x != null; x = x.GetVisualParent())
            if (x is StyledElement { Classes: var c } && c.Contains("handle")) return true;
        return false;
    }

    private void Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (_dragged != null || e.Source is not Visual v || !OnHandle(v)) return;
        var rows = _rows();
        int index = -1;
        for (int i = 0; i < rows.Count && index < 0; i++)
            if (rows[i] == v || rows[i].IsVisualAncestorOf(v)) index = i;
        if (index < 0) return;
        var row = rows[index];
        _dragged = row;
        _from = index;
        _on = -1;
        _fingerY = e.GetPosition(_layer).Y;
        var at = row.TranslatePoint(default, _layer) ?? default;
        _grabY = _fingerY - at.Y;
        _line = new Border { IsVisible = false, IsHitTestVisible = false };
        _layer.Children.Add(_line);
        _ghost = Ghost(row);
        Canvas.SetLeft(_ghost, at.X);
        _layer.Children.Add(_ghost);
        _draggedOpacity = row.Opacity;
        row.Opacity = 0.35;
        e.Pointer.Capture(_host);
        e.PreventGestureRecognition();
        e.Handled = true;
        Haptics.LongPress();
        _edge ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            Glide();
            EdgeScroll();
        });
        _clock.Restart();
        _edge.Start();
        Follow();
    }

    private void Moved(object? sender, PointerEventArgs e)
    {
        if (_dragged == null) return;
        _fingerY = e.GetPosition(_layer).Y;
        e.PreventGestureRecognition();
        e.Handled = true;
        Follow();
    }

    private void Released(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragged == null) return;
        e.PreventGestureRecognition();
        e.Handled = true;
        End(true);
        e.Pointer.Capture(null);
    }

    // The copy under the finger; the line before or after the row nearest to it.
    private void Follow()
    {
        if (_ghost == null || _line == null || _dragged == null) return;
        Canvas.SetTop(_ghost, _fingerY - _grabY);
        var rows = _rows();
        int on = -1;
        double best = double.MaxValue, onTop = 0, onHeight = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            var c = rows[i];
            if (!c.IsEffectivelyVisible || c.TranslatePoint(default, _layer) is not { } at) continue;
            double top = at.Y, h = c.Bounds.Height;
            double away = _fingerY < top ? top - _fingerY : _fingerY > top + h ? _fingerY - top - h : 0;
            if (away >= best) continue;
            best = away;
            on = i;
            onTop = top;
            onHeight = h;
        }
        _on = on;
        if (on < 0)
        {
            _line.IsVisible = false;
            return;
        }
        _after = _fingerY > onTop + onHeight / 2;
        _stays = on == _from || on == _from - 1 && _after || on == _from + 1 && !_after;
        if (_stays && _dragged.TranslatePoint(default, _layer) is { } own) Mark(own.Y, _dragged.Bounds.Height);
        else Mark((_after ? onTop + onHeight : onTop) - 1.5, 3);
    }

    // The line (accent, between two rows) or the box (outlined, a light fill, over a row): the first time it's put in place at
    // once, then it glides there, its look following its height (the desktops' DragVisuals): a box turning into a line is
    // solid only once it's thin, not a block of accent for a moment.
    private void Mark(double top, double height)
    {
        if (_line == null) return;
        _toTop = top;
        _toHeight = height;
        if (!_line.IsVisible || !Ui.Animations)
        {
            _top = top;
            _height = height;
        }
        _line.IsVisible = true;
        Draw();
    }

    private void Glide()
    {
        double dt = Math.Min(0.1, _clock.Elapsed.TotalSeconds);
        _clock.Restart();
        if (_line is not { IsVisible: true } || (_top == _toTop && _height == _toHeight)) return;
        double k = 1 - Math.Exp(-dt / 0.04);
        _top += (_toTop - _top) * k;
        _height += (_toHeight - _height) * k;
        if (Math.Abs(_toTop - _top) < 0.3 && Math.Abs(_toHeight - _height) < 0.3) (_top, _height) = (_toTop, _toHeight);
        Draw();
    }

    // 0 = the line, 1 = the box: everything in between is the morph.
    private void Draw()
    {
        if (_line == null) return;
        var accent = Ui.Res("AccentBrush") is ISolidColorBrush s ? s.Color : Colors.White;
        double box = Math.Clamp((_height - 3) / 24, 0, 1), left = 16 - 8 * box;
        Canvas.SetLeft(_line, left);
        Canvas.SetTop(_line, _top);
        _line.Width = Math.Max(0, _layer.Bounds.Width - 2 * left);
        _line.Height = Math.Max(3, _height);
        _line.CornerRadius = new CornerRadius(2 + 10 * box);
        _line.BorderThickness = new Thickness(2 * box);
        _line.BorderBrush = new SolidColorBrush(accent, box);
        _line.Background = new SolidColorBrush(accent, 1 - 0.82 * box);
    }

    // Near the top or bottom edge of what's visible the list scrolls, faster the closer the finger is.
    private void EdgeScroll()
    {
        if (_dragged == null || _scroll() is not { } sv || _layer.TranslatePoint(new Point(0, _fingerY), sv) is not { } inView) return;
        const double edge = 72;
        double h = sv.Bounds.Height;
        double push = inView.Y < edge ? inView.Y - edge : inView.Y > h - edge ? inView.Y - (h - edge) : 0;
        if (push == 0) return;
        double y = Math.Clamp(sv.Offset.Y + push * 0.35, 0, Math.Max(0, sv.Extent.Height - sv.Viewport.Height));
        if (Math.Abs(y - sv.Offset.Y) < 0.1) return;
        double moved = y - sv.Offset.Y;
        sv.Offset = new Vector(sv.Offset.X, y);
        // (the layer may scroll with the rows: the finger stays where it is on the screen)
        if (_layer.IsVisualAncestorOf(sv) || sv.IsVisualAncestorOf(_layer)) _fingerY += moved;
        Follow();
    }

    private void End(bool drop)
    {
        if (_dragged is not { } row) return;
        _dragged = null;
        _edge?.Stop();
        row.Opacity = _draggedOpacity;
        _layer.Children.Clear();
        _ghost = _line = null;
        if (drop && _on >= 0 && !_stays) _drop(_from, _on, _after);
    }

    // A picture of the row, lifted a little off the list.
    private static Control Ghost(Control row)
    {
        var size = row.Bounds.Size;
        double scale = TopLevel.GetTopLevel(row)?.RenderScaling ?? 1;
        var bmp = new RenderTargetBitmap(new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scale)), Math.Max(1, (int)Math.Ceiling(size.Height * scale))),
            new Vector(96 * scale, 96 * scale));
        bmp.Render(row);
        // (the shadow on the outer border, the rounded clip on the inner one: a clip would cut the shadow away)
        return new Border
        {
            Width = size.Width, Height = size.Height, CornerRadius = new CornerRadius(12), IsHitTestVisible = false, Opacity = 0.92,
            BoxShadow = BoxShadows.Parse("0 6 20 0 #80000000"),
            // (Surface3 on the page's solid colour: with the glass look it's see-through, the rows under the copy showed through it)
            Child = new Border
            {
                CornerRadius = new CornerRadius(12), ClipToBounds = true, Background = Ui.Res("PageSolidBrush"),
                Child = new Border
                {
                    Background = Ui.Res("Surface3Brush"),
                    Child = new CoverImage { Source = bmp, Width = size.Width, Height = size.Height },
                },
            },
        };
    }
}
