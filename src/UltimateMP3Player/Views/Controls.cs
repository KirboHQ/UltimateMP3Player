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

// A small ring filling up as a download goes on (a song being saved on the device): a faint track and the part done in
// the accent colour. Waiting (Indeterminate): a quarter of it, still.
public sealed class ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsIndeterminateProperty = DependencyProperty.Register(nameof(IsIndeterminate), typeof(bool), typeof(ProgressRing),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(ProgressRing),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(ProgressRing),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(ProgressRing),
        new FrameworkPropertyMetadata(2.2, FrameworkPropertyMetadataOptions.AffectsRender));

    // 0-100.
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool IsIndeterminate { get => (bool)GetValue(IsIndeterminateProperty); set => SetValue(IsIndeterminateProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight), t = Thickness;
        if (size <= t * 2) return;
        double r = (size - t) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(Track, t), c, r, r);
        double v = IsIndeterminate ? 0.25 : Math.Clamp(Value / 100, 0, 1);
        if (v <= 0.005) return;
        var pen = new Pen(Stroke, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (v >= 0.999)
        {
            dc.DrawEllipse(null, pen, c, r, r);
            return;
        }
        double a = v * 2 * Math.PI;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X, c.Y - r), false, false);
            ctx.ArcTo(new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a)), new Size(r, r), 0, a > Math.PI, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

// The page area. A new page slides in from the side its place says (the DataContext's IPageOrder: the sidebar's entries top to
// bottom, or a page's tabs left to right, Horizontal) while the old one slides out the other way, a gap between them. The pages
// on the move sit on a strip and the view glides along it (Core.Glide): another page picked halfway goes after the last one
// still seen on its side, the one it was leaving just turns round, and the motion carries on from where it is at the speed it
// has (never cut and started again). A page with no place fades in. A page keeps its view (no new copy) while it's on the strip;
// while they move they're cached as pictures (only moved, not drawn again).
// With KeepViews the last pages left stay built (going back to one doesn't make its view again: the DJ's took several frames).
public sealed class AnimatedContent : Grid
{
    public static readonly DependencyProperty ContentProperty = DependencyProperty.Register(nameof(Content), typeof(object), typeof(AnimatedContent),
        new PropertyMetadata(null, (d, e) => ((AnimatedContent)d).Swap(e.OldValue, e.NewValue)));

    public static readonly DependencyProperty HorizontalProperty = DependencyProperty.Register(nameof(Horizontal), typeof(bool), typeof(AnimatedContent));
    public static readonly DependencyProperty KeepViewsProperty = DependencyProperty.Register(nameof(KeepViews), typeof(bool), typeof(AnimatedContent));

    public object? Content { get => GetValue(ContentProperty); set => SetValue(ContentProperty, value); }
    public bool Horizontal { get => (bool)GetValue(HorizontalProperty); set => SetValue(HorizontalProperty, value); }
    public bool KeepViews { get => (bool)GetValue(KeepViewsProperty); set => SetValue(KeepViewsProperty, value); }

    // The selections that move with the page (SlidingPill) take the same time: they start and end together.
    public const double Ms = 210;
    private const double Gap = 56;

    // The pages on the strip and their place on it (in page lengths); _shown is the one there, or on its way.
    private readonly List<(ContentPresenter View, int Slot)> _strip = new();
    private ContentPresenter _shown = new();
    private readonly Core.Glide _at = new();
    private bool _ticking, _fading;
    // The pages left, newest last (KeepViews), at most Kept.
    private readonly List<ContentPresenter> _kept = new();
    private const int Kept = 6;

    public AnimatedContent()
    {
        Focusable = false;
        ClipToBounds = true;
        Children.Add(_shown);
        _strip.Add((_shown, 0));
        // (another profile's pages: the ones kept are of no use)
        DataContextChanged += (_, _) => Forget();
    }

    // A page's place when a view makes the pages itself (the settings' groups, the overlay editor's tabs): null = none, it fades in.
    public Func<object, int?>? PlaceOf { get; set; }

    private int? Order(object? page) => page == null ? null : PlaceOf != null ? PlaceOf(page) : DataContext is ViewModels.IPageOrder o ? o.OrderOf(page) : null;

