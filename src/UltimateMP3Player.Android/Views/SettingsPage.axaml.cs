using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UltimateMP3Player.Core;
using UltimateMP3Player.Platform;
using UltimateMP3Player.ViewModels;
using Text = UltimateMP3Player.Core.Text;

namespace UltimateMP3Player.Views;

public partial class SettingsPage : UserControl, IPage
{
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };

    public SettingsPage()
    {
        InitializeComponent();
        // A short pause after the last letter: the cards are walked once per word, not at every key.
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            Search();
        };
        SearchBox.TextChanged += (_, _) =>
        {
            _searchTimer.Stop();
            if (string.IsNullOrWhiteSpace(SearchBox.Text)) Search();
            else _searchTimer.Start();
        };
        // The online search's sites: dragged by their handle into the order they're searched in.
        _ = new TouchReorder(ServicesArea, ServicesLayer,
            () => Enumerable.Range(0, ServicesList.ItemCount).Select(ServicesList.ContainerFromIndex).OfType<Control>().ToList(),
            () => ServicesArea.FindAncestorOfType<ScrollViewer>(),
            (from, onto, after) =>
            {
                if (Vm is not { } vm || vm.SearchServices is not { } sites || from >= sites.Count) return;
                vm.MoveService(sites[from].Name, TouchReorder.MoveIndex(from, onto, after));
            });
#if IOS_APP
        // The iPhone: iOS chooses the output (Control Center), the updates of the app come through SideStore (yt-dlp's
        // still from here), the music is in the Files app.
        foreach (var c in new Control[] { OutputRow, OutputLine, AutoUpdateSwitch })
            c.IsVisible = false;
        MusicDirHint.Text = L.T("In the Files app: On My iPhone › Ultimate MP3 Player › Music. If you uninstall the app, the music is deleted.");
