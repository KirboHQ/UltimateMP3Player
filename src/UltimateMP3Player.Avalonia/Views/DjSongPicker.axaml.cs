using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// "Carica" on a deck: the songs with cover, BPM and length, from the library, a playlist or a tag; the chosen one
// goes on deck A or B (the deck the button belongs to is the default).
public partial class DjSongPicker : UserControl
{
    public sealed record Result(DjDeckViewModel Deck, TrackViewModel? Track = null, string? File = null, DjDeckViewModel? FromDeck = null);

    private readonly DjViewModel _dj = null!;
    private readonly DjDeckViewModel _deck = null!;
    private List<TrackViewModel> _songs = new();
    private DialogWindow? _win;
    private Result? _result;
    private readonly List<Button> _deckButtons = new();

    public DjSongPicker() => InitializeComponent();

    private DjSongPicker(DjViewModel dj, DjDeckViewModel deck) : this()
    {
        _dj = dj;
        _deck = deck;
        var main = dj.Main;
        var sources = new List<Choice> { new(L.T("Tutti i brani"), "all") };
        sources.AddRange(main.Playlists.Select(p => new Choice(p.Name, p, L.T("playlist"))));
        sources.AddRange(main.Tags.Select(t => new Choice("#" + t.Name, t, L.T("tag"))));
        Source.SelectionChanged += (_, _) => SourceChanged();
        Source.ItemsSource = sources;
        Source.SelectedIndex = 0;
        Hint.Text = L.F("Doppio clic o Invio: sul deck {0}. Oppure scegli il deck con i pulsanti qui sotto.", deck.Name);
        Filter.TextChanged += (_, _) => ApplyFilter();
        Filter.AddHandler(KeyDownEvent, FilterKeyDown, RoutingStrategies.Tunnel);
        List.SelectionChanged += (_, _) =>
        {
            foreach (var b in _deckButtons) b.IsEnabled = List.SelectedItem != null;
        };
        List.DoubleTapped += (_, e) =>
        {
            if (Ui.FindAncestor<ListBoxItem>(e.Source as Visual) != null) LoadSelected(_deck);
        };

        void Quick(string glyph, string text, Action pick)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = new TextBlock { Text = Icons.Map(glyph), FontSize = 12 };
            icon.Classes.Add("glyph");
            content.Children.Add(icon);
            content.Children.Add(new TextBlock { Text = text, Margin = new Thickness(7, 0, 0, 0), MaxWidth = 300, TextTrimming = TextTrimming.CharacterEllipsis });
            var b = new Button { Theme = Ui.Theme("ToolbarButton"), Content = content };
            b.Click += (_, _) => pick();
            this.Quick.Children.Add(b);
        }
        if (main.Player.Current is { } cur && File.Exists(cur.T.Path) && dj.Decks.All(d => d.Track != cur.T))
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
        win.Opened += (_, _) => picker.Filter.Focus();
        win.ShowDialog();
        return picker._result;
    }

    private static IBrush Lighter(Color c)
        => new SolidColorBrush(Color.FromRgb((byte)(c.R + (255 - c.R) * 0.18), (byte)(c.G + (255 - c.G) * 0.18), (byte)(c.B + (255 - c.B) * 0.18))).ToImmutable();

    private void SourceChanged()
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

    private void ApplyFilter()
    {
        var q = new TrackQuery(Filter.Text ?? "");
        var rows = _songs.Where(q.Matches).ToList();
        List.ItemsSource = rows;
        Empty.IsVisible = rows.Count == 0;
        // The song already on this deck starts selected, so you see where you are.
        var onDeck = rows.FirstOrDefault(r => r.T == _deck.Track);
        if (onDeck != null)
        {
            List.SelectedItem = onDeck;
            Dispatcher.UIThread.Post(() => List.ScrollIntoView(onDeck), DispatcherPriority.Loaded);
        }
    }

    // Down from the search box goes into the list; Enter loads the selected (or first) song.
    private void FilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (List.ItemCount == 0) return;
        if (e.Key == Key.Down)
        {
            if (List.SelectedIndex < 0) List.SelectedIndex = 0;
            List.ContainerFromIndex(List.SelectedIndex)?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (List.SelectedIndex < 0) List.SelectedIndex = 0;
            LoadSelected(_deck);
            e.Handled = true;
        }
    }

    private void LoadSelected(DjDeckViewModel deck)
    {
        if (List.SelectedItem is TrackViewModel t) Close(new Result(deck, t));
    }

    private void PickFile()
    {
        if (Dialogs.PickFile(L.T("Scegli un file audio"), (L.T("Audio e video"), Importer.Extensions)) is { } file)
            Close(new Result(_deck, File: file));
    }

    private void Close(Result r)
    {
        _result = r;
        if (_win != null) _win.DialogResult = true;
    }
}
