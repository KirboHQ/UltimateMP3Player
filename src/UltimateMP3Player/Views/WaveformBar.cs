using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UltimateMP3Player.Views;

// Seek bar; bars baked into bitmaps so dragging stays smooth.
public sealed class WaveformBar : FrameworkElement
{
    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(nameof(Peaks), typeof(byte[]), typeof(WaveformBar),
        new PropertyMetadata(null, (d, _) => ((WaveformBar)d).Rebuild()));

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(WaveformBar),
        new PropertyMetadata(0.0, (d, _) => ((WaveformBar)d).UpdateClips()));

    public static readonly DependencyProperty PlayedBrushProperty = DependencyProperty.Register(nameof(PlayedBrush), typeof(Brush), typeof(WaveformBar),
        new PropertyMetadata(Brushes.MediumPurple, (d, _) => ((WaveformBar)d).Rebuild()));

    public static readonly DependencyProperty RestBrushProperty = DependencyProperty.Register(nameof(RestBrush), typeof(Brush), typeof(WaveformBar),
        new PropertyMetadata(Brushes.DimGray, (d, _) => ((WaveformBar)d).Rebuild()));

    public static readonly DependencyProperty BarWidthProperty = DependencyProperty.Register(nameof(BarWidth), typeof(double), typeof(WaveformBar),
        new PropertyMetadata(2.5, (d, _) => ((WaveformBar)d).Rebuild()));

    public static readonly DependencyProperty SeekCommandProperty = DependencyProperty.Register(nameof(SeekCommand), typeof(ICommand), typeof(WaveformBar));

    public byte[]? Peaks { get => (byte[]?)GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public Brush PlayedBrush { get => (Brush)GetValue(PlayedBrushProperty); set => SetValue(PlayedBrushProperty, value); }
    public Brush RestBrush { get => (Brush)GetValue(RestBrushProperty); set => SetValue(RestBrushProperty, value); }
    public double BarWidth { get => (double)GetValue(BarWidthProperty); set => SetValue(BarWidthProperty, value); }
    public ICommand? SeekCommand { get => (ICommand?)GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }

    private static readonly Brush HoverBrush = Freeze(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)));

    private readonly DrawingVisual _rest = new();
    private readonly DrawingVisual _hover = new();
    private readonly DrawingVisual _played = new();
    private readonly RectangleGeometry _hoverClip = new();
    private readonly RectangleGeometry _playedClip = new();
    private readonly VisualCollection _children;
    private double? _hoverAt;
    private bool _dragging;

    public WaveformBar()
    {
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
        _hover.Clip = _hoverClip;
        _played.Clip = _playedClip;
        _children = new VisualCollection(this) { _rest, _hover, _played };
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    protected override int VisualChildrenCount => _children.Count;
    protected override Visual GetVisualChild(int index) => _children[index];

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Rebuild();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Rebuild();
    }

    // Transparent background: the whole area takes the mouse.
    protected override void OnRender(DrawingContext dc) => dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

    private void Rebuild()
    {
        var size = RenderSize;
        if (size.Width <= 0 || size.Height <= 0) return;
        var g = Build(size);
        var dpi = VisualTreeHelper.GetDpi(this);
        int pw = Math.Max(1, (int)Math.Ceiling(size.Width * dpi.DpiScaleX)), ph = Math.Max(1, (int)Math.Ceiling(size.Height * dpi.DpiScaleY));
        var rect = new Rect(size);
        BitmapSource Bake(Brush brush)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawGeometry(brush, null, g);
            var bmp = new RenderTargetBitmap(pw, ph, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Freeze();
            return bmp;
        }
        using (var dc = _rest.RenderOpen()) dc.DrawImage(Bake(RestBrush), rect);
        using (var dc = _hover.RenderOpen()) dc.DrawImage(Bake(HoverBrush), rect);
        using (var dc = _played.RenderOpen()) dc.DrawImage(Bake(PlayedBrush), rect);
        UpdateClips();
    }

    private Geometry Build(Size size)
    {
        var g = new StreamGeometry();
        double bw = BarWidth, gap = Math.Max(1, bw * 0.6), step = bw + gap;
        int bars = Math.Max(1, (int)((size.Width + gap) / step));
        double mid = size.Height / 2;
        var peaks = Peaks;
        using (var ctx = g.Open())
        {
            for (int i = 0; i < bars; i++)
            {
                double v;
                if (peaks is { Length: > 0 })
                {
                    int a = i * peaks.Length / bars, b = Math.Max(a + 1, (i + 1) * peaks.Length / bars);
                    int max = 0;
                    for (int k = a; k < b && k < peaks.Length; k++) max = Math.Max(max, peaks[k]);
                    v = max / 255.0;
                }
                else v = 0.08;
                double h = Math.Max(2, v * size.Height);
                double x = i * step, y = mid - h / 2;
                ctx.BeginFigure(new Point(x, y), true, true);
                ctx.LineTo(new Point(x + bw, y), false, false);
                ctx.LineTo(new Point(x + bw, y + h), false, false);
                ctx.LineTo(new Point(x, y + h), false, false);
            }
        }
        g.Freeze();
        return g;
    }

    private void UpdateClips()
    {
        double w = RenderSize.Width, h = RenderSize.Height;
        double played = Math.Clamp(_dragging && _hoverAt is double d ? d : Progress, 0, 1) * w;
        _playedClip.Rect = new Rect(0, 0, played, h);
        if (_hoverAt is double hv && !_dragging && hv * w > played) _hoverClip.Rect = new Rect(played, 0, hv * w - played, h);
        else _hoverClip.Rect = Rect.Empty;
    }

    private double Fraction(MouseEventArgs e) => Math.Clamp(e.GetPosition(this).X / Math.Max(1, ActualWidth), 0, 1);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _hoverAt = Fraction(e);
        UpdateClips();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_dragging) return;
        _hoverAt = null;
        UpdateClips();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragging = true;
        _hoverAt = Fraction(e);
        CaptureMouse();
        UpdateClips();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        var f = Fraction(e);
        if (SeekCommand?.CanExecute(f) == true) SeekCommand.Execute(f);
        _hoverAt = IsMouseOver ? f : null;
        UpdateClips();
        e.Handled = true;
    }
}
