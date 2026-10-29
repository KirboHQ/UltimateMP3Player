using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class SearchView : UserControl
{
    // The page on screen, for Ctrl+F (a page that opens puts itself in its box).
    private static SearchView? _shown;

    // Ctrl on Windows and Linux, ⌘ on macOS.
    private static readonly KeyModifiers Command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    public SearchView()
    {
        InitializeComponent();
        // (styles in SearchView.axaml: they follow the light and dark look)
        SearchBox.GotFocus += (_, _) => SearchPill.Classes.Set("focused", true);
        SearchBox.LostFocus += (_, _) => SearchPill.Classes.Set("focused", false);
        SearchBox.AddHandler(KeyDownEvent, Search_KeyDown, RoutingStrategies.Tunnel);
        // (the caret after the words, nothing selected: a key pressed by habit, Space, doesn't wipe them)
        Loaded += (_, _) =>
        {
            _shown = this;
            SearchBox.Focus();
            SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
        };
        Unloaded += (_, _) => { if (_shown == this) _shown = null; };
    }

    // Only your songs and playlists are chosen (Ctrl or Shift + click): a heading, a card or a note clicked isn't.
    private void List_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        foreach (var item in e.AddedItems.Cast<object>().Where(i => i is not (TrackRow or PlaylistViewModel)).ToList())
            (sender as ListBox)?.SelectedItems?.Remove(item);
    }

    // The chips' lists on screen (two while one slides in): the selection bar acts on the newest one still there.
    private readonly List<ListBox> _lists = new();

    private void List_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not ListBox list) return;
        if (!_lists.Contains(list)) _lists.Add(list);
        Bar.List = list;
    }

    private void List_Unloaded(object? sender, RoutedEventArgs e)
    {
        if (sender is ListBox list) _lists.Remove(list);
        Bar.List = _lists.LastOrDefault();
    }

    private MainViewModel? Main => (DataContext as SearchViewModel)?.Main;

    public static void FocusShown()
    {
        if (_shown is not { } view) return;
        view.SearchBox.Focus();
        view.SearchBox.SelectAll();
    }

    private void Search_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Main is not { } main) return;
        if (e.Key == Key.Enter)
        {
            main.SubmitSearchCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            main.SearchText = "";
            e.Handled = true;
        }
        // A list of songs (several links, an exported playlist) opens as one card: the box would keep only its first line.
        else if (e.Key == Key.V && (e.KeyModifiers & Command) != 0)
        {
            e.Handled = true;
            _ = Paste(main);
        }
    }

    private async Task Paste(MainViewModel main)
    {
        var text = await Ui.ClipboardTextAsync();
        if (!main.PasteList(text)) SearchBox.Paste();
    }
}
