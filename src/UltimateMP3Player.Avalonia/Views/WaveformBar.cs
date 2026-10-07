using System.Diagnostics;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace UltimateMP3Player.Views;

// Seek bar (the Windows app's WaveformBar): the bars are one geometry drawn three times, clipped to the played part,
// the hovered part and the rest.
public sealed class WaveformBar : Control
{
    public static readonly StyledProperty<byte[]?> PeaksProperty = AvaloniaProperty.Register<WaveformBar, byte[]?>(nameof(Peaks));
    public static readonly StyledProperty<double> ProgressProperty = AvaloniaProperty.Register<WaveformBar, double>(nameof(Progress));
    public static readonly StyledProperty<bool> IsPlayingProperty = AvaloniaProperty.Register<WaveformBar, bool>(nameof(IsPlaying));
    public static readonly StyledProperty<double> DurationProperty = AvaloniaProperty.Register<WaveformBar, double>(nameof(Duration));
    public static readonly StyledProperty<double> RateProperty = AvaloniaProperty.Register<WaveformBar, double>(nameof(Rate), 1.0);
    public static readonly StyledProperty<IBrush?> PlayedBrushProperty = AvaloniaProperty.Register<WaveformBar, IBrush?>(nameof(PlayedBrush), Brushes.MediumPurple);
    public static readonly StyledProperty<IBrush?> RestBrushProperty = AvaloniaProperty.Register<WaveformBar, IBrush?>(nameof(RestBrush), Brushes.DimGray);
    public static readonly StyledProperty<double> BarWidthProperty = AvaloniaProperty.Register<WaveformBar, double>(nameof(BarWidth), 2.5);
    public static readonly StyledProperty<ICommand?> SeekCommandProperty = AvaloniaProperty.Register<WaveformBar, ICommand?>(nameof(SeekCommand));

