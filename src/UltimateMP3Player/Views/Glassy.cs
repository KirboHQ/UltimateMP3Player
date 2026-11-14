#if AVALONIA
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FrameworkElementKind = Avalonia.Controls.Control;
#else
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FrameworkElementKind = System.Windows.FrameworkElement;
#endif
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Views;

// The glass look on what floats over the window (menus, drop-downs, the queue, the playback panel, dialogs, the phones' sheets): its ground
// is the window's own picture cut where it lies, with a pane's tint over it (Themes.PopupGlass), so it is a pane of the same glass. One
// brush, placed when it shows (and when the window's picture or size changes while it's up), never at every frame. Brush: the resource
// it has without the glass (PopupBrush, SheetBrush). Code that knows better when (a sheet before it slides in) calls Place itself.
public static class Glassy
{
    // The window's picture lies in this (MainWindow's, MainView's GlassBack).
    private static FrameworkElementKind? _host;

    // (what's up follows the window's picture and size)
    private static event Action? Refresh;

    public static FrameworkElementKind? Host
    {
        get => _host;
        set
        {
            if (_host == null) Themes.BackdropChanged += () => Refresh?.Invoke();
            else _host.SizeChanged -= HostSized;
            _host = value;
            if (value != null) value.SizeChanged += HostSized;
        }
    }

    private static void HostSized(object? sender, SizeChangedEventArgs e) => Refresh?.Invoke();

#if AVALONIA
    public static readonly AttachedProperty<string?> BrushProperty = AvaloniaProperty.RegisterAttached<Border, string?>("Brush", typeof(Glassy));
    public static string? GetBrush(Border b) => b.GetValue(BrushProperty);
    public static void SetBrush(Border b, string? value) => b.SetValue(BrushProperty, value);

    static Glassy() => BrushProperty.Changed.AddClassHandler<Border>((b, e) =>
    {
        if (e.GetNewValue<string?>() is not { } key) return;
        b.Background = Ui.Res(key);
        if (e.GetOldValue<string?>() != null) return;
        void Again() => Place(b, GetBrush(b));
        // (a popup is laid out and put in its place after it joins the window: once it's there)
        void Soon() => Dispatcher.UIThread.Post(Again, Avalonia.Threading.DispatcherPriority.Loaded);
        b.AttachedToVisualTree += (_, _) =>
        {
            Refresh += Again;
            Soon();
        };
        b.DetachedFromVisualTree += (_, _) => Refresh -= Again;
        // (shown again; laid out again: a panel that grows from the bottom, the window resized)
        b.PropertyChanged += (_, change) =>
        {
            if (change.Property == Visual.IsVisibleProperty && b.IsVisible || change.Property == Visual.BoundsProperty && b.IsEffectivelyVisible) Soon();
        };
    });

    public static void Place(Border b, string? key)
    {
        if (Slice(b) is { } brush)
        {
            if (b.Background is ImageBrush old && ReferenceEquals(old.Source, brush.Source) && old.DestinationRect == brush.DestinationRect) return;
            b.Background = brush;
        }
        else if (key != null) b.Background = Ui.Res(key);
    }

    private static ImageBrush? Slice(Border b)
    {
        double w = b.Bounds.Width, h = b.Bounds.Height;
        if (w <= 0 || h <= 0 || !b.IsEffectivelyVisible || Themes.PopupGlass is not { } picture || _host is not { IsEffectivelyVisible: true } host
            || TopLevel.GetTopLevel(b) is not { } top || TopLevel.GetTopLevel(host) is not { } hostTop) return null;
        double x, y, side, unit;
        if (top == hostTop)
        {
            // In the same window (the phones' sheets, the queue, the panel of the downloads): over the picture, in its units, where it
            // rests (a sheet still sliding in, the queue rising, count where they stop).
            if (Rest(b, host, new Point(0, 0)) is not { } from || Rest(b, host, new Point(w, h)) is not { } to) return null;
            unit = (to.X - from.X) / w;
            (x, y, side) = Glass.Slice(0, 0, host.Bounds.Width, host.Bounds.Height, from.X, from.Y);
        }
        else
        {
            // A window of its own (menus, drop-downs, dialogs): screen pixels, from its middle, which stays put while a dialog comes
            // forward from a little smaller around it.
            unit = top.RenderScaling * Ui.ZoomTransform.ScaleX;
            var middle = b.PointToScreen(new Point(w / 2, h / 2));
            PixelPoint from = host.PointToScreen(new Point(0, 0)), to = host.PointToScreen(new Point(host.Bounds.Width, host.Bounds.Height));
            (x, y, side) = Glass.Slice(from.X, from.Y, to.X - from.X, to.Y - from.Y, middle.X - w * unit / 2, middle.Y - h * unit / 2);
        }
        if (unit <= 0) return null;
        return new ImageBrush(picture) { Stretch = Stretch.Fill, DestinationRect = new RelativeRect(x / unit, y / unit, side / unit, side / unit, RelativeUnit.Absolute) };
    }

