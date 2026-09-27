using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class DownloadsView : UserControl
{
    private DownloadsPageViewModel? _vm;

    public DownloadsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as DownloadsPageViewModel);
        Unloaded += (_, _) => Attach(null);
        Loaded += (_, _) => Attach(DataContext as DownloadsPageViewModel);
    }

    private void Attach(DownloadsPageViewModel? vm)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnChanged;
            _vm.Queue.PropertyChanged -= OnChanged;
        }
        _vm = vm;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnChanged;
            _vm.Queue.PropertyChanged += OnChanged;
        }
        UpdateRows();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DownloadsPageViewModel.Link) or nameof(DownloadQueue.HasJobs)) UpdateRows();
    }

    // A playlist being chosen takes the height; the queue keeps a strip.
    private void UpdateRows()
    {
        bool bigCard = _vm?.Link?.IsCollection == true;
        AnalysisRow.Height = bigCard ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        QueueListRow.Height = bigCard ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        JobList.MaxHeight = bigCard ? 170 : double.PositiveInfinity;
    }

    // A downloaded song has the usual song menu.
    private void Job_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadJobViewModel { Song: { } t }) return;
        Menus.Open(Menus.ForTrack(t, t.Main.LibraryPage), (UIElement)sender, false);
        e.Handled = true;
    }

    // Double click plays it within "All songs".
    private void Job_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || (sender as FrameworkElement)?.DataContext is not DownloadJobViewModel { Song: { } t }) return;
        t.Main.PlayInLibrary(t);
        e.Handled = true;
    }
}
