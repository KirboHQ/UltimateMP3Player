using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// The sheets that slide up from the bottom (the phone's menus and dialogs): one over the other, the screen dimmed behind
// them. A tap on the dim part or the back button closes the top one, a drag down too.
public sealed class SheetHost : Panel
{
    private readonly List<Layer> _layers = new();
    private double _keyboard;

    public SheetHost()
    {
        IsVisible = false;
        SizeChanged += (_, _) =>
        {
            foreach (var l in _layers) l.Card.MaxHeight = MaxFor(l.Share);
        };
    }

    // The screen's height (the host may not be measured yet when the first sheet opens).
    private double MaxFor(double share)
    {
        double h = Bounds.Height > 0 ? Bounds.Height : TopLevel.GetTopLevel(this)?.ClientSize.Height ?? 800;
        return Math.Max(240, (h - _keyboard - _insets.Top) * share);
    }

    private Thickness _insets;

    // The system's bars: a sheet's last row stays above the gesture line or the buttons (the sheets already open too).
    public void SetInsets(Thickness insets)
    {
        _insets = insets;
        foreach (var l in _layers)
        {
            l.Card.Padding = CardPadding;
            l.Card.MaxHeight = MaxFor(l.Share);
        }
    }

    // (with the keyboard open too: the height it reports doesn't count the system's buttons under it)
    private Thickness CardPadding => new(_insets.Left, 0, _insets.Right, _insets.Bottom);

    public bool IsOpen => _layers.Count > 0;

    // The keyboard covers this much of the bottom: the sheets go above it.
    public void SetKeyboard(double height)
    {
        _keyboard = height;
        foreach (var l in _layers)
        {
            l.Card.Margin = new Thickness(0, 0, 0, _keyboard);
            l.Card.Padding = CardPadding;
            l.Card.MaxHeight = MaxFor(l.Share);
        }
    }

    private sealed class Layer
    {
        public required Border Scrim { get; init; }
        public required Border Card { get; init; }
        public required TranslateTransform Shift { get; init; }
        public required TaskCompletionSource Closed { get; init; }
        public bool Dismissable { get; init; }
        public Action? OnDismiss { get; init; }
        public double Share { get; init; }
        public bool Closing { get; set; }
    }

    // Shows content in a new sheet; the task ends when it's closed (by the code, a tap outside, back).
    public Task Show(Control content, bool dismissable = true, Action? onDismiss = null, double maxHeightShare = 0.88)
    {
        IsVisible = true;
        var scrim = new Border { Background = Ui.Find<IBrush>("ScrimBrush"), Opacity = 0 };
        var handle = new Border
        {
            Width = 40, Height = 5, CornerRadius = new CornerRadius(3), Background = Ui.Find<IBrush>("BorderStrongBrush"),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 6),
        };
        var stack = new DockPanel();
        DockPanel.SetDock(handle, Dock.Top);
        stack.Children.Add(handle);
        stack.Children.Add(content);
        var shift = new TranslateTransform();
        var card = new Border
        {
            Background = Ui.Find<IBrush>("SheetBrush"),
            CornerRadius = new CornerRadius(24, 24, 0, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = stack,
            RenderTransform = shift,
            Margin = new Thickness(0, 0, 0, _keyboard),
            Padding = CardPadding,
            BoxShadow = BoxShadows.Parse("0 -4 24 0 #66000000"),
            MaxWidth = 640,
        };
        card.MaxHeight = MaxFor(maxHeightShare);
        var layer = new Layer
        {
            Scrim = scrim, Card = card, Shift = shift, Closed = new TaskCompletionSource(), Dismissable = dismissable, OnDismiss = onDismiss, Share = maxHeightShare,
        };
        if (dismissable) scrim.Tapped += (_, _) => Dismiss(layer);
        DragToClose(layer, card);
        _layers.Add(layer);
        Children.Add(scrim);
        Children.Add(card);
        // From below the screen: its height is only known once it's measured.
        shift.Y = 2000;
        card.LayoutUpdated += First;
        void First(object? s, EventArgs e)
        {
            card.LayoutUpdated -= First;
            double h = card.Bounds.Height;
            if (!Ui.Animations)
            {
                shift.Y = 0;
                scrim.Opacity = 1;
                return;
            }
            Ui.Tween(this, 280, Ui.CubicOut, t =>
            {
                if (layer.Closing) return;
                shift.Y = h * (1 - t);
                scrim.Opacity = t;
            });
        }
        return layer.Closed.Task;
    }

