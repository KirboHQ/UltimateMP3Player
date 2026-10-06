using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class DownloadsView : UserControl
{
    private DownloadsPageViewModel? _vm;

    public DownloadsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as DownloadsPageViewModel);
        DetachedFromVisualTree += (_, _) => Attach(null);
        AttachedToVisualTree += (_, _) => Attach(DataContext as DownloadsPageViewModel);
        // The tags button of the analyzed link (inside its template).
        AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button { Classes: var c } b && c.Contains("tags") && b.DataContext is LinkViewModel link && App.Host.Session is { } main)
                Menus.Open(Menus.TagPicker(link.TagIds, main, link.OnTagsChosen), b, true);
        });
        // A downloaded song has the usual song menu; a double click plays it within "All songs".
        JobList.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Right || Job(e.Source) is not { Song: { } t } || Ui.FindAncestor<Border>(e.Source as Visual) is not { } row) return;
            Menus.Open(Menus.ForTrack(t, t.Main.LibraryPage), row, false);
            e.Handled = true;
        });
        JobList.DoubleTapped += (_, e) =>
        {
            if (Ui.FindAncestor<Button>(e.Source as Visual) != null || Job(e.Source) is not { Song: { } t }) return;
            t.Main.PlayInLibrary(t);
            e.Handled = true;
        };
    }

    private static DownloadJobViewModel? Job(object? source)
    {
        for (var v = source as Visual; v != null; v = v.GetVisualParent())
            if (v is Border { Classes: var c } b && c.Contains("job")) return b.DataContext as DownloadJobViewModel;
        return null;
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
        Root.RowDefinitions[1].Height = bigCard ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        Root.RowDefinitions[3].Height = bigCard ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        JobList.MaxHeight = bigCard ? 170 : double.PositiveInfinity;
    }
}
