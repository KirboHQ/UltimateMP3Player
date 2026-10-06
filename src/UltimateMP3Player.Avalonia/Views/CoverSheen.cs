using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace UltimateMP3Player.Views;

// The glossy sheen over the tilting cover (the Windows app's DrawingBrush on the 3D cover): diagonal streaks sliding
// against the mouse, a slower wide band behind them, brightest near the light, and the edge catching it.
// Drawn in a 400×400 space like the original.
public sealed class CoverSheen : Control
{
    private double _near, _far, _rimOpacity;
    private Point _maskCenter = new(200, 200), _rimStart = new(0, 0), _rimEnd = new(400, 400);

    private static readonly Matrix Lean = Matrix.CreateTranslation(-200, -200) * Matrix.CreateRotation(28 * Math.PI / 180) * Matrix.CreateTranslation(200, 200);

    public CoverSheen() => IsHitTestVisible = false;

    // x, y: the mouse as the cover follows it (-1..1). The light sits opposite.
    public void Place(double x, double y, double cos, double sin)
    {
        double shift = -(x * cos + y * sin) * 230;
        _near = shift;
        _far = shift * 0.55;
        _maskCenter = new Point(200 - x * 180, 200 - y * 180);
        double len = Math.Sqrt(x * x + y * y);
        if (len > 0.001)
        {
            double dx = -x / len * 283, dy = -y / len * 283;
            _rimStart = new Point(200 + dx, 200 + dy);
            _rimEnd = new Point(200 - dx, 200 - dy);
        }
        _rimOpacity = Math.Min(1, len * 1.6);
        InvalidateVisual();
    }

    private static IBrush Band(params (uint Argb, double Offset)[] stops)
    {
        var b = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        };
        foreach (var (argb, offset) in stops) b.GradientStops.Add(new GradientStop(Color.FromUInt32(argb), offset));
        return b;
    }

    private static readonly IBrush FarBand = Band((0x00FFFFFF, 0), (0x12FFFFFF, 0.5), (0x00FFFFFF, 1));
    private static readonly IBrush GlassBand = Band((0x00FFFFFF, 0), (0x12FFFFFF, 0.3), (0x22FFFFFF, 0.5), (0x12FFFFFF, 0.7), (0x00FFFFFF, 1));
    private static readonly IBrush Cyan = Band((0x0070E4FF, 0), (0x2A70E4FF, 1));
    private static readonly IBrush Line = Band((0x18FFFFFF, 0), (0x5CFFFFFF, 0.5), (0x18FFFFFF, 1));
    private static readonly IBrush Pink = Band((0x2AFF7ACF, 0), (0x00FF7ACF, 1));
    private static readonly IBrush Thin1 = new SolidColorBrush(Color.FromUInt32(0x40FFFFFF));
    private static readonly IBrush Thin2 = new SolidColorBrush(Color.FromUInt32(0x26FFFFFF));

    private static Rect Strip(double left, double right) => new(left, -250, right - left, 900);

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        var full = new Rect(0, 0, 400, 400);
        using (dc.PushTransform(Matrix.CreateScale(w / 400, h / 400)))
        using (dc.PushClip(new RoundedRect(full, 14)))
        {
            var mask = new RadialGradientBrush
            {
                Center = new RelativePoint(_maskCenter, RelativeUnit.Absolute),
                GradientOrigin = new RelativePoint(_maskCenter, RelativeUnit.Absolute),
                RadiusX = new RelativeScalar(330, RelativeUnit.Absolute),
                RadiusY = new RelativeScalar(330, RelativeUnit.Absolute),
                GradientStops = { new GradientStop(Color.FromUInt32(0xFF000000), 0), new GradientStop(Color.FromUInt32(0x40000000), 1) },
            };
            using (dc.PushClip(full))
            using (dc.PushOpacityMask(mask, full))
            {
                // far layer: one wide soft band, moving slower
                using (dc.PushTransform(Matrix.CreateTranslation(_far, 0) * Lean))
                    dc.FillRectangle(FarBand, Strip(-60, 160));
                // near layer: glass band, crisp lines and an iridescent fringe
                using (dc.PushTransform(Matrix.CreateTranslation(_near, 0) * Lean))
                {
                    dc.FillRectangle(GlassBand, Strip(140, 260));
                    dc.FillRectangle(Cyan, Strip(258, 272));
                    dc.FillRectangle(Line, Strip(272, 280));
                    dc.FillRectangle(Pink, Strip(280, 294));
                    dc.FillRectangle(Thin1, Strip(304, 306.5));
                    dc.FillRectangle(Thin2, Strip(116, 118));
                }
            }
            // the edge catching the light
            if (_rimOpacity > 0.001)
            {
                var rim = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(_rimStart, RelativeUnit.Absolute),
                    EndPoint = new RelativePoint(_rimEnd, RelativeUnit.Absolute),
                    Opacity = _rimOpacity,
                    GradientStops = { new GradientStop(Color.FromUInt32(0x80FFFFFF), 0), new GradientStop(Color.FromUInt32(0x00FFFFFF), 0.45) },
                };
                dc.DrawRectangle(null, new Pen(rim, 2.5), new Rect(1, 1, 398, 398), 13, 13);
            }
        }
    }
}
