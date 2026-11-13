using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using UltimateMP3Player.ViewModels;
using Text = UltimateMP3Player.Core.Text;

namespace UltimateMP3Player.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        MakePages();
        // Wheel over a band changes it; when the EQ is off the page scrolls.
        AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (Band(e.Source) is not { } band || e.Delta.Y == 0) return;
            band.Gain += e.Delta.Y > 0 ? 0.5 : -0.5;
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        AddHandler(DoubleTappedEvent, (_, e) =>
        {
            if (Band(e.Source) is { } band) band.Gain = 0;
        }, RoutingStrategies.Bubble, true);
        SearchBox.TextChanged += (_, _) => Apply();
        foreach (var group in Groups.Children.OfType<RadioButton>())
            group.IsCheckedChanged += (_, _) =>
            {
                if (!_applying && group.IsChecked == true && group.Tag is string key) ShowGroup(key);
            };
        Apply();
        SearchBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || string.IsNullOrEmpty(SearchBox.Text)) return;
            SearchBox.Text = "";
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
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
        // The online search's sites are dragged into their order like the songs of a playlist.
        ServiceList.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) _serviceDragStart = e.GetPosition(null);
        }, RoutingStrategies.Tunnel);
        ServiceList.AddHandler(PointerMovedEvent, Service_Moved, RoutingStrategies.Tunnel);
        ServiceList.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.Data.Contains(ServiceFormat) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        });
        ServiceList.AddHandler(DragDrop.DropEvent, (_, e) => e.Handled = e.Data.Contains(ServiceFormat));
        // Settings › Layout: the pages and Home's rows the same way, each within its own list.
        foreach (var list in new[] { MenuRowList, SectionRowList, HomeRowList })
        {
            list.AddHandler(PointerPressedEvent, (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) _serviceDragStart = e.GetPosition(null);
            }, RoutingStrategies.Tunnel);
            list.AddHandler(PointerMovedEvent, LayoutRow_Moved, RoutingStrategies.Tunnel);
            list.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            {
                e.DragEffects = e.Data.Contains(LayoutFormat) ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
            });
            list.AddHandler(DragDrop.DropEvent, (_, e) => e.Handled = e.Data.Contains(LayoutFormat));
        }
    }

    private const string ServiceFormat = "UltimateMP3Player.SearchService";
    private Point? _serviceDragStart;

    // Not from its switch: the line says where the site lands.
    private async void Service_Moved(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _serviceDragStart is not Point start) return;
        if (Ui.FindAncestor<Avalonia.Controls.Primitives.ToggleButton>(e.Source as Visual) != null || Ui.FindAncestor<Button>(e.Source as Visual) != null) return;
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - start.X) < 6 && Math.Abs(p.Y - start.Y) < 6) return;
        _serviceDragStart = null;
        if (Ui.FindAncestor<ListBoxItem>(e.Source as Visual) is not { DataContext: SearchServiceOption site } item || DataContext is not SettingsViewModel vm) return;
        var row = item.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "Row") ?? item;
        var session = new DragSession(ServiceList, site);
        var data = new DataObject();
        data.Set(ServiceFormat, site.Name);
        var outcome = await DragVisuals.RunAsync(row, e, data, DragDropEffects.Move, session);
        if (outcome == DragOutcome.Placed && session.Target is SearchServiceOption target)
            vm.MoveService(site.Name, DragVisuals.MoveIndex(site.Index, target.Index, session.After));
    }

    private const string LayoutFormat = "UltimateMP3Player.LayoutRow";

    // By its handle (not from its switch): the line says where the page or the row lands.
    private async void LayoutRow_Moved(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _serviceDragStart is not Point start || sender is not ListBox list) return;
        if (Ui.FindAncestor<Avalonia.Controls.Primitives.ToggleButton>(e.Source as Visual) != null) return;
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - start.X) < 6 && Math.Abs(p.Y - start.Y) < 6) return;
        _serviceDragStart = null;
        if (Ui.FindAncestor<ListBoxItem>(e.Source as Visual) is not { DataContext: LayoutRow row } item) return;
        var view = item.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "Row") ?? item;
        var session = new DragSession(list, row);
        var data = new DataObject();
        data.Set(LayoutFormat, row.Label);
        var outcome = await DragVisuals.RunAsync(view, e, data, DragDropEffects.Move, session);
        if (outcome == DragOutcome.Placed && session.Target is LayoutRow target)
            row.MoveTo(DragVisuals.MoveIndex(row.Index, target.Index, session.After));
    }

    private static EqBandViewModel? Band(object? source)
        => Ui.FindAncestor<Slider>(source as Visual) is { IsEnabled: true, Classes: var c } s && c.Contains("eqband") ? s.DataContext as EqBandViewModel : null;

    // ------------------------------------------------------------------ search

    // Extra words (in the app language) that find a card.
    public static readonly AttachedProperty<string> KeywordsProperty = AvaloniaProperty.RegisterAttached<SettingsView, Control, string>("Keywords", "");
    public static string GetKeywords(Control o) => o.GetValue(KeywordsProperty);
    public static void SetKeywords(Control o, string value) => o.SetValue(KeywordsProperty, value);

    // The group a card is in (the entries on the left), the one open.
    public static readonly AttachedProperty<string> GroupProperty = AvaloniaProperty.RegisterAttached<SettingsView, Control, string>("Group", "");
    public static string GetGroup(Control o) => o.GetValue(GroupProperty);
    public static void SetGroup(Control o, string value) => o.SetValue(GroupProperty, value);

    private string _group = "appearance";
    private bool _applying, _searching;
    private SettingsViewModel? _vm;

    // Every card, from the XAML's Cards: each lives in its group's page, all of them in the search's while there are words in the box.
    private readonly List<Control> _cards = new();
    private readonly Dictionary<string, ScrollViewer> _pages = new();

    // The cards of the page shown, what the search left of them.
    public IReadOnlyList<Control> AllCards => _cards;
    public IEnumerable<Control> CardsShown => ((Panel)(_searching ? Cards : _pages[_group].Content!)).Children.Where(c => c.IsVisible);

    private void MakePages()
    {
        var order = Groups.Children.OfType<RadioButton>().Select(g => (string)g.Tag!).ToList();
        _cards.AddRange(Cards.Children);
        Cards.Children.Clear();
        foreach (var key in order)
        {
            var panel = new MasonryPanel { ColumnWidth = Cards.ColumnWidth, MaxColumns = Cards.MaxColumns, MaxColumnWidth = Cards.MaxColumnWidth, Gap = Cards.Gap, Margin = ((Control)Scroller.Content!).Margin };
            panel.Children.AddRange(_cards.Where(c => GetGroup(c) == key));
            _pages[key] = new ScrollViewer { VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Focusable = false, Content = panel, Tag = order.IndexOf(key) };
        }
        // (the search's page has no place: it fades in)
        Pages.PlaceOf = page => (page as Control)?.Tag as int?;
        // A page away from the window stays bound: nothing to bind again each time it comes back, and the search reads its words.
        DataContextChanged += (_, _) =>
        {
            foreach (var page in _pages.Values.Append(Scroller)) page.DataContext = DataContext;
        };
    }

    private void Logins_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Dialogs.ShowLogins();
        _vm?.LoginsChanged();
    }

    private void Zanno_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => MainViewModel.OpenUrl("https://www.twitch.tv/zanno_tw");

    private void GroupAsked(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.StartGroup) || _vm?.StartGroup is not { } group) return;
        _vm.StartGroup = null;
        ShowGroup(group);
    }

    public void ShowGroup(string key)
    {
        _group = key;
        // (a search written: its change does it)
        if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = "";
        else Apply();
    }

    // What the search hid, each with what gives it back its own visibility.
    private readonly Dictionary<Control, IDisposable?> _hidden = new();

    // Only the cards, and inside them the settings, whose words match. A match in a card's title keeps
    // the whole card; its hidden keywords find the card but matching rows still narrow it down;
    // cards tagged "whole" never lose single rows.
    // Without words: the page of the group open. With them: the search's, every group's cards, and no group lit.
    private void Apply()
    {
        var words = Text.Normalize(SearchBox.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var el in _hidden.Keys.ToList()) Show(el, true);
        _applying = true;
        foreach (var group in Groups.Children.OfType<RadioButton>()) group.IsChecked = words.Length == 0 && group.Tag as string == _group;
        _applying = false;
        bool searching = words.Length > 0;
        if (searching != _searching)
        {
            _searching = searching;
            foreach (var card in _cards)
            {
                ((Panel)card.Parent!).Children.Remove(card);
                ((Panel)(searching ? Cards : _pages[GetGroup(card)].Content!)).Children.Add(card);
            }
        }
        var page = searching ? Scroller : _pages[_group];
        // Searched from further down: what's found starts from the top.
        page.Offset = default;
        Pages.Content = page;
        if (!searching)
        {
            NoResults.IsVisible = false;
            return;
        }
        int shown = 0;
        foreach (var card in _cards)
        {
            // (a group's name finds its cards, as a card's title does)
            if (Matches(TextOf(FirstText(card)) + " " + Groups.Children.OfType<RadioButton>().First(g => g.Tag as string == GetGroup(card)).Content, words))
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
        NoResults.IsVisible = shown == 0;
    }

    private static bool Matches(string text, string[] words)
    {
        var t = Text.Normalize(text);
        return words.All(t.Contains);
    }

    private static bool BoundVisibility(Control c) => BindingOperations.GetBindingExpressionBase(c, IsVisibleProperty) != null || BindingOperations.GetBindingExpressionBase(c, Fold.OpenProperty) != null;

    // The settings of a card: a field label goes with the control under it, an element shown
    // only in some cases (bound visibility) with the setting above it.
    private static List<List<Control>> Units(Control card)
    {
        var units = new List<List<Control>>();
        if ((card as Border)?.Child is not Panel root) return units;
        var children = root.Children.OfType<Control>().ToList();
        for (int i = 1; i < children.Count; i++)
        {
            var c = children[i];
            if (c is TextBlock { Classes: var cls } && cls.Contains("field-label") && i + 1 < children.Count)
            {
                units.Add(new List<Control> { c, children[++i] });
                continue;
            }
            if (units.Count > 0 && BoundVisibility(c)) units[^1].Add(c);
            else units.Add(new List<Control> { c });
        }
        return units;
    }

    private static TextBlock? FirstText(ILogical d)
    {
        if (d is TextBlock tb) return tb;
        foreach (var child in d.LogicalChildren)
            if (FirstText(child) is { } found) return found;
        return null;
    }

    // Every text inside: labels, hints, button and switch captions.
    private static string TextOf(ILogical? d)
    {
        if (d == null) return "";
        var parts = new List<string>();
        void Walk(ILogical x)
        {
            switch (x)
            {
                case TextBlock tb:
                    parts.Add(tb.Text ?? string.Concat(tb.Inlines?.OfType<Run>().Select(r => r.Text) ?? Enumerable.Empty<string?>()));
                    break;
                case ContentControl { Content: string s }:
                    parts.Add(s);
                    break;
                case TextBox { Watermark: string placeholder }:
                    parts.Add(placeholder);
                    break;
            }
            foreach (var child in x.LogicalChildren) Walk(child);
        }
        Walk(d);
        return string.Join(" ", parts);
    }

    // Hidden over whatever it is (a bound visibility keeps its binding and comes back as it was): a value above the others,
    // taken away again by the handle SetValue gives (setting UnsetValue there would only add another layer on top, and the
    // "hidden" one under it would stay: a card hidden once never came back).
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
}
