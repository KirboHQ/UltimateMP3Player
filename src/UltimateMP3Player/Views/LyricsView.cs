using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Views;

// The lyrics on the song page, like Spotify's: the line being sung is lit and glides to a third of the height, the ones
// sung are half lit, the next ones dim; a click on a line jumps there; during a pause in the singing three dots fill up.
// Scrolling by hand stops the following (FollowingChanged) until Follow() or a click on a line.
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
        public FrameworkElement Element = null!;
        public double Time;
        public double End = double.MaxValue;
        public bool IsGap;
        public Ellipse[]? Dots;
        public double Shown = -1;
    }

    private static readonly DependencyProperty OffsetProperty = DependencyProperty.Register("Offset", typeof(double), typeof(LyricsView),
        new PropertyMetadata(0.0, (d, e) => ((LyricsView)d).OnOffset((double)e.NewValue)));

    private readonly ScrollViewer _scroll;
    private readonly StackPanel _panel = new();
    private readonly Border _top = new(), _bottom = new();
    // The lines fade out at the top and bottom edges (Relayout places the stops).
    private readonly LinearGradientBrush _fade = new() { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
    private readonly List<Row> _rows = new();
    private readonly LyricsClock _clock = new();
    private Border? _note;
    private LyricsText? _lyrics;
    private int _active = -2;
    private bool _following = true, _ignoreOffset, _ticking;
    private long _userScrolledAt;

    public LyricsView()
    {
        ClipToBounds = true;
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Focusable = false,
            Content = _panel,
            OpacityMask = _fade,
            // The mask is laid over what's drawn: a background makes that the whole height, not just the lines.
            Background = Brushes.Transparent,
        };
        SetFade(0, 0.1);
        Children.Add(_scroll);
        AddHandler(PreviewMouseWheelEvent, new MouseWheelEventHandler((_, _) => UserScrolled()), true);
        _scroll.PreviewMouseLeftButtonDown += (_, e) => { if (e.OriginalSource is System.Windows.Controls.Primitives.Thumb) UserScrolled(); };
        SizeChanged += (_, _) => Relayout(false);
        IsVisibleChanged += (_, _) => { UpdateTicking(); if (IsVisible) Relayout(false); };
    }

    public event Action<bool>? FollowingChanged;
    public event Action<double>? SeekRequested;
    // Seeking may need a permission (in a room).
    public Func<bool>? CanSeek { get; set; }

    public bool IsSynced => _lyrics?.Synced == true;
    public bool IsFollowing => _following;

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
        if (_note != null) _note.Visibility = plain ? Visibility.Visible : Visibility.Collapsed;
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
            Foreground = (Brush)FindResource("MutedBrush"),
            Margin = new Thickness(2, 34, 0, 0),
        };
        _panel.Children.Add(credit);
        _panel.Children.Add(_bottom);
        Relayout(true);
        UpdateTicking();
    }

    internal static readonly FontFamily Display = new("Segoe UI Variable Display, Segoe UI");

    private void AddLine(LyricLine l, bool synced)
    {
        var text = new TextBlock
        {
            Text = l.Text.Length == 0 ? " " : l.Text,
            FontFamily = Display,
            FontWeight = FontWeights.Bold,
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
        if (synced)
        {
            box.Cursor = Cursors.Hand;
            box.MouseEnter += (_, _) => Paint(row, _rows.IndexOf(row), true);
            box.MouseLeave += (_, _) => Paint(row, _rows.IndexOf(row), true);
            box.MouseLeftButtonUp += (_, e) =>
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
        strip.RenderTransformOrigin = new Point(0, 0.5);
        strip.RenderTransform = new ScaleTransform(1, 1);
        var row = new Row { Element = strip, Time = from, End = to, IsGap = true, Dots = dots };
        strip.Opacity = Future;
        _rows.Add(row);
        _panel.Children.Add(strip);
    }

    // "Lyrics without times", pinned at the top left over plain lyrics: the text scrolls away under it.
    private Border Note(string text)
    {
        var fg = new SolidColorBrush(Color.FromArgb(0xEE, 255, 255, 255));
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, Foreground = fg, VerticalAlignment = VerticalAlignment.Center });
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
        _fade.GradientStops = new GradientStopCollection
        {
            new(Colors.Transparent, 0), new(Colors.Transparent, from), new(Colors.Black, to), new(Colors.Black, 0.86), new(Colors.Transparent, 1),
        };
    }

    // Sizes follow the width; the empty space above and below lets the first and last line reach their place.
    private void Relayout(bool jump)
    {
        double w = ActualWidth, h = ActualHeight;
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
        double note = (_note is { ActualHeight: > 0 } n ? n.ActualHeight : 28) + 2;
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
        bool want = IsVisible && IsSynced && _clock.Playing;
        if (want == _ticking) return;
        _ticking = want;
        if (want) CompositionTarget.Rendering += OnFrame;
        else CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e) => Tick();

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
        if (!r.IsGap && index != _active && r.Element.IsMouseOver && CanSeek?.Invoke() != false) target = Hover;
        if (r.IsGap && index != _active)
        {
            foreach (var d in r.Dots!) d.Opacity = 0.35;
            ((ScaleTransform)r.Element.RenderTransform).ScaleX = ((ScaleTransform)r.Element.RenderTransform).ScaleY = 1;
        }
        if (Math.Abs(r.Shown - target) < 0.001) return;
        r.Shown = target;
        if (!Ui.Animations)
        {
            r.Element.BeginAnimation(OpacityProperty, null);
            r.Element.Opacity = target;
        }
        else r.Element.BeginAnimation(OpacityProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(hoverOnly ? 120 : 260)));
    }

    private static void FillDots(Row gap, double pos)
    {
        double span = gap.End == double.MaxValue ? 4 : Math.Max(0.5, gap.End - gap.Time);
        double p = Math.Clamp((pos - gap.Time) / span, 0, 1);
        for (int k = 0; k < 3; k++) gap.Dots![k].Opacity = 0.35 + 0.65 * Math.Clamp(p * 3 - k, 0, 1);
        // A slow breath while waiting, a small pop just before the words come back.
        double breath = 1 + 0.06 * Math.Sin(p * span * Math.PI * 1.2);
        double end = p > 0.94 ? 1 + (p - 0.94) / 0.06 * 0.12 : 1;
        var s = (ScaleTransform)gap.Element.RenderTransform;
        s.ScaleX = s.ScaleY = breath * end;
    }

    // ------------------------------------------------------------------ scrolling

    private void ScrollToActive(bool animate)
    {
        if (_rows.Count == 0) return;
        var row = _rows[Math.Max(0, _active)];
        if (!row.Element.IsDescendantOf(_panel)) return;
        double top = row.Element.TranslatePoint(new Point(0, 0), _panel).Y;
        // A row's centre at the anchor when it's small (the dots), its top when it's a line.
        double target = top - ActualHeight * Anchor + (row.IsGap ? row.Element.ActualHeight / 2 - 12 : -6);
        SetOffset(Math.Clamp(target, 0, Math.Max(0, _scroll.ExtentHeight - _scroll.ViewportHeight)), animate);
    }

    private void SetOffset(double target, bool animate)
    {
        if (!animate || !Ui.Animations)
        {
            StopAnimation();
            _scroll.ScrollToVerticalOffset(target);
            return;
        }
        BeginAnimation(OffsetProperty, new DoubleAnimation(_scroll.VerticalOffset, target, TimeSpan.FromMilliseconds(620))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    private void StopAnimation()
    {
        // Taking the animation off puts the property back to its base value: that isn't a place to scroll to.
        _ignoreOffset = true;
        BeginAnimation(OffsetProperty, null);
        _ignoreOffset = false;
    }

    private void OnOffset(double value)
    {
        if (!_ignoreOffset) _scroll.ScrollToVerticalOffset(value);
    }

    private void UserScrolled()
    {
        if (!IsSynced) return;
        StopAnimation();
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
