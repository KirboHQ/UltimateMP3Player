using System.Numerics;
using System.Runtime.InteropServices;
using SkiaSharp;
using UltimateMP3Player.Core.Mods;

namespace UltimateMP3Player.Views;

// A mod's draw list (Core/Mods/ModCanvas.cs) drawn with Skia, the same on every UI: Avalonia and the phones on their render
// thread (ModCanvasView), WPF into a picture of its own (linked there). The pictures mods load are decoded here too.
public static class ModSkia
{
    private static readonly SKPaint Paint = new() { IsAntialias = true };
    private static readonly SKTypeface BoldFace = SKTypeface.FromFamilyName(null, SKFontStyle.Bold) ?? SKTypeface.Default;
    private static readonly Dictionary<uint, SKColorFilter> Tints = new();
    // (a WPF window and an Avalonia one never draw at once, but two WPF views may: one list at a time; WPF's bands each have a
    // paint of their own)
    private static readonly object Gate = new();

    private static SKMatrix Sk(Matrix3x2 m) => new(m.M11, m.M21, m.M31, m.M12, m.M22, m.M32, 0, 0, 1);

    private static SKColor Color(uint argb, float alpha) => new((uint)((uint)Math.Clamp((argb >> 24) * alpha, 0, 255) << 24 | argb & 0xFFFFFF));

    private static SKBlendMode BlendOf(ModBlend b) => b switch
    {
        ModBlend.Lighter => SKBlendMode.Plus,
        ModBlend.DestinationOut => SKBlendMode.DstOut,
        ModBlend.DestinationIn => SKBlendMode.DstIn,
        ModBlend.SourceAtop => SKBlendMode.SrcATop,
        ModBlend.Multiply => SKBlendMode.Multiply,
        ModBlend.Screen => SKBlendMode.Screen,
        ModBlend.Copy => SKBlendMode.Src,
        _ => SKBlendMode.SrcOver,
    };

    // How much a transform enlarges (a blur is in the canvas' units, whatever the mod's transform).
    private static float ScaleOf(Matrix3x2 m) => MathF.Sqrt(MathF.Abs(m.M11 * m.M22 - m.M12 * m.M21));

    public static void Draw(SKCanvas canvas, DrawList list)
    {
        lock (Gate) DrawLocked(canvas, list, Paint);
    }

    // WPF's frames, drawn on the CPU: in bands one above the other, each on its own core (each band draws the whole list, only its
    // rows kept, as Skia does tiles), so a page as big as the window keeps up with a fast screen. Into bitmap, cleared first.
    // still (one per view): what stayed the same at the start of the list since the last frame is copied from it, not drawn again.
    public static void DrawBands(SKBitmap bitmap, DrawList list, Backdrop? still = null)
    {
        lock (Gate)
        {
            Prepare(list);
            int w = bitmap.Width, h = bitmap.Height;
            int n = h < 256 ? 1 : Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
            float sx = w / list.Width, sy = h / list.Height;
            int from = still != null ? Settle(still, list, w, h, n, sx, sy) : 0;
            var under = from > 0 ? still!.Image : null;
            Parallel.For(0, n, k => Band(bitmap, list, k, n, sx, sy, from, list.Cmds.Count, under));
        }
    }

    // One band of rows: the backdrop's (or nothing), then the commands from..to.
    private static void Band(SKBitmap bitmap, DrawList list, int k, int n, float sx, float sy, int from, int to, SKImage? under)
    {
        int w = bitmap.Width, h = bitmap.Height;
        int y0 = h * k / n, y1 = h * (k + 1) / n;
        using var band = new SKBitmap();
        if (!bitmap.ExtractSubset(band, new SKRectI(0, y0, w, y1))) return;
        using var canvas = new SKCanvas(band);
        using var paint = new SKPaint { IsAntialias = true };
        if (under != null)
        {
            // (pixel for pixel, replacing what was there: a copy)
            paint.BlendMode = SKBlendMode.Src;
            canvas.DrawImage(under, 0, -y0, paint);
        }
        else canvas.Clear(SKColors.Transparent);
        canvas.Translate(0, -y0);
        canvas.Scale(sx, sy);
        DrawLocked(canvas, list, paint, from, to);
    }

    // The start of a page that stays the same from frame to frame (a mod's background: its picture, the dim and the shades over
    // it), drawn once into a picture of its own and copied under the frames after: on the CPU a picture over the whole page costs
    // more than all the rest (the osu! page at 2560×1080: 3 of its 5 ms). One per view.
    public sealed class Backdrop : IDisposable
    {
        internal SKBitmap? Pixels;
        // Pixels as a picture to draw, made once each time they're drawn (a bitmap drawn as it is is copied whole at every draw:
        // 11 MB a band a frame).
        internal SKImage? Image;
        // How many of the last frame's first commands Pixels holds; the size it was drawn for.
        internal int Count, Longer;
        internal float Width, Height;
        // The last frame's first commands (up to its first clip or layer), with the versions of their paths and gradients.
        internal DrawCmd[] Last = Array.Empty<DrawCmd>();
        internal (int Path, int Gradient)[] Versions = Array.Empty<(int, int)>();
        internal int LastCount;

