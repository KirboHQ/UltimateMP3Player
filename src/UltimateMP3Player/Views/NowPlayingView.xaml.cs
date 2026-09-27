using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    }

    // ------------------------------------------------------------------ video frame

    private void Stage_SizeChanged(object sender, SizeChangedEventArgs e)
        => StageVideo.Clip = new RectangleGeometry(new Rect(e.NewSize), 16, 16);

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
        Attach(DataContext as NowPlayingViewModel);
        WatchCover();
        Update();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window != null) _window.StateChanged -= OnWindowState;
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
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.Current))
        {
            _vm?.Refresh();
            WatchCover();
            Update();
        }
        else if (e.PropertyName == nameof(PlayerViewModel.IsPlaying)) Sync(true);
        else if (e.PropertyName == nameof(PlayerViewModel.Position) && _vm != null && Math.Abs(Video.Position.TotalSeconds - _vm.Player.Position) > 1.5)
            Sync(true);
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
            Video.Source = new Uri(path!);
        }
        Sync(true);
        _sync.Start();
    }

    private void Sync(bool force)
    {
        if (_vm == null || _source == null) return;
        var player = _vm.Player;
        var target = player.EnginePosition;
        if (force || Math.Abs((Video.Position - target).TotalSeconds) > 0.25) Video.Position = target;
        if (player.IsPlaying) Video.Play();
        else Video.Pause();
    }

    private void CloseVideo()
    {
        _sync.Stop();
        if (_source == null) return;
        _source = null;
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
        CoverImage.ImageSource = bmp;
    }

    // ------------------------------------------------------------------ menus

    private void Current_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm?.Player.Current is not { } t) return;
        Menus.Open(Menus.ForTrack(t, null), (UIElement)sender, false);
        e.Handled = true;
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (_vm?.Player.Current is { } t) Menus.Open(Menus.ForTrack(t, null), (UIElement)sender, true);
    }

    private void Row_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QueueRow row) return;
        Menus.Open(Menus.ForQueue(row), (UIElement)sender, false);
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
        var time = TimeSpan.FromMilliseconds(show ? 300 : 170);
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
            ? new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
            : new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
        if (show) Ripple(true);
    }

    // A ring spreading out from the bin, over and over.
    private void Ripple(bool on)
    {
        AnimationTimeline? grow = null, fade = null;
        if (on)
        {
            var period = TimeSpan.FromMilliseconds(1200);
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
        TrashFill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(hot ? TrashHot : TrashIdle, TimeSpan.FromMilliseconds(140)));
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
        var scale = new DoubleAnimation(hot ? 1.22 : 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease };
        TrashScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        TrashScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        if (hot)
        {
            var wiggle = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420), RepeatBehavior = RepeatBehavior.Forever };
            foreach (var (t, a) in new[] { (0, 0.0), (80, -14.0), (180, 12.0), (280, -8.0), (360, 4.0), (420, 0.0) })
                wiggle.KeyFrames.Add(new EasingDoubleKeyFrame(a, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t))));
            TrashTilt.BeginAnimation(RotateTransform.AngleProperty, wiggle);
        }
        else TrashTilt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
    }

    // The move itself happens when the drag ends (Row_MouseMove).
    private void Queue_Drop(object sender, DragEventArgs e) => e.Handled = e.Data.GetDataPresent(typeof(QueueRow));
}
