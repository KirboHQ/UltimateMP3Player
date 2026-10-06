using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace UltimateMP3Player.Services;

// Wheel scrolling like Firefox's (the Windows app's SmoothScroll, same curve and timings): every notch starts a short
// curve to the new destination that begins at the speed the list is already moving. Only for wheel notches: touchpads
// (and the Magic Mouse) already scroll smoothly by themselves and keep the system's feel.
public static class SmoothScroll
{
    public static bool Enabled { get; set; } = true;

    private const double MinMs = 50, MaxMs = 200, IntervalRatio = 2.0;
    private const double VelocityWeighting = 0.25, StopDeceleration = 0.4;
    // Windows' default: 3 lines per notch, 100 pixels.
    private const double Step = 100;

    private sealed class Anim
    {
        public double Start, Destination;
        public double StartTime = double.NaN, Duration;
        public Bezier Timing = new(0, 0, 1 - StopDeceleration, 1);
        public readonly double[] Prev = new double[3];
        public double Set;
    }

    private static readonly Dictionary<ScrollViewer, Anim> Active = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double _renderLead;
    private static bool _hooked, _registered;

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        // Every window, dialog, menu and drop-down (popups are top levels of their own).
        InputElement.PointerWheelChangedEvent.AddClassHandler<TopLevel>(OnWheel, RoutingStrategies.Tunnel);
    }

    private static double Now => Clock.Elapsed.TotalMilliseconds + _renderLead;

    private static double ScrollableHeight(ScrollViewer sv) => Math.Max(0, sv.Extent.Height - sv.Viewport.Height);

    private static void OnWheel(TopLevel top, PointerWheelEventArgs e)
    {
        if (!Enabled || e.Handled || e.KeyModifiers != KeyModifiers.None) return;
        double notches = e.Delta.Y;
        // Whole notches only: fractions come from touchpads.
        if (notches == 0 || Math.Abs(notches - Math.Round(notches)) > 0.001 || e.Delta.X != 0) return;
        var sv = Target(e.Source as Visual, notches);
        if (sv == null) return;
        e.Handled = true;

        bool running = Active.TryGetValue(sv, out var a);
        if (!running)
        {
            a = new Anim { Start = sv.Offset.Y, Destination = sv.Offset.Y, Set = sv.Offset.Y };
            Active[sv] = a;
        }
        else Follow(sv, a!);
        double dest = Math.Clamp(a!.Destination - notches * Step, 0, ScrollableHeight(sv));
        if (running && Math.Abs(dest - a.Destination) < 0.5) return;
        Update(a, dest, running && !double.IsNaN(a.StartTime) ? Now : double.NaN);
        Hook(top);
    }

    private static void Update(Anim a, double destination, double now)
    {
        if (double.IsNaN(now))
        {
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

    private static double VelocityAt(Anim a, double now)
    {
        double t = Progress(a, now);
        if (t >= 1) return 0;
        a.Timing.Derivative(t, out double dt, out double dxy);
        if (dt <= 0) return 0;
        return dxy / dt * (a.Destination - a.Start) / (a.Duration / 1000);
    }

    private static void Follow(ScrollViewer sv, Anim a)
    {
        double moved = sv.Offset.Y - a.Set;
        if (Math.Abs(moved) < 1) return;
        a.Start += moved;
        a.Destination += moved;
        a.Set = sv.Offset.Y;
    }

    // Innermost scroll area under the mouse that can still move that way.
    private static ScrollViewer? Target(Visual? v, double notches)
    {
        for (; v != null; v = v.GetVisualParent())
        {
            if (v is Slider) return null;
            if (v is not ScrollViewer sv || ScrollableHeight(sv) <= 0 || sv.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled) continue;
            double at = Active.TryGetValue(sv, out var a) ? a.Destination : sv.Offset.Y;
            if (notches > 0 ? at > 0.5 : at < ScrollableHeight(sv) - 0.5) return sv;
        }
        return null;
    }

    private static void Hook(TopLevel top)
    {
        if (_hooked) return;
        _hooked = true;
        top.RequestAnimationFrame(t => OnFrame(top, t));
    }

    private static void OnFrame(TopLevel top, TimeSpan time)
    {
        double render = time.TotalMilliseconds;
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
            double max = ScrollableHeight(sv);
            bool done = render >= a.StartTime + a.Duration;
            double pos = Math.Clamp(done ? a.Destination : PositionAt(a, render), 0, max);
            if (Math.Abs(pos - sv.Offset.Y) >= 0.01) sv.Offset = new Vector(sv.Offset.X, pos);
            a.Set = pos;
            if (done || (pos <= 0 && a.Destination <= 0) || (pos >= max && a.Destination >= max)) Active.Remove(sv);
        }
        _hooked = false;
        if (Active.Count == 0) return;
        // The next frame from a window that is still there.
        var next = top.IsLoaded ? top : Active.Keys.Select(TopLevel.GetTopLevel).FirstOrDefault(t => t != null);
        if (next == null)
        {
            Active.Clear();
            return;
        }
        Hook(next);
    }

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
