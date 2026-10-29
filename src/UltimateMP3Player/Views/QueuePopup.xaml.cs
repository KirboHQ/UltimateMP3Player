using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class QueuePopup : UserControl
{
    private readonly TranslateTransform _rise = new();

    public QueuePopup()
    {
        InitializeComponent();
        RenderTransform = _rise;
        // It rises a little out of the player bar as it shows.
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible || !Ui.Animations) return;
            var time = TimeSpan.FromMilliseconds(220);
            _rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, time) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, time));
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void Row_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QueueRow row) return;
        // Several selected (Ctrl or Shift + click): the menu of all of them.
        var many = Templates.SelectionAround(sender).OfType<QueueRow>().ToList();
        Menus.Open(many.Count > 1 ? Menus.ForQueueRows(many) : Menus.ForQueue(row), (UIElement)sender, false);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ reordering, like the song view's queue (no bin: the x is on the row)

    private Point? _dragStart;

    private void Row_MouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    private void Row_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not Point start) return;
        if (e.OriginalSource is DependencyObject d && Ui.FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(d) != null) return;
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - start.X) < 6 && Math.Abs(p.Y - start.Y) < 6) return;
        _dragStart = null;
        if (sender is not FrameworkElement fe || fe.DataContext is not QueueRow row || Vm is not { } vm) return;
        var session = new DragSession(QueueList, row);
        var outcome = DragVisuals.Run(fe, new DataObject(typeof(QueueRow), row), DragDropEffects.Move, session);
        // The song lands where the line was, even if the mouse was released elsewhere.
        if (outcome == DragOutcome.Placed && session.Target is QueueRow target)
        {
            int last = vm.Player.UpNext.LastOrDefault()?.Index ?? row.Index;
            vm.Player.MoveUpcoming(row.Index, Math.Clamp(DragVisuals.MoveIndex(row.Index, target.Index, session.After), 0, last));
        }
    }

    private void Queue_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(QueueRow)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    // The move itself happens when the drag ends (Row_MouseMove).
    private void Queue_Drop(object sender, DragEventArgs e) => e.Handled = e.Data.GetDataPresent(typeof(QueueRow));
}
