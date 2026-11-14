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
// Made at the screen's own pixels: a phone has three for each of ours, and a smaller picture stretched comes out in blocks.
// A text that's scrolling (Marquee) stays out of it: the picture doesn't move, and its letters would smear beside the ones going by;
// so does a spinner (it turns, its shadow would stand still, or jump at each new text: "Loading… 40%").
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
    private double _pictureScale = 1;
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

    // (it's made on the UI thread: a phone's 3x would stop the bars. Letters stay sharp at 2x; the buttons' wide blur, redone at each play or pause, needs no more than 1x)
    private double Scale => Math.Min(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1, Heavy ? 1 : 2);

    private string Key(Control target)
    {
        var key = new StringBuilder();
        Texts(target, key);
        return key.Append(target.Bounds).Append(Themes.IsLight).Append(Scale).ToString();
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
        // (coloured fringes around the letters, beside the dark of the shadow, are all one sees)
        RenderOptions.SetTextRenderingMode(target, TextRenderingMode.Antialias);
        _picture?.Dispose();
        _pictureScale = Scale;
        _picture = Picture(target, _pictureScale, Heavy);
        Baked++;
        InvalidateVisual();
    }

    public static Bitmap Picture(Control target, double scale, bool heavy)
    {
        // (a picture of it comes out moved by as much as it stands inside its parent, which is where this one stands too)
        int w = (int)Math.Ceiling(target.Bounds.Right * scale), h = (int)Math.Ceiling(target.Bounds.Bottom * scale);
        int spill = (int)Math.Ceiling(Spill * scale);
        var dpi = new Vector(96 * scale, 96 * scale);
        using var letters = new RenderTargetBitmap(new PixelSize(w, h), dpi);
        var moving = target.GetVisualDescendants().OfType<Control>().Where(c => c is TextBlock t ? Marquee.IsMoving(t) : c.Classes.Contains("spinner")).ToList();
        foreach (var text in moving) text.Opacity = 0;
        letters.Render(target);
        foreach (var text in moving) text.ClearValue(OpacityProperty);
        // Android's bitmaps are RGBA (the computers' BGRA).
        var type = letters.Format == Avalonia.Platform.PixelFormat.Bgra8888 ? SKColorType.Bgra8888 : SKColorType.Rgba8888;
        using var source = new SKBitmap(new SKImageInfo(w, h, type, SKAlphaType.Premul));
        letters.CopyPixels(new PixelRect(0, 0, w, h), source.GetPixels(), source.ByteCount, source.RowBytes);
        using var surface = SKSurface.Create(new SKImageInfo(w + 2 * spill, h + 2 * spill, SKColorType.Bgra8888, SKAlphaType.Premul));
        var ink = Themes.IsLight ? Themes.Get("PageSolid") : Colors.Black;
        using var tint = SKColorFilter.CreateBlendMode(new SKColor(ink.R, ink.G, ink.B), SKBlendMode.SrcIn);
        // Close and dense, then wide and soft: letters are thin, one blur alone fades them away.
        foreach (float sigma in heavy ? new[] { 2f, 2f, 2f, 2f, 4.7f, 4.7f } : new[] { 1.3f, 1.3f, 4f })
        {
            float blur = sigma * (float)scale;
            using var paint = new SKPaint { ColorFilter = tint, ImageFilter = SKImageFilter.CreateBlur(blur, blur) };
            surface.Canvas.DrawBitmap(source, (float)(Spill * scale), (float)(Spill * scale), paint);
        }
        using var pixels = surface.PeekPixels();
        return new Bitmap(Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul, pixels.GetPixels(), new PixelSize(pixels.Width, pixels.Height), new Vector(96, 96), pixels.RowBytes);
    }

    public override void Render(DrawingContext context)
    {
        if (_picture != null) context.DrawImage(_picture, new Rect(_picture.Size), new Rect(_picture.Size / _pictureScale));
    }
}
