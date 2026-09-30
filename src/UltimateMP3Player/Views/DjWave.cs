using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// Vertical level bar: green, then yellow, red near clipping.
public sealed class LevelMeter : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(float), typeof(LevelMeter),
        new FrameworkPropertyMetadata(0f, FrameworkPropertyMetadataOptions.AffectsRender));

    public float Level { get => (float)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }

    private static readonly Brush Fill = Freeze(new LinearGradientBrush(new GradientStopCollection
    {
        new(Color.FromRgb(0xE5, 0x48, 0x4D), 0), new(Color.FromRgb(0xF5, 0xB4, 0x00), 0.2), new(Color.FromRgb(0x1D, 0xB9, 0x54), 0.45),
    }, new Point(0, 0), new Point(0, 1)));

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRoundedRectangle((Brush)Application.Current.Resources["BgBrush"], null, new Rect(0, 0, w, h), w / 2, w / 2);
        // dB scale: -40 dB at the bottom.
        double db = 20 * Math.Log10(Math.Max(1e-4, Level));
        double v = Math.Clamp((db + 40) / 40, 0, 1);
        if (v <= 0) return;
        dc.PushClip(new RectangleGeometry(new Rect(0, h * (1 - v), w, h * v), w / 2, w / 2));
        dc.DrawRectangle(Fill, null, new Rect(0, 0, w, h));
        dc.Pop();
    }
}

// A deck's waveform. Scrolling: ~10 s around the fixed playhead, beat grid and cue; drag to scrub.
// Overview: the whole song, click or drag to jump.
public sealed class DjWave : FrameworkElement
{
    public static readonly DependencyProperty DeckProperty = DependencyProperty.Register(nameof(Deck), typeof(DjDeckViewModel), typeof(DjWave),
        new FrameworkPropertyMetadata(null, (d, e) => ((DjWave)d).OnDeck(e.OldValue as DjDeckViewModel, e.NewValue as DjDeckViewModel)));

    public static readonly DependencyProperty OverviewProperty = DependencyProperty.Register(nameof(Overview), typeof(bool), typeof(DjWave),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public DjDeckViewModel? Deck { get => (DjDeckViewModel?)GetValue(DeckProperty); set => SetValue(DeckProperty, value); }
    public bool Overview { get => (bool)GetValue(OverviewProperty); set => SetValue(OverviewProperty, value); }

    // Seconds of music the scrolling view shows across its width.
    public const double Window = 10;
    private double _dragX, _dragPos;

    public DjWave()
    {
        ClipToBounds = true;
        Cursor = Cursors.Hand;
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
            && Math.Abs(Deck!.Position / Duration * ActualWidth - _drawnX) < 0.5) return;
        if (e.PropertyName is nameof(DjDeckViewModel.Position) or nameof(DjDeckViewModel.Analysis) or nameof(DjDeckViewModel.Cue)
            or nameof(DjDeckViewModel.Rate) or nameof(DjDeckViewModel.HasTrack) or nameof(DjDeckViewModel.IsLoading) or nameof(DjDeckViewModel.Bpm)
            or nameof(DjDeckViewModel.FirstBeat))
            InvalidateVisual();
    }

    private double Duration => Deck is { } d ? (d.Engine.Duration > 0 ? d.Engine.Duration : d.Analysis?.Duration ?? 0) : 0;
    // Seconds of the song per pixel: the view keeps ~10 s of what you hear, so faster songs scroll more.
    private double SecondsPerPixel => Window * (Deck?.Rate ?? 1) / Math.Max(1, ActualWidth);

