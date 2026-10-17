using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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

    // Android: the device's own look, chosen here rather than left to Avalonia (its window stops telling once it has shown
    // one). Null elsewhere: Avalonia follows the device by itself.
    public static Func<ThemeVariant>? DeviceLook;

    public static bool IsLight => Application.Current?.ActualThemeVariant == ThemeVariant.Light;

    public static void Apply(string? id, string profileColor, string? mode)
    {
        if (Application.Current is not { } app) return;
        var t = ThemeColors.Resolve(id, profileColor);
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
    }

    // (the live brushes first: what redraws on Changed may read them)
    private static void Restyle()
    {
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
            r[name + "Brush"] = Solid(ToColor(argb));
        }
        Color C(string name) => ToColor(c[name]);
        Color a = C("Accent"), b = C("Accent2"), bg = C("Bg");
        RelativePoint top = new(0, 0, RelativeUnit.Relative), bottom = new(0, 1, RelativeUnit.Relative);
        r["AccentGradient"] = Gradient(a, b);
        r["AccentGradientHover"] = Gradient(C("AccentHover"), C("Accent2Hover"));
        r["ProgressGradient"] = Linear(a, b, top, new RelativePoint(1, 0, RelativeUnit.Relative));
        r["FavoritesGradient"] = Gradient(C("Fav1"), C("Fav2"));
        r["PlaceholderGradient"] = Gradient(C("Placeholder1"), C("Placeholder2"));
        r["HeaderGradient"] = Linear(C("HeaderTop"), bg, top, bottom);
        // (see Theme.axaml: the opacity inside the brush, no layer)
        r["HeaderGlow"] = Linear(C("HeaderTop"), bg, top, bottom, 0.55);
        r["HeaderGlowStrong"] = Linear(C("HeaderTop"), bg, top, bottom, 0.6);
        // The song page over the blurred cover: the page's background, see-through on top, nearly solid at the bottom.
        r["NowPlayingVeil"] = Stops(top, bottom, (bg, 0x55, 0), (bg, 0xEE, 1));
        r["NowPlayingVeilLyrics"] = Stops(top, bottom, (bg, 0x1A, 0), (bg, 0x33, 0.55), (bg, 0xD9, 1));
        r["PlayerPageVeil"] = Stops(top, bottom, (bg, 0x33, 0), (bg, 0x99, 0.55), (bg, 0xF2, 1));
        r["LyricsVeilBrush"] = Solid(Color.FromArgb(0x59, bg.R, bg.G, bg.B));
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
