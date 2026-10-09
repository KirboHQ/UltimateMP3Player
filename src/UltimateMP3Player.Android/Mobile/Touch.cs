using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// What the computer apps do with the mouse, with the finger: a tap runs a command (the computer's click, or double click
// on a row), a long press opens the menu (the right click), with a little vibration.
public static class Touch
{
    public static readonly AttachedProperty<ICommand?> TapProperty = AvaloniaProperty.RegisterAttached<Control, ICommand?>("Tap", typeof(Touch));
    public static readonly AttachedProperty<object?> TapParameterProperty = AvaloniaProperty.RegisterAttached<Control, object?>("TapParameter", typeof(Touch));
    // "track" (a song of a list), "card" (a song card), "playlist", "tag", "queue" (a song of "next up"), "current" (the song playing).
    public static readonly AttachedProperty<string?> MenuProperty = AvaloniaProperty.RegisterAttached<Control, string?>("Menu", typeof(Touch));

    public static ICommand? GetTap(Control c) => c.GetValue(TapProperty);
    public static void SetTap(Control c, ICommand? v) => c.SetValue(TapProperty, v);
    public static object? GetTapParameter(Control c) => c.GetValue(TapParameterProperty);
    public static void SetTapParameter(Control c, object? v) => c.SetValue(TapParameterProperty, v);
    public static string? GetMenu(Control c) => c.GetValue(MenuProperty);
    public static void SetMenu(Control c, string? v) => c.SetValue(MenuProperty, v);

    static Touch()
    {
        TapProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            c.Tapped -= OnTapped;
            if (e.NewValue != null) c.Tapped += OnTapped;
        });
        MenuProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            c.ContextRequested -= OnMenu;
            if (e.NewValue != null) c.ContextRequested += OnMenu;
            // A selection to make: a tap picks the song (even without a command of its own).
            c.Tapped -= OnTapped;
            if (e.NewValue != null || GetTap(c) != null) c.Tapped += OnTapped;
        });
        // Every new press starts clean (first thing on its way down from the window).
        InputElement.PointerPressedEvent.AddClassHandler<TopLevel>((_, _) => _menuFrom = null, RoutingStrategies.Tunnel, true);
    }

    // The element whose menu this press opened: lifting the finger after a long press isn't a tap too (Avalonia sends
    // one), or the song would start while its menu opens.
    private static Control? _menuFrom;

    // The press ending now opened the menu of this element (or of one inside it, or around it).
    public static bool OpenedMenu(Control c) =>
        _menuFrom != null && (_menuFrom == c || _menuFrom.IsVisualAncestorOf(c) || c.IsVisualAncestorOf(_menuFrom));

    // Inside a button of the element (its "⋮", its heart) or on its drag handle: not the element's tap.
    private static bool OnInnerButton(Control c, object? source)
    {
        if (source is not Visual v) return false;
        if (v.FindAncestorOfType<Button>(true) is { } b && b != c && c.IsVisualAncestorOf(b)) return true;
        for (var x = v; x != null && x != c; x = x.GetVisualParent())
            if (x is StyledElement { Classes: var cls } && cls.Contains("handle")) return true;
        return false;
    }

    // The rows of a ListBox (the songs' lists) get the same touch's Tapped twice from Avalonia: the second one would untick
    // what the first one ticked while choosing (and play the song twice). One per element and press.
    private static Control? _lastTapOn;
    private static ulong _lastTapAt;

    // A list being arranged (dragging its songs by the handle): its rows don't play nor open menus.
    private static bool Arranging(Control c) => c.FindAncestorOfType<SongList>() is { Reordering: true };

    private static void OnTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control c || e.Handled || OnInnerButton(c, e.Source)) return;
        if (Arranging(c))
        {
            e.Handled = true;
            return;
        }
        if (c == _lastTapOn && e.Timestamp == _lastTapAt)
        {
            e.Handled = true;
            return;
        }
        _lastTapOn = c;
        _lastTapAt = e.Timestamp;
        // The finger only stopped a moving list.
        if (FlingScroll.Caught)
        {
            e.Handled = true;
            return;
        }
        if (OpenedMenu(c))
        {
            _menuFrom = null;
            e.Handled = true;
            return;
        }
        // Choosing (Selection.IsActive on the list): a tap ticks or unticks (things of the kind being chosen).
        if (Selection.Of(c) is { IsActive: true } sel && c.DataContext is { } item && Selection.KindOf(item) == sel.Kind && sel.Contains(item))
        {
            sel.Toggle(item);
            e.Handled = true;
            return;
        }
        var cmd = GetTap(c);
        if (cmd == null) return;
        var arg = c.IsSet(TapParameterProperty) ? GetTapParameter(c) : c.DataContext;
        if (cmd.CanExecute(arg)) cmd.Execute(arg);
        e.Handled = true;
    }

    private static void OnMenu(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control c || e.Handled || OnInnerButton(c, e.Source) || FlingScroll.Caught) return;
        e.Handled = true;
        if (Arranging(c)) return;
        _menuFrom = c;
        // While choosing: the menu of the chosen ones (with this one among them).
        if (Selection.Of(c) is { IsActive: true } sel && c.DataContext is { } item && Selection.KindOf(item) == sel.Kind && sel.Contains(item))
        {
            Haptics.LongPress();
            if (!sel.IsChecked(item)) sel.Toggle(item);
            if (Menus.ForSelection(sel) is { } many) Menus.Open(many);
            return;
        }
        // (nothing to offer, like a download still going: no vibration either)
        if (Menus.MenuFor(GetMenu(c), c.DataContext, c) is not { } menu) return;
        Haptics.LongPress();
        Menus.Open(menu);
    }
}

// The phone's vibration for a long press (the system's own, it follows the user's settings).
public static class Haptics
{
    public static void LongPress()
    {
        try { MainActivity.Current?.Window?.DecorView?.PerformHapticFeedback(Android.Views.FeedbackConstants.LongPress); }
        catch { }
    }

    public static void Tick()
    {
        try { MainActivity.Current?.Window?.DecorView?.PerformHapticFeedback(Android.Views.FeedbackConstants.ClockTick); }
        catch { }
    }
}
