using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// Vertical level bar: green, then yellow, red near clipping.
public sealed class LevelMeter : Control
{
    public static readonly StyledProperty<float> LevelProperty = AvaloniaProperty.Register<LevelMeter, float>(nameof(Level));

    static LevelMeter() => AffectsRender<LevelMeter>(LevelProperty);

    public float Level { get => GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    private static readonly IBrush Fill = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromRgb(0xE5, 0x48, 0x4D), 0), new GradientStop(Color.FromRgb(0xF5, 0xB4, 0x00), 0.2),
            new GradientStop(Color.FromRgb(0x1D, 0xB9, 0x54), 0.45),
        },
    }.ToImmutable();

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        dc.DrawRectangle(Ui.Res("BgBrush"), null, new Rect(0, 0, w, h), w / 2, w / 2);
        // dB scale: -40 dB at the bottom.
        double db = 20 * Math.Log10(Math.Max(1e-4, Level));
        double v = Math.Clamp((db + 40) / 40, 0, 1);
        if (v <= 0) return;
        using (dc.PushClip(new RoundedRect(new Rect(0, h * (1 - v), w, h * v), w / 2)))
            dc.FillRectangle(Fill, new Rect(0, 0, w, h));
    }
}

// A deck's waveform (the Windows app's DjWave). Scrolling: ~10 s around the fixed playhead, beat grid and cue; drag to
// scrub. Overview: the whole song, click or drag to jump.
public sealed class DjWave : Control
{
    public static readonly StyledProperty<DjDeckViewModel?> DeckProperty = AvaloniaProperty.Register<DjWave, DjDeckViewModel?>(nameof(Deck));
    public static readonly StyledProperty<bool> OverviewProperty = AvaloniaProperty.Register<DjWave, bool>(nameof(Overview));

    public DjDeckViewModel? Deck { get => GetValue(DeckProperty); set => SetValue(DeckProperty, value); }
    public bool Overview { get => GetValue(OverviewProperty); set => SetValue(OverviewProperty, value); }

    // Seconds of music the scrolling view shows across its width.
    public const double Window = 10;
    private double _dragX, _dragPos;
    private bool _dragging;

    static DjWave()
    {
        AffectsRender<DjWave>(OverviewProperty);
        DeckProperty.Changed.AddClassHandler<DjWave>((w, e) => w.OnDeck(e.OldValue as DjDeckViewModel, e.NewValue as DjDeckViewModel));
    }

    public DjWave()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    private void OnDeck(DjDeckViewModel? old, DjDeckViewModel? deck)
    {
        if (old != null) old.PropertyChanged -= OnDeckChanged;
        if (deck != null) deck.PropertyChanged += OnDeckChanged;
        InvalidateVisual();
    }

