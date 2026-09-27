using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundTouch;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Audio;

// One deck: file → 44.1 kHz stereo → SoundTouch (tempo, key) → 3-band EQ → gain, fader, crossfader.
// Silence while paused. Settings are written by the UI and read by the audio thread.
public sealed class DjDeckEngine : ISampleProvider
{
    public const int SampleRate = 44100;
    // Seconds between a sample leaving Read and being heard (DjEngine's WASAPI buffer).
    private const double OutputLatency = 0.07;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);

    private readonly object _lock = new();
    private readonly SoundTouchProcessor _st = new() { SampleRate = SampleRate, Channels = 2 };
    private readonly float[] _in = new float[4096 * 2];
    private WaveStream? _reader;
    private ISampleProvider? _source;
    private string? _tempWav;

    public volatile bool Playing;
    // Before the start or past the end the deck plays silence, so two decks moved together keep their distance.
    private double _preroll, _postroll;
    private bool _atEnd, _flushed;

    public double Duration { get; private set; }
    public bool IsLoaded => _source != null;
    // Goes up at every jump (seek, new song): the smoothed position on screen goes straight there instead of gliding.
    public int Jumps { get; private set; }

    // ------------------------------------------------------------------ settings

    private double _tempo = 1, _semitones;
    private bool _keyLock = true;

    // Speed ratio (1 = original). With key lock the key stays, otherwise it follows the speed like vinyl.
    public double Tempo { get => _tempo; set { _tempo = value; ApplyTempo(); } }
    public bool KeyLock { get => _keyLock; set { _keyLock = value; ApplyTempo(); } }
    public double Semitones { get => _semitones; set { _semitones = value; ApplyTempo(); } }

    private void ApplyTempo()
    {
        lock (_lock)
        {
            _st.Tempo = _keyLock ? _tempo : 1;
            _st.Rate = _keyLock ? 1 : _tempo;
            _st.PitchSemiTones = _semitones;
        }
    }

    public volatile float Gain = 1, Fader = 1, XFade = 1;
    // dB, -26 = killed.
    private float _low, _mid, _high;
    private int _eqVersion, _eqBuilt = -1;
    private readonly BiQuadFilter[] _filters = new BiQuadFilter[6];
    public float Low { get => _low; set { _low = value; _eqVersion++; } }
    public float Mid { get => _mid; set { _mid = value; _eqVersion++; } }
    public float High { get => _high; set { _high = value; _eqVersion++; } }

    // Level of the last buffer (0-1), for the meters.
    public volatile float Level;

    // ------------------------------------------------------------------ loading

    // Always decoded by ffmpeg into a temporary WAV: the same decoder as the waveform and the beat grid,
    // and sample-exact seeking (Windows' MP3 decoder played ~60 ms off after a seek).
    public async Task LoadAsync(string path)
    {
        var temp = Path.Combine(AppPaths.TempDir, "dj-" + Ids.New() + ".wav");
        var r = await ProcRunner.RunAsync(Engines.Ffmpeg, new[] { "-v", "error", "-y", "-i", path, "-vn", "-ac", "2", "-ar", SampleRate.ToString(), temp },
            captureOut: false, timeout: TimeSpan.FromMinutes(3));
        if (r.ExitCode != 0 || !File.Exists(temp)) throw new EngineException(L.T("File non leggibile:") + " " + Path.GetFileName(path));
        WaveStream reader = new WaveFileReader(temp);
        ISampleProvider sp = reader.ToSampleProvider();
        if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
        if (sp.WaveFormat.SampleRate != SampleRate) sp = new WdlResamplingSampleProvider(sp, SampleRate);
        lock (_lock)
        {
            Close();
            _reader = reader;
            _source = sp;
            _tempWav = temp;
            Duration = reader.TotalTime.TotalSeconds;
            _st.Clear();
            _preroll = _postroll = 0;
            _atEnd = _flushed = false;
            Jumps++;
        }
    }

    public void Unload()
    {
        Playing = false;
        lock (_lock) Close();
        Duration = 0;
    }

    private void Close()
    {
        _reader?.Dispose();
        _reader = null;
        _source = null;
        if (_tempWav != null) try { File.Delete(_tempWav); } catch { }
        _tempWav = null;
    }

    // Where the listener is, in seconds of the song (the reader is ahead by what SoundTouch holds).
    // Negative before the start, past Duration after the end.
    public double Position
    {
        get
        {
            lock (_lock)
            {
                if (_reader == null) return 0;
                if (_preroll > 0) return -_preroll;
                if (_atEnd) return Duration + _postroll;
                double buffered = (_st.UnprocessedSampleCount + _st.AvailableSamples * _tempo) / (double)SampleRate;
                // While playing, what reaches the speakers is also behind by the output buffer.
                if (Playing) buffered += OutputLatency * _tempo;
                return Math.Max(0, _reader.CurrentTime.TotalSeconds - buffered);
            }
        }
    }

    public void Seek(double seconds)
    {
        lock (_lock)
        {
            if (_reader == null) return;
            _st.Clear();
            _preroll = _postroll = 0;
            _atEnd = _flushed = false;
            Jumps++;
            if (seconds < 0)
            {
                _preroll = -seconds;
                _reader.CurrentTime = TimeSpan.Zero;
            }
            else if (seconds >= Duration)
            {
                _atEnd = true;
                _postroll = seconds - Duration;
            }
            else _reader.CurrentTime = TimeSpan.FromSeconds(seconds);
        }
    }

    // ------------------------------------------------------------------ audio thread

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / 2, got = 0;
        if (Playing && _source != null)
        {
            lock (_lock)
            {
                var span = buffer.AsSpan(offset, count);
                while (got < frames && _source != null)
                {
                    if (_preroll > 0)
                    {
                        // Silence until the song's start comes round.
                        int n = Math.Min(frames - got, Math.Max(1, (int)Math.Ceiling(_preroll * SampleRate / _tempo)));
                        span.Slice(got * 2, n * 2).Clear();
                        _preroll = Math.Max(0, _preroll - n * _tempo / SampleRate);
                        got += n;
                        continue;
                    }
                    if (_atEnd)
                    {
                        int n = frames - got;
                        span.Slice(got * 2, n * 2).Clear();
                        _postroll += n * _tempo / SampleRate;
                        got += n;
                        break;
                    }
                    if (_st.AvailableSamples > 0)
                    {
                        var dst = span.Slice(got * 2);
                        got += _st.ReceiveSamples(dst, frames - got);
                        continue;
                    }
                    int read = _source.Read(_in, 0, _in.Length);
                    if (read == 0)
                    {
                        if (!_flushed)
                        {
                            _flushed = true;
                            _st.Flush();
                            if (_st.AvailableSamples > 0) continue;
                        }
                        _atEnd = true;
                        continue;
                    }
                    ReadOnlySpan<float> input = _in;
                    _st.PutSamples(input, read / 2);
                }
            }
        }
        Array.Clear(buffer, offset + got * 2, count - got * 2);
        if (got > 0) Shape(buffer, offset, got * 2);
        else Level *= 0.8f;
        return count;
    }

    private void Shape(float[] b, int offset, int n)
    {
        if (_eqBuilt != _eqVersion)
        {
            _eqBuilt = _eqVersion;
            for (int c = 0; c < 2; c++)
            {
                _filters[c * 3] = BiQuadFilter.LowShelf(SampleRate, 250, 1, _low);
                _filters[c * 3 + 1] = BiQuadFilter.PeakingEQ(SampleRate, 1000, 0.8f, _mid);
                _filters[c * 3 + 2] = BiQuadFilter.HighShelf(SampleRate, 4000, 1, _high);
            }
        }
        bool eq = _low != 0 || _mid != 0 || _high != 0;
        float g = Gain * Fader * XFade, peak = 0;
        for (int i = 0; i < n; i++)
        {
            float x = b[offset + i];
            if (eq)
            {
                int c = (i & 1) * 3;
                x = _filters[c + 2].Transform(_filters[c + 1].Transform(_filters[c].Transform(x)));
            }
            x *= g;
            b[offset + i] = x;
            float a = Math.Abs(x);
            if (a > peak) peak = a;
        }
        Level = Math.Max(peak, Level * 0.85f);
    }
}

