using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace UltimateMP3Player;

// Content that doesn't fit scrolls to show the rest while the mouse is over its row or card (Marquee.Host),
// then back, until the mouse leaves (the Windows app's Marquee, same timing and fades).
// - a TextBlock cut with "…": it moves inside its own clip (the clip moves back as much, so it stays in place);
// - anything else (e.g. a row of tag chips): it moves inside its parent, which must clip it (a Canvas with ClipToBounds).
// Both edges fade: the right one while it scrolls, the left one only as far as the content has moved away from it.
public static class Marquee
{
    public static readonly AttachedProperty<bool> IsEnabledProperty = AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(Marquee));
    public static bool GetIsEnabled(Control o) => o.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Control o, bool value) => o.SetValue(IsEnabledProperty, value);

    // The element whose hover starts it; without one up the tree, the element itself.
    public static readonly AttachedProperty<bool> HostProperty = AvaloniaProperty.RegisterAttached<Control, bool>("Host", typeof(Marquee));
    public static bool GetHost(Control o) => o.GetValue(HostProperty);
    public static void SetHost(Control o, bool value) => o.SetValue(HostProperty, value);

    private const double Speed = 40, Edge = 14;

    private sealed class State
    {
        public required Control Element;
        public Control? Host;
        public TranslateTransform? Shift;
        public TextTrimming? Trimming;
        public Control? MaskHost;
        public double Width, Travel, RightFade;
        public double Go, Back;
        public DateTime Started;
    }

    private static readonly AttachedProperty<State?> StateProperty = AvaloniaProperty.RegisterAttached<Control, State?>("State", typeof(Marquee));
    private static readonly HashSet<State> Running = new();
    private static bool _frame;

    static Marquee()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            c.AttachedToVisualTree -= OnAttached;
            c.DetachedFromVisualTree -= OnDetached;
            if (e.NewValue is true)
            {
                c.AttachedToVisualTree += OnAttached;
                c.DetachedFromVisualTree += OnDetached;
                if (c.IsAttachedToVisualTree()) Attach(c);
            }
            else Detach(c);
        });
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) => Attach((Control)sender!);
    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => Detach((Control)sender!);

    private static void Attach(Control el)
    {
        if (el.GetValue(StateProperty) != null) return;
        Control host = el;
        for (var v = el.GetVisualParent(); v != null; v = v.GetVisualParent())
        {
            if (v is Control c && GetHost(c)) { host = c; break; }
            if (v is Window) break;
        }
        var st = new State { Element = el, Host = host };
        el.SetValue(StateProperty, st);
        host.PointerEntered += (_, _) => Start(st);
        host.PointerExited += (_, _) => Stop(st);
        // The same element may show another song after recycling: start over.
        el.DataContextChanged += (_, _) => Stop(st);
    }

    private static void Detach(Control el)
    {
        if (el.GetValue(StateProperty) is State st) Stop(st);
    }

    private static void Start(State st)
    {
        if (st.Shift != null) return;
        var travel = st.Element is TextBlock tb ? StartText(st, tb) : StartElement(st);
        if (travel == null || st.Shift == null || st.MaskHost == null) return;
        st.Width = st.MaskHost.Bounds.Width;
        st.Travel = travel.Value.Travel;
        st.RightFade = travel.Value.RightFade;
        st.Go = Math.Max(0.8, st.Travel / Speed);
        st.Back = Math.Max(0.35, st.Go * 0.3);
        st.Started = DateTime.Now;
        Running.Add(st);
        Apply(st, 0);
        Request();
    }

    private static (double Travel, double RightFade)? StartText(State st, TextBlock tb)
    {
        if (string.IsNullOrEmpty(tb.Text) || tb.Bounds.Width < 20) return null;
        var ft = new FormattedText(tb.Text, System.Globalization.CultureInfo.CurrentUICulture, tb.FlowDirection,
            new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.Black);
        double overflow = ft.WidthIncludingTrailingWhitespace - tb.Bounds.Width;
        if (overflow <= 1) return null;
        st.Trimming = tb.TextTrimming;
        tb.TextTrimming = TextTrimming.None;
        st.Shift = new TranslateTransform();
        tb.RenderTransform = st.Shift;
        tb.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute);
        st.MaskHost = tb;
        return (overflow + Edge, Edge);
    }

    private static (double Travel, double RightFade)? StartElement(State st)
    {
        var el = st.Element;
        if (el.GetVisualParent() is not Control clip) return null;
        double overflow = el.Bounds.Width - clip.Bounds.Width;
        if (overflow <= 1) return null;
        st.Shift = new TranslateTransform();
        el.RenderTransform = st.Shift;
        st.MaskHost = clip;
        double right = Math.Max(Edge, Ui.GetFadeRight(clip));
        return (overflow + right, right);
    }

    private static void Request()
    {
        if (_frame || Running.Count == 0 || App.Host?.Window is not { } w) return;
        _frame = true;
        w.RequestAnimationFrame(_ =>
        {
            _frame = false;
            foreach (var st in Running.ToList()) Apply(st, (DateTime.Now - st.Started).TotalSeconds);
            Request();
        });
    }

    // Pause, glide to the end, pause, back quickly, pause, again.
    private static double Position(State st, double t)
    {
        double cycle = 0.6 + st.Go + 1.3 + st.Back + 0.9;
        t %= cycle;
        if (t < 0.6) return 0;
        t -= 0.6;
        if (t < st.Go) return -st.Travel * SineInOut(t / st.Go);
        t -= st.Go;
        if (t < 1.3) return -st.Travel;
        t -= 1.3;
        if (t < st.Back) return -st.Travel * (1 - CubicInOut(t / st.Back));
        return 0;
    }

    private static double SineInOut(double x) => -(Math.Cos(Math.PI * x) - 1) / 2;
    private static double CubicInOut(double x) => x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;

    private static void Apply(State st, double t)
    {
        if (st.Shift == null || st.MaskHost == null) return;
        double x = Position(st, t);
        st.Shift.X = x;
        double w = st.Width;
        if (w <= 0) return;
        // The window that shows the content stays where it was: for a text it moves back by as much as the text moved.
        double origin = ReferenceEquals(st.MaskHost, st.Element) ? -x : 0;
        if (ReferenceEquals(st.MaskHost, st.Element)) st.Element.Clip = new RectangleGeometry(new Rect(origin, -4, w, st.Element.Bounds.Height + 8));
        double left = Math.Min(Edge, Math.Max(0, -x));
        st.MaskHost.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(origin, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(origin + w, 0, RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(left > 0.5 ? Colors.Transparent : Colors.Black, 0),
                new GradientStop(Colors.Black, left / w),
                new GradientStop(Colors.Black, Math.Max(0, (w - st.RightFade) / w)),
                new GradientStop(Colors.Transparent, 1),
            },
        };
    }

    private static void Stop(State st)
    {
        if (st.Shift == null) return;
        Running.Remove(st);
        st.Shift = null;
        if (st.MaskHost != null)
        {
            st.MaskHost.OpacityMask = null;
            // A clip that fades its right edge on its own (Ui.FadeRight) gets it back.
            if (Ui.GetFadeRight(st.MaskHost) > 0) Ui.SetFadeRight(st.MaskHost, Ui.GetFadeRight(st.MaskHost) + 0.0001);
        }
        st.MaskHost = null;
        if (st.Element is TextBlock tb)
        {
            tb.Clip = null;
            tb.RenderTransform = null;
            tb.TextTrimming = st.Trimming ?? TextTrimming.CharacterEllipsis;
        }
        else st.Element.RenderTransform = null;
    }
}
