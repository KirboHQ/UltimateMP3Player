using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using UltimateMP3Player.Core;
using UltimateMP3Player.Core.Mods;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// A mod's visualizer (Core/Mods/ModVisuals.cs) where the song page puts it: in place of the cover (Scene), or as the bars of
// the music. Asks the mod for a frame at each screen frame while the song plays (once when something changes while paused:
// a seek, its size), never waiting for it, and shows the latest one. Avalonia and the phones have theirs.
// Drawn with Skia as on the other UIs (ModSkia, linked: WPF's own drawing can't add colours, blur or put down a layer), on the
// mod's thread as each frame comes, into a picture the UI thread only copies into its WriteableBitmap.
public sealed class ModCanvasView : FrameworkElement
{
    public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(nameof(Scene), typeof(ModVisualizer), typeof(ModCanvasView),
        new PropertyMetadata(null, (d, _) => ((ModCanvasView)d).Update()));
    public static readonly DependencyProperty PlayerProperty = DependencyProperty.Register(nameof(Player), typeof(PlayerViewModel), typeof(ModCanvasView),
        new PropertyMetadata(null, (d, e) => ((ModCanvasView)d).OnPlayerSet((PlayerViewModel?)e.OldValue)));
    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(ModCanvasView),
        new PropertyMetadata(false, (d, _) => ((ModCanvasView)d).Dirty()));
    public static readonly DependencyProperty BarsPlaceProperty = DependencyProperty.Register(nameof(BarsPlace), typeof(VisualizerPlace), typeof(ModCanvasView),
        new PropertyMetadata(VisualizerPlace.Bottom, (d, _) => ((ModCanvasView)d).Dirty()));

    public ModVisualizer? Scene { get => (ModVisualizer?)GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
    public PlayerViewModel? Player { get => (PlayerViewModel?)GetValue(PlayerProperty); set => SetValue(PlayerProperty, value); }
    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public VisualizerPlace BarsPlace { get => (VisualizerPlace)GetValue(BarsPlaceProperty); set => SetValue(BarsPlaceProperty, value); }

    // A "page" visualizer: the empty place where the cover would be (f.stage), and whether its video shows under it (f.video).
    public static readonly DependencyProperty StageOfProperty = DependencyProperty.Register(nameof(StageOf), typeof(FrameworkElement), typeof(ModCanvasView),
        new PropertyMetadata(null, (d, e) => ((ModCanvasView)d).OnStageOf((FrameworkElement?)e.OldValue)));
    public static readonly DependencyProperty VideoShownProperty = DependencyProperty.Register(nameof(VideoShown), typeof(bool), typeof(ModCanvasView),
        new PropertyMetadata(false, (d, _) => ((ModCanvasView)d).Dirty()));
    public FrameworkElement? StageOf { get => (FrameworkElement?)GetValue(StageOfProperty); set => SetValue(StageOfProperty, value); }
    public bool VideoShown { get => (bool)GetValue(VideoShownProperty); set => SetValue(VideoShownProperty, value); }
    // How much of its bottom the player bar lies over (f.below: over the whole screen, NowPlayingView.PlaceFullScreen).
    public static readonly DependencyProperty BelowProperty = DependencyProperty.Register(nameof(Below), typeof(double), typeof(ModCanvasView),
        new PropertyMetadata(0.0, (d, _) => ((ModCanvasView)d).Dirty()));
    public double Below { get => (double)GetValue(BelowProperty); set => SetValue(BelowProperty, value); }

    private void OnStageOf(FrameworkElement? old)
    {
        if (old != null) old.SizeChanged -= OnStageSize;
        if (StageOf != null) StageOf.SizeChanged += OnStageSize;
        Dirty();
    }

    private void OnStageSize(object sender, SizeChangedEventArgs e) => Dirty();

    // A visualizer that takes taps (press): a click on it goes to the mod.
    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_scene is not { Visualizer.CanPress: true } scene) return;
        var p = e.GetPosition(this);
        scene.Press((float)p.X, (float)p.Y);
        e.Handled = true;
    }

    private ModScene? _scene;
    private bool _hooked, _dirty = true, _spectrum;
    // The picture shown; the frames drawn for it (one waiting for the UI thread, one to draw the next into); its size in pixels.
    private WriteableBitmap? _picture, _back;
    private readonly object _gate = new();
    private SKBitmap? _ready, _spare;
    // What stays the same at the start of each frame (a mod's background), drawn once (ModSkia.DrawBands).
    private readonly ModSkia.Backdrop _backdrop = new();
    private volatile int _pixelWidth, _pixelHeight;
    private readonly ModClock _clock = new();
    private TimeSpan? _frameAt, _shownAt;
    private double _frameMs;

    public ModCanvasView()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
        Loaded += (_, _) => { Ui.HiddenChanged += Dirty; Update(); };
        Unloaded += (_, _) => { Ui.HiddenChanged -= Dirty; Close(); };
        IsVisibleChanged += (_, _) => Update();
        SizeChanged += (_, _) => Dirty();
    }

    private void OnPlayerSet(PlayerViewModel? old)
    {
        if (old != null) old.PropertyChanged -= OnPlayer;
        if (Player != null && _scene != null) Player.PropertyChanged += OnPlayer;
    }

    private void Update()
    {
        var v = Scene;
        // (its own Visibility: IsVisible of something just shown is still false here)
        if (v == null || Visibility != Visibility.Visible || !IsLoaded)
        {
            Close();
            return;
        }
        IsHitTestVisible = v.CanPress;
        if (_scene?.Visualizer != v)
        {
            Close();
            _scene = v.Mod.OpenScene(v);
            _scene.Ready += OnReady;
            Spectrum.Shared.Acquire();
            _spectrum = true;
            if (Player != null) Player.PropertyChanged += OnPlayer;
        }
        Dirty();
    }

    private void Close()
    {
        Hook(false);
        if (_scene != null)
        {
            _scene.Ready -= OnReady;
            _scene.Dispose();
            _scene = null;
            if (Player != null) Player.PropertyChanged -= OnPlayer;
            (_picture, _back) = (null, null);
            lock (_gate)
            {
                _ready?.Dispose();
                _spare?.Dispose();
                (_ready, _spare) = (null, null);
            }
            _backdrop.Dispose();
            InvalidateVisual();
        }
        if (_spectrum)
        {
            Spectrum.Shared.Release();
            _spectrum = false;
        }
    }

    // A frame came (the mod's thread, before it may start the next): drawn here (what stays the same at its start copied from the
    // backdrop), then handed to the UI thread.
    private void OnReady()
    {
        if (_scene?.Latest is not { } list) return;
        int w = _pixelWidth, h = _pixelHeight;
        if (w <= 0 || h <= 0 || list.Width <= 0 || list.Height <= 0) return;
        SKBitmap? bitmap;
        lock (_gate) (bitmap, _spare) = (_spare, null);
        if (bitmap == null || bitmap.Width != w || bitmap.Height != h)
        {
            bitmap?.Dispose();
            bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        }
        ModSkia.DrawBands(bitmap, list, _backdrop);
        SKBitmap? stale;
        lock (_gate)
        {
            (stale, _ready) = (_ready, bitmap);
            if (_spare == null) (_spare, stale) = (stale, null);
        }
        stale?.Dispose();
        Dispatcher.BeginInvoke(ShowIdle);
    }

    // Moving, the frame goes on screen at the next screen frame (Tick); else as it comes (a seek while paused, a new size).
    private void ShowIdle()
    {
        if (!_hooked) Show();
    }

    private bool Show()
    {
        SKBitmap? bitmap;
        lock (_gate) (bitmap, _ready) = (_ready, null);
        if (bitmap == null) return false;
        if (_scene != null)
        {
            // Into the picture not on screen, then that one shown: the one on screen may still be on its way to the graphics card,
            // and writing to it made WPF wait and skip frames.
            var next = _back;
            if (next == null || next.PixelWidth != bitmap.Width || next.PixelHeight != bitmap.Height)
                next = new WriteableBitmap(bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Pbgra32, null);
            next.WritePixels(new Int32Rect(0, 0, bitmap.Width, bitmap.Height), bitmap.GetPixels(), bitmap.ByteCount, bitmap.RowBytes);
            (_back, _picture) = (_picture, next);
            InvalidateVisual();
        }
        lock (_gate)
        {
            if (_spare == null) (_spare, bitmap) = (bitmap, null);
        }
        bitmap?.Dispose();
        return true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_scene != null && _picture != null) dc.DrawImage(_picture, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    private void OnPlayer(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.Position) or nameof(PlayerViewModel.Current)) Dirty();
    }

    private void Dirty()
    {
        _dirty = true;
        if (_scene != null) Hook(true);
    }

    // Rendering only while there's something to draw: subscribed, WPF draws the window at every frame.
    private void Hook(bool on)
    {
        if (on == _hooked) return;
        _hooked = on;
        if (on) CompositionTarget.Rendering += Tick;
        else CompositionTarget.Rendering -= Tick;
    }

    private void Tick(object? sender, EventArgs e)
    {
        // (it can come more than once for the same frame: one frame asked for each, so the mod's frames keep step with the screen;
        // when the mod was still busy, asked again the next time it comes)
        var at = (e as RenderingEventArgs)?.RenderingTime;
        if (at != null && at == _frameAt) return;
        if (_scene == null || !IsVisible) { Hook(false); return; }
        // (pages sliding are each a cached picture: a frame shown in one had the whole page drawn again at every screen frame,
        // the song page's as big as the screen; 210 ms, then on, as WaveVisualizer does)
        if (AnimatedContent.Sliding > 0) return;
        bool moving = IsPlaying && !Ui.Hidden;
        // The frame drawn since the last screen frame goes on screen with this one, once a screen frame; with none, the picture is
        // drawn again. WPF keeps in step with the screen only while each of its frames brings something: a frame shown as it came,
        // between two of them, made it miss the next 3 to 5 (gaps of 25 ms at 200 Hz: 130 frames a second that looked like 20).
        if (at == null || at != _shownAt)
        {
            // (how long a screen frame lasts, for Info: the frame asked now shows at the next one)
            if (at is { } now && _shownAt is { } before && (now - before).TotalMilliseconds is > 1 and < 50 and var ms)
                _frameMs = _frameMs == 0 ? ms : _frameMs + (ms - _frameMs) * 0.1;
            _shownAt = at;
            if (!Show() && moving) InvalidateVisual();
        }
        if ((moving || _dirty) && ActualWidth > 0 && ActualHeight > 0 && _scene.Request(Info()))
        {
            _dirty = false;
            _frameAt = at;
        }
        if (!moving && !_dirty) Hook(false);
    }

    private ModFrameInfo Info()
    {
        var p = Player;
        double speed = p?.Speed ?? 1;
        float sx = 0, sy = 0, sw = 0, sh = 0;
        if (StageOf is { ActualWidth: > 0, ActualHeight: > 0, IsVisible: true } stage)
        {
            try
            {
                var at = stage.TranslatePoint(new Point(0, 0), this);
                (sx, sy, sw, sh) = ((float)at.X, (float)at.Y, (float)stage.ActualWidth, (float)stage.ActualHeight);
            }
            catch (InvalidOperationException) { }
        }
        float scale = (float)VisualTreeHelper.GetDpi(this).DpiScaleX * (float)Ui.Zoom;
        (_pixelWidth, _pixelHeight) = ((int)Math.Min(8192, Math.Ceiling(ActualWidth * scale)), (int)Math.Min(8192, Math.Ceiling(ActualHeight * scale)));
        return new ModFrameInfo
        {
            StageX = sx, StageY = sy, StageWidth = sw, StageHeight = sh,
            Video = VideoShown,
            Below = (float)Below,
            // (the time it will be when this frame is on screen: a screen frame on, playing, as it shows at the next Tick)
            Time = _clock.Now(p?.EnginePosition.TotalMilliseconds ?? 0, IsPlaying, speed) + (IsPlaying && !Ui.Hidden ? _frameMs * speed : 0),
            Duration = (p?.Current?.T.Duration ?? 0) * 1000,
            Playing = p?.IsPlaying == true,
            Speed = speed,
            Width = (float)ActualWidth,
            Height = (float)ActualHeight,
            Scale = scale,
            Bands = Spectrum.Shared.Read().ToArray(),
            Accent = Argb("AccentBrush"),
            Text = Argb("TextBrush"),
            Place = _scene?.Visualizer.Place == "bars" ? BarsPlace.ToString().ToLowerInvariant() : "song",
        };
    }

    private uint Argb(string key)
    {
        if (TryFindResource(key) is not SolidColorBrush b) return 0xFFFFFFFF;
        var c = b.Color;
        return (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B);
    }
}
