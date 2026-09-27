using System.IO;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Audio;

// One song in the mix: decoder, resampler, gain, fades.
internal sealed class Voice : IDisposable
{
    public Voice(ITrackSource source, float gain, double duration)
    {
        Source = source;
        Gain = gain;
        Duration = duration > 0 ? duration : source.Duration.TotalSeconds;
    }

    public ITrackSource Source { get; }
    public ISampleProvider? Chain { get; private set; }
    public float Gain;
    public double Duration;
    // 0..1, heard through an equal-power curve.
    public float Fade = 1f;
    public float FadeTarget = 1f;
    public float FadeStep = 1f;
    public TimeSpan? PendingSeek;
    public bool Ended;
    public double CrossfadeAt = double.MaxValue;
    public bool NearEndRaised;

    public void Rebuild(int rate)
    {
        var s = Source.Samples;
        Chain = s.WaveFormat.SampleRate == rate ? s : new WdlResamplingSampleProvider(s, rate);
    }

    public void FadeTo(float target, double seconds, int rate)
    {
        FadeTarget = target;
        if (seconds <= 0) { Fade = target; FadeStep = 1f; }
        else FadeStep = (float)(1.0 / (seconds * rate));
    }

    public void Dispose() => Source.Dispose();
}

// Current song plus the fading one, then EQ, volume, limiter.
public sealed class MasterProvider : ISampleProvider
{
    private const double MicroFade = 0.03;
    private const double SeekFade = 0.015;

    private readonly object _lock = new();
    private Voice? _current;
    private Voice? _outgoing;
    private float[] _mix = Array.Empty<float>();
    private int _rate;

    private readonly BiQuadFilter?[,] _eq = new BiQuadFilter?[2, Equalizer.BandCount];
    private readonly double[] _eqGains = new double[Equalizer.BandCount];
    private bool _eqEnabled;
    private float _eqPreamp = 1f;

    private float _volume = 1f;
    private float _gain = 1f;
    private float _level = 1f, _levelTarget = 1f, _levelStep = 1f;

    public event Action? Ended;
    public event Action? NearEnd;

    public MasterProvider(int rate)
    {
        _rate = rate;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
    }

    public WaveFormat WaveFormat { get; private set; }

