using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace UltimateMP3Player.Services;

// Wheel scrolling like Firefox's: every notch starts a short curve to the new destination that begins at the
// speed the list is already moving (Firefox's "Bezier" smooth scrolling, same timings), so a run of notches is
// one continuous glide instead of a burst and a slow-down per notch. Quick notches shorten the curve, a single
// one takes the longest. Works in every window, menu and drop-down of the app.
public static class SmoothScroll
{
    public static bool Enabled { get; set; } = true;

    // Firefox defaults: general.smoothScroll.mouseWheel.duration{Min,Max}MS, durationToIntervalRatio,
    // currentVelocityWeighting, stopDecelerationWeighting.
    private const double MinMs = 50, MaxMs = 200, IntervalRatio = 2.0;
    private const double VelocityWeighting = 0.25, StopDeceleration = 0.4;

    private sealed class Anim
    {
        public double Start, Destination;
        // Timeline in ms (render clock); NaN until the first frame.
        public double StartTime = double.NaN, Duration;
        public Bezier Timing = new(0, 0, 1 - StopDeceleration, 1);
        public readonly double[] Prev = new double[3];
        // Offset last given to the list, to notice when something else moves it.
        public double Set;
    }

    private static readonly Dictionary<ScrollViewer, Anim> Active = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    // Render time minus stopwatch time: wheel events are placed on the same timeline as the frames.
    private static double _renderLead;
    private static bool _hooked, _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        // Every window (dialogs too) and the menus, which live in their own popups.
        foreach (var type in new[] { typeof(Window), typeof(ContextMenu), typeof(Popup) })
            EventManager.RegisterClassHandler(type, UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWheel));
    }

    // Kept for the main window (registration is global anyway).
    public static void Attach(Window w) => Register();

    private static double Now => Clock.Elapsed.TotalMilliseconds + _renderLead;

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Enabled || e.Handled || Keyboard.Modifiers != ModifierKeys.None) return;
        var sv = Target(e.OriginalSource as DependencyObject, e.Delta);
        if (sv == null) return;
        e.Handled = true;

        double step = 100 * Math.Max(1, SystemParameters.WheelScrollLines) / 3.0;
        bool running = Active.TryGetValue(sv, out var a);
        if (!running)
        {
            a = new Anim { Start = sv.VerticalOffset, Destination = sv.VerticalOffset, Set = sv.VerticalOffset };
            Active[sv] = a;
        }
        else Follow(sv, a!);
        double dest = Math.Clamp(a!.Destination - e.Delta / 120.0 * step, 0, sv.ScrollableHeight);
        // Against the end: a notch that changes nothing doesn't restart (and slow down) the curve.
        if (running && Math.Abs(dest - a.Destination) < 0.5) return;
        Update(a, dest, running && !double.IsNaN(a.StartTime) ? Now : double.NaN);
        Hook();
    }

    // A new destination: the curve restarts from where the list is, at the speed it already has.
    private static void Update(Anim a, double destination, double now)
    {
        if (double.IsNaN(now))
        {
            // First notch: the imaginary previous notches are as far apart as they can matter (longest curve).
            a.Destination = destination;
            a.Duration = MaxMs;
            a.Timing = Curve(0, a.Start, destination, a.Duration);
            a.StartTime = double.NaN;
            return;
        }
        double velocity = VelocityAt(a, now);
        a.Start = PositionAt(a, now);
        double duration = Duration(a, now);
        a.StartTime = now;
        a.Duration = duration;
        a.Destination = destination;
        a.Timing = Curve(velocity, a.Start, destination, duration);
    }

    // Notches in quick succession → shorter curves (average of the last three intervals).
    private static double Duration(Anim a, double now)
    {
        double interval = (now - a.Prev[2]) / 3;
        a.Prev[2] = a.Prev[1];
        a.Prev[1] = a.Prev[0];
        a.Prev[0] = now;
        return Math.Clamp(interval * IntervalRatio, MinMs, MaxMs);
    }

    private static void Seed(Anim a, double now)
    {
        double max = MaxMs / IntervalRatio;
        a.Prev[0] = now - max;
        a.Prev[1] = a.Prev[0] - max;
        a.Prev[2] = a.Prev[1] - max;
    }

    // Timing curve whose start slope matches the current speed (weighted, as Firefox does).
    private static Bezier Curve(double velocity, double from, double to, double durationMs)
    {
        if (Math.Abs(to - from) < 0.01 || velocity == 0) return new Bezier(0, 0, 1 - StopDeceleration, 1);
        double slope = velocity * (durationMs / 1000) / (to - from);
        double norm = Math.Sqrt(1 + slope * slope);
        double dt = 1 / norm * VelocityWeighting, dxy = slope / norm * VelocityWeighting;
        return new Bezier(dt, dxy, 1 - StopDeceleration, 1);
    }

    private static double Progress(Anim a, double now)
        => double.IsNaN(a.StartTime) || a.Duration <= 0 ? 0 : Math.Clamp((now - a.StartTime) / a.Duration, 0, 1);

    private static double PositionAt(Anim a, double now)
    {
        double p = a.Timing.Value(Progress(a, now));
        return a.Start + (a.Destination - a.Start) * p;
    }

    // Pixels per second.
    private static double VelocityAt(Anim a, double now)
    {
        double t = Progress(a, now);
        if (t >= 1) return 0;
        a.Timing.Derivative(t, out double dt, out double dxy);
        if (dt <= 0) return 0;
        return dxy / dt * (a.Destination - a.Start) / (a.Duration / 1000);
    }

    // Someone else moved the list (scroll bar, keys, a list re-measuring its rows): the curve moves with it.
    private static void Follow(ScrollViewer sv, Anim a)
    {
        double moved = sv.VerticalOffset - a.Set;
        if (Math.Abs(moved) < 1) return;
        a.Start += moved;
        a.Destination += moved;
        a.Set = sv.VerticalOffset;
    }

    // Innermost scroll area under the mouse that can still move that way.
    private static ScrollViewer? Target(DependencyObject? d, int delta)
    {
        for (; d != null; d = Ui.Parent(d))
        {
            if (d is Slider) return null;
            if (d is not ScrollViewer sv || sv.ScrollableHeight <= 0 ||
                sv.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled || !InPixels(sv)) continue;
            double at = Active.TryGetValue(sv, out var a) ? a.Destination : sv.VerticalOffset;
            if (delta > 0 ? at > 0.5 : at < sv.ScrollableHeight - 0.5) return sv;
        }
        return null;
    }

    // Lists that scroll by whole items count their offset in rows, not pixels: those keep WPF's own wheel.
    private static bool InPixels(ScrollViewer sv)
        => !sv.CanContentScroll || sv.TemplatedParent is ItemsControl ic && VirtualizingPanel.GetScrollUnit(ic) == ScrollUnit.Pixel;

    private static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private static void OnFrame(object? sender, EventArgs e)
    {
        // The time this frame will be on screen: positions follow the display, not when the handler happens to run.
        double render = ((RenderingEventArgs)e).RenderingTime.TotalMilliseconds;
        _renderLead = render - Clock.Elapsed.TotalMilliseconds;
        foreach (var (sv, a) in Active.ToList())
        {
            if (!sv.IsLoaded)
            {
                Active.Remove(sv);
                continue;
            }
            if (double.IsNaN(a.StartTime))
            {
                a.StartTime = render;
                Seed(a, render);
                Duration(a, render);
            }
            Follow(sv, a);
            double max = sv.ScrollableHeight;
            bool done = render >= a.StartTime + a.Duration;
            double pos = Math.Clamp(done ? a.Destination : PositionAt(a, render), 0, max);
            if (Math.Abs(pos - sv.VerticalOffset) >= 0.01) sv.ScrollToVerticalOffset(pos);
            a.Set = pos;
            if (done || (pos <= 0 && a.Destination <= 0) || (pos >= max && a.Destination >= max)) Active.Remove(sv);
        }
        if (Active.Count == 0)
        {
            CompositionTarget.Rendering -= OnFrame;
            _hooked = false;
        }
    }

    // CSS-style cubic timing curve from (0,0) to (1,1) with control points (x1,y1), (x2,y2).
    private readonly struct Bezier
    {
        private readonly double _x1, _y1, _x2, _y2;

        public Bezier(double x1, double y1, double x2, double y2)
        {
            _x1 = x1;
            _y1 = y1;
            _x2 = x2;
            _y2 = y2;
        }

        private static double Calc(double t, double a1, double a2) => ((A(a1, a2) * t + B(a1, a2)) * t + C(a1)) * t;
        private static double Slope(double t, double a1, double a2) => 3 * A(a1, a2) * t * t + 2 * B(a1, a2) * t + C(a1);
        private static double A(double a1, double a2) => 1 - 3 * a2 + 3 * a1;
        private static double B(double a1, double a2) => 3 * a2 - 6 * a1;
        private static double C(double a1) => 3 * a1;

        // Curve parameter whose x is the given time fraction.
        private double ParamFor(double x)
        {
            double t = x;
            for (int i = 0; i < 8; i++)
            {
                double err = Calc(t, _x1, _x2) - x;
                if (Math.Abs(err) < 1e-7) return t;
                double d = Slope(t, _x1, _x2);
                if (Math.Abs(d) < 1e-6) break;
                t -= err / d;
            }
            // Newton gave up: bisection.
            double lo = 0, hi = 1;
            t = x;
            for (int i = 0; i < 40; i++)
            {
                double v = Calc(t, _x1, _x2);
                if (Math.Abs(v - x) < 1e-7) break;
                if (v < x) lo = t; else hi = t;
                t = (lo + hi) / 2;
            }
            return t;
        }

        public double Value(double x) => x <= 0 ? 0 : x >= 1 ? 1 : Calc(ParamFor(x), _y1, _y2);

        public void Derivative(double x, out double dt, out double dxy)
        {
            double t = ParamFor(Math.Clamp(x, 0, 1));
            dt = Slope(t, _x1, _x2);
            dxy = Slope(t, _y1, _y2);
        }
    }
}
