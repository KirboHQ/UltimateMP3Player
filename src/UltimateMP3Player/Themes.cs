using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using UltimateMP3Player.Core;

namespace UltimateMP3Player;

// The colours of the app (Core ThemeColors) as resources; views use them via DynamicResource. "Like the system" reads
// Windows' app mode and follows it while the app is open.
public static class Themes
{
    private static string? _id, _mode, _song;
    private static string _profileColor = ThemeColors.All[0].A;
    private static Dictionary<string, uint> _colors = ThemeColors.Build(ThemeColors.All[0], false);
    private static bool _watching;

    // The light or dark look changed (another choice, Windows switched): what draws its own colours redraws.
    public static event Action? Changed;

    // The accent moved to another song's (what draws it by itself looks again).
    public static event Action? AccentChanged;

    // How long the colours take from one song to the next.
    public const double FadeMs = 700;

    public static bool IsLight { get; private set; }

    public static void Apply(string? id, string profileColor, string? mode, string? songColors = null)
    {
        (_id, _profileColor, _mode, _song) = (id, profileColor, mode, songColors);
        if (!_watching)
        {
            _watching = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General)
                    Application.Current?.Dispatcher.BeginInvoke(() => { if (IsLight != Light(_mode)) Apply(_id, _profileColor, _mode, _song); });
            };
        }
        IsLight = Light(mode);
        _colors = ThemeColors.Build(ThemeColors.Resolve(id, profileColor, songColors), IsLight);
        bool moves = ThemeColors.Moves(id);
        _fade++;
        _movingBrushes.Clear();
        _movingStops.Clear();
        var r = Application.Current.Resources;
        foreach (var (name, argb) in _colors)
        {
            r[name + "Color"] = ToColor(argb);
            if (moves && ThemeColors.AccentKeys.Contains(name)) Keep(r, name + "Brush", Moving(name));
            else r[name + "Brush"] = Solid(ToColor(argb));
        }
        Color C(string name) => ToColor(_colors[name]);
        Color bg = C("Bg");
        Point top = new(0, 0), bottom = new(0, 1), corner = new(1, 1);
        void Two(string key, string from, string to, Point end)
        {
            if (moves) Keep(r, key, MovingGradient(from, to, end));
            else r[key] = Freeze(new LinearGradientBrush(C(from), C(to), top, end));
        }
        Two("AccentGradient", "Accent", "Accent2", corner);
        Two("AccentGradientHover", "AccentHover", "Accent2Hover", corner);
        Two("ProgressGradient", "Accent", "Accent2", new Point(1, 0));
        Two("FavoritesGradient", "Fav1", "Fav2", corner);
        r["PlaceholderGradient"] = Gradient(C("Placeholder1"), C("Placeholder2"));
        Two("HeaderGradient", "HeaderTop", "Ground", bottom);
        bool glass = Glass.On;
        // The glass look: light catching the top edge of a pane, fading down its sides; the side bar and the player bar float on the
        // picture; its grain; what has a picture of its own behind it in the plain look (the song page) goes (SolidOnly).
        r["RimBrush"] = !glass ? Brushes.Transparent
            : Stops(top, bottom, (Colors.White, (byte)(IsLight ? 0xE6 : 0x38), 0), (Colors.White, (byte)(IsLight ? 0x73 : 0x12), 0.4), (Colors.White, (byte)(IsLight ? 0x59 : 0x0A), 1));
        r["RimThickness"] = new Thickness(glass ? 1 : 0);
        // (the side bar's card where its entries are when attached: 14 from everything; Layout: floating or attached, glass or not)
        bool floating = Layout.Floating;
        r["SidebarMargin"] = floating ? new Thickness(14, 0, 14, 14) : new Thickness(0);
        r["PlayerBarMargin"] = floating ? new Thickness(14, 0, 14, 14) : new Thickness(0);
        r["PaneRadius"] = new CornerRadius(floating ? 18 : 0);
        r["GrainOpacity"] = glass && Glass.Look.Grain ? 0.6 : 0.0;
        r["SolidOnly"] = glass ? Visibility.Collapsed : Visibility.Visible;
        // The song page over the blurred cover: the page's background, see-through on top, nearly solid at the bottom. (Not over the
        // glass look's picture: one for the whole window.)
        r["NowPlayingVeil"] = glass ? Brushes.Transparent : Stops(top, bottom, (bg, 0x55, 0), (bg, 0xEE, 1));
        // Over the whole screen the player bar lies on the page: its buttons read over the bars of the music, whose colours die out behind them.
        r["PlayerBarVeil"] = Stops(top, bottom, (bg, 0x00, 0), (bg, 0xB8, 0.3), (bg, 0xE6, 1));
        r["NowPlayingVeilLyrics"] = glass ? Brushes.Transparent : Stops(top, bottom, (bg, 0x1A, 0), (bg, 0x33, 0.55), (bg, 0xD9, 1));
        r["LyricsVeilBrush"] = glass ? Brushes.Transparent : Solid(Color.FromArgb(0x59, bg.R, bg.G, bg.B));
        r["PopupShadowColor"] = IsLight ? Color.FromArgb(0x59, 0, 0, 0) : Colors.Black;
        foreach (Window w in Application.Current.Windows) Ui.DarkTitleBar(w);
        RunRgb();
        Changed?.Invoke();
    }

    // The theme RGB: the accent takes its next colours in place, only while the window can be seen.
    private static DispatcherTimer? _rgb;

    public static void RunRgb()
    {
        if (_rgb == null)
        {
            if (_id != ThemeColors.RgbId) return;
            _rgb = new DispatcherTimer(DispatcherPriority.Background);
            _rgb.Tick += (_, _) => StepRgb();
            Ui.HiddenChanged += RunRgb;
        }
        _rgb.Interval = TimeSpan.FromMilliseconds(ThemeColors.RgbStepMs);
        _rgb.IsEnabled = _id == ThemeColors.RgbId && !Ui.Hidden;
    }

    private static void StepRgb()
    {
        if (Ui.Animations) PaintAccent(ThemeColors.Rgb());
    }

    // Other colours on the accent's brushes, in place: no window looks up its resources again.
    public static void PaintAccent(AccentTheme theme)
    {
        _colors = ThemeColors.Build(theme, IsLight);
        foreach (var (key, brush) in _movingBrushes) brush.Color = ToColor(_colors[key]);
        foreach (var (stop, key) in _movingStops) stop.Color = ToColor(_colors[key]);
        AccentChanged?.Invoke();
    }

    // Brushes that stay the same object from song to song, so nothing in the windows has to look them up again: only their colours move,
    // on the render thread, and nothing runs once they've arrived. (Not frozen, so only these few, and only with this theme.)
    private static readonly Dictionary<string, SolidColorBrush> _movingBrushes = new();
    private static readonly List<(GradientStop Stop, string Key)> _movingStops = new();
    private static int _fade;

    private static SolidColorBrush Moving(string key) => _movingBrushes[key] = new SolidColorBrush(ToColor(_colors[key]));

    // WPF freezes what's in the application's resources (now, and again when a window loads its own), unless it's being animated:
    // so these stay animated, on an animation already over, which asks for no frame.
    private static void Keep(ResourceDictionary resources, string key, Brush brush)
    {
        brush.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(1, new Duration(TimeSpan.Zero)));
        resources[key] = brush;
    }

    private static LinearGradientBrush MovingGradient(string from, string to, Point end)
    {
        GradientStop a = new(ToColor(_colors[from]), 0), b = new(ToColor(_colors[to]), 1);
        _movingStops.Add((a, from));
        _movingStops.Add((b, to));
        return new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = end, GradientStops = { a, b } };
    }

    // To the colours of another song. Off the screen or without animations, straight there.
    public static void FadeTo(AccentTheme theme)
    {
        if (_movingBrushes.Count == 0) return;
        _colors = ThemeColors.Build(theme, IsLight);
        int run = ++_fade;
        bool glide = Ui.Animations && !Ui.Hidden;
        var time = Ui.Time(FadeMs);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        ease.Freeze();
        var moved = new List<Action>();
        void Move(Animatable target, DependencyProperty property, Color to)
        {
            void Land()
            {
                target.BeginAnimation(property, null);
                target.SetValue(property, to);
            }
            if (!glide) Land();
            else
            {
                moved.Add(Land);
                var move = new ColorAnimation(to, time) { EasingFunction = ease };
                // (they all end together: then no animation is left, not even one holding its last value)
                if (moved.Count == 1)
                    move.Completed += (_, _) =>
                    {
                        if (run == _fade) foreach (var land in moved) land();
                    };
                target.BeginAnimation(property, move);
            }
        }
        foreach (var (key, brush) in _movingBrushes) Move(brush, SolidColorBrush.ColorProperty, ToColor(_colors[key]));
        foreach (var (stop, key) in _movingStops) Move(stop, GradientStop.ColorProperty, ToColor(_colors[key]));
        AccentChanged?.Invoke();
    }

    // The glass look's picture behind the window (ViewModels/GlassBackdrop; null: no glass), and its grain, tiled over it.
    public static ImageSource? Backdrop { get; private set; }
    public static event Action? BackdropChanged;

    public static void ShowBackdrop(byte[]? px)
    {
        Backdrop = px == null ? null : Freeze(BitmapSource.Create(Glass.Size, Glass.Size, 96, 96, PixelFormats.Bgra32, null, px, Glass.Size * 4));
        BackdropChanged?.Invoke();
    }

    private static ImageBrush? _grain;

    public static ImageBrush Grain => _grain ??= Freeze(new ImageBrush(Freeze(BitmapSource.Create(Glass.GrainSize, Glass.GrainSize, 96, 96, PixelFormats.Bgra32, null, Glass.GrainPixels(), Glass.GrainSize * 4)))
    {
        TileMode = TileMode.Tile, Viewport = new Rect(0, 0, Glass.GrainSize, Glass.GrainSize), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None,
    });

    // A cover small, as blue, green, red, alpha.
    public static (byte[] Bgra, int Width, int Height)? PixelsOf(string file, int width = 28)
    {
        if (!File.Exists(file)) return null;
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.CreateOptions = BitmapCreateOptions.IgnoreImageCache | BitmapCreateOptions.IgnoreColorProfile;
        img.DecodePixelWidth = width;
        img.UriSource = new Uri(file);
        img.EndInit();
        img.Freeze();
        var small = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
        var px = new byte[small.PixelWidth * small.PixelHeight * 4];
        small.CopyPixels(px, small.PixelWidth * 4, 0);
        return (px, small.PixelWidth, small.PixelHeight);
    }

    // "From the song": no colour of its own, so all of them.
    public static Brush SongSwatch() => Freeze(new LinearGradientBrush(new GradientStopCollection
    {
        new(Parse("#FF4D6D"), 0), new(Parse("#FFC24D"), 0.33), new(Parse("#4DE08A"), 0.66), new(Parse("#4DB8FF"), 1),
    }, new Point(0, 0), new Point(1, 1)));

    // RGB: the whole round.
    public static Brush RgbSwatch() => Freeze(new LinearGradientBrush(new GradientStopCollection
    {
        new(Parse("#FF3B3B"), 0), new(Parse("#FFD93B"), 0.2), new(Parse("#3BFF6B"), 0.4), new(Parse("#3BE0FF"), 0.6), new(Parse("#4B3BFF"), 0.8), new(Parse("#FF3BD8"), 1),
    }, new Point(0, 0), new Point(1, 0)));

    // A colour of the look showing now (code that draws by itself).
    public static Color Get(string name) => ToColor(_colors.TryGetValue(name, out var c) ? c : 0xFF808080);

    public static Brush Swatch(AccentTheme t)
        => IsLight ? Gradient(Parse(t.LightA), Parse(t.LightB)) : Gradient(Parse(t.A), Parse(t.B));

    // Windows' "app mode" (Settings > Personalization > Colors); no value = light, as Windows does.
    private static bool Light(string? mode)
    {
        if (mode == ThemeColors.Light) return true;
        if (mode == ThemeColors.Dark) return false;
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is not 0; }
        catch { return false; }
    }

    public static Brush Gradient(Color a, Color b) => Freeze(new LinearGradientBrush(a, b, new Point(0, 0), new Point(1, 1)));

    private static Brush Stops(Point from, Point to, params (Color C, byte A, double At)[] stops)
    {
        var g = new LinearGradientBrush { StartPoint = from, EndPoint = to };
        foreach (var (c, alpha, at) in stops) g.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, c.R, c.G, c.B), at));
        return Freeze(g);
    }

    public static Color Parse(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Parse("#7C5CFF"); }
    }

    public static Color ToColor(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static SolidColorBrush Solid(Color c) => Freeze(new SolidColorBrush(c));

    private static TBrush Freeze<TBrush>(TBrush b) where TBrush : Freezable
    {
        b.Freeze();
        return b;
    }
}
