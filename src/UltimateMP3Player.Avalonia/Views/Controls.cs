using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;

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
    {
        Control.LoadedEvent.AddClassHandler<Avalonia.Controls.Shapes.Ellipse>((e, _) =>
        {
            if (e.Classes.Contains("spinner")) Spin(e);
            else if (e.Classes.Contains("pulse")) Pulse(e);
        });
        Control.LoadedEvent.AddClassHandler<Border>((e, _) => { if (e.Classes.Contains("runner")) Run(e); });
    }

    // 40 steps a second (a spinner doesn't need the 120 of a phone's screen).
    private const double StepMs = 25;

    // While it's loaded: step(seconds since the start) at each of its frames, while it's shown; with stopAfter, still from then
    // on, until it's shown again.
    private static void Animate(Control e, Action<double> step, double stopAfter = double.PositiveInfinity)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var pacer = new FramePacer(StepMs);
        void Next()
        {
            if (!e.IsLoaded) return;
            if (!e.IsEffectivelyVisible || TopLevel.GetTopLevel(e) is not { } top)
            {
                started.Restart();
                Ui.Later(Next);
                return;
            }
            if (started.Elapsed.TotalSeconds >= stopAfter)
            {
                Ui.Later(Next);
                return;
            }
            pacer.Request(top, _ =>
            {
                step(started.Elapsed.TotalSeconds);
                Next();
            });
        }
        Next();
    }

    // The ring going out behind "searching" (the Windows app's Pulse style): 0.6 → 1.5 and fading, every 2.2 s, three times
    // each time the page is shown.
    private static void Pulse(Control e)
    {
        var scale = new ScaleTransform(0.6, 0.6);
        e.RenderTransform = scale;
        Animate(e, s =>
        {
            double t = s % 2.2 / 2.2;
            scale.ScaleX = scale.ScaleY = 0.6 + 0.9 * t;
            e.Opacity = s >= 6.5 ? 0 : 0.7 * (1 - t);
        }, 6.6);
    }

    private static void Spin(Control e)
    {
        var rotate = e.RenderTransform as RotateTransform ?? new RotateTransform();
        e.RenderTransform = rotate;
        Animate(e, s => rotate.Angle = s / 0.9 * 360 % 360);
    }

    // The block running along a progress bar that can't tell how far it is (ProgressBar's "Runner"): from just before its
    // left end to past its right one every 1.4 s, only while it's shown (it's hidden when the bar knows its value).
    private static void Run(Border e)
    {
        var shift = e.RenderTransform as TranslateTransform ?? new TranslateTransform();
        e.RenderTransform = shift;
        Animate(e, s =>
        {
            double w = (e.Parent as Control)?.Bounds.Width ?? 420;
            shift.X = -e.Bounds.Width + (w + e.Bounds.Width) * (s % 1.4 / 1.4);
        });
    }
}

// The Windows app makes a click on a slider's track jump there and keep dragging: Avalonia's sliders already do.
public static class SliderDrag
{
    public static void Register() { }
}

// The page area. A new page slides in from the side its place says (the DataContext's IPageOrder: the sidebar's entries top to
// bottom, or a page's tabs left to right, Horizontal) while the old one slides out the other way, a gap between them. The pages
// on the move sit on a strip and the view glides along it (Core.Glide): another page picked halfway goes after the last one
// still seen on its side, the one it was leaving just turns round, and the motion carries on from where it is at the speed it
// has (never cut and started again). A page with no place fades in. A page keeps its view (no new copy) while it's on the
// strip. With KeepViews the last pages left stay built (going back to one doesn't make its view again: the DJ's took several
// frames). Like the Windows app's.
public sealed class AnimatedContent : Panel
{
    public static readonly StyledProperty<object?> ContentProperty = AvaloniaProperty.Register<AnimatedContent, object?>(nameof(Content));
    public static readonly StyledProperty<bool> HorizontalProperty = AvaloniaProperty.Register<AnimatedContent, bool>(nameof(Horizontal));
    public static readonly StyledProperty<bool> KeepViewsProperty = AvaloniaProperty.Register<AnimatedContent, bool>(nameof(KeepViews));

    public object? Content { get => GetValue(ContentProperty); set => SetValue(ContentProperty, value); }
    public bool Horizontal { get => GetValue(HorizontalProperty); set => SetValue(HorizontalProperty, value); }
    public bool KeepViews { get => GetValue(KeepViewsProperty); set => SetValue(KeepViewsProperty, value); }

    // The selections that move with the page (SlidingPill) take the same time: they start and end together.
    public const double Ms = 210;
    private const double Gap = 56;

