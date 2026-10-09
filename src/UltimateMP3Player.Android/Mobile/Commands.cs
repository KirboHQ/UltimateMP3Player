using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// Commands and converters the phone's templates use (they have no code behind).
public static class Commands
{
    // The "⋮" of a row: the menu of what the row shows (the same as a long press on it).
    public static readonly System.Windows.Input.ICommand MoreCommand = new RelayCommand(p =>
    {
        if (p is not Control c) return;
        var kind = Touch.GetMenu(c);
        if (Selection.Of(c) is { IsActive: true } sel && c.DataContext is { } item && Selection.KindOf(item) == sel.Kind && sel.Contains(item))
        {
            if (!sel.IsChecked(item)) sel.Toggle(item);
            if (Menus.ForSelection(sel) is { } many) Menus.Open(many);
            return;
        }
        if (Menus.MenuFor(kind, c.DataContext, c) is { } menu) Menus.Open(menu);
    });
}

public static class Converters
{
    // Playing: pause; otherwise play (Segoe codes, drawn by the IconConverter's font).
    public static readonly IValueConverter PlayGlyph = new FuncValueConverter<bool, string>(playing => Icons.Map(playing ? "" : ""));

    public static readonly IValueConverter Heart = new FuncValueConverter<bool, string>(fav => Icons.Map(fav ? "" : ""));

    // A Segoe code from a view model (a cloud or a warning sign) to the bundled font's.
    public static readonly IValueConverter Glyph = new FuncValueConverter<string?, string>(s => Icons.Map(s ?? ""));

    public static readonly IValueConverter HeartFont =new FuncValueConverter<bool, Avalonia.Media.FontFamily>(fav => fav ? Icons.Filled : Icons.Regular);
}

// A list of songs (a ListBox whose rows are SongItems): with its Selection, found by the rows' ticks and long presses.
public sealed class SongList : ListBox
{
    protected override Type StyleKeyOverride => typeof(ListBox);

    public SongList()
    {
        Views.Selection.SetOwner(this, Picks);
    }

    // The songs being chosen (not the ListBox's own selection, which the rows don't use: a tap plays or ticks).
    public Selection Picks { get; } = new();

    // The header (and the notes) keep rows of their own when scrolled away: a song's row taking the header's place, and
    // back, would build both from nothing each time around the top of the list.
    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        bool needs = base.NeedsContainerOverride(item, index, out recycleKey);
        if (needs && item is not SongItem && item != null) recycleKey = item.GetType();
        return needs;
    }
}
