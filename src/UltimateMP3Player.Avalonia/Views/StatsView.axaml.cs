using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class StatsView : UserControl
{
    private StatsViewModel? _vm;

    public StatsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.RowsReplacing -= KeepView;
            _vm = DataContext as StatsViewModel;
            if (_vm != null) _vm.RowsReplacing += KeepView;
        };
        FilterBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || DataContext is not StatsViewModel vm) return;
            vm.Filter = "";
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        TagFilterButton.Click += (_, _) =>
        {
            if (DataContext is StatsViewModel vm && App.Host.Session is { } main)
                Menus.Open(Menus.TagFilterMenu(vm.TagFilter, main), TagFilterButton, true);
        };
        NeverButton.Click += (_, _) => SelectNever();
    }

    // The songs never played (of the ones the search and the tags let through): the list shows just them,
    // all selected, and the selection bar takes it from there.
    private void SelectNever()
    {
        if (DataContext is not StatsViewModel vm) return;
        vm.ShowNever();
        Dispatcher.UIThread.Post(() =>
        {
            List.UnselectAll();
            foreach (var row in List.Items.OfType<TrackRow>().Where(r => r.Track.Plays == 0)) List.SelectedItems?.Add(row);
            List.Focus();
        }, DispatcherPriority.Loaded);
    }

    // The numbers changed while the page is open and the list is sorted again: what was selected stays selected
    // and the list stays where it was scrolled.
    private void KeepView()
    {
        var selected = (List.SelectedItems?.OfType<TrackRow>() ?? Enumerable.Empty<TrackRow>()).Select(r => r.Track).ToHashSet();
        var scroller = List.FindDescendantOfType<ScrollViewer>();
        var offset = scroller?.Offset ?? default;
        Dispatcher.UIThread.Post(() =>
        {
            if (selected.Count > 0)
                foreach (var row in List.Items.OfType<TrackRow>().Where(r => selected.Contains(r.Track))) List.SelectedItems?.Add(row);
            if (scroller != null) scroller.Offset = offset;
        }, DispatcherPriority.Loaded);
    }
}
