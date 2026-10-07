using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class MiniPlayer : UserControl
{
    private Point? _start;
    private long _startAt;
    private readonly TranslateTransform _shift = new();

    public MiniPlayer()
    {
        InitializeComponent();
        Row.RenderTransform = _shift;
        Card.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (Ui.FindAncestor<Button>(e.Source as Visual) != null) return;
            _start = e.GetPosition(this);
            _startAt = Environment.TickCount64;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Card.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (_start is not Point s) return;
            var p = e.GetPosition(this);
            double dx = p.X - s.X;
            if (Math.Abs(dx) > 10 && Math.Abs(dx) > Math.Abs(p.Y - s.Y)) _shift.X = dx * 0.6;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Card.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (_start is not Point s) return;
            _start = null;
            var p = e.GetPosition(this);
            double dx = p.X - s.X, dy = p.Y - s.Y;
            long ms = Math.Max(1, Environment.TickCount64 - _startAt);
            var player = DataContext as PlayerViewModel;
            if (Math.Abs(dx) > 70 && Math.Abs(dx) > Math.Abs(dy) * 1.4)
            {
                // Sideways: the next or the previous song, the row slides away and back.
                if (dx < 0) player?.NextCommand.Execute(null);
                else player?.PreviousCommand.Execute(null);
                Haptics.Tick();
                Settle(dx < 0 ? -1 : 1);
                e.Handled = true;
                return;
            }
            Settle(0);
            // A long press opened the song's menu: that's all it does.
            if (Touch.OpenedMenu(Card)) return;
            if (dy < -40 && -dy / ms > 0.25 || Math.Abs(dx) < 10 && Math.Abs(dy) < 10)
            {
                if (Ui.FindAncestor<Button>(e.Source as Visual) == null) OpenRequested?.Invoke();
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    public event Action? OpenRequested;

    private void Settle(int direction)
    {
        double from = _shift.X;
        if (!Ui.Animations || direction == 0)
        {
            Ui.Tween(this, 160, Ui.CubicOut, t => _shift.X = from * (1 - t));
            return;
        }
        double away = direction * Bounds.Width;
        Ui.Tween(this, 140, t => t, t =>
        {
            _shift.X = from + (away - from) * t;
            if (t < 1) return;
            _shift.X = -away * 0.4;
            Row.Opacity = 0;
            Ui.Tween(this, 220, Ui.CubicOut, k =>
            {
                _shift.X = -away * 0.4 * (1 - k);
                Row.Opacity = k;
            });
        });
    }
}

// A thin line of how far the song is: it runs on by itself between the player's updates (like the waveform).
public sealed class ProgressLine : Control
{
    public static readonly StyledProperty<double> ProgressProperty = AvaloniaProperty.Register<ProgressLine, double>(nameof(Progress));
    public static readonly StyledProperty<bool> IsPlayingProperty = AvaloniaProperty.Register<ProgressLine, bool>(nameof(IsPlaying));
    public static readonly StyledProperty<double> DurationProperty = AvaloniaProperty.Register<ProgressLine, double>(nameof(Duration));
    public static readonly StyledProperty<double> RateProperty = AvaloniaProperty.Register<ProgressLine, double>(nameof(Rate), 1.0);
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<ProgressLine, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<ProgressLine, IBrush?>(nameof(Track));

    public double Progress { get => GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public bool IsPlaying { get => GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public double Duration { get => GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double Rate { get => GetValue(RateProperty); set => SetValue(RateProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    private double _base, _shown;
    private long _baseAt;
    private bool _ticking, _requested;

    static ProgressLine()
    {
        AffectsRender<ProgressLine>(FillProperty, TrackProperty);
        ProgressProperty.Changed.AddClassHandler<ProgressLine>((c, _) => c.OnProgress());
        IsPlayingProperty.Changed.AddClassHandler<ProgressLine>((c, _) => c.Clock());
        IsVisibleProperty.Changed.AddClassHandler<ProgressLine>((c, _) => c.Clock());
    }

    public ProgressLine()
    {
        AttachedToVisualTree += (_, _) => Clock();
        DetachedFromVisualTree += (_, _) => Clock();
    }

    private void OnProgress()
    {
        _base = Progress;
        _baseAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_ticking || Math.Abs(_base - _shown) * Duration > 0.8) _shown = _base;
        InvalidateVisual();
    }

    private void Clock()
    {
        bool want = IsPlaying && Duration > 0 && IsEffectivelyVisible && VisualRoot != null;
        if (want == _ticking) return;
        _ticking = want;
        if (want) Request();
        else
        {
            _shown = Progress;
            InvalidateVisual();
        }
    }

    // A few frames a second are enough for a line this thin (and save battery).
    private void Request()
    {
        if (_requested || TopLevel.GetTopLevel(this) is not { } top) return;
        _requested = true;
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            _requested = false;
            if (!_ticking) return;
            // The app in the background: nobody sees it, it waits to be shown again.
            if (Ui.Hidden)
            {
                _ticking = false;
                Ui.Later(Clock);
                return;
            }
            double rate = Rate > 0 ? Rate : 1;
            double f = System.Diagnostics.Stopwatch.Frequency;
            _shown = Math.Clamp(_base + (System.Diagnostics.Stopwatch.GetTimestamp() - _baseAt) / f * rate / Math.Max(1, Duration), 0, 1);
            InvalidateVisual();
            Request();
        }, TimeSpan.FromMilliseconds(Math.Clamp(Duration * 1000 / Math.Max(1, Bounds.Width) / 2, 33, 500)));
    }

    public override void Render(DrawingContext dc)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        var r = h / 2;
        if (Track != null) dc.DrawRectangle(Track, null, new RoundedRect(new Rect(0, 0, w, h), r));
        double p = Math.Clamp(_shown, 0, 1) * w;
        if (p > 0 && Fill != null) dc.DrawRectangle(Fill, null, new RoundedRect(new Rect(0, 0, Math.Max(h, p), h), r));
    }
}
