using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Views;

// The bars of the music behind the song's page (Spectrum, Settings › Appearance). Drawn on the render thread, with no layout,
// property or allocation per frame: the UI thread doesn't know they're moving. At every frame of the screen while the music plays
// and the page is on screen; the bars fade and fall to rest, then nothing runs at all. Linked into the phones.
// One draw per column, never one path with all of them: Skia draws a rounded rectangle on the GPU straight away, a path of
// hundreds of them on the CPU into a mask (what made a maximized window crawl).
public sealed class WaveVisualizer : Control
{
    public static readonly StyledProperty<bool> IsPlayingProperty = AvaloniaProperty.Register<WaveVisualizer, bool>(nameof(IsPlaying));
    public static readonly StyledProperty<bool> IsPreviewProperty = AvaloniaProperty.Register<WaveVisualizer, bool>(nameof(IsPreview));
    public static readonly StyledProperty<VisualizerFlow> FlowProperty = AvaloniaProperty.Register<WaveVisualizer, VisualizerFlow>(nameof(Flow), VisualizerFlow.MirrorSides);
    public static readonly StyledProperty<VisualizerPlace> PlaceProperty = AvaloniaProperty.Register<WaveVisualizer, VisualizerPlace>(nameof(Place));
    public static readonly StyledProperty<VisualizerStyle> BarStyleProperty = AvaloniaProperty.Register<WaveVisualizer, VisualizerStyle>(nameof(BarStyle));
    public static readonly StyledProperty<VisualizerTint> TintProperty = AvaloniaProperty.Register<WaveVisualizer, VisualizerTint>(nameof(Tint));
    public static readonly StyledProperty<Color> ColorProperty = AvaloniaProperty.Register<WaveVisualizer, Color>(nameof(Color), Colors.Transparent);
    public static readonly StyledProperty<int> ThicknessProperty = AvaloniaProperty.Register<WaveVisualizer, int>(nameof(Thickness), 1);
    public static readonly StyledProperty<double> StrengthProperty = AvaloniaProperty.Register<WaveVisualizer, double>(nameof(Strength), 1);
    public static readonly StyledProperty<double> DimProperty = AvaloniaProperty.Register<WaveVisualizer, double>(nameof(Dim), 1);
    public static readonly StyledProperty<double> HeightShareProperty = AvaloniaProperty.Register<WaveVisualizer, double>(nameof(HeightShare), 0.38);
    public static readonly StyledProperty<double> MiddleProperty = AvaloniaProperty.Register<WaveVisualizer, double>(nameof(Middle), double.NaN);

