using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Audio;

// The player's sound: MasterProvider on the default output device (AudioPlatform), followed when it changes.
public sealed class AudioEngine : IDisposable
{
    public const int LatencyMs = 120;
    private const double PauseFade = 0.06;

    private readonly Dispatcher _ui;
    private readonly MasterProvider _master = new(48000);
    private readonly IDisposable? _watcher;
    private IWavePlayer? _out;
    private bool _playing;
    // The device still plays for a moment after Pause (the fade): a song swapped meanwhile must fade too.
    private bool _outRunning;
    private int _pauseVersion, _openVersion;
    private double _crossfade;

    public event Action? Ended;
    public event Action? NearEnd;
    public event Action<string>? Failed;

    public AudioEngine(Dispatcher ui)
    {
        _ui = ui;
        _master.Ended += () => _ui.BeginInvoke(() => Ended?.Invoke());
        _master.NearEnd += () => _ui.BeginInvoke(() => NearEnd?.Invoke());
        _watcher = AudioPlatform.WatchDefaultDevice(() => _ui.BeginInvoke(SwitchDevice));
    }

    public bool IsPlaying => _playing;
    public bool HasSource => _master.HasSource;
    public string? SourcePath { get; private set; }

    public TimeSpan Position
    {
        get
        {
            var p = _master.Position;
            // The output buffer holds LatencyMs of sound, that is more of the song when it plays faster. An output that
            // measures what it holds (the phone's) is asked, playing or not: emptied at a pause, it holds nothing.
            if (_out is IOutputLatency o) p -= TimeSpan.FromMilliseconds(o.LatencyMs * _master.Speed);
            else if (_playing) p -= TimeSpan.FromMilliseconds(LatencyMs * _master.Speed);
            return p < TimeSpan.Zero ? TimeSpan.Zero : p;
        }
    }

    // A jump or another song asked while the output holds a lot (the phone with the screen off): what's waiting is
    // thrown away, so it's heard at once and not after all of it. Not at the end of a song (the next one, repeat): the
    // end of this one is still waiting there to be heard.
    private void Cut(bool ended)
    {
        if (!ended && _playing && _out is IOutputLatency { LatencyMs: > 200 } o) o.Drop();
    }

    public TimeSpan Duration => _master.Duration;

    public float Volume { set => _master.Volume = value; }

    // Playback speed 0.5-2; pitch = the key follows the speed (like a record), otherwise it stays.
    public double Speed => _master.Speed;

    public void SetSpeed(double speed, bool pitch) => _master.SetSpeed(speed, pitch);

    private bool Smooth => _playing || _outRunning;

    // Crossfade seconds, 0 = off.
    public double CrossfadeSeconds
    {
        get => _crossfade;
        set
        {
            _crossfade = value;
            _master.SetCrossfade(value);
        }
    }

    public double RemainingSeconds => _master.RemainingSeconds;

    public void SetEqualizer(bool enabled, IReadOnlyList<double> gains) => _master.SetEqualizer(enabled, gains);

    public void SetTrackGain(double db) => _master.SetTrackGain(db);

    private static Task<ITrackSource> OpenSource(string path, double durationHint) => Task.Run(() => AudioPlatform.Open(path, durationHint));

    // Decoders are created off the UI thread. An open overtaken by another one (or by Close) is thrown away:
    // it would otherwise put its song back in the engine.
    public async Task OpenAsync(string path, double durationHint, double trackGainDb)
    {
        int version = ++_openVersion;
        var source = await OpenSource(path, durationHint);
        if (version != _openVersion)
        {
            source.Dispose();
            return;
        }
        bool ended = _master.ReadToEnd;
        _master.SetSource(source, trackGainDb, durationHint, _crossfade, Smooth);
        SourcePath = path;
        Cut(ended);
    }

