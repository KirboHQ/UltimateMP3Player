using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class StatsView : UserControl
{
    public StatsView() => InitializeComponent();

    private void Filter_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not StatsViewModel vm) return;
        vm.Filter = "";
        e.Handled = true;
    }

    private void TagFilter_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is StatsViewModel vm && App.Host.Session is { } main)
            Menus.Open(Menus.TagFilterMenu(vm.TagFilter, main), (UIElement)sender, true);
    }

    // The songs never played (of the ones shown): the selection bar takes it from there.
    private void SelectNever_Click(object sender, RoutedEventArgs e)
    {
        List.UnselectAll();
        foreach (var row in List.Items.OfType<TrackRow>().Where(r => r.Track.Plays == 0)) List.SelectedItems.Add(row);
        List.Focus();
    }
}
