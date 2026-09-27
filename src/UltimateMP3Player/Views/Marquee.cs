using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace UltimateMP3Player;

// Content that doesn't fit scrolls to show the rest while the mouse is over its row or card (Marquee.Host),
// then back, until the mouse leaves.
// - a TextBlock cut with "…": its glyphs move inside it (TextEffect), so it keeps its place and clip;
// - anything else (e.g. a row of tag chips): it moves inside its parent, which must clip it (a Canvas with ClipToBounds).
// Both edges fade: the right one while it scrolls, the left one only as far as the content has moved away from it.
public static class Marquee
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(Marquee), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetIsEnabled(DependencyObject o) => (bool)o.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject o, bool value) => o.SetValue(IsEnabledProperty, value);

    // The element whose hover starts it; without one up the tree, the element itself.
    public static readonly DependencyProperty HostProperty = DependencyProperty.RegisterAttached(
        "Host", typeof(bool), typeof(Marquee), new PropertyMetadata(false));

    public static bool GetHost(DependencyObject o) => (bool)o.GetValue(HostProperty);
    public static void SetHost(DependencyObject o, bool value) => o.SetValue(HostProperty, value);

    private const double Speed = 40, Edge = 14;

    private sealed class State
    {
        public required FrameworkElement Element;
        public TranslateTransform? Shift;
        public object? Trimming;
        // The mask on the element that clips (the text itself, or the parent), and what it had before.
        public FrameworkElement? MaskHost;
        public LinearGradientBrush? Mask;
        public object? OldMask;
        public double Width;
    }

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(Marquee), new PropertyMetadata(null));

    private static readonly HashSet<State> Running = new();

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        if (e.NewValue is true)
        {
            el.Loaded += OnLoaded;
            el.Unloaded += OnUnloaded;
            if (el.IsLoaded) Attach(el);
        }
        else
        {
            el.Loaded -= OnLoaded;
            el.Unloaded -= OnUnloaded;
            Detach(el);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Attach((FrameworkElement)sender);
    private static void OnUnloaded(object sender, RoutedEventArgs e) => Detach((FrameworkElement)sender);

    private static void Attach(FrameworkElement el)
    {
        if (el.GetValue(StateProperty) != null) return;
        FrameworkElement host = el;
        for (DependencyObject? d = VisualTreeHelper.GetParent(el); d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is FrameworkElement fe && GetHost(fe)) { host = fe; break; }
            if (d is Window) break;
        }
        var st = new State { Element = el };
        el.SetValue(StateProperty, st);
        host.MouseEnter += (_, _) => Start(st);
        host.MouseLeave += (_, _) => Stop(st);
        // The same element may show another song after recycling: start over.
        el.DataContextChanged += (_, _) => Stop(st);
    }

    private static void Detach(FrameworkElement el)
    {
        if (el.GetValue(StateProperty) is State st) Stop(st);
    }

    private static void Start(State st)
    {
        if (st.Shift != null) return;
        var travel = st.Element is TextBlock tb ? StartText(st, tb) : StartElement(st);
        if (travel == null || st.Shift == null || st.MaskHost == null) return;
        st.Width = st.MaskHost.ActualWidth;
        st.Mask = EdgeMask(st.Width, travel.Value.RightFade);
        st.OldMask = st.MaskHost.ReadLocalValue(UIElement.OpacityMaskProperty);
        st.MaskHost.OpacityMask = st.Mask;
        Animate(st.Shift, travel.Value.Travel);
        if (Running.Count == 0) CompositionTarget.Rendering += OnFrame;
        Running.Add(st);
    }

    private static (double Travel, double RightFade)? StartText(State st, TextBlock tb)
    {
        if (string.IsNullOrEmpty(tb.Text) || tb.ActualWidth < 20) return null;
        var ft = new FormattedText(tb.Text, CultureInfo.CurrentUICulture, tb.FlowDirection,
            new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch), tb.FontSize, Brushes.Black,
            VisualTreeHelper.GetDpi(tb).PixelsPerDip);
        double overflow = ft.WidthIncludingTrailingWhitespace - tb.ActualWidth;
        if (overflow <= 1) return null;

        st.Trimming = tb.ReadLocalValue(TextBlock.TextTrimmingProperty);
        tb.TextTrimming = TextTrimming.None;
        st.Shift = new TranslateTransform();
        tb.TextEffects = new TextEffectCollection { new TextEffect { PositionStart = 0, PositionCount = tb.Text.Length, Transform = st.Shift } };
        st.MaskHost = tb;
        return (overflow + Edge, Edge);
    }

    private static (double Travel, double RightFade)? StartElement(State st)
    {
        var el = st.Element;
        if (VisualTreeHelper.GetParent(el) is not FrameworkElement clip) return null;
        double overflow = el.ActualWidth - clip.ActualWidth;
        if (overflow <= 1) return null;
        st.Shift = new TranslateTransform();
        el.RenderTransform = st.Shift;
        st.MaskHost = clip;
        double right = Math.Max(Edge, Ui.GetFadeRight(clip));
        return (overflow + right, right);
    }

    // Stops: [0] [1] = left edge (flat until the content moves), [2] [3] = right edge.
    private static LinearGradientBrush EdgeMask(double w, double right)
    {
        var mask = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(w, 0) };
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, Math.Max(0, (w - right) / w)));
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        return mask;
    }

    // The left fade follows how far the content has moved: none at rest, full once it's Edge px in.
    private static void OnFrame(object? sender, EventArgs e)
    {
        foreach (var st in Running)
        {
            if (st.Shift == null || st.Mask == null || st.Width <= 0) continue;
            double f = Math.Min(Edge, Math.Max(0, -st.Shift.X));
            st.Mask.GradientStops[0].Color = f > 0.5 ? Colors.Transparent : Colors.Black;
            st.Mask.GradientStops[1].Offset = f / st.Width;
        }
    }

    // Pause, glide to the end, pause, back quickly, pause, again.
    private static void Animate(TranslateTransform shift, double travel)
    {
        double go = Math.Max(0.8, travel / Speed), back = Math.Max(0.35, go * 0.3);
        var anim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        double t = 0.6;
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t))));
        t += go;
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(-travel, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t)), new SineEase { EasingMode = EasingMode.EaseInOut }));
        t += 1.3;
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(-travel, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t))));
        t += back;
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t)), new CubicEase { EasingMode = EasingMode.EaseInOut }));
        t += 0.9;
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t))));
        shift.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private static void Stop(State st)
    {
        if (st.Shift == null) return;
        st.Shift.BeginAnimation(TranslateTransform.XProperty, null);
        st.Shift = null;
        if (Running.Remove(st) && Running.Count == 0) CompositionTarget.Rendering -= OnFrame;
        if (st.MaskHost != null)
        {
            if (st.OldMask == DependencyProperty.UnsetValue || st.OldMask == null) st.MaskHost.ClearValue(UIElement.OpacityMaskProperty);
            else st.MaskHost.OpacityMask = (Brush)st.OldMask;
        }
        st.MaskHost = null;
        st.Mask = null;
        if (st.Element is TextBlock tb)
        {
            tb.TextEffects = null;
            if (st.Trimming == DependencyProperty.UnsetValue) tb.ClearValue(TextBlock.TextTrimmingProperty);
            else tb.TextTrimming = (TextTrimming)st.Trimming!;
        }
        else st.Element.ClearValue(UIElement.RenderTransformProperty);
    }
}