    // The pages on the strip and their place on it (in page lengths); _shown is the one there, or on its way.
    private readonly List<(ContentPresenter View, int Slot)> _strip = new();
    private ContentPresenter _shown = new();
    private readonly Core.Glide _at = new();
    private readonly Tweener _fade;
    private bool _ticking, _fading, _requested;
    // The pages left and their views, newest last (KeepViews), at most Kept.
    private readonly List<(object Page, Control View)> _kept = new();
    private const int Kept = 6;

    public AnimatedContent()
    {
        Focusable = false;
        ClipToBounds = true;
        Children.Add(_shown);
        _strip.Add((_shown, 0));
        _fade = new Tweener(this, Fade);
        // (another profile's pages: the ones kept are of no use)
        DataContextChanged += (_, _) => Forget();
    }

    // A page's place when a view makes the pages itself (the settings' groups, the overlay editor's tabs): null = none, it fades in.
    public Func<object, int?>? PlaceOf { get; set; }

    private int? Order(object? page) => page == null ? null : PlaceOf != null ? PlaceOf(page) : DataContext is ViewModels.IPageOrder o ? o.OrderOf(page) : null;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContentProperty) Swap(change.OldValue, change.NewValue);
    }

    private void Swap(object? old, object? now)
    {
        if (_fading) Settle();
        if (old == null || now == null || !Ui.Animations || !IsLoaded || Bounds.Width < 1 || Bounds.Height < 1)
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
        if (TopLevel.GetTopLevel(this)?.FocusManager is { } focus && focus.GetFocusedElement() is Visual focused && _shown.IsVisualAncestorOf(focused)) focus.ClearFocus();
        if (_strip.FirstOrDefault(p => PageOf(p.View) == now).View is { } back) _shown = back;
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
        foreach (var (view, _) in _strip) view.IsHitTestVisible = view == _shown;
        _at.To(_strip.First(p => p.View == _shown).Slot, Ms);
        // (in place before the first frame: the new page never shows over the old one)
        Place();
        if (_ticking) return;
        _ticking = true;
        Frame();
    }

    // (one frame asked at a time: a fade that stopped the slide and a slide again keep one loop)
    private void Frame()
    {
        if (_requested) return;
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            Settle();
            return;
        }
        _requested = true;
        top.RequestAnimationFrame(_ =>
        {
            _requested = false;
            if (!_ticking) return;
            Place();
            if (_at.Moving) Frame();
            else Settle();
        });
    }

    private void Place()
    {
        double step = (Horizontal ? Bounds.Width : Bounds.Height) + Gap, at = _at.Value;
        foreach (var (view, slot) in _strip)
        {
            if (view.RenderTransform is not TranslateTransform shift) view.RenderTransform = shift = new TranslateTransform();
            if (Horizontal) shift.X = (slot - at) * step;
            else shift.Y = (slot - at) * step;
        }
    }

    // A page with no place: what's there fades out where it is, the new one fades in with a short rise.
    private void FadeIn(object now)
    {
        _ticking = false;
        foreach (var (view, _) in _strip) view.IsHitTestVisible = false;
        _shown = ViewOf(now);
        _shown.RenderTransform = new TranslateTransform();
        _shown.Opacity = 0;
        Children.Add(_shown);
        _strip.Clear();
        _strip.Add((_shown, 0));
        _at.Set(0);
        _fading = true;
        _fade.To(1, 120, Ui.CubicOut, from: 0, completed: Settle);
    }

    private void Fade(double e)
    {
        if (!_fading) return;
        foreach (var view in Children.OfType<ContentPresenter>())
            if (view != _shown) view.Opacity = Math.Max(0, 1 - e * 1.7);
        _shown.Opacity = e;
        if (_shown.RenderTransform is TranslateTransform shift) shift.Y = 14 * (1 - e);
    }

    // A page's view: the one kept for it (in a new presenter: one put back in the tree makes its view again), or a new one.
    private ContentPresenter ViewOf(object? page)
    {
        int i = _kept.FindIndex(k => k.Page == page);
        if (i < 0) return new ContentPresenter { Content = page };
        var view = _kept[i].View;
        _kept.RemoveAt(i);
        return new ContentPresenter { Content = view, Tag = page };
    }

    // (a view kept says its page in the Tag; a page may be a control itself)
    private static object? PageOf(ContentPresenter view) => view.Tag ?? view.Content;

    private void Discard(ContentPresenter presenter)
    {
        Children.Remove(presenter);
        var page = PageOf(presenter);
        var view = presenter.Child;
        presenter.Content = null;
        presenter.UpdateChild();
        if (!KeepViews || page == null || view == null) return;
        view.DataContext = page;
        _kept.RemoveAll(k => k.Page == page);
        _kept.Add((page, view));
        if (_kept.Count > Kept) Forget(_kept[0].Page);
    }

    private void Forget(object? only = null)
    {
        foreach (var k in _kept.Where(k => only == null || k.Page == only).ToList())
        {
            k.View.DataContext = null;
            _kept.Remove(k);
        }
    }

    // Arrived (or cut short by a page with no place): the page shown alone, the others let go.
    private void Settle()
    {
        _ticking = false;
        _fading = false;
        _fade.Stop();
        foreach (var view in Children.OfType<ContentPresenter>().Where(v => v != _shown).ToList()) Discard(view);
        _strip.Clear();
        _strip.Add((_shown, 0));
        _at.Set(0);
        _shown.RenderTransform = null;
        _shown.Opacity = 1;
        _shown.IsHitTestVisible = true;
    }
}

