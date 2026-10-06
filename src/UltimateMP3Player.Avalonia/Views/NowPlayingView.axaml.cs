using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// The song's page (the Windows app's NowPlayingView): the cover tilting in 3D with the mouse, the video synced to the
// audio, the lyrics, "next up" with drag & drop and the bin.
public partial class NowPlayingView : UserControl
{
    private const double MaxTilt = 11;
    // The Windows app's camera: 4.29 units away from a cover 2 units wide, i.e. 858 px from a 400 px one.
    private const double Depth = 858;

    private NowPlayingViewModel? _vm;
    private Window? _window;
    private readonly Rotate3DTransform _tilt = new(0, 0, 0, 0, 0, 0, Depth);
    private readonly Tweener _glow, _lyricsIn, _coverIn, _follow, _paneWidth, _queueShift, _queueFade, _showQueueFade;
    private readonly TranslateTransform _queueShiftT = new();

    public NowPlayingView()
    {
        InitializeComponent();
        Tilt.RenderTransform = _tilt;
        QueueCard.RenderTransform = _queueShiftT;
        _glow = new Tweener(this, v => LyricsGlow.Opacity = v);
        _lyricsIn = new Tweener(this, v => LyricsHost.Opacity = v, 1);
        _coverIn = new Tweener(this, v => CoverArea.Opacity = v, 1);
        _follow = new Tweener(this, v => FollowBtn.Opacity = v);
        _paneWidth = new Tweener(this, v => QueuePane.Width = v, PaneOpen);
        _queueShift = new Tweener(this, v => _queueShiftT.X = v);
        _queueFade = new Tweener(this, v => QueueCard.Opacity = v, 1);
        _showQueueFade = new Tweener(this, v => ShowQueueBtn.Opacity = v);
        SetupTrash();

        AttachedToVisualTree += (_, _) => OnLoaded();
        DetachedFromVisualTree += (_, _) => OnUnloaded();
        DataContextChanged += (_, _) => Attach(DataContext as NowPlayingViewModel);
        Video.Clock = () => _vm?.Player.EnginePosition ?? TimeSpan.Zero;
        Video.MediaOpened += FitVideo;
        Video.FrameChanged += () => BackdropVideo.Source = Video.Picture;
        LyricsPane.CanSeek = () => _vm?.Player.SeekCommand.CanExecute(null) == true;
        LyricsPane.SeekRequested += t => _vm?.Player.Seek(t);
        LyricsPane.FollowingChanged += ShowFollowButton;
        FollowBtn.Click += (_, _) => LyricsPane.Follow();
        VideoArea.SizeChanged += (_, _) => FitVideo();
        CoverHit.PointerMoved += Cover_MouseMove;
        CoverHit.PointerExited += (_, _) =>
        {
            _mouseX = _mouseY = _glareTarget = 0;
            StartTicking();
        };
        LyricsHost.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && LyricsHost.IsVisible) LyricsPane.OnShown();
        };
        foreach (var c in new Control[] { CoverHit, VideoArea, LyricsPane, TitleBlock })
            c.AddHandler(PointerReleasedEvent, Current_RightClick);
        MoreButton.Click += (_, _) =>
        {
            if (_vm?.Player.Current is { } t) Menus.Open(Menus.ForTrack(t, null), MoreButton, true);
        };
        QueueList.AddHandler(PointerPressedEvent, Row_MouseDown, RoutingStrategies.Tunnel);
        QueueList.AddHandler(PointerMovedEvent, Row_MouseMove, RoutingStrategies.Tunnel);
        foreach (var target in new Control[] { QueueList, TrashZone })
        {
            target.AddHandler(DragDrop.DragOverEvent, Queue_DragOver);
            target.AddHandler(DragDrop.DropEvent, Queue_Drop);
        }
    }

    // ------------------------------------------------------------------ video frame

    // The frame takes the video's own proportions, so there are no black bars.
    private void FitVideo()
    {
        double aw = VideoArea.Bounds.Width, ah = VideoArea.Bounds.Height;
        if (aw <= 0 || ah <= 0) return;
        double ratio = Video.NaturalVideoWidth > 0 && Video.NaturalVideoHeight > 0 ? (double)Video.NaturalVideoWidth / Video.NaturalVideoHeight : 16.0 / 9;
        double w = Math.Min(aw, ah * ratio), h = w / ratio;
        VideoFrame.Width = w;
        VideoFrame.Height = h;
    }

    private void OnLoaded()
    {
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window != null) _window.PropertyChanged += OnWindowState;
        Attach(DataContext as NowPlayingViewModel);
        WatchCover();
        Update();
        PlaceQueue(false);
        _lyricsShown = null;
        ShowLyricsText();
    }

    private void OnUnloaded()
    {
        if (_window != null) _window.PropertyChanged -= OnWindowState;
        Attach(null);
        CloseVideo();
        _ticking = false;
        if (_coverTrack != null) _coverTrack.PropertyChanged -= OnCoverChanged;
        _coverTrack = null;
    }

    private void Attach(NowPlayingViewModel? vm)
    {
        if (_vm == vm) return;
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.Player.PropertyChanged -= OnPlayerChanged;
        }
        _vm = vm;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnVmChanged;
            _vm.Player.PropertyChanged += OnPlayerChanged;
        }
    }

    private void OnWindowState(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty) Update();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NowPlayingViewModel.VideoPath) or nameof(NowPlayingViewModel.ShowVideo)) Update();
        else if (e.PropertyName == nameof(NowPlayingViewModel.QueueHidden)) PlaceQueue(true);
        else if (e.PropertyName == nameof(NowPlayingViewModel.LyricsVisible)) ShowLyricsText();
    }

    // ------------------------------------------------------------------ lyrics

    // Which lyrics are shown: song, kind and file time (a new search replaces them).
    private string? _lyricsKey;
    private bool? _lyricsShown;
    private const double GlowOpacity = 1;

    private void ShowLyricsText()
    {
        var t = _vm?.Player.Current?.T;
        bool visible = _vm?.LyricsVisible == true && t != null;
        string? key = visible ? $"{t!.Id}|{t.Lyrics}|{FileStamp(t)}" : null;
        if (key != _lyricsKey)
        {
            _lyricsKey = key;
            var lyrics = visible ? LyricsStore.Load(t!) : null;
            LyricsPane.SetLyrics(lyrics);
            Captions.SetLyrics(lyrics);
        }
        PushClock();
        // Subtitles on the video leave the stage as it is: only the lyrics page fades in.
        visible = visible && _vm!.LyricsPage;
        if (visible == _lyricsShown) return;
        bool first = _lyricsShown == null;
        _lyricsShown = visible;
        if (!visible) ShowFollowButton(true);
        if (first || !Ui.Animations || VisualRoot == null)
        {
            _glow.Set(visible ? GlowOpacity : 0);
            return;
        }
        // Cross-fade: what comes in fades up, the glow of the cover's colours spreads behind the lyrics.
        (visible ? _lyricsIn : _coverIn).To(1, visible ? 320 : 260, Ui.CubicOut, from: 0);
        _glow.To(visible ? GlowOpacity : 0, visible ? 500 : 200);
    }

    private static long FileStamp(Track t)
    {
        try { return File.GetLastWriteTimeUtc(LyricsStore.PathFor(t.Id, t.Lyrics == LyricsKind.Synced)).Ticks; }
        catch { return 0; }
    }

    // The song's position (4 times a second), playing or not, speed: the lyrics run on by themselves in between.
    private void PushClock()
    {
        if (_vm == null) return;
        var p = _vm.Player;
        LyricsPane.SetClock(p.Position, p.IsPlaying, p.Speed);
        Captions.SetClock(p.Position, p.IsPlaying, p.Speed);
    }

    private void ShowFollowButton(bool following)
    {
        bool show = !following && LyricsPane.IsSynced;
        if (!Ui.Animations)
        {
            FollowBtn.IsVisible = show;
            _follow.Set(1);
            return;
        }
        if (show) FollowBtn.IsVisible = true;
        _follow.To(show ? 1 : 0, show ? 200 : 140, completed: () =>
        {
            if (!show && (LyricsPane.IsFollowing || !LyricsPane.IsSynced)) FollowBtn.IsVisible = false;
        });
    }

    // ------------------------------------------------------------------ folding "next up" away

    // Pane width with the card, and without (the gap at the page edge stays).
    private const double PaneOpen = 350, PaneClosed = 20;

    // The pane narrows while the card slides out to the right; the stage takes the room and a pill brings it back.
    private void PlaceQueue(bool animate)
    {
        bool hidden = _vm?.QueueHidden == true;
        double width = hidden ? PaneClosed : PaneOpen, shift = hidden ? PaneOpen : 0;
        QueueCard.IsVisible = ShowQueueBtn.IsVisible = true;
        void Settle()
        {
            if ((_vm?.QueueHidden == true) != hidden) return;
            QueueCard.IsVisible = !hidden;
            ShowQueueBtn.IsVisible = hidden;
        }
        if (!animate || !Ui.Animations)
        {
            _paneWidth.Set(width);
            _queueShift.Set(shift);
            _queueFade.Set(hidden ? 0 : 1);
            _showQueueFade.Set(hidden ? 1 : 0);
            Settle();
            return;
        }
        double time = hidden ? 280 : 320;
        _paneWidth.To(width, time, Ui.CubicInOut, completed: Settle);
        _queueShift.To(shift, time, Ui.CubicInOut);
        _queueFade.To(hidden ? 0 : 1, time);
        if (hidden) _showQueueFade.To(1, 220, delay: 160);
        else _showQueueFade.To(0, 120);
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.Current))
        {
            _vm?.Refresh();
            WatchCover();
            Update();
            ShowLyricsText();
        }
        else if (e.PropertyName is nameof(PlayerViewModel.IsPlaying) or nameof(PlayerViewModel.Position) or nameof(PlayerViewModel.Speed)) PushClock();
    }

    // The video plays only while the page shows it and the window isn't minimized.
    private void Update()
    {
        var path = _vm?.VideoPath;
        bool show = path != null && VisualRoot != null && _window?.WindowState != WindowState.Minimized;
        if (!show)
        {
            CloseVideo();
            return;
        }
        Video.Source = path;
    }

    private void CloseVideo()
    {
        Video.Source = null;
        BackdropVideo.Source = null;
    }

    // ------------------------------------------------------------------ 3D cover

    // Targets from the mouse; the angles glide to them every frame.
    private double _mouseX, _mouseY, _nowX, _nowY, _glare, _glareTarget;
    private bool _ticking;
    private TimeSpan _lastFrame;
    // The streaks lean 28°.
    private static readonly double SheenCos = Math.Cos(28 * Math.PI / 180), SheenSin = Math.Sin(28 * Math.PI / 180);

    private void Cover_MouseMove(object? sender, PointerEventArgs e)
    {
        if (_vm?.CoverTilt != true) return;
        var p = e.GetPosition(CoverHit);
        _mouseX = Math.Clamp(p.X / CoverHit.Bounds.Width * 2 - 1, -1, 1);
        _mouseY = Math.Clamp(p.Y / CoverHit.Bounds.Height * 2 - 1, -1, 1);
        _glareTarget = 1;
        StartTicking();
    }

    private void StartTicking()
    {
        if (_ticking) return;
        _ticking = true;
        _lastFrame = TimeSpan.Zero;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan now)
    {
        if (!_ticking) return;
        double dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Min(0.1, (now - _lastFrame).TotalSeconds);
        _lastFrame = now;
        double k = 1 - Math.Exp(-dt / 0.075), kg = 1 - Math.Exp(-dt / 0.12);
        // _nowX/_nowY: the mouse as the cover follows it (-1..1).
        _nowX += (_mouseX - _nowX) * k;
        _nowY += (_mouseY - _nowY) * k;
        _glare += (_glareTarget - _glare) * kg;
        // The side under the mouse goes down, like the Windows app's 3D rotation.
        _tilt.AngleY = _nowX * MaxTilt;
        _tilt.AngleX = -_nowY * MaxTilt;
        Sheen.Place(_nowX, _nowY, SheenCos, SheenSin);
        Sheen.Opacity = _glare;
        // Arrived: stop until the mouse moves again.
        bool settled = Math.Abs(_mouseX - _nowX) < 0.001 && Math.Abs(_mouseY - _nowY) < 0.001 && Math.Abs(_glareTarget - _glare) < 0.005;
        if (settled)
        {
            if (_glareTarget == 0)
            {
                _tilt.AngleX = _tilt.AngleY = _nowX = _nowY = 0;
                Sheen.Opacity = _glare = 0;
            }
            _ticking = false;
            return;
        }
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
    }

    // The cover becomes one image, redrawn only when the song or its cover changes.
    private TrackViewModel? _coverTrack;

    private void WatchCover()
    {
        var t = _vm?.Player.Current;
        if (_coverTrack != t)
        {
            if (_coverTrack != null) _coverTrack.PropertyChanged -= OnCoverChanged;
            _coverTrack = t;
            if (t != null) t.PropertyChanged += OnCoverChanged;
        }
        BakeCover();
    }

    private void OnCoverChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackViewModel.Cover300)) BakeCover();
    }

    private void BakeCover()
    {
        const double size = 400, radius = 14;
        var rect = new Rect(0, 0, size, size);
        var img = _coverTrack?.Cover300 as Bitmap;
        // Drawn at twice the size (sharp on HiDPI screens), in pixels.
        var bmp = new RenderTargetBitmap(new PixelSize(800, 800), new Vector(96, 96));
        using (var ctx = bmp.CreateDrawingContext())
        using (ctx.PushTransform(Matrix.CreateScale(2, 2)))
        {
            var dc = ctx;
            using (dc.PushClip(new RoundedRect(rect, radius)))
            {
                if (img is { PixelSize.Width: > 0, PixelSize.Height: > 0 })
                {
                    double scale = Math.Max(size / img.Size.Width, size / img.Size.Height);
                    double w = img.Size.Width * scale, h = img.Size.Height * scale;
                    using (dc.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
                        dc.DrawImage(img, new Rect(0, 0, img.Size.Width, img.Size.Height), new Rect((size - w) / 2, (size - h) / 2, w, h));
                }
                else
                {
                    dc.FillRectangle(Ui.Find<IBrush>("PlaceholderGradient"), rect);
                    var note = new FormattedText(Icons.Map(""), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface(Icons.Regular), 110, Ui.Res("MutedBrush"));
                    dc.DrawText(note, new Point((size - note.Width) / 2, (size - note.Height) / 2));
                }
            }
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)), 1.5), new Rect(0.75, 0.75, size - 1.5, size - 1.5), radius, radius);
        }
        CoverImage.Source = bmp;
        LyricsGlow.Background = new ImageBrush(BakeGlow(img)) { Stretch = Stretch.UniformToFill };
    }

    // Behind the lyrics, like Spotify: the cover's strongest colour, with a very blurred copy of the cover over it.
    private static Bitmap BakeGlow(Bitmap? img)
    {
        const int size = 120, spill = 40;
        bool hasImage = img is { PixelSize.Width: > 0, PixelSize.Height: > 0 };
        var mood = hasImage ? Mood(img!) : Darken((Ui.Res("AccentBrush") as ISolidColorBrush)?.Color ?? Colors.MediumPurple);
        // Drawn with Skia: Avalonia's effects (the blur) only exist on screen, not in a picture made off it.
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(size, size, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(new SkiaSharp.SKColor(mood.R, mood.G, mood.B, mood.A));
        // Larger than the picture, so the blur doesn't pull the edges in; half opaque over the colour, very blurred
        // (WPF's blur of radius 34).
        var dest = new SkiaSharp.SKRect(-spill, -spill, size + spill, size + spill);
        using var paint = new SkiaSharp.SKPaint
        {
            Color = new SkiaSharp.SKColor(255, 255, 255, 128),
            ImageFilter = SkiaSharp.SKImageFilter.CreateBlur(12, 12),
            FilterQuality = SkiaSharp.SKFilterQuality.High,
            IsAntialias = true,
        };
        SkiaSharp.SKBitmap? cover = null;
        if (hasImage)
        {
            try
            {
                using var ms = new MemoryStream();
                img!.Save(ms);
                ms.Position = 0;
                cover = SkiaSharp.SKBitmap.Decode(ms);
            }
            catch { cover = null; }
        }
        if (cover != null)
        {
            // Uniform to fill.
            float scale = Math.Max(dest.Width / cover.Width, dest.Height / cover.Height);
            float w = cover.Width * scale, h = cover.Height * scale;
            canvas.DrawBitmap(cover, new SkiaSharp.SKRect(dest.MidX - w / 2, dest.MidY - h / 2, dest.MidX + w / 2, dest.MidY + h / 2), paint);
            cover.Dispose();
        }
        else
        {
            paint.Shader = SkiaSharp.SKShader.CreateLinearGradient(new SkiaSharp.SKPoint(dest.Left, dest.Top), new SkiaSharp.SKPoint(dest.Right, dest.Bottom),
                new[] { new SkiaSharp.SKColor(0x2A, 0x2F, 0x3A), new SkiaSharp.SKColor(0x1B, 0x1E, 0x25) }, SkiaSharp.SKShaderTileMode.Clamp);
            canvas.DrawRect(dest, paint);
        }
        using var image = surface.Snapshot();
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        using var stream = data.AsStream();
        return new Bitmap(stream);
    }
    // The cover's colour: its pixels averaged, the vivid ones counting more (a grey sky doesn't win over a red dress).
    private static Color Mood(Bitmap img)
    {
        try
        {
            using var small = img.CreateScaledBitmap(new PixelSize(24, 24), BitmapInterpolationMode.MediumQuality);
            int w = 24, h = 24;
            var px = new byte[w * h * 4];
            unsafe
            {
                fixed (byte* p = px) small.CopyPixels(new PixelRect(0, 0, w, h), (IntPtr)p, px.Length, w * 4);
            }
            // CopyPixels gives the bitmap's own format: BGRA on every platform the app runs on.
            double r = 0, g = 0, b = 0, sum = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                var (_, s, l) = ToHsl(px[i + 2] / 255.0, px[i + 1] / 255.0, px[i] / 255.0);
                double weight = 0.03 + s * s * Math.Max(0, 1 - Math.Abs(l - 0.5) * 1.7);
                r += px[i + 2] * weight;
                g += px[i + 1] * weight;
                b += px[i] * weight;
                sum += weight;
            }
            return Darken(Color.FromRgb((byte)(r / sum), (byte)(g / sum), (byte)(b / sum)));
        }
        catch { return Color.FromRgb(0x2A, 0x24, 0x3A); }
    }

    // Vivid enough to be a colour, dark enough for white text.
    private static Color Darken(Color c)
    {
        var (hue, s, _) = ToHsl(c.R / 255.0, c.G / 255.0, c.B / 255.0);
        s = s < 0.08 ? s : Math.Clamp(s * 1.25, 0.3, 0.78);
        return FromHsl(hue, s, 0.3);
    }

    private static (double H, double S, double L) ToHsl(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        if (d < 1e-6) return (0, 0, l);
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        double Channel(double t)
        {
            t = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
            return t < 1 / 6.0 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
        }
        return Color.FromRgb((byte)Math.Round(Channel(h + 1 / 3.0) * 255), (byte)Math.Round(Channel(h) * 255), (byte)Math.Round(Channel(h - 1 / 3.0) * 255));
    }

    // ------------------------------------------------------------------ menus

    private void Current_RightClick(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right || e.Handled || _vm?.Player.Current is not { } t || sender is not Control c) return;
        Menus.Open(Menus.ForTrack(t, null), c, false);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ reordering the queue

    private Point? _dragStart;

    private static Border? RowOf(object? source)
    {
        for (var v = source as Visual; v != null; v = v.GetVisualParent())
            if (v is Border { Classes: var c } b && c.Contains("qrow")) return b;
        return null;
    }

    private void Row_MouseDown(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && RowOf(e.Source) != null) _dragStart = e.GetPosition(null);
    }

    private async void Row_MouseMove(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _dragStart is not Point start) return;
        if (Ui.FindAncestor<Button>(e.Source as Visual) != null) return;
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - start.X) < 6 && Math.Abs(p.Y - start.Y) < 6) return;
        _dragStart = null;
        if (RowOf(e.Source) is not { DataContext: QueueRow row } fe || _vm == null) return;
        var session = new DragSession(QueueList, row, TrashZone);
        session.TrashHot += SetTrashHot;
        ShowTrash(true);
        var data = new DataObject();
        data.Set(Rows.QueueRowFormat, row);
        var outcome = await DragVisuals.RunAsync(fe, e, data, DragDropEffects.Move, session);
        ShowTrash(false);
        // The song lands where the line was.
        if (outcome == DragOutcome.Trashed) _vm.Player.RemoveUpcoming(row.Index);
        else if (outcome == DragOutcome.Placed && session.Target is QueueRow target)
        {
            int last = _vm.Player.UpNext.LastOrDefault()?.Index ?? row.Index;
            _vm.Player.MoveUpcoming(row.Index, Math.Clamp(DragVisuals.MoveIndex(row.Index, target.Index, session.After), 0, last));
        }
    }

    private void Queue_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(Rows.QueueRowFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    // The move itself happens when the drag ends (Row_MouseMove).
    private void Queue_Drop(object? sender, DragEventArgs e) => e.Handled = e.Data.Contains(Rows.QueueRowFormat);

    // ------------------------------------------------------------------ the bin

    private static readonly Color TrashIdle = Color.FromArgb(0xF2, 0x2A, 0x12, 0x16), TrashHotColor = Color.FromArgb(0xF2, 0xD4, 0x32, 0x3A);
    private static readonly IBrush TrashIconIdle = Ui.BrushFrom("#FF7076");
    private readonly ScaleTransform _trashScale = new(1, 1), _trashPop = new(1, 1), _rippleScale = new(1, 1);
    private readonly TranslateTransform _trashShift = new();
    private readonly RotateTransform _trashTilt = new();
    private Tweener _pop = null!, _shift = null!, _zoneFade = null!, _hot = null!, _hotScale = null!;
    private bool _trashShown, _rippling, _wiggling;

    private void SetupTrash()
    {
        TrashZone.RenderTransform = new TransformGroup { Children = { _trashScale, _trashPop, _trashShift } };
        TrashRipple.RenderTransform = _rippleScale;
        TrashIcon.RenderTransform = _trashTilt;
        _pop = new Tweener(this, v => _trashPop.ScaleX = _trashPop.ScaleY = v, 1);
        _shift = new Tweener(this, v => _trashShift.X = v);
        _zoneFade = new Tweener(this, v => TrashZone.Opacity = v, 1);
        // 0 = idle, 1 = hot (red)
        _hot = new Tweener(this, v => TrashDisc.Background = new SolidColorBrush(Mix(TrashIdle, TrashHotColor, v)));
        _hotScale = new Tweener(this, v => _trashScale.ScaleX = _trashScale.ScaleY = v, 1);
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    // The bin pops out of the queue's edge, pulses while waiting and shrinks away at the end.
    private void ShowTrash(bool show)
    {
        _trashShown = show;
        if (show)
        {
            _hotScale.Set(1);
            _wiggling = false;
            _trashTilt.Angle = 0;
            _hot.Set(0);
            TrashIcon.Foreground = TrashIconIdle;
        }
        if (!Ui.Animations)
        {
            TrashZone.IsVisible = show;
            return;
        }
        TrashZone.IsVisible = true;
        double time = show ? 300 : 170;
        if (show) _pop.To(1, time, t => Ui.BackOut(t, 0.6), from: 0.3);
        else _pop.To(0.25, time, t => Ui.BackIn(t, 0.4), completed: () =>
        {
            if (_trashShown) return;
            TrashZone.IsVisible = false;
            _rippling = false;
        });
        if (show) _shift.To(0, time, Ui.CubicOut, from: 40);
        if (show) _zoneFade.To(1, 160, from: 0);
        else _zoneFade.To(0, 150);
        if (show) Ripple();
    }

    // A ring spreading out from the bin, over and over.
    private void Ripple()
    {
        if (_rippling) return;
        _rippling = true;
        TimeSpan? start = null;
        void Frame(TimeSpan now)
        {
            if (!_rippling)
            {
                TrashRipple.Opacity = 0;
                return;
            }
            start ??= now;
            double t = (now - start.Value).TotalMilliseconds % 1200 / 1200;
            _rippleScale.ScaleX = _rippleScale.ScaleY = 1 + 0.75 * Ui.CubicOut(t);
            TrashRipple.Opacity = 0.8 * (1 - t);
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Frame);
        }
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Frame);
    }

    // Over the bin: it grows, turns red and wiggles.
    private void SetTrashHot(bool hot)
    {
        TrashIcon.Foreground = hot ? Brushes.White : TrashIconIdle;
        if (!Ui.Animations)
        {
            _hot.Set(hot ? 1 : 0);
            _hotScale.Set(hot ? 1.2 : 1);
            return;
        }
        _hot.To(hot ? 1 : 0, 140);
        _hotScale.To(hot ? 1.22 : 1, 200, t => Ui.BackOut(t, 0.6));
        if (hot) Wiggle();
        else
        {
            _wiggling = false;
            _trashTilt.Angle = 0;
        }
    }

    private void Wiggle()
    {
        if (_wiggling) return;
        _wiggling = true;
        (double T, double A)[] keys = { (0, 0), (80, -14), (180, 12), (280, -8), (360, 4), (420, 0) };
        TimeSpan? start = null;
        void Frame(TimeSpan now)
        {
            if (!_wiggling) return;
            start ??= now;
            double ms = (now - start.Value).TotalMilliseconds % 420;
            for (int i = 1; i < keys.Length; i++)
            {
                if (ms > keys[i].T) continue;
                double f = (ms - keys[i - 1].T) / (keys[i].T - keys[i - 1].T);
                _trashTilt.Angle = keys[i - 1].A + (keys[i].A - keys[i - 1].A) * f;
                break;
            }
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Frame);
        }
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Frame);
    }
}
