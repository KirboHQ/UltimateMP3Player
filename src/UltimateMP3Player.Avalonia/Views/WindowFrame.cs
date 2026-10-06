using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace UltimateMP3Player.Views;

// The app's own title bar (the Windows app's WindowFrame): dark, 32 px, logo and name on the left.
// Windows: our minimize/maximize/close buttons. macOS: the system's traffic lights over the same bar (where every Mac
// app has them; the name moves right to make room). Linux: a frameless window with our buttons and resize edges.
public static class WindowFrame
{
    public const double CaptionHeight = 32;

    public static void Apply(Window w, Panel root, Control titleBar, Control titleText, Control windowButtons,
                             Button? min, Button? max, Button close, Panel resizeEdges)
    {
        if (OperatingSystem.IsMacOS())
        {
            w.ExtendClientAreaToDecorationsHint = true;
            w.ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome;
            w.ExtendClientAreaTitleBarHeightHint = CaptionHeight;
            windowButtons.IsVisible = false;
            titleText.Margin = new Thickness(78, 0, 0, 0);
        }
        else if (OperatingSystem.IsWindows())
        {
            w.ExtendClientAreaToDecorationsHint = true;
            w.ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
            w.ExtendClientAreaTitleBarHeightHint = CaptionHeight;
        }
        else
        {
            w.SystemDecorations = SystemDecorations.None;
            if (w.CanResize) AddResizeEdges(w, resizeEdges);
        }

        if (min != null) min.Click += (_, _) => w.WindowState = WindowState.Minimized;
        if (max != null) max.Click += (_, _) => Toggle(w);
        close.Click += (_, _) => w.Close();

        // Dragging the bar moves the window, a double click maximizes (not from its buttons).
        titleBar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(titleBar).Properties.IsLeftButtonPressed || e.Handled) return;
            if (e.ClickCount == 2 && max != null && w.CanResize)
            {
                Toggle(w);
                e.Handled = true;
                return;
            }
            try { w.BeginMoveDrag(e); } catch { }
        };

        void Fit()
        {
            bool maximized = w.WindowState == WindowState.Maximized;
            if (max != null) max.Content = Icons.Map(maximized ? "" : "");
            // A maximized window spills over the screen by its invisible frame (Windows).
            root.Margin = maximized ? w.OffScreenMargin : default;
            resizeEdges.IsVisible = !maximized && w.WindowState != WindowState.FullScreen && resizeEdges.Children.Count > 0;
        }
        w.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty || e.Property == Window.OffScreenMarginProperty) Fit();
        };
        w.Opened += (_, _) => Fit();
        Fit();
    }

    public static void Toggle(Window w) => w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private static void AddResizeEdges(Window w, Panel edges)
    {
        const double t = 5, c = 10;
        void Edge(WindowEdge edge, StandardCursorType cursor, HorizontalAlignment h, VerticalAlignment v, double width, double height)
        {
            var b = new Border
            {
                Background = Brushes.Transparent,
                HorizontalAlignment = h,
                VerticalAlignment = v,
                Width = width,
                Height = height,
                Cursor = new Cursor(cursor),
            };
            b.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(b).Properties.IsLeftButtonPressed) return;
                try { w.BeginResizeDrag(edge, e); } catch { }
                e.Handled = true;
            };
            edges.Children.Add(b);
        }
        Edge(WindowEdge.North, StandardCursorType.TopSide, HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN, t);
        Edge(WindowEdge.South, StandardCursorType.BottomSide, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, t);
        Edge(WindowEdge.West, StandardCursorType.LeftSide, HorizontalAlignment.Left, VerticalAlignment.Stretch, t, double.NaN);
        Edge(WindowEdge.East, StandardCursorType.RightSide, HorizontalAlignment.Right, VerticalAlignment.Stretch, t, double.NaN);
        Edge(WindowEdge.NorthWest, StandardCursorType.TopLeftCorner, HorizontalAlignment.Left, VerticalAlignment.Top, c, c);
        Edge(WindowEdge.NorthEast, StandardCursorType.TopRightCorner, HorizontalAlignment.Right, VerticalAlignment.Top, c, c);
        Edge(WindowEdge.SouthWest, StandardCursorType.BottomLeftCorner, HorizontalAlignment.Left, VerticalAlignment.Bottom, c, c);
        Edge(WindowEdge.SouthEast, StandardCursorType.BottomRightCorner, HorizontalAlignment.Right, VerticalAlignment.Bottom, c, c);
        edges.IsVisible = true;
    }
}
