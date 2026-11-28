using System.Globalization;
using System.Numerics;

namespace UltimateMP3Player.Core.Mods;

// What a mod's visualizer draws in one frame, as a flat list of commands: recorded on the mod's thread (ModCanvasRecorder),
// drawn by Skia on every UI (Avalonia/Views/ModSkia.cs, linked into WPF and the phones).
// Each command carries everything it needs (transform, alpha, paint), so the renderers keep no state but the clips and layers
// (ClipPush and LayerPush are each closed by their own pop, in the order they were opened).
public enum DrawOp : byte { Rect, Path, Image, Text, ClipPush, ClipPop, LayerPush, LayerPop }

public enum LineCap : byte { Butt, Round, Square }
public enum LineJoin : byte { Miter, Round, Bevel }
public enum TextAlign : byte { Left, Center, Right }
public enum TextBaseline : byte { Alphabetic, Top, Middle, Bottom }

// globalCompositeOperation: the canvas' names for the ones there are here.
public enum ModBlend : byte { SourceOver, Lighter, DestinationOut, DestinationIn, SourceAtop, Multiply, Screen, Copy }

public struct DrawCmd
{
    public DrawOp Op;
    public bool Stroke;
    public ModBlend Blend;
    public readonly bool Additive => Blend == ModBlend.Lighter;
    // filter "blur(Npx)": how far it spreads (the standard deviation), in the canvas' units whatever the transform; 0: none.
    public float Blur;
    public Matrix3x2 M;
    public float Alpha;
    public uint Color;              // ARGB
    public ModGradient? Gradient;   // instead of Color
    public float LineWidth;
    public LineCap Cap;
    public LineJoin Join;
    public float X, Y, W, H;        // a rect, an image's place, a text's point
    public float SX, SY, SW, SH;    // the part of the image (SW 0: all of it)
    public uint Tint;               // an image multiplied by a colour (0: none)
    public ModPath? Path;
    public ModImage? Image;
    public string? Text;
    public float FontSize;
    public bool Bold;
    public TextAlign Align;
    public TextBaseline Baseline;
}

public sealed class DrawList
{
    public readonly List<DrawCmd> Cmds = new();
    public float Width, Height;
    public int Frame;
}

public enum SegKind : byte { Move, Line, Quad, Cubic, Close }

public readonly record struct PathSeg(SegKind Kind, float X1, float Y1, float X2 = 0, float Y2 = 0, float X3 = 0, float Y3 = 0);

// A path: lines and curves (arcs and ellipses become curves here, so the renderers only know these). A renderer keeps what
// it made of it in Native, made again when Version moves (a Path2D added to after being drawn).
public sealed class ModPath
{
    public readonly List<PathSeg> Segs = new();
    public int Version;
    public object? Native;
    public int NativeVersion = -1;
    private float _x, _y, _startX, _startY;
    private bool _open;

    private void Add(PathSeg s)
    {
        if (Segs.Count >= 100_000) throw new ModException("A path can have at most 100000 parts.");
        Segs.Add(s);
        Version++;
    }

    public void MoveTo(float x, float y)
    {
        Add(new(SegKind.Move, x, y));
        (_x, _y, _startX, _startY, _open) = (x, y, x, y, true);
    }

    private void Ensure(float x, float y)
    {
        if (!_open) MoveTo(x, y);
    }

    public void LineTo(float x, float y)
    {
        Ensure(x, y);
        Add(new(SegKind.Line, x, y));
        (_x, _y) = (x, y);
    }

    public void QuadTo(float cx, float cy, float x, float y)
    {
        Ensure(cx, cy);
        Add(new(SegKind.Quad, cx, cy, x, y));
        (_x, _y) = (x, y);
    }

    public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
    {
        Ensure(c1x, c1y);
        Add(new(SegKind.Cubic, c1x, c1y, c2x, c2y, x, y));
        (_x, _y) = (x, y);
    }

    public void Close()
    {
        if (!_open) return;
        Add(new(SegKind.Close, 0, 0));
        (_x, _y) = (_startX, _startY);
    }

    public void Rect(float x, float y, float w, float h)
    {
        MoveTo(x, y);
        LineTo(x + w, y);
        LineTo(x + w, y + h);
        LineTo(x, y + h);
        Close();
        MoveTo(x, y);
    }