    protected override void OnRender(DrawingContext dc)
    {
        var res = Application.Current.Resources;
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRoundedRectangle((Brush)res["BgBrush"], null, new Rect(0, 0, w, h), 8, 8);
        if (Deck is not { } deck) return;
        var col = deck.Color;
        var a = deck.Analysis;
        double dur = Duration;
        if (a == null || dur <= 0)
        {
            if (Overview) return;
            string msg = deck.IsLoading ? L.T("Analisi del brano…") : deck.HasTrack ? "" : deck.EmptyText;
            var ft = new FormattedText(msg, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface((FontFamily)res["UiFont"], FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 13, (Brush)res["MutedBrush"],
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
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
            if (_anchorPen?.Brush is not SolidColorBrush { } ab || ab.Color != col) _anchorPen = FrozenPen(col, 3);
            for (long k = (long)Math.Ceiling((tStart - first) / period); ; k++)
            {
                double t = first + k * period;
                if (t > tEnd) break;
                double x = w / 2 + (t - pos) / spp;
                bool downbeat = k % 4 == 0;
                dc.DrawLine(k == 0 ? _anchorPen : downbeat ? BarPen : BeatPen, new Point(x, downbeat ? 0 : h * 0.2), new Point(x, downbeat ? h : h * 0.8));
            }
        }

        // Cue marker and playhead.
        double cueX = Overview ? deck.Cue / dur * w : w / 2 + (deck.Cue - pos) / spp;
        if (cueX >= -6 && cueX <= w + 6)
        {
            dc.PushTransform(new TranslateTransform(cueX, 0));
            dc.DrawGeometry(Brushes.Orange, null, CueShape);
            dc.Pop();
        }
        double px = Overview ? pos / dur * w : w / 2;
        _drawnX = px;
        dc.DrawRectangle(Brushes.White, null, new Rect(px - 1, 0, 2, h));
    }

    private double _drawnX = double.NaN;
    private Pen? _anchorPen;
    private static readonly Pen BeatPen = FrozenPen(Color.FromArgb(0x40, 255, 255, 255), 1);
    private static readonly Pen BarPen = FrozenPen(Color.FromArgb(0xA0, 255, 255, 255), 1.5);
    private static readonly Geometry CueShape = Frozen(Geometry.Parse("M-5,0 h10 l-5,7 z"));

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    private static Pen FrozenPen(Color c, double width) => Frozen(new Pen(Frozen(new SolidColorBrush(c)), width));

    // ------------------------------------------------------------------ pictures of the waveform

    // One bar every 2 px, each on a fixed stretch of the song. The bars are drawn once into pictures (tiles of
    // 512 bars, one in the played colour and one in the colour still to play) and every frame just slides them:
    // drawing hundreds of bars at every frame made the page lag.
    private const double Step = 2;
    private const int TileBars = 512;
    private readonly Dictionary<(long Tile, bool Played), BitmapSource> _tiles = new();
    private readonly Queue<(long, bool)> _tileOrder = new();
    private object? _tileKey, _overKey;
    private BitmapSource? _overPlayed, _overAhead;

    private void DrawScrolling(DrawingContext dc, DjDeckViewModel deck, DjAnalysis a, double dur, double pos, double spp, double w, double h)
    {
        // Tiles are made at the song's own speed and stretched while drawn, so moving the tempo fader doesn't remake them.
        var dpi = VisualTreeHelper.GetDpi(this);
        double baseSpp = Window / Math.Max(1, w);
        var key = (a, baseSpp, h, deck.Color, dpi.DpiScaleX, dpi.DpiScaleY);
        if (!key.Equals(_tileKey))
        {
            _tiles.Clear();
            _tileOrder.Clear();
            _tileKey = key;
        }
        double bin = baseSpp * Step, tileSec = bin * TileBars, tileW = tileSec / spp;
        // Played on the left of the playhead, still to play on the right.
        foreach (bool played in new[] { true, false })
        {
            double from = played ? pos - w / 2 * spp : pos, to = played ? pos : pos + w / 2 * spp;
            long k0 = (long)Math.Floor(Math.Max(0, from) / tileSec), k1 = (long)Math.Floor(Math.Min(dur, to) / tileSec);
            if (k1 < k0) continue;
            dc.PushClip(new RectangleGeometry(played ? new Rect(0, 0, w / 2, h) : new Rect(w / 2, 0, w / 2, h)));
            for (long k = k0; k <= k1; k++)
                dc.DrawImage(Tile(a, k, played, bin, h, dur, deck.Color, dpi), new Rect(w / 2 + (k * tileSec - pos) / spp, 0, tileW, h));
            dc.Pop();
        }
    }

    private BitmapSource Tile(DjAnalysis a, long k, bool played, double bin, double h, double dur, Color col, DpiScale dpi)
    {
        if (_tiles.TryGetValue((k, played), out var bmp)) return bmp;
        bmp = Picture(Bars(a, bin, k * TileBars, TileBars, h, dur), TileBars * Step, h, played ? Faded(col) : col, dpi);
        _tiles[(k, played)] = bmp;
        _tileOrder.Enqueue((k, played));
        while (_tileOrder.Count > 12) _tiles.Remove(_tileOrder.Dequeue());
        return bmp;
    }

    // The whole song in one picture per colour, split at the playhead.
    private void DrawOverview(DrawingContext dc, DjDeckViewModel deck, DjAnalysis a, double dur, double pos, double w, double h)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var key = (a, dur, w, h, deck.Color, dpi.DpiScaleX, dpi.DpiScaleY);
        if (!key.Equals(_overKey) || _overPlayed == null || _overAhead == null)
        {
            var g = Bars(a, dur / w * Step, 0, (long)Math.Ceiling(w / Step), h, dur);
            _overPlayed = Picture(g, w, h, Faded(deck.Color), dpi);
            _overAhead = Picture(g, w, h, deck.Color, dpi);
            _overKey = key;
        }
        double px = Math.Clamp(pos / dur * w, 0, w);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, px, h)));
        dc.DrawImage(_overPlayed, new Rect(0, 0, w, h));
        dc.Pop();
        dc.PushClip(new RectangleGeometry(new Rect(px, 0, w - px, h)));
        dc.DrawImage(_overAhead, new Rect(0, 0, w, h));
        dc.Pop();
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
                c.BeginFigure(new Point(x, mid - bar), true, true);
                c.LineTo(new Point(x + Step - 0.5, mid - bar), false, false);
                c.LineTo(new Point(x + Step - 0.5, mid + bar), false, false);
                c.LineTo(new Point(x, mid + bar), false, false);
            }
        return Frozen(g);
    }

    private static BitmapSource Picture(Geometry g, double w, double h, Color col, DpiScale dpi)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawGeometry(Frozen(new SolidColorBrush(col)), null, g);
        var bmp = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(w * dpi.DpiScaleX)), Math.Max(1, (int)Math.Ceiling(h * dpi.DpiScaleY)),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(dv);
        return Frozen(bmp);
    }

    // Wheel on the scrolling waveform: the song moves 10 ms per notch (fine alignment).
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Overview || Deck is not { HasTrack: true } deck) return;
        deck.NudgeStep(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ mouse

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (Deck is not { HasTrack: true } deck) return;
        var p = e.GetPosition(this);
        _dragX = p.X;
        _dragPos = deck.Position;
        if (Overview) deck.Seek(p.X / Math.Max(1, ActualWidth) * Duration);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!IsMouseCaptured || Deck is not { } deck) return;
        var p = e.GetPosition(this);
        deck.Seek(Overview ? p.X / Math.Max(1, ActualWidth) * Duration : _dragPos - (p.X - _dragX) * SecondsPerPixel);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();
}
