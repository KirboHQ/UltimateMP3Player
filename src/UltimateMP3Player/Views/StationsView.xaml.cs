using System.Windows.Controls;
using System.Windows.Input;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class StationsView : UserControl
{
    public StationsView() => InitializeComponent();

    private void Find_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not StationsViewModel vm) return;
        if (e.Key == Key.Enter) vm.SearchCommand.Execute(null);
        else if (e.Key == Key.Escape) vm.Query = "";
        else return;
        e.Handled = true;
    }
}
