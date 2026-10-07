using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Views;

// The lyrics on the song page, like Spotify's (the Windows app's LyricsView): the line being sung is lit and glides to a
// third of the height, the ones sung are half lit, the next ones dim; a click on a line jumps there; during a pause in
// the singing three dots fill up. Scrolling by hand stops the following (FollowingChanged) until Follow() or a click.
// Without times (plain lyrics) it's just the text to scroll, with a note pinned at the top left.
public sealed class LyricsView : Grid
{
    // The line lights up a moment before it's sung: there's time to read it.
    internal const double Lead = 0.18;
    // Where the lit line sits, as a share of the height.
    private const double Anchor = 0.3;
    // A pause at the start longer than this gets its dots.
    private const double IntroGap = 4;
    private const double Past = 0.5, Future = 0.3, Hover = 0.82, Plain = 0.92;

    private sealed class Row
    {
        public Control Element = null!;
        public Tweener Fade = null!;
        public double Time;
        public double End = double.MaxValue;
        public bool IsGap;
        public Ellipse[]? Dots;
        public ScaleTransform? Scale;
        public double Shown = -1;
    }

    private readonly ScrollViewer _scroll;
    private readonly StackPanel _panel = new();
    private readonly Border _top = new(), _bottom = new();
    private readonly List<Row> _rows = new();
    private readonly LyricsClock _clock = new();
    private readonly Tweener _offset;
    private Border? _note;
    private LyricsText? _lyrics;
    private int _active = -2;
    private bool _following = true, _ticking;
    private long _userScrolledAt;

