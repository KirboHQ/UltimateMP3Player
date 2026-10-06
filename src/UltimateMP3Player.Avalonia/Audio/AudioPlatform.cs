using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SDL;
using UltimateMP3Player.Core;
using static SDL.SDL3;

namespace UltimateMP3Player.Audio;

// What the audio engine needs from the system, on Linux and macOS: the default output device through SDL3 (PipeWire,
// PulseAudio, ALSA or Core Audio, whatever the system has; it follows the default device by itself) and the decoders:
// MP3 in managed code (NLayer, sample-exact seeking), WAV directly, everything else through ffmpeg.
public static class AudioPlatform
{
    private static bool _ready;

    internal static bool Init()
    {
        if (_ready) return true;
        _ready = SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO);
        return _ready;
    }

    public static unsafe int OutputRate()
    {
        if (!Init()) return 48000;
        SDL_AudioSpec spec;
        int frames;
        return SDL_GetAudioDeviceFormat(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, &frames) && spec.freq > 0 ? spec.freq : 48000;
    }

    public static IWavePlayer CreateOutput(int latencyMs) => new SdlOut(latencyMs);

    public static ITrackSource Open(string path, double durationHint)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("File not found", path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (ext == ".mp3") return new Mp3TrackSource(path);
            if (ext == ".wav") return new ReaderTrackSource(new WaveFileReader(path));
        }
        catch { }
        return new StreamingFfmpegSource(path, TimeSpan.FromSeconds(durationHint));
    }

    // SDL moves the stream to the new default device by itself.
    public static IDisposable? WatchDefaultDevice(Action changed) => null;
}

// A NAudio wave player on SDL3's default playback device. SDL asks for sound on its own thread; the stream is kept about
// latencyMs ahead (what AudioEngine.LatencyMs assumes the output holds).
public sealed unsafe class SdlOut : IWavePlayer
{
    private readonly int _latencyMs;
    private IWaveProvider? _source;
    private SDL_AudioStream* _stream;
    private GCHandle _self;
    private byte[] _buf = Array.Empty<byte>();
    private int _bytesPerSecond;
    private volatile bool _stopped;

