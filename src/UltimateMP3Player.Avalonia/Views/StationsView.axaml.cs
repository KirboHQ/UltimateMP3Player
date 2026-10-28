using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class StationsView : UserControl
{
    public StationsView()
    {
        InitializeComponent();
        FindBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (DataContext is not StationsViewModel vm) return;
            if (e.Key == Key.Enter) vm.SearchCommand.Execute(null);
            else if (e.Key == Key.Escape) vm.Query = "";
            else return;
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }
}
