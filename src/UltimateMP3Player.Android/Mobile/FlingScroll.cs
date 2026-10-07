using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.GestureRecognizers;
using Avalonia.Interactivity;

namespace UltimateMP3Player;

// The finger's scrolling (the ScrollViewer's template uses it instead of Avalonia's ScrollGestureRecognizer, same
// properties and events). Avalonia moves the content after the finger lifts with a 16 ms timer of low priority, not with the
// screen's frames: a fling stuttered (about 30 steps a second on the emulator, uneven ones on a 120 Hz phone) while dragging
// was smooth. Here every frame of the screen moves it, along Android's own curve (the distance and the duration Android gives
// a fling of that speed, slowing down to a stop). A finger that catches a moving list stops it without tapping what's under it.
public sealed class FlingScroll : GestureRecognizer
{
    public static readonly DirectProperty<FlingScroll, bool> CanHorizontallyScrollProperty =
        AvaloniaProperty.RegisterDirect<FlingScroll, bool>(nameof(CanHorizontallyScroll), o => o.CanHorizontallyScroll, (o, v) => o.CanHorizontallyScroll = v);
    public static readonly DirectProperty<FlingScroll, bool> CanVerticallyScrollProperty =
        AvaloniaProperty.RegisterDirect<FlingScroll, bool>(nameof(CanVerticallyScroll), o => o.CanVerticallyScroll, (o, v) => o.CanVerticallyScroll = v);
    public static readonly DirectProperty<FlingScroll, bool> IsScrollInertiaEnabledProperty =
        AvaloniaProperty.RegisterDirect<FlingScroll, bool>(nameof(IsScrollInertiaEnabled), o => o.IsScrollInertiaEnabled, (o, v) => o.IsScrollInertiaEnabled = v);

    private bool _canHorizontally, _canVertically, _inertia = true;
    public bool CanHorizontallyScroll { get => _canHorizontally; set => SetAndRaise(CanHorizontallyScrollProperty, ref _canHorizontally, value); }
    public bool CanVerticallyScroll { get => _canVertically; set => SetAndRaise(CanVerticallyScrollProperty, ref _canVertically, value); }
    public bool IsScrollInertiaEnabled { get => _inertia; set => SetAndRaise(IsScrollInertiaEnabledProperty, ref _inertia, value); }

    // How far the finger moves before it's scrolling and not a tap (Avalonia's: half the size of a tap).
    private double StartDistance => _startDistance ??= (TopLevel.GetTopLevel(Target as Visual)?.PlatformSettings?.GetTapSize(PointerType.Touch).Height ?? 10) / 2;
    private double? _startDistance;

    // Android's limits for a fling (dp per second).
    private const double MinFling = 50, MaxFling = 8000;

    // The press now going on stopped a moving list: lifting the finger isn't a tap (Touch, the rows' Tap).
    public static bool Caught { get; private set; }

    static FlingScroll()
    {
        InputElement.PointerPressedEvent.AddClassHandler<TopLevel>((_, _) => Caught = false, RoutingStrategies.Tunnel, true);
    }

    private IPointer? _tracking;
    private Visual? _root;
    private bool _scrolling;
    private int _gestureId;
    private Point _pressed, _last;
    private ulong _lastMove;
    private readonly Velocity _velocity = new();

    // The fling under way (its gesture id, 0 = none).
    private int _flingId;
    private Vector _flingDir;
    private double _flingDistance, _flingSeconds, _flingDone;
    private TimeSpan? _flingT0;
    private readonly Stopwatch _sinceRelease = new();

    protected override void PointerPressed(PointerPressedEventArgs e)
    {
        if (e.Pointer.Type is not (PointerType.Touch or PointerType.Pen)) return;
        bool wasFlinging = _flingId != 0;
        EndGesture();
        _tracking = e.Pointer;
        _gestureId = ScrollGestureEventArgs.GetNextFreeId();
        _root = TopLevel.GetTopLevel(Target as Visual);
        _pressed = _last = e.GetPosition(_root);
        _velocity.Reset();
        _velocity.Add(e.Timestamp, default);
        if (wasFlinging)
        {
            // Caught: the content stops where it is; the button or row under the finger doesn't get this press.
            Caught = true;
            Capture(e.Pointer);
        }
    }

    protected override void PointerMoved(PointerEventArgs e)
    {
        if (e.Pointer != _tracking) return;
        var p = e.GetPosition(_root);
        if (!_scrolling)
        {
            if ((CanHorizontallyScroll && Math.Abs(_last.X - p.X) > StartDistance) || (CanVerticallyScroll && Math.Abs(_last.Y - p.Y) > StartDistance))
            {
                _scrolling = true;
                // (from where the finger crossed the slop: no jump of its size)
                _last = new Point(_last.X - (_last.X >= p.X ? StartDistance : -StartDistance), _last.Y - (_last.Y >= p.Y ? StartDistance : -StartDistance));
                Capture(e.Pointer);
            }
        }
        if (!_scrolling) return;
        _velocity.Add(e.Timestamp, _pressed - p);
        _lastMove = e.Timestamp;
        Target!.RaiseEvent(new ScrollGestureEventArgs(_gestureId, _last - p));
        _last = p;
        e.Handled = true;
    }

    protected override void PointerCaptureLost(IPointer pointer)
    {
        if (pointer == _tracking) EndGesture();
    }

