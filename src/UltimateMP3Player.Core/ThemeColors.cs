using System.Diagnostics;
using System.Globalization;

namespace UltimateMP3Player.Core;

// An accent theme: its two gradient colours and the heart's, in the dark look and in the light one (deeper colours: the
// bright ones of the dark look would fade on white).
public sealed record AccentTheme(string Id, string Name, string A, string B, string Heart, string LightA, string LightB, string LightHeart);

// The colours of the whole app for one accent, light or dark; each platform's Themes.Apply turns them into the
// "XColor"/"XBrush" resources. Light isn't dark turned inside out: its own greys (a hint of the accent in them), deeper
// status colours and accent text dark enough to read on white.
public static class ThemeColors
{
    public const string ProfileId = "profile";
    // ProfileData.Appearance.
    public const string System = "system", Light = "light", Dark = "dark";

    public static readonly AccentTheme[] All =
    {
        new("ultimate", "Ultimate", "#7C5CFF", "#FF4FA3", "#FF4FA3", "#6A47F5", "#EC3D93", "#EC3D93"),
        new("pink", "Pink", "#FF4FA3", "#FF8A5C", "#FF4FA3", "#EC3D93", "#FF7A45", "#EC3D93"),
        new("green", "Green", "#1DB954", "#12B5A5", "#1DB954", "#14A04A", "#0E9E90", "#14A04A"),
        new("orange", "Orange", "#FF7A1A", "#FF4F6D", "#FF7A1A", "#F26B0C", "#F23F5E", "#F26B0C"),
        new("blue", "Blue", "#1E9BFF", "#7C5CFF", "#1E9BFF", "#1487EA", "#6A47F5", "#1487EA"),
        new("red", "Red", "#E5484D", "#FF7A1A", "#E5484D", "#DB3A40", "#F26B0C", "#DB3A40"),
        new("teal", "Teal", "#12B5A5", "#1E9BFF", "#12B5A5", "#0E9E90", "#1487EA", "#0E9E90"),
        new("gold", "Gold", "#F5B400", "#FFE27A", "#F5B400", "#E0A000", "#F2C53D", "#D99A00"),
    };

    public static AccentTheme Resolve(string? id, string profileColor)
    {
        if (id == ProfileId)
        {
            if (All.FirstOrDefault(t => t.A.Equals(profileColor, StringComparison.OrdinalIgnoreCase)) is { } same) return same;
            uint p = Parse(profileColor), b = Mix(p, Parse("#FF4FA3"), 0.5), lp = Mix(p, 0xFF000000, 0.08);
            return new AccentTheme(ProfileId, "Profile", profileColor, Hex(b), profileColor, Hex(lp), Hex(Mix(b, 0xFF000000, 0.08)), Hex(lp));
        }
        return All.FirstOrDefault(t => t.Id == id) ?? All[0];
    }

