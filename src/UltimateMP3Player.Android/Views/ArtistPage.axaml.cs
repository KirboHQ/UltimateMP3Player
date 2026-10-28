using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class ArtistPage : UserControl, IPage
{
    private ArtistPageViewModel? _vm;

    public ArtistPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        List.Picks.ActiveChanged += () =>
        {
            bool on = List.Picks.IsActive;
            SelectBar.IsVisible = on;
            TopBar.IsVisible = !on;
            SelectActions.IsVisible = on;
        };
    }

    private void Attach()
    {
        if (_vm != null) _vm.PropertyChanged -= OnVm;
        _vm = DataContext as ArtistPageViewModel;
        if (_vm != null) _vm.PropertyChanged += OnVm;
        Fill();
    }

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ArtistPageViewModel.Rows)) Fill();
    }

    // The header (picture, buttons, shelves), then the songs: the list scrolls as one, the songs' rows are virtualized.
    private void Fill()
    {
        if (_vm == null) return;
        var items = _vm.Rows.Select(r => new SongItem(r)).ToList();
        List.Picks.SetItems(items, _vm);
        var all = new List<object> { new ListHeader(_vm) };
        all.AddRange(items);
        if (items.Count == 0) all.Add(new ListNote(L.T("None of their songs in your library yet"), L.T("Search for them online, or open one of their albums.")));
        List.ItemsSource = all;
    }

    private void Back_Click(object? sender, RoutedEventArgs e) => App.Host.Session?.BackCommand.Execute(null);

    private void More_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm != null) Menus.Open(Menus.ForArtist(_vm));
    }

    private void Select_Click(object? sender, RoutedEventArgs e)
    {
        if (List.Picks.Items.Count > 0) List.Picks.Start(null);
    }

    public bool Back()
    {
        if (!List.Picks.IsActive) return false;
        List.Picks.Stop();
        return true;
    }

    public void ScrollToTop()
    {
        if (List.FindDescendantOfType<ScrollViewer>() is { } sv) sv.Offset = default;
    }
}
