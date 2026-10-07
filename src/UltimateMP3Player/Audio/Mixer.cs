using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundTouch;
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

// Current song plus the fading one, then speed, EQ, volume, limiter.
public sealed class MasterProvider : ISampleProvider
{
    private const double MicroFade = 0.03;
    private const double SeekFade = 0.015;
    // Frames the songs are read in while the speed is changed.
    private const int StretchChunk = 1024;

    private readonly object _lock = new();
    private Voice? _current;
    private Voice? _outgoing;
    private float[] _mix = Array.Empty<float>();
    private int _rate;

    // Playback speed (YouTube-like, 0.5-2): through SoundTouch, keeping the key or, with _pitch, like a record played faster.
    // At 1 the songs go straight through. A new speed of the same kind is applied on the fly; going in or out of
    // the stretcher, turning the key option on or off (or crossing 1 with it on: SoundTouch rearranges its stages)
    // the sound dips for a moment after SoundTouch (_swap) and, once silent, the stretcher starts again from the
    // point being heard (_reconfigure): no click, no jump.
    private const double SwapFade = 0.02;
    private readonly SoundTouchProcessor _st = new() { Channels = 2 };
    private readonly float[] _stIn = new float[StretchChunk * 2];
    private float[] _stOut = Array.Empty<float>();
    // Wanted, and what SoundTouch runs at now.
    private double _speed = 1, _stSpeed = 1;
    private bool _pitch, _stPitch, _stretching, _reconfigure;
    private float _swap = 1f, _swapTarget = 1f, _swapStep = 1f;

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
        _st.SampleRate = rate;
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
            _st.SampleRate = rate;
            _st.Clear();
        }
    }

    public bool HasSource
    {
        get { lock (_lock) return _current != null; }
    }

    // Where the song is, in seconds of the song: what SoundTouch still holds hasn't been heard yet.
    public TimeSpan Position
    {
        get
        {
            lock (_lock)
            {
                if (_current == null) return TimeSpan.Zero;
                if (_current.PendingSeek is { } seek) return seek;
                var p = _current.Source.Position;
                if (_stretching) p -= TimeSpan.FromSeconds(Buffered);
                return p < TimeSpan.Zero ? TimeSpan.Zero : p;
            }
        }
    }

    // Seconds of the song inside SoundTouch, not heard yet.
    private double Buffered => (_st.UnprocessedSampleCount + _st.AvailableSamples * _stSpeed) / _rate;

    public double Speed
    {
        get { lock (_lock) return _speed; }
    }

    // Faster or slower (0.5-2); pitch = the key goes up and down with the speed.
    public void SetSpeed(double speed, bool pitch)
    {
        speed = Math.Clamp(speed, 0.5, 2);
        if (Math.Abs(speed - 1) < 0.001) speed = 1;
        lock (_lock)
        {
            if (speed == _speed && pitch == _pitch) return;
            _speed = speed;
            _pitch = pitch;
            bool want = speed != 1;
            if (!want && !_stretching)
            {
                // Still straight through (only the key option changed at 1×).
                _reconfigure = false;
                _swapTarget = 1f;
            }
            else if (want && _stretching && pitch == _stPitch && (!pitch || Math.Sign(speed - 1) == Math.Sign(_stSpeed - 1)))
            {
                _reconfigure = false;
                _swapTarget = 1f;
                ApplyStretch();
            }
            else
            {
                // Read restarts the stretcher once the dip is silent.
                _reconfigure = true;
                _swapStep = (float)(1.0 / (SwapFade * _rate));
                _swapTarget = 0f;
            }
        }
    }

    private void ApplyStretch()
    {
        _st.Tempo = _pitch ? 1 : _speed;
        _st.Rate = _pitch ? _speed : 1;
        _stSpeed = _speed;
        _stPitch = _pitch;
    }

    private void Reconfigure()
    {
        // The song goes back to the point being heard (what SoundTouch still holds would be skipped).
        if (_stretching && _current is { PendingSeek: null } cur)
        {
            double heard = cur.Source.Position.TotalSeconds - Buffered;
            try { cur.Source.Seek(TimeSpan.FromSeconds(Math.Max(0, heard))); } catch { }
            cur.Rebuild(_rate);
        }
        _st.Clear();
        ApplyStretch();
        _stretching = _speed != 1;
        _reconfigure = false;
        _swapTarget = 1f;
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
                    Leave(_outgoing, MicroFade);
                }
                else drop.Add(_current);
            }
            if (!smooth)
            {
                if (_outgoing != null)
                {
                    drop.Add(_outgoing);
                    _outgoing = null;
                }
                // Nothing of the old song is left to hear: a change of the stretcher can happen right away.
                _st.Clear();
                ApplyStretch();
                _stretching = _speed != 1;
                _reconfigure = false;
                _swapTarget = 1f;
            }
            _current = v;
        }
        foreach (var d in drop) d.Dispose();
    }

    // A song on its way out only fades: a jump still pending on it would have faded it back in, and it played on
    // under the new one (the "two songs at once" of Listen together).
    private void Leave(Voice v, double seconds)
    {
        v.PendingSeek = null;
        v.FadeTo(0, seconds, _rate);
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
            if (_outgoing != null) Leave(_outgoing, seconds);
            _current = v;
        }
        drop?.Dispose();
    }

    // The same song from another file (its copy in the library is about to be deleted): the new file starts right where
    // the old one is, with the shortest of crossfades, so nothing is heard. False: nothing was playing.
    public bool Swap(ITrackSource source)
    {
        Voice? drop;
        lock (_lock)
        {
            if (_current is not { } cur) return false;
            var v = new Voice(source, cur.Gain, cur.Duration) { CrossfadeAt = cur.CrossfadeAt, NearEndRaised = cur.NearEndRaised, Ended = cur.Ended };
            v.Rebuild(_rate);
            // Fade 0 with a jump pending: the first read seeks it there and fades it in.
            v.PendingSeek = cur.PendingSeek ?? cur.Source.Position;
            v.Fade = 0;
            v.FadeTarget = 0;
            drop = _outgoing;
            _outgoing = cur;
            Leave(cur, MicroFade);
            _current = v;
        }
        drop?.Dispose();
        return true;
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

    // The song has been read to its end (what's left of it may still be waiting in the output).
    public bool ReadToEnd
    {
        get { lock (_lock) return _current is { Ended: true }; }
    }

    // Paused, and the output threw away what it still held: the song goes back to the point heard last, right away (a
    // song through ffmpeg restarts it there during the pause), and fades in when it plays again.
    public void Rewind(TimeSpan t)
    {
        lock (_lock)
        {
            if (_current is not { } cur) return;
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            try { cur.Source.Seek(t); } catch { }
            cur.PendingSeek = null;
            cur.Rebuild(_rate);
            cur.Fade = 0f;
            cur.FadeTo(1, MicroFade, _rate);
            cur.Ended = false;
            cur.NearEndRaised = cur.CrossfadeAt <= t.TotalSeconds;
            // What SoundTouch still held came after that point.
            if (_stretching) _st.Clear();
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
            bool sound = _current != null || _outgoing != null;
            if (_reconfigure && (_swap <= 0f || !sound)) Reconfigure();
            if (!_stretching)
            {
                Zero(buffer, offset, count);
                Mix(buffer, offset, count, ref finished, ref ended, ref nearEnd);
            }
            else Stretch(buffer, offset, count, ref finished, ref ended, ref nearEnd);
            if (sound || finished != null) Process(buffer, offset, count);
        }
        finished?.Dispose();
        if (ended) Ended?.Invoke();
        if (nearEnd) NearEnd?.Invoke();
        return count;
    }

    // The songs, read in small blocks through SoundTouch until the output buffer is full.
    private void Stretch(float[] buffer, int offset, int count, ref Voice? finished, ref bool ended, ref bool nearEnd)
    {
        int frames = count / 2, got = 0;
        if (_stOut.Length < count) _stOut = new float[count];
        for (int guard = 0; got < frames && guard < 256; guard++)
        {
            if (_st.AvailableSamples > 0)
            {
                int n = _st.ReceiveSamples(_stOut.AsSpan(0, (frames - got) * 2), frames - got);
                // Element by element: NAudio's buffer is a byte[] seen as float[].
                for (int i = 0; i < n * 2; i++) buffer[offset + got * 2 + i] = _stOut[i];
                got += n;
                continue;
            }
            Zero(_stIn, 0, _stIn.Length);
            Mix(_stIn, 0, _stIn.Length, ref finished, ref ended, ref nearEnd);
            _st.PutSamples(_stIn, StretchChunk);
        }
        if (got < frames) Zero(buffer, offset + got * 2, count - got * 2);
    }

    private void Mix(float[] dst, int offset, int count, ref Voice? finished, ref bool ended, ref bool nearEnd)
    {
        if (_current is { } cur)
        {
            int r = ReadVoice(cur, dst, offset, count);
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
            for (int i = 0; i < r; i++) dst[offset + i] += _mix[i];
            if (r < count || (old.Fade <= 0f && old.FadeTarget <= 0f))
            {
                finished = old;
                _outgoing = null;
            }
        }
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
        // A jump asked at the end of the song (repeat one, back to the start): no sound left to fade out,
        // so the fade would never finish and the song stayed stuck in silence. Jump on the next read.
        if (v.PendingSeek != null && read < count) v.Fade = 0f;
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
            if (_swap != _swapTarget)
                _swap = _swap < _swapTarget ? Math.Min(_swapTarget, _swap + _swapStep) : Math.Max(_swapTarget, _swap - _swapStep);
            float g = (g0 + step * (i / 2)) * _level * _swap;
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
