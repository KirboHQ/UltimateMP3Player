using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// What WPF's MouseBinding did in the Windows app's templates: a left click (or a double click) runs a command.
public static class Click
{
    public static readonly AttachedProperty<ICommand?> CommandProperty = AvaloniaProperty.RegisterAttached<Control, ICommand?>("Command", typeof(Click));
    public static readonly AttachedProperty<object?> CommandParameterProperty = AvaloniaProperty.RegisterAttached<Control, object?>("CommandParameter", typeof(Click));
    public static readonly AttachedProperty<ICommand?> DoubleProperty = AvaloniaProperty.RegisterAttached<Control, ICommand?>("Double", typeof(Click));

    public static ICommand? GetCommand(Control c) => c.GetValue(CommandProperty);
    public static void SetCommand(Control c, ICommand? v) => c.SetValue(CommandProperty, v);
    public static object? GetCommandParameter(Control c) => c.GetValue(CommandParameterProperty);
    public static void SetCommandParameter(Control c, object? v) => c.SetValue(CommandParameterProperty, v);
    public static ICommand? GetDouble(Control c) => c.GetValue(DoubleProperty);
    public static void SetDouble(Control c, ICommand? v) => c.SetValue(DoubleProperty, v);

    static Click()
    {
        CommandProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            c.PointerReleased -= OnReleased;
            if (e.NewValue != null) c.PointerReleased += OnReleased;
        });
        DoubleProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            c.DoubleTapped -= OnDouble;
            if (e.NewValue != null) c.DoubleTapped += OnDouble;
        });
    }

    private static void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control c || e.InitialPressMouseButton != MouseButton.Left || e.Handled) return;
        // Released over the element (not a drag that ended elsewhere) and not on a button inside it.
        var p = e.GetPosition(c);
        if (p.X < 0 || p.Y < 0 || p.X > c.Bounds.Width || p.Y > c.Bounds.Height) return;
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(true) is { } b && b != c && c.IsVisualAncestorOf(b)) return;
        var cmd = GetCommand(c);
        var arg = c.IsSet(CommandParameterProperty) ? GetCommandParameter(c) : c.DataContext;
        if (cmd?.CanExecute(arg) == true) cmd.Execute(arg);
    }

    private static void OnDouble(object? sender, TappedEventArgs e)
    {
        if (sender is not Control c) return;
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(true) is { } b && b != c && c.IsVisualAncestorOf(b)) return;
        var cmd = GetDouble(c);
        if (cmd?.CanExecute(null) == true) cmd.Execute(null);
        e.Handled = true;
    }
}

// Right click on a row, a card, a playlist or a tag: its menu (Menus). Track rows can be dragged onto playlists and tags.
public static class Rows
{
    // "track" (a song row of a list), "card" (a song card), "hit" (a song found online), "playlist", "tag", "queue" (a row of "next up").
    public static readonly AttachedProperty<string?> MenuProperty = AvaloniaProperty.RegisterAttached<Control, string?>("Menu", typeof(Rows));
    public static string? GetMenu(Control c) => c.GetValue(MenuProperty);
    public static void SetMenu(Control c, string? v) => c.SetValue(MenuProperty, v);

    // The song rows can be dragged (onto a playlist or a tag of the sidebar, or inside a playlist).
    public static readonly AttachedProperty<bool> DragProperty = AvaloniaProperty.RegisterAttached<Control, bool>("Drag", typeof(Rows));
    public static bool GetDrag(Control c) => c.GetValue(DragProperty);
    public static void SetDrag(Control c, bool v) => c.SetValue(DragProperty, v);

    public const string TrackRowFormat = "ump/track-row";
    public const string QueueRowFormat = "ump/queue-row";