// The pill behind the checked one of a row or column of radio buttons (anywhere inside Target: a panel of them, an ItemsControl):
// it glides from one to the next with the page (AnimatedContent's time, Core.Glide: a click halfway carries the motion on)
// instead of jumping. The buttons' own checked background is none.
public sealed class SlidingPill : Control
{
    public static readonly StyledProperty<Control?> TargetProperty = AvaloniaProperty.Register<SlidingPill, Control?>(nameof(Target));
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<SlidingPill, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<double> RadiusProperty = AvaloniaProperty.Register<SlidingPill, double>(nameof(Radius), 999);

    static SlidingPill() => AffectsRender<SlidingPill>(FillProperty, RadiusProperty);

    public Control? Target { get => GetValue(TargetProperty); set => SetValue(TargetProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public double Radius { get => GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    private readonly Core.Glide _x = new(), _y = new(), _w = new(), _h = new(), _show = new();
    private bool _ticking;

    public SlidingPill()
    {
        IsHitTestVisible = false;
        // (placed at once, without gliding, when it first shows or the buttons move)
        Loaded += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => Place(false), Avalonia.Threading.DispatcherPriority.Loaded);
        SizeChanged += (_, _) => Place(false);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TargetProperty) return;
        (change.OldValue as Control)?.RemoveHandler(Avalonia.Controls.Primitives.ToggleButton.IsCheckedChangedEvent, OnChecked);
        (change.NewValue as Control)?.AddHandler(Avalonia.Controls.Primitives.ToggleButton.IsCheckedChangedEvent, OnChecked);
    }

    // (after the binding has unchecked the old one too)
    private void OnChecked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Place(IsLoaded && Ui.Animations), Avalonia.Threading.DispatcherPriority.Input);

    private static IEnumerable<RadioButton> Buttons(Visual parent)
    {
        foreach (var child in parent.GetVisualChildren())
        {
            if (child is RadioButton r) yield return r;
            else foreach (var inner in Buttons(child)) yield return inner;
        }
    }

    private void Place(bool glide, bool again = false)
    {
        if (Target == null || !IsLoaded) return;
        var on = Buttons(Target).FirstOrDefault(r => r.IsChecked == true && r.IsVisible);
        // (the row just shown, its buttons not laid out yet: once more after the layout)
        if (on != null && on.Bounds.Width < 1)
        {
            if (!again) Avalonia.Threading.Dispatcher.UIThread.Post(() => Place(glide, true), Avalonia.Threading.DispatcherPriority.Loaded);
            return;
        }
        if (on != null && on.TranslatePoint(default, this) is { } at)
        {
            // (coming back from nothing: it appears where it is, it doesn't fly in from the last place)
            bool move = glide && _show.Value > 0.01;
            Go(_x, at.X, move);
            Go(_y, at.Y, move);
            Go(_w, on.Bounds.Width, move);
            Go(_h, on.Bounds.Height, move);
        }
        Go(_show, on == null ? 0 : 1, glide);
        InvalidateVisual();
        if (!glide || _ticking) return;
        _ticking = true;
        Frame();
    }

    private static void Go(Core.Glide g, double to, bool glide)
    {
        if (glide) g.To(to, AnimatedContent.Ms);
        else g.Set(to);
    }