    public void SetRate(int rate)
    {
        lock (_lock)
        {
            if (rate == _rate) return;
            _rate = rate;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 2);
            _current?.Rebuild(rate);
            _outgoing?.Rebuild(rate);
            RebuildEq();
        }
    }

    public bool HasSource
    {
        get { lock (_lock) return _current != null; }
    }

    public TimeSpan Position
    {
        get { lock (_lock) return _current == null ? TimeSpan.Zero : _current.PendingSeek ?? _current.Source.Position; }
    }

    public TimeSpan Duration
    {
        get { lock (_lock) return _current == null ? TimeSpan.Zero : TimeSpan.FromSeconds(_current.Duration); }
    }

    private static float Db(double db) => (float)Math.Pow(10, db / 20);

    // Smooth: the old song fades out in milliseconds.
    public void SetSource(ITrackSource? source, double gainDb, double duration, double crossfadeSeconds, bool smooth)
    {
        var drop = new List<Voice>();
        lock (_lock)
        {
            Voice? v = null;
            if (source != null)
            {
                v = NewVoice(source, gainDb, duration, crossfadeSeconds);
                if (smooth) { v.Fade = 0; v.FadeTo(1, MicroFade, _rate); }
            }
            if (_current != null)
            {
                if (smooth)
                {
                    if (_outgoing != null) drop.Add(_outgoing);
                    _outgoing = _current;
                    _outgoing.FadeTo(0, MicroFade, _rate);
                }
                else drop.Add(_current);
            }
            if (!smooth && _outgoing != null)
            {
                drop.Add(_outgoing);
                _outgoing = null;
            }
            _current = v;
        }
        foreach (var d in drop) d.Dispose();
    }

    // Next song fades in while this one fades out.
    public void Crossfade(ITrackSource source, double gainDb, double duration, double crossfadeSeconds, double seconds)
    {
        Voice? drop;
        lock (_lock)
        {
            var v = NewVoice(source, gainDb, duration, crossfadeSeconds);
            v.Fade = 0;
            v.FadeTo(1, seconds, _rate);
            drop = _outgoing;
            _outgoing = _current;
            _outgoing?.FadeTo(0, seconds, _rate);
            _current = v;
        }
        drop?.Dispose();
    }

    private Voice NewVoice(ITrackSource source, double gainDb, double duration, double crossfadeSeconds)
    {
        var v = new Voice(source, Db(gainDb), duration);
        v.Rebuild(_rate);
        SetCrossfadePoint(v, crossfadeSeconds);
        return v;
    }

    private static void SetCrossfadePoint(Voice v, double seconds)
    {
        v.CrossfadeAt = seconds > 0 && v.Duration > 3 ? v.Duration - Math.Min(seconds, v.Duration / 3) : double.MaxValue;
        v.NearEndRaised = false;
    }

    public void SetCrossfade(double seconds)
    {
        lock (_lock)
            if (_current != null) SetCrossfadePoint(_current, seconds);
    }

    public double RemainingSeconds
    {
        get { lock (_lock) return _current == null ? 0 : Math.Max(0, _current.Duration - _current.Source.Position.TotalSeconds); }
    }

    public void Seek(TimeSpan t)
    {
        lock (_lock)
        {
            if (_current == null) return;
            _current.PendingSeek = t;
            _current.Ended = false;
            _current.NearEndRaised = _current.CrossfadeAt <= t.TotalSeconds;
        }
    }

    public float Volume
    {
        set { lock (_lock) _volume = value; }
    }

    public void SetTrackGain(double db)
    {
        lock (_lock)
            if (_current != null) _current.Gain = Db(db);
    }

    // Master level faded around pause and resume.
    public void FadeLevel(float target, double seconds)
    {
        lock (_lock)
        {
            _levelTarget = target;
            if (seconds <= 0) { _level = target; _levelStep = 1f; }
            else _levelStep = (float)(1.0 / (seconds * _rate));
        }
    }

    public void SetEqualizer(bool enabled, IReadOnlyList<double> gains)
    {
        lock (_lock)
        {
            _eqEnabled = enabled;
            for (int i = 0; i < Equalizer.BandCount; i++) _eqGains[i] = i < gains.Count ? gains[i] : 0;
            _eqPreamp = Db(Equalizer.PreampDb(_eqGains));
            RebuildEq();
        }
    }

    // Shelves at the ends, peaking bands in between (see Equalizer).
    private void RebuildEq()
    {
        for (int b = 0; b < Equalizer.BandCount; b++)
        {
            bool active = _eqEnabled && Math.Abs(_eqGains[b]) >= 0.05;
            for (int ch = 0; ch < 2; ch++)
            {
                if (!active) { _eq[ch, b] = null; continue; }
                float f = (float)Equalizer.FilterFrequency(b), q = (float)Equalizer.Q, g = (float)_eqGains[b];
                if (f >= _rate / 2.2f) { _eq[ch, b] = null; continue; }
                switch (Equalizer.Kind(b))
                {
                    case EqBandKind.LowShelf:
                        _eq[ch, b] = BiQuadFilter.LowShelf(_rate, f, (float)Equalizer.ShelfSlope, g);
                        break;
                    case EqBandKind.HighShelf:
                        _eq[ch, b] = BiQuadFilter.HighShelf(_rate, f, (float)Equalizer.ShelfSlope, g);
                        break;
                    default:
                        if (_eq[ch, b] is { } existing) existing.SetPeakingEq(_rate, f, q, g);
                        else _eq[ch, b] = BiQuadFilter.PeakingEQ(_rate, f, q, g);
                        break;
                }
            }
        }
    }

    // NAudio passes a byte[] as float[]: Array.Clear would miss samples.
    private static void Zero(float[] buf, int offset, int count)
    {
        for (int i = 0; i < count; i++) buf[offset + i] = 0f;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        Voice? finished = null;
        bool ended = false, nearEnd = false;
        lock (_lock)
        {
            Zero(buffer, offset, count);
            if (_current is { } cur)
            {
                int r = ReadVoice(cur, buffer, offset, count);
                if (r < count && cur.PendingSeek == null && !cur.Ended)
                {
                    cur.Ended = true;
                    ended = true;
                }
                if (!cur.NearEndRaised && cur.PendingSeek == null && cur.Source.Position.TotalSeconds >= cur.CrossfadeAt)
                {
                    cur.NearEndRaised = true;
                    nearEnd = true;
                }
            }
            if (_outgoing is { } old)
            {
                if (_mix.Length < count) _mix = new float[count];
                Zero(_mix, 0, count);
                int r = ReadVoice(old, _mix, 0, count);
                for (int i = 0; i < r; i++) buffer[offset + i] += _mix[i];
                if (r < count || (old.Fade <= 0f && old.FadeTarget <= 0f))
                {
                    finished = old;
                    _outgoing = null;
                }
            }
            if (_current != null || finished != null || _outgoing != null) Process(buffer, offset, count);
        }
        finished?.Dispose();
        if (ended) Ended?.Invoke();
        if (nearEnd) NearEnd?.Invoke();
        return count;
    }

    private int ReadVoice(Voice v, float[] dst, int offset, int count)
    {
        if (v.PendingSeek is { } seek)
        {
            // Fade out, jump, fade in: no click.
            if (v.Fade <= 0f)
            {
                try { v.Source.Seek(seek); } catch { }
                v.PendingSeek = null;
                v.Rebuild(_rate);
                v.FadeTo(1, MicroFade, _rate);
            }
            else if (v.FadeTarget > 0f) v.FadeTo(0, SeekFade, _rate);
        }
        if (v.Chain == null) return 0;
        int read;
        try { read = v.Chain.Read(dst, offset, count); }
        catch { read = 0; }
        for (int i = 0; i + 1 < read; i += 2)
        {
            if (v.Fade != v.FadeTarget)
                v.Fade = v.Fade < v.FadeTarget ? Math.Min(v.FadeTarget, v.Fade + v.FadeStep) : Math.Max(v.FadeTarget, v.Fade - v.FadeStep);
            float g = v.Fade >= 1f ? v.Gain : v.Gain * MathF.Sin(v.Fade * MathF.PI / 2);
            dst[offset + i] *= g;
            dst[offset + i + 1] *= g;
        }
        return read;
    }

    private void Process(float[] buf, int offset, int count)
    {
        float target = _volume * (_eqEnabled ? _eqPreamp : 1f);
        float g0 = _gain;
        float step = (target - g0) / Math.Max(1, count / 2);
        bool eq = _eqEnabled;
        for (int i = 0; i + 1 < count; i += 2)
        {
            if (_level != _levelTarget)
                _level = _level < _levelTarget ? Math.Min(_levelTarget, _level + _levelStep) : Math.Max(_levelTarget, _level - _levelStep);
            float g = (g0 + step * (i / 2)) * _level;
            for (int ch = 0; ch < 2; ch++)
            {
                float x = buf[offset + i + ch];
                if (eq)
                    for (int b = 0; b < Equalizer.BandCount; b++)
                        if (_eq[ch, b] is { } f) x = f.Transform(x);
                x *= g;
                float ax = Math.Abs(x);
                if (ax > 0.9f) x = Math.Sign(x) * (0.9f + 0.1f * MathF.Tanh((ax - 0.9f) / 0.1f));
                buf[offset + i + ch] = x;
            }
        }
        _gain = target;
    }
}

