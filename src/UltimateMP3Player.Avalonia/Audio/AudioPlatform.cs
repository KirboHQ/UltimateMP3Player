using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NAudio.Wave;
using SDL;
using UltimateMP3Player.Core;
using static SDL.SDL3;

namespace UltimateMP3Player.Audio;

// What the audio engine needs from the system, on Linux and macOS: the default output device through SDL3 (PipeWire,
// PulseAudio, ALSA or Core Audio, whatever the system has; it follows the default device by itself) and the decoders
// (Decoders.cs): MP3 in managed code (NLayer, sample-exact seeking), WAV directly, everything else through ffmpeg.
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
