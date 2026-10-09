using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// "Carica" on a deck: the songs with cover, BPM and length, from the library, a playlist or a tag; the chosen one
// goes on deck A or B (the deck the button belongs to is the default).
public partial class DjSongPicker : UserControl
{
    public sealed record Result(DjDeckViewModel Deck, TrackViewModel? Track = null, string? File = null, DjDeckViewModel? FromDeck = null);

    private readonly DjViewModel _dj;
    private readonly DjDeckViewModel _deck;
    private List<TrackViewModel> _songs = new();
    private Window? _win;
    private Result? _result;
    private readonly List<Button> _deckButtons = new();

    private DjSongPicker(DjViewModel dj, DjDeckViewModel deck)
    {
        _dj = dj;
        _deck = deck;
        InitializeComponent();
        var main = dj.Main;
        var sources = new List<Choice> { new(L.T("Tutti i brani"), "all") };
        sources.AddRange(main.Playlists.Select(p => new Choice(p.Name, p, L.T("playlist"))));
        sources.AddRange(main.Tags.Select(t => new Choice("#" + t.Name, t, L.T("tag"))));
        Source.ItemsSource = sources;
        Source.SelectedIndex = 0;
        Hint.Text = L.F("Doppio clic o Invio: sul deck {0}. Oppure scegli il deck con i pulsanti qui sotto.", deck.Name);

        void Quick(string glyph, string text, Action pick)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), FontSize = 12 });
            content.Children.Add(new TextBlock { Text = text, Margin = new Thickness(7, 0, 0, 0), MaxWidth = 300, TextTrimming = TextTrimming.CharacterEllipsis });
            var b = new Button { Style = (Style)FindResource("ToolbarButton"), Content = content };
            b.Click += (_, _) => pick();
            this.Quick.Children.Add(b);
        }
        if (main.Player.Current is { } cur && (cur.T.AudioPath != null || cur.T.HasLink) && dj.Decks.All(d => d.Track != cur.T))
            Quick("", L.F("In riproduzione: {0}", cur.Title), () => Close(new Result(deck, cur)));
        var other = deck.Other;
        if (other.HasTrack && other.FilePath != null)
            Quick("", L.F("Come il deck {0}: {1}", other.Name, other.Title), () => Close(new Result(deck, FromDeck: other)));
        Quick("", L.T("Un file dal computer…"), PickFile);
    }

    public static Result? Show(DjViewModel dj, DjDeckViewModel deck)
    {
        var picker = new DjSongPicker(dj, deck);
        var buttons = new List<Button> { Dialogs.Button(L.T("Annulla"), "GhostButton", isCancel: true) };
        foreach (var d in dj.Decks)
        {
            var b = Dialogs.Button(L.F("Carica sul deck {0}", d.Name), "PrimaryButton", isDefault: d == deck);
            b.Background = d.Brush;
            Ui.SetHoverBrush(b, Lighter(d.Color));
            b.IsEnabled = false;
            var target = d;
            b.Click += (_, _) => picker.LoadSelected(target);
            picker._deckButtons.Add(b);
            buttons.Add(b);
        }
        var win = Dialogs.Frame(L.T("Scegli il brano"), picker, 780, buttons.ToArray());
        picker._win = win;
        win.Loaded += (_, _) => picker.Filter.Focus();
        win.ShowDialog();
        return picker._result;
    }

    private static Brush Lighter(Color c)
    {
        var b = new SolidColorBrush(Color.FromRgb((byte)(c.R + (255 - c.R) * 0.18), (byte)(c.G + (255 - c.G) * 0.18), (byte)(c.B + (255 - c.B) * 0.18)));
        b.Freeze();
        return b;
    }

    private void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        var main = _dj.Main;
        IEnumerable<TrackViewModel> songs = (Source.SelectedItem as Choice)?.Value switch
        {
            PlaylistViewModel p => p.P.Tracks.Select(main.Library.Get).OfType<Track>().Select(main.Vm),
            TagViewModel t => main.AllVms().Where(v => v.TagIds.Contains(t.Id)).OrderBy(v => v.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => main.AllVms().OrderBy(v => v.Title, StringComparer.CurrentCultureIgnoreCase),
        };
        _songs = songs.ToList();
        ApplyFilter();
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (List == null) return;
        var q = new TrackQuery(Filter.Text);
        var rows = _songs.Where(q.Matches).ToList();
        List.ItemsSource = rows;
        Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // The song already on this deck starts selected, so you see where you are.
        var onDeck = rows.FirstOrDefault(r => r.T == _deck.Track);
        if (onDeck != null)
        {
            List.SelectedItem = onDeck;
            List.ScrollIntoView(onDeck);
        }
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var b in _deckButtons) b.IsEnabled = List.SelectedItem != null;
    }

    // Down from the search box goes into the list; Enter loads the selected (or first) song.
    private void Filter_KeyDown(object sender, KeyEventArgs e)
    {
        if (List.Items.Count == 0) return;
        if (e.Key == Key.Down)
        {
            if (List.SelectedIndex < 0) List.SelectedIndex = 0;
            (List.ItemContainerGenerator.ContainerFromIndex(List.SelectedIndex) as ListBoxItem)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (List.SelectedIndex < 0) List.SelectedIndex = 0;
            LoadSelected(_deck);
            e.Handled = true;
        }
    }

    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && Ui.FindAncestor<ListBoxItem>(d) != null) LoadSelected(_deck);
    }

    private void LoadSelected(DjDeckViewModel deck)
    {
        if (List.SelectedItem is TrackViewModel t) Close(new Result(deck, t));
    }

    private void PickFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("Scegli un file audio"),
            Filter = L.T("Audio e video") + "|" + string.Join(";", Importer.Extensions.Select(x => "*" + x)) + "|" + L.T("Tutti i file") + "|*.*",
        };
        if (dlg.ShowDialog(_win) == true) Close(new Result(_deck, File: dlg.FileName));
    }

    private void Close(Result r)
    {
        _result = r;
        if (_win != null) _win.DialogResult = true;
    }
}
