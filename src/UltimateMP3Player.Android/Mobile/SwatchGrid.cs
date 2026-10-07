using Avalonia;
using Avalonia.Controls;

namespace UltimateMP3Player.Views;

// Colour swatches (the app's colours, a profile's, a tag's) laid out evenly: as many per row as fit, the rows balanced
// (8 → 4 + 4, not 7 + 1), each swatch centred in an equal share of the width, a shorter last row centred too.
public sealed class SwatchGrid : Panel
{
    public static readonly StyledProperty<double> CellProperty = AvaloniaProperty.Register<SwatchGrid, double>(nameof(Cell), 56);

    // The narrowest share of the width a swatch gets.
    public double Cell
    {
        get => GetValue(CellProperty);
        set => SetValue(CellProperty, value);
    }

    private List<Control> Shown => Children.Where(c => c.IsVisible).ToList();

    private (int Columns, int Rows) Grid(int count, double width)
    {
        if (count == 0) return (1, 0);
        int fit = double.IsInfinity(width) ? count : Math.Max(1, (int)(width / Cell));
        int rows = (int)Math.Ceiling(count / (double)Math.Min(fit, count));
        return ((int)Math.Ceiling(count / (double)rows), rows);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var shown = Shown;
        double rowHeight = 0;
        foreach (var c in Children)
        {
            c.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (c.IsVisible) rowHeight = Math.Max(rowHeight, c.DesiredSize.Height);
        }
        var (columns, rows) = Grid(shown.Count, availableSize.Width);
        double width = double.IsInfinity(availableSize.Width) ? columns * Cell : availableSize.Width;
        return new Size(width, rows * rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var shown = Shown;
        var (columns, _) = Grid(shown.Count, finalSize.Width);
        double cell = finalSize.Width / columns;
        double rowHeight = shown.Count > 0 ? shown.Max(c => c.DesiredSize.Height) : 0;
        for (int i = 0; i < shown.Count; i++)
        {
            int row = i / columns, column = i % columns;
            int inRow = Math.Min(columns, shown.Count - row * columns);
            double start = (columns - inRow) * cell / 2;
            var c = shown[i];
            var size = c.DesiredSize;
            c.Arrange(new Rect(start + column * cell + (cell - size.Width) / 2, row * rowHeight, size.Width, size.Height));
        }
        return finalSize;
    }
}
