using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        MakePages();
        Apply();
        // Opened to show one setting (a download that needs the browser's cookies): it starts searched.
        Loaded += (_, _) =>
        {
            if (DataContext is not SettingsViewModel vm) return;
            _vm = vm;
            vm.PropertyChanged += GroupAsked;
            GroupAsked(vm, new(nameof(SettingsViewModel.StartGroup)));
            if (vm.StartSearch is not { } words) return;
            vm.StartSearch = null;
            SearchBox.Text = words;
        };
        Unloaded += (_, _) =>
        {
            if (_vm != null) _vm.PropertyChanged -= GroupAsked;
        };
    }

    private SettingsViewModel? _vm;

    private void Logins_Click(object sender, RoutedEventArgs e)
    {
        Dialogs.ShowLogins();
        _vm?.LoginsChanged();
    }

    private void Zanno_Click(object sender, RoutedEventArgs e) => MainViewModel.OpenUrl("https://www.twitch.tv/zanno_tw");

    private void GroupAsked(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.StartGroup) || _vm?.StartGroup is not { } group) return;
        _vm.StartGroup = null;
        ShowGroup(group);
    }

    // Wheel over a band changes it; when the EQ is off the page scrolls.
    private void Eq_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Slider { IsEnabled: true } s || s.DataContext is not EqBandViewModel band) return;
        band.Gain += e.Delta > 0 ? 0.5 : -0.5;
        e.Handled = true;
    }

    private void Eq_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider { IsEnabled: true } s && s.DataContext is EqBandViewModel band) band.Gain = 0;
    }

    // ------------------------------------------------------------------ the online search's sites, in their order

    private Point? _serviceDragStart;

    private void Service_MouseDown(object sender, MouseButtonEventArgs e) => _serviceDragStart = e.GetPosition(null);

    // Dragged like the songs of a playlist: the line says where the site lands (not from its switch).
    private void Service_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _serviceDragStart is not Point start) return;
        if (e.OriginalSource is DependencyObject d && Ui.FindAncestor<ButtonBase>(d) != null) return;
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - start.X) < 6 && Math.Abs(p.Y - start.Y) < 6) return;
        _serviceDragStart = null;
        if (sender is not FrameworkElement fe || fe.DataContext is not SearchServiceOption site || DataContext is not SettingsViewModel vm) return;
        var session = new DragSession(ServiceList, site);
        var outcome = DragVisuals.Run(fe, new DataObject(typeof(SearchServiceOption), site), DragDropEffects.Move, session);
        if (outcome == DragOutcome.Placed && session.Target is SearchServiceOption target)
            vm.MoveService(site.Name, DragVisuals.MoveIndex(site.Index, target.Index, session.After));
    }

    private void Services_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(SearchServiceOption)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    // ------------------------------------------------------------------ search

    // Extra words (in the app language) that find a card.
    public static readonly DependencyProperty KeywordsProperty = DependencyProperty.RegisterAttached(
        "Keywords", typeof(string), typeof(SettingsView), new PropertyMetadata(""));

    public static string GetKeywords(DependencyObject o) => (string)o.GetValue(KeywordsProperty);
    public static void SetKeywords(DependencyObject o, string value) => o.SetValue(KeywordsProperty, value);

    private readonly HashSet<FrameworkElement> _hidden = new();

    // The group a card is in (the entries on the left), the one open.
    public static readonly DependencyProperty GroupProperty = DependencyProperty.RegisterAttached(
        "Group", typeof(string), typeof(SettingsView), new PropertyMetadata(""));

    public static string GetGroup(DependencyObject o) => (string)o.GetValue(GroupProperty);
    public static void SetGroup(DependencyObject o, string value) => o.SetValue(GroupProperty, value);

    private string _group = "appearance";
    private bool _applying, _searching;

    // Every card, from the XAML's Cards: each lives in its group's page, all of them in the search's while there are words in the box.
    private readonly List<FrameworkElement> _cards = new();
    private readonly Dictionary<string, ScrollViewer> _pages = new();

    // The cards of the page shown, what the search left of them.
    public IReadOnlyList<FrameworkElement> AllCards => _cards;
    public IEnumerable<FrameworkElement> CardsShown => ((Panel)(_searching ? Cards : _pages[_group].Content)).Children.OfType<FrameworkElement>().Where(c => c.Visibility == Visibility.Visible);

    private void MakePages()
    {
        var order = Groups.Children.OfType<RadioButton>().Select(g => (string)g.Tag).ToList();
        _cards.AddRange(Cards.Children.OfType<FrameworkElement>());
        Cards.Children.Clear();
        foreach (var key in order)
        {
            var panel = new MasonryPanel { ColumnWidth = Cards.ColumnWidth, MaxColumns = Cards.MaxColumns, MaxColumnWidth = Cards.MaxColumnWidth, Gap = Cards.Gap, Margin = ((FrameworkElement)Scroller.Content).Margin };
            foreach (var card in _cards.Where(c => GetGroup(c) == key)) panel.Children.Add(card);
            _pages[key] = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Background = System.Windows.Media.Brushes.Transparent, Content = panel, Tag = order.IndexOf(key) };
        }
        // (the search's page has no place: it fades in)
        Pages.PlaceOf = page => (page as FrameworkElement)?.Tag as int?;
        // A page away from the window stays bound: nothing to bind again each time it comes back, and the search reads its words.
        DataContextChanged += (_, e) =>
        {
            foreach (var page in _pages.Values.Append(Scroller)) page.DataContext = e.NewValue;
        };
    }

    private void Group_Checked(object sender, RoutedEventArgs e)
    {
        if (!_applying && Cards != null && sender is FrameworkElement { Tag: string key }) ShowGroup(key);
    }

    public void ShowGroup(string key)
    {
        _group = key;
        // (a search written: its change does it)
        if (SearchBox.Text.Length > 0) SearchBox.Text = "";
        else Apply();
    }

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || SearchBox.Text.Length == 0) return;
        SearchBox.Text = "";
        e.Handled = true;
    }

    // Only the cards, and inside them the settings, whose words match. A match in a card's title keeps
    // the whole card; its hidden keywords find the card but matching rows still narrow it down;
    // cards tagged "whole" never lose single rows.
    private void Search_TextChanged(object sender, TextChangedEventArgs e) => Apply();

    // Without words: the page of the group open. With them: the search's, every group's cards, and no group lit.
    private void Apply()
    {
        var words = Text.Normalize(SearchBox.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var el in _hidden.ToList()) Show(el, true);
        _applying = true;
        foreach (var group in Groups.Children.OfType<RadioButton>()) group.IsChecked = words.Length == 0 && (string)group.Tag == _group;
        _applying = false;
        bool searching = words.Length > 0;
        if (searching != _searching)
        {
            _searching = searching;
            foreach (var card in _cards)
            {
                ((Panel)card.Parent).Children.Remove(card);
                ((Panel)(searching ? Cards : _pages[GetGroup(card)].Content)).Children.Add(card);
            }
        }
        var page = searching ? Scroller : _pages[_group];
        // Searched from further down: what's found starts from the top.
        page.ScrollToTop();
        Pages.Content = page;
        if (!searching)
        {
            NoResults.Visibility = Visibility.Collapsed;
            return;
        }
        int shown = 0;
        foreach (var card in _cards)
        {
            // (a group's name finds its cards, as a card's title does)
            if (Matches(TextOf(FirstText(card)) + " " + Groups.Children.OfType<RadioButton>().First(g => (string)g.Tag == GetGroup(card)).Content, words))
            {
                shown++;
                continue;
            }
            var units = Units(card);
            // (a part of a card that holds several has its own hidden words)
            var matching = units.Where(u => Matches(string.Join(" ", u.Select(el => TextOf(el) + " " + GetKeywords(el))), words)).ToList();
            if (matching.Count == 0 && !Matches(TextOf(card) + " " + GetKeywords(card), words))
            {
                Show(card, false);
                continue;
            }
            shown++;
            if (matching.Count == 0 || card.Tag as string == "whole") continue;
            foreach (var u in units.Except(matching))
                foreach (var el in u) Show(el, false);
        }
        NoResults.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool Matches(string text, string[] words)
    {
        var t = Text.Normalize(text);
        return words.All(t.Contains);
    }

    // The settings of a card: a field label goes with the control under it, an element shown
    // only in some cases (bound visibility) with the setting above it.
    private static List<List<FrameworkElement>> Units(FrameworkElement card)
    {
        var units = new List<List<FrameworkElement>>();
        if ((card as Border)?.Child is not Panel root) return units;
        var children = root.Children.OfType<FrameworkElement>().ToList();
        for (int i = 1; i < children.Count; i++)
        {
            var c = children[i];
            if (c is TextBlock { Style: var s } && s == (Style)Application.Current.Resources["FieldLabel"] && i + 1 < children.Count)
            {
                units.Add(new List<FrameworkElement> { c, children[++i] });
                continue;
            }
            if (units.Count > 0 && (BindingOperations.IsDataBound(c, VisibilityProperty) || BindingOperations.IsDataBound(c, Fold.OpenProperty))) units[^1].Add(c);
            else units.Add(new List<FrameworkElement> { c });
        }
        return units;
    }

    private static TextBlock? FirstText(DependencyObject d)
    {
        if (d is TextBlock tb) return tb;
        foreach (var child in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
            if (FirstText(child) is { } found) return found;
        return null;
    }

    // Every text inside: labels, hints, button and switch captions.
    private static string TextOf(DependencyObject? d)
    {
        if (d == null) return "";
        var parts = new List<string>();
        void Walk(DependencyObject x)
        {
            switch (x)
            {
                case TextBlock tb:
                    parts.Add(tb.Text);
                    break;
                case ContentControl { Content: string s }:
                    parts.Add(s);
                    break;
                case TextBox { Tag: string placeholder }:
                    parts.Add(placeholder);
                    break;
            }
            foreach (var child in LogicalTreeHelper.GetChildren(x).OfType<DependencyObject>()) Walk(child);
        }
        Walk(d);
        return string.Join(" ", parts);
    }

    // Hidden without losing bindings: a bound visibility gets its value back from the binding.
    private void Show(FrameworkElement el, bool visible)
    {
        if (visible)
        {
            if (!_hidden.Remove(el)) return;
            if (BindingOperations.GetBindingExpressionBase(el, VisibilityProperty) is { } b) b.UpdateTarget();
            else if (BindingOperations.IsDataBound(el, Fold.OpenProperty)) el.Visibility = Fold.GetOpen(el) ? Visibility.Visible : Visibility.Collapsed;
            else el.ClearValue(VisibilityProperty);
            return;
        }
        if (!_hidden.Add(el)) return;
        if (BindingOperations.IsDataBound(el, VisibilityProperty)) el.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
        else el.Visibility = Visibility.Collapsed;
    }
}
