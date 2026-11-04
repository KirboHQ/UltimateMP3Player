using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Views;

// The bars of the music behind the song's page (Spectrum, Settings › Appearance): at every frame of the screen
// (CompositionTarget.Rendering), one geometry redrawn on its own visual, no layout. Only while the music plays and the page is
// on screen; the bars fade and fall to rest, then nothing runs at all.
// Everything made of columns is drawn without antialiasing, on whole pixels: WPF rasterizes an antialiased geometry on the
// CPU, on the render thread, and hundreds of rounded bars took 40 ms a frame on a maximized window (the whole app at 25 fps,
// a core at 100%); aliased, under 2 ms, and on whole pixels nothing is lost. Measured by WpfShots "perf".
public sealed class WaveVisualizer : FrameworkElement
{
    private static readonly PropertyChangedCallback Restyle = (d, _) => ((WaveVisualizer)d).Forget();

    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(WaveVisualizer),
        new PropertyMetadata(false, (d, _) => ((WaveVisualizer)d).UpdateRun()));
    public static readonly DependencyProperty IsPreviewProperty = DependencyProperty.Register(nameof(IsPreview), typeof(bool), typeof(WaveVisualizer),
        new PropertyMetadata(false, (d, _) => ((WaveVisualizer)d).UpdateRun()));
    public static readonly DependencyProperty FlowProperty = DependencyProperty.Register(nameof(Flow), typeof(VisualizerFlow), typeof(WaveVisualizer),
        new PropertyMetadata(VisualizerFlow.MirrorSides));
    public static readonly DependencyProperty PlaceProperty = DependencyProperty.Register(nameof(Place), typeof(VisualizerPlace), typeof(WaveVisualizer),
        new PropertyMetadata(VisualizerPlace.Bottom, Restyle));
    public static readonly DependencyProperty BarStyleProperty = DependencyProperty.Register(nameof(BarStyle), typeof(VisualizerStyle), typeof(WaveVisualizer),
        new PropertyMetadata(VisualizerStyle.Bars, Restyle));
    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(nameof(Tint), typeof(VisualizerTint), typeof(WaveVisualizer),
        new PropertyMetadata(VisualizerTint.Cover, Restyle));
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(nameof(Color), typeof(Color), typeof(WaveVisualizer),
        new PropertyMetadata(Colors.Transparent, Restyle));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(int), typeof(WaveVisualizer),
        new PropertyMetadata(1, Restyle));
    public static readonly DependencyProperty StrengthProperty = DependencyProperty.Register(nameof(Strength), typeof(double), typeof(WaveVisualizer),
        new PropertyMetadata(1.0, Restyle));
    // How much of its space they can take.
    public static readonly DependencyProperty HeightShareProperty = DependencyProperty.Register(nameof(HeightShare), typeof(double), typeof(WaveVisualizer),
        new PropertyMetadata(0.38, Restyle));
    // Where the line of "in the center" runs (the middle of the cover); NaN: the middle of its space.
    public static readonly DependencyProperty MiddleProperty = DependencyProperty.Register(nameof(Middle), typeof(double), typeof(WaveVisualizer),
        new PropertyMetadata(double.NaN, Restyle));

    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public bool IsPreview { get => (bool)GetValue(IsPreviewProperty); set => SetValue(IsPreviewProperty, value); }
    public VisualizerFlow Flow { get => (VisualizerFlow)GetValue(FlowProperty); set => SetValue(FlowProperty, value); }
    public VisualizerPlace Place { get => (VisualizerPlace)GetValue(PlaceProperty); set => SetValue(PlaceProperty, value); }
    public VisualizerStyle BarStyle { get => (VisualizerStyle)GetValue(BarStyleProperty); set => SetValue(BarStyleProperty, value); }
    public VisualizerTint Tint { get => (VisualizerTint)GetValue(TintProperty); set => SetValue(TintProperty, value); }
    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public int Thickness { get => (int)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public double Strength { get => (double)GetValue(StrengthProperty); set => SetValue(StrengthProperty, value); }
    public double HeightShare { get => (double)GetValue(HeightShareProperty); set => SetValue(HeightShareProperty, value); }
    public double Middle { get => (double)GetValue(MiddleProperty); set => SetValue(MiddleProperty, value); }

    // Frames drawn since the start, on every visualizer: for the measures of the tests.
    public static long Drawn;

    private readonly DrawingVisual _visual = new();
    private readonly SpectrumColumns _columns = new();
    private Brush? _brush;
    private Pen? _pen;
    private int[] _corner = Array.Empty<int>();
    private int _cornerFor = -1;
    private bool _looking, _running, _hooked, _ticking;
    private TimeSpan _frameAt;
    private ScrollViewer? _scroller;

    public WaveVisualizer()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        AddVisualChild(_visual);
        IsVisibleChanged += (_, _) => UpdateRun();
        Loaded += (_, _) =>
        {
            if (!_hooked)
            {
                Themes.Changed += Forget;
                Ui.HiddenChanged += UpdateRun;
                _scroller = Ancestor<ScrollViewer>();
                if (_scroller != null && IsPreview) _scroller.ScrollChanged += OnScrolled;
            }
            _hooked = true;
            UpdateRun();
        };
        Unloaded += (_, _) =>
        {
            UpdateRun();
            Themes.Changed -= Forget;
            Ui.HiddenChanged -= UpdateRun;
            if (_scroller != null) _scroller.ScrollChanged -= OnScrolled;
            _hooked = false;
            _scroller = null;
        };
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;

    protected override Size MeasureOverride(Size available) => new(
        double.IsInfinity(available.Width) ? 0 : available.Width,
        double.IsInfinity(available.Height) ? 0 : available.Height);

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Forget();
    }

    private void Forget()
    {
        _brush = null;
        _pen = null;
    }

    private void OnScrolled(object sender, ScrollChangedEventArgs e) => UpdateRun();

    private void UpdateRun()
    {
        _running = (IsPlaying || (IsPreview && Seen())) && IsVisible && IsLoaded;
        if (_running != _looking)
        {
            _looking = _running;
            if (_running) Spectrum.Shared.Acquire();
            else Spectrum.Shared.Release();
        }
        // (stopped, it goes on until the bars have faded)
        if (IsVisible && IsLoaded)
        {
            if (_running || _columns.Presence > 0) Tick(true);
        }
        else
        {
            Tick(false);
            using (_visual.RenderOpen()) { }
        }
    }

    private void Tick(bool on)
    {
        if (on == _ticking) return;
        _ticking = on;
        if (on) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // (it can come more than once for the same frame)
        if (e is RenderingEventArgs r)
        {
            if (r.RenderingTime == _frameAt) return;
            _frameAt = r.RenderingTime;
        }
        if (AnimatedContent.Sliding == 0) Frame();
    }

    // The preview in the settings, scrolled away or with the window out of sight: nothing to draw, and no frames asked for.
    private bool Seen()
    {
        if (Ui.Hidden || !IsLoaded) return false;
        if (_scroller == null) return true;
        var box = TransformToAncestor(_scroller).TransformBounds(new Rect(RenderSize));
        return box.Bottom > 0 && box.Top < _scroller.ViewportHeight;
    }

    private T? Ancestor<T>() where T : DependencyObject
    {
        for (var d = VisualTreeHelper.GetParent(this); d != null; d = VisualTreeHelper.GetParent(d))
            if (d is T found) return found;
        return null;
    }

    private double MiddleOf(double h) => double.IsNaN(Middle) ? h / 2 : Math.Clamp(Middle, 0, h);

    private void Frame()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var style = BarStyle;
        bool moving = _columns.Update(SpectrumColumns.CountFor(w, Thickness, style), Flow, _running, IsPreview, Ui.Animations);
        bool smooth = style is VisualizerStyle.Line or VisualizerStyle.Wave;
        RenderOptions.SetEdgeMode(_visual, smooth ? EdgeMode.Unspecified : EdgeMode.Aliased);
        _visual.Opacity = _columns.Presence;
        // (the size of the interface scales everything too: whole pixels are the screen's)
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX * Ui.Zoom;
        Drawn++;
        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
        {
            if (moving)
                switch (style)
                {
                    case VisualizerStyle.Blocks: Blocks(c, w, h, dpi); break;
                    case VisualizerStyle.Dots: Dots(c, w, h, dpi); break;
                    case VisualizerStyle.Line: Curve(c, w, h, dpi, false); break;
                    case VisualizerStyle.Wave: Curve(c, w, h, dpi, true); break;
                    default: Bars(c, w, h, dpi, style == VisualizerStyle.Peaks); break;
                }
        }
        geometry.Freeze();
        if (_brush == null) MakeBrushes(w, h);
        using (var dc = _visual.RenderOpen())
        {
            if (style == VisualizerStyle.Line) dc.DrawGeometry(null, _pen, geometry);
            else dc.DrawGeometry(_brush, null, geometry);
        }
        if (!moving) Tick(false);
    }

    // The line a lane starts from, on a whole pixel (where the two of "in the center" meet no seam shows).
    private (double From, int Way) LaneOf(int lane, double h, double dpi)
    {
        var (from, way) = SpectrumColumns.Lane(Place, lane, h, MiddleOf(h));
        return (Math.Round(from * dpi) / dpi, way);
    }

    private void Bars(StreamGeometryContext c, double w, double h, double dpi, bool peaks)
    {
        var heights = _columns.Heights;
        int n = heights.Length, barPx = Math.Max(2, (int)Math.Round(SpectrumColumns.BarOf(w / n, Thickness, BarStyle) * dpi));
        double slot = w / n, bar = barPx / dpi, least = 3 / dpi, reach = SpectrumColumns.ReachOf(Place, h, HeightShare);
        var corner = CornerFor(barPx);
        for (int lane = 0, lanes = SpectrumColumns.LanesOf(Place); lane < lanes; lane++)
        {
            var (from, way) = LaneOf(lane, h, dpi);
            for (int i = 0; i < n; i++)
            {
                double x = Math.Round((i * slot + (slot - bar) / 2) * dpi) / dpi;
                double length = Math.Max(least, Math.Round(heights[i] * reach * dpi) / dpi);
                if (way < 0) Column(c, x, from - length, from, barPx, corner, dpi, true, false);
                else Column(c, x, from, from + length, barPx, corner, dpi, false, true);
                if (!peaks) continue;
                double peak = Math.Round(_columns.Peaks[i] * reach * dpi) / dpi + 4 / dpi;
                if (peak > length + 4 / dpi) Box(c, x, way < 0 ? from - peak : from + peak - 3 / dpi, bar, 3 / dpi);
            }
        }
    }

    private void Dots(StreamGeometryContext c, double w, double h, double dpi)
    {
        var heights = _columns.Heights;
        int n = heights.Length, barPx = Math.Max(3, (int)Math.Round(SpectrumColumns.BarOf(w / n, Thickness, BarStyle) * dpi));
        double slot = w / n, bar = barPx / dpi, reach = SpectrumColumns.ReachOf(Place, h, HeightShare);
        var corner = CornerFor(barPx);
        for (int lane = 0, lanes = SpectrumColumns.LanesOf(Place); lane < lanes; lane++)
        {
            var (from, way) = LaneOf(lane, h, dpi);
            for (int i = 0; i < n; i++)
            {
                double x = Math.Round((i * slot + (slot - bar) / 2) * dpi) / dpi;
                double along = Math.Round(heights[i] * Math.Max(0, reach - bar) * dpi) / dpi;
                double top = way < 0 ? from - bar - along : from + along;
                Column(c, x, top, top + bar, barPx, corner, dpi, true, true);
            }
        }
    }

    private void Blocks(StreamGeometryContext c, double w, double h, double dpi)
    {
        var heights = _columns.Heights;
        int n = heights.Length, barPx = Math.Max(3, (int)Math.Round(SpectrumColumns.BarOf(w / n, Thickness, BarStyle) * dpi));
        double slot = w / n, bar = barPx / dpi, block = Math.Max(4, Math.Min(barPx, 9)) / dpi, gap = 2 / dpi, step = block + gap;
        double reach = SpectrumColumns.ReachOf(Place, h, HeightShare);
        for (int lane = 0, lanes = SpectrumColumns.LanesOf(Place); lane < lanes; lane++)
        {
            var (from, way) = LaneOf(lane, h, dpi);
            for (int i = 0; i < n; i++)
            {
                double x = Math.Round((i * slot + (slot - bar) / 2) * dpi) / dpi;
                int count = Math.Max(1, (int)(heights[i] * reach / step));
                for (int k = 0; k < count; k++) Box(c, x, way < 0 ? from - (k + 1) * step + gap : from + k * step, bar, block);
            }
        }
    }

    private static void Box(StreamGeometryContext c, double x, double y, double width, double height)
    {
        c.BeginFigure(new Point(x, y), true, true);
        c.LineTo(new Point(x + width, y), false, false);
        c.LineTo(new Point(x + width, y + height), false, false);
        c.LineTo(new Point(x, y + height), false, false);
    }

    // A column with the corners of its tip cut along a circle, whole pixels at a time: as round as a small bar needs, with no antialiasing.
    private static void Column(StreamGeometryContext c, double x, double top, double bottom, int barPx, int[] corner, double dpi, bool roundTop, bool roundBottom)
    {
        double right = x + barPx / dpi;
        // (a bar too narrow for round corners has none to cut: with no rows the top would be a diagonal, a sliver and slow to fill)
        roundTop &= corner.Length > 0;
        roundBottom &= corner.Length > 0;
        if (roundTop)
        {
            c.BeginFigure(new Point(x, top + corner.Length / dpi), true, true);
            for (int k = corner.Length - 1; k >= 0; k--)
            {
                double inset = corner[k] / dpi, y = top + k / dpi;
                c.LineTo(new Point(x + inset, y + 1 / dpi), false, false);
                c.LineTo(new Point(x + inset, y), false, false);
            }
            for (int k = 0; k < corner.Length; k++)
            {
                double inset = corner[k] / dpi, y = top + k / dpi;
                c.LineTo(new Point(right - inset, y), false, false);
                c.LineTo(new Point(right - inset, y + 1 / dpi), false, false);
            }
        }
        else
        {
            c.BeginFigure(new Point(x, top), true, true);
            c.LineTo(new Point(right, top), false, false);
        }
        if (!roundBottom)
        {
            c.LineTo(new Point(right, bottom), false, false);
            c.LineTo(new Point(x, bottom), false, false);
            return;
        }
        for (int k = corner.Length - 1; k >= 0; k--)
        {
            double inset = corner[k] / dpi, y = bottom - k / dpi;
            c.LineTo(new Point(right - inset, y - 1 / dpi), false, false);
            c.LineTo(new Point(right - inset, y), false, false);
        }
        for (int k = 0; k < corner.Length; k++)
        {
            double inset = corner[k] / dpi, y = bottom - k / dpi;
            c.LineTo(new Point(x + inset, y), false, false);
            c.LineTo(new Point(x + inset, y - 1 / dpi), false, false);
        }
    }

    // How far each pixel row of a corner stands in (only the rows that do).
    private int[] CornerFor(int barPx)
    {
        if (_cornerFor == barPx) return _corner;
        _cornerFor = barPx;
        int r = Math.Min(barPx / 2, 5);
        var rows = new List<int>();
        for (int k = 0; k < r; k++)
        {
            double y = r - k - 0.5;
            int inset = (int)Math.Round(r - Math.Sqrt(r * r - y * y));
            if (inset > 0) rows.Add(inset);
        }
        return _corner = rows.ToArray();
    }

    // One line through the tips of each lane, smoothed; filled: the area between it and where they start. In the center the two
    // sides are one figure: two figures touching would leave a seam, or a hole, along the middle.
    private void Curve(StreamGeometryContext c, double w, double h, double dpi, bool filled)
    {
        var heights = _columns.Heights;
        int n = heights.Length;
        double step = w / (n - 1), inset = filled ? 0 : SpectrumColumns.LineOf(Thickness) / 2;
        double reach = SpectrumColumns.ReachOf(Place, h, HeightShare) - inset;
        void Forward(double from, int way)
        {
            for (int i = 0; i < n - 1; i++)
            {
                double x0 = i * step, y0 = from + way * heights[i] * reach, y1 = from + way * heights[i + 1] * reach;
                c.QuadraticBezierTo(new Point(x0, y0), new Point(x0 + step / 2, (y0 + y1) / 2), !filled, true);
            }
            c.LineTo(new Point(w, from + way * heights[n - 1] * reach), !filled, true);
        }
        void Backward(double from, int way)
        {
            for (int i = n - 1; i > 0; i--)
            {
                double x0 = i * step, y0 = from + way * heights[i] * reach, y1 = from + way * heights[i - 1] * reach;
                c.QuadraticBezierTo(new Point(x0, y0), new Point(x0 - step / 2, (y0 + y1) / 2), false, true);
            }
            c.LineTo(new Point(0, from + way * heights[0] * reach), false, true);
        }
        if (filled && Place == VisualizerPlace.Center)
        {
            var (middle, _) = LaneOf(0, h, dpi);
            c.BeginFigure(new Point(0, middle - heights[0] * reach), true, true);
            Forward(middle, -1);
            c.LineTo(new Point(w, middle + heights[n - 1] * reach), false, true);
            Backward(middle, 1);
            return;
        }
        for (int lane = 0, lanes = SpectrumColumns.LanesOf(Place); lane < lanes; lane++)
        {
            var (from, way) = LaneOf(lane, h, dpi);
            // (a line at rest lies whole inside the space)
            if (!filled && from <= 0) from = inset;
            else if (!filled && from >= h) from = h - inset;
            if (filled)
            {
                c.BeginFigure(new Point(0, from), true, true);
                c.LineTo(new Point(0, from + way * heights[0] * reach), false, false);
            }
            else c.BeginFigure(new Point(0, from + way * heights[0] * reach), false, false);
            Forward(from, way);
            if (filled) c.LineTo(new Point(w, from), false, false);
        }
    }

    private Color Base() => Tint switch
    {
        VisualizerTint.Accent => Themes.Get("Accent"),
        VisualizerTint.Text => Themes.Get("Text"),
        _ => Color.A > 0 ? Color : Themes.Get("Accent"),
    };

    private void MakeBrushes(double w, double h)
    {
        byte A(double a) => (byte)Math.Clamp(a * Strength * 255, 0, 255);
        Brush brush, line;
        if (Tint == VisualizerTint.Rainbow)
        {
            var stops = new GradientStopCollection();
            string[] hues = { "#FF4D6D", "#FF9F3D", "#FFE14D", "#4DE08A", "#4DB8FF", "#9D6BFF", "#FF4DC4" };
            for (int i = 0; i < hues.Length; i++)
            {
                var c = (Color)ColorConverter.ConvertFromString(hues[i]);
                stops.Add(new GradientStop(Color.FromArgb(A(0.8), c.R, c.G, c.B), i / (hues.Length - 1.0)));
            }
            brush = line = new LinearGradientBrush(stops, new Point(0, 0), new Point(w, 0)) { MappingMode = BrushMappingMode.Absolute };
        }
        else
        {
            var c = Base();
            var stops = new GradientStopCollection();
            foreach (var (at, alpha) in SpectrumColumns.FadeOf(Place, h, SpectrumColumns.ReachOf(Place, h, HeightShare), MiddleOf(h)))
                stops.Add(new GradientStop(Color.FromArgb(A(alpha), c.R, c.G, c.B), at));
            // (in pixels of the visual, not of the geometry: the colours stay where they are while the tallest bar moves)
            brush = new LinearGradientBrush(stops, new Point(0, 0), new Point(0, h)) { MappingMode = BrushMappingMode.Absolute };
            line = new SolidColorBrush(Color.FromArgb(A(0.9), c.R, c.G, c.B));
        }
        brush.Freeze();
        line.Freeze();
        _brush = brush;
        _pen = new Pen(line, SpectrumColumns.LineOf(Thickness)) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        _pen.Freeze();
    }
}
