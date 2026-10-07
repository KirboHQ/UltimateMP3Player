using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using UltimateMP3Player.Core;
using Avalonia.Interactivity;

namespace UltimateMP3Player.Views;

public partial class LibraryPage : UserControl, IPage
{
    private LibraryHub? _hub;
    private bool _syncing;

    public LibraryPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        TabSongs.IsCheckedChanged += (_, _) => Pick(0, TabSongs);
        TabLists.IsCheckedChanged += (_, _) => Pick(1, TabLists);
        TabTags.IsCheckedChanged += (_, _) => Pick(2, TabTags);
    }

    private void Attach()
    {
        if (_hub != null) _hub.PropertyChanged -= OnHub;
        _hub = DataContext as LibraryHub;
        if (_hub == null) return;
        _hub.PropertyChanged += OnHub;
        Songs.DataContext = _hub.Main.LibraryPage;
        Show();
    }

    private void OnHub(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryHub.Tab)) Show();
    }

    private void Pick(int tab, RadioButton b)
    {
        if (_syncing || b.IsChecked != true || _hub == null) return;
        _hub.Tab = tab;
    }

    private void Show()
    {
        int tab = _hub?.Tab ?? 1;
        _syncing = true;
        TabSongs.IsChecked = tab == 0;
        TabLists.IsChecked = tab == 1;
        TabTags.IsChecked = tab == 2;
        _syncing = false;
        Songs.IsVisible = tab == 0;
        ListsPane.IsVisible = tab == 1;
        TagsPane.IsVisible = tab == 2;
        if (tab == 0)
        {
            _hub?.Main.LibraryPage.EnsureFresh();
            Songs.Shown();
        }
    }

    public void Shown()
    {
        if (Songs.IsVisible) Songs.Shown();
    }

    // "+": a playlist, a tag, or songs from the phone.
    private void Add_Click(object? sender, RoutedEventArgs e)
    {
        if (_hub?.Main is not { } main) return;
        var menu = new SheetMenu { Title = L.T("Aggiungi") };
        menu.Add(new(L.T("Nuova playlist…"), "", () => _ = main.NewPlaylist(null)));
        menu.Add(new(L.T("Nuovo tag…"), "", () => _ = main.NewTag()));
        menu.Line();
        menu.Add(new(L.T("Brani dal telefono…"), "", () => _ = main.ImportDialog(false)));
        menu.Add(new(L.T("Una cartella di musica…"), "", () => _ = main.ImportDialog(true)));
        menu.Add(new(L.T("Importa un pacchetto .ump…"), "", () => _ = main.PickPack()));
        Menus.Open(menu);
    }

    private void Unsorted_Tapped(object? sender, TappedEventArgs e)
    {
        if (_hub?.Main is { } main) main.Navigate(main.UnsortedPage);
    }

    public bool Back() => Songs.IsVisible && Songs.Back();

    public void ScrollToTop()
    {
        if (Songs.IsVisible) Songs.ScrollToTop();
        ListsPane.Offset = default;
        TagsPane.Offset = default;
    }
}
