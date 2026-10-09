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
        // Opened to show one setting (a download that needs the browser's cookies): it starts searched.
        Loaded += (_, _) =>
        {
            if (DataContext is not SettingsViewModel { StartSearch: { } words } vm) return;
            vm.StartSearch = null;
            SearchBox.Text = words;
        };
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

    // ------------------------------------------------------------------ search

    // Extra words (in the app language) that find a card.
    public static readonly DependencyProperty KeywordsProperty = DependencyProperty.RegisterAttached(
        "Keywords", typeof(string), typeof(SettingsView), new PropertyMetadata(""));

    public static string GetKeywords(DependencyObject o) => (string)o.GetValue(KeywordsProperty);
    public static void SetKeywords(DependencyObject o, string value) => o.SetValue(KeywordsProperty, value);

    private readonly HashSet<FrameworkElement> _hidden = new();

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || SearchBox.Text.Length == 0) return;
        SearchBox.Text = "";
        e.Handled = true;
    }

    // Only the cards, and inside them the settings, whose words match. A match in a card's title keeps
    // the whole card; its hidden keywords find the card but matching rows still narrow it down;
    // cards tagged "whole" never lose single rows.
    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        var words = Text.Normalize(SearchBox.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var el in _hidden.ToList()) Show(el, true);
        int shown = 0;
        foreach (var card in Cards.Children.OfType<FrameworkElement>())
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
        NoResults.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Searched from further down: what's found starts from the top.
        Scroller.ScrollToTop();
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
            if (units.Count > 0 && BindingOperations.IsDataBound(c, VisibilityProperty)) units[^1].Add(c);
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
            else el.ClearValue(VisibilityProperty);
            return;
        }
        if (!_hidden.Add(el)) return;
        if (BindingOperations.IsDataBound(el, VisibilityProperty)) el.SetCurrentValue(VisibilityProperty, Visibility.Collapsed);
        else el.Visibility = Visibility.Collapsed;
    }
}
