using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using UltimateMP3Player.Core;

namespace UltimateMP3Player;

// The colours of the app (Core ThemeColors) as resources; views use them via DynamicResource. Both looks are in the
// application's theme dictionaries: Avalonia shows the one of the device (or the chosen one) and switches by itself when
// the device changes while the app is open.
public static class Themes
{
    private static Dictionary<string, uint> _dark = ThemeColors.Build(ThemeColors.All[0], false), _light = ThemeColors.Build(ThemeColors.All[0], true);
    private static bool _watching, _followsDevice;

    // The light or dark look changed (another choice, the device switched): what draws its own colours redraws.
    public static event Action? Changed;

    // The accent moved to another song's (what draws it by itself looks again).
    public static event Action? AccentChanged;

    // How long the colours take from one song to the next.
    public const double FadeMs = 700;

    // Android: the device's own look, chosen here rather than left to Avalonia (its window stops telling once it has shown
    // one). Null elsewhere: Avalonia follows the device by itself.
    public static Func<ThemeVariant>? DeviceLook;

    public static bool IsLight => Application.Current?.ActualThemeVariant == ThemeVariant.Light;

    public static void Apply(string? id, string profileColor, string? mode, string? songColors = null)
    {
        if (Application.Current is not { } app) return;
        _rgbChosen = id == ThemeColors.RgbId;
        var t = ThemeColors.Resolve(id, profileColor, songColors);
        _dark = ThemeColors.Build(t, false);
        _light = ThemeColors.Build(t, true);
        app.Resources.ThemeDictionaries[ThemeVariant.Dark] = Dictionary(_dark, false);
        app.Resources.ThemeDictionaries[ThemeVariant.Light] = Dictionary(_light, true);
        if (!_watching)
        {
            _watching = true;
            app.ActualThemeVariantChanged += (_, _) => Restyle();
        }
        _followsDevice = mode != ThemeColors.Light && mode != ThemeColors.Dark;
        var variant = mode == ThemeColors.Light ? ThemeVariant.Light : mode == ThemeColors.Dark ? ThemeVariant.Dark : DeviceLook?.Invoke() ?? ThemeVariant.Default;
        app.RequestedThemeVariant = variant;
        Restyle();
        RunRgb();
    }

    // The theme RGB: the accent takes its next colours in place, only while the window can be seen. (Made here: the platform is up by now.)
    private static bool _rgbChosen;
    private static Avalonia.Threading.DispatcherTimer? _rgb;

    public static bool IsRgb => _rgbChosen;

    public static void RunRgb()
    {
        if (_rgb == null)
        {
            if (!_rgbChosen) return;
            _rgb = new Avalonia.Threading.DispatcherTimer();
            _rgb.Tick += (_, _) => StepRgb();
            Ui.HiddenChanged += RunRgb;
        }
        _rgb.Interval = TimeSpan.FromMilliseconds(ThemeColors.RgbStepMs);
        _rgb.IsEnabled = _rgbChosen && !Ui.Hidden;
    }

    private static void StepRgb()
    {
        if (Ui.Animations) PaintAccent(ThemeColors.Rgb());
    }

    // Other colours on the accent's brushes, in place. (Only the look showing: the other one is made when it's shown.)
    public static void PaintAccent(AccentTheme theme)
    {
        var colors = ThemeColors.Build(theme, IsLight);
        if (IsLight) _light = colors;
        else _dark = colors;
        Paint(colors);
        AccentChanged?.Invoke();
    }

    // (the live brushes first: what redraws on Changed may read them)
    private static void Restyle()
    {
        _fade++;
        Paint(IsLight ? _light : _dark);
        if (Application.Current is { } app)
            foreach (var (key, live) in _live)
                if (app.TryFindResource(key, app.ActualThemeVariant, out var v) && v is IBrush now) Copy(now, live);
        Changed?.Invoke();
    }

