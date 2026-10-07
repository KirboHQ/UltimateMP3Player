using Avalonia;
using Avalonia.Media;
using UltimateMP3Player.Core;

namespace UltimateMP3Player;

public sealed record ThemeDef(string Id, string Name, string A, string B, string Heart)
{
    public IBrush Swatch => Themes.Gradient(Themes.Parse(A), Themes.Parse(B));
    public string Label => L.T(Name);
}

// Accent colours; views use them via DynamicResource (same as the Windows app's Themes.cs).
public static class Themes
{
    public const string ProfileId = "profile";

    public static readonly ThemeDef[] All =
    {
        new("ultimate", "Ultimate", "#7C5CFF", "#FF4FA3", "#FF4FA3"),
        new("pink", "Rosa", "#FF4FA3", "#FF8A5C", "#FF4FA3"),
        new("green", "Verde", "#1DB954", "#12B5A5", "#1DB954"),
        new("orange", "Arancione", "#FF7A1A", "#FF4F6D", "#FF7A1A"),
        new("blue", "Blu", "#1E9BFF", "#7C5CFF", "#1E9BFF"),
        new("red", "Rosso", "#E5484D", "#FF7A1A", "#E5484D"),
        new("teal", "Turchese", "#12B5A5", "#1E9BFF", "#12B5A5"),
        new("gold", "Oro", "#F5B400", "#FFE27A", "#F5B400"),
    };

    private static readonly Color Bg = Parse("#0E1014");

    public static ThemeDef Resolve(string? id, string profileColor)
    {
        if (id == ProfileId)
            return All.FirstOrDefault(t => t.A.Equals(profileColor, StringComparison.OrdinalIgnoreCase))
                   ?? new ThemeDef(ProfileId, "Profilo", profileColor, Hex(Mix(Parse(profileColor), Parse("#FF4FA3"), 0.5)), profileColor);
        return All.FirstOrDefault(t => t.Id == id) ?? All[0];
    }

    public static void Apply(string? id, string profileColor)
    {
        var t = Resolve(id, profileColor);
        Color a = Parse(t.A), b = Parse(t.B), white = Colors.White, black = Colors.Black;
        if (Application.Current is not { } app) return;
        var r = app.Resources;
        r["AccentColor"] = a;
        r["Accent2Color"] = b;
        r["AccentBrush"] = Solid(a);
        r["AccentHoverBrush"] = Solid(Mix(a, white, 0.12));
        r["AccentTextBrush"] = Solid(Mix(a, white, 0.35));
        r["AccentTextHoverBrush"] = Solid(Mix(a, white, 0.6));
        // Selected rows: a dark tint of the accent (mostly background), readable with any theme colour.
        r["AccentSoftBrush"] = Solid(Mix(a, Bg, 0.76));
        r["PinkBrush"] = Solid(Parse(t.Heart));
        r["OnAccentBrush"] = Solid(Luminance(Mix(a, b, 0.5)) > 0.4 ? Parse("#0E1014") : white);
        r["AccentGradient"] = Gradient(a, b);
        r["AccentGradientHover"] = Gradient(Mix(a, white, 0.12), Mix(b, white, 0.12));
        r["ProgressGradient"] = Linear(a, b, new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(1, 0, RelativeUnit.Relative));
        r["FavoritesGradient"] = Gradient(Mix(a, black, 0.25), Mix(a, white, 0.45));
        RelativePoint top = new(0, 0, RelativeUnit.Relative), bottom = new(0, 1, RelativeUnit.Relative);
        r["HeaderGradient"] = Linear(Mix(a, Bg, 0.27), Bg, top, bottom);
        // (see Theme.axaml: the opacity inside the brush, no layer)
        r["HeaderGlow"] = Linear(Mix(a, Bg, 0.27), Bg, top, bottom, 0.55);
        r["HeaderGlowStrong"] = Linear(Mix(a, Bg, 0.27), Bg, top, bottom, 0.6);
    }

    public static IBrush Gradient(Color a, Color b) => Linear(a, b, new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(1, 1, RelativeUnit.Relative));

    private static IBrush Linear(Color a, Color b, RelativePoint from, RelativePoint to, double opacity = 1)
        => new LinearGradientBrush { StartPoint = from, EndPoint = to, Opacity = opacity, GradientStops = { new GradientStop(a, 0), new GradientStop(b, 1) } }.ToImmutable();

    public static Color Parse(string hex)
    {
        try { return Color.Parse(hex); }
        catch { return Color.Parse("#7C5CFF"); }
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color Mix(Color a, Color b, double t)
        => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private static double Luminance(Color c)
    {
        static double Ch(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    private static IBrush Solid(Color c) => new SolidColorBrush(c).ToImmutable();
}