#endif
    }

    // The logins' row follows them (only while the page is in the window: a page let go of isn't kept alive by them).
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SiteLogins.Changed -= ShowLogins;
        SiteLogins.Changed += ShowLogins;
        ShowLogins();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        SiteLogins.Changed -= ShowLogins;
    }

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    // ------------------------------------------------------------------ search

    // More words (in the app's language) that find a card, or a setting inside it.
    public static readonly AttachedProperty<string> KeywordsProperty = AvaloniaProperty.RegisterAttached<SettingsPage, Control, string>("Keywords", "");
    public static string GetKeywords(Control o) => o.GetValue(KeywordsProperty);
    public static void SetKeywords(Control o, string value) => o.SetValue(KeywordsProperty, value);

    // What the search hid, each with what gives it back its own visibility.
    private readonly Dictionary<Control, IDisposable?> _hidden = new();

    // The cards whose words match, and inside them only the settings that match: a match in the card's title keeps the
    // whole card, its keywords find it but matching settings still narrow it down. The thin lines between the settings
    // stay only between the ones left.
    private void Search()
    {
        var words = Text.Normalize(SearchBox.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var el in _hidden.Keys.ToList()) Show(el, true);
        int shown = 0;
        var children = Cards.Children.ToList();
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is not Border { Classes: var cls } card || !cls.Contains("card")) continue;
            var title = i > 0 && children[i - 1] is TextBlock { Classes: var tc } t && tc.Contains("card-title") ? t : null;
            if (words.Length == 0 || title != null && Matches(TextOf(title), words))
            {
                shown++;
                continue;
            }
            var (units, lines) = Units(card);
            var matching = units.Where(u => Matches(string.Join(" ", u.Select(c => TextOf(c) + " " + GetKeywords(c))), words)).ToList();
            if (matching.Count == 0)
            {
                if (Matches(GetKeywords(card), words))
                {
                    shown++;
                    continue;
                }
                Show(card, false);
                if (title != null) Show(title, false);
                continue;
            }
            shown++;
            foreach (var u in units.Except(matching))
                foreach (var el in u) Show(el, false);
            // A line before every setting left but the first.
            var keep = new HashSet<Control>();
            bool before = false;
            Control? line = null;
            if ((card.Child as Panel)?.Children is { } row)
                foreach (var c in row)
                {
                    if (lines.Contains(c))
                    {
                        if (before) line ??= c;
                        continue;
                    }
                    if (_hidden.ContainsKey(c)) continue;
                    if (line != null) keep.Add(line);
                    line = null;
                    before = true;
                }
            foreach (var l in lines.Where(l => !keep.Contains(l))) Show(l, false);
        }
        NoResults.IsVisible = shown == 0;
        Footer.IsVisible = words.Length == 0;
        Scroller.Offset = default;
    }

    private static bool Matches(string text, string[] words)
    {
        var t = Text.Normalize(text);
        return words.All(t.Contains);
    }

    // The settings of a card: what's between two thin lines is one (a label with its choices, a switch with its slider).
    private static (List<List<Control>> Units, List<Control> Lines) Units(Border card)
    {
        var units = new List<List<Control>>();
        var lines = new List<Control>();
        if (card.Child is not Panel root)
        {
            if (card.Child is { } only) units.Add(new List<Control> { only });
            return (units, lines);
        }
        List<Control>? unit = null;
        foreach (var c in root.Children)
        {
            if (c is Border { Classes: var cls } && cls.Contains("line"))
            {
                lines.Add(c);
                unit = null;
                continue;
            }
            if (unit == null) units.Add(unit = new List<Control>());
            unit.Add(c);
        }
        return (units, lines);
    }

    // Every text inside: names, values, hints, captions.
    private static string TextOf(ILogical? d)
    {
        if (d == null) return "";
        var parts = new List<string>();
        void Walk(ILogical x)
        {
            switch (x)
            {
                case TextBlock tb when !string.IsNullOrEmpty(tb.Text):
                    parts.Add(tb.Text);
                    break;
                case ContentControl { Content: string s }:
                    parts.Add(s);
                    break;
            }
            foreach (var child in x.LogicalChildren) Walk(child);
        }
        Walk(d);
        return string.Join(" ", parts);
    }

    // Hidden over whatever it is (a visibility that has a binding keeps it, and comes back as it was): a value above the
    // others, taken away again by the handle SetValue gives (setting UnsetValue there would only add another layer on top,
    // and the "hidden" one under it would stay).
    private void Show(Control el, bool visible)
    {
        if (visible)
        {
            if (_hidden.Remove(el, out var undo)) undo?.Dispose();
            return;
        }
        if (_hidden.ContainsKey(el)) return;
        _hidden[el] = el.SetValue(IsVisibleProperty, false, BindingPriority.Animation);
    }

    // ------------------------------------------------------------------ logins

    private void ShowLogins() => LoginsValue.Text = SiteLogins.Summary();

    private void Logins_Click(object? sender, RoutedEventArgs e) => LoginsSheet.Show();

    // (through the view model: a room is left first)
    private void Computer_Click(object? sender, RoutedEventArgs e) => Vm?.ComputerLink.Open();

    // A row with an arrow: its choices in a sheet (the computer's drop-down).
    private void Choose_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Control { Tag: string what }) return;
        // (headphones or a speaker connected since the page opened)
        if (what == "OutputDevice") vm.RefreshOutputDevices();
        (string Title, List<Choice> Items, Choice? Current, Action<Choice> Set)? c = what switch
        {
            "OutputDevice" => (L.T("Audio output"), vm.OutputDevices, vm.OutputDevice, x => vm.OutputDevice = x),
            "Appearance" => (L.T("Light or dark"), vm.Appearances, vm.Appearance, x => vm.Appearance = x),
            "Language" => (L.T("Language"), vm.Languages, vm.Language, x => vm.Language = x),
            "StartupProfile" => (L.T("At startup"), vm.StartupChoices, vm.StartupProfile, x => vm.StartupProfile = x),
            "RadioSource" => (L.T("Where similar songs come from"), vm.RadioSourceChoices, vm.RadioSource, x => vm.RadioSource = x),
            "RadioAhead" => (L.T("Similar songs prepared ahead"), vm.TogetherAheadChoices, vm.RadioAhead, x => vm.RadioAhead = x),
            "TogetherAhead" => (L.T("Songs got ready ahead when you listen"), vm.TogetherAheadChoices, vm.TogetherAhead, x => vm.TogetherAhead = x),
            "TogetherHostAhead" => (L.T("Songs got ready ahead when you're the host"), vm.TogetherAheadChoices, vm.TogetherHostAhead, x => vm.TogetherHostAhead = x),
            "SearchMode" => (L.T("How to search"), vm.SearchModes, vm.SearchMode, x => vm.SearchMode = x),
            "AudioFormat" => (L.T("Audio format"), vm.AudioFormats, vm.AudioFormat, x => vm.AudioFormat = x),
            "VideoQuality" => (L.T("Maximum video quality"), vm.VideoQualities, vm.VideoQuality, x => vm.VideoQuality = x),
            "Parallel" => (L.T("Downloads at once"), vm.ParallelChoices, vm.Parallel, x => vm.Parallel = x),
            "CacheSize" => (L.T("Temporary storage (unsaved songs)"), vm.CacheChoices, vm.CacheSize, x => vm.CacheSize = x),
            _ => null,
        };
        if (c is not { } choice) return;
        var menu = new SheetMenu { Title = choice.Title };
        foreach (var item in choice.Items)
        {
            var it = item;
            var text = string.IsNullOrEmpty(item.Hint) ? item.Label : $"{item.Label}  ·  {item.Hint}";
            menu.Add(new SheetEntry(text, Equals(item.Value, choice.Current?.Value) ? "" : "", () =>
            {
                choice.Set(it);
                // The rows show the new value (the view model only says so for some of them).
                var dc = DataContext;
                DataContext = null;
                DataContext = dc;
            }));
        }
        Menus.Open(menu);
    }

    // ------------------------------------------------------------------ equalizer

    private void Equalizer_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Equalizer is not { } eq || App.Host.View?.Sheets is not { } sheets) return;
        var body = new StackPanel { Margin = new Thickness(20, 4, 20, 18), DataContext = eq };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(new TextBlock { Text = L.T("Equalizer"), FontSize = 20, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center });
        var on = new CheckBox { Theme = Ui.Theme("Switch"), [!ToggleButton.IsCheckedProperty] = new Binding(nameof(EqualizerViewModel.Enabled)) };
        Grid.SetColumn(on, 1);
        head.Children.Add(on);
        body.Children.Add(head);

        var preset = new Button { Theme = Ui.Theme("TouchGhost"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 14, 0, 0) };
        var presetText = new TextBlock { FontWeight = FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center, [!TextBlock.TextProperty] = new Binding("Preset.Label") };
        var presetRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        presetRow.Children.Add(presetText);
        var arrow = new TextBlock { Text = Icons.Map(""), FontSize = 12, Foreground = Ui.Res("SubTextBrush") };
        arrow.Classes.Add("glyph");
        Grid.SetColumn(arrow, 1);
        presetRow.Children.Add(arrow);
        preset.Content = presetRow;
        preset[!IsEnabledProperty] = new Binding(nameof(EqualizerViewModel.Enabled));
        preset.Click += (_, _) =>
        {
            var menu = new SheetMenu { Title = L.T("Presets") };
            foreach (var p in eq.Presets)
            {
                var pp = p;
                var text = string.IsNullOrEmpty(p.Hint) ? p.Label : $"{p.Label}  ·  {p.Hint}";
                menu.Add(new SheetEntry(text, p == eq.Preset ? "" : "", () => eq.Preset = pp));
            }
            Menus.Open(menu);
        };
        body.Children.Add(preset);

        // The bands: the curve of the sound behind, the sliders, the frequencies under them.
        var area = new Grid { Height = 200, Margin = new Thickness(0, 18, 0, 0) };
        area[!IsEnabledProperty] = new Binding(nameof(EqualizerViewModel.Enabled));
        area.Children.Add(new Border { Height = 1, Background = Ui.Res("BorderStrongBrush"), VerticalAlignment = VerticalAlignment.Center });
        area.Children.Add(new EqCurve
        {
            IsHitTestVisible = false, Stroke = Ui.Res("AccentTextBrush"),
            [!EqCurve.GainsProperty] = new Binding(nameof(EqualizerViewModel.Gains)),
            [!EqCurve.VersionProperty] = new Binding(nameof(EqualizerViewModel.CurveVersion)),
        });
        var sliders = new ItemsControl
        {
            ItemsSource = eq.Bands,
            ItemsPanel = new FuncTemplate<Panel?>(() => new UniformGrid { Rows = 1 }),
            ItemTemplate = new FuncDataTemplate<EqBandViewModel>((_, _) => new Slider
            {
                Theme = Ui.Theme("EqSlider"), Height = 200, Width = 32, HorizontalAlignment = HorizontalAlignment.Center,
                [!Slider.ValueProperty] = new Binding(nameof(EqBandViewModel.Gain), BindingMode.TwoWay),
            }),
        };
        area.Children.Add(sliders);
        body.Children.Add(area);
        var labels = new ItemsControl
        {
            ItemsSource = eq.Bands, Margin = new Thickness(0, 8, 0, 0),
            ItemsPanel = new FuncTemplate<Panel?>(() => new UniformGrid { Rows = 1 }),
            ItemTemplate = new FuncDataTemplate<EqBandViewModel>((_, _) =>
            {
                var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                s.Children.Add(new TextBlock { FontSize = 11.5, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, [!TextBlock.TextProperty] = new Binding(nameof(EqBandViewModel.GainText)) });
                s.Children.Add(new TextBlock { FontSize = 10.5, Foreground = Ui.Res("MutedBrush"), HorizontalAlignment = HorizontalAlignment.Center, [!TextBlock.TextProperty] = new Binding(nameof(EqBandViewModel.Label)) });
                return s;
            }),
        };
        body.Children.Add(labels);

        var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        foreach (var (text, cmd) in new[] { (L.T("Reset"), eq.ResetCommand), (L.T("Save as preset…"), eq.SaveCommand), (L.T("Delete preset"), eq.DeleteCommand) })
            buttons.Children.Add(new Button { Theme = Ui.Theme("SmallGhost"), Content = text, Command = cmd, Margin = new Thickness(0, 0, 8, 8) });
        body.Children.Add(buttons);
        _ = sheets.Show(new ScrollViewer { Content = body });
    }

    // ------------------------------------------------------------------ bulk edit

    private void RenameArtist_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || App.Host.View?.Sheets is not { } sheets) return;
        var body = new DockPanel { Margin = new Thickness(20, 4, 20, 18), DataContext = vm };
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(new TextBlock { Text = L.T("Rename an artist"), FontSize = 20, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 8) });
        top.Children.Add(Ui.Text(L.T("Choose the artist, type the right name: it changes on all their songs."), "hint", new Thickness(0, 0, 0, 12)));
        var filter = new TextBox { Theme = Ui.Theme("FilterBox"), Watermark = L.T("Search for an artist"), [!TextBox.TextProperty] = new Binding(nameof(SettingsViewModel.ArtistFilter), BindingMode.TwoWay) };
        top.Children.Add(filter);
        body.Children.Add(top);

        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        bottom.Children.Add(Ui.Text(L.T("New name"), "field-label", new Thickness(4, 0, 0, 8)));
        bottom.Children.Add(new TextBox { Theme = Ui.Theme("TouchBox"), [!TextBox.TextProperty] = new Binding(nameof(SettingsViewModel.NewArtistName), BindingMode.TwoWay) });
        var ok = new Button { Theme = Ui.Theme("TouchPrimary"), Content = L.T("Rename"), Command = vm.RenameArtistCommand, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
        bottom.Children.Add(ok);
        body.Children.Add(bottom);

        var list = new ListBox
        {
            Background = Brushes.Transparent, MaxHeight = 300, Margin = new Thickness(0, 10, 0, 0),
            [!ItemsControl.ItemsSourceProperty] = new Binding(nameof(SettingsViewModel.Artists)),
            [!SelectingItemsControl.SelectedItemProperty] = new Binding(nameof(SettingsViewModel.SelectedArtist), BindingMode.TwoWay),
            ItemTemplate = new FuncDataTemplate<Choice>((_, _) =>
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(10, 10) };
                g.Children.Add(new TextBlock { FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis, [!TextBlock.TextProperty] = new Binding(nameof(Choice.Label)) });
                var hint = new TextBlock { FontSize = 12.5, Foreground = Ui.Res("MutedBrush"), Margin = new Thickness(10, 0, 0, 0), [!TextBlock.TextProperty] = new Binding(nameof(Choice.Hint)) };
                Grid.SetColumn(hint, 1);
                g.Children.Add(hint);
                return g;
            }),
        };
        body.Children.Add(list);
        _ = sheets.Show(body, maxHeightShare: 0.92);
    }

    // Back with a search written: the whole page again.
    // Opened to show one setting (the Spotify key, from the search's Spotify tab): it starts searched, and Back goes
    // straight back (the search emptied for next time).
    private string? _opened;

    public void Shown()
    {
        if (Vm is not { StartSearch: { } words } vm) return;
        vm.StartSearch = null;
        SearchBox.Text = _opened = words;
    }

    public bool Back()
    {
        if (string.IsNullOrEmpty(SearchBox.Text)) return false;
        bool leave = SearchBox.Text == _opened;
        _opened = null;
        SearchBox.Text = "";
        return !leave;
    }

    public void ScrollToTop() => Scroller.Offset = default;
}