    static Rows()
    {
        MenuProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            c.PointerReleased -= OnRightClick;
            if (e.NewValue != null) c.PointerReleased += OnRightClick;
        });
        DragProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            c.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
            c.PointerMoved -= OnMoved;
            c.PointerReleased -= OnReleased;
            c.PointerCaptureLost -= OnCaptureLost;
            if (e.NewValue is true)
            {
                // (also the press a button takes: a song found online is one)
                c.AddHandler(InputElement.PointerPressedEvent, OnPressed, handledEventsToo: true);
                c.PointerMoved += OnMoved;
                c.PointerReleased += OnReleased;
                c.PointerCaptureLost += OnCaptureLost;
            }
        });
    }

    private static void OnRightClick(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control c || e.InitialPressMouseButton != MouseButton.Right || e.Handled) return;
        // On an item where several are selected (Ctrl or Shift + click): the menu of all of them.
        var many = GetMenu(c) is "card" or "playlist" or "queue" or "download" ? Menus.SelectionAround(c) : new List<object>();
        var menu = GetMenu(c) switch
        {
            "track" when c.DataContext is TrackRow row => Menus.MenuFor(row, c),
            "hit" when c.DataContext is OnlineHitViewModel h => Menus.ForHit(h),
            "card" when many.Count > 1 => Menus.ForSelection(SelectionBar.SongsOf(many), null),
            "card" when c.DataContext is TrackViewModel t => Menus.ForTrack(t, null),
            "playlist" when many.OfType<PlaylistViewModel>().ToList() is { Count: > 1 } lists => Menus.ForPlaylists(lists),
            "playlist" when c.DataContext is PlaylistViewModel p => Menus.ForPlaylist(p),
            "tag" when c.DataContext is TagViewModel t => Menus.ForTag(t),
            "queue" when many.OfType<QueueRow>().ToList() is { Count: > 1 } rows => Menus.ForQueueRows(rows),
            "queue" when c.DataContext is QueueRow q => Menus.ForQueue(q),
            "download" when many.OfType<DownloadJobViewModel>().ToList() is { Count: > 1 } jobs => Menus.ForJobs(jobs),
            "download" when c.DataContext is DownloadJobViewModel { Song: { } song } => Menus.ForTrack(song, song.Main.LibraryPage),
            "current" when App.Host.Session?.Player.Current is { } cur => Menus.ForTrack(cur, null),
            _ => null,
        };
        if (menu == null) return;
        Menus.Open(menu, c, false);
        e.Handled = true;
    }

    private static Point? _start;

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control c && e.GetCurrentPoint(c).Properties.IsLeftButtonPressed) _start = e.GetPosition(null);
    }

    private static async void OnMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control c) return;
        if (_pull != null)
        {
            _pull.Move(e.GetPosition(null).X - _pullFrom);
            e.Handled = true;
            return;
        }
        if (_start is not Point start || !e.GetCurrentPoint(c).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - start.X) < 6 && Math.Abs(p.Y - start.Y) < 6) return;
        _start = null;
        // (a button of the row, not the button a song found online is)
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(true) is { } b && c.IsVisualAncestorOf(b)) return;
        // Mostly to the right: the row is pulled (into the queue, as on the phones); any other way it's dragged (onto a
        // playlist or a tag of the sidebar, inside a playlist).
        if (p.X - start.X > 1.5 * Math.Abs(p.Y - start.Y) && _wheelPull == null && Pull(c) is { } pull)
        {
            // (a button gives the pointer to what it shows: no click when it's let go; before _pull, as the button losing it
            // would end the pull)
            e.Pointer.Capture(c is Button { Content: Control inner } ? inner : c);
            _pull = pull;
            _pullFrom = p.X;
            e.Handled = true;
            return;
        }
        if (c.DataContext is not TrackRow row) return;
        var data = new DataObject();
        data.Set(TrackRowFormat, row);
        await DragVisuals.RunAsync(c, e, data, DragDropEffects.Copy | DragDropEffects.Move);
    }

    // ------------------------------------------------------------------ a song's row pulled to the right (RowSwipe)

    // By the mouse: from where the pull began.
    private static RowSwipe? _pull;
    private static double _pullFrom;
    // By two fingers on the touchpad (a sideways wheel): the wheel has no release, so the row lets go once the fingers stop
    // for a moment (their momentum included). In wheel steps, not pixels: a step takes the row this share of the way to the
    // mark (whatever the row's width); the row moves after this much of a step.
    private const double StepShare = 0.4, StartSteps = 0.15;
    // On the computer the mark is at this share of the row (wider than the phones', which have it at 0.3).
    private const double MarkShare = 0.15;
    private static RowSwipe? _wheelPull;
    private static Control? _wheelRow;
    private static double _wheelSteps;
    private static DispatcherTimer? _wheelEnd;

    private static RowSwipe? Pull(Control c)
    {
        var run = c.DataContext switch
        {
            TrackRow row => Menus.AddToQueue(row.Track, row.Owner),
            OnlineHitViewModel h => h.QueueAction,
            _ => null,
        };
        return run != null && AdornerLayer.GetAdornerLayer(c) is { } layer ? new RowSwipe(c, run, layer, MarkShare) : null;
    }

    private static void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pull == null || e.InitialPressMouseButton != MouseButton.Left) return;
        e.Handled = true;
        EndPull(true);
        e.Pointer.Capture(null);
    }

    // (the row's own capture: not one of its children's, given up when the row took it)
    private static void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (e.Source == sender) EndPull(false);
    }

    private static void EndPull(bool commit)
    {
        var pull = _pull;
        _pull = null;
        pull?.End(commit);
    }

    // The main window's wheel, before the lists scroll: sideways over a song's row, the row is pulled.
    public static void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        // (positive: the content goes right)
        double steps = e.Delta.X;
        bool sideways = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y);
        if (_wheelPull != null)
        {
            // (scrolling up or down again: let go)
            if (!sideways)
            {
                EndWheel();
                return;
            }
            _wheelSteps += steps;
            _wheelPull.Move(_wheelSteps * StepShare * _wheelPull.MarkAt);
            _wheelEnd!.Stop();
            _wheelEnd.Start();
            e.Handled = true;
            return;
        }
        if (!sideways || _pull != null || RowAt(e.Source as Visual) is not { } c) return;
        if (c != _wheelRow) _wheelSteps = 0;
        _wheelRow = c;
        _wheelSteps += steps;
        _wheelEnd ??= new DispatcherTimer(TimeSpan.FromMilliseconds(160), DispatcherPriority.Normal, (_, _) => EndWheel());
        _wheelEnd.Stop();
        _wheelEnd.Start();
        // (a little to the right first, as the mouse's: then the row follows from there)
        if (_wheelSteps < StartSteps || Pull(c) is not { } pull) return;
        _wheelPull = pull;
        _wheelSteps = 0;
        e.Handled = true;
    }

    private static void EndWheel()
    {
        _wheelEnd?.Stop();
        var pull = _wheelPull;
        _wheelPull = null;
        _wheelRow = null;
        _wheelSteps = 0;
        pull?.End(true);
    }

    private static Control? RowAt(Visual? v)
    {
        for (; v != null; v = v.GetVisualParent())
            if (v is Control c && GetMenu(c) is "track" or "hit" && GetDrag(c)) return c;
        return null;
    }
}

