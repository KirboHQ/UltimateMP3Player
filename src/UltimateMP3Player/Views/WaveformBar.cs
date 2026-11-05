using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace UltimateMP3Player.Views;

// Seek bar, in the shape chosen (Core SeekShapes); baked into bitmaps so dragging stays smooth.
public sealed class WaveformBar : FrameworkElement
{
    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(nameof(Peaks), typeof(byte[]), typeof(WaveformBar),
        new PropertyMetadata(null, (d, _) => ((WaveformBar)d).Rebuild()));

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(WaveformBar),
        new PropertyMetadata(0.0, (d, _) => ((WaveformBar)d).OnProgress()));

    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(WaveformBar),
        new PropertyMetadata(false, (d, _) => ((WaveformBar)d).UpdateClock()));

    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(nameof(Duration), typeof(double), typeof(WaveformBar),
        new PropertyMetadata(0.0, (d, _) => ((WaveformBar)d).UpdateClock()));

    public static readonly DependencyProperty RateProperty = DependencyProperty.Register(nameof(Rate), typeof(double), typeof(WaveformBar),
        new PropertyMetadata(1.0));

    public static readonly DependencyProperty PlayedBrushProperty = DependencyProperty.Register(nameof(PlayedBrush), typeof(Brush), typeof(WaveformBar),
        // (a callback also hears the colours of the same brush moving, each frame of a fade: those reach the bars by themselves through
        // the mask in Rebuild, baking again for them took 20 ms a time)
        new PropertyMetadata(Brushes.MediumPurple, (d, e) => { if (e.OldValue != e.NewValue) ((WaveformBar)d).Rebuild(); }));

    public static readonly DependencyProperty RestBrushProperty = DependencyProperty.Register(nameof(RestBrush), typeof(Brush), typeof(WaveformBar),
        new PropertyMetadata(Brushes.DimGray, (d, _) => ((WaveformBar)d).Rebuild()));

    public static readonly DependencyProperty BarWidthProperty = DependencyProperty.Register(nameof(BarWidth), typeof(double), typeof(WaveformBar),
        new PropertyMetadata(2.5, (d, _) => ((WaveformBar)d).Rebuild()));

    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(nameof(Shape), typeof(Core.VisualizerStyle), typeof(WaveformBar),
        new PropertyMetadata(Core.VisualizerStyle.Bars, (d, _) => ((WaveformBar)d).Rebuild()));

    public static readonly DependencyProperty MirroredProperty = DependencyProperty.Register(nameof(Mirrored), typeof(bool), typeof(WaveformBar),
        new PropertyMetadata(true, (d, _) => ((WaveformBar)d).Rebuild()));

    public static readonly DependencyProperty SeekCommandProperty = DependencyProperty.Register(nameof(SeekCommand), typeof(ICommand), typeof(WaveformBar));

    public byte[]? Peaks { get => (byte[]?)GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    // Seconds; with IsPlaying, lets the bar move on between the player's position updates.
    public double Duration { get => (double)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    // Playback speed: seconds of song per second.
    public double Rate { get => (double)GetValue(RateProperty); set => SetValue(RateProperty, value); }
    public Brush PlayedBrush { get => (Brush)GetValue(PlayedBrushProperty); set => SetValue(PlayedBrushProperty, value); }
    public Brush RestBrush { get => (Brush)GetValue(RestBrushProperty); set => SetValue(RestBrushProperty, value); }
    public double BarWidth { get => (double)GetValue(BarWidthProperty); set => SetValue(BarWidthProperty, value); }
    public Core.VisualizerStyle Shape { get => (Core.VisualizerStyle)GetValue(ShapeProperty); set => SetValue(ShapeProperty, value); }
    public bool Mirrored { get => (bool)GetValue(MirroredProperty); set => SetValue(MirroredProperty, value); }
    public ICommand? SeekCommand { get => (ICommand?)GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }

    // The stretch under the mouse: the text's colour, see-through (white when dark, black when light).
    private static Brush HoverBrush => Freeze(new SolidColorBrush(Themes.Get("Text")) { Opacity = 70 / 255.0 });

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
        IsVisibleChanged += (_, _) => UpdateClock();
    }

    // ------------------------------------------------------------------ smooth progress

    // The player updates the position 4 times a second; in between the played part runs on by itself every frame
    // and glides onto each new value (a seek or a new song jumps straight there).
    private double _shown;
    private double _base;
    private long _baseAt, _frameAt;
    private bool _ticking;

    // As often as the played part grows by about half a pixel of the screen (a few times a second for a song of minutes). Asking for
    // every frame, on a bar that's always on screen, had the whole window drawn and the desktop composed at the screen's rate for
    // as long as the music played, on any page: a laptop's battery for a movement nobody could see.
    private DispatcherTimer? _clock;

    private void Pace()
    {
        double pixelsPerSecond = ActualWidth * VisualTreeHelper.GetDpi(this).DpiScaleX * Ui.Zoom * (Rate > 0 ? Rate : 1) / Math.Max(1, Duration);
        var every = TimeSpan.FromMilliseconds(Math.Clamp(500 / Math.Max(0.01, pixelsPerSecond), 16, 250));
        if (_clock!.Interval != every) _clock.Interval = every;
    }

    private void OnProgress()
    {
        _base = Progress;
        _baseAt = Stopwatch.GetTimestamp();
        if (!_ticking || Math.Abs(_base - _shown) * Duration > 0.6) _shown = _base;
        UpdateClips();
    }

    private void UpdateClock()
    {
        bool want = IsPlaying && Duration > 0 && IsVisible;
        if (want == _ticking) return;
        _ticking = want;
        if (want)
        {
            _baseAt = _frameAt = Stopwatch.GetTimestamp();
            _clock ??= new DispatcherTimer(DispatcherPriority.Render, Dispatcher);
            _clock.Tick -= OnFrame;
            _clock.Tick += OnFrame;
            Pace();
            _clock.Start();
        }
        else
        {
            _clock?.Stop();
            _shown = Progress;
            UpdateClips();
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double f = Stopwatch.Frequency, dt = Math.Min(0.1, (now - _frameAt) / f), rate = Rate > 0 ? Rate : 1;
        _frameAt = now;
        double target = Math.Min(1, _base + (now - _baseAt) / f * rate / Duration);
        double next = _shown + dt * rate / Duration;
        _shown = Math.Clamp(next + (target - next) * (1 - Math.Exp(-dt / 0.15)), 0, 1);
        UpdateClips();
        Pace();
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
        using (var dc = _played.RenderOpen())
        {
            if (PlayedBrush.IsFrozen) dc.DrawImage(Bake(PlayedBrush), rect);
            else
            {
                // (a brush whose colours move, from song to song: the bars as a mask, so they move too)
                dc.PushOpacityMask(new ImageBrush(Bake(Brushes.White)));
                dc.DrawRectangle(PlayedBrush, null, rect);
                dc.Pop();
            }
        }
        UpdateClips();
    }

    private Geometry Build(Size size)
    {
        // (nonzero: the thin columns of the line and the wave overlap a little)
        var g = new StreamGeometry { FillRule = FillRule.Nonzero };
        bool dots = Shape == Core.VisualizerStyle.Dots;
        using (var ctx = g.Open())
        {
            foreach (var m in Core.SeekShapes.Build(Shape, Peaks, size.Width, size.Height, BarWidth, Mirrored))
            {
                if (dots)
                {
                    var r = new Size(m.W / 2, m.H / 2);
                    ctx.BeginFigure(new Point(m.X, m.Y + r.Height), true, true);
                    ctx.ArcTo(new Point(m.X + m.W, m.Y + r.Height), r, 0, false, SweepDirection.Clockwise, false, false);
                    ctx.ArcTo(new Point(m.X, m.Y + r.Height), r, 0, false, SweepDirection.Clockwise, false, false);
                    continue;
                }
                ctx.BeginFigure(new Point(m.X, m.Y), true, true);
                ctx.LineTo(new Point(m.X + m.W, m.Y), false, false);
                ctx.LineTo(new Point(m.X + m.W, m.Y + m.H), false, false);
                ctx.LineTo(new Point(m.X, m.Y + m.H), false, false);
            }
        }
        g.Freeze();
        return g;
    }

    private void UpdateClips()
    {
        double w = RenderSize.Width, h = RenderSize.Height;
        double played = Math.Clamp(_dragging && _hoverAt is double d ? d : _shown, 0, 1) * w;
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
