using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

    // "Add files": a menu when mods add importers (Menus.ImportMenu), else straight to the files.
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (App.Host.Session is not { } main) return;
        if (Menus.ImportMenu(main) is { } menu) Menus.Open(menu, (UIElement)sender, true);
        else _ = main.ImportDialog(false);
    }

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

    private void StorageFilter_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is LibraryViewModel vm) Menus.Open(Menus.StorageFilterMenu(vm.Storage), (UIElement)sender, true);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        RowPaging.LoadAll(List);
        List.SelectAll();
        List.Focus();
    }
}
