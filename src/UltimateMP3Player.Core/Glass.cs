namespace UltimateMP3Player.Core;

// The glass look's choices, per profile (ProfileData.Glass, Settings › Appearance).
public sealed class GlassLook
{
    // On by default since 5.3, for the profiles made before too (they have no Glass saved: this one).
    public bool On { get; set; } = true;
    // The picture from the theme's colours instead of the cover playing; or one of your own (Profile.BackdropPicture).
    public bool FromTheme { get; set; }
    public bool Picture { get; set; }
    // The panes: GlassTint.Smoke (dark glass), Frost (white glass) or Color (the accent's).
    public string Tint { get; set; } = GlassTint.Smoke;
    // 0-1: how blurred, how bright, how colourful the picture; how solid the panes. (5.3.2: the look the author settled on.)
    public double Blur { get; set; } = 0.1;
    public double Brightness { get; set; } = 0.5;
    public double Vivid { get; set; } = 0.67;
    public double Panes { get; set; } = 0.5;
    // 0-1: the light on the panes' edges (Glass.RimAlpha); half is how 5.3 drew it, none takes the edge away.
    public double Rim { get; set; } = 0.5;
    public bool Grain { get; set; }

    // Still as 5.3 and 5.3.1 made them (saved by every profile, touched or not): the glass turned on takes today's instead.
    public bool AsFirstMade => !FromTheme && !Picture && Tint == GlassTint.Smoke && Blur == 0.5 && Brightness == 0.45 && Vivid == 0.5 && Panes == 0.55 && Rim == 0.5 && Grain;
}

public static class GlassTint
{
    public const string Smoke = "smoke", Frost = "frost", Color = "color";
}

// The glass look: the window lies on the cover of the song playing, blurred into colour, and every surface of the app is a pane of
// the same glass over it (ThemeColors.Build): one picture for the whole window, no page with a glow or a veil of its own. The picture is
// made once per song and small: stretched to the window it's all blur anyway. Nothing that moves ever lies behind a pane, only this
// picture, so the panes need no blur of their own (none of the UIs could afford one at every frame, least of all a phone).
public static class Glass
{
    // The profile's choices (AppHost.ApplyTheme).
    public static GlassLook Look { get; set; } = new();
    public static bool On => Look.On;

    // The side of the picture (the window stretches it).
    public const int Size = 72;

    // A cover (blue, green, red, alpha; any size) → the picture behind the window, Size x Size, in the same order.
    public static byte[] FromCover(byte[] bgra, int width, int height, bool light)
    {
        var px = Square(bgra, width, height);
        Blur(px, (int)Math.Round(2 + Math.Clamp(Look.Blur, 0, 1) * 9));
        Tone(px, light);
        return px;
    }