    public bool IsPlaying { get => GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    // Keeps going with nothing playing, on a made-up sound (the settings).
    public bool IsPreview { get => GetValue(IsPreviewProperty); set => SetValue(IsPreviewProperty, value); }
    public VisualizerFlow Flow { get => GetValue(FlowProperty); set => SetValue(FlowProperty, value); }
    public VisualizerPlace Place { get => GetValue(PlaceProperty); set => SetValue(PlaceProperty, value); }
    public VisualizerStyle BarStyle { get => GetValue(BarStyleProperty); set => SetValue(BarStyleProperty, value); }
    public VisualizerTint Tint { get => GetValue(TintProperty); set => SetValue(TintProperty, value); }
    public Color Color { get => GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    // 0 thin, 1 normal, 2 wide.
    public int Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public double Strength { get => GetValue(StrengthProperty); set => SetValue(StrengthProperty, value); }
    // (Strength as chosen, Dim where they run behind buttons: the phones)
    public double Dim { get => GetValue(DimProperty); set => SetValue(DimProperty, value); }
    // How much of its space they can take.
    public double HeightShare { get => GetValue(HeightShareProperty); set => SetValue(HeightShareProperty, value); }
    // Where the line of "in the center" runs (the middle of the cover); NaN: the middle of its space.
    public double Middle { get => GetValue(MiddleProperty); set => SetValue(MiddleProperty, value); }

    // Frames drawn since the start, on every visualizer: for the measures of the tests.
    public static long Drawn;

    private CompositionCustomVisual? _visual;
    private readonly Painter _painter = new();
    private bool _looking, _seen = true;

    public WaveVisualizer()
    {
        IsHitTestVisible = false;
        AttachedToVisualTree += (_, _) => Attach();
        DetachedFromVisualTree += (_, _) => Detach();
        // The preview in the settings, scrolled away: nothing to draw.
        EffectiveViewportChanged += (_, e) =>
        {
            bool seen = e.EffectiveViewport.Width > 0 && e.EffectiveViewport.Height > 0;
            if (seen == _seen) return;
            _seen = seen;
            if (IsPreview) UpdateRun();
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && _visual != null)
            _visual.Size = new Vector2((float)Bounds.Width, (float)Bounds.Height);
        if (change.Property == IsPlayingProperty || change.Property == IsPreviewProperty || change.Property == IsVisibleProperty) UpdateRun();
        if (change.Property == FlowProperty || change.Property == PlaceProperty || change.Property == BarStyleProperty || change.Property == TintProperty || change.Property == ColorProperty
            || change.Property == ThicknessProperty || change.Property == StrengthProperty || change.Property == DimProperty || change.Property == IsPreviewProperty
            || change.Property == HeightShareProperty || change.Property == MiddleProperty) Push();
    }

    private void Attach()
    {
        if (ElementComposition.GetElementVisual(this)?.Compositor is not { } compositor) return;
        _visual = compositor.CreateCustomVisual(_painter);
        _visual.ClipToBounds = true;
        ElementComposition.SetElementChildVisual(this, _visual);
        _visual.Size = new Vector2((float)Bounds.Width, (float)Bounds.Height);
        Themes.Changed += OnTheme;
        Ui.HiddenChanged += UpdateRun;
        Push();
        UpdateRun();
    }

    private void Detach()
    {
        Themes.Changed -= OnTheme;
        Ui.HiddenChanged -= UpdateRun;
        SetLooking(false);
        ElementComposition.SetElementChildVisual(this, null);
        _visual = null;
    }

    private void OnTheme() => Dispatcher.UIThread.Post(Push);

    // A hidden page (kept built) doesn't look; IsVisible of an ancestor is looked at again in a while, like WaveformBar's.
    private bool _waitShown;

    private void UpdateRun()
    {
        if (_visual == null) return;
        bool want = (IsPlaying || (IsPreview && _seen && !Ui.Hidden)) && IsVisible;
        if (want && !IsEffectivelyVisible)
        {
            want = false;
            if (!_waitShown)
            {
                _waitShown = true;
                Ui.Later(() =>
                {
                    _waitShown = false;
                    UpdateRun();
                });
            }
        }
        SetLooking(want);
        _visual.SendHandlerMessage(want);
    }

    private void SetLooking(bool on)
    {
        if (on == _looking) return;
        _looking = on;
        if (on) Spectrum.Shared.Acquire();
        else Spectrum.Shared.Release();
    }

    private void Push()
    {
        var color = Tint switch
        {
            VisualizerTint.Accent => Themes.Get("Accent"),
            VisualizerTint.Text => Themes.Get("Text"),
            _ => Color.A > 0 ? Color : Themes.Get("Accent"),
        };
        _visual?.SendHandlerMessage(new Painter.Look(Flow, Place, BarStyle, Tint == VisualizerTint.Rainbow, color, Thickness, Strength * Dim, IsPreview, Math.Clamp(HeightShare, 0, 1), Middle));
    }

    private sealed class Painter : CompositionCustomVisualHandler
    {
        public sealed record Look(VisualizerFlow Flow, VisualizerPlace Place, VisualizerStyle Style, bool Rainbow, Color Color, int Thickness, double Strength, bool Preview, double Share, double Middle);

        private Look _look = new(VisualizerFlow.MirrorSides, VisualizerPlace.Bottom, VisualizerStyle.Bars, false, Colors.White, 1, 1, false, 0.38, double.NaN);
        private bool _running, _ticking;
        private readonly SpectrumColumns _columns = new();
        private readonly SKPath _path = new();
        private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
        private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        private SKShader? _shader;
        private SKColor _line;
        private float _shaderW, _shaderH;
        private bool _any;

        public override void OnMessage(object message)
        {
            if (message is Look look)
            {
                _look = look;
                _shader?.Dispose();
                _shader = null;
                Invalidate();
            }
            else if (message is bool run)
            {
                _running = run;
                if (_ticking) return;
                _ticking = true;
                RegisterForNextAnimationFrameUpdate();
            }
        }

        public override void OnAnimationFrameUpdate()
        {
            int n = SpectrumColumns.CountFor(EffectiveSize.X, _look.Thickness, _look.Style);
            _any = _columns.Update(n, _look.Flow, _running, _look.Preview, Ui.Animations);
            Invalidate();
            if (_any) RegisterForNextAnimationFrameUpdate();
            else _ticking = false;
        }

        public override void OnRender(ImmediateDrawingContext context)
        {
            var heights = _columns.Heights;
            int n = heights.Length;
            if (n == 0 || !_any) return;
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature) return;
            using var lease = feature.Lease();
            float w = (float)EffectiveSize.X, h = (float)EffectiveSize.Y;
            if (w <= 0 || h <= 0) return;
            float middle = double.IsNaN(_look.Middle) ? h / 2 : Math.Clamp((float)_look.Middle, 0, h);
            float reach = (float)SpectrumColumns.ReachOf(_look.Place, h, _look.Share);
            MakeColors(w, h, reach, middle);
            // (the fade: a paint's own alpha multiplies what its shader gives)
            byte presence = (byte)(_columns.Presence * 255);
            _fill.Color = new SKColor(255, 255, 255, presence);
            _stroke.Color = _look.Rainbow ? _fill.Color : _line.WithAlpha((byte)(_line.Alpha * _columns.Presence));
            Interlocked.Increment(ref Drawn);
            var canvas = lease.SkCanvas;
            float slot = w / n;
            var place = _look.Place;
            int lanes = SpectrumColumns.LanesOf(place);
            switch (_look.Style)
            {
                case VisualizerStyle.Line:
                    Curve(heights, w, h, reach, middle, false);
                    canvas.DrawPath(_path, _stroke);
                    break;
                case VisualizerStyle.Wave:
                    Curve(heights, w, h, reach, middle, true);
                    canvas.DrawPath(_path, _fill);
                    break;
                case VisualizerStyle.Dots:
                {
                    float r = Math.Max(1.5f, (float)SpectrumColumns.BarOf(slot, _look.Thickness, VisualizerStyle.Dots) / 2);
                    for (int lane = 0; lane < lanes; lane++)
                    {
                        var (from, way) = SpectrumColumns.Lane(place, lane, h, middle);
                        for (int i = 0; i < n; i++)
                            canvas.DrawCircle((i + 0.5f) * slot, (float)from + way * (r + heights[i] * Math.Max(0, reach - 2 * r)), r, _fill);
                    }
                    break;
                }
                case VisualizerStyle.Blocks:
                {
                    float bar = Math.Max(2, (float)SpectrumColumns.BarOf(slot, _look.Thickness, VisualizerStyle.Blocks)), block = Math.Clamp(bar, 4, 9), step = block + 2;
                    for (int lane = 0; lane < lanes; lane++)
                    {
                        var (from, way) = SpectrumColumns.Lane(place, lane, h, middle);
                        for (int i = 0; i < n; i++)
                        {
                            float x = i * slot + (slot - bar) / 2;
                            int count = Math.Max(1, (int)(heights[i] * reach / step));
                            for (int k = 0; k < count; k++)
                            {
                                float top = way < 0 ? (float)from - (k + 1) * step + 2 : (float)from + k * step;
                                canvas.DrawRect(new SKRect(x, top, x + bar, top + block), _fill);
                            }
                        }
                    }
                    break;
                }
                default:
                {
                    float bar = Math.Max(1.5f, (float)SpectrumColumns.BarOf(slot, _look.Thickness, _look.Style)), r = bar / 2;
                    bool peaks = _look.Style == VisualizerStyle.Peaks;
                    var tops = _columns.Peaks;
                    // In the center one rectangle for both sides: two half-transparent ones overlapping would show a band.
                    if (place == VisualizerPlace.Center)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            float x = i * slot + (slot - bar) / 2, length = Math.Max(3, heights[i] * reach), peak = tops[i] * reach;
                            canvas.DrawRoundRect(new SKRect(x, middle - length, x + bar, middle + length), r, r, _fill);
                            if (!peaks || peak <= length + 4) continue;
                            canvas.DrawRect(new SKRect(x, middle - peak - 4, x + bar, middle - peak - 1), _fill);
                            canvas.DrawRect(new SKRect(x, middle + peak + 1, x + bar, middle + peak + 4), _fill);
                        }
                        break;
                    }
                    for (int lane = 0; lane < lanes; lane++)
                    {
                        var (from0, way) = SpectrumColumns.Lane(place, lane, h, middle);
                        float from = (float)from0;
                        for (int i = 0; i < n; i++)
                        {
                            float x = i * slot + (slot - bar) / 2, length = Math.Max(3, heights[i] * reach), peak = tops[i] * reach;
                            // (the rounded base goes past the edge, the visual cuts it: only the tips are round)
                            canvas.DrawRoundRect(way < 0 ? new SKRect(x, from - length, x + bar, from + r) : new SKRect(x, from - r, x + bar, from + length), r, r, _fill);
                            if (!peaks || peak <= length + 4) continue;
                            canvas.DrawRect(way < 0 ? new SKRect(x, from - peak - 4, x + bar, from - peak - 1) : new SKRect(x, from + peak + 1, x + bar, from + peak + 4), _fill);
                        }
                    }
                    break;
                }
            }
        }

