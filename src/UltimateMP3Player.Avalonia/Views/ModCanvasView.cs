using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using UltimateMP3Player.Core;
using UltimateMP3Player.Core.Mods;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// A mod's visualizer (Core/Mods/ModVisuals.cs) where the song page puts it: in place of the cover (Scene), or as the bars of
// the music. Asks the mod for a frame at each screen frame while the song plays (once when something changes while paused:
// a seek, its size), never waiting for it, and draws the latest with Skia (ModSkia). Linked into the phones; WPF has its own.
public sealed class ModCanvasView : Control
{
    public static readonly StyledProperty<ModVisualizer?> SceneProperty = AvaloniaProperty.Register<ModCanvasView, ModVisualizer?>(nameof(Scene));
    public static readonly StyledProperty<PlayerViewModel?> PlayerProperty = AvaloniaProperty.Register<ModCanvasView, PlayerViewModel?>(nameof(Player));
    public static readonly StyledProperty<bool> IsPlayingProperty = AvaloniaProperty.Register<ModCanvasView, bool>(nameof(IsPlaying));
    public static readonly StyledProperty<VisualizerPlace> BarsPlaceProperty = AvaloniaProperty.Register<ModCanvasView, VisualizerPlace>(nameof(BarsPlace));

    public ModVisualizer? Scene { get => GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
    public PlayerViewModel? Player { get => GetValue(PlayerProperty); set => SetValue(PlayerProperty, value); }
    public bool IsPlaying { get => GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public VisualizerPlace BarsPlace { get => GetValue(BarsPlaceProperty); set => SetValue(BarsPlaceProperty, value); }

    // A "page" visualizer: the empty place where the cover would be (f.stage), and whether its video shows under it (f.video).
    public static readonly StyledProperty<Control?> StageOfProperty = AvaloniaProperty.Register<ModCanvasView, Control?>(nameof(StageOf));
    public static readonly StyledProperty<bool> VideoShownProperty = AvaloniaProperty.Register<ModCanvasView, bool>(nameof(VideoShown));
    public Control? StageOf { get => GetValue(StageOfProperty); set => SetValue(StageOfProperty, value); }
    public bool VideoShown { get => GetValue(VideoShownProperty); set => SetValue(VideoShownProperty, value); }
    // How much of its bottom the player bar lies over (f.below: over the whole screen, NowPlayingView.PlaceFullScreen).
    public static readonly StyledProperty<double> BelowProperty = AvaloniaProperty.Register<ModCanvasView, double>(nameof(Below));
    public double Below { get => GetValue(BelowProperty); set => SetValue(BelowProperty, value); }

    private ModScene? _scene;
    private PlayerViewModel? _player;
    private bool _looping, _dirty = true, _spectrum, _attached;
    private readonly ModClock _clock = new();

    public ModCanvasView()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
        // A visualizer that takes taps (press): a tap or a click goes to the mod (Tapped: a swipe across the page isn't one).
        Tapped += (_, e) =>
        {
            if (_scene is not { Visualizer.CanPress: true } scene) return;
            var p = e.GetPosition(this);
            scene.Press((float)p.X, (float)p.Y);
            e.Handled = true;
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Ui.HiddenChanged += Wake;
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Ui.HiddenChanged -= Wake;
        Close();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SceneProperty || change.Property == IsVisibleProperty) Update();
        else if (change.Property == PlayerProperty)
        {
            if (_player != null) _player.PropertyChanged -= OnPlayer;
            _player = Player;
            if (_player != null && _scene != null) _player.PropertyChanged += OnPlayer;
        }
        else if (change.Property == StageOfProperty)
        {
            if (change.OldValue is Control old) old.PropertyChanged -= OnStage;
            if (StageOf != null) StageOf.PropertyChanged += OnStage;
            _dirty = true;
            Wake();
        }
        else if (change.Property == IsPlayingProperty || change.Property == BoundsProperty || change.Property == BarsPlaceProperty || change.Property == VideoShownProperty
                 || change.Property == BelowProperty)
        {
            _dirty = true;
            Wake();
        }
    }

    // A tap that landed on something over the canvas (the phone's stage): to the mod as if on it.
    public bool PressFrom(Visual from, Point p)
    {
        if (_scene is not { Visualizer.CanPress: true } scene || from.TranslatePoint(p, this) is not { } at) return false;
        scene.Press((float)at.X, (float)at.Y);
        return true;
    }

    private void OnStage(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != BoundsProperty) return;
        _dirty = true;
        Wake();
    }

    private void Update()
    {
        var v = Scene;
        if (v == null || !IsVisible || !_attached)
        {
            IsHitTestVisible = false;
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
            if (_player != null) _player.PropertyChanged += OnPlayer;
            _dirty = true;
        }
        Wake();
    }

    private void Close()
    {
        if (_scene != null)
        {
            _scene.Ready -= OnReady;
            _scene.Dispose();
            _scene = null;
            if (_player != null) _player.PropertyChanged -= OnPlayer;
            InvalidateVisual();
        }
        if (_spectrum)
        {
            Spectrum.Shared.Release();
            _spectrum = false;
        }
    }

