using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Views;

// Timed lyrics over the video, as subtitles: only the line being sung, at the bottom of the picture.
// A pause in the singing (an empty line) clears it, and a line doesn't stay up through a long instrumental.
public sealed class LyricsCaptions : Grid
{
    private readonly Border _box;
    private readonly TextBlock _text;
    private readonly TranslateTransform _rise = new();
    private readonly LyricsClock _clock = new();
    private readonly Tweener _fade, _lift;
    private LyricsText? _lyrics;
    private int _shown = -2;
    private bool _ticking;

    public LyricsCaptions()
    {
        IsHitTestVisible = false;
        _text = new TextBlock
        {
            FontFamily = LyricsView.Display,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        };
        _box = new Border
        {
            Child = _text,
            Background = new SolidColorBrush(Color.FromArgb(0xB8, 0x08, 0x09, 0x0C)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            RenderTransform = _rise,
            Opacity = 0,
        };
        _fade = new Tweener(_box, v => _box.Opacity = v);
        _lift = new Tweener(_box, v => _rise.Y = v);
        Children.Add(_box);
        SizeChanged += (_, _) => Relayout();
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty) return;
            UpdateTicking();
            Reset();
            Tick();
        };
        AttachedToVisualTree += (_, _) => UpdateTicking();
        DetachedFromVisualTree += (_, _) => UpdateTicking();
    }

    public void SetLyrics(LyricsText? lyrics)
    {
        _lyrics = lyrics is { Synced: true, Lines.Count: > 0 } ? lyrics : null;
        Reset();
        UpdateTicking();
        Tick();
    }

    public void SetClock(double position, bool playing, double rate)
    {
        _clock.Set(position, playing, rate);
        UpdateTicking();
        Tick();
    }

    // Sizes follow the picture, like a player's subtitles.
    private void Relayout()
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        double size = Math.Clamp(h / 17, 13, 34);
        _text.FontSize = size;
        _text.LineHeight = size * 1.25;
        _box.Padding = new Thickness(size * 0.6, size * 0.2, size * 0.6, size * 0.26);
        _box.CornerRadius = new CornerRadius(size * 0.4);
        _box.Margin = new Thickness(0, 0, 0, Math.Max(10, h * 0.07));
        _box.MaxWidth = Math.Max(120, w * 0.86);
    }

    private void UpdateTicking()
    {
        bool want = IsVisible && VisualRoot != null && _lyrics != null && _clock.Playing;
        if (want == _ticking) return;
        _ticking = want;
        if (want) RequestFrame();
    }

    private void RequestFrame() => TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ =>
    {
        if (!_ticking) return;
        Tick();
        RequestFrame();
    });

    private void Tick()
    {
        int line = Current();
        if (line == _shown) return;
        _shown = line;
        if (line < 0) Hide();
        else Show(_lyrics!.Lines[line].Text);
    }

    // The line being sung (a moment early, as on the lyrics page); -1 before the first, in a pause or after a long hold.
    private int Current()
    {
        if (_lyrics == null) return -1;
        var lines = _lyrics.Lines;
        double now = _clock.Now, pos = now + LyricsView.Lead;
        int i = -1;
        while (i + 1 < lines.Count && lines[i + 1].Time <= pos) i++;
        if (i < 0 || lines[i].Text.Length == 0) return -1;
        // No end times in the lyrics: a line still up long after it was sung is an instrumental, not singing.
        double hold = Math.Clamp(lines[i].Text.Length * 0.3, 6, 12);
        return now - lines[i].Time > hold ? -1 : i;
    }

    private void Show(string text)
    {
        _text.Text = text;
        // From line to line it only dips; after a pause it rises in.
        bool shown = _box.Opacity > 0.5;
        _fade.To(1, shown ? 140 : 200, from: shown ? 0.35 : 0);
        _lift.To(0, shown ? 160 : 260, Ui.CubicOut, from: shown ? 3 : _text.FontSize * 0.35);
    }

    private void Hide() => _fade.To(0, 220);

    // New lyrics, or shown again: start clean, the next tick puts the line up.
    private void Reset()
    {
        _shown = -2;
        _fade.Set(0);
    }
}
