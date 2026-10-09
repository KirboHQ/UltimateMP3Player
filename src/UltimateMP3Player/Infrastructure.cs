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

// {local:HoverBrushBinding}: the templated control's Ui.HoverBrush, for the hover triggers of the button templates. Built
// from the property itself: the same {Binding (local:Ui.HoverBrush)} written in XAML is resolved through the XAML
// namespaces of the window, which the dialogs built in code don't have (it failed there, and the button lost its background).
[MarkupExtensionReturnType(typeof(Binding))]
public sealed class HoverBrushBinding : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding { Path = new PropertyPath(Ui.HoverBrushProperty), RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) };
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

// A toolbar (a DockPanel of buttons on both sides) that fits its room instead of running its buttons under each other:
// the texts of its buttons (TextBlocks with ToolbarPanel.Label; the icon and the tooltip stay) go one at a time, lowest
// ToolbarPanel.Priority first (the same: the last one first), and if that's not enough an element with ToolbarPanel.Shrink
// (the search box) gets narrower, down to its MinWidth; all back when there's room again. A DockPanel hands each child only
// the room left, so their natural width is measured here, with no limit; the texts' width is worked out from the text,
// shown or not, so the choice doesn't depend on what's hidden now (no back and forth). (The same as the Linux/macOS app's.)
public sealed class ToolbarPanel : System.Windows.Controls.DockPanel
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.RegisterAttached(
        "Label", typeof(bool), typeof(ToolbarPanel), new PropertyMetadata(false, (d, _) => Remeasure(d)));

    public static bool GetLabel(DependencyObject o) => (bool)o.GetValue(LabelProperty);
    public static void SetLabel(DependencyObject o, bool value) => o.SetValue(LabelProperty, value);

    public static readonly DependencyProperty PriorityProperty = DependencyProperty.RegisterAttached(
        "Priority", typeof(int), typeof(ToolbarPanel), new PropertyMetadata(0, (d, _) => Remeasure(d)));

    public static int GetPriority(DependencyObject o) => (int)o.GetValue(PriorityProperty);
    public static void SetPriority(DependencyObject o, int value) => o.SetValue(PriorityProperty, value);

    public static readonly DependencyProperty ShrinkProperty = DependencyProperty.RegisterAttached(
        "Shrink", typeof(bool), typeof(ToolbarPanel), new PropertyMetadata(false));

    public static bool GetShrink(DependencyObject o) => (bool)o.GetValue(ShrinkProperty);
    public static void SetShrink(DependencyObject o, bool value) => o.SetValue(ShrinkProperty, value);

    // The width of each shrinkable element as written (its Width is lowered while there's no room).
    private readonly Dictionary<FrameworkElement, double> _widths = new();

    // A text's priority changed (a filter turned on keeps its name longer): the toolbar decides again.
    private static void Remeasure(DependencyObject d)
    {
        for (var p = d; p != null; p = VisualTreeHelper.GetParent(p))
            if (p is ToolbarPanel bar)
            {
                bar.InvalidateMeasure();
                return;
            }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!double.IsInfinity(availableSize.Width)) Fit(availableSize.Width, availableSize.Height);
        return base.MeasureOverride(availableSize);
    }

    private void Fit(double room, double height)
    {
        var children = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        double need = 0;
        foreach (var c in children)
        {
            c.Measure(new Size(double.PositiveInfinity, height));
            need += c.DesiredSize.Width;
        }
        // The width with every text shown and nothing narrowed.
        var labels = new List<(System.Windows.Controls.TextBlock T, double W, int Order)>();
        double full = need;
        foreach (var c in children)
            foreach (var t in Descendants(c).OfType<System.Windows.Controls.TextBlock>())
                if (GetLabel(t) && InShownPart(t, c))
                {
                    if (t.Visibility == Visibility.Visible) full -= t.DesiredSize.Width;
                    double w = TextWidth(t);
                    full += w;
                    labels.Add((t, w, labels.Count));
                }
        var shrink = children.OfType<FrameworkElement>().Where(GetShrink).ToList();
        foreach (var e in shrink)
        {
            if (!_widths.ContainsKey(e) && !double.IsNaN(e.Width)) _widths[e] = e.Width;
            if (_widths.TryGetValue(e, out var own)) full += own - e.Width;
        }

        // What goes: texts first (lowest priority, the last first), then the shrinkable ones.
        double over = full - room;
        var hide = new HashSet<System.Windows.Controls.TextBlock>();
        foreach (var l in labels.OrderBy(l => GetPriority(l.T)).ThenByDescending(l => l.Order))
        {
            if (over <= 0.5) break;
            hide.Add(l.T);
            over -= l.W;
        }
        foreach (var l in labels)
        {
            var v = hide.Contains(l.T) ? Visibility.Collapsed : Visibility.Visible;
            if (l.T.Visibility != v) l.T.Visibility = v;
        }
        foreach (var e in shrink)
        {
            if (!_widths.TryGetValue(e, out var own)) continue;
            double w = over > 0.5 ? Math.Max(e.MinWidth, own - over) : own;
            over -= own - w;
            if (Math.Abs(e.Width - w) > 0.5) e.Width = w;
        }
    }

    // Not inside a part of the button that's hidden anyway.
    private static bool InShownPart(DependencyObject t, DependencyObject child)
    {
        for (var p = VisualTreeHelper.GetParent(t); p != null && p != child; p = VisualTreeHelper.GetParent(p))
            if (p is UIElement u && u.Visibility == Visibility.Collapsed) return false;
        return true;
    }

    private double TextWidth(System.Windows.Controls.TextBlock t)
    {
        var text = new FormattedText(t.Text ?? "", CultureInfo.CurrentUICulture, t.FlowDirection,
            new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch), t.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return Math.Ceiling(text.WidthIncludingTrailingWhitespace) + t.Margin.Left + t.Margin.Right;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject d)
    {
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(d); i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            yield return c;
            foreach (var g in Descendants(c)) yield return g;
        }
    }
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

    // ------------------------------------------------------------------ what the view models ask of the platform

    public static Color ColorFrom(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Colors.Gray; }
    }

    public static void CopyText(string text)
    {
        try { Clipboard.SetText(text); }
        catch { }
    }

    public static Task<string?> ClipboardTextAsync()
    {
        try { return Task.FromResult(Clipboard.ContainsText() ? Clipboard.GetText() : null); }
        catch { return Task.FromResult<string?>(null); }
    }

    // The "Tag" column of the song lists exists only once there are tags.
    public static void SetTagColumn(bool on) => Application.Current.Resources["TagColumnWidth"] = new GridLength(on ? 1.3 : 0, GridUnitType.Star);

    public static void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch { }
    }

    public static void ShowInFolder(string file)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{file}\""); }
        catch { }
    }

    // "Windows 11 (build 26200)", for the bug reports.
    public static string SystemName
    {
        get
        {
            var os = Environment.OSVersion.Version;
            string windows = os.Major == 10 && os.Build >= 22000 ? "Windows 11" : os.Major == 10 ? "Windows 10" : "Windows " + os;
            return $"{windows} (build {os.Build})";
        }
    }
}