    private void Swap(object? old, object? now)
    {
        if (_fading) Settle();
        if (old == null || now == null || !Ui.Animations || !IsLoaded || ActualWidth < 1 || ActualHeight < 1)
        {
            Settle();
            var left = _shown;
            _shown = ViewOf(now);
            Children.Add(_shown);
            _strip[0] = (_shown, 0);
            Discard(left);
            return;
        }
        int? from = Order(old), to = Order(now);
        // (the keys don't stay in a box of the page going away: Ctrl+arrows work at once)
        if (_shown.IsKeyboardFocusWithin) Window.GetWindow(this)?.Focus();
        if (_strip.FirstOrDefault(p => p.View.Content == now).View is { } back) _shown = back;
        else if (from is { } f && to is { } t && f != t)
        {
            int dir = Math.Sign(t - f);
            double at = _at.Value;
            // (the ones out of sight go: the new one comes after the last one still seen on its side)
            foreach (var p in _strip.Where(p => p.View != _shown && Math.Abs(p.Slot - at) >= 1).ToList())
            {
                Discard(p.View);
                _strip.Remove(p);
            }
            int slot = (dir > 0 ? _strip.Max(p => p.Slot) : _strip.Min(p => p.Slot)) + dir;
            _shown = ViewOf(now);
            Children.Add(_shown);
            _strip.Add((_shown, slot));
        }
        else
        {
            FadeIn(now);
            return;
        }
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        foreach (var (view, _) in _strip)
        {
            view.IsHitTestVisible = view == _shown;
            view.CacheMode ??= new BitmapCache(scale) { SnapsToDevicePixels = true };
        }
        _at.To(_strip.First(p => p.View == _shown).Slot, Ms);
        // (in place before the first frame: the new page never shows over the old one)
        Place();
        if (_ticking) return;
        _ticking = true;
        Sliding++;
        CompositionTarget.Rendering += OnFrame;
    }

    // Pages on their way, each a cached picture: what changes in one at every frame (the bars of the music) waits, or the
    // whole page would be drawn again at each of them.
    public static int Sliding { get; private set; }

    private void OnFrame(object? sender, EventArgs e)
    {
        Place();
        if (!_at.Moving) Settle();
    }

    private void Place()
    {
        double step = (Horizontal ? ActualWidth : ActualHeight) + Gap, at = _at.Value;
        foreach (var (view, slot) in _strip)
        {
            if (view.RenderTransform is not TranslateTransform { IsFrozen: false } shift) view.RenderTransform = shift = new TranslateTransform();
            if (Horizontal) shift.X = (slot - at) * step;
            else shift.Y = (slot - at) * step;
        }
    }

    // A page with no place: what's there fades out where it is, the new one fades in with a short rise.
    private void FadeIn(object now)
    {
        StopTicking();
        foreach (var (view, _) in _strip)
        {
            view.IsHitTestVisible = false;
            view.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Ui.Time(70)));
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var shift = new TranslateTransform();
        var view2 = _shown = ViewOf(now);
        view2.RenderTransform = shift;
        Children.Add(view2);
        _strip.Clear();
        _strip.Add((view2, 0));
        _at.Set(0);
        _fading = true;
        var fade = new DoubleAnimation(0, 1, Ui.Time(120)) { EasingFunction = ease };
        fade.Completed += (_, _) => { if (_fading && _shown == view2) Settle(); };
        view2.BeginAnimation(OpacityProperty, fade);
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, Ui.Time(120)) { EasingFunction = ease });
    }

    private void StopTicking()
    {
        if (!_ticking) return;
        _ticking = false;
        Sliding--;
        CompositionTarget.Rendering -= OnFrame;
    }

    // A page's view: the one kept for it, or a new one.
    private ContentPresenter ViewOf(object? page)
    {
        var view = _kept.FirstOrDefault(v => v.Content == page);
        if (view == null) return new ContentPresenter { Content = page };
        _kept.Remove(view);
        return view;
    }

    private void Discard(ContentPresenter view)
    {
        Children.Remove(view);
        view.BeginAnimation(OpacityProperty, null);
        view.RenderTransform = Transform.Identity;
        view.CacheMode = null;
        if (!KeepViews || view.Content == null)
        {
            view.Content = null;
            return;
        }
        _kept.Remove(view);
        _kept.Add(view);
        if (_kept.Count > Kept) Forget(_kept[0]);
    }

    private void Forget(ContentPresenter? only = null)
    {
        foreach (var view in _kept.Where(v => only == null || v == only).ToList())
        {
            view.Content = null;
            _kept.Remove(view);
        }
    }

    // Arrived (or cut short by a page with no place): the page shown alone, the others let go.
    private void Settle()
    {
        StopTicking();
        _fading = false;
        foreach (var view in Children.OfType<ContentPresenter>().Where(v => v != _shown).ToList()) Discard(view);
        _strip.Clear();
        _strip.Add((_shown, 0));
        _at.Set(0);
        _shown.BeginAnimation(OpacityProperty, null);
        _shown.RenderTransform = Transform.Identity;
        _shown.CacheMode = null;
        _shown.IsHitTestVisible = true;
    }
}