    private void OnReady() => Dispatcher.UIThread.Post(InvalidateVisual);

    // Paused, a seek or another song still needs its frame.
    private void OnPlayer(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.Position) or nameof(PlayerViewModel.Current))
        {
            _dirty = true;
            Wake();
        }
    }

    private void Wake()
    {
        if (_looping || _scene == null) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        _looping = true;
        top.RequestAnimationFrame(Frame);
    }

    private void Frame(TimeSpan _)
    {
        _looping = false;
        if (_scene == null || !IsEffectivelyVisible) return;
        // (nothing moves unseen: a covered window, a sheet over the page)
        bool moving = IsPlaying && !Ui.Hidden && !WaveVisualizer.Covered;
        if ((moving || _dirty) && Bounds.Width > 0 && Bounds.Height > 0 && _scene.Request(Info()))
            _dirty = false;
        if (moving || _dirty) Wake();
    }

    private ModFrameInfo Info()
    {
        var p = Player;
        double speed = p?.Speed ?? 1;
        float sx = 0, sy = 0, sw = 0, sh = 0;
        if (StageOf is { IsEffectivelyVisible: true } stage && stage.Bounds is { Width: > 0, Height: > 0 } b && stage.TranslatePoint(default, this) is { } at)
            (sx, sy, sw, sh) = ((float)at.X, (float)at.Y, (float)b.Width, (float)b.Height);
        return new ModFrameInfo
        {
            StageX = sx, StageY = sy, StageWidth = sw, StageHeight = sh,
            Video = VideoShown,
            Below = (float)Below,
            Time = _clock.Now(p?.EnginePosition.TotalMilliseconds ?? 0, IsPlaying, speed),
            Duration = (p?.Current?.T.Duration ?? 0) * 1000,
            Playing = p?.IsPlaying == true,
            Speed = speed,
            Width = (float)Bounds.Width,
            Height = (float)Bounds.Height,
            Scale = (float)(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1),
            Bands = Spectrum.Shared.Read().ToArray(),
            Accent = Argb("AccentBrush"),
            Text = Argb("TextBrush"),
            Place = _scene?.Visualizer.Place == "bars" ? BarsPlace.ToString().ToLowerInvariant() : "song",
        };
    }

    private static uint Argb(string key) => Ui.Res(key) is ISolidColorBrush b ? b.Color.ToUInt32() : 0xFFFFFFFF;

    public override void Render(DrawingContext context)
    {
        if (_scene?.Latest is { } list) context.Custom(new Op(new Rect(Bounds.Size), list));
    }

    private sealed class Op(Rect bounds, DrawList list) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => other is Op o && ReferenceEquals(o.List, List);
        public void Dispose() { }
        private DrawList List => list;

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            int saved = canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, (float)bounds.Width, (float)bounds.Height));
            ModSkia.Draw(canvas, list);
            canvas.RestoreToCount(saved);
        }
    }
}

// A "page" visualizer's video (visualizers.video: an osu! beatmap's) in a VideoView under its canvas: the file it asked for, from
// where the mod says it starts; the canvas tells the mod when it shows (f.video), the mod drawing its picture until then.
// ffmpeg decodes any kind here (the iPhone's own decoder: MP4 and MOV only, the others stay the picture).
public sealed class ModSceneVideo
{
    private readonly VideoView _video;
    private readonly ModCanvasView _canvas;
    private readonly Func<PlayerViewModel?> _player;
    private ModVisualizer? _of;
    private double _offset;
    private bool _opened;
    private DispatcherTimer? _timer;

    public ModSceneVideo(VideoView video, ModCanvasView canvas, Func<PlayerViewModel?> player)
    {
        _video = video;
        _canvas = canvas;
        _player = player;
        video.Clock = () => (_player()?.EnginePosition ?? TimeSpan.Zero) - TimeSpan.FromMilliseconds(_offset);
        video.MediaOpened += () =>
        {
            _opened = true;
            Check();
        };
    }

    // The visualizer over the page now (null: none, or the page gone).
    public void Watch(ModVisualizer? v)
    {
        if (v != _of)
        {
            if (_of != null) _of.VideoChanged -= OnVideo;
            _of = v;
            if (v != null) v.VideoChanged += OnVideo;
        }
        OnVideo();
    }

    private void OnVideo() => Dispatcher.UIThread.Post(() =>
    {
        var want = _of?.Video;
        _offset = want?.Offset ?? 0;
        if (want?.Path != _video.Source)
        {
            _opened = false;
            _video.Source = want?.Path;
        }
        Check();
        if (want == null)
        {
            _timer?.Stop();
            return;
        }
        _timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Check());
        _timer.Start();
    });

    private void Check() =>
        _canvas.VideoShown = _opened && _video.Source != null && (_player()?.EnginePosition.TotalMilliseconds ?? 0) >= _offset;
}