// A sidebar playlist or tag that takes the songs dropped on it (the border lights up while one is over it).
public static class Drops
{
    // "playlist": the song is added to it; "tag": the song gets it.
    public static readonly AttachedProperty<string?> TargetProperty = AvaloniaProperty.RegisterAttached<Control, string?>("Target", typeof(Drops));
    public static string? GetTarget(Control c) => c.GetValue(TargetProperty);
    public static void SetTarget(Control c, string? v) => c.SetValue(TargetProperty, v);

    static Drops()
    {
        TargetProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            c.RemoveHandler(DragDrop.DragOverEvent, OnOver);
            c.RemoveHandler(DragDrop.DragLeaveEvent, OnLeave);
            c.RemoveHandler(DragDrop.DropEvent, OnDrop);
            if (e.NewValue == null) return;
            DragDrop.SetAllowDrop(c, true);
            c.AddHandler(DragDrop.DragOverEvent, OnOver);
            c.AddHandler(DragDrop.DragLeaveEvent, OnLeave);
            c.AddHandler(DragDrop.DropEvent, OnDrop);
        });
    }

    private static void OnOver(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains(Rows.TrackRowFormat) || sender is not Border b) return;
        e.DragEffects = DragDropEffects.Copy;
        b.BorderThickness = new Thickness(1);
        e.Handled = true;
    }

    private static void OnLeave(object? sender, DragEventArgs e)
    {
        if (sender is Border b) b.BorderThickness = new Thickness(0);
    }

    private static void OnDrop(object? sender, DragEventArgs e)
    {
        if (sender is not Control c) return;
        if (c is Border b) b.BorderThickness = new Thickness(0);
        if (e.Data.Get(Rows.TrackRowFormat) is not TrackRow row) return;
        switch (c.DataContext)
        {
            case PlaylistViewModel p when GetTarget(c) == "playlist":
                row.Track.Main.AddToPlaylist(row.Track, p.P);
                break;
            case TagViewModel t when GetTarget(c) == "tag":
                row.Track.Main.SetTag(new[] { row.Track }, t, true);
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