    public async Task CrossfadeToAsync(string path, double durationHint, double trackGainDb)
    {
        int version = ++_openVersion;
        var source = await OpenSource(path, durationHint);
        if (version != _openVersion)
        {
            source.Dispose();
            return;
        }
        double seconds = Math.Clamp(Math.Min(_crossfade, _master.RemainingSeconds), 0.3, 12);
        _master.Crossfade(source, trackGainDb, durationHint, _crossfade, seconds);
        SourcePath = path;
    }

    // The song playing goes on from another copy of its file, at the same point.
    public async Task SwapAsync(string path, double durationHint)
    {
        int version = ++_openVersion;
        var source = await OpenSource(path, durationHint);
        if (version != _openVersion || !_master.Swap(source))
        {
            source.Dispose();
            return;
        }
        SourcePath = path;
    }

    public void Close()
    {
        _openVersion++;
        _master.SetSource(null, 0, 0, 0, Smooth);
        SourcePath = null;
    }

    public void Seek(TimeSpan t)
    {
        bool ended = _master.ReadToEnd;
        _master.Seek(t);
        Cut(ended);
    }

    public void Play()
    {
        _pauseVersion++;
        try
        {
            bool wasStopped = !_playing;
            EnsureOutput();
            if (wasStopped && !_outRunning) _master.FadeLevel(0, 0);
            _out!.Play();
            _playing = true;
            _outRunning = true;
            _master.FadeLevel(1, PauseFade);
        }
        catch (Exception ex)
        {
            ResetOutput();
            _playing = false;
            Failed?.Invoke(L.T("Impossibile usare il dispositivo audio:") + " " + ex.Message);
        }
    }

    // Fade out first: a hard stop clicks.
    public void Pause()
    {
        if (!_playing) return;
        _playing = false;
        _master.FadeLevel(0, PauseFade);
        int version = ++_pauseVersion;
        _ = Task.Delay(TimeSpan.FromSeconds(PauseFade + 0.05)).ContinueWith(_ => _ui.BeginInvoke(() =>
        {
            if (version != _pauseVersion || _playing) return;
            // An output that can throw away what it still holds (the fade and the silence after it, and a little of the
            // song before them, not heard yet): emptied, and the song goes back to the point heard last, so playing
            // again goes on from there, without the dip of the fade in the middle.
            if (_out is IOutputLatency o)
            {
                var heard = Position;
                try { _out.Pause(); } catch { }
                if (o.Flush() > 0) _master.Rewind(heard);
            }
            else
            {
                try { _out?.Pause(); } catch { }
            }
            _outRunning = false;
        }));
    }

    private void EnsureOutput()
    {
        if (_out != null) return;
        _master.SetRate(AudioPlatform.OutputRate());
        var o = AudioPlatform.CreateOutput(LatencyMs);
        o.Init(new SampleToWaveProvider(_master));
        o.PlaybackStopped += (_, e) =>
        {
            if (e.Exception == null) return;
            _ui.BeginInvoke(() =>
            {
                bool was = _playing;
                _playing = false;
                ResetOutput();
                if (was) Play();
            });
        };
        _out = o;
    }

    private void ResetOutput()
    {
        var o = _out;
        _out = null;
        _outRunning = false;
        if (o == null) return;
        try { o.Stop(); } catch { }
        try { o.Dispose(); } catch { }
    }

    // Headphones plugged in, Bluetooth: move there.
    private void SwitchDevice()
    {
        bool was = _playing;
        _playing = false;
        ResetOutput();
        if (was) Play();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        ResetOutput();
        _master.SetSource(null, 0, 0, 0, false);
    }
}

// An output that knows how much sound it holds, not heard yet (the phone's AudioTrack, whose buffer grows while the app is
// away), and can throw it away.
public interface IOutputLatency
{
    int LatencyMs { get; }

    // Playing: what's waiting goes, the sound mixed from now on comes at once.
    void Drop();

    // Paused: what's waiting goes; how many milliseconds it was.
    int Flush();
}