// Called once per frame while subscribed (the waveforms of the DJ).
public static class FrameClock
{
    public static void Add(EventHandler handler) => CompositionTarget.Rendering += handler;
    public static void Remove(EventHandler handler) => CompositionTarget.Rendering -= handler;
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

    // A picture in memory (a web thumbnail, a cover inside a pack, an avatar), width = 0: its own size.
    public static BitmapSource? FromBytes(byte[]? data, int width)
    {
        if (data is not { Length: > 0 }) return null;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.StreamSource = new MemoryStream(data);
            if (width > 0) bi.DecodePixelWidth = width;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    // A picture file as a JPEG this wide (covers sent to a room).
    public static byte[]? Jpeg(string path, int size)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.UriSource = new Uri(path);
            bi.DecodePixelWidth = size;
            bi.EndInit();
            bi.Freeze();
            var enc = new JpegBitmapEncoder { QualityLevel = 85 };
            enc.Frames.Add(BitmapFrame.Create(bi));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    // A site's picture as a square cover (the middle of a 16:9 video thumbnail).
    public static byte[]? SquareJpeg(byte[] data, int size)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.StreamSource = new MemoryStream(data);
            bi.EndInit();
            int side = Math.Min(bi.PixelWidth, bi.PixelHeight);
            BitmapSource square = new CroppedBitmap(bi, new Int32Rect((bi.PixelWidth - side) / 2, (bi.PixelHeight - side) / 2, side, side));
            if (side > size) square = new TransformedBitmap(square, new ScaleTransform(size / (double)side, size / (double)side));
            var enc = new JpegBitmapEncoder { QualityLevel = 85 };
            enc.Frames.Add(BitmapFrame.Create(square));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
