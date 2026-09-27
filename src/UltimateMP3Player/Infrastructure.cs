using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltimateMP3Player.Core;

namespace UltimateMP3Player;

// XAML text in the app language: {local:T 'Testo'}.
[MarkupExtensionReturnType(typeof(string))]
public sealed class T : MarkupExtension
{
    public T(string text) => Text = text;

    public string Text { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnChanged(name);
        return true;
    }

    protected void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void OnChanged(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _run;
    private readonly Func<object?, bool>? _can;

    public RelayCommand(Action run, Func<bool>? can = null) : this(_ => run(), can == null ? null : _ => can()) { }

    public RelayCommand(Action<object?> run, Func<object?, bool>? can = null)
    {
        _run = run;
        _can = can;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _can?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _run(parameter);
}

public sealed class Choice
{
    public Choice(string label, object? value = null, string? hint = null)
    {
        Label = label;
        Value = value;
        Hint = hint;
    }

    public string Label { get; }
    public object? Value { get; }
    public string? Hint { get; }
    public override string ToString() => Label;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NullToCollapsedInverseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null || (value is string s && s.Length == 0) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// Height → fully round ends (pill buttons of any height).
public sealed class PillRadiusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => new CornerRadius(value is double h && h > 0 ? h / 2 : 10);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

// Attached properties and helpers used by styles and views.
public static class Ui
{
    // App-wide switch for the light animations.
    public static bool Animations { get; set; } = true;

    public static readonly DependencyProperty IsHotProperty = DependencyProperty.RegisterAttached(
        "IsHot", typeof(bool), typeof(Ui), new PropertyMetadata(false));

    public static bool GetIsHot(DependencyObject o) => (bool)o.GetValue(IsHotProperty);
    public static void SetIsHot(DependencyObject o, bool value) => o.SetValue(IsHotProperty, value);

    public static readonly DependencyProperty HoverBrushProperty = DependencyProperty.RegisterAttached(
        "HoverBrush", typeof(Brush), typeof(Ui), new PropertyMetadata(null));

    public static Brush? GetHoverBrush(DependencyObject o) => (Brush?)o.GetValue(HoverBrushProperty);
    public static void SetHoverBrush(DependencyObject o, Brush? value) => o.SetValue(HoverBrushProperty, value);

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(Ui), new PropertyMetadata(null));

    public static string? GetGlyph(DependencyObject o) => (string?)o.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject o, string? value) => o.SetValue(GlyphProperty, value);

    // Soft right edge this many pixels wide: what runs past the element fades out instead of being cut.
    // In pixels of the element itself (a relative brush would follow the size of the overflowing content).
    public static readonly DependencyProperty FadeRightProperty = DependencyProperty.RegisterAttached(
        "FadeRight", typeof(double), typeof(Ui), new PropertyMetadata(0.0, OnFadeRight));

    public static double GetFadeRight(DependencyObject o) => (double)o.GetValue(FadeRightProperty);
    public static void SetFadeRight(DependencyObject o, double value) => o.SetValue(FadeRightProperty, value);

    private static void OnFadeRight(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.SizeChanged -= OnFadeSize;
        if ((double)e.NewValue > 0) fe.SizeChanged += OnFadeSize;
        ApplyFade(fe);
    }

    private static void OnFadeSize(object sender, SizeChangedEventArgs e) => ApplyFade((FrameworkElement)sender);

    private static void ApplyFade(FrameworkElement fe)
    {
        double w = fe.ActualWidth, fade = GetFadeRight(fe);
        if (fade <= 0 || w <= fade)
        {
            fe.ClearValue(UIElement.OpacityMaskProperty);
            return;
        }
        var mask = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(w, 0) };
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, (w - fade) / w));
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        mask.Freeze();
        fe.OpacityMask = mask;
    }

    public static Brush BrushFrom(string hex)
    {
        try
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
        catch { return Brushes.Gray; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // Dark frame, rounded corners on Windows 11.
    public static void DarkTitleBar(Window w)
    {
        try
        {
            var h = new WindowInteropHelper(w).Handle;
            int on = 1;
            if (DwmSetWindowAttribute(h, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(h, 19, ref on, sizeof(int));
            int caption = 0x000F0C0A; // COLORREF of #0A0C0F
            DwmSetWindowAttribute(h, 35, ref caption, sizeof(int));
            int text = 0x00F3EEEC;
            DwmSetWindowAttribute(h, 36, ref text, sizeof(int));
            int border = 0x00362C27;
            DwmSetWindowAttribute(h, 34, ref border, sizeof(int));
            int round = 2;
            DwmSetWindowAttribute(h, 33, ref round, sizeof(int));
        }
        catch { }
    }

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    // Gives memory back when the app goes to the tray.
    public static void TrimMemory()
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        try { EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle); } catch { }
    }

    public static TAnc? FindAncestor<TAnc>(DependencyObject? d) where TAnc : DependencyObject
    {
        while (d != null && d is not TAnc) d = Parent(d);
        return d as TAnc;
    }

    public static DependencyObject? Parent(DependencyObject d)
        => (d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : null) ?? LogicalTreeHelper.GetParent(d);

    public static bool IsInside(DependencyObject? d, DependencyObject ancestor)
    {
        for (; d != null; d = Parent(d))
            if (d == ancestor) return true;
        return false;
    }
}

// Covers from disk at display size, cached while in use.
public static class Images
{
    private static readonly Dictionary<string, WeakReference<BitmapSource>> Cache = new();
    private static readonly LinkedList<BitmapSource> Recent = new();
    private const int KeepStrong = 120;

    public static BitmapSource? TryGet(string path, int size, int version)
    {
        var key = $"{path}|{size}|{version}";
        lock (Cache)
            if (Cache.TryGetValue(key, out var w) && w.TryGetTarget(out var img)) return img;
        return null;
    }

    public static async Task<BitmapSource?> LoadAsync(string path, int size, int version)
    {
        var key = $"{path}|{size}|{version}";
        var hit = TryGet(path, size, version);
        if (hit != null) return hit;
        var img = await Task.Run(() => Decode(path, size));
        if (img == null) return null;
        lock (Cache)
        {
            Cache[key] = new WeakReference<BitmapSource>(img);
            Recent.AddFirst(img);
            while (Recent.Count > KeepStrong) Recent.RemoveLast();
            if (Cache.Count > 4000)
                foreach (var k in Cache.Where(kv => !kv.Value.TryGetTarget(out _)).Select(kv => kv.Key).ToList()) Cache.Remove(k);
        }
        return img;
    }

    public static BitmapSource? Decode(string path, int size)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache;
            bi.UriSource = new Uri(path);
            if (size > 0) bi.DecodePixelWidth = size;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    // Drops strong references when closed to the tray.
    public static void Release()
    {
        lock (Cache)
        {
            Recent.Clear();
            Cache.Clear();
        }
    }
}
