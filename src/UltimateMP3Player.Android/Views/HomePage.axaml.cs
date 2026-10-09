using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class HomePage : UserControl, IPage
{
    // Choosing several playlists, or several songs (the recently played and the recently added ones).
    public Selection Picks { get; } = new();

    private INotifyPropertyChanged? _vm;

    public HomePage()
    {
        InitializeComponent();
        Selection.SetOwner(this, Picks);
        DataContextChanged += (_, _) => Attach();
    }

    private void Attach()
    {
        if (_vm != null) _vm.PropertyChanged -= OnVm;
        _vm = DataContext as INotifyPropertyChanged;
        if (_vm != null) _vm.PropertyChanged += OnVm;
        Fill();
    }

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HomeViewModel.Recent) or nameof(HomeViewModel.RecentlyAdded) or nameof(HomeViewModel.PlaylistCards)) Fill();
    }

    // What can be chosen: the very objects the lists show.
    private void Fill()
    {
        if (DataContext is not HomeViewModel home || App.Host.Session is not { } main) return;
        Picks.SetItems(main.Playlists.Cast<object>().Concat(home.Recent).Concat(home.RecentlyAdded));
    }

    public bool Back()
    {
        if (!Picks.IsActive) return false;
        Picks.Stop();
        return true;
    }

    public void ScrollToTop() => Scroller.Offset = default;

    // "Show all": the library's songs.
    private void ShowAll_Click(object? sender, RoutedEventArgs e)
    {
        if (App.Host.View?.Hub is not { } hub) return;
        hub.Tab = 0;
        App.Host.Session?.Navigate(hub);
    }
}