        // One line through the tips of each lane, smoothed; filled: the area between it and where they start. In the center the two
        // sides are one figure: two figures touching would leave a seam along the middle.
        private void Curve(float[] heights, float w, float h, float reach, float middle, bool filled)
        {
            int n = heights.Length;
            float step = w / (n - 1), inset = filled ? 0 : (float)SpectrumColumns.LineOf(_look.Thickness) / 2;
            reach -= inset;
            _path.Rewind();
            void Forward(float from, int way)
            {
                for (int i = 0; i < n - 1; i++)
                {
                    float x0 = i * step, y0 = from + way * heights[i] * reach, y1 = from + way * heights[i + 1] * reach;
                    _path.QuadTo(x0, y0, x0 + step / 2, (y0 + y1) / 2);
                }
                _path.LineTo(w, from + way * heights[n - 1] * reach);
            }
            if (filled && _look.Place == VisualizerPlace.Center)
            {
                _path.MoveTo(0, middle - heights[0] * reach);
                Forward(middle, -1);
                _path.LineTo(w, middle + heights[n - 1] * reach);
                for (int i = n - 1; i > 0; i--)
                {
                    float x0 = i * step, y0 = middle + heights[i] * reach, y1 = middle + heights[i - 1] * reach;
                    _path.QuadTo(x0, y0, x0 - step / 2, (y0 + y1) / 2);
                }
                _path.LineTo(0, middle + heights[0] * reach);
                _path.Close();
                return;
            }
            for (int lane = 0, lanes = SpectrumColumns.LanesOf(_look.Place); lane < lanes; lane++)
            {
                var (from0, way) = SpectrumColumns.Lane(_look.Place, lane, h, middle);
                float from = (float)from0;
                // (a line at rest lies whole inside the space)
                if (!filled && from <= 0) from = inset;
                else if (!filled && from >= h) from = h - inset;
                if (filled)
                {
                    _path.MoveTo(0, from);
                    _path.LineTo(0, from + way * heights[0] * reach);
                }
                else _path.MoveTo(0, from + way * heights[0] * reach);
                Forward(from, way);
                if (!filled) continue;
                _path.LineTo(w, from);
                _path.Close();
            }
        }

