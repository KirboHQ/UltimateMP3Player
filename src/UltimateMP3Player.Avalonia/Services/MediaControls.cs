using Avalonia.Threading;

namespace UltimateMP3Player.Services;

// Media keys and the system's "now playing" controls (the Windows app's MediaControls): on Linux the MPRIS player that
// GNOME, KDE and the others show in the notification area and the lock screen, on macOS the Now Playing of Control Center.
public sealed class MediaControls : IDisposable
{
    private readonly Mpris? _mpris;
    private readonly MacNowPlaying? _mac;
    private readonly object _lock = new();

    public event Action? PlayPressed;
    public event Action? PausePressed;
    public event Action? PlayPausePressed;
    public event Action? NextPressed;
    public event Action? PreviousPressed;
    public event Action<TimeSpan>? SeekRequested;
    public event Action<double>? VolumeRequested;
    public event Action? RaiseRequested;
    public event Action? QuitRequested;

    public MediaControls()
    {
        try
        {
            if (OperatingSystem.IsLinux()) _mpris = new Mpris(this);
            else if (OperatingSystem.IsMacOS()) _mac = MacNowPlaying.Create(this);
        }
        catch { }
    }

    // ------------------------------------------------------------------ what is playing (read from the D-Bus thread too)

    internal sealed record Track(int Number, string Title, string Artist, string Album, string? Cover, TimeSpan Duration);

    private Track? _track;
    private int _number;
    private bool _playing;
    private TimeSpan _position;
    private DateTime _positionAt = DateTime.UtcNow;
    private double _speed = 1, _volume = 1;

    internal Track? Current { get { lock (_lock) return _track; } }
    internal bool Playing { get { lock (_lock) return _playing; } }
    internal double Speed { get { lock (_lock) return _speed; } }
    internal double Volume { get { lock (_lock) return _volume; } }

    // The position goes on by itself while playing, like the app's.
    internal TimeSpan Position
    {
        get
        {
            lock (_lock) return Extrapolate();
        }
    }

    private TimeSpan Extrapolate()
    {
        var p = _position;
        if (_playing) p += (DateTime.UtcNow - _positionAt) * _speed;
        if (_track != null && _track.Duration > TimeSpan.Zero && p > _track.Duration) p = _track.Duration;
        return p < TimeSpan.Zero ? TimeSpan.Zero : p;
    }

    public void SetTrack(string? title, string? artist, string? album, string? coverPath, double duration)
    {
        Track? t;
        lock (_lock)
        {
            t = title == null ? null : new Track(++_number, title, artist ?? "", album ?? "",
                coverPath != null && File.Exists(coverPath) ? coverPath : null, TimeSpan.FromSeconds(Math.Max(0, duration)));
            if (t != null && _track != null && t with { Number = _track.Number } == _track) return;
            _track = t;
            _position = TimeSpan.Zero;
            _positionAt = DateTime.UtcNow;
        }
        _mpris?.OnTrack();
        _mac?.OnChanged();
    }

    public void SetState(bool playing)
    {
        lock (_lock)
        {
            if (_playing == playing) return;
            _position = Extrapolate();
            _positionAt = DateTime.UtcNow;
            _playing = playing;
        }
        _mpris?.OnState();
        _mac?.OnChanged();
    }

    public void SetTimeline(TimeSpan position, TimeSpan duration, double speed)
    {
        bool jumped, rate;
        lock (_lock)
        {
            jumped = (Extrapolate() - position).Duration() > TimeSpan.FromSeconds(1.5);
            rate = Math.Abs(_speed - speed) > 0.001;
            _position = position;
            _positionAt = DateTime.UtcNow;
            _speed = speed > 0 ? speed : 1;
            if (_track != null && duration > TimeSpan.Zero && _track.Duration != duration) _track = _track with { Duration = duration };
        }
        if (rate) _mpris?.OnRate();
        if (jumped) _mpris?.OnSeeked(position);
        _mac?.OnChanged();
    }

    public void SetVolume(double volume)
    {
        lock (_lock)
        {
            if (Math.Abs(_volume - volume) < 0.001) return;
            _volume = volume;
        }
        _mpris?.OnVolume();
    }

    // ------------------------------------------------------------------ requests from the system (any thread)

    internal void Post(Action? a)
    {
        if (a != null) Dispatcher.UIThread.Post(a);
    }

    internal void Play() => Post(PlayPressed);
    internal void Pause() => Post(PausePressed);
    internal void PlayPause() => Post(PlayPausePressed);
    internal void Next() => Post(NextPressed);
    internal void Previous() => Post(PreviousPressed);
    internal void Raise() => Post(RaiseRequested);
    internal void Quit() => Post(QuitRequested);

    internal void Seek(TimeSpan to)
    {
        var d = Current?.Duration ?? TimeSpan.Zero;
        if (to < TimeSpan.Zero) to = TimeSpan.Zero;
        if (d > TimeSpan.Zero && to > d) to = d;
        Dispatcher.UIThread.Post(() => SeekRequested?.Invoke(to));
    }

    internal void RequestVolume(double v) => Dispatcher.UIThread.Post(() => VolumeRequested?.Invoke(Math.Clamp(v, 0, 1)));

    public void Dispose()
    {
        _mpris?.Dispose();
        _mac?.Dispose();
    }
}