        public void Dispose()
        {
            lock (Gate)
            {
                Image?.Dispose();
                Pixels?.Dispose();
                (Image, Pixels, Count, LastCount) = (null, null, 0, 0);
            }
        }
    }

    // How many of the list's first commands the backdrop holds for this frame (0: none), drawn again when what stays changed.
    private static int Settle(Backdrop b, DrawList list, int w, int h, int n, float sx, float sy)
    {
        var cmds = CollectionsMarshal.AsSpan(list.Cmds);
        int lead = 0;
        while (lead < cmds.Length && cmds[lead].Op is DrawOp.Rect or DrawOp.Path or DrawOp.Image or DrawOp.Text) lead++;
        int same = 0;
        while (same < lead && same < b.LastCount && Same(cmds[same], b.Last[same], b.Versions[same])) same++;
        if (b.Last.Length < lead) (b.Last, b.Versions) = (new DrawCmd[lead], new (int, int)[lead]);
        for (int i = 0; i < lead; i++) (b.Last[i], b.Versions[i]) = (cmds[i], (cmds[i].Path?.Version ?? 0, cmds[i].Gradient?.Version ?? 0));
        b.LastCount = lead;
        // (what it holds stays good while this frame starts the same way as the last; more of it the same for a while: drawn again,
        // longer, not at every frame that happens to match a little further)
        bool fits = b.Pixels != null && b.Pixels.Width == w && b.Pixels.Height == h && b.Width == list.Width && b.Height == list.Height;
        if (fits && b.Count > 0 && same >= b.Count)
        {
            b.Longer = same > b.Count ? b.Longer + 1 : 0;
            if (b.Longer < 8) return b.Count;
        }
        b.Longer = 0;
        if (same == 0)
        {
            b.Count = 0;
            return 0;
        }
        b.Image?.Dispose();
        b.Image = null;
        if (!fits)
        {
            b.Pixels?.Dispose();
            b.Pixels = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            (b.Width, b.Height) = (list.Width, list.Height);
        }
        var pixels = b.Pixels!;
        Parallel.For(0, n, k => Band(pixels, list, k, n, sx, sy, 0, same, null));
        // (on the same memory, not a copy: drawn into only again once this picture is gone)
        using (var pixmap = pixels.PeekPixels()) b.Image = SKImage.FromPixels(pixmap);
        b.Count = b.Image != null ? same : 0;
        return same;
    }

    // Two commands that draw the same pixels (the same path or gradient only if not added to since).
    private static bool Same(in DrawCmd a, in DrawCmd b, (int Path, int Gradient) v) =>
        a.Op == b.Op && a.Stroke == b.Stroke && a.Blend == b.Blend && a.Blur == b.Blur && a.M == b.M && a.Alpha == b.Alpha
        && a.Color == b.Color && ReferenceEquals(a.Gradient, b.Gradient) && (a.Gradient?.Version ?? 0) == v.Gradient
        && a.LineWidth == b.LineWidth && a.Cap == b.Cap && a.Join == b.Join
        && a.X == b.X && a.Y == b.Y && a.W == b.W && a.H == b.H && a.SX == b.SX && a.SY == b.SY && a.SW == b.SW && a.SH == b.SH
        && a.Tint == b.Tint && ReferenceEquals(a.Path, b.Path) && (a.Path?.Version ?? 0) == v.Path && ReferenceEquals(a.Image, b.Image)
        && a.Text == b.Text && a.FontSize == b.FontSize && a.Bold == b.Bold && a.Align == b.Align && a.Baseline == b.Baseline;

    // The paths, shaders and tints of a list made before its bands draw at once: then they only read them.
    private static void Prepare(DrawList list)
    {
        foreach (ref readonly var c in CollectionsMarshal.AsSpan(list.Cmds))
        {
            if (c.Path != null) _ = PathOf(c.Path).Bounds;
            if (c.Gradient is { Stops.Count: > 0 } g) ShaderOf(g);
            if (c.Tint != 0) TintOf(c.Tint);
        }
    }

    private static SKColorFilter TintOf(uint tint)
    {
        if (!Tints.TryGetValue(tint, out var filter)) Tints[tint] = filter = SKColorFilter.CreateBlendMode(new SKColor(tint | 0xFF000000), SKBlendMode.Modulate);
        return filter;
    }