        private void MakeColors(float w, float h, float reach, float middle)
        {
            if (_shader != null && _shaderW == w && _shaderH == h) return;
            _shader?.Dispose();
            _shaderW = w;
            _shaderH = h;
            byte A(double a) => (byte)Math.Clamp(a * _look.Strength * 255, 0, 255);
            _stroke.StrokeWidth = (float)SpectrumColumns.LineOf(_look.Thickness);
            if (_look.Rainbow)
            {
                uint[] hues = { 0xFF4D6D, 0xFF9F3D, 0xFFE14D, 0x4DE08A, 0x4DB8FF, 0x9D6BFF, 0xFF4DC4 };
                var colors = hues.Select(c => new SKColor((byte)(c >> 16), (byte)(c >> 8), (byte)c, A(0.8))).ToArray();
                _shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(w, 0), colors, SKShaderTileMode.Clamp);
                _stroke.Shader = _shader;
            }
            else
            {
                var c = _look.Color;
                var fade = SpectrumColumns.FadeOf(_look.Place, h, reach, middle);
                // (in pixels of the visual: the colours stay where they are while the tallest bar moves)
                _shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, h),
                    fade.Select(f => new SKColor(c.R, c.G, c.B, A(f.Alpha))).ToArray(), fade.Select(f => (float)f.At).ToArray(), SKShaderTileMode.Clamp);
                _stroke.Shader = null;
                _line = new SKColor(c.R, c.G, c.B, A(0.9));
            }
            _fill.Shader = _shader;
        }
    }
}