    public byte[]? Peaks { get => GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public double Progress { get => GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public bool IsPlaying { get => GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    // Seconds; with IsPlaying, lets the bar move on between the player's position updates.
    public double Duration { get => GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    // Playback speed: seconds of song per second.
    public double Rate { get => GetValue(RateProperty); set => SetValue(RateProperty, value); }
    public IBrush? PlayedBrush { get => GetValue(PlayedBrushProperty); set => SetValue(PlayedBrushProperty, value); }
    public IBrush? RestBrush { get => GetValue(RestBrushProperty); set => SetValue(RestBrushProperty, value); }
    public double BarWidth { get => GetValue(BarWidthProperty); set => SetValue(BarWidthProperty, value); }
    public ICommand? SeekCommand { get => GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }

    private static readonly IBrush HoverBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)).ToImmutable();

    private Geometry? _bars;
    private Size _barsSize;
    private double? _hoverAt;
    private bool _dragging;

    static WaveformBar()
    {
        AffectsRender<WaveformBar>(PlayedBrushProperty, RestBrushProperty);
        PeaksProperty.Changed.AddClassHandler<WaveformBar>((w, _) => w.Rebuild());
        BarWidthProperty.Changed.AddClassHandler<WaveformBar>((w, _) => w.Rebuild());
        ProgressProperty.Changed.AddClassHandler<WaveformBar>((w, _) => w.OnProgress());
        IsPlayingProperty.Changed.AddClassHandler<WaveformBar>((w, _) => w.UpdateClock());
        DurationProperty.Changed.AddClassHandler<WaveformBar>((w, _) => w.UpdateClock());
        IsVisibleProperty.Changed.AddClassHandler<WaveformBar>((w, _) => w.UpdateClock());
    }

    public WaveformBar()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        AttachedToVisualTree += (_, _) => UpdateClock();
        DetachedFromVisualTree += (_, _) => UpdateClock();
    }

    // ------------------------------------------------------------------ smooth progress

    // The player updates the position 4 times a second; in between the played part runs on by itself every frame
    // and glides onto each new value (a seek or a new song jumps straight there).
    private double _shown;
    private double _base;
    private long _baseAt, _frameAt;
    private bool _ticking, _frameRequested;

    private void OnProgress()
    {
        _base = Progress;
        _baseAt = Stopwatch.GetTimestamp();
        if (!_ticking || Math.Abs(_base - _shown) * Duration > 0.6) _shown = _base;
        InvalidateVisual();
    }

    private void UpdateClock()
    {
        bool want = IsPlaying && Duration > 0 && VisualRoot != null;
        // On a hidden page: still, and looked at again in a while (Ui.Later) to move on once it's shown.
        if (want && !IsEffectivelyVisible)
        {
            want = false;
            if (!_waitShown)
            {
                _waitShown = true;
                Ui.Later(() =>
                {
                    _waitShown = false;
                    UpdateClock();
                });
            }
        }
        if (want == _ticking) return;
        _ticking = want;
        if (want)
        {
            _baseAt = _frameAt = Stopwatch.GetTimestamp();
            RequestFrame();
        }
        else
        {
            _shown = Progress;
            InvalidateVisual();
        }
    }

    // As often as the played part grows by about half a pixel of the screen (a few times a second for a song of minutes),
    // not at every frame of the screen.
    private readonly FramePacer _pacer = new(16);

    private void RequestFrame()
    {
        if (_frameRequested || TopLevel.GetTopLevel(this) is not { } top) return;
        _frameRequested = true;
        double pixelsPerSecond = Bounds.Width * top.RenderScaling * (Rate > 0 ? Rate : 1) / Math.Max(1, Duration);
        _pacer.MinMs = Math.Clamp(500 / Math.Max(0.01, pixelsPerSecond), 16, 250);
        _pacer.Request(top, _ =>
        {
            _frameRequested = false;
            if (!_ticking) return;
            if (!IsEffectivelyVisible)
            {
                UpdateClock();
                return;
            }
            OnFrame();
            RequestFrame();
        });
    }

    private bool _waitShown;

    private void OnFrame()
    {
        long now = Stopwatch.GetTimestamp();
        double f = Stopwatch.Frequency, dt = Math.Min(0.1, (now - _frameAt) / f), rate = Rate > 0 ? Rate : 1;
        _frameAt = now;
        double target = Math.Min(1, _base + (now - _baseAt) / f * rate / Duration);
        double next = _shown + dt * rate / Duration;
        _shown = Math.Clamp(next + (target - next) * (1 - Math.Exp(-dt / 0.15)), 0, 1);
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ drawing

    private void Rebuild()
    {
        _bars = null;
        InvalidateVisual();
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
                ctx.BeginFigure(new Point(x, y), true);
                ctx.LineTo(new Point(x + bw, y));
                ctx.LineTo(new Point(x + bw, y + h));
                ctx.LineTo(new Point(x, y + h));
                ctx.EndFigure(true);
            }
        }
        return g;
    }

    public override void Render(DrawingContext dc)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        // Transparent background: the whole area takes the mouse.
        dc.FillRectangle(Brushes.Transparent, new Rect(size));
        if (_bars == null || _barsSize != size)
        {
            _bars = Build(size);
            _barsSize = size;
        }
        double w = size.Width, h = size.Height;
        double played = Math.Clamp(_dragging && _hoverAt is double d ? d : _shown, 0, 1) * w;
        dc.DrawGeometry(RestBrush, null, _bars);
        if (_hoverAt is double hv && !_dragging && hv * w > played)
            using (dc.PushClip(new Rect(played, 0, hv * w - played, h)))
                dc.DrawGeometry(HoverBrush, null, _bars);
        if (played > 0)
            using (dc.PushClip(new Rect(0, 0, played, h)))
                dc.DrawGeometry(PlayedBrush, null, _bars);
    }

    // ------------------------------------------------------------------ mouse

    private double Fraction(PointerEventArgs e) => Math.Clamp(e.GetPosition(this).X / Math.Max(1, Bounds.Width), 0, 1);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hoverAt = Fraction(e);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_dragging) return;
        _hoverAt = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        _hoverAt = Fraction(e);
        e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        var f = Fraction(e);
        if (SeekCommand?.CanExecute(f) == true) SeekCommand.Execute(f);
        // A finger has no hover: nothing stays lit once it's lifted.
        _hoverAt = IsPointerOver && e.Pointer.Type == PointerType.Mouse ? f : null;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }
}
