using System.Runtime.InteropServices;
using AudioToolbox;
using AudioUnit;
using AVFoundation;
using Foundation;
using NAudio.Wave;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Audio;

// What the audio engine needs from the system, on the iPhone: the output through an AudioQueue (iOS sends it to the
// speaker, the headphones, Bluetooth or AirPlay, chosen in Control Center) and the decoders: MP3 in managed code (NLayer,
// sample-exact seeking, as on the other phones), WAV directly, everything else through AudioToolbox (M4A/AAC, ALAC, FLAC,
// AIFF, the audio of MP4 videos). Ogg, Opus and WebM need ffmpeg, which iOS can't run: not yet.
public static class AudioPlatform
{
    public static int OutputRate()
    {
        try
        {
            double rate = AVAudioSession.SharedInstance().SampleRate;
            if (rate is >= 8000 and <= 192000) return (int)rate;
        }
        catch { }
        return 48000;
    }

    public static IWavePlayer CreateOutput(int latencyMs) => new QueueOut(latencyMs);

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
        return new AppleTrackSource(path);
    }

    // The output is iOS's choice (Control Center): nothing to choose in Settings.
    public static string? Device { get; set; }
    public static List<(string Id, string Name)> OutputDevices() => new();
    public static string? OutputId() => null;
    public static IDisposable? WatchDevices(Action changed) => null;
}

// A NAudio wave player on an AudioQueue: three buffers of about latencyMs/3 each go round, refilled from the mix on the
// queue's own thread as each one is played. Paused, the buffers wait in the queue; playing again goes on from them.
public sealed unsafe class QueueOut : IWavePlayer
{
    private const int Buffers = 3;
    private readonly int _latencyMs;
    private readonly object _lock = new();
    // Buffers given back by the queue and not filled again (stopped), filled at the next Play.
    private readonly List<IntPtr> _free = new();
    private OutputAudioQueue? _queue;
    private IWaveProvider? _source;
    private byte[] _bytes = Array.Empty<byte>();
    private int _size;
    private bool _running, _closed;

    public QueueOut(int latencyMs) => _latencyMs = latencyMs;

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;
    public float Volume { get; set; } = 1f;
    public WaveFormat OutputWaveFormat => _source?.WaveFormat ?? WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    public void Init(IWaveProvider waveProvider)
    {
        var wf = waveProvider.WaveFormat;
        if (wf.Encoding != WaveFormatEncoding.IeeeFloat || wf.BitsPerSample != 32 || wf.Channels != 2)
            throw new ArgumentException("QueueOut plays 32-bit float stereo");
        _source = waveProvider;
        _queue = new OutputAudioQueue(AppleTrackSource.Float(wf.SampleRate, 2));
        _queue.BufferCompleted += OnBufferCompleted;
        int frame = wf.BlockAlign;
        _size = Math.Max(frame * 256, wf.AverageBytesPerSecond / 1000 * _latencyMs / Buffers / frame * frame);
        _bytes = new byte[_size];
        for (int i = 0; i < Buffers; i++)
        {
            var status = _queue.AllocateBuffer(_size, out IntPtr buffer);
            if (status != AudioQueueStatus.Ok) throw new InvalidOperationException("AudioQueue " + status);
            _free.Add(buffer);
        }
    }

    private void OnBufferCompleted(object? sender, BufferCompletedEventArgs e)
    {
        lock (_lock)
        {
            if (_closed) return;
            if (!_running)
            {
                _free.Add(e.IntPtrBuffer);
                return;
            }
            Fill(e.IntPtrBuffer);
        }
    }

    // The next piece of the mix into a buffer, and the buffer into the queue (under _lock).
    private void Fill(IntPtr buffer)
    {
        int got;
        try { got = _source!.Read(_bytes, 0, _size); }
        catch { got = 0; }
        if (got < _size) Array.Clear(_bytes, got, _size - got);
        if (Volume < 0.999f)
        {
            var f = MemoryMarshal.Cast<byte, float>(_bytes.AsSpan(0, _size));
            for (int i = 0; i < f.Length; i++) f[i] *= Volume;
        }
        var b = (AudioQueueBuffer*)buffer;
        Marshal.Copy(_bytes, 0, b->AudioData, _size);
        b->AudioDataByteSize = (uint)_size;
        _queue!.EnqueueBuffer(buffer, _size, null!);
    }

    public void Play()
    {
        if (_queue == null || _closed) return;
        AudioSession.Activate();
        lock (_lock)
        {
            _running = true;
            foreach (var b in _free) Fill(b);
            _free.Clear();
        }
        var status = _queue.Start();
        if (status != AudioQueueStatus.Ok) throw new InvalidOperationException("AudioQueue " + status);
        PlaybackState = PlaybackState.Playing;
    }

    public void Pause()
    {
        if (_queue == null || _closed) return;
        _queue.Pause();
        PlaybackState = PlaybackState.Paused;
    }

    public void Stop()
    {
        if (_queue == null || _closed) return;
        lock (_lock) _running = false;
        _queue.Stop(true);
        PlaybackState = PlaybackState.Stopped;
        PlaybackStopped?.Invoke(this, new StoppedEventArgs());
    }

    public void Dispose()
    {
        var q = _queue;
        if (q == null) return;
        // (a buffer being filled finishes first; none starts after this)
        lock (_lock) _closed = true;
        _queue = null;
        q.BufferCompleted -= OnBufferCompleted;
        try { q.Stop(true); } catch { }
        q.Dispose();
        PlaybackState = PlaybackState.Stopped;
    }
}

