using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

    private void Filter_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not LibraryViewModel vm) return;
        vm.Filter = "";
        e.Handled = true;
    }

    private void TagFilter_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is LibraryViewModel vm && App.Host.Session is { } main)
            Menus.Open(Menus.TagFilterMenu(vm.TagFilter, main), (UIElement)sender, true);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        List.SelectAll();
        List.Focus();
    }
}