// Both decks summed (plus the tap tool's player), a soft limiter, the output device and the recorder.
public sealed class DjEngine : ISampleProvider, IDisposable
{
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(DjDeckEngine.SampleRate, 2);

    private readonly MixingSampleProvider _mix;
    private WasapiOut? _out;
    private WaveFileWriter? _rec;
    private readonly object _recLock = new();

    public DjDeckEngine A { get; } = new();
    public DjDeckEngine B { get; } = new();
    // The "Tap BPM" tab: the song as it is, outside the crossfader.
    public DjDeckEngine T { get; } = new();
    public volatile float Master = 1;
    public volatile float Level;

    public DjEngine()
    {
        _mix = new MixingSampleProvider(WaveFormat) { ReadFully = true };
        _mix.AddMixerInput(A);
        _mix.AddMixerInput(B);
        _mix.AddMixerInput(T);
    }

    private float _xfade;
    // -1 = only A, 1 = only B; both at full volume in the middle, constant power towards the sides.
    public float Crossfader
    {
        get => _xfade;
        set
        {
            _xfade = Math.Clamp(value, -1, 1);
            double t = (_xfade + 1) * Math.PI / 4;
            A.XFade = (float)Math.Min(1, Math.Cos(t) * Math.Sqrt(2));
            B.XFade = (float)Math.Min(1, Math.Sin(t) * Math.Sqrt(2));
        }
    }

    public void EnsureRunning()
    {
        if (_out != null) return;
        _out = new WasapiOut(AudioClientShareMode.Shared, true, 60);
        _out.Init(this);
        _out.Play();
    }

    public int Read(float[] buffer, int offset, int count)
    {
        _mix.Read(buffer, offset, count);
        float m = Master, peak = 0;
        for (int i = offset, end = offset + count; i < end; i++)
        {
            float x = buffer[i] * m;
            float a = Math.Abs(x);
            // Soft knee above 0.8 instead of hard clipping.
            if (a > 0.8f) x = Math.Sign(x) * (0.8f + 0.2f * (float)Math.Tanh((a - 0.8f) / 0.2f));
            buffer[i] = x;
            if (a > peak) peak = a;
        }
        Level = Math.Max(peak, Level * 0.85f);
        lock (_recLock) _rec?.WriteSamples(buffer, offset, count);
        return count;
    }

    // ------------------------------------------------------------------ recording

    public string? RecordingFile { get; private set; }
    public bool IsRecording => _rec != null;

    public void StartRecording()
    {
        EnsureRunning();
        var file = Path.Combine(AppPaths.TempDir, "dj-rec-" + Ids.New() + ".wav");
        lock (_recLock) _rec = new WaveFileWriter(new BufferedStream(File.Create(file), 1 << 20), WaveFormat);
        RecordingFile = file;
    }

    // Returns the recorded WAV.
    public string? StopRecording()
    {
        lock (_recLock)
        {
            _rec?.Dispose();
            _rec = null;
        }
        return RecordingFile;
    }

    public void Dispose()
    {
        A.Playing = B.Playing = T.Playing = false;
        StopRecording();
        try { _out?.Stop(); } catch { }
        _out?.Dispose();
        _out = null;
        A.Unload();
        B.Unload();
        T.Unload();
    }
}
