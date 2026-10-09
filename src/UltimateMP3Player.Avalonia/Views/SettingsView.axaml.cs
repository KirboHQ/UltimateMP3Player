using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using UltimateMP3Player.ViewModels;
using Text = UltimateMP3Player.Core.Text;

namespace UltimateMP3Player.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
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
        SearchBox.TextChanged += (_, _) => Search();
        SearchBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || string.IsNullOrEmpty(SearchBox.Text)) return;
            SearchBox.Text = "";
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        // Opened to show one setting (a download that needs the browser's cookies): it starts searched.
        Loaded += (_, _) =>
        {
            if (DataContext is not SettingsViewModel { StartSearch: { } words } vm) return;
            vm.StartSearch = null;
            SearchBox.Text = words;
        };
    }

    private static EqBandViewModel? Band(object? source)
        => Ui.FindAncestor<Slider>(source as Visual) is { IsEnabled: true, Classes: var c } s && c.Contains("eqband") ? s.DataContext as EqBandViewModel : null;

    // ------------------------------------------------------------------ search

    // Extra words (in the app language) that find a card.
    public static readonly AttachedProperty<string> KeywordsProperty = AvaloniaProperty.RegisterAttached<SettingsView, Control, string>("Keywords", "");
    public static string GetKeywords(Control o) => o.GetValue(KeywordsProperty);
    public static void SetKeywords(Control o, string value) => o.SetValue(KeywordsProperty, value);

    // What the search hid, each with what gives it back its own visibility.
    private readonly Dictionary<Control, IDisposable?> _hidden = new();

    // Only the cards, and inside them the settings, whose words match. A match in a card's title keeps
    // the whole card; its hidden keywords find the card but matching rows still narrow it down;
    // cards tagged "whole" never lose single rows.
    private void Search()
    {
        var words = Text.Normalize(SearchBox.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var el in _hidden.Keys.ToList()) Show(el, true);
        int shown = 0;
        foreach (var card in Cards.Children.OfType<Control>())
        {
            if (words.Length == 0 || Matches(TextOf(FirstText(card)), words))
            {
                shown++;
                continue;
            }
            var units = Units(card);
            var matching = units.Where(u => Matches(string.Join(" ", u.Select(TextOf)), words)).ToList();
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
        // Searched from further down: what's found starts from the top.
        Scroller.Offset = default;
    }

    private static bool Matches(string text, string[] words)
    {
        var t = Text.Normalize(text);
        return words.All(t.Contains);
    }

    private static bool BoundVisibility(Control c) => BindingOperations.GetBindingExpressionBase(c, IsVisibleProperty) != null;

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
