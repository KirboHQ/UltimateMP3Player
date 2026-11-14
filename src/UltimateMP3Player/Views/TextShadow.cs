using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace UltimateMP3Player.Views;

// A soft shadow behind a block of text, so it reads over the bars of the music moving behind it: the block's letters blurred
// once into a picture, made again only when the text changes. (An effect on the text would be worked out again at every frame
// of the bars, and a cached one would blur the text itself.) Goes right before its Target, in the same place. A text that's
// scrolling (Marquee) stays out of it: the picture doesn't move, and its letters would smear beside the ones going by; so does a
// spinner (it turns, its shadow would stand still, or jump at each new text: "Loading… 40%").
public sealed class TextShadow : FrameworkElement
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(nameof(Target), typeof(FrameworkElement), typeof(TextShadow));

    public FrameworkElement? Target { get => (FrameworkElement?)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

    // For icons: one-pixel lines leave next to nothing once blurred, so more layers and wider.
    public bool Heavy { get; set; }

    // Pictures made since the start: for the tests.
    public static int Baked;

    private const int Spill = 24;
    private BitmapSource? _picture;
    private string _made = "";
    private bool _pending;

    public TextShadow()
    {
        IsHitTestVisible = false;
        // Whatever changes the text changes the layout: the moment to look, and at the bars' frames there's none.
        LayoutUpdated += (_, _) => Look();
    }

    private void Look()
    {
        if (_pending || !IsVisible || Target is not { ActualWidth: > 0, ActualHeight: > 0 } target || Key(target) == _made) return;
        _pending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, Bake);
    }

    private static string Key(FrameworkElement target)
    {
        var key = new StringBuilder();
        Texts(target, key);
        return key.Append(target.ActualWidth).Append('x').Append(target.ActualHeight).Append(Themes.IsLight).ToString();
    }

    private static void Texts(DependencyObject parent, StringBuilder to)
    {
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(parent); i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is UIElement { Visibility: not Visibility.Visible }) continue;
            if (child is TextBlock text)
            {
                if (!Marquee.IsMoving(text)) to.Append(text.Text).Append('\n');
            }
            else Texts(child, to);
        }
    }

    private static void Moving(DependencyObject parent, List<UIElement> to)
    {
        for (int i = 0, n = VisualTreeHelper.GetChildrenCount(parent); i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock text)
            {
                if (Marquee.IsMoving(text)) to.Add(text);
            }
            // (the Spinner style: an ellipse turning)
            else if (child is System.Windows.Shapes.Ellipse { RenderTransform: RotateTransform } spinner) to.Add(spinner);
            else Moving(child, to);
        }
    }

    private void Bake()
    {
        _pending = false;
        if (Target is not { ActualWidth: > 0, ActualHeight: > 0 } target) return;
        _made = Key(target);
        double w = target.ActualWidth, h = target.ActualHeight;
        var ink = new SolidColorBrush(Themes.IsLight ? Themes.Get("PageSolid") : Colors.Black);
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(w) + 2 * Spill, (int)Math.Ceiling(h) + 2 * Spill, 96, 96, PixelFormats.Pbgra32);
        var moving = new List<UIElement>();
        Moving(target, moving);
        foreach (var text in moving) text.Opacity = 0;
        // Close and dense, then wide and soft: letters are thin, one blur alone fades them away.
        foreach (double radius in Heavy ? new[] { 6.0, 6.0, 6.0, 6.0, 14.0, 14.0 } : new[] { 4.0, 4.0, 12.0 })
        {
            var layer = new DrawingVisual { Effect = new BlurEffect { Radius = radius, KernelType = KernelType.Gaussian } };
            using (var dc = layer.RenderOpen())
            {
                dc.PushOpacityMask(new VisualBrush(target) { Viewbox = new Rect(0, 0, w, h), ViewboxUnits = BrushMappingMode.Absolute });
                dc.DrawRectangle(ink, null, new Rect(Spill, Spill, w, h));
                dc.Pop();
            }
            bmp.Render(layer);
        }
        foreach (var text in moving) text.ClearValue(OpacityProperty);
        bmp.Freeze();
        _picture = bmp;
        Baked++;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_picture != null) dc.DrawImage(_picture, new Rect(-Spill, -Spill, _picture.PixelWidth, _picture.PixelHeight));
    }
}
