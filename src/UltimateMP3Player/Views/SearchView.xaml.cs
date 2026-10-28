using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class SearchView : UserControl
{
    // The page on screen, for Ctrl+F (a page that opens puts itself in its box).
    private static SearchView? _shown;

    public SearchView()
    {
        InitializeComponent();
        // A list of songs (several links, an exported playlist) opens as one card: the box would keep only its first line.
        DataObject.AddPastingHandler(SearchBox, (_, e) =>
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is string text && Main?.PasteList(text) == true) e.CancelCommand();
        });
        // (the caret after the words, nothing selected: a key pressed by habit, Space, doesn't wipe them)
        Loaded += (_, _) =>
        {
            _shown = this;
            SearchBox.Focus();
            SearchBox.CaretIndex = SearchBox.Text.Length;
        };
        Unloaded += (_, _) => { if (_shown == this) _shown = null; };
    }

    private MainViewModel? Main => (DataContext as SearchViewModel)?.Main;

    public static void FocusShown()
    {
        if (_shown is not { } view) return;
        view.SearchBox.Focus();
        view.SearchBox.SelectAll();
    }

    private void Search_KeyDown(object sender, KeyEventArgs e)
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
    }

    private void Search_Focus(object sender, KeyboardFocusChangedEventArgs e)
    {
        bool focused = SearchBox.IsKeyboardFocused;
        // (references: they follow the light and dark look)
        if (focused) SearchPill.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        else SearchPill.BorderBrush = Brushes.Transparent;
        SearchIcon.SetResourceReference(TextBlock.ForegroundProperty, focused ? "TextBrush" : "SubTextBrush");
    }

    // Only your songs and playlists can be selected (the selection bar acts on them): headings, shelves and the sites' rows
    // are let go at once.
    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var item in e.AddedItems)
            if (item is not (TrackRow or PlaylistViewModel)) List.SelectedItems.Remove(item);
    }

    private void Artist_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as System.Windows.FrameworkElement)?.DataContext is not ArtistCardViewModel a) return;
        Menus.Open(Menus.ForArtistCard(a), (System.Windows.UIElement)sender, false);
        e.Handled = true;
    }
}
