using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace UltimateMP3Player.Views;

// Borderless window with our own title bar (Discord style).
public static class WindowFrame
{
    public const double CaptionHeight = 32;

    private const int WM_NCHITTEST = 0x0084, WM_NCMOUSEMOVE = 0x00A0, WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2,
        WM_NCMOUSELEAVE = 0x02A2, HTMAXBUTTON = 9;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    public static void Apply(Window w, FrameworkElement root, Button? min, Button? max, Button close)
    {
        WindowChrome.SetWindowChrome(w, new WindowChrome
        {
            CaptionHeight = CaptionHeight,
            ResizeBorderThickness = w.ResizeMode == ResizeMode.NoResize ? new Thickness(0) : new Thickness(6),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        if (min != null) min.Click += (_, _) => w.WindowState = WindowState.Minimized;
        if (max != null) max.Click += (_, _) => Toggle(w);
        close.Click += (_, _) => w.Close();
        w.StateChanged += (_, _) => Fit(w, root, max);
        w.SourceInitialized += (_, _) =>
        {
            Ui.DarkTitleBar(w);
            Fit(w, root, max);
            if (max != null) HwndSource.FromHwnd(new WindowInteropHelper(w).Handle)?.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) => Hook(w, max, msg, wp, lp, ref handled));
        };
    }

    public static void Toggle(Window w) => w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // A maximized window overflows the screen by its invisible frame.
    private static void Fit(Window w, FrameworkElement root, Button? max)
    {
        if (max != null) max.Content = w.WindowState == WindowState.Maximized ? "" : "";
        if (w.WindowState != WindowState.Maximized)
        {
            root.Margin = new Thickness(0);
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(w);
        uint dpiValue = (uint)Math.Round(96 * dpi.DpiScaleX);
        double px;
        try { px = GetSystemMetricsForDpi(32, dpiValue) + GetSystemMetricsForDpi(92, dpiValue); }
        catch { px = 8 * dpi.DpiScaleX; }
        double m = px / dpi.DpiScaleX;
        root.Margin = new Thickness(m, m, m, m);
    }

    // Windows 11 snap layouts: the maximize button must answer HTMAXBUTTON.
    private static IntPtr Hook(Window w, Button max, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                if (Over(max, lp))
                {
                    Ui.SetIsHot(max, true);
                    handled = true;
                    return (IntPtr)HTMAXBUTTON;
                }
                Ui.SetIsHot(max, false);
                break;
            case WM_NCMOUSEMOVE when (int)wp != HTMAXBUTTON:
            case WM_NCMOUSELEAVE:
                Ui.SetIsHot(max, false);
                break;
            case WM_NCLBUTTONDOWN when (int)wp == HTMAXBUTTON:
                handled = true;
                break;
            case WM_NCLBUTTONUP when (int)wp == HTMAXBUTTON:
                handled = true;
                Ui.SetIsHot(max, false);
                Toggle(w);
                break;
        }
        return IntPtr.Zero;
    }

    private static bool Over(FrameworkElement e, IntPtr lp)
    {
        if (!e.IsVisible) return false;
        int x = unchecked((short)((long)lp & 0xFFFF)), y = unchecked((short)(((long)lp >> 16) & 0xFFFF));
        try
        {
            var p = e.PointFromScreen(new Point(x, y));
            return p.X >= 0 && p.Y >= 0 && p.X < e.ActualWidth && p.Y < e.ActualHeight;
        }
        catch { return false; }
    }
}
