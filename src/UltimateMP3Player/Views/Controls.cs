using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace UltimateMP3Player.Views;

// Sliders: press anywhere and drag.
public static class SliderDrag
{
    public static void Register()
        => EventManager.RegisterClassHandler(typeof(Slider), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnDown), true);

    private static void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider s || !s.IsEnabled || e.ClickCount > 1) return;
        if (s.Template?.FindName("PART_Track", s) is not Track track || track.Thumb is not { } thumb || thumb.IsDragging) return;
        if (e.OriginalSource is DependencyObject d && Ui.FindAncestor<Thumb>(d) == thumb) return;
        s.Value = track.ValueFromPoint(e.GetPosition(track));
        s.UpdateLayout();
        thumb.RaiseEvent(new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            Source = thumb,
        });
        e.Handled = true;
    }
}

// Content host that fades and slides in each new page.
public sealed class AnimatedContent : ContentControl
{
    private readonly TranslateTransform _shift = new();

    public AnimatedContent()
    {
        RenderTransform = _shift;
        Focusable = false;
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (!Ui.Animations || oldContent == null || !IsLoaded) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var time = TimeSpan.FromMilliseconds(220);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, time) { EasingFunction = ease });
        _shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, time) { EasingFunction = ease });
    }
}

// Cards in as many columns as fit, each into the shortest one.
public sealed class MasonryPanel : Panel
{
    public static readonly DependencyProperty ColumnWidthProperty = DependencyProperty.Register(nameof(ColumnWidth), typeof(double),
        typeof(MasonryPanel), new FrameworkPropertyMetadata(560.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(nameof(MaxColumns), typeof(int),
        typeof(MasonryPanel), new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double),
        typeof(MasonryPanel), new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ColumnWidth { get => (double)GetValue(ColumnWidthProperty); set => SetValue(ColumnWidthProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    private readonly List<(int Col, double Y)> _slots = new();

    private int Columns(double width)
        => double.IsInfinity(width) ? 1 : Math.Clamp((int)((width + Gap) / (ColumnWidth + Gap)), 1, Math.Max(1, MaxColumns));

    protected override Size MeasureOverride(Size available)
    {
        int cols = Columns(available.Width);
        double colW = double.IsInfinity(available.Width) ? ColumnWidth : (available.Width - Gap * (cols - 1)) / cols;
        var heights = new double[cols];
        _slots.Clear();
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(colW, double.PositiveInfinity));
            if (child.Visibility == Visibility.Collapsed) { _slots.Add((0, 0)); continue; }
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
        for (int i = 0; i < InternalChildren.Count && i < _slots.Count; i++)
        {
            var (c, y) = _slots[i];
            InternalChildren[i].Arrange(new Rect(c * (colW + Gap), y, colW, InternalChildren[i].DesiredSize.Height));
        }
        return final;
    }
}
