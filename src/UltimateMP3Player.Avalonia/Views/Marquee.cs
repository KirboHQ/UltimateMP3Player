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

    // It goes by itself, without a mouse over it (the phone: the song playing, its row in a list), as long as it's shown
    // and doesn't fit; a new text or width starts it over.
    public static readonly AttachedProperty<bool> AutoProperty = AvaloniaProperty.RegisterAttached<Control, bool>("Auto", typeof(Marquee));
    public static bool GetAuto(Control o) => o.GetValue(AutoProperty);
    public static void SetAuto(Control o, bool value) => o.SetValue(AutoProperty, value);

    private const double Speed = 40, Edge = 14;

    private sealed class State
    {
        public required Control Element;
        public Control? Host;
        public TranslateTransform? Shift;
        public TextTrimming? Trimming;
        public Control? MaskHost;
        public double Width, Travel, RightFade;
        public double OldWidth = double.NaN;
        public bool Pending;
        public Avalonia.Layout.HorizontalAlignment OldAlignment;
        public double Go, Back;
        public DateTime Started;
        // Where it was drawn last (the pauses draw nothing again) and its fades, changed in place frame by frame.
        public double LastX = double.NaN;
        public LinearGradientBrush? Mask;
        // A text fades through its own colour (a gradient as its foreground while it moves): an OpacityMask costs an
        // offscreen layer at every frame. Ink = that colour; InkSet gives the text its own foreground back.
        public Color? Ink;
        public IDisposable? InkSet;
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
        AutoProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            if (c.GetValue(StateProperty) is not State st) return;
            if (e.NewValue is true) Later(st);
            else Stop(st);
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
        host.PointerEntered += (_, _) => { if (!GetAuto(el)) Start(st); };
        host.PointerExited += (_, _) => { if (!GetAuto(el)) Stop(st); };
        // The same element may show another song after recycling: start over.
        el.DataContextChanged += (_, _) =>
        {
            Stop(st);
            if (GetAuto(el)) Later(st);
        };
        el.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty && GetAuto(el)) Later(st);
        };
        // A new width (laid out the first time, a rotation) starts it over; not its own widening while it moves, nor
        // the narrowing back when it stops.
        el.SizeChanged += (_, e) =>
        {
            if (GetAuto(el) && st.Shift == null && Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1 && Math.Abs(e.NewSize.Width - st.Width) > 1) Later(st);
        };
        // While it moves its own width is fixed: when the room around it narrows (a list's row while choosing several gets
        // a tick) its window would reach over the buttons beside it. Started over in the new room (only a narrowing: a
        // parent that widens with it, like a row of its own, would start it over for ever).
        if (el.GetVisualParent() is Control room)
            room.SizeChanged += (_, e) =>
            {
                if (GetAuto(el) && st.Shift != null && e.NewSize.Width < st.Width - 1) Later(st);
            };
        if (GetAuto(el)) Later(st);
    }

    // Once the layout has settled (the new text measured). Several asks at once count as one; one that's moving stops
    // first and starts again after the layout has narrowed it back.
    private static void Later(State st)
    {
        if (st.Pending) return;
        st.Pending = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            st.Pending = false;
            if (!GetAuto(st.Element) || !st.Element.IsAttachedToVisualTree()) return;
            if (st.Shift != null)
            {
                Stop(st);
                Later(st);
                return;
            }
            Start(st);
        }, Avalonia.Threading.DispatcherPriority.Background);
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
        st.LastX = double.NaN;
        Running.Add(st);
        Apply(st, 0);
        // A pause of the others planned without this one: planned again.
        _still?.Dispose();
        _still = null;
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
        // As wide as all its text while it moves (its window stays as wide as before: the clip and the fades). Moved
        // further than its own width, a narrower element would count as off screen and not be drawn at all.
        st.OldWidth = tb.Width;
        st.OldAlignment = tb.HorizontalAlignment;
        tb.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        tb.Width = Math.Ceiling(ft.WidthIncludingTrailingWhitespace) + 1;
        st.Shift = new TranslateTransform();
        tb.RenderTransform = st.Shift;
        tb.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute);
        st.MaskHost = tb;
        st.Ink = tb.Foreground is ISolidColorBrush ink ? Color.FromArgb((byte)Math.Round(ink.Color.A * ink.Opacity), ink.Color.R, ink.Color.G, ink.Color.B) : null;
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
        if (Running.Count == 0 || _still != null || App.Host?.Window is not { } w) return;
        // A frame asked to a screen that's gone (the phone's app closed and opened again) never comes.
        if (_frame && _frameOn == w) return;
        // Only what's on screen moves (a hidden page's text doesn't keep the screen redrawing); looked at again in a while.
        var now = DateTime.Now;
        double still = double.MaxValue;
        foreach (var s in Running)
            if (s.Element.IsEffectivelyVisible) still = Math.Min(still, StillFor(s, (now - s.Started).TotalSeconds));
        if (still == double.MaxValue)
        {
            if (!_idle)
            {
                _idle = true;
                Ui.Later(() =>
                {
                    _idle = false;
                    Request();
                });
            }
            return;
        }
        // All of them resting at an end: no frames until the first one moves again.
        if (still > 0.05)
        {
            _still = Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                _still = null;
                Request();
            }, TimeSpan.FromSeconds(still - 0.02));
            return;
        }
        _frame = true;
        _frameOn = w;
        Pacer.Request(w, _ =>
        {
            _frame = false;
            var at = DateTime.Now;
            foreach (var st in Running.ToList())
                if (st.Element.IsEffectivelyVisible) Apply(st, (at - st.Started).TotalSeconds);
            Request();
        });
    }

    // 60 steps a second at most: smooth for a text at this speed, half the frames of a 120 Hz phone.
    private static readonly Views.FramePacer Pacer = new(1000 / 61.0);
    private static bool _idle;
    private static TopLevel? _frameOn;
    private static IDisposable? _still;

    // How long it stays where it is from t on (0 while it moves).
    private static double StillFor(State st, double t)
    {
        double cycle = 0.6 + st.Go + 1.3 + st.Back + 0.9;
        t %= cycle;
        if (t < 0.6) return 0.6 - t;
        t -= 0.6 + st.Go;
        if (t < 0) return 0;
        if (t < 1.3) return 1.3 - t;
        t -= 1.3 + st.Back;
        if (t < 0) return 0;
        // The pause at the start, then the next turn's.
        return 0.9 - t + 0.6;
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
        // Resting at an end: drawn already.
        if (x == st.LastX) return;
        st.LastX = x;
        st.Shift.X = x;
        double w = st.Width;
        if (w <= 0) return;
        // The window that shows the content stays where it was: for a text it moves back by as much as the text moved.
        bool text = ReferenceEquals(st.MaskHost, st.Element);
        double origin = text ? -x : 0;
        if (text)
        {
            var window = new Rect(origin, -4, w, st.Element.Bounds.Height + 8);
            if (st.Element.Clip is RectangleGeometry clip) clip.Rect = window;
            else st.Element.Clip = new RectangleGeometry(window);
        }
        double left = Math.Min(Edge, Math.Max(0, -x));
        if (st.Mask == null)
        {
            st.Mask = new LinearGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Colors.Black, 0), new GradientStop(Colors.Black, 0),
                    new GradientStop(Colors.Black, 1), new GradientStop(Colors.Transparent, 1),
                },
            };
        }
        var mask = st.Mask;
        // A text: the gradient is its colour (to transparent at the edges); anything else: a mask over it.
        var ink = text && st.Element is TextBlock ? st.Ink : null;
        Color solid = ink ?? Colors.Black, clear = ink is { } c ? Color.FromArgb(0, c.R, c.G, c.B) : Colors.Transparent;
        mask.StartPoint = new RelativePoint(origin, 0, RelativeUnit.Absolute);
        mask.EndPoint = new RelativePoint(origin + w, 0, RelativeUnit.Absolute);
        mask.GradientStops[0].Color = left > 0.5 ? clear : solid;
        mask.GradientStops[1].Color = solid;
        mask.GradientStops[1].Offset = left / w;
        mask.GradientStops[2].Color = solid;
        mask.GradientStops[2].Offset = Math.Max(0, (w - st.RightFade) / w);
        mask.GradientStops[3].Color = clear;
        if (ink != null)
        {
            // Over its own (styled or local) foreground, given back by Stop.
            st.InkSet ??= st.Element.SetValue(TextBlock.ForegroundProperty, mask, Avalonia.Data.BindingPriority.Animation);
        }
        else if (!ReferenceEquals(st.MaskHost.OpacityMask, mask)) st.MaskHost.OpacityMask = mask;
    }

    private static void Stop(State st)
    {
        if (st.Shift == null) return;
        Running.Remove(st);
        st.Shift = null;
        st.Mask = null;
        st.InkSet?.Dispose();
        st.InkSet = null;
        st.Ink = null;
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
            tb.Width = st.OldWidth;
            tb.HorizontalAlignment = st.OldAlignment;
        }
        else st.Element.RenderTransform = null;
    }
}
