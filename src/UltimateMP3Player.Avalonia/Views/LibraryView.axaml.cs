using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class LibraryView : UserControl
{
    // "Add files": a menu when mods add importers (Menus.ImportMenu), else straight to the files.
    private void Import_Click(object? sender, RoutedEventArgs e)
    {
        if (App.Host.Session is not { } main || sender is not Control from) return;
        if (Menus.ImportMenu(main) is { } menu) Menus.Open(menu, from, true);
        else _ = main.ImportDialog(false);
    }

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
            RowPaging.LoadAll(List);
            List.SelectAll();
            List.Focus();
        };
    }
}
