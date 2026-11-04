using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;

namespace UltimateMP3Player.Views;

// A soft shadow behind a block of text, so it reads over the bars of the music moving behind it: the block's letters blurred
// once into a picture, made again only when the text changes. (An effect on the text would be worked out again at every frame
// of the bars.) Goes right before its Target, in the same place; drawn with Skia, like CoverGlow. Linked into the phones.
// A text that's scrolling (Marquee) stays out of it: the picture doesn't move, and its letters would smear beside the ones going by.
public sealed class TextShadow : Control
{
    public static readonly StyledProperty<Control?> TargetProperty = AvaloniaProperty.Register<TextShadow, Control?>(nameof(Target));

    public Control? Target { get => GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

    // For icons: one-pixel lines leave next to nothing once blurred, so more layers and wider.
    public bool Heavy { get; set; }

    // Pictures made since the start: for the tests.
    public static int Baked;

    private const int Spill = 24;
    private Bitmap? _picture;
    private string _made = "";
    private bool _pending;

    public TextShadow()
    {
        IsHitTestVisible = false;
        // (its own bounds take the shadow in: what's outside them may not be drawn)
        Margin = new Thickness(-Spill);
        // Whatever changes the text changes the layout: the moment to look, and at the bars' frames there's none.
        LayoutUpdated += (_, _) => Look();
    }

    private void Look()
    {
        if (_pending || !IsEffectivelyVisible || Target is not { } target || target.Bounds.Width <= 0 || target.Bounds.Height <= 0 || Key(target) == _made) return;
        _pending = true;
        Dispatcher.UIThread.Post(Bake, DispatcherPriority.Background);
    }

    private static string Key(Control target)
    {
        var key = new StringBuilder();
        Texts(target, key);
        return key.Append(target.Bounds).Append(Themes.IsLight).ToString();
    }

    private static void Texts(Visual parent, StringBuilder to)
    {
        foreach (var child in parent.GetVisualChildren())
        {
            if (!child.IsVisible) continue;
            if (child is TextBlock text)
            {
                if (!Marquee.IsMoving(text)) to.Append(text.Text ?? text.Inlines?.Text).Append('\n');
            }
            else Texts(child, to);
        }
    }

    private void Bake()
    {
        _pending = false;
        if (Target is not { } target || target.Bounds.Width <= 0 || target.Bounds.Height <= 0) return;
        _made = Key(target);
        // (a picture of it comes out moved by as much as it stands inside its parent, which is where this one stands too)
        int w = (int)Math.Ceiling(target.Bounds.Right), h = (int)Math.Ceiling(target.Bounds.Bottom);
        using var letters = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
        var moving = target.GetVisualDescendants().OfType<TextBlock>().Where(t => Marquee.IsMoving(t)).ToList();
        foreach (var text in moving) text.Opacity = 0;
        letters.Render(target);
        foreach (var text in moving) text.ClearValue(OpacityProperty);
        // Android's bitmaps are RGBA (the computers' BGRA).
        var type = letters.Format == Avalonia.Platform.PixelFormat.Bgra8888 ? SKColorType.Bgra8888 : SKColorType.Rgba8888;
        using var source = new SKBitmap(new SKImageInfo(w, h, type, SKAlphaType.Premul));
        letters.CopyPixels(new PixelRect(0, 0, w, h), source.GetPixels(), source.ByteCount, source.RowBytes);
        using var surface = SKSurface.Create(new SKImageInfo(w + 2 * Spill, h + 2 * Spill, SKColorType.Bgra8888, SKAlphaType.Premul));
        var ink = Themes.IsLight ? Themes.Get("Bg") : Colors.Black;
        using var tint = SKColorFilter.CreateBlendMode(new SKColor(ink.R, ink.G, ink.B), SKBlendMode.SrcIn);
        // Close and dense, then wide and soft: letters are thin, one blur alone fades them away.
        foreach (float sigma in Heavy ? new[] { 2f, 2f, 2f, 2f, 4.7f, 4.7f } : new[] { 1.3f, 1.3f, 4f })
        {
            using var paint = new SKPaint { ColorFilter = tint, ImageFilter = SKImageFilter.CreateBlur(sigma, sigma) };
            surface.Canvas.DrawBitmap(source, Spill, Spill, paint);
        }
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = data.AsStream();
        _picture?.Dispose();
        _picture = new Bitmap(stream);
        Baked++;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_picture != null) context.DrawImage(_picture, new Rect(_picture.Size));
    }
}