    // Name → 0xAARRGGBB. Besides the palette there are the ends of the gradients (Fav1/Fav2, Placeholder1/2, HeaderTop,
    // Accent2Hover) that Themes.Apply puts together.
    public static Dictionary<string, uint> Build(AccentTheme t, bool light)
    {
        uint a = Parse(light ? t.LightA : t.A), b = Parse(light ? t.LightB : t.B), white = 0xFFFFFFFF, black = 0xFF000000;
        // The greys take a hint of the accent: green's light look isn't purple's with another accent.
        uint N(string hex) => Mix(Parse(hex), a, 0.03);
        var c = new Dictionary<string, uint>();
        if (light)
        {
            c["Bg"] = N("#F4F5F8");
            c["Sidebar"] = N("#EEEFF3");
            c["Surface"] = white;
            c["Popup"] = white;
            c["Surface2"] = N("#E3E5EA");
            c["Surface3"] = N("#D8DBE2");
            c["Border"] = N("#DFE1E6");
            c["BorderStrong"] = N("#C7CBD3");
            c["Text"] = Parse("#16181D");
            c["SubText"] = Parse("#596171");
            c["Muted"] = Parse("#7D8493");
            c["Success"] = Parse("#17874F");
            c["SuccessSoft"] = Parse("#DCF2E6");
            c["Error"] = Parse("#D42F47");
            c["ErrorSoft"] = Parse("#FBE4E8");
            c["Warning"] = Parse("#A86300");
            c["WarningSoft"] = Parse("#FBEFD4");
            c["RowHover"] = N("#E8E9ED");
            c["Wave"] = N("#C7CBD3");
            c["ScrollThumb"] = N("#C2C6CE");
            c["ScrollThumbHover"] = N("#A3A9B4");
            c["Placeholder1"] = N("#E3E6EC");
            c["Placeholder2"] = N("#D2D6DE");
            c["MiniPlayer"] = white;
            c["Pressed"] = 0x14000000;
            c["Scrim"] = 0x59000000;
            // Frosted pills floating on the song page.
            c["Glass"] = WithAlpha(white, 0xB8);
            c["GlassHover"] = WithAlpha(white, 0xF0);
            c["GlassBorder"] = 0x1A000000;
            // Accent text: darkened until it reads on white (the gold one is far too light as it is).
            uint text = a;
            for (int i = 0; i < 20 && Contrast(text, white) < 4.5; i++) text = Mix(text, black, 0.06);
            c["AccentText"] = text;
            c["AccentTextHover"] = Mix(text, black, 0.2);
            c["AccentHover"] = Mix(a, black, 0.08);
            c["Accent2Hover"] = Mix(b, black, 0.08);
            c["AccentSoft"] = Mix(a, white, 0.85);
            c["HeaderTop"] = Mix(a, c["Bg"], 0.78);
        }
        else
        {
            c["Bg"] = N("#0E1014");
            c["Sidebar"] = N("#0A0C0F");
            c["Surface"] = N("#161920");
            c["Surface2"] = N("#1D2129");
            c["Popup"] = c["Surface2"];
            c["Surface3"] = N("#262B35");
            c["Border"] = N("#272C36");
            c["BorderStrong"] = N("#3A4150");
            c["Text"] = Parse("#ECEEF3");
            c["SubText"] = Parse("#9AA3B5");
            c["Muted"] = Parse("#687084");
            c["Success"] = Parse("#34D399");
            c["SuccessSoft"] = Parse("#183A2D");
            c["Error"] = Parse("#FF5C72");
            c["ErrorSoft"] = Parse("#3A1D25");
            c["Warning"] = Parse("#FBBF24");
            c["WarningSoft"] = Parse("#3A3020");
            c["RowHover"] = N("#1A1D24");
            c["Wave"] = N("#3A4150");
            c["ScrollThumb"] = N("#353B48");
            c["ScrollThumbHover"] = N("#4A5263");
            c["Placeholder1"] = N("#2A2F3A");
            c["Placeholder2"] = N("#1B1E25");
            c["MiniPlayer"] = N("#1A1D25");
            c["Pressed"] = 0x1FFFFFFF;
            c["Scrim"] = 0x99000000;
            c["Glass"] = 0xA6101318;
            c["GlassHover"] = WithAlpha(c["Surface2"], 0xE6);
            c["GlassBorder"] = 0x30FFFFFF;
            c["AccentText"] = Mix(a, white, 0.35);
            c["AccentTextHover"] = Mix(a, white, 0.6);
            c["AccentHover"] = Mix(a, white, 0.12);
            c["Accent2Hover"] = Mix(b, white, 0.12);
            // Selected rows: a dark tint of the accent (mostly background), readable with any theme colour.
            c["AccentSoft"] = Mix(a, c["Bg"], 0.76);
            c["HeaderTop"] = Mix(a, c["Bg"], 0.27);
        }
        c["TitleBar"] = c["Sidebar"];
        c["Sheet"] = c["Surface"];
        c["NavBar"] = WithAlpha(c["Sidebar"], 0xF2);
        // Over the page while something is dropped on the window or a panel covers it.
        c["Veil"] = WithAlpha(c["Bg"], 0xC4);
        c["ErrorSoftHover"] = Mix(c["ErrorSoft"], c["Error"], 0.15);
        c["Accent"] = a;
        c["Accent2"] = b;
        c["Pink"] = Parse(light ? t.LightHeart : t.Heart);
        c["OnAccent"] = Luminance(Mix(a, b, 0.5)) > 0.4 ? Parse("#0E1014") : white;
        c["Fav1"] = Mix(a, black, 0.25);
        c["Fav2"] = Mix(a, white, 0.45);
        Debug.Assert(Contrast(c["AccentText"], c["Surface"]) >= (light ? 4.5 : 3), "accent text unreadable: " + t.Id);
        return c;
    }

    public static uint Parse(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length == 6) s = "FF" + s;
        return s.Length == 8 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0xFF7C5CFF;
    }

    public static string Hex(uint c) => $"#{(c >> 16) & 0xFF:X2}{(c >> 8) & 0xFF:X2}{c & 0xFF:X2}";

    public static uint WithAlpha(uint c, byte alpha) => (c & 0x00FFFFFF) | ((uint)alpha << 24);

    // Between two opaque colours (t = 0: a, 1: b).
    public static uint Mix(uint a, uint b, double t)
    {
        static uint Ch(uint x, uint y, int shift, double t) => (uint)Math.Round(((x >> shift) & 0xFF) + ((double)((y >> shift) & 0xFF) - ((x >> shift) & 0xFF)) * t) << shift;
        return 0xFF000000 | Ch(a, b, 16, t) | Ch(a, b, 8, t) | Ch(a, b, 0, t);
    }

    public static double Luminance(uint c)
    {
        static double Ch(uint v)
        {
            double s = (v & 0xFF) / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c >> 16) + 0.7152 * Ch(c >> 8) + 0.0722 * Ch(c);
    }

    public static double Contrast(uint x, uint y)
    {
        double lx = Luminance(x), ly = Luminance(y);
        return (Math.Max(lx, ly) + 0.05) / (Math.Min(lx, ly) + 0.05);
    }
}
