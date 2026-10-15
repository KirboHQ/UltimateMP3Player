using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using NAudio.Wave;
using SDL;
using UltimateMP3Player.Core;
using static SDL.SDL3;

namespace UltimateMP3Player.Audio;

// What the audio engine needs from the system, on Linux and macOS: the output device through SDL3 (PipeWire, PulseAudio,
// ALSA or Core Audio, whatever the system has): the one chosen in Settings, or the default one, which SDL follows by
// itself; and the decoders (Decoders.cs): MP3 in managed code (NLayer, sample-exact seeking), WAV directly, everything
// else through ffmpeg.
public static class AudioPlatform
{
    private static volatile bool _ready;

    internal static bool Init()
    {
        if (_ready) return true;
        _ready = SDL_InitSubSystem(SDL_InitFlags.SDL_INIT_AUDIO);
        return _ready;
    }

    // The output chosen in Settings (its name: SDL's ids change from a run to the next), null = the system's default.
    public static string? Device { get; set; }

    // The outputs there now, to choose from in Settings (two with the same name are one).
    public static List<(string Id, string Name)> OutputDevices()
    {
        var list = new List<(string Id, string Name)>();
        if (!Init()) return list;
        foreach (var (_, name) in Playback())
            if (list.All(d => d.Id != name)) list.Add((name, name));
        return list;
    }

    private static List<(SDL_AudioDeviceID Id, string Name)> Playback()
    {
        var list = new List<(SDL_AudioDeviceID, string)>();
        using var ids = SDL_GetAudioPlaybackDevices();
        if (ids == null) return list;
        for (int i = 0; i < ids.Count; i++)
            if (SDL_GetAudioDeviceName(ids[i]) is { Length: > 0 } name) list.Add((ids[i], name));
        return list;
    }

    // The chosen output while it's there; null = the default one.
    private static SDL_AudioDeviceID? Target()
    {
        if (Device is not { } want || !Init()) return null;
        foreach (var (id, name) in Playback())
            if (name == want) return id;
        return null;
    }

    // The device a new output would play on: when it's another one, the engine moves there.
    public static string? OutputId() => Target() != null ? Device : null;

    public static unsafe int OutputRate()
    {
        if (!Init()) return 48000;
        SDL_AudioSpec spec;
        int frames;
        return SDL_GetAudioDeviceFormat(Target() ?? SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec, &frames) && spec.freq > 0 ? spec.freq : 48000;
    }

    public static IWavePlayer CreateOutput(int latencyMs) => new SdlOut(latencyMs, Target() ?? SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK);

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

    // SDL moves the stream to the new default device by itself; the chosen output is looked for every few seconds
    // (unplugged, the music goes to the default one; back, it goes back there). Not before the sound was ever opened:
    // SDL starts on the window's thread.
    public static IDisposable? WatchDevices(Action changed)
    {
        string? last = null;
        return new Timer(_ =>
        {
            if (!_ready) return;
            try
            {
                var now = Device == null ? null : OutputId();
                if (now == last) return;
                last = now;
                changed();
            }
            catch { }
        }, null, 3000, 3000);
    }
}

// A NAudio wave player on an SDL3 playback device (the default one unless chosen). SDL asks for sound on its own thread;
// the stream is kept about latencyMs ahead (what AudioEngine.LatencyMs assumes the output holds).
public sealed unsafe class SdlOut : IWavePlayer
{
    private readonly int _latencyMs;
    private readonly SDL_AudioDeviceID _device;
    private IWaveProvider? _source;
    private SDL_AudioStream* _stream;
    private GCHandle _self;
    private byte[] _buf = Array.Empty<byte>();
    private int _bytesPerSecond;
    private volatile bool _stopped;

    public SdlOut(int latencyMs, SDL_AudioDeviceID device)
    {
        _latencyMs = latencyMs;
        _device = device;
    }

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
        _stream = SDL_OpenAudioDeviceStream(_device, &spec, &Feed, GCHandle.ToIntPtr(_self));
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
