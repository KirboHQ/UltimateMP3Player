using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using UltimateMP3Player.Core;

namespace UltimateMP3Player;

// XAML text in the app language: {local:T 'Testo'}.
public sealed class T : MarkupExtension
{
    public T() => Text = "";
    public T(string text) => Text = text;

    public string Text { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<TValue>(ref TValue field, TValue value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<TValue>.Default.Equals(field, value)) return false;
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

public sealed class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action<object?> _run;
    private readonly Func<object?, bool>? _can;

    public RelayCommand(Action run, Func<bool>? can = null) : this(_ => run(), can == null ? null : _ => can()) { }

    public RelayCommand(Action<object?> run, Func<object?, bool>? can = null)
    {
        _run = run;
        _can = can;
    }

    // Like WPF's: every command hears CommandManager.InvalidateRequerySuggested (the main window calls it after input too).
    public event EventHandler? CanExecuteChanged
    {
        add { if (_can != null) System.Windows.Input.CommandManager.RequerySuggested += value; }
        remove { if (_can != null) System.Windows.Input.CommandManager.RequerySuggested -= value; }
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

// ------------------------------------------------------------------ converters

public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

// Not null and not an empty string.
public sealed class HasValueConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is not null && !(value is string s && s.Length == 0)) != Invert;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ZeroConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is 0;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

// One value for true, another for false (an icon, a text).
public sealed class BoolChoice : IValueConverter
{
    public object? True { get; set; }
    public object? False { get; set; }
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? True : False;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

// Height → fully round ends (pill buttons of any height).
public sealed class PillRadiusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => new CornerRadius(value is double h && h > 0 ? h / 2 : 10);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

// A Segoe icon code from the shared code (e.g. the play/pause glyph) as the same icon of the bundled font.
public sealed class IconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is string s ? Icons.Map(s) : value;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

// The icon font a Segoe code needs (the filled one for the few filled icons).
public sealed class IconFontConverter : IValueConverter
{
    public static readonly IconFontConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Icons.FontFor(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

// {local:Icon E768}: a Segoe Fluent Icons code written as in the Windows app, drawn with the bundled icon font.
public sealed class Icon : MarkupExtension
{
    public Icon() => Code = "";
    public Icon(string code) => Code = code;

    public string Code { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) => Icons.Map(((char)System.Convert.ToInt32(Code, 16)).ToString());
}

// ------------------------------------------------------------------ helpers

// Attached properties and helpers used by styles and views.
public static class Ui
{
    // App-wide switch for the light animations.
    public static bool Animations { get; set; } = true;

    public static readonly AttachedProperty<IBrush?> HoverBrushProperty = AvaloniaProperty.RegisterAttached<Control, IBrush?>("HoverBrush", typeof(Ui));
    public static IBrush? GetHoverBrush(Control o) => o.GetValue(HoverBrushProperty);
    public static void SetHoverBrush(Control o, IBrush? value) => o.SetValue(HoverBrushProperty, value);

    public static readonly AttachedProperty<string?> GlyphProperty = AvaloniaProperty.RegisterAttached<Control, string?>("Glyph", typeof(Ui));
    public static string? GetGlyph(Control o) => o.GetValue(GlyphProperty);
    public static void SetGlyph(Control o, string? value) => o.SetValue(GlyphProperty, value);

    public static readonly AttachedProperty<bool> IsHotProperty = AvaloniaProperty.RegisterAttached<Control, bool>("IsHot", typeof(Ui));
    public static bool GetIsHot(Control o) => o.GetValue(IsHotProperty);
    public static void SetIsHot(Control o, bool value) => o.SetValue(IsHotProperty, value);

    // Soft right edge this many pixels wide: what runs past the element fades out instead of being cut.
    public static readonly AttachedProperty<double> FadeRightProperty = AvaloniaProperty.RegisterAttached<Control, double>("FadeRight", typeof(Ui));
    public static double GetFadeRight(Control o) => o.GetValue(FadeRightProperty);
    public static void SetFadeRight(Control o, double value) => o.SetValue(FadeRightProperty, value);

    // Icons drawn in grayscale like the Windows app's (no coloured fringes on the white play button).
    public static readonly AttachedProperty<bool> GrayTextProperty = AvaloniaProperty.RegisterAttached<Visual, bool>("GrayText", typeof(Ui));
    public static bool GetGrayText(Visual o) => o.GetValue(GrayTextProperty);
    public static void SetGrayText(Visual o, bool value) => o.SetValue(GrayTextProperty, value);

    static Ui()
    {
        GrayTextProperty.Changed.AddClassHandler<Visual>((v, e) =>
            RenderOptions.SetTextRenderingMode(v, e.NewValue is true ? TextRenderingMode.Antialias : TextRenderingMode.Unspecified));
        FadeRightProperty.Changed.AddClassHandler<Control>((c, _) =>
        {
            c.SizeChanged -= OnFadeSize;
            if (GetFadeRight(c) > 0)
            {
                c.SizeChanged += OnFadeSize;
                // A panel's content growing or shrinking (the tags of a row) decides it too.
                if (c is Panel p && !FadeWatched.TryGetValue(p, out var watched))
                {
                    FadeWatched.Add(p, p);
                    foreach (var child in p.Children) child.SizeChanged += (_, _) => ApplyFade(p);
                    p.Children.CollectionChanged += (_, e) =>
                    {
                        if (e.NewItems != null)
                            foreach (var child in e.NewItems.OfType<Control>()) child.SizeChanged += (_, _) => ApplyFade(p);
                        ApplyFade(p);
                    };
                }
            }
            ApplyFade(c);
        });
    }

    // The panels whose children are watched already (weak: rows let go of aren't kept).
    private static readonly ConditionalWeakTable<Panel, Panel> FadeWatched = new();

    private static void OnFadeSize(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Control c) ApplyFade(c);
    }

    // The mask is an offscreen layer at every frame (for each row of a list that has one): only while something really runs
    // past the edge (a panel's children wider than it).
    private static void ApplyFade(Control c)
    {
        double w = c.Bounds.Width, fade = GetFadeRight(c);
        bool overflows = c is not Panel p || p.Children.Any(ch => ch.IsVisible && ch.Bounds.Right > w + 0.5);
        if (fade <= 0 || w <= fade || !overflows)
        {
            c.OpacityMask = null;
            return;
        }
        c.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(w, 0, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(Colors.Black, 0), new GradientStop(Colors.Black, (w - fade) / w), new GradientStop(Colors.Transparent, 1) },
        };
    }

    public static IBrush BrushFrom(string hex)
    {
        try { return new SolidColorBrush(Color.Parse(hex)).ToImmutable(); }
        catch { return Brushes.Gray; }
    }

    public static Color ColorFrom(string hex)
    {
        try { return Color.Parse(hex); }
        catch { return Colors.Gray; }
    }

    public static IBrush Res(string key) => System.Windows.Application.Current.Resources[key] as IBrush ?? Brushes.Gray;

    // Any resource of the app (templates, themes of the controls).
    public static TRes Find<TRes>(string key) where TRes : class => (Application.Current!.FindResource(key) as TRes)!;
    public static Avalonia.Styling.ControlTheme Theme(string key) => Find<Avalonia.Styling.ControlTheme>(key);

    public static readonly Cursor Hand = new(StandardCursorType.Hand);

    // The app isn't on screen (another app in front on the phone, the window minimized): what waits to be shown (Later)
    // isn't looked at again until it's back, so the app really rests.
    private static bool _hidden;
    public static bool Hidden
    {
        get => _hidden;
        set
        {
            if (_hidden == value) return;
            _hidden = value;
            if (!value && Waiting.Count > 0 && _poll == null) _poll = Avalonia.Threading.DispatcherTimer.RunOnce(Poll, TimeSpan.FromMilliseconds(30));
        }
    }

    private static readonly List<Action> Waiting = new();
    private static IDisposable? _poll;

    // Something hidden that moves once it's shown (a spinner, a scrolling title): asked again in a while. One timer for
    // all of them, none while the app is hidden.
    public static void Later(Action check)
    {
        Waiting.Add(check);
        if (!_hidden && _poll == null) _poll = Avalonia.Threading.DispatcherTimer.RunOnce(Poll, TimeSpan.FromMilliseconds(300));
    }

    private static void Poll()
    {
        _poll = null;
        if (_hidden) return;
        var due = Waiting.ToList();
        Waiting.Clear();
        foreach (var check in due)
        {
            try { check(); }
            catch { }
        }
    }

    // A short animation on the render clock (Avalonia's Animation objects get in the way of the property changes they
    // happen next to): apply(eased progress 0..1) every frame for ms milliseconds.
    public static void Tween(Visual owner, double ms, Func<double, double> ease, Action<double> apply)
    {
        var top = TopLevel.GetTopLevel(owner);
        if (top == null || ms <= 0)
        {
            apply(1);
            return;
        }
        TimeSpan? start = null;
        void Frame(TimeSpan now)
        {
            start ??= now;
            double t = Math.Clamp((now - start.Value).TotalMilliseconds / ms, 0, 1);
            apply(ease(t));
            if (t < 1) top.RequestAnimationFrame(Frame);
        }
        apply(ease(0));
        top.RequestAnimationFrame(Frame);
    }

    public static double Linear(double t) => t;
    public static double CubicOut(double t) => 1 - Math.Pow(1 - t, 3);
    // WPF's BackEase (amplitude a), eased out: a little overshoot at the end.
    public static double BackOut(double t) => BackOut(t, 1);
    public static double BackOut(double t, double a)
    {
        double p = 1 - t;
        return 1 - (p * p * p - p * a * Math.Sin(Math.PI * p));
    }
    // WPF's BackEase eased in (it pulls back a little before going).
    public static double BackIn(double t, double a) => t * t * t - t * a * Math.Sin(Math.PI * t);
    public static double CubicInOut(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    // A TextBlock with one of the text classes of App.axaml (field-label, hint, section-title...).
    public static TextBlock Text(string text, string cls, Thickness margin = default)
    {
        var t = new TextBlock { Text = text, Margin = margin };
        t.Classes.Add(cls);
        return t;
    }

    private static TopLevel? Top => App.Host?.Window ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public static void CopyText(string text)
    {
        try { _ = Top?.Clipboard?.SetTextAsync(text); }
        catch { }
    }

    public static async Task<string?> ClipboardTextAsync()
    {
        try { return Top?.Clipboard is { } c ? await c.GetTextAsync() : null; }
        catch { return null; }
    }

    // The "Tag" column of the song lists exists only once there are tags.
    public static void SetTagColumn(bool on)
    {
        if (Application.Current is { } app) app.Resources["TagColumnWidth"] = new GridLength(on ? 1.3 : 0, GridUnitType.Star);
    }

    public static void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Open(dir);
        }
        catch { }
    }

    // The file selected in the file manager where the system can (macOS: Finder; Linux: the file manager over D-Bus, or its folder).
    public static void ShowInFolder(string file)
    {
        // (a phone has no file manager to open; and the Android app never starts programs through .NET, see ChildProcess)
        if (OperatingSystem.IsAndroid()) return;
        try
        {
            if (OperatingSystem.IsMacOS()) Process.Start("open", new[] { "-R", file });
            else if (OperatingSystem.IsWindows()) Process.Start("explorer.exe", $"/select,\"{file}\"");
            else if (!ShowItems(file)) Open(Path.GetDirectoryName(file)!);
        }
        catch { }
    }

    // org.freedesktop.FileManager1.ShowItems (Nautilus, Dolphin, Nemo, Thunar...).
    private static bool ShowItems(string file)
    {
        try
        {
            var uri = new Uri(file).AbsoluteUri;
            using var p = Process.Start(new ProcessStartInfo("dbus-send", new[]
            {
                "--session", "--print-reply", "--dest=org.freedesktop.FileManager1", "--type=method_call", "/org/freedesktop/FileManager1",
                "org.freedesktop.FileManager1.ShowItems", $"array:string:{uri}", "string:",
            }) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p == null) return false;
            p.WaitForExit(3000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch { return false; }
    }

    // A folder, a file or a link with the system's default program.
    public static void Open(string target)
    {
        if (OperatingSystem.IsAndroid()) return;
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", new[] { target });
            else Process.Start("xdg-open", new[] { target });
        }
        catch { }
    }

    // "Ubuntu 24.04.1 LTS", "macOS 15.1", for the bug reports.
    public static string SystemName
    {
        get
        {
            try
            {
#if ANDROID_APP
                return $"Android {Android.OS.Build.VERSION.Release} ({Android.OS.Build.Manufacturer} {Android.OS.Build.Model})";
#else
                if (OperatingSystem.IsMacOS()) return "macOS " + Environment.OSVersion.Version.ToString(3);
                if (OperatingSystem.IsLinux() && File.Exists("/etc/os-release"))
                {
                    var line = File.ReadAllLines("/etc/os-release").FirstOrDefault(l => l.StartsWith("PRETTY_NAME="));
                    if (line != null) return line[12..].Trim('"') + " (Linux)";
                }
#endif
            }
            catch { }
            return System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        }
    }

    public static T? FindAncestor<T>(Visual? v) where T : class
    {
        for (; v != null; v = v.GetVisualParent())
            if (v is T t) return t;
        return null;
    }
}

// A toolbar (a DockPanel of buttons on both sides) that fits its room instead of running its buttons under each other:
// the texts of its buttons (TextBlocks with the class "label"; the icon and the tooltip stay) go one at a time, lowest
// ToolbarPanel.Priority first (the same: the last one first), and if that's not enough an element with ToolbarPanel.Shrink
// (the search box) gets narrower, down to its MinWidth; all back when there's room again. A DockPanel hands each child only
// the room left, so their natural width is measured here, with no limit; the texts' width is worked out from the text,
// shown or not, so the choice doesn't depend on what's hidden now (no back and forth). (The same as the Windows app's.)
public sealed class ToolbarPanel : DockPanel
{
    public static readonly AttachedProperty<int> PriorityProperty = AvaloniaProperty.RegisterAttached<ToolbarPanel, Control, int>("Priority");
    public static int GetPriority(Control c) => c.GetValue(PriorityProperty);
    public static void SetPriority(Control c, int value) => c.SetValue(PriorityProperty, value);

    public static readonly AttachedProperty<bool> ShrinkProperty = AvaloniaProperty.RegisterAttached<ToolbarPanel, Control, bool>("Shrink");
    public static bool GetShrink(Control c) => c.GetValue(ShrinkProperty);
    public static void SetShrink(Control c, bool value) => c.SetValue(ShrinkProperty, value);

    // A filter's name: the first text to go, the last one while it filters.
    public static readonly IValueConverter KeepWhen = new FuncValueConverter<bool, int>(on => on ? 2 : -1);

    // The width of each shrinkable element as written (its Width is lowered while there's no room).
    private readonly Dictionary<Control, double> _widths = new();

    static ToolbarPanel()
    {
        // A text's priority changed (a filter turned on keeps its name longer): the toolbar decides again.
        PriorityProperty.Changed.AddClassHandler<Control>((c, _) => c.FindAncestorOfType<ToolbarPanel>()?.InvalidateMeasure());
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!double.IsInfinity(availableSize.Width)) Fit(availableSize.Width, availableSize.Height);
        return base.MeasureOverride(availableSize);
    }

    private void Fit(double room, double height)
    {
        var children = Children.Where(c => c.IsVisible).ToList();
        double need = 0;
        foreach (var c in children)
        {
            c.Measure(new Size(double.PositiveInfinity, height));
            need += c.DesiredSize.Width;
        }
        // The width with every text shown and nothing narrowed.
        var labels = new List<(TextBlock T, double W, int Order)>();
        double full = need;
        foreach (var c in children)
            foreach (var t in c.GetVisualDescendants().OfType<TextBlock>())
                if (t.Classes.Contains("label") && InShownPart(t, c))
                {
                    if (t.IsVisible) full -= t.DesiredSize.Width;
                    double w = TextWidth(t);
                    full += w;
                    labels.Add((t, w, labels.Count));
                }
        var shrink = children.Where(GetShrink).ToList();
        foreach (var e in shrink)
        {
            if (!_widths.ContainsKey(e) && !double.IsNaN(e.Width)) _widths[e] = e.Width;
            if (_widths.TryGetValue(e, out var own)) full += own - e.Width;
        }

        // What goes: texts first (lowest priority, the last first), then the shrinkable ones.
        double over = full - room;
        var hide = new HashSet<TextBlock>();
        foreach (var l in labels.OrderBy(l => GetPriority(l.T)).ThenByDescending(l => l.Order))
        {
            if (over <= 0.5) break;
            hide.Add(l.T);
            over -= l.W;
        }
        foreach (var l in labels)
        {
            bool visible = !hide.Contains(l.T);
            if (l.T.IsVisible != visible) l.T.IsVisible = visible;
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
    private static bool InShownPart(Visual t, Visual child)
    {
        for (var p = t.GetVisualParent(); p != null && p != child; p = p.GetVisualParent())
            if (!p.IsVisible) return false;
        return true;
    }

    private static double TextWidth(TextBlock t)
    {
        var text = new FormattedText(t.Text ?? "", CultureInfo.CurrentUICulture, t.FlowDirection,
            new Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch), t.FontSize, Brushes.Black);
        return Math.Ceiling(text.WidthIncludingTrailingWhitespace) + t.Margin.Left + t.Margin.Right;
    }
}

// The bundled icon font (Fluent System Icons, MIT) in place of Windows' Segoe Fluent Icons: the app keeps the Segoe
// codes everywhere (shared code, XAML written like the Windows one) and they're translated here, one by one, to the
// same icon of the Fluent set (Segoe Fluent Icons is drawn from it). Filled icons are in a second font.
public static class Icons
{
    public static readonly FontFamily Regular = new("avares://UltimateMP3Player/Assets/Fonts#FluentSystemIcons-Regular");
    public static readonly FontFamily Filled = new("avares://UltimateMP3Player/Assets/Fonts#FluentSystemIcons-Filled");

    // Segoe code → Fluent code (20 px regular icons; FilledCodes are in the filled font).
    private static readonly Dictionary<char, char> Map_ = new()
    {
        [''] = '', [''] = '', [''] = '', [''] = '', [''] = '', [''] = '',
        [''] = '', [''] = '', [''] = '', [''] = '', [''] = '', [''] = '',
        [''] = '', [''] = '', [''] = '', [''] = '', [''] = '', [''] = '',
        [''] = '', [''] = '', [''] = '', [''] = '', [''] = '', [''] = '',
        [''] = '', [''] = '', [''] = '', [''] = '', [''] = '', [''] = '',
        [''] = '', [''] = '', [''] = '', [''] = '', [''] = '', [''] = '',
        [''] = '', [''] = '', [''] = '', [''] = '', [''] = '', [''] = '',
        [''] = '', [''] = '', [''] = '', [''] = '', [''] = '', [''] = '',
        [''] = '', [''] = '', [''] = '', [''] = '', [''] = '', [''] = '',
        [''] = '', [''] = '', [''] = '', [''] = '', [''] = '', [''] = '',
        [''] = '', [''] = '', [''] = '', [''] = '', [''] = '', [''] = '',
        [''] = '', [''] = '', [''] = '', [''] = '', [''] = '', [''] = '',
        [''] = '', [''] = '', [''] = '', [''] = '', [''] = '', [''] = '',
        [''] = '', [''] = '', [''] = '', [''] = '', [''] = '', [''] = '',
        [''] = '', [''] = '', [''] = '', [''] = '',
    };

    // Segoe codes whose icon is in the filled font.
    private static readonly HashSet<char> FilledCodes = new() { '' };

    public static string Map(string s)
    {
        if (s.Length == 0) return s;
        Span<char> buf = s.Length <= 64 ? stackalloc char[s.Length] : new char[s.Length];
        bool changed = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is >= '' and <= '' && Map_.TryGetValue(c, out var m))
            {
                buf[i] = m;
                changed = true;
            }
            else buf[i] = c;
        }
        return changed ? new string(buf) : s;
    }

    // The font for a Segoe code (the filled one for the few filled icons).
    public static FontFamily FontFor(string? segoe) => segoe is { Length: > 0 } && FilledCodes.Contains(segoe[0]) ? Filled : Regular;

    public static bool IsFilled(string? segoe) => segoe is { Length: > 0 } && FilledCodes.Contains(segoe[0]);
}

// The app's resources and styles read again from their files (a language change: the texts in them follow it). The
// XAML compiler turns App.axaml's ResourceInclude/StyleInclude into the dictionaries themselves, so the files are named
// here. Everything is loaded first: if a file fails, the resources in use stay as they are.
public static class AppResources
{
    public static void Reload(IReadOnlyList<string> resources, IReadOnlyList<string> styles)
    {
        if (Application.Current is not { } app) return;
        var freshResources = resources.Select(s => (IResourceProvider)AvaloniaXamlLoader.Load(new Uri(s))).ToList();
        var freshStyles = styles.Select(s => (Avalonia.Styling.IStyle)AvaloniaXamlLoader.Load(new Uri(s))).ToList();
        var merged = app.Resources.MergedDictionaries;
        merged.Clear();
        foreach (var r in freshResources) merged.Add(r);
        // The Fluent theme comes first, the app's own styles last: those are replaced where they are.
        int start = app.Styles.Count - freshStyles.Count;
        for (int i = 0; i < freshStyles.Count && start >= 0; i++) app.Styles[start + i] = freshStyles[i];
    }
}

// Called once per frame while subscribed (the waveforms of the DJ).
public static class FrameClock
{
    private static readonly List<EventHandler> Handlers = new();
    private static bool _requested;

