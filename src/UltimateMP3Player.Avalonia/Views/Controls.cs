using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace UltimateMP3Player.Views;

// Shortcuts as the system writes them (⌘ on macOS).
public static class Keys
{
    public static string Command => OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+";
    public static string SelectAll => Command + "A";
}

// The spinning arcs (Ellipse.spinner, the Windows app's Spinner style): a turn every 0.9 s while on screen; hidden ones
// only look every 300 ms whether they're back (no frames drawn for nothing), and not at all while the app is hidden.
public static class Spinners
{
    public static void Register()
        => Control.LoadedEvent.AddClassHandler<Avalonia.Controls.Shapes.Ellipse>((e, _) =>
        {
            if (e.Classes.Contains("spinner")) Spin(e);
            else if (e.Classes.Contains("pulse")) Pulse(e);
        });

    // The ring going out behind "searching" (the Windows app's Pulse style): 0.6 → 1.5 and fading, every 2.2 s.
    private static void Pulse(Control e)
    {
        var scale = new ScaleTransform(0.6, 0.6);
        e.RenderTransform = scale;
        var started = System.Diagnostics.Stopwatch.StartNew();
        void Next()
        {
            if (!e.IsLoaded) return;
            if (!e.IsEffectivelyVisible)
            {
                Ui.Later(Next);
                return;
            }
            TopLevel.GetTopLevel(e)?.RequestAnimationFrame(_ =>
            {
                double t = started.Elapsed.TotalSeconds % 2.2 / 2.2;
                scale.ScaleX = scale.ScaleY = 0.6 + 0.9 * t;
                e.Opacity = 0.7 * (1 - t);
                Next();
            });
        }
        Next();
    }

    private static void Spin(Control e)
    {
        var rotate = e.RenderTransform as RotateTransform ?? new RotateTransform();
        e.RenderTransform = rotate;
        var started = System.Diagnostics.Stopwatch.StartNew();
        void Next()
        {
            if (!e.IsLoaded) return;
            if (!e.IsEffectivelyVisible)
            {
                Ui.Later(Next);
                return;
            }
            TopLevel.GetTopLevel(e)?.RequestAnimationFrame(_ =>
            {
                rotate.Angle = started.Elapsed.TotalSeconds / 0.9 * 360 % 360;
                Next();
            });
        }
        Next();
    }
}

// The Windows app makes a click on a slider's track jump there and keep dragging: Avalonia's sliders already do.
public static class SliderDrag
{
    public static void Register() { }
}

// Content host that fades and slides in each new page.
public sealed class AnimatedContent : ContentControl
{
    private readonly TranslateTransform _shift = new();

    public AnimatedContent()
    {
        RenderTransform = _shift;
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ContentProperty || change.OldValue == null || !Ui.Animations || !IsLoaded) return;
        // After the new page is in (not while the content is being set): 220 ms, cubic ease out, like the Windows app.
        _started = null;
        Opacity = 0;
        _shift.Y = 14;
        if (!_running)
        {
            _running = true;
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
        }
    }

    private TimeSpan? _started;
    private bool _running;

    private void OnFrame(TimeSpan now)
    {
        _started ??= now;
        double t = Math.Clamp((now - _started.Value).TotalMilliseconds / 220, 0, 1);
        double e = 1 - Math.Pow(1 - t, 3);
        Opacity = e;
        _shift.Y = 14 * (1 - e);
        if (t < 1 && TopLevel.GetTopLevel(this) is { } top) top.RequestAnimationFrame(OnFrame);
        else
        {
            Opacity = 1;
            _shift.Y = 0;
            _running = false;
        }
    }
}
// Cards in equal columns that fill the width: as many as fit at MinItemWidth, then widened (no ragged gap on the right).
public sealed class CardGrid : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty = AvaloniaProperty.Register<CardGrid, double>(nameof(MinItemWidth), 176);
    public static readonly StyledProperty<double> GapProperty = AvaloniaProperty.Register<CardGrid, double>(nameof(Gap), 12);
    // 0 = all rows; otherwise the cards that don't fit are left out.
    public static readonly StyledProperty<int> MaxRowsProperty = AvaloniaProperty.Register<CardGrid, int>(nameof(MaxRows));
    // From 560 wide on (a phone turned, a tablet), cards at least this wide instead (0 = MinItemWidth everywhere).
    public static readonly StyledProperty<double> WideItemWidthProperty = AvaloniaProperty.Register<CardGrid, double>(nameof(WideItemWidth));

    static CardGrid() => AffectsMeasure<CardGrid>(MinItemWidthProperty, GapProperty, MaxRowsProperty, WideItemWidthProperty);

    public double MinItemWidth { get => GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double WideItemWidth { get => GetValue(WideItemWidthProperty); set => SetValue(WideItemWidthProperty, value); }
    public double Gap { get => GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public int MaxRows { get => GetValue(MaxRowsProperty); set => SetValue(MaxRowsProperty, value); }

    private double _rowHeight;

    private int Limit(int cols) => MaxRows > 0 ? MaxRows * cols : int.MaxValue;

    private (int Cols, double ItemWidth) Columns(double width)
    {
        if (double.IsInfinity(width)) return (Math.Max(1, Children.Count), MinItemWidth);
        double min = WideItemWidth > 0 && width >= 560 ? WideItemWidth : MinItemWidth;
        int cols = Math.Max(1, (int)((width + Gap) / (min + Gap)));
        return (cols, Math.Max(0, (width - Gap * (cols - 1)) / cols));
    }

    // Every row as tall as the tallest card, so they all line up.
    protected override Size MeasureOverride(Size available)
    {
        var (cols, itemW) = Columns(available.Width);
        _rowHeight = 0;
        int count = 0, limit = Limit(cols);
        foreach (var child in Children)
        {
            child.Measure(new Size(itemW, double.PositiveInfinity));
            if (!child.IsVisible || count == limit) continue;
            _rowHeight = Math.Max(_rowHeight, child.DesiredSize.Height);
            count++;
        }
        if (count == 0) return new Size();
        int rows = (count + cols - 1) / cols;
        double w = double.IsInfinity(available.Width) ? cols * itemW + (cols - 1) * Gap : available.Width;
        return new Size(w, rows * _rowHeight + (rows - 1) * Gap);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var (cols, itemW) = Columns(final.Width);
        int i = 0, limit = Limit(cols);
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            // Past the last row: a zero-size slot hides it.
            child.Arrange(i < limit ? new Rect(i % cols * (itemW + Gap), i / cols * (_rowHeight + Gap), itemW, _rowHeight) : new Rect());
            i++;
        }
        return final;
    }
}