    // The theme's colours (or no song, or one without a cover): a glow of each, soft enough to need no blur.
    public static byte[] FromColors(uint a, uint b, bool light)
    {
        var px = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                double u = (x + 0.5) / Size, v = (y + 0.5) / Size;
                uint c = ThemeColors.Mix(ThemeColors.Mix(a, b, 0.5), a, Glow(u - 0.15, v - 0.1, 1.1));
                c = ThemeColors.Mix(c, b, Glow(u - 0.9, v - 0.85, 1.0));
                int at = (y * Size + x) * 4;
                px[at] = (byte)c;
                px[at + 1] = (byte)(c >> 8);
                px[at + 2] = (byte)(c >> 16);
                px[at + 3] = 255;
            }
        Tone(px, light);
        return px;
    }

    private static double Glow(double dx, double dy, double reach)
    {
        double t = 1 - Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / reach);
        return t * t * (3 - 2 * t);
    }

    // The middle square of the cover, each pixel the average of the ones it covers (no aliasing on a busy cover).
    private static byte[] Square(byte[] bgra, int width, int height)
    {
        var px = new byte[Size * Size * 4];
        int side = Math.Min(width, height), left = (width - side) / 2, top = (height - side) / 2;
        double step = (double)side / Size;
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int x0 = left + (int)(x * step), x1 = Math.Max(x0 + 1, left + (int)((x + 1) * step));
                int y0 = top + (int)(y * step), y1 = Math.Max(y0 + 1, top + (int)((y + 1) * step));
                double b = 0, g = 0, r = 0;
                int n = 0;
                for (int sy = y0; sy < y1 && sy < height; sy++)
                    for (int sx = x0; sx < x1 && sx < width; sx++)
                    {
                        int i = (sy * width + sx) * 4;
                        b += bgra[i];
                        g += bgra[i + 1];
                        r += bgra[i + 2];
                        n++;
                    }
                int at = (y * Size + x) * 4;
                n = Math.Max(n, 1);
                px[at] = (byte)(b / n);
                px[at + 1] = (byte)(g / n);
                px[at + 2] = (byte)(r / n);
                px[at + 3] = 255;
            }
        return px;
    }

    // Three box blurs each way: close to a Gaussian, the edges held (no dark rim).
    private static void Blur(byte[] px, int radius)
    {
        var line = new double[Size * 3];
        for (int pass = 0; pass < 3; pass++)
            for (int dir = 0; dir < 2; dir++)
                for (int row = 0; row < Size; row++)
                {
                    int At(int i) => (dir == 0 ? row * Size + i : i * Size + row) * 4;
                    for (int i = 0; i < Size; i++)
                        for (int c = 0; c < 3; c++) line[i * 3 + c] = px[At(i) + c];
                    for (int i = 0; i < Size; i++)
                        for (int c = 0; c < 3; c++)
                        {
                            double sum = 0;
                            for (int k = -radius; k <= radius; k++) sum += line[Math.Clamp(i + k, 0, Size - 1) * 3 + c];
                            px[At(i) + c] = (byte)Math.Round(sum / (2 * radius + 1));
                        }
                }
    }

    // The colours as strong as chosen, then dark enough for white text or pale enough for black text, wherever it is, by the brightness
    // chosen: the same all over the window (no corner of its own), only a breath darker at the very edges in the dark look.
    private static void Tone(byte[] px, bool light)
    {
        double bright = Math.Clamp(Look.Brightness, 0, 1), more = 0.3 + 1.7 * Math.Clamp(Look.Vivid, 0, 1);
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int at = (y * Size + x) * 4;
                double b = px[at] / 255.0, g = px[at + 1] / 255.0, r = px[at + 2] / 255.0;
                double luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                r = Math.Clamp(luma + (r - luma) * more, 0, 1);
                g = Math.Clamp(luma + (g - luma) * more, 0, 1);
                b = Math.Clamp(luma + (b - luma) * more, 0, 1);
                luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                if (light)
                {
                    // (washed with white, then never below the least: the text is nearly black)
                    double wash = 0.3 + 0.55 * bright, least = 0.68 + 0.22 * bright;
                    r += (1 - r) * wash; g += (1 - g) * wash; b += (1 - b) * wash;
                    luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    if (luma < least)
                    {
                        double k = (least - luma) / (1 - luma);
                        r += (1 - r) * k; g += (1 - g) * k; b += (1 - b) * k;
                    }
                }
                else
                {
                    // (never above the most: white text keeps its contrast, and the panes add a little light)
                    double most = 0.1 + 0.3 * bright, k = 0.25 + 0.75 * bright;
                    if (luma * k > most) k = most / luma;
                    double dx = (x + 0.5) / Size - 0.5, dy = (y + 0.5) / Size - 0.5;
                    k *= 1 - 0.3 * Math.Min(1, (dx * dx + dy * dy) * 2);
                    r = r * k + 0.02; g = g * k + 0.022; b = b * k + 0.03;
                }
                px[at] = (byte)Math.Round(Math.Clamp(b, 0, 1) * 255);
                px[at + 1] = (byte)Math.Round(Math.Clamp(g, 0, 1) * 255);
                px[at + 2] = (byte)Math.Round(Math.Clamp(r, 0, 1) * 255);
            }
    }

    // The colour along the top of a picture (blue, green, red, alpha), what the title bar shows: the window's own ground under it, seen only
    // while the window grows (see-through, Windows showed white there until the next frame).
    public static uint TopColor(byte[] px)
    {
        long b = 0, g = 0, r = 0;
        int n = Size * (Size / 8);
        for (int i = 0; i < n * 4; i += 4)
        {
            b += px[i];
            g += px[i + 1];
            r += px[i + 2];
        }
        return 0xFF000000 | (uint)(r / n) << 16 | (uint)(g / n) << 8 | (uint)(b / n);
    }

    // What floats over the window (menus, drop-downs, dialogs, sheets: Views/Glassy) is a pane of the same glass: the picture with a pane's
    // tint over it (argb), made once per picture and tint. Blending with one colour and stretching commute: the same as a pane over the picture.
    public static byte[] Tinted(byte[] px, uint argb)
    {
        var tinted = new byte[px.Length];
        double alpha = (argb >> 24) / 255.0;
        for (int i = 0; i < px.Length; i += 4)
        {
            tinted[i] = (byte)Math.Round(px[i] + ((byte)argb - px[i]) * alpha);
            tinted[i + 1] = (byte)Math.Round(px[i + 1] + ((byte)(argb >> 8) - px[i + 1]) * alpha);
            tinted[i + 2] = (byte)Math.Round(px[i + 2] + ((byte)(argb >> 16) - px[i + 2]) * alpha);
            tinted[i + 3] = 255;
        }
        return tinted;
    }

    // Where the window's picture lies, seen from something floating over it whose top-left corner is at left, top: the window stretches the
    // picture uniformly to fill (a square on its longer side, centred). All in the same units; the square's corner and side.
    public static (double X, double Y, double Side) Slice(double windowLeft, double windowTop, double windowWidth, double windowHeight, double left, double top)
    {
        double side = Math.Max(windowWidth, windowHeight);
        return (windowLeft - (side - windowWidth) / 2 - left, windowTop - (side - windowHeight) / 2 - top, side);
    }

    // The frost: a fine grain over the picture (tiled), which also hides the steps of a dark gradient. Grey, the alpha does it.
    public const int GrainSize = 96;

    public static byte[] GrainPixels()
    {
        var px = new byte[GrainSize * GrainSize * 4];
        var random = new Random(11);
        for (int i = 0; i < px.Length; i += 4)
        {
            byte v = (byte)(random.Next(2) * 255);
            px[i] = px[i + 1] = px[i + 2] = v;
            px[i + 3] = (byte)random.Next(0, 22);
        }
        return px;
    }

    // The panes' edges (ThemeColors' Rim0-2): each stop of the light as strong as chosen (half: as given, none: gone).
    public static byte RimAlpha(int half) => (byte)Math.Min(255, Math.Round(half * Math.Clamp(Look.Rim, 0, 1) * 2));

    // How see-through a pane is (0-255), by the choice; frosted white is much stronger than dark glass, so it takes less.
    public static byte PaneAlpha(bool light)
    {
        double p = Math.Clamp(Look.Panes, 0, 1);
        if (light) return (byte)(0x26 + p * 0xC8);
        return Look.Tint == GlassTint.Frost ? (byte)(0x08 + p * 0x40) : (byte)(0x14 + p * 0xD0);
    }
}
