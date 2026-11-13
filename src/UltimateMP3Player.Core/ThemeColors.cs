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
    // The colours of the song playing (the default).
    public const string DynamicId = "dynamic";
    // Colours going round by themselves, whatever the song plays, only while the screen can be seen (Themes). At 1x two degrees at a time, too
    // little to see a step, the screen drawn only 4 times a second (8 cost a phone twice as much for nothing to see). Faster = steps closer
    // together, not bigger, up to 30 a second (2.7 degrees at 10x).
    public const string RgbId = "rgb";
    public const double RgbRoundSeconds = 45, RgbMinSpeed = 1, RgbMaxSpeed = 10;
    public static double RgbSpeed { get; set; } = 1;
    public static double RgbStepMs => Math.Max(250 / RgbSpeed, 1000.0 / 30);
    // The accent's brushes move in place, the rest of the palette stays: no hint of the accent in the greys.
    public static bool Moves(string? id) => id is DynamicId or RgbId or CustomId;
    // A colour of your own (ProfileData.CustomColor); it moves too, while you choose it.
    public const string CustomId = "custom";
    public static string CustomColor { get; set; } = "#7C5CFF";
    // What moves from one song to the next, in place: the rest of the palette stays as it is (every surface fading would redraw the whole window).
    public static readonly string[] AccentKeys = { "Accent", "Accent2", "AccentHover", "Accent2Hover", "AccentSoft", "AccentText", "AccentTextHover", "Pink", "OnAccent", "Fav1", "Fav2", "HeaderTop" };
    // ProfileData.Appearance.
    public const string System = "system", Light = "light", Dark = "dark";

    public static readonly AccentTheme[] All =
    {
        // (in the order of the rainbow, the way Settings shows them)
        new("ultimate", "Ultimate", "#7C5CFF", "#FF4FA3", "#FF4FA3", "#6A47F5", "#EC3D93", "#EC3D93"),
        new("pink", "Pink", "#FF4FA3", "#FF8A5C", "#FF4FA3", "#EC3D93", "#FF7A45", "#EC3D93"),
        new("red", "Red", "#E5484D", "#FF7A1A", "#E5484D", "#DB3A40", "#F26B0C", "#DB3A40"),
        new("orange", "Orange", "#FF7A1A", "#FF4F6D", "#FF7A1A", "#F26B0C", "#F23F5E", "#F26B0C"),
        new("gold", "Gold", "#F5B400", "#FFE27A", "#F5B400", "#E0A000", "#F2C53D", "#D99A00"),
        new("green", "Green", "#1DB954", "#12B5A5", "#1DB954", "#14A04A", "#0E9E90", "#14A04A"),
        new("teal", "Teal", "#12B5A5", "#1E9BFF", "#12B5A5", "#0E9E90", "#1487EA", "#0E9E90"),
        new("blue", "Blue", "#1E9BFF", "#7C5CFF", "#1E9BFF", "#1487EA", "#6A47F5", "#1487EA"),
    };

    public static AccentTheme Resolve(string? id, string profileColor, string? songColors = null)
    {
        if (id == DynamicId) return FromSong(songColors);
        if (id == RgbId) return Rgb();
        if (id == CustomId) return Custom(CustomColor);
        if (id == ProfileId)
        {
            if (All.FirstOrDefault(t => t.A.Equals(profileColor, StringComparison.OrdinalIgnoreCase)) is { } same) return same;
            uint p = Parse(profileColor), b = Mix(p, Parse("#FF4FA3"), 0.5), lp = Mix(p, 0xFF000000, 0.08);
            return new AccentTheme(ProfileId, "Profile", profileColor, Hex(b), profileColor, Hex(lp), Hex(Mix(b, 0xFF000000, 0.08)), Hex(lp));
        }
        return All.FirstOrDefault(t => t.Id == id) ?? All[0];
    }

    // What Palette found on a cover as a theme; null: no song or no cover yet, the app's own colours. The same lightness as the stream
    // overlays': a dark cover or a pale one still gives a colour that reads on the page.
    public static AccentTheme FromSong(string? colors)
    {
        if (colors == GreyCover) return new AccentTheme(DynamicId, "From the song", "#D9DCE3", "#9AA1B0", "#9AA1B0", "#3D4350", "#6B7280", "#6B7280");
        var parts = colors?.Split(';');
        if (parts is not { Length: 4 }) return All[0] with { Id = DynamicId, Name = "From the song" };
        double V(int i) => double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        double h1 = V(0), s1 = Math.Clamp(V(1), 0.55, 0.92), h2 = V(2), s2 = Math.Clamp(V(3), 0.5, 0.92);
        string b = Hsl(h2, s2, 0.62), lightB = Hsl(h2, s2, 0.4);
        return new AccentTheme(DynamicId, "From the song", Hsl(h1, s1, 0.62), b, b, Hsl(h1, s1, 0.42), lightB, lightB);
    }

    // The colour as it is, a lighter tint of it for the gradients; the light look a little deeper.
    public static AccentTheme Custom(string color)
    {
        uint c = Parse(color), b = Mix(c, 0xFFFFFFFF, 0.25), lc = Mix(c, 0xFF000000, 0.12);
        return new AccentTheme(CustomId, "Custom", color, Hex(b), color, Hex(lc), Hex(Mix(b, 0xFF000000, 0.12)), Hex(lc));
    }

    public static string FromHue(double h) => Hsl(h, 0.85, 0.6);

    public static double HueOf(string color)
    {
        uint c = Parse(color);
        double r = ((c >> 16) & 0xFF) / 255.0, g = ((c >> 8) & 0xFF) / 255.0, b = (c & 0xFF) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), d = max - Math.Min(r, Math.Min(g, b));
        if (d == 0) return 0;
        double h = max == r ? (g - b) / d : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60 + 360) % 360;
    }

    // What was typed, if it's a whole colour yet.
    public static string? ColorFrom(string? text)
    {
        string t = (text ?? "").Trim().TrimStart('#');
        return t.Length == 6 && t.All(Uri.IsHexDigit) ? "#" + t.ToUpperInvariant() : null;
    }

    // Where the round is, in degrees: it moves on by the time passed and the speed, so another speed goes on from the same colour.
    public static double RgbHue { get; private set; }
    private static long _rgbAt;

    public static AccentTheme Rgb()
    {
        long now = Environment.TickCount64;
        if (_rgbAt > 0) RgbHue = (RgbHue + (now - _rgbAt) / 1000.0 / RgbRoundSeconds * 360 * RgbSpeed) % 360;
        _rgbAt = now;
        return RgbAt(RgbHue);
    }

    // What's written on the buttons stays one colour all the way round (Build: chosen by brightness it flipped between white and black a few
    // times a round), so every colour has to read under it: dark on the dark look's colours, the blues lightened for it; white on the light
    // look's, the yellows darkened for it.
    public static AccentTheme RgbAt(double h)
    {
        double next = (h + 60) % 360;
        string b = ForDarkText(next), lightB = ForWhiteText(next);
        return new AccentTheme(RgbId, "RGB", ForDarkText(h), b, b, ForWhiteText(h), lightB, lightB);
    }

    private static string ForDarkText(double h)
    {
        double l = 0.62;
        while (l < 0.8 && Luminance(Parse(Hsl(h, 0.9, l))) < 0.2) l += 0.02;
        return Hsl(h, 0.9, l);
    }

    private static string ForWhiteText(double h)
    {
        double l = 0.42;
        while (l > 0.2 && Luminance(Parse(Hsl(h, 0.85, l))) > 0.18) l -= 0.02;
        return Hsl(h, 0.85, l);
    }

    private const string GreyCover = "grey";

    // A cover's two strongest colours from its pixels (blue, green, red, alpha), by hue, the vivid ones counting more: the same reading as
    // the overlays' (Overlay.html, readPalette). A grey has no hue, so it says nothing; a cover with hardly any colour is grey, not made up from its noise.
    public static string Palette(byte[] bgra)
    {
        var buckets = new (double W, double S, double H)[18];
        double vivid = 0;
        int count = bgra.Length / 4;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            double b = bgra[i] / 255.0, g = bgra[i + 1] / 255.0, r = bgra[i + 2] / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
            if (d < 1e-5) continue;
            double s = l > 0.5 ? d / (2 - max - min) : d / (max + min), h = (max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4) * 60;
            double w = s * s * Math.Max(0, 1 - Math.Abs(l - 0.5) * 1.7);
            if (w <= 0) continue;
            ref var bucket = ref buckets[(int)(h / 20) % 18];
            bucket.W += w;
            bucket.S += s * w;
            bucket.H += h * w;
            vivid += w;
        }
        if (vivid < count * 0.02) return GreyCover;
        var sorted = buckets.Where(x => x.W > 0).OrderByDescending(x => x.W).ToList();
        var first = sorted[0];
        double h1 = first.H / first.W, s1 = first.S / first.W, h2 = (h1 + 30) % 360, s2 = s1;
        foreach (var other in sorted)
        {
            double apart = Math.Abs(other.H / other.W - h1);
            if (Math.Min(apart, 360 - apart) <= 40 || other.W <= first.W * 0.12) continue;
            h2 = other.H / other.W;
            s2 = other.S / other.W;
            break;
        }
        return string.Create(CultureInfo.InvariantCulture, $"{h1:0.#};{s1:0.###};{h2:0.#};{s2:0.###}");
    }

    private static string Hsl(double h, double s, double l)
    {
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        double Channel(double t)
        {
            t = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
        }
        double at = h / 360;
        uint C(double t) => (uint)Math.Round(Channel(t) * 255);
        return Hex(0xFF000000 | C(at + 1 / 3.0) << 16 | C(at) << 8 | C(at - 1 / 3.0));
    }

    // Name → 0xAARRGGBB. Besides the palette there are the ends of the gradients (Fav1/Fav2, Placeholder1/2, HeaderTop,
    // Accent2Hover) that Themes.Apply puts together.
    public static Dictionary<string, uint> Build(AccentTheme t, bool light)
    {
        uint a = Parse(light ? t.LightA : t.A), b = Parse(light ? t.LightB : t.B), white = 0xFFFFFFFF, black = 0xFF000000;
        // The greys take a hint of the accent: green's light look isn't purple's with another accent. From the song and RGB they stay as they are.
        uint hint = Moves(t.Id) ? Parse(light ? All[0].LightA : All[0].A) : a;
        uint N(string hex) => Mix(Parse(hex), hint, 0.03);
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
        c["OnAccent"] = t.Id == RgbId ? (light ? white : Parse("#0E1014")) : Luminance(Mix(a, b, 0.5)) > 0.4 ? Parse("#0E1014") : white;
        c["Fav1"] = Mix(a, black, 0.25);
        c["Fav2"] = Mix(a, white, 0.45);
        Debug.Assert(Contrast(c["AccentText"], c["Surface"]) >= (light ? 4.5 : 3), "accent text unreadable: " + t.Id);
        // The page's own ground, where its glows fade to (HeaderGradient): Bg, or nothing over the glass look's picture. PageSolid: the
        // page's colour, solid in both looks, for what must hide what's under it (a bar over a page's bar, a page over the pages, a halo).
        c["Ground"] = c["PageSolid"] = c["Bg"];
        // (the same for a window of its own, which has no picture behind it: the profile picker)
        c["TitleBarSolid"] = c["TitleBar"];
        if (Glass.On) Frost(c, a, light);
        return c;
    }

    // The glass look (Glass): every surface on the page is a pane of the same glass (one colour, see-through as chosen, so nothing looks
    // pieced together); the controls on it a touch of white (or of black, light); what lies over the page (menus, sheets, dialogs) stays
    // nearly solid, since nothing blurs what's under it. Bg keeps its job of the wells inside a pane (fields, previews): a shade darker.
    // No glow at the top of the pages (HeaderTop): the picture is the only colour behind. The texts a little brighter (or deeper).
    private static void Frost(Dictionary<string, uint> c, uint a, bool light)
    {
        static uint W(byte alpha) => WithAlpha(0xFFFFFFFF, alpha);
        static uint K(byte alpha) => WithAlpha(0xFF000000, alpha);
        c["Ground"] = c["TitleBar"] = WithAlpha(c["Bg"], 0);
        c["Popup"] = WithAlpha(c["Popup"], 0xF2);
        c["Sheet"] = WithAlpha(c["Sheet"], 0xF5);
        c["HeaderTop"] = WithAlpha(a, 0);
        uint tint = Glass.Look.Tint switch
        {
            GlassTint.Frost => 0xFFFFFFFF,
            GlassTint.Color => light ? Mix(a, 0xFFFFFFFF, 0.8) : Mix(a, 0xFF101217, 0.72),
            _ => light ? 0xFFDCDFE5 : 0xFF101217,
        };
        uint pane = WithAlpha(tint, Glass.PaneAlpha(light));
        c["Surface"] = c["Sidebar"] = c["MiniPlayer"] = pane;
        if (light)
        {
            c["NavBar"] = WithAlpha(tint, Math.Max(Glass.PaneAlpha(true), (byte)0xB3));
            c["Bg"] = K(0x0D);
            c["Surface2"] = K(0x0F);
            c["Surface3"] = K(0x1A);
            c["Border"] = K(0x14);
            c["BorderStrong"] = K(0x26);
            c["RowHover"] = K(0x0A);
            c["Wave"] = K(0x26);
            c["ScrollThumb"] = K(0x2E);
            c["ScrollThumbHover"] = K(0x4D);
            c["SubText"] = Parse("#474E5C");
            c["Muted"] = Parse("#666D7C");
            c["AccentSoft"] = WithAlpha(a, 0x33);
        }
        else
        {
            c["NavBar"] = pane;
            c["Bg"] = K(0x40);
            c["Surface2"] = W(0x1A);
            c["Surface3"] = W(0x29);
            c["Border"] = W(0x17);
            c["BorderStrong"] = W(0x2B);
            c["RowHover"] = W(0x10);
            c["Wave"] = W(0x33);
            c["ScrollThumb"] = W(0x33);
            c["ScrollThumbHover"] = W(0x55);
            c["SubText"] = Parse("#B7BFCD");
            c["Muted"] = Parse("#8A92A5");
            c["AccentSoft"] = WithAlpha(a, 0x45);
        }
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
