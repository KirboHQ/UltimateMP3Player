using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class QueuePopup : UserControl
{
    private readonly TranslateTransform _rise = new();
    private readonly Tweener _show;
    private Point? _dragStart;

    public QueuePopup()
    {
        InitializeComponent();
        RenderTransform = _rise;
        // It rises a little out of the player bar as it shows.
        _show = new Tweener(this, e =>
        {
            Opacity = e;
            _rise.Y = 18 * (1 - e);
        }, 1);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible) _show.To(1, 220, Ui.CubicOut, from: 0);
        };
        QueueList.AddHandler(PointerPressedEvent, Row_MouseDown, RoutingStrategies.Tunnel);
        QueueList.AddHandler(PointerMovedEvent, Row_MouseMove, RoutingStrategies.Tunnel);
        QueueList.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.Data.Contains(Rows.QueueRowFormat) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        });
        // (the move itself happens when the drag ends: Row_MouseMove)
        QueueList.AddHandler(DragDrop.DropEvent, (_, e) => e.Handled = e.Data.Contains(Rows.QueueRowFormat));
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // ------------------------------------------------------------------ reordering, like the song view's queue (no bin: the x is on the row)

    private static Border? RowOf(object? source)
    {
        for (var v = source as Visual; v != null; v = v.GetVisualParent())
            if (v is Border { Classes: var c } b && c.Contains("qrow")) return b;
        return null;
    }

    private void Row_MouseDown(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && RowOf(e.Source) != null) _dragStart = e.GetPosition(null);
    }

    private async void Row_MouseMove(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _dragStart is not Point start) return;
        if (Ui.FindAncestor<Button>(e.Source as Visual) != null) return;
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - start.X) < 6 && Math.Abs(p.Y - start.Y) < 6) return;
        _dragStart = null;
        if (RowOf(e.Source) is not { DataContext: QueueRow row } fe || Vm is not { } vm) return;
        var session = new DragSession(QueueList, row);
        var data = new DataObject();
        data.Set(Rows.QueueRowFormat, row);
        var outcome = await DragVisuals.RunAsync(fe, e, data, DragDropEffects.Move, session);
        // The song lands where the line was.
        if (outcome == DragOutcome.Placed && session.Target is QueueRow target)
        {
            int last = vm.Player.UpNext.LastOrDefault()?.Index ?? row.Index;
            vm.Player.MoveUpcoming(row.Index, Math.Clamp(DragVisuals.MoveIndex(row.Index, target.Index, session.After), 0, last));
        }
    }
}