// A row whose first child (a title) gets the room the others leave, the others right after it:
// "A long title… [badge]" instead of a title that pushes the badge out and never shows "…".
public sealed class InlinePanel : Panel
{
    private double Others()
    {
        double w = 0;
        for (int i = 1; i < Children.Count; i++) w += Children[i].DesiredSize.Width;
        return w;
    }

    protected override Size MeasureOverride(Size available)
    {
        if (Children.Count == 0) return new Size();
        double height = 0;
        for (int i = 1; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(double.PositiveInfinity, available.Height));
            height = Math.Max(height, Children[i].DesiredSize.Height);
        }
        double others = Others();
        var first = Children[0];
        first.Measure(new Size(Math.Max(0, available.Width - others), available.Height));
        return new Size(first.DesiredSize.Width + others, Math.Max(height, first.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (Children.Count == 0) return final;
        double others = Others();
        var first = Children[0];
        double x = Math.Min(first.DesiredSize.Width, Math.Max(0, final.Width - others));
        first.Arrange(new Rect(0, 0, x, final.Height));
        for (int i = 1; i < Children.Count; i++)
        {
            var c = Children[i];
            c.Arrange(new Rect(x, 0, c.DesiredSize.Width, final.Height));
            x += c.DesiredSize.Width;
        }
        return final;
    }
}

// As tall as it is wide: card covers that grow with the grid.
public sealed class SquareBox : Decorator
{
    protected override Size MeasureOverride(Size constraint)
    {
        double side = double.IsInfinity(constraint.Width) ? 152 : constraint.Width;
        Child?.Measure(new Size(side, side));
        return new Size(side, side);
    }

    protected override Size ArrangeOverride(Size size)
    {
        Child?.Arrange(new Rect(size));
        return size;
    }
}

// Cards in as many columns as fit, each into the shortest one.
public sealed class MasonryPanel : Panel
{
    public static readonly StyledProperty<double> ColumnWidthProperty = AvaloniaProperty.Register<MasonryPanel, double>(nameof(ColumnWidth), 560);
    public static readonly StyledProperty<int> MaxColumnsProperty = AvaloniaProperty.Register<MasonryPanel, int>(nameof(MaxColumns), 3);
    public static readonly StyledProperty<double> GapProperty = AvaloniaProperty.Register<MasonryPanel, double>(nameof(Gap), 16);

    static MasonryPanel() => AffectsMeasure<MasonryPanel>(ColumnWidthProperty, MaxColumnsProperty, GapProperty);

    public double ColumnWidth { get => GetValue(ColumnWidthProperty); set => SetValue(ColumnWidthProperty, value); }
    public int MaxColumns { get => GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
    public double Gap { get => GetValue(GapProperty); set => SetValue(GapProperty, value); }

    private readonly List<(int Col, double Y)> _slots = new();

    private int Columns(double width)
        => double.IsInfinity(width) ? 1 : Math.Clamp((int)((width + Gap) / (ColumnWidth + Gap)), 1, Math.Max(1, MaxColumns));

    protected override Size MeasureOverride(Size available)
    {
        int cols = Columns(available.Width);
        double colW = double.IsInfinity(available.Width) ? ColumnWidth : (available.Width - Gap * (cols - 1)) / cols;
        var heights = new double[cols];
        _slots.Clear();
        foreach (var child in Children)
        {
            child.Measure(new Size(colW, double.PositiveInfinity));
            if (!child.IsVisible) { _slots.Add((0, 0)); continue; }
            int c = Array.IndexOf(heights, heights.Min());
            _slots.Add((c, heights[c]));
            heights[c] += child.DesiredSize.Height + Gap;
        }
        double w = double.IsInfinity(available.Width) ? colW : available.Width;
        return new Size(w, Math.Max(0, heights.Max() - Gap));
    }

    protected override Size ArrangeOverride(Size final)
    {
        int cols = Columns(final.Width);
        double colW = (final.Width - Gap * (cols - 1)) / cols;
        for (int i = 0; i < Children.Count && i < _slots.Count; i++)
        {
            var (c, y) = _slots[i];
            Children[i].Arrange(new Rect(c * (colW + Gap), y, colW, Children[i].DesiredSize.Height));
        }
        return final;
    }
}
