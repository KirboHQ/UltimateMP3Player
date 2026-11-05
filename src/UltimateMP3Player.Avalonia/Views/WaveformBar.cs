using System.Diagnostics;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace UltimateMP3Player.Views;

// Seek bar, in the shape chosen (Core SeekShapes; the Windows app's WaveformBar). One Skia draw per bar, cut where the played part ends: one geometry with all of them
// would be rasterized on the CPU into a mask, and drawn again at every frame of whatever moves over it (the phones' bars of the music).
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
    public static readonly StyledProperty<Core.VisualizerStyle> ShapeProperty = AvaloniaProperty.Register<WaveformBar, Core.VisualizerStyle>(nameof(Shape));
    public static readonly StyledProperty<bool> MirroredProperty = AvaloniaProperty.Register<WaveformBar, bool>(nameof(Mirrored), true);
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
    public Core.VisualizerStyle Shape { get => GetValue(ShapeProperty); set => SetValue(ShapeProperty, value); }
    public bool Mirrored { get => GetValue(MirroredProperty); set => SetValue(MirroredProperty, value); }
    public ICommand? SeekCommand { get => GetValue(SeekCommandProperty); set => SetValue(SeekCommandProperty, value); }

    private SKRect[]? _bars;
    private Size _barsSize;
    private Inks? _inks;
    private double? _hoverAt;
    private bool _dragging;

    static WaveformBar()
    {
        AffectsRender<WaveformBar>(PlayedBrushProperty, RestBrushProperty);
        PeaksProperty.Changed.AddClassHandler<WaveformBar>((w, _) => w.Rebuild());
        BarWidthProperty.Changed.AddClassHandler<WaveformBar>((w, _) => w.Rebuild());
        ShapeProperty.Changed.AddClassHandler<WaveformBar>((w, _) => w.Rebuild());
        MirroredProperty.Changed.AddClassHandler<WaveformBar>((w, _) => w.Rebuild());
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

    private SKRect[] Build(Size size)
    {
        var marks = Core.SeekShapes.Build(Shape, Peaks, size.Width, size.Height, BarWidth, Mirrored);
        var bars = new SKRect[marks.Length];
        for (int i = 0; i < marks.Length; i++) bars[i] = new SKRect(marks[i].X, marks[i].Y, marks[i].X + marks[i].W, marks[i].Y + marks[i].H);
        return bars;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Themes.Painted += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Themes.Painted -= InvalidateVisual;
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
        double w = size.Width;
        double played = Math.Clamp(_dragging && _hoverAt is double d ? d : _shown, 0, 1) * w;
        double hovered = _hoverAt is double hv && !_dragging ? hv * w : played;
        var inks = Inks.For(ref _inks, RestBrush, PlayedBrush, Themes.Get("Text"), (float)w);
        dc.Custom(new BarsDraw(new Rect(size), _bars, inks, (float)played, (float)Math.Max(played, hovered), Shape == Core.VisualizerStyle.Dots));
    }

    // The colours, kept from one drawing to the next (a played part that grows a few times a second, the same paints).
    private sealed class Inks
    {
        public readonly SKPaint Rest = new() { IsAntialias = true }, Played = new() { IsAntialias = true }, Hover = new() { IsAntialias = true };
        private Color _rest, _text;
        private Color[] _played = Array.Empty<Color>();
        private float _width;

        private static Color[] ColorsOf(IBrush? brush) => brush switch
        {
            ISolidColorBrush s => new[] { Scaled(s.Color, s.Opacity) },
            IGradientBrush g when g.GradientStops.Count > 0 => g.GradientStops.Select(x => Scaled(x.Color, g.Opacity)).ToArray(),
            _ => new[] { Colors.Gray },
        };

        private static Color Scaled(Color c, double opacity) => Color.FromArgb((byte)(c.A * opacity), c.R, c.G, c.B);

        public static Inks For(ref Inks? kept, IBrush? rest, IBrush? played, Color text, float width)
        {
            var restColor = ColorsOf(rest)[0];
            var playedColors = ColorsOf(played);
            if (kept != null && kept._rest == restColor && kept._text == text && kept._width == width && kept._played.SequenceEqual(playedColors)) return kept;
            var inks = new Inks { _rest = restColor, _text = text, _played = playedColors, _width = width };
            inks.Rest.Color = Sk(restColor);
            inks.Hover.Color = new SKColor(text.R, text.G, text.B, 70);
            if (playedColors.Length == 1) inks.Played.Color = Sk(playedColors[0]);
            else inks.Played.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, 0), playedColors.Select(Sk).ToArray(), null, SKShaderTileMode.Clamp);
            return kept = inks;
        }

        private static SKColor Sk(Color c) => new(c.R, c.G, c.B, c.A);
    }

    private sealed class BarsDraw : ICustomDrawOperation
    {
        private readonly SKRect[] _bars;
        private readonly Inks _inks;
        private readonly float _played, _hovered;
        private readonly bool _round;

        public BarsDraw(Rect bounds, SKRect[] bars, Inks inks, float played, float hovered, bool round)
        {
            _round = round;
            Bounds = bounds;
            _bars = bars;
            _inks = inks;
            _played = played;
            _hovered = hovered;
        }

        public Rect Bounds { get; }
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            foreach (var bar in _bars)
            {
                // (a dot takes one colour, by its middle)
                if (_round)
                {
                    canvas.DrawOval(bar, bar.MidX < _played ? _inks.Played : bar.MidX < _hovered ? _inks.Hover : _inks.Rest);
                    continue;
                }
                Part(canvas, bar, float.MinValue, _played, _inks.Played);
                Part(canvas, bar, _played, _hovered, _inks.Hover);
                Part(canvas, bar, _hovered, float.MaxValue, _inks.Rest);
            }
        }

        // What of a bar lies between two places along the bar.
        private static void Part(SKCanvas canvas, SKRect bar, float from, float to, SKPaint paint)
        {
            float left = Math.Max(bar.Left, from), right = Math.Min(bar.Right, to);
            if (right > left) canvas.DrawRect(new SKRect(left, bar.Top, right, bar.Bottom), paint);
        }
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
