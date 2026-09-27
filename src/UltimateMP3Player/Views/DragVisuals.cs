using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace UltimateMP3Player.Views;

// Drag feedback: a floating copy of the row and an insertion line.
public static class DragVisuals
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    private static InsertionAdorner? _line;

    public static DragDropEffects Run(FrameworkElement row, object data, DragDropEffects allowed)
    {
        var root = Window.GetWindow(row)?.Content as FrameworkElement;
        var layer = root != null ? AdornerLayer.GetAdornerLayer(root) : null;
        if (root == null || layer == null) return DragDrop.DoDragDrop(row, data, allowed);

        var grab = System.Windows.Input.Mouse.GetPosition(row);
        var ghost = new GhostAdorner(root, Snapshot(row), grab);
        layer.Add(ghost);
        GiveFeedbackEventHandler follow = (_, e) =>
        {
            if (GetCursorPos(out var p)) ghost.MoveTo(root.PointFromScreen(new Point(p.X, p.Y)));
            e.UseDefaultCursors = true;
        };
        row.GiveFeedback += follow;
        var opacity = row.Opacity;
        row.Opacity = 0.35;
        try { return DragDrop.DoDragDrop(row, data, allowed); }
        finally
        {
            row.GiveFeedback -= follow;
            row.Opacity = opacity;
            layer.Remove(ghost);
            HideLine();
        }
    }

    private static ImageSource Snapshot(FrameworkElement e)
    {
        var dpi = VisualTreeHelper.GetDpi(e);
        int w = Math.Max(1, (int)Math.Ceiling(e.ActualWidth * dpi.DpiScaleX)), h = Math.Max(1, (int)Math.Ceiling(e.ActualHeight * dpi.DpiScaleY));
        var bmp = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRoundedRectangle((Brush)Application.Current.Resources["Surface3Brush"], null, new Rect(0, 0, e.ActualWidth, e.ActualHeight), 8, 8);
            var box = new Rect(0, 0, e.ActualWidth, e.ActualHeight);
            dc.DrawRectangle(new VisualBrush(e) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = box }, null, box);
        }
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    // Where a drop lands: before or after the row under the mouse (section titles don't count).
    public static (object? Target, bool After) Hit(ListBox list, DragEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        var item = d != null ? Ui.FindAncestor<ListBoxItem>(d) : null;
        if (item == null || ItemsControl.ItemsControlFromItemContainer(item) != list) return (null, true);
        var row = RowOf(item);
        bool after = e.GetPosition(row).Y > row.ActualHeight / 2;
        ShowLine(list, row, after);
        return (item.DataContext, after);
    }

    // The element named "Row" in the item template, or the whole item.
    private static FrameworkElement RowOf(ListBoxItem item)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(item);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is FrameworkElement { Name: "Row" } fe) return fe;
            for (int i = VisualTreeHelper.GetChildrenCount(d) - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
        return item;
    }

    private static void ShowLine(ListBox list, FrameworkElement row, bool after)
    {
        double y = row.TranslatePoint(new Point(0, after ? row.ActualHeight : 0), list).Y;
        y = Math.Clamp(y, 0, list.ActualHeight);
        if (_line == null || _line.AdornedElement != list)
        {
            HideLine();
            if (AdornerLayer.GetAdornerLayer(list) is not { } layer) return;
            _line = new InsertionAdorner(list, y);
            layer.Add(_line);
        }
        else _line.GoTo(y);
    }

    public static void HideLine()
    {
        if (_line == null) return;
        AdornerLayer.GetAdornerLayer(_line.AdornedElement)?.Remove(_line);
        _line = null;
    }

    // Index for PlayQueue.Move / Profile.MoveTrack.
    public static int MoveIndex(int from, int target, bool after)
    {
        int to = after ? target + 1 : target;
        return from < to ? to - 1 : to;
    }

    private sealed class GhostAdorner : Adorner
    {
        private readonly ImageSource _image;
        private readonly Point _grab;
        private readonly TranslateTransform _at = new();
        private readonly ScaleTransform _scale = new(1, 1);

        public GhostAdorner(UIElement root, ImageSource image, Point grab) : base(root)
        {
            _image = image;
            _grab = grab;
            IsHitTestVisible = false;
            Opacity = 0.92;
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 6, Opacity = 0.55, Color = Colors.Black };
            var group = new TransformGroup();
            group.Children.Add(new TranslateTransform(-grab.X, -grab.Y));
            group.Children.Add(_scale);
            group.Children.Add(new RotateTransform(-2));
            group.Children.Add(_at);
            RenderTransform = group;
            if (Ui.Animations)
            {
                var pop = new DoubleAnimation(0.9, 1.03, TimeSpan.FromMilliseconds(160)) { EasingFunction = new BackEase { Amplitude = 0.4 } };
                _scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                _scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            }
        }

        public void MoveTo(Point p)
        {
            _at.X = p.X;
            _at.Y = p.Y;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var w = ((BitmapSource)_image).PixelWidth / (VisualTreeHelper.GetDpi(this).DpiScaleX);
            var h = ((BitmapSource)_image).PixelHeight / (VisualTreeHelper.GetDpi(this).DpiScaleY);
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), 8, 8));
            dc.DrawImage(_image, new Rect(0, 0, w, h));
            dc.Pop();
        }
    }

    private sealed class InsertionAdorner : Adorner
    {
        private static readonly DependencyProperty YProperty = DependencyProperty.Register("Y", typeof(double), typeof(InsertionAdorner),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public InsertionAdorner(UIElement list, double y) : base(list)
        {
            IsHitTestVisible = false;
            SetValue(YProperty, y);
            if (Ui.Animations) BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        }

        public void GoTo(double y)
        {
            if (!Ui.Animations)
            {
                SetValue(YProperty, y);
                return;
            }
            BeginAnimation(YProperty, new DoubleAnimation(y, TimeSpan.FromMilliseconds(110)) { EasingFunction = new QuadraticEase() });
        }

        protected override void OnRender(DrawingContext dc)
        {
            var brush = (Brush)Application.Current.Resources["AccentBrush"];
            double y = (double)GetValue(YProperty), w = ((FrameworkElement)AdornedElement).ActualWidth;
            dc.DrawRoundedRectangle(brush, null, new Rect(10, y - 1.5, Math.Max(0, w - 24), 3), 1.5, 1.5);
            dc.DrawEllipse(Brushes.Transparent, new Pen(brush, 2.5), new Point(8, y), 4.5, 4.5);
        }
    }
}