    public LyricsView()
    {
        ClipToBounds = true;
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Content = _panel,
            // The mask is laid over what's drawn: a background makes that the whole height, not just the lines.
            Background = Brushes.Transparent,
        };
        _offset = new Tweener(this, v => _scroll.Offset = new Vector(_scroll.Offset.X, v));
        SetFade(0, 0.1);
        Children.Add(_scroll);
        AddHandler(PointerWheelChangedEvent, (_, _) => UserScrolled(), RoutingStrategies.Tunnel, true);
        // A finger dragging the lyrics (the phone) is a scroll by hand too.
        AddHandler(Gestures.ScrollGestureEvent, (_, _) => UserScrolled(), RoutingStrategies.Bubble, true);
        SizeChanged += (_, _) => Relayout(false);
        AttachedToVisualTree += (_, _) => UpdateTicking();
        DetachedFromVisualTree += (_, _) => UpdateTicking();
    }

    public event Action<bool>? FollowingChanged;
    public event Action<double>? SeekRequested;
    // Seeking may need a permission (in a room).
    public Func<bool>? CanSeek { get; set; }

    public bool IsSynced => _lyrics?.Synced == true;
    public bool IsFollowing => _following;

    // Shown again (the page switched to the lyrics): sizes and place are recomputed.
    public void OnShown()
    {
        UpdateTicking();
        Relayout(false);
    }

    // ------------------------------------------------------------------ building

    public void SetLyrics(LyricsText? lyrics)
    {
        _lyrics = lyrics;
        _rows.Clear();
        _panel.Children.Clear();
        _active = -2;
        SetFollowing(true);
        bool plain = lyrics is { Synced: false, Lines.Count: > 0 };
        if (plain && _note == null) Children.Add(_note = Note(L.T("Testo senza tempi: scorri per leggerlo")));
        if (_note != null) _note.IsVisible = plain;
        if (lyrics == null || lyrics.Lines.Count == 0)
        {
            UpdateTicking();
            return;
        }
        _panel.Children.Add(_top);
        var lines = lyrics.Lines;
        if (lyrics.Synced && lines[0].Time >= IntroGap) AddGap(0, lines[0].Time);
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            double next = i + 1 < lines.Count ? lines[i + 1].Time : double.MaxValue;
            if (lyrics.Synced && l.Text.Length == 0) AddGap(l.Time, next);
            else AddLine(l, lyrics.Synced);
        }
        var credit = new TextBlock
        {
            Text = L.T("Testo da LRCLIB"),
            FontSize = 12,
            Foreground = Ui.Res("MutedBrush"),
            Margin = new Thickness(2, 34, 0, 0),
        };
        _panel.Children.Add(credit);
        _panel.Children.Add(_bottom);
        Relayout(true);
        UpdateTicking();
    }

    internal static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI, avares://UltimateMP3Player/Assets/Fonts#Selawik");

    private void AddLine(LyricLine l, bool synced)
    {
        var text = new TextBlock
        {
            Text = l.Text.Length == 0 ? " " : l.Text,
            FontFamily = Display,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
        };
        var box = new Border
        {
            Child = text,
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Left,
            Opacity = synced ? Future : Plain,
        };
        var row = new Row { Element = box, Time = synced ? l.Time : -1 };
        row.Fade = new Tweener(box, v => box.Opacity = v, box.Opacity);
        if (synced)
        {
            box.Cursor = Ui.Hand;
            box.PointerEntered += (_, _) => Paint(row, _rows.IndexOf(row), true);
            box.PointerExited += (_, _) => Paint(row, _rows.IndexOf(row), true);
            // A tap (not the end of a drag that scrolled the lyrics).
            box.Tapped += (_, e) =>
            {
                if (CanSeek?.Invoke() == false) return;
                e.Handled = true;
                SetFollowing(true);
                SeekRequested?.Invoke(row.Time);
            };
        }
        _rows.Add(row);
        _panel.Children.Add(box);
    }

    // A pause in the singing: three dots that fill up while it lasts.
    private void AddGap(double from, double to)
    {
        var dots = Enumerable.Range(0, 3).Select(_ => new Ellipse { Fill = Brushes.White, Opacity = 0.35 }).ToArray();
        var strip = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent };
        foreach (var d in dots) strip.Children.Add(d);
        var scale = new ScaleTransform(1, 1);
        strip.RenderTransformOrigin = new RelativePoint(0, 0.5, RelativeUnit.Relative);
        strip.RenderTransform = scale;
        strip.Opacity = Future;
        var row = new Row { Element = strip, Time = from, End = to, IsGap = true, Dots = dots, Scale = scale };
        row.Fade = new Tweener(strip, v => strip.Opacity = v, strip.Opacity);
        _rows.Add(row);
        _panel.Children.Add(strip);
    }

    // "Lyrics without times", pinned at the top left over plain lyrics: the text scrolls away under it.
    private static Border Note(string text)
    {
        var fg = new SolidColorBrush(Color.FromArgb(0xEE, 255, 255, 255));
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new TextBlock { Text = Icons.Map(""), FontSize = 12, Foreground = fg, VerticalAlignment = VerticalAlignment.Center };
        icon.Classes.Add("glyph");
        sp.Children.Add(icon);
        sp.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = fg, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return new Border
        {
            Child = sp,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(11, 5, 12, 5),
            Background = new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
    }

    // Transparent down to `from`, solid from `to` (shares of the height); the bottom always fades out.
    private void SetFade(double from, double to)
    {
        _scroll.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.Transparent, 0), new GradientStop(Colors.Transparent, from), new GradientStop(Colors.Black, to),
                new GradientStop(Colors.Black, 0.86), new GradientStop(Colors.Transparent, 1),
            },
        };
    }

    // Sizes follow the width; the empty space above and below lets the first and last line reach their place.
    private void Relayout(bool jump)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0 || _lyrics == null) return;
        bool synced = _lyrics.Synced;
        double size = Math.Clamp(w / 24, 21, 36);
        foreach (var r in _rows)
        {
            if (r.IsGap)
            {
                double dot = Math.Round(size * 0.4);
                foreach (var d in r.Dots!)
                {
                    d.Width = d.Height = dot;
                    d.Margin = new Thickness(0, 0, dot * 0.7, 0);
                }
                r.Element.Margin = new Thickness(2, size * 0.55, 0, size * 0.55 + 4);
                r.Element.Height = double.NaN;
            }
            else if (r.Element is Border { Child: TextBlock t } b)
            {
                t.FontSize = synced ? size : Math.Round(size * 0.8);
                t.LineHeight = t.FontSize * 1.22;
                b.Padding = new Thickness(0, t.FontSize * 0.28, 0, t.FontSize * 0.28);
                b.MaxWidth = Math.Max(200, Math.Min(w - 8, size * 26));
            }
        }
        // Plain lyrics start clear below the pinned note and fade out before passing under it.
        double note = (_note is { Bounds.Height: > 0 } n ? n.Bounds.Height : 28) + 2;
        if (synced) SetFade(0, 0.1);
        else SetFade(Math.Min(0.5, note / h), Math.Min(0.6, (note + 26) / h));
        _top.Height = synced ? h * Anchor : Math.Max(h * 0.12, note + 32);
        _bottom.Height = synced ? h * (1 - Anchor) : h * 0.2;
        if (synced && (jump || _following))
        {
            UpdateLayout();
            ScrollToActive(false);
        }
        else if (jump) SetOffset(0, false);
    }

    // ------------------------------------------------------------------ time

    public void SetClock(double position, bool playing, double rate)
    {
        _clock.Set(position, playing, rate);
        UpdateTicking();
        Tick();
    }

    private void UpdateTicking()
    {
        bool want = VisualRoot != null && IsSynced && _clock.Playing;
        // Hidden (the song's page closed, another tab): no frames, looked at again in a while (Ui.Later).
        if (want && !IsEffectivelyVisible)
        {
            _ticking = false;
            if (!_waitShown)
            {
                _waitShown = true;
                Ui.Later(() =>
                {
                    _waitShown = false;
                    UpdateTicking();
                });
            }
            return;
        }
        if (want == _ticking) return;
        _ticking = want;
        if (want)
        {
            Tick();
            RequestFrame();
        }
    }

    private bool _waitShown, _frameAsked;
    // The sung line and the dots of a pause checked 33 times a second, not at every frame of the screen.
    private readonly FramePacer _pacer = new(30);

    private void RequestFrame()
    {
        if (_frameAsked || TopLevel.GetTopLevel(this) is not { } top) return;
        _frameAsked = true;
        _pacer.Request(top, _ =>
        {
            _frameAsked = false;
            if (!_ticking) return;
            if (!IsEffectivelyVisible)
            {
                UpdateTicking();
                return;
            }
            Tick();
            RequestFrame();
        });
    }

    private void Tick()
    {
        if (!IsSynced || _rows.Count == 0) return;
        double pos = _clock.Now + Lead;
        int active = -1;
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Time <= pos) active = i;
            else break;
        }
        // A pause is over when its time is: the next line may still be a moment away.
        if (active >= 0 && _rows[active].IsGap && pos - Lead >= _rows[active].End) active = Math.Min(active + 1, _rows.Count - 1);
        if (active != _active)
        {
            _active = active;
            for (int i = 0; i < _rows.Count; i++) Paint(_rows[i], i, false);
            // Scrolled by hand a while ago: it follows again from the next line.
            if (!_following && Stopwatch.GetElapsedTime(_userScrolledAt) > TimeSpan.FromSeconds(8)) SetFollowing(true);
            if (_following) ScrollToActive(true);
        }
        if (active >= 0 && _rows[active] is { IsGap: true } gap) FillDots(gap, pos - Lead);
    }

    // Lit, sung, to come; a line under the mouse lights up a little (it can be clicked).
    private void Paint(Row r, int index, bool hoverOnly)
    {
        if (!IsSynced) return;
        double target = index == _active ? 1 : index < _active ? Past : Future;
        if (!r.IsGap && index != _active && r.Element.IsPointerOver && CanSeek?.Invoke() != false) target = Hover;
        if (r.IsGap && index != _active)
        {
            foreach (var d in r.Dots!) d.Opacity = 0.35;
            r.Scale!.ScaleX = r.Scale.ScaleY = 1;
        }
        if (Math.Abs(r.Shown - target) < 0.001) return;
        r.Shown = target;
        r.Fade.To(target, hoverOnly ? 120 : 260);
    }

    private static void FillDots(Row gap, double pos)
    {
        double span = gap.End == double.MaxValue ? 4 : Math.Max(0.5, gap.End - gap.Time);
        double p = Math.Clamp((pos - gap.Time) / span, 0, 1);
        for (int k = 0; k < 3; k++) gap.Dots![k].Opacity = 0.35 + 0.65 * Math.Clamp(p * 3 - k, 0, 1);
        // A slow breath while waiting, a small pop just before the words come back.
        double breath = 1 + 0.06 * Math.Sin(p * span * Math.PI * 1.2);
        double end = p > 0.94 ? 1 + (p - 0.94) / 0.06 * 0.12 : 1;
        gap.Scale!.ScaleX = gap.Scale.ScaleY = breath * end;
    }

    // ------------------------------------------------------------------ scrolling

    private void ScrollToActive(bool animate)
    {
        if (_rows.Count == 0) return;
        var row = _rows[Math.Max(0, _active)];
        if (!_panel.IsVisualAncestorOf(row.Element)) return;
        double top = row.Element.TranslatePoint(new Point(0, 0), _panel)?.Y ?? 0;
        // A row's centre at the anchor when it's small (the dots), its top when it's a line.
        double target = top - Bounds.Height * Anchor + (row.IsGap ? row.Element.Bounds.Height / 2 - 12 : -6);
        SetOffset(Math.Clamp(target, 0, Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height)), animate);
    }

    private void SetOffset(double target, bool animate)
    {
        if (!animate)
        {
            _offset.Stop();
            _scroll.Offset = new Vector(_scroll.Offset.X, target);
            return;
        }
        _offset.To(target, 620, Ui.CubicInOut, from: _scroll.Offset.Y);
    }

    private void UserScrolled()
    {
        if (!IsSynced) return;
        _offset.Stop();
        _userScrolledAt = Stopwatch.GetTimestamp();
        SetFollowing(false);
    }

    private void SetFollowing(bool on)
    {
        if (_following == on) return;
        _following = on;
        FollowingChanged?.Invoke(on);
    }

    // Back to the line being sung.
    public void Follow()
    {
        SetFollowing(true);
        ScrollToActive(true);
    }
}

// The song's position 4 times a second; in between it runs on by the clock.
internal sealed class LyricsClock
{
    private double _base;
    private long _baseAt;
    private double _rate = 1;

    public bool Playing { get; private set; }

    public void Set(double position, bool playing, double rate)
    {
        _base = position;
        _baseAt = Stopwatch.GetTimestamp();
        Playing = playing;
        _rate = rate > 0 ? rate : 1;
    }

    public double Now => Playing ? _base + (Stopwatch.GetTimestamp() - _baseAt) / (double)Stopwatch.Frequency * _rate : _base;
}