    private void OnDeckChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The overview only moves its playhead: redrawn once it has moved half a pixel.
        if (Overview && e.PropertyName == nameof(DjDeckViewModel.Position) && Duration > 0
            && Math.Abs(Deck!.Position / Duration * Bounds.Width - _drawnX) < 0.5) return;
        if (e.PropertyName is nameof(DjDeckViewModel.Position) or nameof(DjDeckViewModel.Analysis) or nameof(DjDeckViewModel.Cue)
            or nameof(DjDeckViewModel.Rate) or nameof(DjDeckViewModel.HasTrack) or nameof(DjDeckViewModel.IsLoading) or nameof(DjDeckViewModel.Bpm)
            or nameof(DjDeckViewModel.FirstBeat))
            InvalidateVisual();
    }

    private double Duration => Deck is { } d ? (d.Engine.Duration > 0 ? d.Engine.Duration : d.Analysis?.Duration ?? 0) : 0;
    // Seconds of the song per pixel: the view keeps ~10 s of what you hear, so faster songs scroll more.
    private double SecondsPerPixel => Window * (Deck?.Rate ?? 1) / Math.Max(1, Bounds.Width);

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        dc.DrawRectangle(Ui.Res("BgBrush"), null, new Rect(0, 0, w, h), 8, 8);
        if (Deck is not { } deck) return;
        var col = deck.Color;
        var a = deck.Analysis;
        double dur = Duration;
        if (a == null || dur <= 0)
        {
            if (Overview) return;
            string msg = deck.IsLoading ? L.T("Analisi del brano…") : deck.HasTrack ? "" : deck.EmptyText;
            if (msg.Length == 0) return;
            var ft = new FormattedText(msg, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(Ui.Find<FontFamily>("UiFont")), 13, Ui.Res("MutedBrush"));
            dc.DrawText(ft, new Point((w - ft.Width) / 2, (h - ft.Height) / 2));
            return;
        }

        double pos = deck.Position, spp = SecondsPerPixel;
        if (Overview) DrawOverview(dc, deck, a, dur, pos, w, h);
        else DrawScrolling(dc, deck, a, dur, pos, spp, w, h);

        // Beat grid from the deck's first beat (every 4th beat stronger, the first one in the deck colour).
        if (!Overview && deck.Bpm is { } bpm)
        {
            double period = 60 / bpm, tStart = pos - w / 2 * spp, tEnd = pos + w / 2 * spp, first = deck.FirstBeat;
            if (_anchorColor != col)
            {
                _anchorPen = new Pen(new SolidColorBrush(col).ToImmutable(), 3);
                _anchorColor = col;
            }
            for (long k = (long)Math.Ceiling((tStart - first) / period); ; k++)
            {
                double t = first + k * period;
                if (t > tEnd) break;
                double x = w / 2 + (t - pos) / spp;
                bool downbeat = k % 4 == 0;
                dc.DrawLine(k == 0 ? _anchorPen! : downbeat ? BarPen : BeatPen, new Point(x, downbeat ? 0 : h * 0.2), new Point(x, downbeat ? h : h * 0.8));
            }
        }

        // Cue marker and playhead.
        double cueX = Overview ? deck.Cue / dur * w : w / 2 + (deck.Cue - pos) / spp;
        if (cueX >= -6 && cueX <= w + 6)
            using (dc.PushTransform(Matrix.CreateTranslation(cueX, 0)))
                dc.DrawGeometry(Brushes.Orange, null, CueShape);
        double px = Overview ? pos / dur * w : w / 2;
        _drawnX = px;
        dc.FillRectangle(Brushes.White, new Rect(px - 1, 0, 2, h));
    }

    private double _drawnX = double.NaN;
    private Pen? _anchorPen;
    private Color? _anchorColor;
    private static readonly Pen BeatPen = new(new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)).ToImmutable(), 1);
    private static readonly Pen BarPen = new(new SolidColorBrush(Color.FromArgb(0xA0, 255, 255, 255)).ToImmutable(), 1.5);
    private static readonly Geometry CueShape = Geometry.Parse("M-5,0 h10 l-5,7 z");

    // ------------------------------------------------------------------ the bars

    // One bar every 2 px, each on a fixed stretch of the song. The bars are made once per tile of 512 and every frame
    // only slides them (redrawn at the song's own speed and stretched, so moving the tempo fader doesn't remake them).
    private const double Step = 2;
    private const int TileBars = 512;
    private readonly Dictionary<long, Geometry> _tiles = new();
    private readonly Queue<long> _tileOrder = new();
    private object? _tileKey, _overKey;
    private Geometry? _overBars;

    private void DrawScrolling(DrawingContext dc, DjDeckViewModel deck, DjAnalysis a, double dur, double pos, double spp, double w, double h)
    {
        double baseSpp = Window / Math.Max(1, w);
        var key = (a, baseSpp, h);
        if (!key.Equals(_tileKey))
        {
            _tiles.Clear();
            _tileOrder.Clear();
            _tileKey = key;
        }
        double bin = baseSpp * Step, tileSec = bin * TileBars, stretch = baseSpp / spp;
        var ahead = new SolidColorBrush(deck.Color);
        var played = new SolidColorBrush(Faded(deck.Color));
        // Played on the left of the playhead, still to play on the right.
        foreach (bool isPlayed in new[] { true, false })
        {
            double from = isPlayed ? pos - w / 2 * spp : pos, to = isPlayed ? pos : pos + w / 2 * spp;
            long k0 = (long)Math.Floor(Math.Max(0, from) / tileSec), k1 = (long)Math.Floor(Math.Min(dur, to) / tileSec);
            if (k1 < k0) continue;
            using (dc.PushClip(isPlayed ? new Rect(0, 0, w / 2, h) : new Rect(w / 2, 0, w / 2, h)))
                for (long k = k0; k <= k1; k++)
                {
                    double x = w / 2 + (k * tileSec - pos) / spp;
                    using (dc.PushTransform(Matrix.CreateScale(stretch, 1) * Matrix.CreateTranslation(x, 0)))
                        dc.DrawGeometry(isPlayed ? played : ahead, null, Tile(a, k, bin, h, dur));
                }
        }
    }

    private Geometry Tile(DjAnalysis a, long k, double bin, double h, double dur)
    {
        if (_tiles.TryGetValue(k, out var g)) return g;
        g = Bars(a, bin, k * TileBars, TileBars, h, dur);
        _tiles[k] = g;
        _tileOrder.Enqueue(k);
        while (_tileOrder.Count > 12) _tiles.Remove(_tileOrder.Dequeue());
        return g;
    }

    // The whole song, split at the playhead.
    private void DrawOverview(DrawingContext dc, DjDeckViewModel deck, DjAnalysis a, double dur, double pos, double w, double h)
    {
        var key = (a, dur, w, h);
        if (!key.Equals(_overKey) || _overBars == null)
        {
            _overBars = Bars(a, dur / w * Step, 0, (long)Math.Ceiling(w / Step), h, dur);
            _overKey = key;
        }
        double px = Math.Clamp(pos / dur * w, 0, w);
        using (dc.PushClip(new Rect(0, 0, px, h)))
            dc.DrawGeometry(new SolidColorBrush(Faded(deck.Color)), null, _overBars);
        using (dc.PushClip(new Rect(px, 0, w - px, h)))
            dc.DrawGeometry(new SolidColorBrush(deck.Color), null, _overBars);
    }

    private static Color Faded(Color c) => Color.FromArgb(0x66, c.R, c.G, c.B);

    // Bars for the stretches [b0, b0 + n) of `bin` seconds each, starting at x = 0.
    private static Geometry Bars(DjAnalysis a, double bin, long b0, long n, double h, double dur)
    {
        double mid = h / 2, half = h / 2 - 3;
        var g = new StreamGeometry();
        using (var c = g.Open())
            for (long b = b0; b < b0 + n; b++)
            {
                double t0 = b * bin, t1 = t0 + bin;
                if (t1 < 0 || t0 > dur) continue;
                int i0 = Math.Max(0, (int)(t0 * a.PeaksPerSecond)), i1 = Math.Min(a.Peaks.Length - 1, (int)(t1 * a.PeaksPerSecond));
                float v = 0;
                for (int i = i0; i <= i1; i++) v = Math.Max(v, a.Peaks[i]);
                double bar = Math.Max(1, v * half), x = (b - b0) * Step;
                c.BeginFigure(new Point(x, mid - bar), true);
                c.LineTo(new Point(x + Step - 0.5, mid - bar));
                c.LineTo(new Point(x + Step - 0.5, mid + bar));
                c.LineTo(new Point(x, mid + bar));
                c.EndFigure(true);
            }
        return g;
    }

    // Wheel on the scrolling waveform: the song moves 10 ms per notch (fine alignment).
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Overview || Deck is not { HasTrack: true } deck || e.Delta.Y == 0) return;
        deck.NudgeStep(e.Delta.Y > 0 ? 1 : -1);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ mouse

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Deck is not { HasTrack: true } deck) return;
        var p = e.GetPosition(this);
        _dragX = p.X;
        _dragPos = deck.Position;
        if (Overview) deck.Seek(p.X / Math.Max(1, Bounds.Width) * Duration);
        _dragging = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging || Deck is not { } deck) return;
        var p = e.GetPosition(this);
        deck.Seek(Overview ? p.X / Math.Max(1, Bounds.Width) * Duration : _dragPos - (p.X - _dragX) * SecondsPerPixel);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }
}
