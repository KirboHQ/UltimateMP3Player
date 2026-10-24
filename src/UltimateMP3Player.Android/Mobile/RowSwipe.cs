using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// The phones' side of a song's row pulled to the right (the strip itself: Avalonia/Views/RowSwipe.cs, linked): which rows
// the finger pulls, and the tick past the mark. MainView tells this from the swipe between pages.
public sealed partial class RowSwipe
{
    // The row of a song under the finger and what letting it go does (null: not a song's row, or nothing to queue there:
    // a suggested song, a song of the room itself, choosing songs, arranging a playlist).
    public static (Control Row, Action Run)? Find(Visual? source)
    {
        Control? row = null;
        for (var v = source; v != null && row == null; v = v.GetVisualParent())
            if (v is Control c && Touch.GetMenu(c) is "track" or "hit") row = c;
        if (row == null || Selection.Of(row) is { IsActive: true } || row.FindAncestorOfType<SongList>() is { Reordering: true }) return null;
        var run = row.DataContext switch
        {
            SongItem s => Menus.AddToQueue(s.Track, s.Row.Owner),
            TrackRow r => Menus.AddToQueue(r.Track, r.Owner),
            OnlineHitViewModel h => h.QueueAction,
            _ => null,
        };
        return run != null ? (row, run) : null;
    }

    static partial void Tick() => Haptics.Tick();
}