    // The commands from..to (a backdrop's first ones have no clip or layer: starting after them leaves none open).
    private static void DrawLocked(SKCanvas canvas, DrawList list, SKPaint paint, int from = 0, int to = int.MaxValue)
    {
        // Clips and layers are both a save on the canvas, closed in the order they were opened.
        int depth = 0;
        var cmds = CollectionsMarshal.AsSpan(list.Cmds);
        foreach (ref readonly var c in cmds[from..Math.Min(to, cmds.Length)])
        {
            if (c.Op is DrawOp.ClipPop or DrawOp.LayerPop)
            {
                if (depth > 0) { canvas.Restore(); depth--; }
                continue;
            }
            if (c.Op == DrawOp.LayerPush)
            {
                using var layer = new SKPaint { Color = new SKColor(255, 255, 255, (byte)Math.Clamp(c.Alpha * 255, 0, 255)), BlendMode = BlendOf(c.Blend) };
                if (c.Blur > 0) layer.ImageFilter = SKImageFilter.CreateBlur(c.Blur, c.Blur);
                canvas.SaveLayer(layer);
                depth++;
                continue;
            }
            var keep = canvas.TotalMatrix;
            var m = Sk(c.M);
            if (c.Op == DrawOp.ClipPush)
            {
                canvas.Save();
                depth++;
                canvas.Concat(ref m);
                if (c.Path != null) canvas.ClipPath(PathOf(c.Path), SKClipOperation.Intersect, true);
                canvas.SetMatrix(keep);
                continue;
            }
            float scale = ScaleOf(c.M);
            if (c.Blur > 0 && scale <= 0) continue;
            canvas.Concat(ref m);
            Setup(c, paint);
            SKMaskFilter? blur = c.Blur > 0 && c.Op != DrawOp.Image ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, c.Blur / scale) : null;
            paint.MaskFilter = blur;
            switch (c.Op)
            {
                case DrawOp.Rect:
                    canvas.DrawRect(SKRect.Create(c.X, c.Y, c.W, c.H).Standardized, paint);
                    break;
                case DrawOp.Path when c.Path != null:
                    canvas.DrawPath(PathOf(c.Path), paint);
                    break;
                case DrawOp.Image when c.Image?.Native is SKImage img:
                    paint.Style = SKPaintStyle.Fill;
                    paint.Shader = null;
                    paint.Color = new SKColor(255, 255, 255, (byte)Math.Clamp(c.Alpha * 255, 0, 255));
                    if (c.Tint != 0) paint.ColorFilter = TintOf(c.Tint);
                    if (c.Blur > 0) paint.ImageFilter = SKImageFilter.CreateBlur(c.Blur / scale, c.Blur / scale);
                    var src = c.SW > 0 && c.SH > 0 ? SKRect.Create(c.SX, c.SY, c.SW, c.SH) : SKRect.Create(0, 0, img.Width, img.Height);
                    canvas.DrawImage(img, src, SKRect.Create(c.X, c.Y, c.W, c.H).Standardized, paint);
                    if (paint.ImageFilter is { } filter) { paint.ImageFilter = null; filter.Dispose(); }
                    break;
                case DrawOp.Text when c.Text != null:
                    using (var font = new SKFont(c.Bold ? BoldFace : SKTypeface.Default, c.FontSize))
                    {
                        float x = c.X, y = c.Y;
                        if (c.Align != TextAlign.Left)
                        {
                            float w = Width(font, c.Text);
                            x -= c.Align == TextAlign.Center ? w / 2 : w;
                        }
                        var metrics = font.Metrics;
                        y -= c.Baseline switch
                        {
                            TextBaseline.Top => metrics.Ascent,
                            TextBaseline.Middle => (metrics.Ascent + metrics.Descent) / 2,
                            TextBaseline.Bottom => metrics.Descent,
                            _ => 0,
                        };
                        canvas.DrawText(c.Text, x, y, font, paint);
                    }
                    break;
            }
            if (blur != null) { paint.MaskFilter = null; blur.Dispose(); }
            canvas.SetMatrix(keep);
        }
        while (depth-- > 0) canvas.Restore();
    }

    private static void Setup(in DrawCmd c, SKPaint paint)
    {
        paint.ColorFilter = null;
        paint.Style = c.Stroke ? SKPaintStyle.Stroke : SKPaintStyle.Fill;
        paint.StrokeWidth = c.LineWidth;
        paint.StrokeCap = c.Cap switch { LineCap.Round => SKStrokeCap.Round, LineCap.Square => SKStrokeCap.Square, _ => SKStrokeCap.Butt };
        paint.StrokeJoin = c.Join switch { LineJoin.Round => SKStrokeJoin.Round, LineJoin.Bevel => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Miter };
        paint.BlendMode = BlendOf(c.Blend);
        paint.FilterQuality = SKFilterQuality.Medium;
        if (c.Gradient is { Stops.Count: > 0 } g)
        {
            paint.Shader = ShaderOf(g);
            paint.Color = new SKColor(255, 255, 255, (byte)Math.Clamp(c.Alpha * 255, 0, 255));
        }
        else
        {
            paint.Shader = null;
            paint.Color = Color(c.Color, c.Alpha);
        }
    }

    private static SKShader ShaderOf(ModGradient g)
    {
        if (g.Native is SKShader known && g.NativeVersion == g.Version) return known;
        var colors = g.Stops.Select(s => new SKColor(s.Color)).ToArray();
        var at = g.Stops.Select(s => s.Offset).ToArray();
        var shader = g.Radial
            ? SKShader.CreateTwoPointConicalGradient(new SKPoint(g.X0, g.Y0), g.R0, new SKPoint(g.X1, g.Y1), g.R1, colors, at, SKShaderTileMode.Clamp)
            : SKShader.CreateLinearGradient(new SKPoint(g.X0, g.Y0), new SKPoint(g.X1, g.Y1), colors, at, SKShaderTileMode.Clamp);
        (g.Native, g.NativeVersion) = (shader, g.Version);
        return shader;
    }

    private static SKPath PathOf(ModPath p)
    {
        if (p.Native is SKPath known && p.NativeVersion == p.Version) return known;
        var path = new SKPath { FillType = SKPathFillType.Winding };
        foreach (var s in CollectionsMarshal.AsSpan(p.Segs))
            switch (s.Kind)
            {
                case SegKind.Move: path.MoveTo(s.X1, s.Y1); break;
                case SegKind.Line: path.LineTo(s.X1, s.Y1); break;
                case SegKind.Quad: path.QuadTo(s.X1, s.Y1, s.X2, s.Y2); break;
                case SegKind.Cubic: path.CubicTo(s.X1, s.Y1, s.X2, s.Y2, s.X3, s.Y3); break;
                case SegKind.Close: path.Close(); break;
            }
        (p.Native, p.NativeVersion) = (path, p.Version);
        return path;
    }

    // ---- for the mods' threads (ModsViewModel → IModHost)
    public static ModImage? Decode(string file)
    {
        try
        {
            using var codec = SKCodec.Create(file);
            if (codec == null || codec.Info.Width is <= 0 or > 8192 || codec.Info.Height is <= 0 or > 8192) return null;
            using var bitmap = SKBitmap.Decode(codec);
            if (bitmap == null) return null;
            var image = SKImage.FromBitmap(bitmap);
            return image == null ? null : new ModImage(image.Width, image.Height, image);
        }
        catch (Exception) { return null; }
    }

    // Smaller (at most maxSide pixels a side) and blurred, made once: scaled down, then blurred on its pixels (Core's PixelBlur).
    public static ModImage? Decode(string file, int maxSide, float blur)
    {
        try
        {
            using var codec = SKCodec.Create(file);
            if (codec == null || codec.Info.Width is <= 0 or > 8192 || codec.Info.Height is <= 0 or > 8192) return null;
            using var decoded = SKBitmap.Decode(codec);
            if (decoded == null) return null;
            double k = maxSide > 0 ? Math.Min(1, maxSide / (double)Math.Max(decoded.Width, decoded.Height)) : 1;
            var info = new SKImageInfo(Math.Max(1, (int)Math.Round(decoded.Width * k)), Math.Max(1, (int)Math.Round(decoded.Height * k)), SKColorType.Bgra8888, SKAlphaType.Premul);
            using var scaled = new SKBitmap(info);
#pragma warning disable CS0618
            if (!decoded.ScalePixels(scaled, SKFilterQuality.Medium)) return null;
#pragma warning restore CS0618
            var px = scaled.Bytes;
            if (blur > 0) PixelBlur.Blur(px, info.Width, info.Height, blur);
            var image = SKImage.FromPixelCopy(info, px, info.RowBytes);
            return image == null ? null : new ModImage(image.Width, image.Height, image);
        }
        catch (Exception) { return null; }
    }

    public static float Measure(string text, float size, bool bold)
    {
        using var font = new SKFont(bold ? BoldFace : SKTypeface.Default, Math.Clamp(size, 1, 1000));
        return Width(font, text);
    }

    // SkiaSharp 2.88's SKFont measures only glyphs: its paint measures words.
    private static float Width(SKFont font, string text)
    {
        using var paint = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
        return paint.MeasureText(text);
    }
}
