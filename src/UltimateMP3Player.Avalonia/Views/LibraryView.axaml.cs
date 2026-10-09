using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        FilterBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || DataContext is not LibraryViewModel vm) return;
            vm.Filter = "";
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        TagFilterButton.Click += (_, _) =>
        {
            if (DataContext is LibraryViewModel vm && App.Host.Session is { } main)
                Menus.Open(Menus.TagFilterMenu(vm.TagFilter, main), TagFilterButton, true);
        };
        StorageFilterButton.Click += (_, _) =>
        {
            if (DataContext is LibraryViewModel vm) Menus.Open(Menus.StorageFilterMenu(vm.Storage), StorageFilterButton, true);
        };
        SelectAllButton.Click += (_, _) =>
        {
            List.SelectAll();
            List.Focus();
        };
    }
}