    protected override void PointerReleased(PointerReleasedEventArgs e)
    {
        if (e.Pointer != _tracking || !_scrolling) return;
        e.Handled = true;
        var v = _velocity.Estimate();
        if (!CanHorizontallyScroll) v = v.WithX(0);
        if (!CanVerticallyScroll) v = v.WithY(0);
        var speed = v.Length;
        // A finger that stopped before lifting: no fling.
        if (!IsScrollInertiaEnabled || speed < MinFling || e.Timestamp - _lastMove > 80 || TopLevel.GetTopLevel(Target as Visual) is not { } top)
        {
            EndGesture();
            return;
        }
        if (speed > MaxFling)
        {
            v *= MaxFling / speed;
            speed = MaxFling;
        }
        _tracking = null;
        _flingId = _gestureId;
        _flingDir = v / speed;
        (_flingDistance, _flingSeconds) = AndroidFling(speed);
        _flingDone = 0;
        _flingT0 = null;
        _sinceRelease.Restart();
        var id = _flingId;
        top.RequestAnimationFrame(t => Step(id, t));
    }

    private void Step(int id, TimeSpan now)
    {
        if (_flingId != id) return;
        // The fling started when the finger lifted (the first frame comes a little later).
        _flingT0 ??= now - _sinceRelease.Elapsed;
        var x = Math.Clamp((now - _flingT0.Value).TotalSeconds / _flingSeconds, 0, 1);
        var at = _flingDistance * (1 - Math.Pow(1 - x, Exponent));
        var step = at - _flingDone;
        _flingDone = at;
        var args = new ScrollGestureEventArgs(id, _flingDir * step);
        Target!.RaiseEvent(args);
        // At the end of its course, at the end of the list (or nobody scrolled): stop.
        if (x >= 1 || !args.Handled || args.ShouldEndScrollGesture || TopLevel.GetTopLevel(Target as Visual) is not { } top)
        {
            EndGesture();
            return;
        }
        top.RequestAnimationFrame(t => Step(id, t));
    }

    private void EndGesture()
    {
        _tracking = null;
        _flingId = 0;
        if (!_scrolling) return;
        _scrolling = false;
        var id = _gestureId;
        _gestureId = 0;
        _root = null;
        Target?.RaiseEvent(new ScrollGestureEndedEventArgs(id));
    }

    // ------------------------------------------------------------------ Android's fling (android.widget.OverScroller)

    private const double Inflexion = 0.35, Friction = 0.015;
    private static readonly double Deceleration = Math.Log(0.78) / Math.Log(0.9);
    // Gravity × inches per metre × 160 dots per inch (a dp) × 0.84, Android's "physical coefficient" in dp.
    private const double Physical = 9.80665 * 39.37 * 160 * 0.84;
    // The curve's shape: so that it starts at the finger's speed (distance × Exponent / duration = speed).
    private const double Exponent = 1 / Inflexion;

    private static (double Distance, double Seconds) AndroidFling(double speed)
    {
        var l = Math.Log(Inflexion * speed / (Friction * Physical));
        return (Friction * Physical * Math.Exp(Deceleration / (Deceleration - 1) * l), Math.Exp(l / (Deceleration - 1)));
    }

    // ------------------------------------------------------------------ the finger's speed

    // The speed at the last sample: a parabola through the last 100 ms of samples (least squares, like Android's and Flutter's
    // trackers), stopping at a pause of the finger.
    private sealed class Velocity
    {
        private const int Size = 20;
        private readonly (ulong Time, Vector Pos)[] _s = new (ulong, Vector)[Size];
        private int _count, _next;

        public void Reset() => _count = _next = 0;

        public void Add(ulong time, Vector pos)
        {
            _s[_next] = (time, pos);
            _next = (_next + 1) % Size;
            if (_count < Size) _count++;
        }

        public Vector Estimate()
        {
            if (_count < 2) return default;
            Span<double> t = stackalloc double[Size];
            Span<double> px = stackalloc double[Size];
            Span<double> py = stackalloc double[Size];
            int n = 0;
            var newest = _s[(_next - 1 + Size) % Size];
            ulong prev = newest.Time;
            for (int i = 0; i < _count; i++)
            {
                var s = _s[(_next - 1 - i + 2 * Size) % Size];
                double age = (double)(newest.Time - s.Time), gap = (double)(prev - s.Time);
                if (s.Time > newest.Time || age > 100 || gap > 40) break;
                prev = s.Time;
                t[n] = -age;
                px[n] = s.Pos.X;
                py[n] = s.Pos.Y;
                n++;
            }
            if (n < 2) return default;
            return new Vector(Slope(t[..n], px[..n]), Slope(t[..n], py[..n])) * 1000;
        }

        // The derivative at t = 0 of the best parabola (or line, with few or bunched samples), units per millisecond.
        private static double Slope(ReadOnlySpan<double> t, ReadOnlySpan<double> y)
        {
            double s0 = t.Length, s1 = 0, s2 = 0, s3 = 0, s4 = 0, y0 = 0, y1 = 0, y2 = 0;
            for (int i = 0; i < t.Length; i++)
            {
                double a = t[i], a2 = a * a;
                s1 += a; s2 += a2; s3 += a2 * a; s4 += a2 * a2;
                y0 += y[i]; y1 += a * y[i]; y2 += a2 * y[i];
            }
            if (t.Length >= 3)
            {
                // | s0 s1 s2 | |c0|   |y0|
                // | s1 s2 s3 | |c1| = |y1|   → c1 by Cramer's rule
                // | s2 s3 s4 | |c2|   |y2|
                double det = s0 * (s2 * s4 - s3 * s3) - s1 * (s1 * s4 - s3 * s2) + s2 * (s1 * s3 - s2 * s2);
                if (Math.Abs(det) > 1e-6)
                    return (s0 * (y1 * s4 - s3 * y2) - y0 * (s1 * s4 - s3 * s2) + s2 * (s1 * y2 - y1 * s2)) / det;
            }
            double d = s0 * s2 - s1 * s1;
            return Math.Abs(d) > 1e-9 ? (s0 * y1 - s1 * y0) / d : 0;
        }
    }
}
