using System.Runtime.InteropServices;
using Android.Content;
using Android.Media;
using NAudio.Wave;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Audio;

// What the audio engine needs from the system, on Android: the output through an AudioTrack (Android moves it to the
// headphones, the Bluetooth speaker or the car by itself) and the decoders of Decoders.cs (MP3 in managed code, WAV
// directly, everything else through ffmpeg).
public static class AudioPlatform
{
    public static int OutputRate()
    {
        try
        {
            var am = (AudioManager?)Android.App.Application.Context.GetSystemService(Context.AudioService);
            if (int.TryParse(am?.GetProperty(AudioManager.PropertyOutputSampleRate), out var rate) && rate is >= 8000 and <= 192000) return rate;
        }
        catch { }
        return 48000;
    }

    public static IWavePlayer CreateOutput(int latencyMs) => new TrackOut(latencyMs);

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

    // AudioTrack follows the system's routing by itself.
    public static IDisposable? WatchDefaultDevice(Action changed) => null;
}

// A NAudio wave player on an AudioTrack: a thread of its own reads the mix and writes it, blocking while the track's
// buffer (about latencyMs, what AudioEngine.LatencyMs assumes the output holds) is full or the track is paused.
public sealed class TrackOut : IWavePlayer
{
    private readonly int _latencyMs;
    private readonly ManualResetEventSlim _go = new(false);
    private IWaveProvider? _source;
    private AudioTrack? _track;
    private Thread? _thread;
    private volatile bool _closed;
    private volatile bool _paused = true;

    public TrackOut(int latencyMs) => _latencyMs = latencyMs;

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;
    public float Volume { get; set; } = 1f;
    public WaveFormat OutputWaveFormat => _source?.WaveFormat ?? WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    // The audio focus asks to talk over something (a navigation voice): quieter for a while.
    public static float Duck { get; set; } = 1f;

    public void Init(IWaveProvider waveProvider)
    {
        var wf = waveProvider.WaveFormat;
        if (wf.Encoding != WaveFormatEncoding.IeeeFloat || wf.BitsPerSample != 32 || wf.Channels != 2)
            throw new ArgumentException("TrackOut plays 32-bit float stereo");
        _source = waveProvider;
        int min = AudioTrack.GetMinBufferSize(wf.SampleRate, ChannelOut.Stereo, Encoding.PcmFloat);
        int wanted = wf.AverageBytesPerSecond / 1000 * _latencyMs;
        var attrs = new AudioAttributes.Builder()!.SetUsage(AudioUsageKind.Media)!.SetContentType(AudioContentType.Music)!.Build()!;
        var format = new AudioFormat.Builder()!.SetEncoding(Encoding.PcmFloat)!.SetSampleRate(wf.SampleRate)!.SetChannelMask(ChannelOut.Stereo)!.Build()!;
        _track = new AudioTrack.Builder()
            .SetAudioAttributes(attrs)
            .SetAudioFormat(format)
            .SetBufferSizeInBytes(Math.Max(min, wanted))
            .SetTransferMode(AudioTrackMode.Stream)
            .Build();
        if (_track.State != AudioTrackState.Initialized) throw new InvalidOperationException("AudioTrack");
        _thread = new Thread(Run) { IsBackground = true, Name = "Audio out", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    private void Run()
    {
        try { Android.OS.Process.SetThreadPriority(Android.OS.ThreadPriority.UrgentAudio); } catch { }
        var src = _source!;
        int frame = src.WaveFormat.BlockAlign;
        // 20 ms at a time: the mix still answers quickly to a pause or a seek, and the phone is woken half as often as with
        // 10 (each write is a trip into Java with a copy of the samples).
        int chunk = Math.Max(frame, src.WaveFormat.AverageBytesPerSecond / 50 / frame * frame);
        var bytes = new byte[chunk];
        float[]? floats = null;
        // The samples reach Android through one buffer outside .NET's memory, filled in place: no array made in Java and
        // copied there and back at every write (50 times a second, for as long as the music plays).
        Java.Nio.ByteBuffer? direct = null;
        IntPtr address = IntPtr.Zero;
        try
        {
            direct = Java.Nio.ByteBuffer.AllocateDirect(chunk);
            direct?.Order(Java.Nio.ByteOrder.NativeOrder()!);
            if (direct != null) address = Android.Runtime.JNIEnv.GetDirectBufferAddress(direct.Handle);
        }
        catch { address = IntPtr.Zero; }
        if (address == IntPtr.Zero) floats = new float[chunk / 4];
        while (!_closed)
        {
            // Paused: asleep until Play (or closing) wakes it; the long timeout only in case a wake-up got lost.
            if (_paused)
            {
                _go.Wait(5000);
                continue;
            }
            int got;
            try { got = src.Read(bytes, 0, chunk); }
            catch { got = 0; }
            if (got < chunk) Array.Clear(bytes, got, chunk - got);
            var f = MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
            float gain = Volume * Duck;
            if (gain < 0.999f)
                for (int i = 0; i < f.Length; i++) f[i] *= gain;
            try
            {
                var t = _track;
                if (t == null || _closed) break;
                int written;
                if (direct != null && address != IntPtr.Zero)
                {
                    Marshal.Copy(bytes, 0, address, chunk);
                    direct.Clear();
                    written = t.Write(direct, chunk, WriteMode.Blocking);
                }
                else
                {
                    f.CopyTo(floats!);
                    written = t.Write(floats!, 0, floats!.Length, WriteMode.Blocking);
                }
                if (written < 0) throw new InvalidOperationException("AudioTrack " + written);
            }
            catch (Exception ex)
            {
                if (_closed) break;
                PlaybackStopped?.Invoke(this, new StoppedEventArgs(ex));
                break;
            }
        }
        try { direct?.Dispose(); } catch { }
    }

    public void Play()
    {
        if (_track == null || _closed) return;
        _track.Play();
        _paused = false;
        _go.Set();
        PlaybackState = PlaybackState.Playing;
    }

    public void Pause()
    {
        if (_track == null || _closed) return;
        _paused = true;
        _go.Reset();
        try { _track.Pause(); } catch { }
        PlaybackState = PlaybackState.Paused;
    }

    public void Stop()
    {
        if (_track == null) return;
        Pause();
        try { _track.Flush(); } catch { }
        PlaybackState = PlaybackState.Stopped;
        PlaybackStopped?.Invoke(this, new StoppedEventArgs());
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _go.Set();
        var t = _track;
        _track = null;
        if (t == null) return;
        try { t.Pause(); t.Flush(); } catch { }
        // The writer thread may be inside Write: released once it's out.
        _thread?.Join(500);
        try { t.Release(); } catch { }
        t.Dispose();
        PlaybackState = PlaybackState.Stopped;
    }
}
