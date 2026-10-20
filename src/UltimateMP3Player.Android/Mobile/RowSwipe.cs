using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// A song's row pulled to the right with the finger, like Spotify's: let go past a mark, the song goes into the queue (its
// menu's "Add to queue": after the songs added before it, ahead of the list's). The row follows the finger and uncovers a
// strip with the icon and the words; past the mark the strip turns to the accent colour, the icon pops and the phone ticks;
// back before the mark, nothing happens. MainView tells this from the swipe between pages.
public sealed class RowSwipe
{
    private readonly Control _row;
    private readonly Action _run;
    private readonly Canvas _layer, _clip;
    private readonly Border _strip, _fill;
    private readonly TextBlock _icon, _label;
    private readonly StackPanel _mark;
    private readonly TranslateTransform _shift = new();
    private readonly ScaleTransform _pop = new();
    private readonly double _trigger;
    private bool _armed;
    private int _fadeRun, _popRun;
    private double _x;

    // The row of a song under the finger and what letting it go does (null: not a song's row, or nothing to queue there:
    // a suggested song, a song of the room itself, choosing songs, arranging a playlist).
    public static (Control Row, Action Run)? Find(Visual? source)
    {
        Control? row = null;
        for (var v = source; v != null && row == null; v = v.GetVisualParent())
            if (v is Control c && Touch.GetMenu(c) == "track") row = c;
        if (row == null || Selection.Of(row) is { IsActive: true } || row.FindAncestorOfType<SongList>() is { Reordering: true }) return null;
        var run = row.DataContext switch
        {
            SongItem s => Menus.AddToQueue(s.Track, s.Row.Owner),
            TrackRow r => Menus.AddToQueue(r.Track, r.Owner),
            _ => null,
        };
        return run != null ? (row, run) : null;
    }

    // layer: a Canvas over the pages, not hit-testable (the strip is drawn there, in the room the row leaves).
    public RowSwipe(Control row, Action run, Canvas layer)
    {
        _row = row;
        _run = run;
        _layer = layer;
        var size = row.Bounds.Size;
        _trigger = Math.Clamp(size.Width * 0.3, 88, 150);
        // Only inside the list's visible part: a row half under the bar on top doesn't draw its strip over the bar.
        var view = row.FindAncestorOfType<ScrollViewer>() is { } sv && sv.TranslatePoint(default, layer) is { } svAt
            ? new Rect(svAt, sv.Bounds.Size) : new Rect(layer.Bounds.Size);
        var at = row.TranslatePoint(default, layer) ?? default;
        _fill = new Border { Background = Ui.Res("AccentBrush"), Opacity = 0 };
        _icon = new TextBlock { Text = Icons.Map("\uE8FD"), FontSize = 20, VerticalAlignment = VerticalAlignment.Center, RenderTransform = _pop };
        _icon.Classes.Add("glyph");
        _label = new TextBlock
        {
            Text = L.T("Add to queue"), FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        _mark = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _mark.Children.Add(_icon);
        _mark.Children.Add(_label);
        // (the inside as wide as the row: the strip uncovers it, it doesn't squeeze it)
        var inside = new Panel { Width = size.Width, HorizontalAlignment = HorizontalAlignment.Left };
        inside.Children.Add(_fill);
        inside.Children.Add(_mark);
        _strip = new Border
        {
            Width = 0, Height = size.Height, ClipToBounds = true, CornerRadius = new CornerRadius(0, 16, 16, 0),
            Background = Ui.Res("Surface3Brush"), Child = inside,
        };
        Canvas.SetLeft(_strip, at.X - view.X);
        Canvas.SetTop(_strip, at.Y - view.Y);
        _clip = new Canvas { Width = view.Width, Height = view.Height, ClipToBounds = true };
        _clip.Children.Add(_strip);
        Canvas.SetLeft(_clip, view.X);
        Canvas.SetTop(_clip, view.Y);
        layer.Children.Add(_clip);
        Paint();
        row.RenderTransform = _shift;
        Place(0);
    }

    // dx: how far the finger went to the right; past the mark the row follows it less and less.
    public void Move(double dx)
    {
        Place(dx <= _trigger ? Math.Max(0, dx) : _trigger + (dx - _trigger) * 0.35);
        bool armed = _x >= _trigger;
        if (armed == _armed) return;
        _armed = armed;
        Paint();
        double from = _fill.Opacity, to = armed ? 1 : 0;
        int run = ++_fadeRun;
        if (!Ui.Animations)
        {
            _fill.Opacity = to;
            if (armed) Haptics.Tick();
            return;
        }
        Ui.Tween(_layer, 140, Ui.CubicOut, t => { if (run == _fadeRun) _fill.Opacity = from + (to - from) * t; });
        if (!armed) return;
        Haptics.Tick();
        int pop = ++_popRun;
        Ui.Tween(_layer, 260, Ui.CubicOut, t => { if (pop == _popRun) _pop.ScaleX = _pop.ScaleY = 1.3 - 0.3 * t; });
    }

    // Grey on grey, then the accent's text colour on the accent.
    private void Paint()
    {
        var brush = Ui.Res(_armed ? "OnAccentBrush" : "SubTextBrush");
        _icon.Foreground = brush;
        _label.Foreground = brush;
    }

    private void Place(double x)
    {
        _x = x;
        _shift.X = x;
        _strip.Width = x;
        // the icon and the words come in as there's room for them
        _mark.Opacity = Math.Clamp((x - 16) / 40, 0, 1);
    }

    // Let go (or taken away): past the mark the song goes into the queue; the row goes back to its place either way.
    public void End(bool commit)
    {
        if (commit && _armed) _run();
        double from = _x;
        if (!Ui.Animations || from <= 0)
        {
            Done();
            return;
        }
        Ui.Tween(_layer, 240, Ui.CubicOut, t =>
        {
            Place(from * (1 - t));
            if (t >= 1) Done();
        });
    }

    private void Done()
    {
        _layer.Children.Remove(_clip);
        // (unless the same row is already pulled again)
        if (_row.RenderTransform == _shift) _row.ClearValue(Visual.RenderTransformProperty);
    }
}