// The selection under a row (or column) of RadioButtons (anywhere inside Target: a panel of them, an ItemsControl) that glides to
// the one checked with the page it opens (AnimatedContent's time, Core.Glide: a click halfway carries the motion on), and fades
// away when none is. Put it in the same cell as the buttons, behind them.
public sealed class SlidingPill : FrameworkElement
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(nameof(Target), typeof(FrameworkElement), typeof(SlidingPill),
        new PropertyMetadata(null, (d, e) => ((SlidingPill)d).Attach(e.OldValue as FrameworkElement, e.NewValue as FrameworkElement)));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(SlidingPill),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(nameof(Radius), typeof(double), typeof(SlidingPill),
        new FrameworkPropertyMetadata(999.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public FrameworkElement? Target { get => (FrameworkElement?)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    private readonly Core.Glide _x = new(), _y = new(), _w = new(), _h = new(), _show = new();
    private bool _ticking;

    public SlidingPill()
    {
        IsHitTestVisible = false;
        // (placed at once, without gliding, when it first shows or the buttons move)
        Loaded += (_, _) => Dispatcher.BeginInvoke(() => Place(false), System.Windows.Threading.DispatcherPriority.Loaded);
        SizeChanged += (_, _) => Place(false);
    }

    private void Attach(FrameworkElement? old, FrameworkElement? now)
    {
        old?.RemoveHandler(ToggleButton.CheckedEvent, (RoutedEventHandler)OnChecked);
        old?.RemoveHandler(ToggleButton.UncheckedEvent, (RoutedEventHandler)OnChecked);
        now?.AddHandler(ToggleButton.CheckedEvent, (RoutedEventHandler)OnChecked);
        now?.AddHandler(ToggleButton.UncheckedEvent, (RoutedEventHandler)OnChecked);
    }

    // (after the binding has unchecked the old one too)
    private void OnChecked(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(() => Place(IsLoaded && Ui.Animations), System.Windows.Threading.DispatcherPriority.Input);

    private static IEnumerable<RadioButton> Buttons(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is RadioButton r) yield return r;
            else foreach (var inner in Buttons(child)) yield return inner;
        }
    }

    private void Place(bool glide, bool again = false)
    {
        if (Target == null || !IsLoaded) return;
        // (its own Visibility: IsVisible catches up only after the layout, a strip just shown would have none)
        var on = Buttons(Target).FirstOrDefault(r => r.IsChecked == true && r.Visibility == Visibility.Visible);
        // (the row just shown, its buttons not laid out yet: once more after the layout)
        if (on != null && on.RenderSize.Width < 1)
        {
            if (!again) Dispatcher.BeginInvoke(() => Place(glide, true), System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }
        if (on != null)
        {
            var at = on.TransformToVisual(this).TransformBounds(new Rect(on.RenderSize));
            // (coming back from nothing: it appears where it is, it doesn't fly in from the last place)
            bool move = glide && _show.Value > 0.01;
            Go(_x, at.X, move);
            Go(_y, at.Y, move);
            Go(_w, at.Width, move);
            Go(_h, at.Height, move);
        }
        Go(_show, on == null ? 0 : 1, glide);
        InvalidateVisual();
        if (!glide || _ticking) return;
        _ticking = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private static void Go(Core.Glide g, double to, bool glide)
    {
        if (glide) g.To(to, AnimatedContent.Ms);
        else g.Set(to);
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        InvalidateVisual();
        if (_x.Moving || _y.Moving || _w.Moving || _h.Moving || _show.Moving) return;
        _ticking = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double show = _show.Value, w = _w.Value, h = _h.Value;
        if (Fill == null || show <= 0.001 || w < 1 || h < 1) return;
        double r = Math.Min(Radius, h / 2);
        dc.PushOpacity(show);
        dc.DrawRoundedRectangle(Fill, null, new Rect(_x.Value, _y.Value, w, h), r, r);
        dc.Pop();
    }
}

// A list of songs that comes a piece at a time (PagedRows): the next piece two screens before its end, all of it before Ctrl+A.
public static class RowPaging
{
    public static readonly DependencyProperty OnProperty = DependencyProperty.RegisterAttached("On", typeof(bool), typeof(RowPaging), new PropertyMetadata(false, OnChanged));

    public static bool GetOn(DependencyObject o) => (bool)o.GetValue(OnProperty);
    public static void SetOn(DependencyObject o, bool value) => o.SetValue(OnProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox list || e.NewValue is not true) return;
        list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, a) => Near(list, a)));
        list.PreviewKeyDown += (_, a) =>
        {
            if (a.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) LoadAll(list);
        };
    }

    public static void LoadAll(ItemsControl list) => (list.ItemsSource as ViewModels.PagedRows)?.LoadAll();

    private static void Near(ListBox list, ScrollChangedEventArgs a)
    {
        if (list.ItemsSource is not ViewModels.PagedRows { HasMore: true, Waiting: false } rows || a.VerticalOffset + a.ViewportHeight * 3 < a.ExtentHeight) return;
        rows.Waiting = true;
        // (not inside the layout that raised this; the list grows, this comes again, until it's far enough from its end)
        list.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            rows.Waiting = false;
            rows.More();
        });
    }
}

