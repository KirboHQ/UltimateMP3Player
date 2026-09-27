using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UltimateMP3Player.Services;

// Firefox-like wheel scrolling: glides to the target instead of jumping. Every notch moves the
// target further from where the list really is, so scrolling again mid-glide never pulls it back.
public static class SmoothScroll
{
    public static bool Enabled { get; set; } = true;

    private const double TimeConstant = 0.085;

    private sealed class State
    {
        // Where the glide is heading, and the offset last given to the list.
        public double Target;
        public double Set;
    }

    private static readonly Dictionary<ScrollViewer, State> Active = new();
    private static TimeSpan _last;
    private static bool _hooked;

    public static void Attach(Window w) => w.PreviewMouseWheel += OnWheel;

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Enabled || e.Handled || Keyboard.Modifiers != ModifierKeys.None) return;
        var sv = Target(e.OriginalSource as DependencyObject, e.Delta);
        if (sv == null) return;
        e.Handled = true;
        if (!Active.TryGetValue(sv, out var st))
        {
            st = new State { Target = sv.VerticalOffset, Set = sv.VerticalOffset };
            Active[sv] = st;
        }
        else Follow(sv, st);
        double step = 100 * Math.Max(1, SystemParameters.WheelScrollLines) / 3.0;
        st.Target = Math.Clamp(st.Target - e.Delta / 120.0 * step, 0, sv.ScrollableHeight);
        if (!_hooked)
        {
            _hooked = true;
            _last = TimeSpan.Zero;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    // Someone else moved the list (scroll bar, keys, a list re-measuring its rows):
    // keep the distance still to go from where it is now.
    private static void Follow(ScrollViewer sv, State st)
    {
        double moved = sv.VerticalOffset - st.Set;
        if (Math.Abs(moved) < 1) return;
        st.Target += moved;
        st.Set = sv.VerticalOffset;
    }

    // Innermost scroll area under the mouse that can still move that way.
    private static ScrollViewer? Target(DependencyObject? d, int delta)
    {
        for (; d != null; d = Ui.Parent(d))
        {
            if (d is Slider) return null;
            if (d is not ScrollViewer sv || sv.ScrollableHeight <= 0 ||
                sv.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled) continue;
            double at = Active.TryGetValue(sv, out var st) ? st.Target : sv.VerticalOffset;
            if (delta > 0 ? at > 0.5 : at < sv.ScrollableHeight - 0.5) return sv;
        }
        return null;
    }

    private static void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _last == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - _last).TotalSeconds, 0, 0.1);
        _last = now;
        if (dt <= 0) return;
        double k = 1 - Math.Exp(-dt / TimeConstant);
        foreach (var (sv, st) in Active.ToList())
        {
            if (!sv.IsLoaded)
            {
                Active.Remove(sv);
                continue;
            }
            Follow(sv, st);
            // Clamped only here: a list that briefly shrinks doesn't shorten the glide for good.
            double goal = Math.Clamp(st.Target, 0, sv.ScrollableHeight);
            double at = sv.VerticalOffset;
            bool done = Math.Abs(goal - at) < 0.5;
            double next = done ? goal : at + (goal - at) * k;
            sv.ScrollToVerticalOffset(next);
            st.Set = next;
            if (done) Active.Remove(sv);
        }
        if (Active.Count == 0)
        {
            CompositionTarget.Rendering -= OnFrame;
            _hooked = false;
        }
    }
}
