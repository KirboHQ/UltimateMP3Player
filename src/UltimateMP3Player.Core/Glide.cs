using System.Diagnostics;

namespace UltimateMP3Player.Core;

// A value that glides to a target in a set time and can be sent somewhere else halfway without a jolt: the new glide starts
// where the value is, at the speed it has, and arrives at rest (a cubic: from rest it eases in and out). The pages and the
// selections that move with them (a click while they move doesn't cut the motion and start it again).
public sealed class Glide
{
    private double _from, _to, _speed, _start, _ms;

    public Glide(double value = 0) => _from = _to = value;

    // The clock the glides follow (the UI thread's): real time, but a gap longer than a slow frame (the app busy building the
    // page coming in) counts as one frame, so a motion that had to wait goes on from where it was instead of jumping ahead.
    private static double _real = double.NaN, _clock;
    private static double Now
    {
        get
        {
            double real = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
            if (!double.IsNaN(_real)) _clock += Math.Min(real - _real, 20);
            _real = real;
            return _clock;
        }
    }

    public double Target => _to;
    public bool Moving => _ms > 0 && Now < _start + _ms;
    public double Value => At(Now).Value;

    public void Set(double value)
    {
        _from = _to = value;
        _speed = _ms = 0;
    }

    public void To(double target, double ms)
    {
        double now = Now;
        var (value, speed) = At(now);
        if (target == _to && (now < _start + _ms || value == target)) return;
        if (ms <= 0)
        {
            Set(target);
            return;
        }
        (_from, _to, _start, _ms) = (value, target, now, ms);
        // (already going that way faster than the curve can stop in time: held to what it can, so it doesn't fly past)
        double d = target - value;
        _speed = d != 0 && speed * ms / d > 3 ? 3 * d / ms : speed;
    }

    // Where it is and how fast it goes (per ms) at a moment.
    private (double Value, double Speed) At(double now)
    {
        if (_ms <= 0 || now >= _start + _ms) return (_to, 0);
        double s = Math.Max(0, (now - _start) / _ms), m = _speed * _ms, d = _to - _from;
        double value = _from + m * (s * s * s - 2 * s * s + s) + d * (3 * s * s - 2 * s * s * s);
        double slope = m * (3 * s * s - 4 * s + 1) + d * (6 * s - 6 * s * s);
        return (value, slope / _ms);
    }
}