// A row of cards under a title (Home): as many as fit, widened to fill the width, the others to the side: the row scrolls by its arrows,
// its bar, Shift + wheel, the sideways wheel. Cards are made as they near, but the row is as long as all of them: the bar doesn't jump. Its
// content is the ItemsControl or ListBox of the cards, with CardRowTemplate as its panel: the shelf gives it its items. MoreCommand adds a
// button going elsewhere (Recently added: the library, thousands of songs wouldn't fit in a row).
public sealed class Shelf : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(Shelf));
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(nameof(Items), typeof(System.Collections.IEnumerable), typeof(Shelf),
        new PropertyMetadata(null, (d, e) => ((Shelf)d).ItemsChanged(e.OldValue, e.NewValue)));
    public static readonly DependencyProperty MoreLabelProperty = DependencyProperty.Register(nameof(MoreLabel), typeof(string), typeof(Shelf),
        new PropertyMetadata(null, (d, _) => ((Shelf)d).Sync()));
    public static readonly DependencyProperty MoreCommandProperty = DependencyProperty.Register(nameof(MoreCommand), typeof(ICommand), typeof(Shelf));

    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public System.Collections.IEnumerable? Items { get => (System.Collections.IEnumerable?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public string? MoreLabel { get => (string?)GetValue(MoreLabelProperty); set => SetValue(MoreLabelProperty, value); }
    public ICommand? MoreCommand { get => (ICommand?)GetValue(MoreCommandProperty); set => SetValue(MoreCommandProperty, value); }

    private const double MinItemWidth = 176;
    public const double Gap = 12;

    // How wide a card is now (CardRow reads it).
    public double ItemWidth { get; private set; } = MinItemWidth;
    public CardRow? Row { get; set; }
    public int Total { get; private set; }

    private readonly System.Collections.ObjectModel.ObservableCollection<object> _shown = new();
    private readonly Core.Glide _x = new();
    private ScrollViewer? _scroller;
    private Button? _more, _back, _next;
    private int _columns, _want;
    private bool _gliding;

    public Shelf()
    {
        Loaded += (_, _) =>
        {
            if (Items is System.Collections.Specialized.INotifyCollectionChanged live) live.CollectionChanged += ItemsMoved;
            Sync();
        };
        Unloaded += (_, _) =>
        {
            if (Items is System.Collections.Specialized.INotifyCollectionChanged live) live.CollectionChanged -= ItemsMoved;
            if (!_gliding) return;
            _gliding = false;
            CompositionTarget.Rendering -= Glide;
        };
        PreviewMouseWheel += OnWheel;
    }

    private void ItemsMoved(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Sync();

    private void ItemsChanged(object? old, object? now)
    {
        if (!IsLoaded)
        {
            Sync();
            return;
        }
        if (old is System.Collections.Specialized.INotifyCollectionChanged was) was.CollectionChanged -= ItemsMoved;
        if (now is System.Collections.Specialized.INotifyCollectionChanged live) live.CollectionChanged += ItemsMoved;
        Sync();
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (newContent is ItemsControl list) list.ItemsSource = _shown;
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _scroller = GetTemplateChild("PART_Scroller") as ScrollViewer;
        _more = GetTemplateChild("PART_More") as Button;
        _back = GetTemplateChild("PART_Back") as Button;
        _next = GetTemplateChild("PART_Next") as Button;
        if (_more != null) _more.Click += (_, _) => MoreCommand?.Execute(null);
        if (_back != null) _back.Click += (_, _) => Page(-1);
        if (_next != null) _next.Click += (_, _) => Page(1);
        if (_scroller == null) return;
        _scroller.SizeChanged += (_, e) => { if (e.WidthChanged) Fit(e.NewSize.Width); };
        _scroller.ScrollChanged += (_, _) => Moved();
    }

    private void Fit(double width)
    {
        int columns = Math.Max(1, (int)((width + Gap) / (MinItemWidth + Gap)));
        double item = Math.Max(0, (width - Gap * (columns - 1)) / columns);
        if (columns == _columns && item == ItemWidth) return;
        _columns = columns;
        ItemWidth = item;
        Row?.InvalidateMeasure();
        Sync();
    }

    // Keeps the cards that stay where they are: a song just played, a resized window change only what they must.
    private void Sync()
    {
        if (_columns == 0) return;
        var all = Items?.Cast<object>().ToList() ?? new List<object>();
        int want = Math.Min(all.Count, Math.Max(_columns, _want));
        for (int i = 0; i < want; i++)
        {
            if (i == _shown.Count) _shown.Add(all[i]);
            else if (!Equals(_shown[i], all[i])) _shown[i] = all[i];
        }
        while (_shown.Count > want) _shown.RemoveAt(_shown.Count - 1);
        if (Total != all.Count)
        {
            Total = all.Count;
            Row?.InvalidateMeasure();
        }
        bool more = all.Count > _columns;
        if (_more != null)
        {
            _more.Visibility = MoreCommand != null && all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _more.Content = MoreLabel;
        }
        if (_back != null) _back.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
        if (_next != null) _next.Visibility = more ? Visibility.Visible : Visibility.Collapsed;
        if (_scroller == null) return;
        // (the bar's height comes out of the gap under the row: what's below stays where it is)
        _scroller.HorizontalScrollBarVisibility = more ? ScrollBarVisibility.Visible : ScrollBarVisibility.Hidden;
        _scroller.Margin = new Thickness(0, 0, 8, more ? 16 : 26);
    }

    // The arrows stop at the ends. The next page of cards before it's in sight; the bar dragged to the end: all of them. When idle: the
    // first page is drawn first, a glide isn't stopped.
    private void Moved()
    {
        if (_scroller == null || _columns == 0) return;
        if (_back != null) _back.IsEnabled = _scroller.HorizontalOffset > 0.5;
        if (_next != null) _next.IsEnabled = _scroller.HorizontalOffset < _scroller.ScrollableWidth - 0.5;
        int need = (int)Math.Ceiling((_scroller.HorizontalOffset + _scroller.ViewportWidth * 2) / (ItemWidth + Gap) / _columns) * _columns;
        if (need <= _want || _shown.Count >= Total) return;
        _want = need;
        Dispatcher.BeginInvoke(Sync, System.Windows.Threading.DispatcherPriority.Background);
    }

    // (whole cards: the next page's first stands where this one's was)
    private void Page(int way) => GlideBy(way * _columns * (ItemWidth + Gap));

    public void SideWheel(int delta) => GlideBy(delta / 120.0 * 100);

    private void GlideBy(double by)
    {
        if (_scroller == null) return;
        double from = _gliding ? _x.Target : _scroller.HorizontalOffset;
        if (!_gliding) _x.Set(_scroller.HorizontalOffset);
        _x.To(Math.Clamp(from + by, 0, _scroller.ScrollableWidth), AnimatedContent.Ms);
        if (_gliding) return;
        _gliding = true;
        CompositionTarget.Rendering += Glide;
    }

    private void Glide(object? sender, EventArgs e)
    {
        _scroller?.ScrollToHorizontalOffset(_x.Value);
        if (_x.Moving) return;
        _gliding = false;
        CompositionTarget.Rendering -= Glide;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            GlideBy(-e.Delta / 120.0 * 100);
            return;
        }
        // (its scroller would take the wheel and do nothing with it: up and down belong to the page)
        RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = this });
    }
}