    // A point of it over the host as laid out: the moves of render transforms left out, the interface's zoom (a LayoutTransformControl,
    // which scales its child) counted. Null when it isn't beside the host in its window.
    private static Point? Rest(Visual b, Visual host, Point point)
    {
        var common = host.GetVisualParent();
        for (Visual? v = b; v != null;)
        {
            if (v == common) return new Point(point.X - host.Bounds.X, point.Y - host.Bounds.Y);
            var parent = v.GetVisualParent();
            if (parent is LayoutTransformControl { LayoutTransform: ScaleTransform zoom }) point = new Point(point.X * zoom.ScaleX, point.Y * zoom.ScaleY);
            point = new Point(point.X + v.Bounds.X, point.Y + v.Bounds.Y);
            v = parent;
        }
        return null;
    }
#else
    public static readonly DependencyProperty BrushProperty = DependencyProperty.RegisterAttached("Brush", typeof(string), typeof(Glassy),
        new PropertyMetadata(null, (d, e) =>
        {
            if (d is Border b && e.NewValue is string key) Hook(b, key, e.OldValue == null);
        }));

    public static string? GetBrush(DependencyObject d) => (string?)d.GetValue(BrushProperty);
    public static void SetBrush(DependencyObject d, string? value) => d.SetValue(BrushProperty, value);

    private static void Hook(Border b, string key, bool first)
    {
        b.SetResourceReference(Border.BackgroundProperty, key);
        if (!first) return;
        void Again() => Place(b, GetBrush(b));
        b.IsVisibleChanged += (_, _) =>
        {
            Refresh -= Again;
            if (!b.IsVisible) return;
            Refresh += Again;
            Again();
            // (a popup is put in its place within the same pass: once it's there, again)
            b.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Again);
        };
        // (laid out again: a panel that grows from the bottom)
        b.SizeChanged += (_, _) =>
        {
            if (b.IsVisible) b.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Again);
        };
    }

    public static void Place(Border b, string? key)
    {
        if (Slice(b) is { } brush)
        {
            if (b.Background is ImageBrush old && ReferenceEquals(old.ImageSource, brush.ImageSource) && old.Viewport == brush.Viewport) return;
            b.Background = brush;
        }
        else if (key != null) b.SetResourceReference(Border.BackgroundProperty, key);
    }

    private static ImageBrush? Slice(Border b)
    {
        double w = b.ActualWidth, h = b.ActualHeight;
        if (w <= 0 || h <= 0 || !b.IsVisible || Themes.PopupGlass is not { } picture || _host is not { IsVisible: true } host
            || PresentationSource.FromVisual(b)?.CompositionTarget is not { } target) return null;
        double x, y, side, unit;
        if (Rest(b, host) is { } rest)
        {
            // In the window (the queue, the panel of the downloads): over the picture, in its units, where it rests (the queue rising
            // counts where it stops). The zoom is on the host's parent, so both are in the same units.
            unit = 1;
            (x, y, side) = Glass.Slice(0, 0, host.ActualWidth, host.ActualHeight, rest.X, rest.Y);
        }
        else
        {
            // A window of its own (menus, drop-downs, dialogs): screen pixels, from its middle, which stays put while a dialog comes
            // forward from a little smaller around it.
            unit = target.TransformToDevice.M11 * Ui.ZoomTransform.ScaleX;
            var middle = b.PointToScreen(new Point(w / 2, h / 2));
            Point from = host.PointToScreen(new Point(0, 0)), to = host.PointToScreen(new Point(host.ActualWidth, host.ActualHeight));
            (x, y, side) = Glass.Slice(from.X, from.Y, to.X - from.X, to.Y - from.Y, middle.X - w * unit / 2, middle.Y - h * unit / 2);
        }
        var brush = new ImageBrush(picture)
        {
            Stretch = Stretch.Fill, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(x / unit, y / unit, side / unit, side / unit),
        };
        brush.Freeze();
        return brush;
    }

    // Its corner over the host as laid out, the moves of render transforms left out; null when it isn't beside the host in its window.
    private static Point? Rest(Visual b, FrameworkElement host)
    {
        var common = VisualTreeHelper.GetParent(host);
        var at = VisualTreeHelper.GetOffset(host);
        double x = -at.X, y = -at.Y;
        for (DependencyObject? v = b; v != null; v = VisualTreeHelper.GetParent(v))
        {
            if (v == common) return new Point(x, y);
            if (v is not Visual visual) return null;
            var offset = VisualTreeHelper.GetOffset(visual);
            x += offset.X;
            y += offset.Y;
        }
        return null;
    }
#endif
}