    // Brushes looked up by code (the phone's sheets and pages built in code, the dialogs) don't follow a DynamicResource and
    // stayed in the old look while open: each key hands out one brush that takes the new colours in place.
    private static readonly Dictionary<string, IBrush> _live = new();

    public static object? Live(string key, object? found)
    {
        if (found is not IBrush b || !Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) return found;
        // (the accent's take their colours in place already)
        if (b is SolidColorBrush solid && _moving.ContainsValue(solid) || _movingGradients.Contains(b)) return found;
        if (!_live.TryGetValue(key, out var live)) _live[key] = live = Copy(b, null);
        return live;
    }

    // Copies a brush's colours into a changeable one (a new one when there's none yet); other kinds stay as they are.
    private static IBrush Copy(IBrush from, IBrush? into)
    {
        switch (from)
        {
            case ISolidColorBrush s:
                var solid = into as SolidColorBrush ?? new SolidColorBrush();
                solid.Color = s.Color;
                solid.Opacity = s.Opacity;
                return solid;
            case ILinearGradientBrush g:
                var linear = into as LinearGradientBrush ?? new LinearGradientBrush();
                linear.StartPoint = g.StartPoint;
                linear.EndPoint = g.EndPoint;
                linear.Opacity = g.Opacity;
                var stops = new GradientStops();
                foreach (var stop in g.GradientStops) stops.Add(new GradientStop(stop.Color, stop.Offset));
                linear.GradientStops = stops;
                return linear;
            default:
                return into ?? from;
        }
    }

    // The accent's brushes stay the same objects, in both looks and with every theme: nothing in the windows has to look them up again when
    // they change, only their colours move, and nothing runs once they've arrived.
    // (No Avalonia object in a static field here: the phones touch this class before the platform is up, and an AvaloniaObject made
    // that early leaves the UI thread with a dispatcher that never runs anything: a blank screen.)
    private static readonly Dictionary<string, SolidColorBrush> _moving = new();
    private static readonly Dictionary<string, IBrush> _gradients = new();
    private static readonly HashSet<IBrush> _movingGradients = new();
    private static readonly List<(GradientStop Stop, string Key)> _stops = new();
    private static int _fade;

    private static SolidColorBrush Moving(string key)
    {
        if (!_moving.TryGetValue(key, out var brush)) _moving[key] = brush = new SolidColorBrush();
        return brush;
    }

