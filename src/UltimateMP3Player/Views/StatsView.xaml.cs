using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class StatsView : UserControl
{
    public StatsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is StatsViewModel old) old.RowsReplacing -= KeepView;
            if (e.NewValue is StatsViewModel vm) vm.RowsReplacing += KeepView;
        };
    }

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

    // The songs never played (of the ones the search and the tags let through): the list shows just them,
    // all selected, and the selection bar takes it from there.
    private void SelectNever_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not StatsViewModel vm) return;
        vm.ShowNever();
        Dispatcher.BeginInvoke(() =>
        {
            List.UnselectAll();
            foreach (var row in List.Items.OfType<TrackRow>().Where(r => r.Track.Plays == 0)) List.SelectedItems.Add(row);
            List.Focus();
        }, DispatcherPriority.Loaded);
    }

    // The numbers changed while the page is open and the list is sorted again: what was selected stays selected
    // and the list stays where it was scrolled.
    private void KeepView()
    {
        var selected = List.SelectedItems.OfType<TrackRow>().Select(r => r.Track).ToHashSet();
        var scroller = FindScroller(List);
        double offset = scroller?.VerticalOffset ?? 0;
        Dispatcher.BeginInvoke(() =>
        {
            if (selected.Count > 0)
                foreach (var row in List.Items.OfType<TrackRow>().Where(r => selected.Contains(r.Track))) List.SelectedItems.Add(row);
            scroller?.ScrollToVerticalOffset(offset);
        }, DispatcherPriority.Loaded);
    }

    private static ScrollViewer? FindScroller(DependencyObject d)
    {
        if (d is ScrollViewer s) return s;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindScroller(VisualTreeHelper.GetChild(d, i)) is { } found) return found;
        return null;
    }
}
