using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Audio;

// The player's sound: MasterProvider on the output device (AudioPlatform: the one chosen in Settings or the default one),
// followed when it changes.
public sealed class AudioEngine : IDisposable
{
    public const int LatencyMs = 120;
    private const double PauseFade = 0.06;

    private readonly Dispatcher _ui;
    private readonly MasterProvider _master = new(48000);
    private readonly IDisposable? _watcher;
    private IWavePlayer? _out;
    // The device _out plays on (AudioPlatform.OutputId): a change of some other device leaves the music alone.
    private string? _outDevice;
    private bool _playing;
    // The device still plays for a moment after Pause (the fade): a song swapped meanwhile must fade too.
    private bool _outRunning;
    private int _pauseVersion, _openVersion;
    private double _crossfade;

#if IOS_APP
    private bool _keepAwake;

    // iOS keeps an app running in the background only while it plays: in a room (whose connection must stay) a pause goes
    // on with silence, the song held where it is.
    public bool KeepAwake
    {
        get => _keepAwake;
        set
        {
            _keepAwake = value;
            if (value || !_master.Held) return;
            _master.Held = false;
            if (_playing) return;
            try { _out?.Pause(); } catch { }
            _outRunning = false;
        }
    }

    // After an interruption (a call) iOS has stopped the output: on again, playing or with the silence above.
    public void Wake()
    {
        if (_out == null || !_playing && !_master.Held) return;
        try { _out.Play(); } catch { }
    }

#endif
    public event Action? Ended;
    public event Action? NearEnd;
    public event Action<string>? Failed;
    // The site refused the link a song was heard from (LinkTrackSource), or cut it off before the end: its link.
    public event Action<string>? StreamRefused;

    public AudioEngine(Dispatcher ui)
    {
        _ui = ui;
        _master.Ended += () => _ui.BeginInvoke(() => Ended?.Invoke());
        _master.NearEnd += () => _ui.BeginInvoke(() => NearEnd?.Invoke());
        _watcher = AudioPlatform.WatchDevices(() => _ui.BeginInvoke(SwitchDevice));
    }

    // The output chosen in Settings (an id of AudioPlatform.OutputDevices), null = the system's default: the music
    // moves there at once.
    public string? OutputDevice
    {
        get => AudioPlatform.Device;
        set
        {
            AudioPlatform.Device = value;
            SwitchDevice();
        }
    }

    public bool IsPlaying => _playing;
    public bool HasSource => _master.HasSource;
    public string? SourcePath { get; private set; }
    // Heard from its site while it downloads (SourcePath is the link), until its file takes over (SwapAsync).
    public bool Streaming => SourcePath?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true;

#if IOS_APP
    // The speaker, the headphones or Bluetooth hold some more (iOS says how much): in time with the others in a room.
    internal static int DeviceLatencyMs => AudioPlatform.DeviceLatencyMs();
#else
    internal const int DeviceLatencyMs = 0;
#endif

    public TimeSpan Position
    {
        get
        {
            var p = _master.Position;
            // The output buffer holds LatencyMs of sound, that is more of the song when it plays faster. An output that
            // measures what it holds (the phone's) is asked, playing or not: emptied at a pause, it holds nothing.
            if (_out is IOutputLatency o) p -= TimeSpan.FromMilliseconds(o.LatencyMs * _master.Speed);
            else if (_playing) p -= TimeSpan.FromMilliseconds((LatencyMs + DeviceLatencyMs) * _master.Speed);
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

    // The transition matched to the next song's BPM (MasterProvider.SetBend).
    public void SetBend(double ratio, double seconds) => _master.SetBend(ratio, seconds);

    public void SetEqualizer(bool enabled, IReadOnlyList<double> gains) => _master.SetEqualizer(enabled, gains);

    public void SetTrackGain(double db) => _master.SetTrackGain(db);

    private static Task<ITrackSource> OpenSource(string path, double durationHint) => Task.Run(() => AudioPlatform.Open(path, durationHint));

    // Decoders are created off the UI thread. An open overtaken by another one (or by Close) is thrown away:
    // it would otherwise put its song back in the engine.
    public Task OpenAsync(string path, double durationHint, double trackGainDb) => OpenAsync(OpenSource(path, durationHint), path, trackGainDb, durationHint);

    public Task OpenAsync(SongStream link, double durationHint, double trackGainDb) =>
        OpenAsync(Task.Run<ITrackSource>(() => new LinkTrackSource(link, TimeSpan.FromSeconds(durationHint),
            () => _ui.BeginInvoke(() => StreamRefused?.Invoke(link.Url)))), link.Url, trackGainDb, durationHint);

    // An internet radio: MP3 decoded here, with the song on air (LiveMp3Source); other formats, and servers it can't talk to,
    // through ffmpeg (not on the iPhone: MP3 stations only there). onAir: from another thread.
    public Task OpenLiveAsync(SongStream link, Action<string?> onAir) =>
        OpenAsync(Task.Run<ITrackSource>(() =>
        {
            try { return new LiveMp3Source(link, onAir); }
            catch (Exception) when (!OperatingSystem.IsIOS())
            {
                return new LinkTrackSource(link, TimeSpan.Zero, () => _ui.BeginInvoke(() => StreamRefused?.Invoke(link.Url)));
            }
        }), link.Url, 0, 0);

    private async Task OpenAsync(Task<ITrackSource> opening, string path, double trackGainDb, double durationHint)
    {
        int version = ++_openVersion;
        var source = await opening;
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
            _master.Held = false;
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
            Failed?.Invoke(L.T("Couldn't use the audio device:") + " " + ex.Message);
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
#if IOS_APP
            if (_keepAwake)
            {
                _master.Held = true;
                return;
            }
#endif
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
        _outDevice = AudioPlatform.OutputId();
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

    // Headphones plugged in, Bluetooth, another output chosen in Settings: move there, only when it's another device (a
    // device that comes or goes elsewhere changes nothing; with no output open, the next Play opens the right one).
    private void SwitchDevice()
    {
        if (_out == null || AudioPlatform.OutputId() == _outDevice) return;
        bool was = _playing;
        // An output that holds a lot (the phone's, away from the screen): what it held is played again on the new one.
        TimeSpan? heard = was && _out is IOutputLatency ? Position : null;
        _playing = false;
        ResetOutput();
        if (heard is { } h) _master.Rewind(h);
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
