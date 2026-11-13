#if AVALONIA
using Avalonia;
using Avalonia.Controls;
using ElementKind = Avalonia.Controls.Control;
#else
using System.Windows;
using System.Windows.Controls;
using ElementKind = System.Windows.FrameworkElement;
#endif
using System.Runtime.CompilerServices;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Views;

// Settings › Layout (Core Layout) on a panel: its children tagged with a place's keys (Tag) in the order chosen, after the others;
// a hidden one leaves the panel (its own visibility stays its bindings'). Linked into Avalonia and the phones.
public static class Rearrange
{
    private static readonly ConditionalWeakTable<Panel, Dictionary<string, ElementKind>> Known = new();
    private static readonly ConditionalWeakTable<Panel, object> Grouped = new();

    // gap: the space above each but the first (the side bar's sections). keyOf: where a child's key is when its Tag is taken
    // (the phones' tabs show their Tag as a badge).
    public static void Apply(Panel panel, string place, double gap = 0, Func<ElementKind, string?>? keyOf = null)
    {
        var keys = Layout.Keys(place);
        var known = Known.GetValue(panel, _ => new Dictionary<string, ElementKind>());
        foreach (var c in panel.Children.OfType<ElementKind>().ToList())
            if ((keyOf != null ? keyOf(c) : c.Tag as string) is { } key && keys.Contains(key))
            {
                known[key] = c;
                panel.Children.Remove(c);
            }
        bool first = true;
        foreach (var key in Layout.Arrange(place))
            if (known.TryGetValue(key, out var c))
            {
                if (gap > 0) c.Margin = new Thickness(0, first ? 0 : gap, 0, 0);
                first = false;
                panel.Children.Add(c);
            }
    }

    // Each child tagged with a key of the place and the ones after it (a heading and what it shows) into a panel of their own, once:
    // they move together.
    public static void Group(Panel panel, string place)
    {
        if (Grouped.TryGetValue(panel, out _)) return;
        Grouped.Add(panel, place);
        var keys = Layout.Keys(place);
        StackPanel? group = null;
        foreach (var c in panel.Children.OfType<ElementKind>().ToList())
        {
            if (c.Tag is string key && keys.Contains(key))
            {
                group = new StackPanel { Tag = key };
                c.Tag = null;
                panel.Children.Insert(panel.Children.IndexOf(c), group);
            }
            if (group == null) continue;
            panel.Children.Remove(c);
            group.Children.Add(c);
        }
    }
}