    // A drag down anywhere on the sheet closes it, unless it starts on a list scrolled down (then the finger scrolls the
    // list back up first). Seen on the way down (tunnel), so the sheet takes the finger before the list starts scrolling.
    private void DragToClose(Layer layer, Control area)
    {
        Point? start = null;
        double startShift = 0;
        long startAt = 0;
        bool dragging = false;
        void Settle()
        {
            double from = layer.Shift.Y;
            if (from <= 0) return;
            Ui.Tween(this, 180, Ui.CubicOut, t => { if (!layer.Closing) layer.Shift.Y = from * (1 - t); });
        }
        area.AddHandler(PointerPressedEvent, (_, e) =>
        {
            start = null;
            dragging = false;
            if (!layer.Dismissable || e.Pointer.Type == PointerType.Mouse && !e.GetCurrentPoint(area).Properties.IsLeftButtonPressed) return;
            if (e.Source is Visual v && (Ui.FindAncestor<ScrollViewer>(v) is { Offset.Y: > 1 } || OwnDrag(v, area))) return;
            start = e.GetPosition(this);
            startShift = layer.Shift.Y;
            startAt = Environment.TickCount64;
        }, RoutingStrategies.Tunnel, true);
        area.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (start is not Point s) return;
            double dy = e.GetPosition(this).Y - s.Y;
            if (!dragging)
            {
                // Up (or sideways): the list's own scrolling, not ours.
                if (dy < -8 || Math.Abs(e.GetPosition(this).X - s.X) > 24 && Math.Abs(e.GetPosition(this).X - s.X) > dy)
                {
                    start = null;
                    return;
                }
                if (dy < 12) return;
                dragging = true;
                e.Pointer.Capture(area);
            }
            layer.Shift.Y = Math.Max(0, startShift + dy);
            e.Handled = true;
        }, RoutingStrategies.Tunnel, true);
        area.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (start is not Point s) return;
            start = null;
            if (!dragging) return;
            dragging = false;
            double dy = e.GetPosition(this).Y - s.Y;
            double speed = dy / Math.Max(1, Environment.TickCount64 - startAt);
            e.Pointer.Capture(null);
            // Not a tap on what's under the finger.
            e.Handled = true;
            if (dy > layer.Card.Bounds.Height * 0.3 || speed > 0.8) Dismiss(layer);
            else Settle();
        }, RoutingStrategies.Tunnel, true);
        area.PointerCaptureLost += (_, _) =>
        {
            if (dragging) Settle();
            start = null;
            dragging = false;
        };
    }

    // What the finger drags by itself: a slider (the equalizer's bands), a song's handle in "Up next".
    private static bool OwnDrag(Visual v, Visual area)
    {
        for (var x = v; x != null && x != area; x = x.GetVisualParent())
            if (x is Slider || x is StyledElement { Classes: var c } && c.Contains("handle")) return true;
        return false;
    }

    private void Dismiss(Layer layer)
    {
        if (!layer.Dismissable) return;
        layer.OnDismiss?.Invoke();
        Close(layer);
    }

    // The top sheet (back button): false when there's none.
    public bool CloseTop()
    {
        var top = _layers.LastOrDefault(l => !l.Closing);
        if (top == null) return false;
        if (top.Dismissable) Dismiss(top);
        return true;
    }

    // Closes the sheet that shows this content.
    public void Close(Control content)
    {
        var layer = _layers.LastOrDefault(l => l.Card == content || l.Card.IsVisualAncestorOf(content) || IsLogicalAncestor(l.Card, content));
        if (layer != null) Close(layer);
    }

    private static bool IsLogicalAncestor(Avalonia.LogicalTree.ILogical ancestor, Avalonia.LogicalTree.ILogical c)
    {
        for (var x = c.LogicalParent; x != null; x = x.LogicalParent)
            if (x == ancestor) return true;
        return false;
    }

    public void CloseAll()
    {
        foreach (var l in _layers.ToList()) Close(l);
    }

    private void Close(Layer layer)
    {
        if (layer.Closing) return;
        layer.Closing = true;
        void Gone()
        {
            Children.Remove(layer.Scrim);
            Children.Remove(layer.Card);
            _layers.Remove(layer);
            if (_layers.Count == 0) IsVisible = false;
            layer.Closed.TrySetResult();
        }
        if (!Ui.Animations)
        {
            Gone();
            return;
        }
        double from = layer.Shift.Y, h = layer.Card.Bounds.Height, opacity = layer.Scrim.Opacity;
        Ui.Tween(this, 200, t => t * t, t =>
        {
            layer.Shift.Y = from + (h - from) * t;
            layer.Scrim.Opacity = opacity * (1 - t);
            if (t >= 1) Gone();
        });
    }

    // ------------------------------------------------------------------ menus

    public void ShowMenu(SheetMenu menu)
    {
        var body = new StackPanel { Margin = new Thickness(10, 0, 10, 10) };
        if (menu.Title != null) body.Children.Add(MenuHeader(menu));
        Control? content = null;
        void Fill()
        {
            int keep = menu.Title != null ? 1 : 0;
            while (body.Children.Count > keep) body.Children.RemoveAt(body.Children.Count - 1);
            foreach (var e in menu.Items) body.Children.Add(Entry(e, Fill, () => Close(content!)));
        }
        Fill();
        content = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        _ = Show(content);
    }

    private static Control MenuHeader(SheetMenu menu)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(8, 4, 8, 10) };
        var cover = Covers.For(menu.Cover, 48, 8);
        if (cover != null) grid.Children.Add(cover);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(cover != null ? 14 : 0, 0, 0, 0) };
        text.Children.Add(new TextBlock { Text = menu.Title, FontSize = 16.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrEmpty(menu.Subtitle))
            text.Children.Add(new TextBlock { Text = menu.Subtitle, FontSize = 13.5, Foreground = Ui.Res("SubTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var wrap = new StackPanel();
        wrap.Children.Add(grid);
        wrap.Children.Add(new Border { Height = 1, Background = Ui.Res("BorderBrush"), Margin = new Thickness(4, 0, 4, 6) });
        return wrap;
    }

    private Control Entry(SheetEntry e, Action refill, Action close)
    {
        if (e is SheetLine) return new Border { Height = 1, Background = Ui.Res("BorderBrush"), Margin = new Thickness(16, 6) };
        if (e.Info)
            return new TextBlock
            {
                Text = e.Text, Classes = { "hint" }, Margin = new Thickness(18, 8, 18, 8),
            };
        object content = e.Text;
        if (e.Dot != null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = e.Dot, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
            row.Children.Add(new TextBlock { Text = e.Text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            content = row;
        }
        else content = new TextBlock { Text = e.Text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var b = new Button { Theme = Ui.Theme("SheetItem"), Content = content, IsEnabled = e.Enabled };
        bool? isChecked = e.Checked?.Invoke();
        Ui.SetGlyph(b, isChecked == true ? "" : isChecked == false ? "" : e.Glyph ?? "");
        if (isChecked == true) b.Classes.Add("checked");
        if (e.Danger) b.Classes.Add("danger");
        if (e.Sub != null) b.Classes.Add("sub");
        b.Click += (_, _) =>
        {
            if (e.Sub != null)
            {
                close();
                ShowMenu(e.Sub());
                return;
            }
            if (e.Checked != null)
            {
                // A tick: in place, the sheet stays open to tick more.
                e.Click?.Invoke();
                refill();
                return;
            }
            close();
            e.Click?.Invoke();
        };
        return b;
    }
}

// Covers drawn in code (the sheets' headers, the dialogs): a song's, a playlist's, a tag's colour.
public static class Covers
{
    public static Control? For(object? what, double size, double radius)
    {
        var templates = Avalonia.Application.Current!;
        string? key = what switch
        {
            TrackViewModel => "TrackCoverSmall",
            PlaylistViewModel => "PlaylistCover",
            _ => null,
        };
        if (key != null && templates.FindResource(key) is Avalonia.Controls.Templates.IDataTemplate dt)
            return new ContentControl { Content = what, ContentTemplate = dt, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        if (what is TagViewModel tag)
            return new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(radius), Background = Ui.Res("Surface3Brush"),
                Child = new Ellipse { Width = size * 0.38, Height = size * 0.38, Fill = tag.Brush },
            };
        return null;
    }
}