// AudioToolbox's decoder (ExtAudioFile): M4A/AAC, ALAC, FLAC, AIFF, the audio of MP4 videos, as float at the file's own
// rate (the mix resamples). Mono comes out as mono and is doubled here.
public sealed unsafe class AppleTrackSource : ITrackSource, ISampleProvider
{
    private readonly ExtAudioFile _file;
    private readonly AudioBuffers _list = new(1);
    private readonly object _lock = new();
    private readonly int _rate, _channels;
    private float[] _tmp = Array.Empty<float>();
    private long _frame;

    public AppleTrackSource(string path)
    {
        using var url = NSUrl.FromFilename(path);
        _file = ExtAudioFile.OpenUrl(url, out var error) ?? throw new NotSupportedException(L.F("This format can't be played on iPhone ({0}).", Path.GetExtension(path)));
        var format = _file.FileDataFormat;
        _rate = format.SampleRate is > 0 and <= 384000 ? (int)format.SampleRate : 44100;
        _channels = format.ChannelsPerFrame == 1 ? 1 : 2;
        _file.ClientDataFormat = Float(_rate, _channels);
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_rate, 2);
        Duration = TimeSpan.FromSeconds(Math.Max(0, _file.FileLengthFrames) / (double)_rate);
    }

    // Interleaved 32-bit float PCM.
    internal static AudioStreamBasicDescription Float(double rate, int channels) => new()
    {
        SampleRate = rate,
        Format = AudioFormatType.LinearPCM,
        FormatFlags = AudioFormatFlags.IsFloat | AudioFormatFlags.IsPacked,
        ChannelsPerFrame = channels,
        BitsPerChannel = 32,
        BytesPerFrame = 4 * channels,
        FramesPerPacket = 1,
        BytesPerPacket = 4 * channels,
    };

    public WaveFormat WaveFormat { get; }
    public ISampleProvider Samples => this;
    public TimeSpan Duration { get; }
    public TimeSpan Position => TimeSpan.FromSeconds(Interlocked.Read(ref _frame) / (double)_rate);

    // Frames of the file into buffer (interleaved as the file has them, 1 or 2 channels); 0 at the end.
    internal int ReadFrames(float[] buffer, int frames)
    {
        int done = 0;
        fixed (float* p = buffer)
        {
            while (done < frames)
            {
                _list[0] = new AudioBuffer { NumberChannels = _channels, DataByteSize = (frames - done) * _channels * 4, Data = (IntPtr)(p + done * _channels) };
                uint got = _file.Read((uint)(frames - done), _list, out var error);
                if (got == 0 || error != ExtAudioFileError.OK) break;
                done += (int)got;
            }
        }
        return done;
    }

    internal int Channels => _channels;
    internal int Rate => _rate;

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / 2;
        lock (_lock)
        {
            if (_tmp.Length < frames * _channels) _tmp = new float[frames * _channels];
            int got = ReadFrames(_tmp, frames);
            if (_channels == 2) Array.Copy(_tmp, 0, buffer, offset, got * 2);
            else
                for (int i = 0; i < got; i++)
                {
                    buffer[offset + i * 2] = _tmp[i];
                    buffer[offset + i * 2 + 1] = _tmp[i];
                }
            Interlocked.Add(ref _frame, got);
            return got * 2;
        }
    }

    public void Seek(TimeSpan t)
    {
        long frame = Math.Max(0, (long)(t.TotalSeconds * _rate));
        lock (_lock)
        {
            _file.Seek(frame);
            Interlocked.Exchange(ref _frame, frame);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _file.Dispose();
            _list.Dispose();
        }
    }
}

// The app's audio session: music that goes on with the screen locked (Playback), a call or Siri that pauses it (and
// plays again after, when iOS says so), headphones or a Bluetooth speaker gone that pause it, like every player.
public static class AudioSession
{
    private static bool _ready, _resumeAfter;

    public static void Setup()
    {
        if (_ready) return;
        _ready = true;
        try
        {
            var error = AVAudioSession.SharedInstance().SetCategory(AVAudioSessionCategory.Playback);
            if (error != null) App.Log(new InvalidOperationException("AVAudioSession: " + error.LocalizedDescription));
        }
        catch (Exception ex) { App.Log(ex); }
        AVAudioSession.Notifications.ObserveInterruption((_, e) =>
        {
            bool began = e.InterruptionType == AVAudioSessionInterruptionType.Began;
            bool resume = e.Option == AVAudioSessionInterruptionOptions.ShouldResume;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var media = App.Host?.Media;
                if (began)
                {
                    _resumeAfter = media?.IsPlaying == true;
                    media?.RaisePause();
                }
                else
                {
                    if (_resumeAfter && resume) media?.RaisePlay();
                    _resumeAfter = false;
                }
            });
        });
        AVAudioSession.Notifications.ObserveRouteChange((_, e) =>
        {
            if (e.Reason == AVAudioSessionRouteChangeReason.OldDeviceUnavailable)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => App.Host?.Media.RaisePause());
        });
    }

    public static void Activate()
    {
        Setup();
        try
        {
            var error = AVAudioSession.SharedInstance().SetActive(true);
            if (error != null) App.Log(new InvalidOperationException("AVAudioSession: " + error.LocalizedDescription));
        }
        catch (Exception ex) { App.Log(ex); }
    }
}