    private void Frame()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            _ticking = false;
            return;
        }
        top.RequestAnimationFrame(_ =>
        {
            InvalidateVisual();
            if (_x.Moving || _y.Moving || _w.Moving || _h.Moving || _show.Moving) Frame();
            else _ticking = false;
        });
    }

    public override void Render(DrawingContext dc)
    {
        double show = _show.Value, w = _w.Value, h = _h.Value;
        if (Fill == null || show <= 0.001 || w < 1 || h < 1) return;
        double r = Math.Min(Radius, h / 2);
        using (dc.PushOpacity(show))
            dc.DrawRectangle(Fill, null, new RoundedRect(new Rect(_x.Value, _y.Value, w, h), r));
    }
}

// A list of songs that comes a piece at a time (PagedRows): the next piece two screens before its end, all of it before Ctrl+A.
public static class RowPaging
{
    public static readonly AttachedProperty<bool> OnProperty = AvaloniaProperty.RegisterAttached<ListBox, bool>("On", typeof(RowPaging));

    public static bool GetOn(ListBox o) => o.GetValue(OnProperty);
    public static void SetOn(ListBox o, bool value) => o.SetValue(OnProperty, value);

    static RowPaging()
    {
        OnProperty.Changed.AddClassHandler<ListBox>((list, e) =>
        {
            if (e.NewValue is not true) return;
            list.AddHandler(ScrollViewer.ScrollChangedEvent, (_, a) => Near(list, a.Source as ScrollViewer));
            list.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, (_, a) =>
            {
                if (a.Key == Avalonia.Input.Key.A && (a.KeyModifiers & (Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Meta)) != 0) LoadAll(list);
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        });
    }

    public static void LoadAll(ItemsControl list) => (list.ItemsSource as ViewModels.PagedRows)?.LoadAll();

    private static void Near(ListBox list, ScrollViewer? sv)
    {
        if (sv == null || list.ItemsSource is not ViewModels.PagedRows { HasMore: true, Waiting: false } rows || sv.Offset.Y + sv.Viewport.Height * 3 < sv.Extent.Height) return;
        rows.Waiting = true;
        // (not inside the layout that raised this; the list grows, this comes again, until it's far enough from its end)
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            rows.Waiting = false;
            rows.More();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }
}

// A row of cards under a title (Home): as many as fit, widened to fill the width, the others to the side: the row scrolls by its arrows,
// its bar, Shift + wheel, a touchpad (phones: the finger). Cards are made as they near, but the row is as long as all of them: the bar doesn't
// jump. Its content is the ItemsControl or ListBox of the cards, with CardRow as its panel: the shelf gives it its items. MoreCommand adds a
// button going elsewhere (Recently added: the library, thousands of songs wouldn't fit in a row). Phones: FixedCount cards as wide as they are.
public sealed class Shelf : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<Shelf, string?>(nameof(Title));
    public static readonly StyledProperty<System.Collections.IEnumerable?> ItemsProperty = AvaloniaProperty.Register<Shelf, System.Collections.IEnumerable?>(nameof(Items));
    public static readonly StyledProperty<string?> MoreLabelProperty = AvaloniaProperty.Register<Shelf, string?>(nameof(MoreLabel));
    public static readonly StyledProperty<System.Windows.Input.ICommand?> MoreCommandProperty = AvaloniaProperty.Register<Shelf, System.Windows.Input.ICommand?>(nameof(MoreCommand));
    public static readonly StyledProperty<int> FixedCountProperty = AvaloniaProperty.Register<Shelf, int>(nameof(FixedCount));

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public System.Collections.IEnumerable? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public string? MoreLabel { get => GetValue(MoreLabelProperty); set => SetValue(MoreLabelProperty, value); }
    public System.Windows.Input.ICommand? MoreCommand { get => GetValue(MoreCommandProperty); set => SetValue(MoreCommandProperty, value); }
    public int FixedCount { get => GetValue(FixedCountProperty); set => SetValue(FixedCountProperty, value); }

    private const double MinItemWidth = 176, CardGap = 12;

    // How wide a card is now (NaN: as wide as it is) and the gap between two (CardRow reads them).
    public double ItemWidth { get; private set; } = double.NaN;
    public double Gap => FixedCount > 0 ? 0 : CardGap;
    public CardRow? Row { get; set; }
    public int Total { get; private set; }

    private readonly System.Collections.ObjectModel.ObservableCollection<object> _shown = new();
    private readonly UltimateMP3Player.Core.Glide _x = new();
    private ScrollViewer? _scroller;
    private Button? _more, _back, _next;
    private int _columns, _want;
    private bool _gliding;

