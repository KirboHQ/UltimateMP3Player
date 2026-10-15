using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace UltimateMP3Player.Views;

// The song page's cover as one rounded image (the computer apps' NowPlayingView; the glow behind the page: CoverGlow).
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
}
