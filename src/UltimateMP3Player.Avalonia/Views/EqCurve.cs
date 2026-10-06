using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Views;

// The equalizer's real frequency response, drawn behind the sliders.
public sealed class EqCurve : Control
{
    public static readonly StyledProperty<double[]?> GainsProperty = AvaloniaProperty.Register<EqCurve, double[]?>(nameof(Gains));
    public static readonly StyledProperty<int> VersionProperty = AvaloniaProperty.Register<EqCurve, int>(nameof(Version));
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<EqCurve, IBrush?>(nameof(Stroke), Brushes.White);

    static EqCurve() => AffectsRender<EqCurve>(GainsProperty, VersionProperty, StrokeProperty);

    public double[]? Gains { get => GetValue(GainsProperty); set => SetValue(GainsProperty, value); }
    public int Version { get => GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    // Distance of ±12 dB from the edges (half the slider thumb).
    public double Inset { get; set; } = 6;

    public override void Render(DrawingContext dc)
    {
        var gains = Gains;
        double w = Bounds.Width, h = Bounds.Height;
        if (gains == null || w <= 0 || h <= 0 || Stroke == null) return;
        double top = Inset, span = h - 2 * Inset, mid = top + span / 2;
        double Y(double db) => mid - Math.Clamp(db, -Equalizer.MaxGain, Equalizer.MaxGain) / Equalizer.MaxGain * span / 2;
        // Band i sits at the centre of column i of ten.
        double col = w / Equalizer.BandCount;
        double lo = Math.Log2(Equalizer.Frequencies[0]), hi = Math.Log2(Equalizer.Frequencies[^1]);

        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var l = line.Open())
        using (var f = fill.Open())
        {
            int steps = Math.Max(40, (int)(w / 3));
            for (int i = 0; i <= steps; i++)
            {
                double x = i * w / steps;
                double band = (x - col / 2) / (w - col) * (hi - lo) + lo;
                double y = Y(Equalizer.ResponseDb(Math.Pow(2, band), gains));
                if (i == 0)
                {
                    l.BeginFigure(new Point(x, y), false);
                    f.BeginFigure(new Point(x, mid), true);
                }
                l.LineTo(new Point(x, y));
                f.LineTo(new Point(x, y));
            }
            f.LineTo(new Point(w, mid));
            l.EndFigure(false);
            f.EndFigure(true);
        }
        using (dc.PushOpacity(0.14))
            dc.DrawGeometry(Stroke, null, fill);
        dc.DrawGeometry(null, new Pen(Stroke, 2) { LineJoin = PenLineJoin.Round }, line);
    }
}
