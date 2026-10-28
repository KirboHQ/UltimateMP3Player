using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class ArtistsView : UserControl
{
    public ArtistsView()
    {
        InitializeComponent();
        FindBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (DataContext is not ArtistsViewModel vm) return;
            if (e.Key == Key.Enter) vm.FindCommand.Execute(null);
            else if (e.Key == Key.Escape) vm.Query = "";
            else return;
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }
}