    private static IBrush MovingGradient(string name, string from, string to, RelativePoint start, RelativePoint end, double opacity = 1)
    {
        if (_gradients.TryGetValue(name, out var kept)) return kept;
        GradientStop a = new(default, 0), b = new(default, 1);
        _stops.Add((a, from));
        _stops.Add((b, to));
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end, Opacity = opacity, GradientStops = { a, b } };
        _movingGradients.Add(brush);
        return _gradients[name] = brush;
    }

    private static void Paint(Dictionary<string, uint> colors)
    {
        foreach (var (key, brush) in _moving) brush.Color = ToColor(colors[key]);
        foreach (var (stop, key) in _stops) stop.Color = ToColor(colors[key]);
        Painted?.Invoke();
    }

    // A gradient's stops moving tell nobody: what reads them while drawing has to draw again.
    public static event Action? Painted;

    private static Visual? Root() => Application.Current?.ApplicationLifetime switch
    {
        IClassicDesktopStyleApplicationLifetime d => d.MainWindow ?? d.Windows.FirstOrDefault(),
        ISingleViewApplicationLifetime s => s.MainView,
        _ => null,
    };

    // To the colours of another song. Off the screen or without animations, straight there.
    public static void FadeTo(AccentTheme theme)
    {
        _dark = ThemeColors.Build(theme, false);
        _light = ThemeColors.Build(theme, true);
        var colors = IsLight ? _light : _dark;
        int run = ++_fade;
        if (!Ui.Animations || Ui.Hidden || Root() is not { } root) Paint(colors);
        else
        {
            var brushes = _moving.Select(m => (m.Value, From: m.Value.Color, To: ToColor(colors[m.Key]))).ToArray();
            var stops = _stops.Select(s => (s.Stop, From: s.Stop.Color, To: ToColor(colors[s.Key]))).ToArray();
            Ui.Tween(root, FadeMs, Ui.CubicInOut, k =>
            {
                if (run != _fade) return;
                foreach (var (brush, from, to) in brushes) brush.Color = Between(from, to, k);
                foreach (var (stop, from, to) in stops) stop.Color = Between(from, to, k);
            });
        }
        AccentChanged?.Invoke();
    }

    public static Color Between(Color a, Color b, double k)
    {
        static byte Ch(byte x, byte y, double k) => (byte)Math.Round(x + (y - x) * k);
        return Color.FromArgb(Ch(a.A, b.A, k), Ch(a.R, b.R, k), Ch(a.G, b.G, k), Ch(a.B, b.B, k));
    }

    // The glass look's picture behind the window (ViewModels/GlassBackdrop; null: no glass), and its grain, tiled over it.
    public static IImage? Backdrop { get; private set; }
    public static event Action? BackdropChanged;

    // fade: the new picture comes up over the old one; not as the accent steps (RGB, the custom colour being chosen), at once.
    public static bool BackdropFades { get; private set; } = true;

    // (the colour along its top: Glass.TopColor)
    public static Color BackdropTop { get; private set; }

    public static void ShowBackdrop(byte[]? px, bool fade = true)
    {
        Backdrop = px == null ? null : BitmapOf(px, Glass.Size);
        if (px != null) BackdropTop = ToColor(Glass.TopColor(px));
        BackdropFades = fade;
        BackdropChanged?.Invoke();
    }

    // The picture in a window's (or a page's) layers, its grain over it: from one song to the next the new one comes up over the old one,
    // as the song page's cover does. (Desktop MainWindow, the phone's MainView and PlayerPage.)
    public static void PutBackdrop(Panel back, Image before, Image now, Border grain)
    {
        var next = Backdrop;
        back.IsVisible = next != null;
        if (grain.Background == null) FitGrain(grain);
        if (ReferenceEquals(next, now.Source)) return;
        before.Source = now.Source;
        now.Source = next;
        now.Opacity = 1;
        if (next == null || before.Source == null || !BackdropFades || !Ui.Animations || !back.IsEffectivelyVisible) return;
        now.Opacity = 0;
        Ui.Tween(now, FadeMs, Ui.CubicInOut, t => now.Opacity = t);
    }

    // Avalonia draws a tiled bitmap one of its pixels to one pixel of the screen: a tile of GrainSize points on a scaled screen
    // (phones, Retina, 150%) was bigger than the picture, squares of grain with gaps between. The tile is GrainSize pixels.
    private static void FitGrain(Border grain)
    {
        TopLevel? top = null;
        void Fit(object? sender, EventArgs e) => grain.Background = GrainFor(top?.RenderScaling ?? 1);
        grain.AttachedToVisualTree += (_, _) =>
        {
            if (top != null) top.ScalingChanged -= Fit;
            top = TopLevel.GetTopLevel(grain);
            if (top != null) top.ScalingChanged += Fit;
            Fit(null, EventArgs.Empty);
        };
        Fit(null, EventArgs.Empty);
    }

    private static readonly Dictionary<double, IBrush> Grains = new();

    private static IBrush GrainFor(double scale)
    {
        if (Grains.TryGetValue(scale, out var brush)) return brush;
        var side = Glass.GrainSize / scale;
        return Grains[scale] = new ImageBrush(BitmapOf(Glass.GrainPixels(), Glass.GrainSize))
        {
            TileMode = TileMode.Tile, Stretch = Stretch.Fill,
            DestinationRect = new RelativeRect(0, 0, side, side, RelativeUnit.Absolute),
        };
    }

    // (blue, green, red, alpha, as Core makes them: Skia takes them on every system)
    private static Bitmap BitmapOf(byte[] px, int side)
    {
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(px, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Unpremul, pin.AddrOfPinnedObject(), new PixelSize(side, side), new Vector(96, 96), side * 4); }
        finally { pin.Free(); }
    }

    // A cover small, as blue, green, red, alpha.
    public static (byte[] Bgra, int Width, int Height)? PixelsOf(string file, int width = 28)
    {
        if (!File.Exists(file)) return null;
        using var stream = File.OpenRead(file);
        using var small = Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.MediumQuality);
        int w = small.PixelSize.Width, h = small.PixelSize.Height;
        var px = new byte[w * h * 4];
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(px, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { small.CopyPixels(new PixelRect(0, 0, w, h), pin.AddrOfPinnedObject(), px.Length, w * 4); }
        finally { pin.Free(); }
        // (Android's bitmaps are RGBA)
        if (small.Format != PixelFormat.Bgra8888)
            for (int i = 0; i < px.Length; i += 4) (px[i], px[i + 2]) = (px[i + 2], px[i]);
        return (px, w, h);
    }

    // "From the song": no colour of its own, so all of them.
    public static IBrush SongSwatch() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Parse("#FF4D6D"), 0), new GradientStop(Parse("#FFC24D"), 0.33), new GradientStop(Parse("#4DE08A"), 0.66), new GradientStop(Parse("#4DB8FF"), 1) },
    }.ToImmutable();

    // RGB: the whole round.
    public static IBrush RgbSwatch() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Parse("#FF3B3B"), 0), new GradientStop(Parse("#FFD93B"), 0.2), new GradientStop(Parse("#3BFF6B"), 0.4),
            new GradientStop(Parse("#3BE0FF"), 0.6), new GradientStop(Parse("#4B3BFF"), 0.8), new GradientStop(Parse("#FF3BD8"), 1),
        },
    }.ToImmutable();

    // The device switched light or dark (Android): the app follows when the profile says "same as the device".
    public static void DeviceChanged()
    {
        if (_followsDevice && DeviceLook != null && Application.Current is { } app) app.RequestedThemeVariant = DeviceLook();
    }

    // A colour of the look showing now (code that draws by itself).
    public static Color Get(string name) => ToColor((IsLight ? _light : _dark).TryGetValue(name, out var c) ? c : 0xFF808080);

    public static IBrush Swatch(AccentTheme t)
        => IsLight ? Gradient(Parse(t.LightA), Parse(t.LightB)) : Gradient(Parse(t.A), Parse(t.B));

    private static ResourceDictionary Dictionary(Dictionary<string, uint> c, bool light)
    {
        var r = new ResourceDictionary();
        foreach (var (name, argb) in c)
        {
            r[name + "Color"] = ToColor(argb);
            r[name + "Brush"] = ThemeColors.AccentKeys.Contains(name) ? Moving(name) : Solid(ToColor(argb));
        }
        Color C(string name) => ToColor(c[name]);
        Color bg = C("Bg");
        RelativePoint top = new(0, 0, RelativeUnit.Relative), bottom = new(0, 1, RelativeUnit.Relative);
        RelativePoint corner = new(1, 1, RelativeUnit.Relative);
        r["AccentGradient"] = MovingGradient("AccentGradient", "Accent", "Accent2", top, corner);
        r["AccentGradientHover"] = MovingGradient("AccentGradientHover", "AccentHover", "Accent2Hover", top, corner);
        r["ProgressGradient"] = MovingGradient("ProgressGradient", "Accent", "Accent2", top, new RelativePoint(1, 0, RelativeUnit.Relative));
        r["FavoritesGradient"] = MovingGradient("FavoritesGradient", "Fav1", "Fav2", top, corner);
        r["PlaceholderGradient"] = Gradient(C("Placeholder1"), C("Placeholder2"));
        r["HeaderGradient"] = MovingGradient("HeaderGradient", "HeaderTop", "Ground", top, bottom);
        // (see Theme.axaml: the opacity inside the brush, no layer)
        r["HeaderGlow"] = MovingGradient("HeaderGlow", "HeaderTop", "Ground", top, bottom, 0.55);
        r["HeaderGlowStrong"] = MovingGradient("HeaderGlowStrong", "HeaderTop", "Ground", top, bottom, 0.6);
        bool glass = Glass.On;
        // The glass look: light catching the top edge of a pane, fading down its sides; the side bar and the player bar float on the
        // picture (the side bar's card where its entries are in the plain look: 14 from everything, as wide as "Up next"); its grain; what
        // has a picture of its own behind it in the plain look (the song page) goes (SolidOnly). (Both looks are made again: Apply.)
        // (as strong as Settings' Edges: Glass.RimAlpha)
        r["RimBrush"] = !Glass.HasRim ? Brushes.Transparent
            : Stops(top, bottom, (Colors.White, Glass.RimAlpha(light ? 0xE6 : 0x38), 0), (Colors.White, Glass.RimAlpha(light ? 0x73 : 0x12), 0.4), (Colors.White, Glass.RimAlpha(light ? 0x59 : 0x0A), 1));
        r["RimThickness"] = new Thickness(Glass.HasRim ? 1 : 0);
        // (Layout: floating or attached, glass or not)
        bool floating = Layout.Floating;
        r["SidebarMargin"] = floating ? new Thickness(14, 0, 14, 14) : new Thickness(0);
        r["PlayerBarMargin"] = floating ? new Thickness(14, 0, 14, 14) : new Thickness(0);
        r["PaneRadius"] = new CornerRadius(floating ? 18 : 0);
        r["GrainOpacity"] = glass && Glass.Look.Grain ? 0.6 : 0.0;
        r["SolidOnly"] = !glass;
        // The song page over the blurred cover: the page's background, see-through on top, nearly solid at the bottom. (Not over the
        // glass look's picture: one for the whole window.)
        r["NowPlayingVeil"] = glass ? Brushes.Transparent : Stops(top, bottom, (bg, 0x55, 0), (bg, 0xEE, 1));
        // Over the whole screen the player bar lies on the page: its buttons read over the bars of the music, whose colours die out behind them.
        r["PlayerBarVeil"] = Stops(top, bottom, (bg, 0x00, 0), (bg, 0xB8, 0.3), (bg, 0xE6, 1));
        r["NowPlayingVeilLyrics"] = glass ? Brushes.Transparent : Stops(top, bottom, (bg, 0x1A, 0), (bg, 0x33, 0.55), (bg, 0xD9, 1));
        r["PlayerPageVeil"] = glass ? Brushes.Transparent : Stops(top, bottom, (bg, 0x33, 0), (bg, 0x99, 0.55), (bg, 0xF2, 1));
        r["LyricsVeilBrush"] = glass ? Brushes.Transparent : Solid(Color.FromArgb(0x59, bg.R, bg.G, bg.B));
        r["PopupShadow"] = BoxShadows.Parse(light ? "0 6 24 0 #2E000000" : "0 5 18 0 #80000000");
        return r;
    }

    public static IBrush Gradient(Color a, Color b) => Linear(a, b, new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(1, 1, RelativeUnit.Relative));

    private static IBrush Linear(Color a, Color b, RelativePoint from, RelativePoint to, double opacity = 1)
        => new LinearGradientBrush { StartPoint = from, EndPoint = to, Opacity = opacity, GradientStops = { new GradientStop(a, 0), new GradientStop(b, 1) } }.ToImmutable();

    private static IBrush Stops(RelativePoint from, RelativePoint to, params (Color C, byte A, double At)[] stops)
    {
        var g = new LinearGradientBrush { StartPoint = from, EndPoint = to };
        foreach (var (c, alpha, at) in stops) g.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, c.R, c.G, c.B), at));
        return g.ToImmutable();
    }

    public static Color Parse(string hex)
    {
        try { return Color.Parse(hex); }
        catch { return Color.Parse("#7C5CFF"); }
    }

    public static Color ToColor(uint argb) => Color.FromUInt32(argb);

    private static IBrush Solid(Color c) => new SolidColorBrush(c).ToImmutable();
}