    public Shelf()
    {
        AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (e.KeyModifiers != Avalonia.Input.KeyModifiers.Shift) return;
            GlideBy(-e.Delta.Y * 100);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContentProperty && change.NewValue is ItemsControl list) list.ItemsSource = _shown;
        else if (change.Property == ItemsProperty)
        {
            if (change.OldValue is System.Collections.Specialized.INotifyCollectionChanged was) was.CollectionChanged -= ItemsMoved;
            if (change.NewValue is System.Collections.Specialized.INotifyCollectionChanged live && this.GetVisualRoot() != null) live.CollectionChanged += ItemsMoved;
            Sync();
        }
        else if (change.Property == MoreLabelProperty) Sync();
    }

    // Listening only while it's in the window: a page let go of isn't kept alive by the session's lists.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Items is System.Collections.Specialized.INotifyCollectionChanged live)
        {
            live.CollectionChanged -= ItemsMoved;
            live.CollectionChanged += ItemsMoved;
        }
        Sync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Items is System.Collections.Specialized.INotifyCollectionChanged live) live.CollectionChanged -= ItemsMoved;
    }

    private void ItemsMoved(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Sync();

    protected override void OnApplyTemplate(Avalonia.Controls.Primitives.TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _scroller = e.NameScope.Find<ScrollViewer>("PART_Scroller");
        _more = e.NameScope.Find<Button>("PART_More");
        _back = e.NameScope.Find<Button>("PART_Back");
        _next = e.NameScope.Find<Button>("PART_Next");
        if (_more != null) _more.Click += (_, _) => MoreCommand?.Execute(null);
        if (_back != null) _back.Click += (_, _) => Page(-1);
        if (_next != null) _next.Click += (_, _) => Page(1);
        if (FixedCount > 0) _columns = FixedCount;
        if (_scroller == null) return;
        _scroller.SizeChanged += (_, a) => Fit(a.NewSize.Width);
        _scroller.ScrollChanged += (_, _) => Moved();
        Sync();
    }

    private void Fit(double width)
    {
        if (FixedCount > 0 || width <= 0) return;
        int columns = Math.Max(1, (int)((width + CardGap) / (MinItemWidth + CardGap)));
        double item = Math.Max(0, (width - CardGap * (columns - 1)) / columns);
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
            _more.IsVisible = MoreCommand != null && all.Count > 0;
            _more.Content = MoreLabel;
        }
        if (_back != null) _back.IsVisible = more;
        if (_next != null) _next.IsVisible = more;
        if (_scroller == null || FixedCount > 0) return;
        // (the bar's height comes out of the gap under the row: what's below stays where it is)
        _scroller.HorizontalScrollBarVisibility = more ? Avalonia.Controls.Primitives.ScrollBarVisibility.Visible : Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;
        _scroller.Margin = new Thickness(0, 0, 8, more ? 16 : 26);
    }

    // The arrows stop at the ends. The next page of cards before it's in sight; the bar dragged to the end: all of them. When idle: the
    // first page is drawn first, a glide isn't stopped.
    private void Moved()
    {
        if (_scroller == null || _columns == 0 || _shown.Count == 0) return;
        double x = _scroller.Offset.X, view = _scroller.Viewport.Width;
        if (_back != null) _back.IsEnabled = x > 0.5;
        if (_next != null) _next.IsEnabled = x < _scroller.Extent.Width - view - 0.5;
        double card = double.IsNaN(ItemWidth) ? _scroller.Extent.Width / _shown.Count : ItemWidth + CardGap;
        int need = (int)Math.Ceiling((x + view * 2) / card / _columns) * _columns;
        if (need <= _want || _shown.Count >= Total) return;
        _want = need;
        Avalonia.Threading.Dispatcher.UIThread.Post(Sync, Avalonia.Threading.DispatcherPriority.Background);
    }

    // (whole cards: the next page's first stands where this one's was)
    private void Page(int way) => GlideBy(way * _columns * (ItemWidth + CardGap));

    private void GlideBy(double by)
    {
        if (_scroller == null) return;
        double from = _gliding ? _x.Target : _scroller.Offset.X;
        if (!_gliding) _x.Set(_scroller.Offset.X);
        _x.To(Math.Clamp(from + by, 0, Math.Max(0, _scroller.Extent.Width - _scroller.Viewport.Width)), AnimatedContent.Ms);
        if (_gliding) return;
        _gliding = true;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Frame);
    }

    private void Frame(TimeSpan time)
    {
        if (_scroller == null || this.GetVisualRoot() == null)
        {
            _gliding = false;
            return;
        }
        _scroller.Offset = new Vector(_x.Value, 0);
        if (_x.Moving) TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Frame);
        else _gliding = false;
    }
}

// The cards of a Shelf side by side, each as wide as the shelf says.
public sealed class CardRow : Panel
{
    private double _height, _item, _gap;

