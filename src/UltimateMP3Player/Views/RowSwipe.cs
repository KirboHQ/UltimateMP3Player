using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// A song's row pulled to the right, as on the phones (the same strip: Avalonia/Views/RowSwipe.cs): let go past a mark, the
// song goes into the queue (its menu's "Add to queue"). The row follows and uncovers a strip with the icon and the words;
// past the mark the strip turns to the accent colour and the icon pops; back before the mark, nothing happens. Pulled by a
// drag mostly to the right (Templates: any other way it's still dragged onto a playlist), or by two fingers sideways on the
// touchpad (the sideways wheel, Attach), which has no release: the row lets go once the fingers stop for a moment.
public sealed class RowSwipe
{
    private readonly FrameworkElement _row;
    private readonly Action _run;
    private readonly AdornerLayer _layer;
    private readonly Strip _adorner;
    private readonly Grid _strip;
    private readonly Border _fill;
    private readonly TextBlock _icon, _label;
    private readonly StackPanel _mark;
    private readonly TranslateTransform _shift = new();
    private readonly ScaleTransform _pop = new();
    private readonly double _trigger;
    private bool _armed;
    private int _fadeRun, _popRun;
    private double _x;

    // host: the row's parent, which stays still (the strip is its adorner, inside the list's visible part).
    private RowSwipe(FrameworkElement row, Action run, UIElement host, AdornerLayer layer)
    {
        _row = row;
        _run = run;
        _layer = layer;
        _trigger = row.ActualWidth * MarkShare;
        // (square on the left, as on the phones)
        var corners = new CornerRadius(0, 16, 16, 0);
        _fill = new Border { Background = Res("AccentBrush"), CornerRadius = corners, Opacity = 0 };
        _icon = new TextBlock
        {
            Text = "\uE8FD", Style = (Style)Application.Current.Resources["Glyph"], FontSize = 20, VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _pop, RenderTransformOrigin = new Point(0.5, 0.5),
        };
        _label = new TextBlock
        {
            Text = L.T("Add to queue"), FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        _mark = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(22, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _mark.Children.Add(_icon);
        _mark.Children.Add(_label);
        // (the words don't wrap: the strip uncovers them, it doesn't squeeze them)
        _strip = new Grid { Width = 0, Height = row.ActualHeight, ClipToBounds = true };
        _strip.Children.Add(new Border { Background = Res("Surface3Brush"), CornerRadius = corners });
        _strip.Children.Add(_fill);
        _strip.Children.Add(_mark);
        // (where the row's place is, also while it's still going back from a pull just before)
        var at = VisualTreeHelper.GetOffset(row);
        _adorner = new Strip(host, _strip, new Point(at.X, at.Y));
        layer.Add(_adorner);
        Paint();
        row.RenderTransform = _shift;
        Place(0);
    }

    // The mark at this share of the row, whatever the screen (the phones' rows, as wide as their screen, have it at 0.3).
    private const double MarkShare = 0.15;

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    // A song's row (the lists' template, named "Row"), and what letting it go does; null where there's nothing to queue.
    private static RowSwipe? Create(FrameworkElement row)
    {
        if (row.DataContext is not TrackRow r || Menus.AddToQueue(r.Track, r.Owner) is not { } run) return null;
        if (VisualTreeHelper.GetParent(row) is not UIElement host || AdornerLayer.GetAdornerLayer(host) is not { } layer) return null;
        return new RowSwipe(row, run, host, layer);
    }

    // dx: how far it was pulled to the right; past the mark the row follows less and less.
    private void Move(double dx)
    {
        Place(dx <= _trigger ? Math.Max(0, dx) : _trigger + (dx - _trigger) * 0.35);
        bool armed = _x >= _trigger;
        if (armed == _armed) return;
        _armed = armed;
        Paint();
        double from = _fill.Opacity, to = armed ? 1 : 0;
        int run = ++_fadeRun;
        if (!Ui.Animations)
        {
            _fill.Opacity = to;
            return;
        }
        Tween(140, t => { if (run == _fadeRun) _fill.Opacity = from + (to - from) * t; });
        if (!armed) return;
        int pop = ++_popRun;
        Tween(260, t => { if (pop == _popRun) _pop.ScaleX = _pop.ScaleY = 1.3 - 0.3 * t; });
    }

    // Grey on grey, then the accent's text colour on the accent.
    private void Paint()
    {
        var brush = Res(_armed ? "OnAccentBrush" : "SubTextBrush");
        _icon.Foreground = brush;
        _label.Foreground = brush;
    }

    private void Place(double x)
    {
        _x = x;
        _shift.X = x;
        _strip.Width = x;
        // the icon and the words come in as there's room for them
        _mark.Opacity = Math.Clamp((x - 16) / 40, 0, 1);
    }

    // Let go (or taken away): past the mark the song goes into the queue; the row goes back to its place either way.
    private void End(bool commit)
    {
        if (commit && _armed) _run();
        double from = _x;
        if (!Ui.Animations || from <= 0)
        {
            Done();
            return;
        }
        Tween(240, t =>
        {
            Place(from * (1 - t));
            if (t >= 1) Done();
        });
    }

    private void Done()
    {
        _layer.Remove(_adorner);
        // (unless the same row is already pulled again)
        if (_row.RenderTransform == _shift) _row.ClearValue(UIElement.RenderTransformProperty);
    }

    // apply(eased progress 0..1) every frame for ms milliseconds (as Avalonia's Ui.Tween).
    private static void Tween(double ms, Action<double> apply)
    {
        var clock = Stopwatch.StartNew();
        EventHandler? frame = null;
        frame = (_, _) =>
        {
            double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / ms);
            apply(1 - Math.Pow(1 - t, 3));
            if (t >= 1) FrameClock.Remove(frame!);
        };
        apply(0);
        FrameClock.Add(frame);
    }

    // The strip, over the row's parent where the row was (it doesn't follow the row's shift).
    private sealed class Strip : Adorner
    {
        private readonly FrameworkElement _child;
        private readonly Point _at;

        public Strip(UIElement host, FrameworkElement child, Point at) : base(host)
        {
            _child = child;
            _at = at;
            IsHitTestVisible = false;
            AddVisualChild(child);
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _child;

        protected override Size MeasureOverride(Size constraint)
        {
            _child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _child.Arrange(new Rect(_at, _child.DesiredSize));
            return finalSize;
        }
    }

    // ------------------------------------------------------------------ the mouse

    private static RowSwipe? _mouse;
    private static double _mouseFrom;

    // A drag mostly to the right began on a song's row (from: where, in the window): true when the row is pulled now.
    public static bool StartDrag(FrameworkElement row, Point from)
    {
        if (_mouse != null || _wheel != null || Create(row) is not { } swipe) return false;
        _mouse = swipe;
        _mouseFrom = from.X;
        row.MouseMove += OnMouseMove;
        row.MouseLeftButtonUp += OnMouseUp;
        row.LostMouseCapture += OnLostCapture;
        if (!row.CaptureMouse()) EndMouse(false);
        return _mouse != null;
    }

    private static void OnMouseMove(object sender, MouseEventArgs e)
    {
        _mouse?.Move(e.GetPosition(null).X - _mouseFrom);
        e.Handled = true;
    }

    private static void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        EndMouse(true);
    }

    // (the row's own capture: the event also comes up from its children)
    private static void OnLostCapture(object sender, MouseEventArgs e)
    {
        if (e.OriginalSource == sender) EndMouse(false);
    }

    private static void EndMouse(bool commit)
    {
        if (_mouse is not { } swipe) return;
        _mouse = null;
        var row = swipe._row;
        row.MouseMove -= OnMouseMove;
        row.MouseLeftButtonUp -= OnMouseUp;
        row.LostMouseCapture -= OnLostCapture;
        row.ReleaseMouseCapture();
        swipe.End(commit);
    }

    // ------------------------------------------------------------------ the touchpad

    private const int WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;
    // In wheel steps (120 units), not pixels: a step takes the row this share of the way to the mark (whatever the row's
    // width); the row moves after this much of a step.
    private const double StepShare = 0.4, StartSteps = 0.15;
    private static RowSwipe? _wheel;
    private static FrameworkElement? _wheelRow;
    private static double _wheelSteps;
    private static DispatcherTimer? _wheelEnd;

    // The sideways wheel (WPF has no event for it): two fingers on the touchpad, or a tilting wheel.
    public static void Attach(Window w) => w.SourceInitialized += (_, _) =>
        HwndSource.FromHwnd(new WindowInteropHelper(w).Handle)?.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (msg == WM_MOUSEHWHEEL) handled = OnWheel(w, unchecked((short)((long)wp >> 16)), lp);
            // (scrolling up or down again: let go)
            else if (msg == WM_MOUSEWHEEL && _wheel != null) EndWheel();
            return IntPtr.Zero;
        });

    // delta > 0: the content would go left; at: where the pointer is (on the screen). True when it pulled a row.
    private static bool OnWheel(Window w, int delta, IntPtr at)
    {
        double steps = -delta / 120.0;
        _wheelEnd ??= new DispatcherTimer(TimeSpan.FromMilliseconds(160), DispatcherPriority.Normal, (_, _) => EndWheel(), Dispatcher.CurrentDispatcher);
        if (_wheel != null)
        {
            _wheelSteps += steps;
            _wheel.Move(_wheelSteps * StepShare * _wheel._trigger);
            _wheelEnd.Stop();
            _wheelEnd.Start();
            return true;
        }
        var screen = new Point(unchecked((short)((long)at & 0xFFFF)), unchecked((short)(((long)at >> 16) & 0xFFFF)));
        if (_mouse != null || w.InputHitTest(w.PointFromScreen(screen)) is not DependencyObject hit || RowAt(hit) is not { } row) return false;
        if (row != _wheelRow) _wheelSteps = 0;
        _wheelRow = row;
        _wheelSteps += steps;
        _wheelEnd.Stop();
        _wheelEnd.Start();
        // (a little to the right first, as with the mouse: then the row follows from there)
        if (_wheelSteps < StartSteps || Create(row) is not { } swipe) return false;
        _wheel = swipe;
        _wheelSteps = 0;
        return true;
    }

    private static void EndWheel()
    {
        _wheelEnd?.Stop();
        var swipe = _wheel;
        _wheel = null;
        _wheelRow = null;
        _wheelSteps = 0;
        swipe?.End(true);
    }

    private static FrameworkElement? RowAt(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is FrameworkElement { Name: "Row", DataContext: TrackRow } row) return row;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}
