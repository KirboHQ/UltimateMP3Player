using System.Windows;
using System.Windows.Media;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Views;

// The equalizer's real frequency response, drawn behind the sliders.
public sealed class EqCurve : FrameworkElement
{
    public static readonly DependencyProperty GainsProperty = DependencyProperty.Register(nameof(Gains), typeof(double[]), typeof(EqCurve),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(nameof(Version), typeof(int), typeof(EqCurve),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(EqCurve),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public double[]? Gains { get => (double[]?)GetValue(GainsProperty); set => SetValue(GainsProperty, value); }
    public int Version { get => (int)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    // Distance of ±12 dB from the edges (half the slider thumb).
    public double Inset { get; set; } = 6;

    protected override void OnRender(DrawingContext dc)
    {
        var gains = Gains;
        double w = ActualWidth, h = ActualHeight;
        if (gains == null || w <= 0 || h <= 0) return;
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
                    l.BeginFigure(new Point(x, y), false, false);
                    f.BeginFigure(new Point(x, mid), true, true);
                }
                l.LineTo(new Point(x, y), true, true);
                f.LineTo(new Point(x, y), true, true);
            }
            f.LineTo(new Point(w, mid), true, true);
        }
        line.Freeze();
        fill.Freeze();
        var tint = Stroke.CloneCurrentValue();
        tint.Opacity = 0.14;
        tint.Freeze();
        dc.DrawGeometry(tint, null, fill);
        var pen = new Pen(Stroke, 2) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}
