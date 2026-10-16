using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using UltimateMP3Player.Core;

namespace UltimateMP3Player;

// The colours of the app (Core ThemeColors) as resources; views use them via DynamicResource. "Like the system" reads
// Windows' app mode and follows it while the app is open.
public static class Themes
{
    private static string? _id, _mode;
    private static string _profileColor = ThemeColors.All[0].A;
    private static Dictionary<string, uint> _colors = ThemeColors.Build(ThemeColors.All[0], false);
    private static bool _watching;

    // The light or dark look changed (another choice, Windows switched): what draws its own colours redraws.
    public static event Action? Changed;

    public static bool IsLight { get; private set; }

    public static void Apply(string? id, string profileColor, string? mode)
    {
        (_id, _profileColor, _mode) = (id, profileColor, mode);
        if (!_watching)
        {
            _watching = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General)
                    Application.Current?.Dispatcher.BeginInvoke(() => { if (IsLight != Light(_mode)) Apply(_id, _profileColor, _mode); });
            };
        }
        IsLight = Light(mode);
        _colors = ThemeColors.Build(ThemeColors.Resolve(id, profileColor), IsLight);
        var r = Application.Current.Resources;
        foreach (var (name, argb) in _colors)
        {
            r[name + "Color"] = ToColor(argb);
            r[name + "Brush"] = Solid(ToColor(argb));
        }
        Color C(string name) => ToColor(_colors[name]);
        Color a = C("Accent"), b = C("Accent2"), bg = C("Bg");
        Point top = new(0, 0), bottom = new(0, 1);
        r["AccentGradient"] = Gradient(a, b);
        r["AccentGradientHover"] = Gradient(C("AccentHover"), C("Accent2Hover"));
        r["ProgressGradient"] = Freeze(new LinearGradientBrush(a, b, top, new Point(1, 0)));
        r["FavoritesGradient"] = Gradient(C("Fav1"), C("Fav2"));
        r["PlaceholderGradient"] = Gradient(C("Placeholder1"), C("Placeholder2"));
        r["HeaderGradient"] = Freeze(new LinearGradientBrush(C("HeaderTop"), bg, top, bottom));
        // The song page over the blurred cover: the page's background, see-through on top, nearly solid at the bottom.
        r["NowPlayingVeil"] = Stops(top, bottom, (bg, 0x55, 0), (bg, 0xEE, 1));
        r["NowPlayingVeilLyrics"] = Stops(top, bottom, (bg, 0x1A, 0), (bg, 0x33, 0.55), (bg, 0xD9, 1));
        r["LyricsVeilBrush"] = Solid(Color.FromArgb(0x59, bg.R, bg.G, bg.B));
        r["PopupShadowColor"] = IsLight ? Color.FromArgb(0x59, 0, 0, 0) : Colors.Black;
        foreach (Window w in Application.Current.Windows) Ui.DarkTitleBar(w);
        Changed?.Invoke();
    }

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