    public SdlOut(int latencyMs) => _latencyMs = latencyMs;

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;
    public float Volume { get; set; } = 1f;
    public WaveFormat OutputWaveFormat => _source?.WaveFormat ?? WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    public void Init(IWaveProvider waveProvider)
    {
        if (!AudioPlatform.Init()) throw new InvalidOperationException(SDL_GetError() ?? "SDL audio");
        _source = waveProvider;
        var wf = waveProvider.WaveFormat;
        if (wf.Encoding != WaveFormatEncoding.IeeeFloat || wf.BitsPerSample != 32)
            throw new ArgumentException("SdlOut plays 32-bit float");
        _bytesPerSecond = wf.AverageBytesPerSecond;
        var spec = new SDL_AudioSpec { format = SDL_AudioFormat.SDL_AUDIO_F32LE, channels = wf.Channels, freq = wf.SampleRate };
        _self = GCHandle.Alloc(this);
        _stream = SDL_OpenAudioDeviceStream(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, &Feed, GCHandle.ToIntPtr(_self));
        if (_stream == null)
        {
            _self.Free();
            throw new InvalidOperationException(SDL_GetError() ?? "SDL audio");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Feed(IntPtr userdata, SDL_AudioStream* stream, int additional, int total)
    {
        try
        {
            if (GCHandle.FromIntPtr(userdata).Target is SdlOut self) self.Fill(stream, additional);
        }
        catch { }
    }

    private void Fill(SDL_AudioStream* stream, int additional)
    {
        if (_stopped || _source == null) return;
        // Keep about latencyMs queued: less means gaps when the system is busy, more means a late picture of where the song is.
        int queued = SDL_GetAudioStreamQueued(stream);
        int target = _bytesPerSecond / 1000 * _latencyMs;
        int want = Math.Max(additional, target - queued);
        int frame = _source.WaveFormat.BlockAlign;
        want = want / frame * frame;
        if (want <= 0) return;
        if (_buf.Length < want) _buf = new byte[want];
        int got = _source.Read(_buf, 0, want);
        if (got < want) Array.Clear(_buf, got, want - got);
        if (Volume < 0.999f)
        {
            var f = MemoryMarshal.Cast<byte, float>(_buf.AsSpan(0, want));
            for (int i = 0; i < f.Length; i++) f[i] *= Volume;
        }
        fixed (byte* p = _buf) SDL_PutAudioStreamData(stream, (IntPtr)p, want);
    }

    public void Play()
    {
        if (_stream == null) return;
        _stopped = false;
        SDL_ResumeAudioStreamDevice(_stream);
        PlaybackState = PlaybackState.Playing;
    }

    public void Pause()
    {
        if (_stream == null) return;
        SDL_PauseAudioStreamDevice(_stream);
        PlaybackState = PlaybackState.Paused;
    }

    public void Stop()
    {
        if (_stream == null) return;
        _stopped = true;
        SDL_PauseAudioStreamDevice(_stream);
        SDL_ClearAudioStream(_stream);
        PlaybackState = PlaybackState.Stopped;
        PlaybackStopped?.Invoke(this, new StoppedEventArgs());
    }

    public void Dispose()
    {
        if (_stream == null) return;
        _stopped = true;
        SDL_DestroyAudioStream(_stream);
        _stream = null;
        if (_self.IsAllocated) _self.Free();
        PlaybackState = PlaybackState.Stopped;
    }
}

// MP3 decoded in managed code (NLayer): the same on every system, sample-exact seeking.
public sealed class Mp3TrackSource : ITrackSource
{
    private readonly Mp3FileReaderBase _reader;

    public Mp3TrackSource(string path)
    {
        _reader = new Mp3FileReaderBase(path, wf => new NLayer.NAudioSupport.Mp3FrameDecompressor(wf));
        ISampleProvider s = _reader.ToSampleProvider();
        if (s.WaveFormat.Channels == 1) s = new MonoToStereoSampleProvider(s);
        Samples = s;
    }

    public ISampleProvider Samples { get; }
    public TimeSpan Position => _reader.CurrentTime;
    public TimeSpan Duration => _reader.TotalTime;
    public void Seek(TimeSpan t) => _reader.CurrentTime = t < TimeSpan.Zero ? TimeSpan.Zero : t;
    public void Dispose() => _reader.Dispose();
}

// A NAudio reader (WAV).
public sealed class ReaderTrackSource : ITrackSource
{
    private readonly WaveStream _reader;

    public ReaderTrackSource(WaveStream reader)
    {
        _reader = reader;
        ISampleProvider s = reader.ToSampleProvider();
        if (s.WaveFormat.Channels == 1) s = new MonoToStereoSampleProvider(s);
        Samples = s;
    }

    public ISampleProvider Samples { get; }
    public TimeSpan Position => _reader.CurrentTime;
    public TimeSpan Duration => _reader.TotalTime;
    public void Seek(TimeSpan t) => _reader.CurrentTime = t < TimeSpan.Zero ? TimeSpan.Zero : t;
    public void Dispose() => _reader.Dispose();
}

// ffmpeg's PCM for every other format (M4A, Opus, WebM, FLAC...). A seek starts a new ffmpeg in the background: until it
// answers the song is silent (the audio thread never waits for a process to start).
public sealed class StreamingFfmpegSource : ITrackSource, ISampleProvider
{
    private const int Rate = 48000;
    private readonly string _path;
    private readonly object _lock = new();
    private Process? _proc;
    private Stream? _out;
    private TimeSpan _start;
    private long _frames;
    private byte[] _bytes = Array.Empty<byte>();
    private int _leftover;
    private Task<(Process?, Stream?)>? _pending;
    private TimeSpan _pendingAt;

    public StreamingFfmpegSource(string path, TimeSpan duration)
    {
        _path = path;
        Duration = duration;
        (_proc, _out) = StartAt(TimeSpan.Zero);
    }

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
    public ISampleProvider Samples => this;
    public TimeSpan Duration { get; }

    public TimeSpan Position
    {
        get
        {
            lock (_lock) return _pending != null ? _pendingAt : _start + TimeSpan.FromSeconds(_frames / (double)Rate);
        }
    }

    private (Process?, Stream?) StartAt(TimeSpan at)
    {
        var psi = new ProcessStartInfo(Engines.Ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-nostdin", "-loglevel", "quiet", "-ss", at.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                     "-i", _path, "-vn", "-f", "f32le", "-ac", "2", "-ar", Rate.ToString(), "pipe:1" })
            psi.ArgumentList.Add(a);
        var p = Process.Start(psi);
        return (p, p?.StandardOutput.BaseStream);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            if (_pending != null)
            {
                if (!_pending.IsCompleted)
                {
                    for (int i = 0; i < count; i++) buffer[offset + i] = 0;
                    return count;
                }
                Kill();
                (_proc, _out) = _pending.IsCompletedSuccessfully ? _pending.Result : (null, null);
                _start = _pendingAt;
                _frames = 0;
                _leftover = 0;
                _pending = null;
            }
        }
        if (_out == null) return 0;
        int bytesWanted = count * 4;
        if (_bytes.Length < bytesWanted) _bytes = new byte[bytesWanted];
        int got = _leftover;
        _leftover = 0;
        try
        {
            while (got < bytesWanted)
            {
                int n = _out.Read(_bytes, got, bytesWanted - got);
                if (n <= 0) break;
                got += n;
            }
        }
        catch { }
        int whole = got / 8 * 8;
        Buffer.BlockCopy(_bytes, 0, buffer, offset * 4, whole);
        if (got > whole)
        {
            Buffer.BlockCopy(_bytes, whole, _bytes, 0, got - whole);
            _leftover = got - whole;
        }
        _frames += whole / 8;
        return whole / 4;
    }

    public void Seek(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        lock (_lock)
        {
            _pendingAt = t;
            _pending = Task.Run(() => StartAt(t));
        }
    }

    private void Kill()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(); } catch { }
        _proc?.Dispose();
        _proc = null;
        _out = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            Kill();
            if (_pending is { } p)
                _ = p.ContinueWith(t =>
                {
                    try { if (t.IsCompletedSuccessfully && t.Result.Item1 is { HasExited: false } pr) pr.Kill(); } catch { }
                });
        }
    }
}
