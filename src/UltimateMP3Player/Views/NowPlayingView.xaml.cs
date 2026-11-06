using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// Video = muted picture synced to the audio, only while visible.
public partial class NowPlayingView : UserControl
{
    private const double MaxTilt = 11;

    private readonly DispatcherTimer _sync = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };
    private NowPlayingViewModel? _vm;
    private Window? _window;
    private string? _source;

    public NowPlayingView()
    {
        InitializeComponent();
        _sync.Tick += (_, _) => Sync(false);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += (_, _) => Attach(DataContext as NowPlayingViewModel);
        Video.MediaOpened += (_, _) =>
        {
            FitVideo();
            Sync(true);
        };
        Video.MediaEnded += (_, _) => { Video.Position = TimeSpan.Zero; };
        LyricsPane.CanSeek = () => _vm?.Player.SeekCommand.CanExecute(null) == true;
        LyricsPane.SeekRequested += t => _vm?.Player.Seek(t);
        LyricsPane.FollowingChanged += ShowFollowButton;
        // ("Up next" folding away over the whole screen: the song takes its room)
        QueuePane.SizeChanged += (_, _) => { if (_vm?.FullScreen == true) PlaceFullScreen(); };
    }

    // ------------------------------------------------------------------ video frame

    private void Stage_SizeChanged(object sender, SizeChangedEventArgs e) => ClipStage();

    private void VideoArea_SizeChanged(object sender, SizeChangedEventArgs e) => FitVideo();

    // The frame takes the video's own proportions, so there are no black bars.
    private void FitVideo()
    {
        double aw = VideoArea.ActualWidth, ah = VideoArea.ActualHeight;
        if (aw <= 0 || ah <= 0) return;
        double ratio = Video.NaturalVideoWidth > 0 && Video.NaturalVideoHeight > 0 ? (double)Video.NaturalVideoWidth / Video.NaturalVideoHeight : 16.0 / 9;
        double w = Math.Min(aw, ah * ratio), h = w / ratio;
        VideoFrame.Width = w;
        VideoFrame.Height = h;
        Video.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 14, 14);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window != null) _window.StateChanged += OnWindowState;
        LayoutUpdated += OnLayoutUpdated;
        Themes.Changed += BakeCover;
        Attach(DataContext as NowPlayingViewModel);
        WatchCover();
        Update();
        PlaceQueue(false);
        PlaceFullScreen();
        _lyricsShown = null;
        ShowLyricsText();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window != null) _window.StateChanged -= OnWindowState;
        LayoutUpdated -= OnLayoutUpdated;
        Themes.Changed -= BakeCover;
        Attach(null);
        CloseVideo();
        StopTicking();
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

    private void OnWindowState(object? sender, EventArgs e) => Update();

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NowPlayingViewModel.VideoPath) or nameof(NowPlayingViewModel.ShowVideo)) Update();
        else if (e.PropertyName == nameof(NowPlayingViewModel.QueueHidden)) PlaceQueue(true);
        else if (e.PropertyName == nameof(NowPlayingViewModel.LyricsVisible)) ShowLyricsText();
        else if (e.PropertyName == nameof(NowPlayingViewModel.FullScreen)) PlaceFullScreen();
    }

    // ------------------------------------------------------------------ over the whole screen

    // The stage loses its frame and reaches under "Up next" and the see-through player bar (its backdrop behind them); the
    // song and its buttons stay clear of both (MainWindow.ApplyFullScreen), the song in the middle of the screen.
    private void PlaceFullScreen()
    {
        bool full = _vm?.FullScreen == true;
        double bar = full && _window is MainWindow w ? w.PlayerBar.ActualHeight : 0;
        Grid.SetColumnSpan(Stage, full ? 2 : 1);
        Stage.Margin = full ? new Thickness(0) : new Thickness(0, 0, _folding ? -(PaneOpen - PaneClosed) : 0, 14);
        foreach (var b in Stage.Children.OfType<Border>()) b.CornerRadius = new CornerRadius(full ? 0 : 16);
        double pane = full ? QueuePane.ActualWidth : 0;
        StageContent.Margin = new Thickness(28, 28, 28, 28 + bar);
        StageFooter.Margin = new Thickness(0, 22, pane, 0);
        CoverArea.Margin = VideoArea.Margin = new Thickness(pane, 0, pane, 0);
        LyricsHost.Margin = new Thickness(0, -8, pane, 0);
        QueueCard.Margin = full ? new Thickness(14, 14, 0, 14 + bar) : new Thickness(14, 0, 0, 14);
        ClipStage();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => CenterSong();

    // The song above the play button, halfway up the stage (over the whole screen: its very middle), whatever stands around it,
    // as far as its place allows.
    // (By a transform: nothing to lay out again, and it stays still while "Up next" folds away.)
    private void CenterSong()
    {
        if (_window is not MainWindow w) return;
        var stage = Stage.TransformToAncestor(this).Transform(new Point(Stage.ActualWidth / 2, Stage.ActualHeight / 2));
        var middle = new Point(w.PagesMiddleX, stage.Y);
        if (CoverArea.IsVisible)
        {
            // (of the box, only the picture counts: the rest is room for its shadow; it may use the gaps above and below its row)
            double picture = CoverBox.ActualWidth * 400 / 460;
            var shift = Toward(CoverArea, picture, picture - 40, middle);
            CoverShift.X = shift.X;
            CoverShift.Y = shift.Y;
            // (the line of "in the center" goes through the middle of the cover)
            Visualizer.Middle = CoverArea.TransformToAncestor(Stage).Transform(new Point(0, CoverArea.ActualHeight / 2)).Y + shift.Y;
        }
        if (VideoArea.IsVisible)
        {
            var shift = Toward(VideoArea, VideoFrame.ActualWidth, VideoFrame.ActualHeight, middle);
            VideoShift.X = shift.X;
            VideoShift.Y = shift.Y;
        }
        // Mirrored bars have their middle there too: what stands past the stage's edge just isn't seen.
        bool mirrored = _vm?.VisualizerFlow is VisualizerFlow.MirrorSides or VisualizerFlow.MirrorCenter;
        double off = mirrored ? Math.Round(2 * (stage.X - middle.X)) : 0;
        Visualizer.Margin = new Thickness(-Math.Max(0, off), 0, Math.Min(0, off), 0);
        // The pictures behind: their middle there, as wide as the farthest edge the stage can have, "Up next" folded or not.
        double left = stage.X - Stage.ActualWidth / 2, reach = (_vm?.FullScreen == true ? ActualWidth : ActualWidth - PaneClosed) - middle.X;
        double half = Math.Max(middle.X - left, reach), at = middle.X - left;
        var back = new Thickness(Math.Round(at - half), 0, Math.Round(Stage.ActualWidth - at - half), 0);
        if (StageBack.Margin != back) StageBack.Margin = back;
    }

    private Vector Toward(FrameworkElement area, double width, double height, Point middle)
    {
        var at = area.TransformToAncestor(this).Transform(new Point(area.ActualWidth / 2, area.ActualHeight / 2));
        static double Within(double want, double room) => Math.Clamp(want, -Math.Max(0, room) / 2, Math.Max(0, room) / 2);
        return new Vector(Within(middle.X - at.X, area.ActualWidth - width), Within(middle.Y - at.Y, area.ActualHeight - height));
    }

    private void ClipStage()
    {
        double r = _vm?.FullScreen == true ? 0 : 16;
        BackClip.Clip = BarsClip.Clip = StageVideo.Clip = new RectangleGeometry(new Rect(Stage.RenderSize), r, r);
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
        if (first || !Ui.Animations || !IsLoaded)
        {
            LyricsGlow.BeginAnimation(OpacityProperty, null);
            LyricsGlow.Opacity = visible ? GlowOpacity : 0;
            return;
        }
        // Cross-fade: what comes in fades up, the glow of the cover's colours spreads behind the lyrics.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        (visible ? (UIElement)LyricsHost : CoverArea).BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, Ui.Time(visible ? 320 : 260)) { EasingFunction = ease });
        LyricsGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(visible ? GlowOpacity : 0, Ui.Time(visible ? 500 : 200)));
    }

    private static long FileStamp(Core.Track t)
    {
        try { return File.GetLastWriteTimeUtc(LyricsStore.PathFor(t.Id, t.Lyrics == LyricsKind.Synced)).Ticks; }
        catch { return 0; }
    }

    // The song's position (4 times a second), playing or not, speed: the lyrics run on by themselves in between.
    private void PushClock()
    {
        if (_vm == null) return;
        var p = _vm.Player;
        LyricsPane.SetClock(p.Position, p.Animate, p.Speed);
        Captions.SetClock(p.Position, p.Animate, p.Speed);
    }

    private void Follow_Click(object sender, RoutedEventArgs e) => LyricsPane.Follow();

    private void ShowFollowButton(bool following)
    {
        bool show = !following && LyricsPane.IsSynced;
        if (!Ui.Animations)
        {
            FollowBtn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            FollowBtn.Opacity = 1;
            return;
        }
        if (show) FollowBtn.Visibility = Visibility.Visible;
        var fade = new DoubleAnimation(show ? 1 : 0, Ui.Time(show ? 200 : 140));
        if (!show) fade.Completed += (_, _) => { if (LyricsPane.IsFollowing || !LyricsPane.IsSynced) FollowBtn.Visibility = Visibility.Collapsed; };
        FollowBtn.BeginAnimation(OpacityProperty, fade);
    }

    // ------------------------------------------------------------------ folding "next up" away

    // Pane width with the card, and without (the gap at the page edge stays). Open: as wide as the sidebar, its mirror (the card
    // as wide as the sidebar's entries, the same 14 on each side), so that the stage stands in the middle of the window.
    private const double PaneOpen = 280, PaneClosed = 14;

    // Folding: the pane at its widest over a stage reaching under it, so that nothing is laid out while it moves (a width animated
    // meant the whole page laid out again at every frame: half a core, a frame in three lost). The stage laid out once, when it's wider;
    // its edge and its buttons go by a clip and a transform, on the render thread.
    private bool _folding;
    private RectangleGeometry? _foldEdge;
    private double _stageWide;
    private readonly TranslateTransform _buttonsShift = new();

    // The card slides out to the right; the stage takes the room and a pill brings it back.
    private void PlaceQueue(bool animate)
    {
        bool hidden = _vm?.QueueHidden == true, full = _vm?.FullScreen == true;
        double width = hidden ? PaneClosed : PaneOpen, shift = hidden ? PaneOpen : 0, room = PaneOpen - PaneClosed;
        QueueCard.Visibility = ShowQueueBtn.Visibility = Visibility.Visible;
        StageButtonsBox.RenderTransform = _buttonsShift;
        void Land()
        {
            _folding = false;
            _foldEdge?.BeginAnimation(RectangleGeometry.RectProperty, null);
            _foldEdge = null;
            Stage.ClearValue(ClipProperty);
            _buttonsShift.BeginAnimation(TranslateTransform.XProperty, null);
            _buttonsShift.X = 0;
            QueuePane.Width = width;
            PlaceFullScreen();
        }
        void Settle()
        {
            if ((_vm?.QueueHidden == true) != hidden) return;
            Land();
            QueueCard.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
            ShowQueueBtn.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
        }
        if (!animate || !Ui.Animations || !IsLoaded)
        {
            QueueShift.BeginAnimation(TranslateTransform.XProperty, null);
            QueueCard.BeginAnimation(OpacityProperty, null);
            ShowQueueBtn.BeginAnimation(OpacityProperty, null);
            QueueShift.X = shift;
            QueueCard.Opacity = hidden ? 0 : 1;
            ShowQueueBtn.Opacity = hidden ? 1 : 0;
            Settle();
            return;
        }
        var time = Ui.Time(hidden ? 280 : 320);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        // Where the buttons look to be, the stage being wide: over the whole screen the stage never changes, the room left to them does.
        double buttonsOpen = full ? 0 : -room, buttonsClosed = buttonsOpen + room;
        double high = Stage.ActualHeight;
        if (!_folding)
        {
            _stageWide = Stage.ActualWidth + (hidden ? room : 0);
            _folding = true;
            _buttonsShift.X = hidden ? buttonsOpen : buttonsClosed;
            QueuePane.Width = PaneOpen;
            PlaceFullScreen();
            if (!full)
            {
                _foldEdge = new RectangleGeometry(new Rect(0, 0, hidden ? _stageWide - room : _stageWide, high), 16, 16);
                Stage.Clip = _foldEdge;
            }
        }
        _foldEdge?.BeginAnimation(RectangleGeometry.RectProperty, new RectAnimation(new Rect(0, 0, hidden ? _stageWide : _stageWide - room, high), time) { EasingFunction = ease });
        var buttons = new DoubleAnimation(hidden ? buttonsClosed : buttonsOpen, time) { EasingFunction = ease };
        buttons.Completed += (_, _) => Settle();
        _buttonsShift.BeginAnimation(TranslateTransform.XProperty, buttons);
        QueueShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(shift, time) { EasingFunction = ease });
        QueueCard.BeginAnimation(OpacityProperty, new DoubleAnimation(hidden ? 0 : 1, time));
        ShowQueueBtn.BeginAnimation(OpacityProperty, hidden
            ? new DoubleAnimation(1, Ui.Time(220)) { BeginTime = Ui.Time(160) }
            : new DoubleAnimation(0, Ui.Time(120)));
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
        else if (e.PropertyName == nameof(PlayerViewModel.IsPlaying))
        {
            Sync(true);
            PushClock();
        }
        else if (e.PropertyName == nameof(PlayerViewModel.Position))
        {
            PushClock();
            if (_vm != null && _source != null && Math.Abs(Video.Position.TotalSeconds - _vm.Player.Position) > 1.5) Sync(true);
        }
        else if (e.PropertyName is nameof(PlayerViewModel.Speed) or nameof(PlayerViewModel.Animate)) PushClock();
    }

    private void Update()
    {
        var path = _vm?.VideoPath;
        bool show = path != null && IsLoaded && _window?.WindowState != WindowState.Minimized;
        if (!show)
        {
            CloseVideo();
            return;
        }
        if (_source != path)
        {
            _source = path;
            _shown = false;
            Video.Source = new Uri(path!);
        }
        Sync(true);
        _sync.Start();
    }

    // A MediaElement opened while paused draws nothing until it has played once (its renderer only starts on Play): so a
    // video opened with the song paused (shown, or just downloaded) plays for a moment and stops on the frame of the audio's
    // position (ScrubbingEnabled draws the frame of a seek while paused).
    private bool _shown;

    private void Sync(bool force)
    {
        if (_vm == null || _source == null) return;
        var player = _vm.Player;
        var target = player.EnginePosition;
        if (player.IsPlaying)
        {
            if (force || Math.Abs((Video.Position - target).TotalSeconds) > 0.25) Video.Position = target;
            Video.Play();
            _shown = true;
            return;
        }
        if (!_shown)
        {
            _shown = true;
            Video.Play();
            Video.Pause();
            force = true;
        }
        else Video.Pause();
        if (force || Math.Abs((Video.Position - target).TotalSeconds) > 0.25) Video.Position = target;
    }

    private void CloseVideo()
    {
        _sync.Stop();
        if (_source == null) return;
        _source = null;
        _shown = false;
        Video.Stop();
        Video.Close();
        Video.Source = null;
    }

    // ------------------------------------------------------------------ 3D cover

    // Targets from the mouse; the angles glide to them every frame.
    private double _mouseX, _mouseY, _nowX, _nowY, _glare, _glareTarget;
    private bool _ticking;
    private TimeSpan _lastFrame;
    // The streaks lean 28° (as in the XAML).
    private static readonly double SheenCos = Math.Cos(28 * Math.PI / 180), SheenSin = Math.Sin(28 * Math.PI / 180);

    private void Cover_MouseMove(object sender, MouseEventArgs e)
    {
        if (_vm?.CoverTilt != true) return;
        var p = e.GetPosition(CoverHit);
        _mouseX = Math.Clamp(p.X / CoverHit.ActualWidth * 2 - 1, -1, 1);
        _mouseY = Math.Clamp(p.Y / CoverHit.ActualHeight * 2 - 1, -1, 1);
        _glareTarget = 1;
        StartTicking();
    }

    // The light sits opposite the mouse: the streaks slide against it (farther than the tilt), glow near it, and its edge lights up.
    private void PlaceSheen(double x, double y)
    {
        double shift = -(x * SheenCos + y * SheenSin) * 230;
        SheenNear.X = shift;
        SheenFar.X = shift * 0.55;
        SheenMask.Center = SheenMask.GradientOrigin = new Point(200 - x * 180, 200 - y * 180);
        double len = Math.Sqrt(x * x + y * y);
        if (len > 0.001)
        {
            double dx = -x / len * 283, dy = -y / len * 283;
            RimBrush.StartPoint = new Point(200 + dx, 200 + dy);
            RimBrush.EndPoint = new Point(200 - dx, 200 - dy);
        }
        RimBrush.Opacity = Math.Min(1, len * 1.6);
    }

    private void Cover_MouseLeave(object sender, MouseEventArgs e)
    {
        _mouseX = _mouseY = _glareTarget = 0;
        StartTicking();
    }

    private void StartTicking()
    {
        if (_ticking) return;
        _ticking = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopTicking()
    {
        if (!_ticking) return;
        _ticking = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return;
        double dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Min(0.1, (now - _lastFrame).TotalSeconds);
        _lastFrame = now;
        double k = 1 - Math.Exp(-dt / 0.075), kg = 1 - Math.Exp(-dt / 0.12);
        // _nowX/_nowY: the mouse as the cover follows it (-1..1).
        _nowX += (_mouseX - _nowX) * k;
        _nowY += (_mouseY - _nowY) * k;
        _glare += (_glareTarget - _glare) * kg;
        TiltY.Angle = _nowX * MaxTilt;
        TiltX.Angle = _nowY * MaxTilt;
        PlaceSheen(_nowX, _nowY);
        GlareLayer.Opacity = _glare;
        // Arrived: stop until the mouse moves again.
        bool settled = Math.Abs(_mouseX - _nowX) < 0.001 && Math.Abs(_mouseY - _nowY) < 0.001 && Math.Abs(_glareTarget - _glare) < 0.005;
        if (!settled) return;
        if (_glareTarget == 0)
        {
            TiltX.Angle = TiltY.Angle = _nowX = _nowY = 0;
            GlareLayer.Opacity = _glare = 0;
        }
        StopTicking();
    }

    // The cover becomes one texture, redrawn only when the song or its cover changes.
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
        else if (e.PropertyName == nameof(TrackViewModel.Cover160)) ShowBackdrop();
    }

    private void BakeCover()
    {
        const double size = 400, radius = 14;
        var rect = new Rect(0, 0, size, size);
        var img = _coverTrack?.Cover300 as BitmapSource;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushClip(new RectangleGeometry(rect, radius, radius));
            if (img != null && img.PixelWidth > 0 && img.PixelHeight > 0)
            {
                double scale = Math.Max(size / img.PixelWidth, size / img.PixelHeight);
                double w = img.PixelWidth * scale, h = img.PixelHeight * scale;
                dc.DrawImage(img, new Rect((size - w) / 2, (size - h) / 2, w, h));
            }
            else
            {
                dc.DrawRectangle((Brush)FindResource("PlaceholderGradient"), null, rect);
                var note = new FormattedText("", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface((FontFamily)FindResource("IconFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 110,
                    (Brush)FindResource("MutedBrush"), 2);
                dc.DrawText(note, new Point((size - note.Width) / 2, (size - note.Height) / 2));
            }
            dc.Pop();
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x22, 255, 255, 255)), 1.5), new Rect(0.75, 0.75, size - 1.5, size - 1.5), radius, radius);
        }
        var bmp = new RenderTargetBitmap(800, 800, 192, 192, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        if (CoverImage.ImageSource != null && Ui.Animations && IsVisible)
        {
            CoverBefore.ImageSource = CoverImage.ImageSource;
            var coming = new DoubleAnimation(0, 1, Ui.Time(Ui.CoverFadeMs)) { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            coming.Completed += (_, _) =>
            {
                if (ReferenceEquals(CoverImage.ImageSource, bmp)) CoverBefore.ImageSource = null;
            };
            CoverImage.BeginAnimation(Brush.OpacityProperty, coming);
        }
        CoverImage.ImageSource = bmp;
        LyricsBackdrop.ImageSource = BakeGlow(img);
        ShowBackdrop();
        Visualizer.FadeColor(Bright(img is { PixelWidth: > 0, PixelHeight: > 0 } ? Mood(img, Themes.IsLight) : Themes.Get("Accent"), Themes.IsLight));
    }

    // Behind the song: the cover. From one song to the next the new one comes up over the old one (the colours of the whole app move meanwhile).
    private void ShowBackdrop()
    {
        var song = (DataContext as NowPlayingViewModel)?.Player.Current;
        var next = song?.T.HasCover == true ? song.Cover160 : null;
        // (still loading: the old one stays until it comes)
        if (song?.T.HasCover == true && next == null) return;
        if (ReferenceEquals(next, BackdropNowBrush.ImageSource)) return;
        BackdropBeforeBrush.ImageSource = BackdropNowBrush.ImageSource;
        BackdropNowBrush.ImageSource = next;
        bool fade = BackdropBeforeBrush.ImageSource != null && Ui.Animations && IsVisible;
        BackdropBefore.BeginAnimation(OpacityProperty, null);
        BackdropNow.BeginAnimation(OpacityProperty, null);
        BackdropBefore.Opacity = 0;
        BackdropNow.Opacity = 1;
        if (!fade) return;
        BackdropBefore.Opacity = 1;
        var time = Ui.Time(Themes.FadeMs);
        BackdropNow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, time) { FillBehavior = FillBehavior.Stop });
        BackdropBefore.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, time) { FillBehavior = FillBehavior.Stop });
        BackdropBefore.Opacity = 0;
    }

    // Behind the lyrics, like Spotify: the cover's strongest colour, with a very blurred copy of the cover over it. In the
    // light look the colour is a pale one and the page's background washes over the picture: dark text reads on it.
    private ImageSource BakeGlow(BitmapSource? img)
    {
        const int size = 120, spill = 40;
        bool hasImage = img != null && img.PixelWidth > 0 && img.PixelHeight > 0, light = Themes.IsLight;
        var fill = hasImage ? new ImageBrush(img) { Stretch = Stretch.UniformToFill } : (Brush)FindResource("PlaceholderGradient");
        var mood = hasImage ? Mood(img!, light) : Tone(Themes.Get("Accent"), light);
        // Larger than the picture, so the blur doesn't pull the edges in.
        var blob = new System.Windows.Shapes.Rectangle { Fill = fill, Margin = new Thickness(-spill), Opacity = 0.5,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 34, KernelType = System.Windows.Media.Effects.KernelType.Gaussian } };
        var host = new Grid { Width = size, Height = size, ClipToBounds = true, Background = new SolidColorBrush(mood) };
        host.Children.Add(blob);
        if (light) host.Children.Add(new Border { Background = new SolidColorBrush(Themes.Get("Bg")), Opacity = 0.45 });
        RenderOptions.SetBitmapScalingMode(host, BitmapScalingMode.HighQuality);
        host.Measure(new Size(size, size));
        host.Arrange(new Rect(0, 0, size, size));
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(host);
        bmp.Freeze();
        return bmp;
    }

    // The cover's colour: its pixels averaged, the vivid ones counting more (a grey sky doesn't win over a red dress).
    private static Color Mood(BitmapSource img, bool light)
    {
        try
        {
            var small = new FormatConvertedBitmap(new TransformedBitmap(img, new ScaleTransform(24.0 / img.PixelWidth, 24.0 / img.PixelHeight)),
                PixelFormats.Bgra32, null, 0);
            int w = small.PixelWidth, h = small.PixelHeight;
            var px = new byte[w * h * 4];
            small.CopyPixels(px, w * 4, 0);
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
            return Tone(Color.FromRgb((byte)(r / sum), (byte)(g / sum), (byte)(b / sum)), light);
        }
        catch { return Tone(Color.FromRgb(0x2A, 0x24, 0x3A), light); }
    }

    // The same colour for the bars of the music: bright on the page.
    private static Color Bright(Color c, bool light)
    {
        var (hue, s, _) = ToHsl(c.R / 255.0, c.G / 255.0, c.B / 255.0);
        return FromHsl(hue, s < 0.08 ? s : Math.Max(s, 0.55), light ? 0.42 : 0.64);
    }

    // Vivid enough to be a colour, dark enough for white text (light enough for dark text in the light look).
    private static Color Tone(Color c, bool light)
    {
        var (hue, s, _) = ToHsl(c.R / 255.0, c.G / 255.0, c.B / 255.0);
        s = s < 0.08 ? s : Math.Clamp(s * 1.25, 0.3, light ? 0.6 : 0.78);
        return FromHsl(hue, s, light ? 0.86 : 0.3);
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

    private void Current_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm?.Player.Current is not { } t) return;
        Menus.Open(Menus.ForTrack(t, null), (UIElement)sender, false);
        e.Handled = true;
    }

    // The artist's name: its page (which one, for a song of several).
    private void Artist_Click(object sender, MouseButtonEventArgs e)
    {
        if (_vm != null && Menus.ArtistsOfCurrent(_vm.Player) is { } menu) Menus.Open(menu, (UIElement)sender, false);
        e.Handled = true;
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (_vm?.Player.Current is { } t) Menus.Open(Menus.ForTrack(t, null), (UIElement)sender, true);
    }

    private void Row_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QueueRow row) return;
        // Several selected (Ctrl or Shift + click): the menu of all of them.
        var many = Templates.SelectionAround(sender).OfType<QueueRow>().ToList();
        Menus.Open(many.Count > 1 ? Menus.ForQueueRows(many) : Menus.ForQueue(row), (UIElement)sender, false);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ reordering the queue

    private Point? _dragStart;

    private void Row_MouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    private void Row_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not Point start) return;
        if (e.OriginalSource is DependencyObject d && Ui.FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(d) != null) return;
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - start.X) < 6 && Math.Abs(p.Y - start.Y) < 6) return;
        _dragStart = null;
        if (sender is not FrameworkElement fe || fe.DataContext is not QueueRow row || _vm == null) return;
        var session = new DragSession(QueueList, row, TrashZone);
        session.TrashHot += SetTrashHot;
        ShowTrash(true);
        var outcome = DragVisuals.Run(fe, new DataObject(typeof(QueueRow), row), DragDropEffects.Move, session);
        ShowTrash(false);
        // The song lands where the line was, even if the mouse was released elsewhere.
        if (outcome == DragOutcome.Trashed) _vm.Player.RemoveUpcoming(row.Index);
        else if (outcome == DragOutcome.Placed && session.Target is QueueRow target)
        {
            int last = _vm.Player.UpNext.LastOrDefault()?.Index ?? row.Index;
            _vm.Player.MoveUpcoming(row.Index, Math.Clamp(DragVisuals.MoveIndex(row.Index, target.Index, session.After), 0, last));
        }
    }

    private void Queue_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(QueueRow)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private static readonly Color TrashIdle = Color.FromArgb(0xF2, 0x2A, 0x12, 0x16), TrashHot = Color.FromArgb(0xF2, 0xD4, 0x32, 0x3A);
    private static readonly Brush TrashIconIdle = Ui.BrushFrom("#FF7076");
    private bool _trashShown;

    // The bin pops out of the queue's edge, pulses while waiting and shrinks away at the end.
    private void ShowTrash(bool show)
    {
        _trashShown = show;
        if (show)
        {
            TrashScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            TrashScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            TrashTilt.BeginAnimation(RotateTransform.AngleProperty, null);
            TrashFill.BeginAnimation(SolidColorBrush.ColorProperty, null);
            TrashFill.Color = TrashIdle;
            TrashIcon.Foreground = TrashIconIdle;
        }
        if (!Ui.Animations)
        {
            TrashZone.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        TrashZone.Visibility = Visibility.Visible;
        var time = Ui.Time(show ? 300 : 170);
        var pop = show
            ? new DoubleAnimation(0.3, 1, time) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 } }
            : new DoubleAnimation(0.25, time) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseIn, Amplitude = 0.4 } };
        if (!show)
            pop.Completed += (_, _) =>
            {
                if (_trashShown) return;
                TrashZone.Visibility = Visibility.Collapsed;
                Ripple(false);
            };
        TrashPop.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        TrashPop.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        if (show) TrashShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(40, 0, time) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        TrashZone.BeginAnimation(OpacityProperty, show
            ? new DoubleAnimation(0, 1, Ui.Time(160))
            : new DoubleAnimation(0, Ui.Time(150)));
        if (show) Ripple(true);
    }

    // A ring spreading out from the bin, over and over.
    private void Ripple(bool on)
    {
        AnimationTimeline? grow = null, fade = null;
        if (on)
        {
            var period = Ui.Time(1200);
            grow = new DoubleAnimation(1, 1.75, period) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            fade = new DoubleAnimation(0.8, 0, period) { RepeatBehavior = RepeatBehavior.Forever };
        }
        RippleScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        RippleScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        TrashRipple.BeginAnimation(OpacityProperty, fade);
    }

    // Over the bin: it grows, turns red and wiggles.
    private void SetTrashHot(bool hot)
    {
        TrashIcon.Foreground = hot ? Brushes.White : TrashIconIdle;
        if (!Ui.Animations)
        {
            TrashFill.Color = hot ? TrashHot : TrashIdle;
            TrashScale.ScaleX = TrashScale.ScaleY = hot ? 1.2 : 1;
            return;
        }
        TrashFill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(hot ? TrashHot : TrashIdle, Ui.Time(140)));
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
        var scale = new DoubleAnimation(hot ? 1.22 : 1, Ui.Time(200)) { EasingFunction = ease };
        TrashScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        TrashScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        if (hot)
        {
            var wiggle = new DoubleAnimationUsingKeyFrames { Duration = Ui.Time(420), RepeatBehavior = RepeatBehavior.Forever };
            foreach (var (t, a) in new[] { (0, 0.0), (80, -14.0), (180, 12.0), (280, -8.0), (360, 4.0), (420, 0.0) })
                wiggle.KeyFrames.Add(new EasingDoubleKeyFrame(a, KeyTime.FromTimeSpan(Ui.Time(t))));
            TrashTilt.BeginAnimation(RotateTransform.AngleProperty, wiggle);
        }
        else TrashTilt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, Ui.Time(120)));
    }

    // The move itself happens when the drag ends (Row_MouseMove).
    private void Queue_Drop(object sender, DragEventArgs e) => e.Handled = e.Data.GetDataPresent(typeof(QueueRow));
}
