using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace UltimateMP3Player.Views;

// The song page's pictures (the computer apps' NowPlayingView): the cover as one rounded image, and the glow of its
// colours behind the page (its strongest colour with a very blurred copy of it over it, like Spotify).
public static class CoverArt
{
    // The cover, rounded, drawn at twice its size (sharp on the phone's dense screen).
    public static Bitmap Bake(Bitmap? img, int pixels = 900)
    {
        const double size = 400, radius = 16;
        var rect = new Rect(0, 0, size, size);
        double scale = pixels / size;
        var bmp = new RenderTargetBitmap(new PixelSize(pixels, pixels), new Vector(96, 96));
        using (var dc = bmp.CreateDrawingContext())
        using (dc.PushTransform(Matrix.CreateScale(scale, scale)))
        {
            using (dc.PushClip(new RoundedRect(rect, radius)))
            {
                if (img is { PixelSize.Width: > 0, PixelSize.Height: > 0 })
                {
                    double s = Math.Max(size / img.Size.Width, size / img.Size.Height);
                    double w = img.Size.Width * s, h = img.Size.Height * s;
                    using (dc.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                        dc.DrawImage(img, new Rect(0, 0, img.Size.Width, img.Size.Height), new Rect((size - w) / 2, (size - h) / 2, w, h));
                }
                else
                {
                    dc.FillRectangle(Ui.Find<IBrush>("PlaceholderGradient"), rect);
                    var note = new FormattedText(Icons.Map(""), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface(Icons.Regular), 110, Ui.Res("MutedBrush"));
                    dc.DrawText(note, new Point((size - note.Width) / 2, (size - note.Height) / 2));
                }
            }
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)), 1.5), new Rect(0.75, 0.75, size - 1.5, size - 1.5), radius, radius);
        }
        return bmp;
    }

    public static Bitmap Glow(Bitmap? img)
    {
        const int size = 120, spill = 40;
        bool hasImage = img is { PixelSize.Width: > 0, PixelSize.Height: > 0 };
        var mood = hasImage ? Mood(img!) : Darken((Ui.Res("AccentBrush") as ISolidColorBrush)?.Color ?? Colors.MediumPurple);
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(size, size, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(new SkiaSharp.SKColor(mood.R, mood.G, mood.B, mood.A));
        var dest = new SkiaSharp.SKRect(-spill, -spill, size + spill, size + spill);
        using var paint = new SkiaSharp.SKPaint
        {
            Color = new SkiaSharp.SKColor(255, 255, 255, 128),
            ImageFilter = SkiaSharp.SKImageFilter.CreateBlur(12, 12),
            FilterQuality = SkiaSharp.SKFilterQuality.High,
            IsAntialias = true,
        };
        SkiaSharp.SKBitmap? cover = null;
        if (hasImage)
        {
            try
            {
                using var ms = new MemoryStream();
                img!.Save(ms);
                ms.Position = 0;
                cover = SkiaSharp.SKBitmap.Decode(ms);
            }
            catch { cover = null; }
        }
        if (cover != null)
        {
            float scale = Math.Max(dest.Width / cover.Width, dest.Height / cover.Height);
            float w = cover.Width * scale, h = cover.Height * scale;
            canvas.DrawBitmap(cover, new SkiaSharp.SKRect(dest.MidX - w / 2, dest.MidY - h / 2, dest.MidX + w / 2, dest.MidY + h / 2), paint);
            cover.Dispose();
        }
        else
        {
            paint.Shader = SkiaSharp.SKShader.CreateLinearGradient(new SkiaSharp.SKPoint(dest.Left, dest.Top), new SkiaSharp.SKPoint(dest.Right, dest.Bottom),
                new[] { new SkiaSharp.SKColor(0x2A, 0x2F, 0x3A), new SkiaSharp.SKColor(0x1B, 0x1E, 0x25) }, SkiaSharp.SKShaderTileMode.Clamp);
            canvas.DrawRect(dest, paint);
        }
        using var image = surface.Snapshot();
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        using var stream = data.AsStream();
        return new Bitmap(stream);
    }

    // The cover's colour: its pixels averaged, the vivid ones counting more.
    private static Color Mood(Bitmap img)
    {
        try
        {
            using var small = img.CreateScaledBitmap(new PixelSize(24, 24), BitmapInterpolationMode.MediumQuality);
            int w = 24, h = 24;
            var px = new byte[w * h * 4];
            unsafe
            {
                fixed (byte* p = px) small.CopyPixels(new PixelRect(0, 0, w, h), (IntPtr)p, px.Length, w * 4);
            }
            // Android's bitmaps are RGBA (the computers' BGRA).
            bool bgra = small.Format == Avalonia.Platform.PixelFormat.Bgra8888;
            double r = 0, g = 0, b = 0, sum = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                byte rr = bgra ? px[i + 2] : px[i], gg = px[i + 1], bb = bgra ? px[i] : px[i + 2];
                var (_, s, l) = ToHsl(rr / 255.0, gg / 255.0, bb / 255.0);
                double weight = 0.03 + s * s * Math.Max(0, 1 - Math.Abs(l - 0.5) * 1.7);
                r += rr * weight;
                g += gg * weight;
                b += bb * weight;
                sum += weight;
            }
            return Darken(Color.FromRgb((byte)(r / sum), (byte)(g / sum), (byte)(b / sum)));
        }
        catch { return Color.FromRgb(0x2A, 0x24, 0x3A); }
    }

    private static Color Darken(Color c)
    {
        var (hue, s, _) = ToHsl(c.R / 255.0, c.G / 255.0, c.B / 255.0);
        s = s < 0.08 ? s : Math.Clamp(s * 1.25, 0.3, 0.78);
        return FromHsl(hue, s, 0.3);
    }

    private static (double H, double S, double L) ToHsl(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        if (d < 1e-6) return (0, 0, l);
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        double Channel(double t)
        {
            t = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
        }
        return Color.FromRgb((byte)Math.Round(Channel(h + 1 / 3.0) * 255), (byte)Math.Round(Channel(h) * 255), (byte)Math.Round(Channel(h - 1 / 3.0) * 255));
    }
}
