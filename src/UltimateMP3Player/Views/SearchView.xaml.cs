using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class SearchView : UserControl
{
    private Point? _dragStart;

    public SearchView() => InitializeComponent();

    private void Hit_MouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    // A song found online pulled to the right goes into the queue, as the library's rows (RowSwipe). What the button shows
    // takes the mouse: the button lets go of it and doesn't open the song when it's released.
    private void Hit_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not Point start) return;
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - start.X) < 6 && Math.Abs(p.Y - start.Y) < 6) return;
        _dragStart = null;
        if (p.X - start.X > 1.5 * Math.Abs(p.Y - start.Y) && sender is Button { Content: FrameworkElement row }) RowSwipe.StartDrag(row, p);
    }

    private void Hit_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OnlineHitViewModel h } fe || Menus.ForHit(h) is not { } menu) return;
        Menus.Open(menu, fe, false);
        e.Handled = true;
    }
}
