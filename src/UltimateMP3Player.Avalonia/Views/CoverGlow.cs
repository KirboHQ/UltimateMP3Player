using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;

namespace UltimateMP3Player.Views;

// The glow of a cover's colours behind the lyrics (Linux/macOS) and the phone's song page, like Spotify: its strongest
// colour with a very blurred copy of it over it. Drawn with Skia: Avalonia's effects (the blur) only exist on screen, not in
// a picture made off it. Linked into the Android and iPhone apps.
public static class CoverGlow
{
    // veil: the page's background over it at 10 % (the phone: what the element's opacity did, without a layer at every frame).
    // In the light look the colour is a pale one and the page's background washes over the picture: dark text reads on it.
    public static Bitmap Bake(Bitmap? img, bool veil = false)
    {
        const int size = 120, spill = 40;
        bool hasImage = img is { PixelSize.Width: > 0, PixelSize.Height: > 0 }, light = Themes.IsLight;
        var mood = hasImage ? Mood(img!, light) : Tone(Themes.Get("Accent"), light);
        var page = Themes.Get("Bg");
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(mood.R, mood.G, mood.B, mood.A));
        // Larger than the picture, so the blur doesn't pull the edges in; half opaque over the colour, very blurred
        // (WPF's blur of radius 34).
        var dest = new SKRect(-spill, -spill, size + spill, size + spill);
        using var paint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 128),
            ImageFilter = SKImageFilter.CreateBlur(12, 12),
#pragma warning disable CS0618
            FilterQuality = SKFilterQuality.High,
#pragma warning restore CS0618
            IsAntialias = true,
        };
        using var cover = hasImage ? Small(img!, 100) : null;
        if (cover != null)
        {
            // Uniform to fill.
            float scale = Math.Max(dest.Width / cover.Width, dest.Height / cover.Height);
            float w = cover.Width * scale, h = cover.Height * scale;
            canvas.DrawBitmap(cover, new SKRect(dest.MidX - w / 2, dest.MidY - h / 2, dest.MidX + w / 2, dest.MidY + h / 2), paint);
        }
        else
        {
            paint.Shader = SKShader.CreateLinearGradient(new SKPoint(dest.Left, dest.Top), new SKPoint(dest.Right, dest.Bottom),
                new[] { Sk(Themes.Get("Placeholder1")), Sk(Themes.Get("Placeholder2")) }, SKShaderTileMode.Clamp);
            canvas.DrawRect(dest, paint);
        }
        if (veil || light)
            using (var bg = new SKPaint { Color = Sk(page).WithAlpha((byte)((light ? 115 : 0) + (veil ? 26 : 0))) })
                canvas.DrawRect(new SKRect(0, 0, size, size), bg);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = data.AsStream();
        return new Bitmap(stream);
    }

    // The picture this big at most, its pixels copied straight to Skia (not the whole picture encoded and decoded again).
    private static SKBitmap? Small(Bitmap img, int max)
    {
        try
        {
            var ps = img.PixelSize;
            double k = Math.Min(1, max / (double)Math.Max(ps.Width, ps.Height));
            int w = Math.Max(1, (int)Math.Round(ps.Width * k)), h = Math.Max(1, (int)Math.Round(ps.Height * k));
            using var small = img.CreateScaledBitmap(new PixelSize(w, h), BitmapInterpolationMode.MediumQuality);
            // Android's bitmaps are RGBA (the computers' BGRA).
            var type = small.Format == Avalonia.Platform.PixelFormat.Bgra8888 ? SKColorType.Bgra8888 : SKColorType.Rgba8888;
            var alpha = small.AlphaFormat == Avalonia.Platform.AlphaFormat.Unpremul ? SKAlphaType.Unpremul : SKAlphaType.Premul;
            var bmp = new SKBitmap(new SKImageInfo(w, h, type, alpha));
            small.CopyPixels(new PixelRect(0, 0, w, h), bmp.GetPixels(), bmp.ByteCount, bmp.RowBytes);
            return bmp;
        }
        catch { return null; }
    }

    // The cover's colour: its pixels averaged, the vivid ones counting more (a grey sky doesn't win over a red dress).
    private static SKColor Sk(Color c) => new(c.R, c.G, c.B, c.A);

    private static Color Mood(Bitmap img, bool light)
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
            return Tone(Color.FromRgb((byte)(r / sum), (byte)(g / sum), (byte)(b / sum)), light);
        }
        catch { return Tone(Color.FromRgb(0x2A, 0x24, 0x3A), light); }
    }

    // Vivid enough to be a colour, dark enough for white text (light enough for dark text in the light look).
    private static Color Tone(Color c, bool light)
    {
        var (hue, s, _) = ToHsl(c.R / 255.0, c.G / 255.0, c.B / 255.0);
        s = s < 0.08 ? s : Math.Clamp(s * 1.25, 0.3, light ? 0.6 : 0.78);
        return FromHsl(hue, s, light ? 0.86 : 0.3);
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
