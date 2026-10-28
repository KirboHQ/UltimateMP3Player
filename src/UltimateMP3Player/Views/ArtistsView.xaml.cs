using System.Windows.Controls;
using System.Windows.Input;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class ArtistsView : UserControl
{
    public ArtistsView() => InitializeComponent();

    private void Find_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ArtistsViewModel vm) return;
        if (e.Key == Key.Enter) vm.FindCommand.Execute(null);
        else if (e.Key == Key.Escape) vm.Query = "";
        else return;
        e.Handled = true;
    }
}