// The cards of a Shelf side by side, each as wide as the shelf says.
public sealed class CardRow : Panel
{
    private double _height, _item;

    protected override Size MeasureOverride(Size available)
    {
        var shelf = Ui.FindAncestor<Shelf>(this);
        if (shelf != null) shelf.Row = this;
        _item = shelf?.ItemWidth ?? 176;
        _height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(_item, double.PositiveInfinity));
            _height = Math.Max(_height, child.DesiredSize.Height);
        }
        // (all the cards count, made or not)
        int n = Math.Max(InternalChildren.Count, shelf?.Total ?? 0);
        return InternalChildren.Count == 0 ? new Size() : new Size(n * _item + (n - 1) * Shelf.Gap, _height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new Rect(x, 0, _item, _height));
            x += _item + Shelf.Gap;
        }
        return final;
    }
}

// Cards in equal columns that fill the width: as many as fit at MinItemWidth, then widened (no ragged gap on the right).
public sealed class CardGrid : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(nameof(MinItemWidth), typeof(double),
        typeof(CardGrid), new FrameworkPropertyMetadata(176.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double),
        typeof(CardGrid), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    // 0 = all rows; otherwise the cards that don't fit are left out.
    public static readonly DependencyProperty MaxRowsProperty = DependencyProperty.Register(nameof(MaxRows), typeof(int),
        typeof(CardGrid), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth { get => (double)GetValue(MinItemWidthProperty); set => SetValue(MinItemWidthProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public int MaxRows { get => (int)GetValue(MaxRowsProperty); set => SetValue(MaxRowsProperty, value); }

    private double _rowHeight;

    private int Limit(int cols) => MaxRows > 0 ? MaxRows * cols : int.MaxValue;

    private (int Cols, double ItemWidth) Columns(double width)
    {
        if (double.IsInfinity(width)) return (Math.Max(1, InternalChildren.Count), MinItemWidth);
        int cols = Math.Max(1, (int)((width + Gap) / (MinItemWidth + Gap)));
        return (cols, Math.Max(0, (width - Gap * (cols - 1)) / cols));
    }

    // Every row as tall as the tallest card, so they all line up.
    protected override Size MeasureOverride(Size available)
    {
        var (cols, itemW) = Columns(available.Width);
        _rowHeight = 0;
        int count = 0, limit = Limit(cols);
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(itemW, double.PositiveInfinity));
            if (child.Visibility == Visibility.Collapsed || count == limit) continue;
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
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
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
        for (int i = 1; i < InternalChildren.Count; i++) w += InternalChildren[i].DesiredSize.Width;
        return w;
    }

    protected override Size MeasureOverride(Size available)
    {
        if (InternalChildren.Count == 0) return new Size();
        double height = 0;
        for (int i = 1; i < InternalChildren.Count; i++)
        {
            InternalChildren[i].Measure(new Size(double.PositiveInfinity, available.Height));
            height = Math.Max(height, InternalChildren[i].DesiredSize.Height);
        }
        double others = Others();
        var first = InternalChildren[0];
        first.Measure(new Size(Math.Max(0, available.Width - others), available.Height));
        return new Size(first.DesiredSize.Width + others, Math.Max(height, first.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (InternalChildren.Count == 0) return final;
        double others = Others();
        var first = InternalChildren[0];
        double x = Math.Min(first.DesiredSize.Width, Math.Max(0, final.Width - others));
        first.Arrange(new Rect(0, 0, x, final.Height));
        for (int i = 1; i < InternalChildren.Count; i++)
        {
            var c = InternalChildren[i];
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
    public static readonly DependencyProperty ColumnWidthProperty = DependencyProperty.Register(nameof(ColumnWidth), typeof(double),
        typeof(MasonryPanel), new FrameworkPropertyMetadata(560.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(nameof(MaxColumns), typeof(int),
        typeof(MasonryPanel), new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double),
        typeof(MasonryPanel), new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnWidthProperty = DependencyProperty.Register(nameof(MaxColumnWidth), typeof(double),
        typeof(MasonryPanel), new FrameworkPropertyMetadata(double.PositiveInfinity, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double ColumnWidth { get => (double)GetValue(ColumnWidthProperty); set => SetValue(ColumnWidthProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public double MaxColumnWidth { get => (double)GetValue(MaxColumnWidthProperty); set => SetValue(MaxColumnWidthProperty, value); }

    private readonly List<(int Col, double Y)> _slots = new();

    // Never more columns than cards shown: a card alone keeps to the left, as wide as it's allowed.
    private int Columns(double width)
        => double.IsInfinity(width) ? 1 : Math.Clamp((int)((width + Gap) / (ColumnWidth + Gap)), 1, Math.Max(1, Math.Min(MaxColumns, InternalChildren.Cast<UIElement>().Count(c => c.Visibility != Visibility.Collapsed))));

    private double WidthOf(double width, int cols) => Math.Min(MaxColumnWidth, (width - Gap * (cols - 1)) / cols);

    protected override Size MeasureOverride(Size available)
    {
        int cols = Columns(available.Width);
        double colW = double.IsInfinity(available.Width) ? ColumnWidth : WidthOf(available.Width, cols);
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
        double colW = WidthOf(final.Width, cols);
        for (int i = 0; i < InternalChildren.Count && i < _slots.Count; i++)
        {
            var (c, y) = _slots[i];
            InternalChildren[i].Arrange(new Rect(c * (colW + Gap), y, colW, InternalChildren[i].DesiredSize.Height));
        }
        return final;
    }
}

// What opens and closes inside a page (a part of the sidebar, the rows under a switch), bound in place of its Visibility: it unfolds in
// the time a page takes to slide and what's under it moves with it. It's laid out once at its own height: a bottom margin takes away
// what's still shut and a clip hides it, so at each frame only its neighbours move.
public static class Fold
{
    public static readonly DependencyProperty OpenProperty = DependencyProperty.RegisterAttached("Open", typeof(bool), typeof(Fold),
        new PropertyMetadata(true, (d, e) => Go((FrameworkElement)d, (bool)e.NewValue)));

    public static bool GetOpen(DependencyObject o) => (bool)o.GetValue(OpenProperty);
    public static void SetOpen(DependencyObject o, bool value) => o.SetValue(OpenProperty, value);

    private sealed record Run(Thickness Margin, double Height, RectangleGeometry Clip);

    private static readonly DependencyProperty RunProperty = DependencyProperty.RegisterAttached("Run", typeof(Run), typeof(Fold));

    // How open it is, 0 to 1.
    private static readonly DependencyProperty AtProperty = DependencyProperty.RegisterAttached("At", typeof(double), typeof(Fold),
        new PropertyMetadata(1.0, (d, e) => Place((FrameworkElement)d, (double)e.NewValue)));

    private static void Go(FrameworkElement el, bool open)
    {
        // (the first value of its binding, or a page not on the screen: nothing to see)
        if (!Ui.Animations || !el.IsLoaded || VisualTreeHelper.GetParent(el) is not UIElement { IsVisible: true })
        {
            Land(el, open);
            return;
        }
        if (el.GetValue(RunProperty) == null)
        {
            if (el.Visibility == Visibility.Visible == open) return;
            el.Visibility = Visibility.Visible;
            el.UpdateLayout();
            if (el.ActualHeight <= 0)
            {
                Land(el, open);
                return;
            }
            var clip = new RectangleGeometry();
            el.SetValue(RunProperty, new Run(el.Margin, el.ActualHeight, clip));
            el.Clip = clip;
            el.SetValue(AtProperty, open ? 0.0 : 1.0);
            Place(el, open ? 0 : 1);
        }
        var fold = new DoubleAnimation(open ? 1 : 0, Ui.Time(AnimatedContent.Ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        fold.Completed += (_, _) => { if (GetOpen(el) == open) Land(el, open); };
        el.BeginAnimation(AtProperty, fold);
    }

    private static void Place(FrameworkElement el, double at)
    {
        if (el.GetValue(RunProperty) is not Run run) return;
        double whole = run.Height + run.Margin.Top + run.Margin.Bottom;
        el.Margin = new Thickness(run.Margin.Left, run.Margin.Top, run.Margin.Right, run.Margin.Bottom - whole * (1 - at));
        // (a little beyond its sides and top: a shadow or a focus ring isn't cut)
        run.Clip.Rect = new Rect(-40, -40, el.ActualWidth + 80, 40 + Math.Max(0, whole * at - run.Margin.Top));
        el.Opacity = at;
    }

    private static void Land(FrameworkElement el, bool open)
    {
        if (el.GetValue(RunProperty) is Run run)
        {
            el.ClearValue(RunProperty);
            el.BeginAnimation(AtProperty, null);
            el.Margin = run.Margin;
            el.Clip = null;
            el.ClearValue(UIElement.OpacityProperty);
        }
        el.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }
}

// Panes of one cell with one shown (a room and its lobby, the DJ's two modes), bound in place of their Visibility: the new one comes in
// from its side while the old one goes out on its own, as pages do. Unlike AnimatedContent's pages they stay built, in the tree.
public static class Pane
{
    public static readonly DependencyProperty ShownProperty = DependencyProperty.RegisterAttached("Shown", typeof(bool), typeof(Pane),
        new PropertyMetadata(true, (d, e) => Go((FrameworkElement)d, (bool)e.NewValue)));

    // The side it comes from and goes to: -1 left, 1 right.
    public static readonly DependencyProperty SideProperty = DependencyProperty.RegisterAttached("Side", typeof(int), typeof(Pane), new PropertyMetadata(1));

    public static bool GetShown(DependencyObject o) => (bool)o.GetValue(ShownProperty);
    public static void SetShown(DependencyObject o, bool value) => o.SetValue(ShownProperty, value);
    public static int GetSide(DependencyObject o) => (int)o.GetValue(SideProperty);
    public static void SetSide(DependencyObject o, int value) => o.SetValue(SideProperty, value);

    private static void Go(FrameworkElement el, bool shown)
    {
        double step = (VisualTreeHelper.GetParent(el) as FrameworkElement)?.ActualWidth + 56 ?? 0;
        if (!Ui.Animations || !el.IsLoaded || step <= 56 || VisualTreeHelper.GetParent(el) is not UIElement { IsVisible: true })
        {
            if (el.RenderTransform is TranslateTransform { IsFrozen: false } moving) moving.BeginAnimation(TranslateTransform.XProperty, null);
            el.RenderTransform = Transform.Identity;
            el.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        if (el.RenderTransform is not TranslateTransform { IsFrozen: false } shift) el.RenderTransform = shift = new TranslateTransform();
        double aside = GetSide(el) * step;
        if (shown && el.Visibility != Visibility.Visible)
        {
            shift.X = aside;
            el.Visibility = Visibility.Visible;
        }
        var move = new DoubleAnimation(shown ? 0 : aside, Ui.Time(AnimatedContent.Ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        move.Completed += (_, _) =>
        {
            if (GetShown(el) != shown) return;
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.X = 0;
            if (!shown) el.Visibility = Visibility.Collapsed;
        };
        shift.BeginAnimation(TranslateTransform.XProperty, move);
    }
}
