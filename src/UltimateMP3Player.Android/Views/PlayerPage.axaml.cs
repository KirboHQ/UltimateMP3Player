using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class PlayerPage : UserControl
{
    private const double MaxTilt = 11;
    private const double Depth = 858;

    private NowPlayingViewModel? _vm;
    private readonly Rotate3DTransform _tilt = new(0, 0, 0, 0, 0, 0, Depth);
    private readonly TranslateTransform _slide = new(), _swipe = new();
    private readonly Tweener _lyricsIn, _coverIn, _follow;
    private bool _open;

    public PlayerPage()
    {
        InitializeComponent();
        Tilt.RenderTransform = _tilt;
        Root.RenderTransform = _slide;
        CoverBox.RenderTransform = _swipe;
        _lyricsIn = new Tweener(this, v => LyricsHost.Opacity = v, 1);
        _coverIn = new Tweener(this, v => CoverArea.Opacity = v, 1);
        _follow = new Tweener(this, v => FollowBtn.Opacity = v);
        DataContextChanged += (_, _) => Attach(DataContext as NowPlayingViewModel);
        Video.Clock = () => _vm?.Player.EnginePosition ?? TimeSpan.Zero;
        Video.MediaOpened += FitVideo;
        Stage.SizeChanged += (_, _) =>
        {
            FitCover();
            FitVideo();
        };
        Body.SizeChanged += (_, _) => Arrange();
        LyricsPane.CanSeek = () => _vm?.Player.SeekCommand.CanExecute(null) == true;
        LyricsPane.SeekRequested += t => _vm?.Player.Seek(t);
        LyricsPane.FollowingChanged += ShowFollowButton;
        FollowBtn.Click += (_, _) => LyricsPane.Follow();
        LyricsHost.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && LyricsHost.IsVisible) LyricsPane.OnShown();
        };
        foreach (var area in new Control[] { TopBar, Stage, TitleBlock })
        {
            area.AddHandler(PointerPressedEvent, Pressed, RoutingStrategies.Tunnel);
            area.AddHandler(PointerMovedEvent, Moved, RoutingStrategies.Tunnel);
            area.AddHandler(PointerReleasedEvent, Released, RoutingStrategies.Tunnel);
            area.AddHandler(PointerCaptureLostEvent, (_, _) => Cancel());
        }
        // The Android back of the video decodes at most 854×480 (the phone's processor and battery).
        VideoView.FrameMaxWidth = 854;
        VideoView.FrameMaxHeight = 480;
        // Small frames: a 60 fps video can stay at 60 (the screens of phones go to 120 and more).
        VideoView.FrameMaxFps = 60;
    }

    public bool IsOpen => _open;
    public event Action? CloseRequested;

    // The status bar, the system's buttons or gesture line, a notch, the buttons on a side in landscape.
    public void SetInsets(Thickness insets) => Body.Margin = insets;

    // Turned (or a tablet): the cover on the left, the title and the buttons on the right, centred in the height (as on
    // the computer's song page); upright, one under the other.
    private bool? _wide;

    private void Arrange()
    {
        double w = Body.Bounds.Width, h = Body.Bounds.Height;
        if (w <= 0 || h <= 0) return;
        bool wide = w >= 560 && w > h * 1.15;
        if (wide == _wide) return;
        _wide = wide;
        Body.ColumnDefinitions = wide ? new ColumnDefinitions("*,*") : new ColumnDefinitions("*");
        Body.RowDefinitions = wide ? new RowDefinitions("Auto,*,Auto,Auto,Auto,Auto,*") : new RowDefinitions("Auto,*,Auto,Auto,Auto,Auto");
        Grid.SetColumnSpan(TopBar, wide ? 2 : 1);
        Grid.SetRowSpan(Stage, wide ? 6 : 1);
        foreach (var c in new Control[] { TitleBlock, SeekBlock, Buttons, Extras }) Grid.SetColumn(c, wide ? 1 : 0);
        Stage.Margin = wide ? new Thickness(24, 4, 16, 16) : new Thickness(24, 8, 24, 8);
        TitleBlock.Margin = wide ? new Thickness(16, 4, 14, 6) : new Thickness(24, 4, 14, 6);
        SeekBlock.Margin = wide ? new Thickness(16, 2, 24, 0) : new Thickness(24, 2, 24, 0);
        Buttons.Margin = wide ? new Thickness(4, 8, 12, 2) : new Thickness(12, 10, 12, 4);
        Extras.Margin = wide ? new Thickness(6, 2, 14, 6) : new Thickness(14, 4, 14, 10);
    }

    // ------------------------------------------------------------------ in and out

    public void Open(bool animate)
    {
        if (_open) return;
        _open = true;
        IsVisible = true;
        _vm?.Refresh();
        WatchCover();
        UpdateVideo();
        _lyricsShown = null;
        ShowLyricsText();
        double h = Math.Max(Bounds.Height, (Parent as Control)?.Bounds.Height ?? 800);
        if (!animate || !Ui.Animations)
        {
            _slide.Y = 0;
            return;
        }
        _slide.Y = h;
        Ui.Tween(this, 320, Ui.CubicOut, t => { if (_open) _slide.Y = h * (1 - t); });
    }

    public void Close(bool animate)
    {
        if (!_open) return;
        _open = false;
        CloseVideo();
        double from = _slide.Y, h = Math.Max(Bounds.Height, 800);
        if (!animate || !Ui.Animations)
        {
            IsVisible = false;
            _slide.Y = 0;
            return;
        }
        Ui.Tween(this, 240, t => t * t, t =>
        {
            if (_open) return;
            _slide.Y = from + (h - from) * t;
            if (t >= 1) IsVisible = false;
        });
    }

    // Back: nothing of its own to undo (the sheets are the main view's).
    public bool Back() => false;

    // ------------------------------------------------------------------ view model

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
        if (_open)
        {
            WatchCover();
            UpdateVideo();
            ShowLyricsText();
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NowPlayingViewModel.VideoPath) or nameof(NowPlayingViewModel.ShowVideo)) UpdateVideo();
        else if (e.PropertyName == nameof(NowPlayingViewModel.LyricsVisible)) ShowLyricsText();
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.Current))
        {
            _vm?.Refresh();
            WatchCover();
            UpdateVideo();
            ShowLyricsText();
        }
        else if (e.PropertyName is nameof(PlayerViewModel.IsPlaying) or nameof(PlayerViewModel.Position) or nameof(PlayerViewModel.Speed)) PushClock();
    }

    // ------------------------------------------------------------------ cover

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
        Bake();
    }

    private void OnCoverChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackViewModel.Cover300)) Bake();
    }

    private void Bake()
    {
        if (!_open) return;
        var img = _coverTrack?.Cover300 as Bitmap;
        CoverImage.Source = CoverArt.Bake(img);
        Glow.Source = CoverArt.Glow(img);
    }

    // The cover as big as the stage allows, square.
    private void FitCover()
    {
        double side = Math.Max(0, Math.Min(Stage.Bounds.Width, Stage.Bounds.Height));
        CoverBox.Width = CoverBox.Height = side;
    }

    // ------------------------------------------------------------------ video

    private void UpdateVideo()
    {
        var path = _vm?.VideoPath;
        if (path == null || !_open)
        {
            CloseVideo();
            return;
        }
        Video.Source = path;
    }

    private void CloseVideo() => Video.Source = null;

    private void FitVideo()
    {
        double aw = Stage.Bounds.Width, ah = Stage.Bounds.Height;
        if (aw <= 0 || ah <= 0) return;
        double ratio = Video.NaturalVideoWidth > 0 && Video.NaturalVideoHeight > 0 ? (double)Video.NaturalVideoWidth / Video.NaturalVideoHeight : 16.0 / 9;
        double w = Math.Min(aw, ah * ratio), h = w / ratio;
        VideoFrame.Width = w;
        VideoFrame.Height = h;
    }

    // ------------------------------------------------------------------ lyrics

    private string? _lyricsKey;
    private bool? _lyricsShown;

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
        visible = visible && _vm!.LyricsPage;
        if (visible == _lyricsShown) return;
        bool first = _lyricsShown == null;
        _lyricsShown = visible;
        if (!visible) ShowFollowButton(true);
        if (first || !Ui.Animations) return;
        (visible ? _lyricsIn : _coverIn).To(1, visible ? 320 : 260, Ui.CubicOut, from: 0);
    }

    private static long FileStamp(Track t)
    {
        try { return File.GetLastWriteTimeUtc(LyricsStore.PathFor(t.Id, t.Lyrics == LyricsKind.Synced)).Ticks; }
        catch { return 0; }
    }

    private void PushClock()
    {
        if (_vm == null || !_open) return;
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

    // ------------------------------------------------------------------ the finger: tilt, close, skip

    private enum Gesture { None, Tilt, Close, Swipe }

    private Point? _start;
    private long _startAt;
    private Gesture _gesture;
    private Control? _area;

    private bool OnCover(Point p)
    {
        if (_vm?.ShowCover != true) return false;
        var local = Stage.TranslatePoint(p, CoverBox) ?? default;
        return local.X >= 0 && local.Y >= 0 && local.X <= CoverBox.Bounds.Width && local.Y <= CoverBox.Bounds.Height;
    }

    private void Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control area || Ui.FindAncestor<Button>(e.Source as Visual) != null && area != Stage) return;
        if (e.Source is Visual v && (Ui.FindAncestor<Button>(v) != null || Ui.FindAncestor<WaveformBar>(v) != null)) return;
        // The lyrics scroll by themselves: only the top bar and the title close from there.
        if (area == Stage && _vm?.LyricsPage == true) return;
        _area = area;
        _start = e.GetPosition(this);
        _startAt = Environment.TickCount64;
        _gesture = Gesture.None;
        if (area == Stage && _vm?.CoverTilt == true && OnCover(e.GetPosition(Stage))) TiltTo(e.GetPosition(CoverBox));
    }

    private void Moved(object? sender, PointerEventArgs e)
    {
        if (_start is not Point s || sender != _area) return;
        var p = e.GetPosition(this);
        double dx = p.X - s.X, dy = p.Y - s.Y;
        if (_gesture is Gesture.None or Gesture.Tilt)
        {
            if (dy > 16 && dy > Math.Abs(dx) * 1.3) _gesture = Gesture.Close;
            else if (Math.Abs(dx) > 16 && Math.Abs(dx) > Math.Abs(dy) * 1.3 && _area == Stage && _vm?.ShowCover == true) _gesture = Gesture.Swipe;
            else if (_area == Stage && _vm?.CoverTilt == true && OnCover(e.GetPosition(Stage))) _gesture = Gesture.Tilt;
            if (_gesture is Gesture.Close or Gesture.Swipe)
            {
                e.Pointer.Capture(_area);
                Untilt();
            }
        }
        switch (_gesture)
        {
            case Gesture.Close:
                _slide.Y = Math.Max(0, dy);
                e.Handled = true;
                break;
            case Gesture.Swipe:
                _swipe.X = dx * 0.7;
                CoverBox.Opacity = 1 - Math.Min(0.6, Math.Abs(dx) / 600);
                e.Handled = true;
                break;
            case Gesture.Tilt:
                TiltTo(e.GetPosition(CoverBox));
                break;
        }
    }

    private void Released(object? sender, PointerReleasedEventArgs e)
    {
        if (_start is not Point s || sender != _area) return;
        _start = null;
        var p = e.GetPosition(this);
        double dx = p.X - s.X, dy = p.Y - s.Y;
        double ms = Math.Max(1, Environment.TickCount64 - _startAt);
        switch (_gesture)
        {
            case Gesture.Close:
                e.Pointer.Capture(null);
                if (dy > 140 || dy / ms > 0.6) CloseRequested?.Invoke();
                else
                {
                    double from = _slide.Y;
                    Ui.Tween(this, 200, Ui.CubicOut, t => { if (_open) _slide.Y = from * (1 - t); });
                }
                break;
            case Gesture.Swipe:
                e.Pointer.Capture(null);
                if (Math.Abs(dx) > 90 || Math.Abs(dx) / ms > 0.6)
                {
                    if (dx < 0) _vm?.Player.NextCommand.Execute(null);
                    // The song before itself, wherever this one is (the button starts it over first).
                    else if (_vm?.Player is { } player && player.PreviousCommand.CanExecute(null)) _ = player.Previous(false);
                    Haptics.Tick();
                }
                SettleSwipe();
                break;
        }
        _gesture = Gesture.None;
        Untilt();
    }

    private void Cancel()
    {
        if (_gesture == Gesture.Close && _start != null)
        {
            double from = _slide.Y;
            Ui.Tween(this, 200, Ui.CubicOut, t => { if (_open) _slide.Y = from * (1 - t); });
        }
        if (_gesture == Gesture.Swipe) SettleSwipe();
        _start = null;
        _gesture = Gesture.None;
        Untilt();
    }

    private void SettleSwipe()
    {
        double from = _swipe.X, op = CoverBox.Opacity;
        Ui.Tween(this, 220, Ui.CubicOut, t =>
        {
            _swipe.X = from * (1 - t);
            CoverBox.Opacity = op + (1 - op) * t;
        });
    }

    // ------------------------------------------------------------------ the 3D cover (the computer apps' tilt, by the finger)

    private static readonly double SheenCos = Math.Cos(28 * Math.PI / 180), SheenSin = Math.Sin(28 * Math.PI / 180);
    private double _targetX, _targetY, _nowX, _nowY, _glare, _glareTarget;
    private bool _ticking;
    private TimeSpan _lastFrame;

    private void TiltTo(Point p)
    {
        double w = Math.Max(1, CoverBox.Bounds.Width), h = Math.Max(1, CoverBox.Bounds.Height);
        _targetX = Math.Clamp(p.X / w * 2 - 1, -1, 1);
        _targetY = Math.Clamp(p.Y / h * 2 - 1, -1, 1);
        _glareTarget = 1;
        StartTicking();
    }

    private void Untilt()
    {
        if (_glareTarget == 0 && _targetX == 0 && _targetY == 0) return;
        _targetX = _targetY = _glareTarget = 0;
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
        _nowX += (_targetX - _nowX) * k;
        _nowY += (_targetY - _nowY) * k;
        _glare += (_glareTarget - _glare) * kg;
        _tilt.AngleY = _nowX * MaxTilt;
        _tilt.AngleX = -_nowY * MaxTilt;
        Sheen.Place(_nowX, _nowY, SheenCos, SheenSin);
        Sheen.Opacity = _glare;
        bool settled = Math.Abs(_targetX - _nowX) < 0.001 && Math.Abs(_targetY - _nowY) < 0.001 && Math.Abs(_glareTarget - _glare) < 0.005;
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

    // ------------------------------------------------------------------ buttons

    private void Close_Click(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void More_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm?.Player.Current is { } t) Menus.Open(Menus.ForTrack(t, null));
    }

    private void AddToPlaylist_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm?.Player.Current is { } t) Menus.Open(Menus.AddToPlaylistMenu(t));
    }

    private void Queue_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm != null) QueueSheet.Show(_vm.Player);
    }

    private void Speed_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm != null) SpeedSheet.Show(_vm.Player);
    }
}
