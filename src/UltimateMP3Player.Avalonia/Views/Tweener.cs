using Avalonia;
using Avalonia.Controls;

namespace UltimateMP3Player.Views;

// The frames of something small that keeps moving (a scrolling title, a spinner, a waveform's progress): at most one every
// MinMs, in step with the screen. The screen's own rate (120 Hz on many phones) is for the finger and the pages; these look
// the same at a fraction of it, and every frame redraws the picture (the battery, the phone's warmth).
public sealed class FramePacer
{
    private TimeSpan _last = TimeSpan.MinValue;
    private TopLevel? _waitingOn;
    private int _ask;

    public FramePacer(double minMs) => MinMs = minMs;

    public double MinMs { get; set; }

    // apply(time) in the first frame of the screen at least MinMs after the last one; asks already pending count once
    // (unless they were made to a screen that's gone: its frames never come).
    public void Request(TopLevel top, Action<TimeSpan> apply)
    {
        if (_waitingOn == top) return;
        _waitingOn = top;
        int ask = ++_ask;
        top.RequestAnimationFrame(now => OnFrame(top, now, apply, ask));
    }

    private void OnFrame(TopLevel top, TimeSpan now, Action<TimeSpan> apply, int ask)
    {
        if (ask != _ask) return;
        double wait = _last == TimeSpan.MinValue || now < _last ? 0 : MinMs - (now - _last).TotalMilliseconds;
        if (wait > 1.5)
        {
            // Too soon: back in a while (a timer for the long part, so the frames in between don't wake the app).
            if (wait > 8)
                Avalonia.Threading.DispatcherTimer.RunOnce(() => top.RequestAnimationFrame(t => OnFrame(top, t, apply, ask)), TimeSpan.FromMilliseconds(wait - 5));
            else top.RequestAnimationFrame(t => OnFrame(top, t, apply, ask));
            return;
        }
        _waitingOn = null;
        _last = now;
        apply(now);
    }
}

// One animated value (what WPF's BeginAnimation did on a property): To() glides it from where it is to a target on the
// render clock, a new To() or Set() replaces the running one, Completed runs only if it got there.
public sealed class Tweener
{
    private readonly Visual _owner;
    private readonly Action<double> _apply;
    private double _from, _to, _ms;
    private Func<double, double> _ease = Ui.Linear;
    private TimeSpan? _start;
    private double _delay;
    private Action? _completed;
    private int _run;
    private bool _requested;

    public Tweener(Visual owner, Action<double> apply, double initial = 0)
    {
        _owner = owner;
        _apply = apply;
        Value = initial;
    }

    public double Value { get; private set; }
    public bool IsRunning { get; private set; }

    public void Set(double value)
    {
        Stop();
        Value = value;
        _apply(value);
    }

    public void Stop()
    {
        _run++;
        IsRunning = false;
        _completed = null;
    }

    public void To(double target, double ms, Func<double, double>? ease = null, double? from = null, Action? completed = null, double delay = 0)
    {
        if (from is double f)
        {
            Value = f;
            _apply(f);
        }
        if (!Ui.Animations || ms <= 0 || TopLevel.GetTopLevel(_owner) == null)
        {
            Set(target);
            completed?.Invoke();
            return;
        }
        _run++;
        _from = Value;
        _to = target;
        _ms = ms;
        _ease = ease ?? Ui.Linear;
        _delay = delay;
        _completed = completed;
        _start = null;
        IsRunning = true;
        Request(_run);
    }

    private void Request(int run)
    {
        if (TopLevel.GetTopLevel(_owner) is not { } top)
        {
            // Gone from the screen: it lands at once.
            if (run == _run && IsRunning) Finish();
            return;
        }
        if (_requested) return;
        _requested = true;
        top.RequestAnimationFrame(now =>
        {
            _requested = false;
            if (!IsRunning) return;
            _start ??= now;
            double elapsed = (now - _start.Value).TotalMilliseconds - _delay;
            if (elapsed < 0)
            {
                Request(_run);
                return;
            }
            double t = Math.Clamp(elapsed / _ms, 0, 1);
            Value = _from + (_to - _from) * _ease(t);
            _apply(Value);
            if (t >= 1) Finish();
            else Request(_run);
        });
    }

    private void Finish()
    {
        IsRunning = false;
        Value = _to;
        _apply(_to);
        var done = _completed;
        _completed = null;
        done?.Invoke();
    }
}