    public void RoundRect(float x, float y, float w, float h, float r)
    {
        r = Math.Clamp(r, 0, Math.Min(Math.Abs(w), Math.Abs(h)) / 2);
        if (r <= 0) { Rect(x, y, w, h); return; }
        MoveTo(x + r, y);
        LineTo(x + w - r, y);
        Ellipse(x + w - r, y + r, r, r, 0, -MathF.PI / 2, 0, false, connect: true);
        LineTo(x + w, y + h - r);
        Ellipse(x + w - r, y + h - r, r, r, 0, 0, MathF.PI / 2, false, connect: true);
        LineTo(x + r, y + h);
        Ellipse(x + r, y + h - r, r, r, 0, MathF.PI / 2, MathF.PI, false, connect: true);
        LineTo(x, y + r);
        Ellipse(x + r, y + r, r, r, 0, MathF.PI, MathF.PI * 1.5f, false, connect: true);
        Close();
    }

    // Canvas' arc and ellipse: from angle a0 to a1 (clockwise on screen unless ccw), joined by a line to where the path was.
    public void Ellipse(float cx, float cy, float rx, float ry, float rotation, float a0, float a1, bool ccw, bool connect = true)
    {
        if (rx < 0 || ry < 0) throw new ModException("An arc's radius can't be negative.");
        const float Tau = MathF.PI * 2;
        float sweep = a1 - a0;
        if (!ccw)
        {
            if (sweep >= Tau) sweep = Tau;
            else if (sweep < 0) sweep = sweep % Tau + Tau;
        }
        else
        {
            if (sweep <= -Tau) sweep = -Tau;
            else if (sweep > 0) sweep = sweep % Tau - Tau;
        }
        float cos = MathF.Cos(rotation), sin = MathF.Sin(rotation);
        (float, float) P(float a)
        {
            float ex = rx * MathF.Cos(a), ey = ry * MathF.Sin(a);
            return (cx + ex * cos - ey * sin, cy + ex * sin + ey * cos);
        }
        (float, float) D(float a, float k)
        {
            float ex = -rx * MathF.Sin(a) * k, ey = ry * MathF.Cos(a) * k;
            return (ex * cos - ey * sin, ex * sin + ey * cos);
        }
        var (sx, sy) = P(a0);
        if (_open && connect) LineTo(sx, sy);
        else MoveTo(sx, sy);
        // At most a quarter turn per curve: close enough to a circle that no one sees the difference.
        int parts = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(sweep) / (MathF.PI / 2) - 1e-4f));
        float step = sweep / parts, k = 4f / 3f * MathF.Tan(step / 4);
        float a = a0;
        for (int i = 0; i < parts; i++)
        {
            float b = a + step;
            var (x0, y0) = P(a);
            var (x3, y3) = P(b);
            var (d0x, d0y) = D(a, k);
            var (d3x, d3y) = D(b, k);
            CubicTo(x0 + d0x, y0 + d0y, x3 - d3x, y3 - d3y, x3, y3);
            a = b;
        }
    }
}

public sealed class ModGradient
{
    public bool Radial;
    public float X0, Y0, R0, X1, Y1, R1;
    public readonly List<(float Offset, uint Color)> Stops = new();
    public int Version;
    public object? Native;
    public int NativeVersion = -1;
}

// A picture decoded by the UI side (IModHost.LoadImage); Native is Skia's image.
public sealed class ModImage(int width, int height, object native)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public object Native { get; } = native;
}

// A picture's pixels blurred in place (4 bytes a pixel, any order of the channels): three box blurs each way, close to a
// gaussian. Done once when a mod loads a picture blurred (images.load's blur: a map's background), never per frame.
public static class PixelBlur
{
    public static void Blur(byte[] px, int w, int h, float radius)
    {
        int r = (int)Math.Round(radius / 2);
        if (r < 1 || w < 2 || h < 2 || px.Length < w * h * 4) return;
        var tmp = new byte[px.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            Box(px, tmp, w, h, r, true);
            Box(tmp, px, w, h, r, false);
        }
    }