    public static void Add(EventHandler handler)
    {
        Handlers.Add(handler);
        Request();
    }

    public static void Remove(EventHandler handler) => Handlers.Remove(handler);

    private static void Request()
    {
        if (_requested || Handlers.Count == 0 || App.Host?.Window is not { } w) return;
        _requested = true;
        w.RequestAnimationFrame(_ =>
        {
            _requested = false;
            foreach (var h in Handlers.ToList()) h(null, EventArgs.Empty);
            Request();
        });
    }
}

// Covers from disk at display size, cached while in use: the ones used last stay in memory (up to KeepBytes), the others
// as long as something shows them. A small size of a big picture (a row's cover of a 3000-pixel original) is also kept on
// disk as a small file (ThumbDir): scrolling a long list decodes a few kilobytes per row instead of the whole original
// again, which on a phone made the list stutter.
public static class Images
{
    private sealed class Entry
    {
        public required string Key;
        public required Bitmap Image;
        public long Bytes;
    }

    private static readonly Dictionary<string, WeakReference<Bitmap>> Cache = new();
    private static readonly Dictionary<string, LinkedListNode<Entry>> Strong = new();
    private static readonly LinkedList<Entry> Recent = new();
    private const long KeepBytes = 40L << 20;
    private const int ThumbMax = 400;
    private static long _bytes;

