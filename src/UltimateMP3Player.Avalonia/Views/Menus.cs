using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.VisualTree;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// Context menus, built on open so they list the current playlists.
public static partial class Menus
{
    public static void Open(ContextMenu menu, Control target, bool below)
    {
        menu.PlacementTarget = target;
        menu.Placement = below ? PlacementMode.BottomEdgeAlignedLeft : PlacementMode.Pointer;
        menu.Open(target);
    }

    // The "…" of a song row (a command, the templates have no code behind): its menu under the button.
    public static readonly System.Windows.Input.ICommand MoreCommand = new RelayCommand(p =>
    {
        if (p is Control c && c.DataContext is TrackRow row) Open(MenuFor(row, c), c, true);
    });

    // A selected row acts on the whole selection.
    public static ContextMenu MenuFor(TrackRow row, Control source)
    {
        var item = source.FindAncestorOfType<ListBoxItem>(true);
        if (item != null && item.FindAncestorOfType<ListBox>() is { } list)
        {
            if (item.IsSelected && list.SelectedItems?.Count > 1)
                return ForSelection(SelectionBar.Selected(list).Select(r => r.Track).ToList(), row.Owner);
            if (!item.IsSelected)
            {
                list.UnselectAll();
                item.IsSelected = true;
            }
        }
        return ForTrack(row.Track, row.Owner);
    }

    private static MenuItem Item(string header, string glyph, Action click, bool enabled = true)
    {
        var m = new MenuItem { Header = header, IsEnabled = enabled };
        Ui.SetGlyph(m, glyph);
        m.Click += (_, _) => click();
        return m;
    }

    private static void Tip(Control c, string? text, bool onDisabled = false)
    {
        ToolTip.SetTip(c, text);
        if (onDisabled) ToolTip.SetShowOnDisabled(c, true);
    }

    // Right click on an item of a list where several are selected: all of them (a click on one not selected selects only it).
    public static List<object> SelectionAround(Control c)
    {
        if (c.FindAncestorOfType<ListBoxItem>(true) is not { } item || item.FindAncestorOfType<ListBox>() is not { } list) return new List<object>();
        if (item.IsSelected && list.SelectedItems?.Count > 1) return SelectionBar.SelectedItems(list);
        if (!item.IsSelected && list.SelectedItems?.Count > 0) list.UnselectAll();
        return new List<object>();
    }
}