// Default output device, followed when it changes.
public sealed class AudioEngine : IDisposable, IMMNotificationClient
{
    public const int LatencyMs = 120;
    private const double PauseFade = 0.06;

    private readonly Dispatcher _ui;
    private readonly MMDeviceEnumerator _devices = new();
    private readonly MasterProvider _master = new(48000);
    private WasapiOut? _out;
    private bool _playing;
    private int _pauseVersion;
    private double _crossfade;

    public event Action? Ended;
    public event Action? NearEnd;
    public event Action<string>? Failed;

    public AudioEngine(Dispatcher ui)
    {
        _ui = ui;
        _master.Ended += () => _ui.BeginInvoke(() => Ended?.Invoke());
        _master.NearEnd += () => _ui.BeginInvoke(() => NearEnd?.Invoke());
        try { _devices.RegisterEndpointNotificationCallback(this); } catch { }
    }

    public bool IsPlaying => _playing;
    public bool HasSource => _master.HasSource;
    public string? SourcePath { get; private set; }

    public TimeSpan Position
    {
        get
        {
            var p = _master.Position;
            if (_playing) p -= TimeSpan.FromMilliseconds(LatencyMs);
            return p < TimeSpan.Zero ? TimeSpan.Zero : p;
        }
    }

    public TimeSpan Duration => _master.Duration;

    public float Volume { set => _master.Volume = value; }

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

    private static Task<ITrackSource> OpenSource(string path, double durationHint) => Task.Run<ITrackSource>(() =>
    {
        if (!File.Exists(path)) throw new FileNotFoundException("File not found", path);
        try { return new MfTrackSource(path); }
        catch { return new FfmpegTrackSource(path, TimeSpan.FromSeconds(durationHint)); }
    });

    // Decoders are created off the UI thread.
    public async Task OpenAsync(string path, double durationHint, double trackGainDb)
    {
        var source = await OpenSource(path, durationHint);
        _master.SetSource(source, trackGainDb, durationHint, _crossfade, _playing);
        SourcePath = path;
    }

    public async Task CrossfadeToAsync(string path, double durationHint, double trackGainDb)
    {
        var source = await OpenSource(path, durationHint);
        double seconds = Math.Clamp(Math.Min(_crossfade, _master.RemainingSeconds), 0.3, 12);
        _master.Crossfade(source, trackGainDb, durationHint, _crossfade, seconds);
        SourcePath = path;
    }

    public void Close()
    {
        _master.SetSource(null, 0, 0, 0, _playing);
        SourcePath = null;
    }

    public void Seek(TimeSpan t) => _master.Seek(t);

    public void Play()
    {
        _pauseVersion++;
        try
        {
            bool wasStopped = !_playing;
            EnsureOutput();
            if (wasStopped) _master.FadeLevel(0, 0);
            _out!.Play();
            _playing = true;
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
            try { _out?.Pause(); } catch { }
        }));
    }

    private void EnsureOutput()
    {
        if (_out != null) return;
        var device = _devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        int rate = 48000;
        try { rate = device.AudioClient.MixFormat.SampleRate; } catch { }
        _master.SetRate(rate);
        var o = new WasapiOut(device, AudioClientShareMode.Shared, true, LatencyMs);
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

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia) _ui.BeginInvoke(SwitchDevice);
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) { }
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) { }
    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        try { _devices.UnregisterEndpointNotificationCallback(this); } catch { }
        ResetOutput();
        _master.SetSource(null, 0, 0, 0, false);
    }
}