    // Where the small copies go: a cache folder the system may empty (Android's, ~/.cache, ~/Library/Caches).
    public static string? ThumbDir { get; set; } = DefaultThumbDir();

    private static string? DefaultThumbDir()
    {
        try
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string root = !string.IsNullOrEmpty(xdg) ? xdg
                : OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Caches")
                : OperatingSystem.IsWindows() ? Path.GetTempPath()
                : Path.Combine(home, ".cache");
            return Path.Combine(root, "UltimateMP3Player", "thumbs");
        }
        catch { return null; }
    }

    private static string Key(string path, int size, int version) => string.Concat(path, "|", size.ToString(CultureInfo.InvariantCulture), "|", version.ToString(CultureInfo.InvariantCulture));

    public static Bitmap? TryGet(string path, int size, int version)
    {
        var key = Key(path, size, version);
        lock (Cache)
        {
            if (Strong.TryGetValue(key, out var node))
            {
                Recent.Remove(node);
                Recent.AddFirst(node);
                return node.Value.Image;
            }
            if (Cache.TryGetValue(key, out var w) && w.TryGetTarget(out var img))
            {
                Keep(key, img);
                return img;
            }
        }
        return null;
    }

    public static async Task<Bitmap?> LoadAsync(string path, int size, int version)
    {
        var hit = TryGet(path, size, version);
        if (hit != null) return hit;
        var img = await Task.Run(() => Decode(path, size));
        if (img == null) return null;
        var key = Key(path, size, version);
        lock (Cache)
        {
            Cache[key] = new WeakReference<Bitmap>(img);
            Keep(key, img);
            if (Cache.Count > 4000)
                foreach (var k in Cache.Where(kv => !kv.Value.TryGetTarget(out _)).Select(kv => kv.Key).ToList()) Cache.Remove(k);
        }
        return img;
    }

