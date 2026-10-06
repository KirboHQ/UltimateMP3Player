using Avalonia;
using Avalonia.Controls;

namespace UltimateMP3Player.Views;

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