    protected override Size MeasureOverride(Size available)
    {
        var shelf = this.FindAncestorOfType<Shelf>();
        if (shelf != null) shelf.Row = this;
        _item = shelf?.ItemWidth ?? double.NaN;
        _gap = shelf?.Gap ?? 0;
        _height = 0;
        double width = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.IsNaN(_item) ? double.PositiveInfinity : _item, double.PositiveInfinity));
            _height = Math.Max(_height, child.DesiredSize.Height);
            width += (double.IsNaN(_item) ? child.DesiredSize.Width : _item) + _gap;
        }
        // (all the cards count, made or not)
        if (!double.IsNaN(_item) && shelf != null) width = Math.Max(width, shelf.Total * (_item + _gap));
        return Children.Count == 0 ? new Size() : new Size(width - _gap, _height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0;
        foreach (var child in Children)
        {
            double w = double.IsNaN(_item) ? child.DesiredSize.Width : _item;
            child.Arrange(new Rect(x, 0, w, _height));
            x += w + _gap;
        }
        return final;
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

// A picture filling its box like an ImageBrush with UniformToFill (the middle of it), the corners rounded, drawn straight
// onto the screen. Avalonia's Skia renderer draws every ImageBrush through an offscreen surface made again at every frame:
// a list of covers that scrolls made 10-15 of them per frame (the phone's playlists stuttered), the song page's blurred
// background one as big as the screen.
public sealed class CoverImage : Control
{
    public static readonly StyledProperty<IImage?> SourceProperty = AvaloniaProperty.Register<CoverImage, IImage?>(nameof(Source));
    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty = AvaloniaProperty.Register<CoverImage, CornerRadius>(nameof(CornerRadius));
    // Round (an avatar), whatever its size.
    public static readonly StyledProperty<bool> IsCircleProperty = AvaloniaProperty.Register<CoverImage, bool>(nameof(IsCircle));

    static CoverImage()
    {
        AffectsRender<CoverImage>(SourceProperty, CornerRadiusProperty, IsCircleProperty);
        // (nothing to touch: the element around it gets the finger)
        IsHitTestVisibleProperty.OverrideDefaultValue<CoverImage>(false);
    }

    public IImage? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public bool IsCircle
    {
        get => GetValue(IsCircleProperty);
        set => SetValue(IsCircleProperty, value);
    }

    // From one song to the next: the new picture comes up over the old one, then only one is drawn again.
    private IImage? _before;
    private double _coming = 1;
    private int _fade;

    public void FadeTo(IImage? next)
    {
        int run = ++_fade;
        if (Source == null || next == null || !Ui.Animations || !IsEffectivelyVisible)
        {
            _before = null;
            _coming = 1;
            Source = next;
            return;
        }
        _before = Source;
        _coming = 0;
        Source = next;
        Ui.Tween(this, Themes.FadeMs, Ui.CubicInOut, k =>
        {
            if (run != _fade) return;
            _coming = k;
            if (k >= 1) _before = null;
            InvalidateVisual();
        });
    }

    public override void Render(DrawingContext context)
    {
        if (_before is { } old) Draw(context, old);
        if (Source is not { } img) return;
        if (_before == null)
        {
            Draw(context, img);
            return;
        }
        using (context.PushOpacity(_coming)) Draw(context, img);
    }

    private void Draw(DrawingContext context, IImage img)
    {
        var box = new Rect(Bounds.Size);
        // Skia reads a bitmap's source rect in pixels (Avalonia hands it DIPs, the same only at 96 DPI): a snapshot made at
        // the screen's scaling showed just its top-left corner, blown up.
        var size = img is Bitmap bitmap ? new Size(bitmap.PixelSize.Width, bitmap.PixelSize.Height) : img.Size;
        if (box.Width <= 0 || box.Height <= 0 || size.Width <= 0 || size.Height <= 0) return;
        double scale = Math.Max(box.Width / size.Width, box.Height / size.Height);
        double w = box.Width / scale, h = box.Height / scale;
        var source = new Rect((size.Width - w) / 2, (size.Height - h) / 2, w, h);
        var radius = IsCircle ? new CornerRadius(Math.Min(box.Width, box.Height) / 2) : CornerRadius;
        if (radius == default)
        {
            context.DrawImage(img, source, box);
            return;
        }
        using (context.PushClip(new RoundedRect(box, radius)))
            context.DrawImage(img, source, box);
    }
}

// A small ring filling up as a download goes on (a song being saved on the device): a faint track and the part done in
// the accent colour. Waiting (IsIndeterminate): a quarter of it, still (nothing moves, nothing costs).
public sealed class ProgressRing : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<ProgressRing, double>(nameof(Value));
    public static readonly StyledProperty<bool> IsIndeterminateProperty = AvaloniaProperty.Register<ProgressRing, bool>(nameof(IsIndeterminate));
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<ProgressRing, IBrush?>(nameof(Stroke), Brushes.White);
    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<ProgressRing, IBrush?>(nameof(Track), Brushes.Gray);
    public static readonly StyledProperty<double> ThicknessProperty = AvaloniaProperty.Register<ProgressRing, double>(nameof(Thickness), 2.2);

    static ProgressRing()
    {
        AffectsRender<ProgressRing>(ValueProperty, IsIndeterminateProperty, StrokeProperty, TrackProperty, ThicknessProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<ProgressRing>(false);
    }

    // 0-100.
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool IsIndeterminate { get => GetValue(IsIndeterminateProperty); set => SetValue(IsIndeterminateProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    public override void Render(DrawingContext context)
    {
        double size = Math.Min(Bounds.Width, Bounds.Height), t = Thickness;
        if (size <= t * 2) return;
        double r = (size - t) / 2;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        context.DrawEllipse(null, new Pen(Track, t), c, r, r);
        double v = IsIndeterminate ? 0.25 : Math.Clamp(Value / 100, 0, 1);
        if (v <= 0.005) return;
        var pen = new Pen(Stroke, t, lineCap: PenLineCap.Round);
        if (v >= 0.999)
        {
            context.DrawEllipse(null, pen, c, r, r);
            return;
        }
        double a = v * 2 * Math.PI;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X, c.Y - r), false);
            ctx.ArcTo(new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a)), new Size(r, r), 0, a > Math.PI, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, pen, g);
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
    public static readonly StyledProperty<double> MaxColumnWidthProperty = AvaloniaProperty.Register<MasonryPanel, double>(nameof(MaxColumnWidth), double.PositiveInfinity);

    static MasonryPanel() => AffectsMeasure<MasonryPanel>(ColumnWidthProperty, MaxColumnsProperty, GapProperty, MaxColumnWidthProperty);

    public double ColumnWidth { get => GetValue(ColumnWidthProperty); set => SetValue(ColumnWidthProperty, value); }
    public int MaxColumns { get => GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
    public double Gap { get => GetValue(GapProperty); set => SetValue(GapProperty, value); }
    public double MaxColumnWidth { get => GetValue(MaxColumnWidthProperty); set => SetValue(MaxColumnWidthProperty, value); }

    private readonly List<(int Col, double Y)> _slots = new();

    // Never more columns than cards shown: a card alone keeps to the left, as wide as it's allowed.
    private int Columns(double width)
        => double.IsInfinity(width) ? 1 : Math.Clamp((int)((width + Gap) / (ColumnWidth + Gap)), 1, Math.Max(1, Math.Min(MaxColumns, Children.Count(c => c.IsVisible))));

    private double WidthOf(double width, int cols) => Math.Min(MaxColumnWidth, (width - Gap * (cols - 1)) / cols);

    protected override Size MeasureOverride(Size available)
    {
        int cols = Columns(available.Width);
        double colW = double.IsInfinity(available.Width) ? ColumnWidth : WidthOf(available.Width, cols);
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
        double colW = WidthOf(final.Width, cols);
        for (int i = 0; i < Children.Count && i < _slots.Count; i++)
        {
            var (c, y) = _slots[i];
            Children[i].Arrange(new Rect(c * (colW + Gap), y, colW, Children[i].DesiredSize.Height));
        }
        return final;
    }
}

// What opens and closes inside a page (a part of the sidebar, the rows under a switch), bound in place of its IsVisible: it unfolds in
// the time a page takes to slide and what's under it moves with it. It's laid out once at its own height: a bottom margin takes away
// what's still shut and a clip hides it, so at each frame only its neighbours move.
public static class Fold
{
    public static readonly AttachedProperty<bool> OpenProperty = AvaloniaProperty.RegisterAttached<Control, bool>("Open", typeof(Fold), true);

    public static bool GetOpen(Control o) => o.GetValue(OpenProperty);
    public static void SetOpen(Control o, bool value) => o.SetValue(OpenProperty, value);

    static Fold() => OpenProperty.Changed.AddClassHandler<Control>((el, e) => Go(el, e.GetNewValue<bool>()));

    private sealed record Run(Thickness Margin, double Height, RectangleGeometry Clip, Tweener At);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, Run> Runs = new();

    private static void Go(Control el, bool open)
    {
        // (the first value of its binding, or a page not on the screen: nothing to see)
        if (!Ui.Animations || el.GetVisualRoot() == null || el.GetVisualParent() is not { IsEffectivelyVisible: true })
        {
            Land(el, open);
            return;
        }
        if (!Runs.TryGetValue(el, out var run))
        {
            if (el.IsVisible == open) return;
            el.IsVisible = true;
            el.UpdateLayout();
            if (el.Bounds.Height <= 0)
            {
                Land(el, open);
                return;
            }
            var clip = new RectangleGeometry();
            el.Clip = clip;
            var (margin, height) = (el.Margin, el.Bounds.Height);
            run = new Run(margin, height, clip, new Tweener(el, at => Place(el, margin, height, clip, at), open ? 0 : 1));
            Runs.Add(el, run);
            Place(el, margin, height, clip, open ? 0 : 1);
        }
        run.At.To(open ? 1 : 0, AnimatedContent.Ms, Ui.CubicOut, completed: () => { if (GetOpen(el) == open) Land(el, open); });
    }

    private static void Place(Control el, Thickness margin, double height, RectangleGeometry clip, double at)
    {
        double whole = height + margin.Top + margin.Bottom;
        el.Margin = new Thickness(margin.Left, margin.Top, margin.Right, margin.Bottom - whole * (1 - at));
        // (a little beyond its sides and top: a shadow or a focus ring isn't cut)
        clip.Rect = new Rect(-40, -40, el.Bounds.Width + 80, 40 + Math.Max(0, whole * at - margin.Top));
        el.Opacity = at;
    }

    private static void Land(Control el, bool open)
    {
        if (Runs.TryGetValue(el, out var run))
        {
            Runs.Remove(el);
            run.At.Stop();
            el.Margin = run.Margin;
            el.Clip = null;
            el.ClearValue(Visual.OpacityProperty);
        }
        el.IsVisible = open;
    }
}

// Panes of one cell with one shown (a room and its lobby, the DJ's two modes, the phones' tabs), bound or set in place of their IsVisible:
// the new one comes in from its side while the old one goes out on its own, as pages do. Unlike AnimatedContent's pages they stay built, in the tree.
public static class Pane
{
    public static readonly AttachedProperty<bool> ShownProperty = AvaloniaProperty.RegisterAttached<Control, bool>("Shown", typeof(Pane), true);

    // The side it comes from and goes to: -1 left, 1 right.
    public static readonly AttachedProperty<int> SideProperty = AvaloniaProperty.RegisterAttached<Control, int>("Side", typeof(Pane), 1);

    public static bool GetShown(Control o) => o.GetValue(ShownProperty);
    public static void SetShown(Control o, bool value) => o.SetValue(ShownProperty, value);
    public static int GetSide(Control o) => o.GetValue(SideProperty);
    public static void SetSide(Control o, int value) => o.SetValue(SideProperty, value);

    static Pane() => ShownProperty.Changed.AddClassHandler<Control>((el, e) => Go(el, e.GetNewValue<bool>()));

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, Tweener> Runs = new();

    // From one pane of a row of them to another: the side comes from their order.
    public static void Between(IReadOnlyList<Control> panes, int from, int to)
    {
        for (int i = 0; i < panes.Count; i++)
        {
            SetSide(panes[i], (i == to ? 1 : -1) * Math.Sign(to - from));
            SetShown(panes[i], i == to);
        }
    }

    private static void Go(Control el, bool shown)
    {
        double step = (el.GetVisualParent() as Control)?.Bounds.Width + 56 ?? 0;
        if (!Ui.Animations || el.GetVisualRoot() == null || step <= 56 || el.GetVisualParent() is not { IsEffectivelyVisible: true })
        {
            if (Runs.TryGetValue(el, out var moving)) moving.Stop();
            el.RenderTransform = null;
            el.IsVisible = shown;
            return;
        }
        if (el.RenderTransform is not TranslateTransform shift) el.RenderTransform = shift = new TranslateTransform();
        double aside = GetSide(el) * step;
        var run = Runs.GetValue(el, _ => new Tweener(el, x => { if (el.RenderTransform is TranslateTransform t) t.X = x; }));
        if (shown && !el.IsVisible)
        {
            el.IsVisible = true;
            run.Set(aside);
        }
        else if (!run.IsRunning) run.Set(shift.X);
        run.To(shown ? 0 : aside, AnimatedContent.Ms, Ui.CubicOut, completed: () =>
        {
            if (GetShown(el) != shown) return;
            el.RenderTransform = null;
            if (!shown) el.IsVisible = false;
        });
    }
}