    // (under the lock) Among the ones kept, first; the oldest go beyond the budget.
    private static void Keep(string key, Bitmap img)
    {
        if (Strong.TryGetValue(key, out var old))
        {
            Recent.Remove(old);
            _bytes -= old.Value.Bytes;
        }
        var e = new Entry { Key = key, Image = img, Bytes = Math.Max(1L, (long)img.PixelSize.Width * img.PixelSize.Height * 4) };
        Strong[key] = Recent.AddFirst(e);
        _bytes += e.Bytes;
        while (_bytes > KeepBytes && Recent.Count > 1 && Recent.Last is { } last)
        {
            Recent.RemoveLast();
            Strong.Remove(last.Value.Key);
            _bytes -= last.Value.Bytes;
        }
    }

    public static Bitmap? Decode(string path, int size)
    {
        try
        {
            if (!File.Exists(path)) return null;
            if (size > 0 && size <= ThumbMax && ThumbDir != null && Thumbnail(path, size) is { } small) return small;
            using var s = File.OpenRead(path);
            return size > 0 ? Bitmap.DecodeToWidth(s, size, BitmapInterpolationMode.HighQuality) : new Bitmap(s);
        }
        catch { return null; }
    }

    // The small copy of a big picture: read if it's newer than the picture, otherwise made (decoded at a fraction of its
    // size, as JPEG allows, then scaled with care) and saved for the next time. Null: the picture is small already.
    private static Bitmap? Thumbnail(string path, int size)
    {
        var thumbs = ThumbDir!;
        string thumb = Path.Combine(thumbs, $"{Hash(path):x16}-{size}");
        try
        {
            var srcTime = File.GetLastWriteTimeUtc(path);
            foreach (var ext in new[] { ".jpg", ".png" })
            {
                var f = thumb + ext;
                if (File.Exists(f) && File.GetLastWriteTimeUtc(f) >= srcTime)
                {
                    using var ts = File.OpenRead(f);
                    return new Bitmap(ts);
                }
            }
        }
        catch { }

        using var codec = SKCodec.Create(path);
        if (codec == null) return null;
        var info = codec.Info;
        if (info.Width <= size * 1.25 || info.Width <= 0 || info.Height <= 0) return null;
        int h = Math.Max(1, (int)Math.Round(info.Height * (size / (double)info.Width)));
        var near = codec.GetScaledDimensions(size / (float)info.Width);
        using var decoded = SKBitmap.Decode(codec, new SKImageInfo(near.Width, near.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (decoded == null) return null;
#pragma warning disable CS0618
        using var scaled = decoded.Width == size && decoded.Height == h ? decoded.Copy() : decoded.Resize(new SKImageInfo(size, h, SKColorType.Rgba8888, SKAlphaType.Premul), SKFilterQuality.High);
#pragma warning restore CS0618
        if (scaled == null) return null;
        bool alpha = info.AlphaType != SKAlphaType.Opaque;
        try
        {
            Directory.CreateDirectory(thumbs);
            using var img = SKImage.FromBitmap(scaled);
            using var data = alpha ? img.Encode(SKEncodedImageFormat.Png, 100) : img.Encode(SKEncodedImageFormat.Jpeg, 90);
            if (data != null)
            {
                // Written aside, then put in place: another list asking for the same cover meanwhile reads a whole file.
                var target = thumb + (alpha ? ".png" : ".jpg");
                var part = $"{target}.{Environment.CurrentManagedThreadId}.part";
                using (var fs = File.Create(part)) data.SaveTo(fs);
                File.Move(part, target, true);
            }
        }
        catch { }
        return new Bitmap(Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Premul, scaled.GetPixels(),
            new PixelSize(scaled.Width, scaled.Height), new Vector(96, 96), scaled.RowBytes);
    }

    // FNV-1a over the path: the same name for the same file at every start.
    private static ulong Hash(string s)
    {
        ulong h = 14695981039346656037;
        foreach (char c in s)
        {
            h ^= c;
            h *= 1099511628211;
        }
        return h;
    }

    // Drops strong references when closed to the tray.
    public static void Release()
    {
        lock (Cache)
        {
            Recent.Clear();
            Strong.Clear();
            Cache.Clear();
            _bytes = 0;
        }
    }

    // A picture in memory (a web thumbnail, a cover inside a pack, an avatar), width = 0: its own size.
    public static Bitmap? FromBytes(byte[]? data, int width)
    {
        if (data is not { Length: > 0 }) return null;
        try
        {
            using var ms = new MemoryStream(data);
            if (width <= 0) return new Bitmap(ms);
            using var probe = SKBitmap.Decode(data);
            if (probe == null) return null;
            ms.Position = 0;
            return probe.Width > width ? Bitmap.DecodeToWidth(ms, width, BitmapInterpolationMode.HighQuality) : new Bitmap(ms);
        }
        catch { return null; }
    }

    // A picture file as a JPEG this wide (covers sent to a room).
    public static byte[]? Jpeg(string path, int size)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var src = SKBitmap.Decode(path);
            if (src == null) return null;
            return Encode(src, size);
        }
        catch { return null; }
    }

    // A site's picture as a square cover (the middle of a 16:9 video thumbnail).
    public static byte[]? SquareJpeg(byte[] data, int size)
    {
        try
        {
            using var src = SKBitmap.Decode(data);
            if (src == null) return null;
            int side = Math.Min(src.Width, src.Height);
            using var square = new SKBitmap(side, side);
            src.ExtractSubset(square, SKRectI.Create((src.Width - side) / 2, (src.Height - side) / 2, side, side));
            return Encode(square, Math.Min(size, side));
        }
        catch { return null; }
    }

    private static byte[]? Encode(SKBitmap src, int width)
    {
        var target = src;
        if (src.Width > width)
        {
            int h = Math.Max(1, (int)Math.Round(src.Height * (width / (double)src.Width)));
#pragma warning disable CS0618
            target = src.Resize(new SKImageInfo(width, h), SKFilterQuality.High) ?? src;
#pragma warning restore CS0618
        }
        try
        {
            using var img = SKImage.FromBitmap(target);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
            return data.ToArray();
        }
        finally
        {
            if (!ReferenceEquals(target, src)) target.Dispose();
        }
    }
}
