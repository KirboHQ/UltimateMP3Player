using Avalonia.Controls;
using Avalonia.Interactivity;

namespace UltimateMP3Player.Views;

public partial class HomePage : UserControl, IPage
{
    public HomePage() => InitializeComponent();

    public bool Back() => false;

    public void ScrollToTop() => Scroller.Offset = default;

    // "Show all": the library's songs.
    private void ShowAll_Click(object? sender, RoutedEventArgs e)
    {
        if (App.Host.View?.Hub is not { } hub) return;
        hub.Tab = 0;
        App.Host.Session?.Navigate(hub);
    }
}