    private static void Box(byte[] src, byte[] dst, int w, int h, int r, bool rows)
    {
        int lines = rows ? h : w, length = rows ? w : h, step = rows ? 4 : w * 4, lineStep = rows ? w * 4 : 4, span = 2 * r + 1;
        Parallel.For(0, lines, line =>
        {
            int start = line * lineStep;
            for (int c = 0; c < 4; c++)
            {
                int sum = 0;
                for (int k = -r; k <= r; k++) sum += src[start + Math.Clamp(k, 0, length - 1) * step + c];
                for (int i = 0; i < length; i++)
                {
                    dst[start + i * step + c] = (byte)((sum + r) / span);
                    sum += src[start + Math.Min(i + r + 1, length - 1) * step + c] - src[start + Math.Max(i - r, 0) * step + c];
                }
            }
        });
    }
}

// What a visualizer gets each frame (f in frame(ctx, f)).
public sealed class ModFrameInfo
{
    public double Time;          // ms of the song heard now
    public double Duration;      // ms
    public bool Playing;
    public double Speed = 1;
    public float Width, Height;  // in the UI's units
    public float Scale = 1;      // screen pixels per unit
    public float[]? Bands;       // the bars of the music (Spectrum.BandCount, 0..1), when wanted
    public uint Accent, Text;    // the theme's colours, ARGB
    public string Place = "song";
    public bool Glass;
    // A "page" visualizer: where the cover would be (units, from its top left), and whether its video shows under it.
    public float StageX, StageY, StageWidth, StageHeight;
    public bool Video;
    // How much of the page's bottom the app's player bar lies over (units; over the whole screen, where the page reaches under it).
    public float Below;
}

// CSS colours: #rgb, #rgba, #rrggbb, #rrggbbaa, rgb(), rgba(), a few names. Anything else: null.
public static class CssColor
{
    private static readonly Dictionary<string, uint> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["transparent"] = 0x00000000, ["black"] = 0xFF000000, ["white"] = 0xFFFFFFFF, ["red"] = 0xFFFF0000, ["green"] = 0xFF008000,
        ["lime"] = 0xFF00FF00, ["blue"] = 0xFF0000FF, ["yellow"] = 0xFFFFFF00, ["cyan"] = 0xFF00FFFF, ["aqua"] = 0xFF00FFFF,
        ["magenta"] = 0xFFFF00FF, ["fuchsia"] = 0xFFFF00FF, ["gray"] = 0xFF808080, ["grey"] = 0xFF808080, ["orange"] = 0xFFFFA500,
        ["purple"] = 0xFF800080, ["pink"] = 0xFFFFC0CB,
    };

    public static uint? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (Names.TryGetValue(s, out var named)) return named;
        if (s[0] == '#')
        {
            var hex = s[1..];
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return null;
            return hex.Length switch
            {
                3 => 0xFF000000 | Twice(v >> 8) << 16 | Twice(v >> 4) << 8 | Twice(v),
                4 => Twice(v) << 24 | Twice(v >> 12) << 16 | Twice(v >> 8) << 8 | Twice(v >> 4),
                6 => 0xFF000000 | v,
                8 => (v & 0xFF) << 24 | v >> 8,
                _ => null,
            };
        }
        int open = s.IndexOf('('), close = s.LastIndexOf(')');
        if (open < 3 || close < open) return null;
        var fn = s[..open].Trim().ToLowerInvariant();
        if (fn is not ("rgb" or "rgba")) return null;
        var parts = s[(open + 1)..close].Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 3 or > 4) return null;
        double Part(string p, double max) => p.EndsWith('%')
            ? double.Parse(p[..^1], CultureInfo.InvariantCulture) / 100 * max
            : double.Parse(p, CultureInfo.InvariantCulture);
        try
        {
            uint C(double d, double max) => (uint)Math.Clamp(Math.Round(d / max * 255), 0, 255);
            var a = parts.Length == 4 ? C(Part(parts[3], 1), 1) : 255;
            return a << 24 | C(Part(parts[0], 255), 255) << 16 | C(Part(parts[1], 255), 255) << 8 | C(Part(parts[2], 255), 255);
        }
        catch (FormatException) { return null; }
    }

    private static uint Twice(uint nibble) => (nibble & 0xF) * 0x11;

    public static string Of(uint argb) => (argb >> 24) == 0xFF ? $"#{argb & 0xFFFFFF:x6}" : $"#{argb & 0xFFFFFF:x6}{argb >> 24:x2}";
}
