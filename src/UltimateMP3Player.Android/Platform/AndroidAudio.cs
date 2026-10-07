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
// buffer is full or the track is paused. The buffer holds latencyMs of sound while the app is on screen (a tap on pause
// or on the seek bar is heard quickly); while it's away (the screen off, another app in front) it grows to BackgroundMs:
// a stop of the app's code (the garbage collector, a phone slowed down with the screen off) no longer reaches the speaker,
// and the phone wakes up less often. A jump, another song or a pause throw away what's waiting (IOutputLatency), so they
// are heard at once all the same; what it holds is measured, so the position stays the one heard.
public sealed class TrackOut : IWavePlayer, IOutputLatency
{
    private const int BackgroundMs = 1000;
    private static readonly List<TrackOut> Live = new();
    private static bool _background;

    private readonly int _latencyMs;
    private readonly ManualResetEventSlim _go = new(false);
    private readonly object _sync = new();
    private IWaveProvider? _source;
    private AudioTrack? _track;
    private Thread? _thread;
    private volatile bool _closed;
    private volatile bool _paused = true;
    private int _rate = 48000;
    // Frames written since the last flush (the head of the track counts from there too) and how many flushes so far: a
    // write that a flush overtook doesn't count.
    private uint _written;
    private int _flushes;

    public TrackOut(int latencyMs) => _latencyMs = latencyMs;

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;
    public float Volume { get; set; } = 1f;
    public WaveFormat OutputWaveFormat => _source?.WaveFormat ?? WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    // The audio focus asks to talk over something (a navigation voice): quieter for a while.
    public static float Duck { get; set; } = 1f;

    // The app is away (AppHost.SetVisible): every output keeps more sound ready.
    public static bool Background
    {
        get => _background;
        set
        {
            lock (Live)
            {
                if (_background == value) return;
                _background = value;
                foreach (var o in Live) o.ApplySize();
            }
        }
    }

    public void Init(IWaveProvider waveProvider)
    {
        var wf = waveProvider.WaveFormat;
        if (wf.Encoding != WaveFormatEncoding.IeeeFloat || wf.BitsPerSample != 32 || wf.Channels != 2)
            throw new ArgumentException("TrackOut plays 32-bit float stereo");
        _source = waveProvider;
        _rate = wf.SampleRate;
        int min = AudioTrack.GetMinBufferSize(wf.SampleRate, ChannelOut.Stereo, Encoding.PcmFloat);
        // Room for the big buffer; how much of it is used is set by ApplySize.
        int room = wf.AverageBytesPerSecond / 1000 * Math.Max(_latencyMs, BackgroundMs);
        var attrs = new AudioAttributes.Builder()!.SetUsage(AudioUsageKind.Media)!.SetContentType(AudioContentType.Music)!.Build()!;
        var format = new AudioFormat.Builder()!.SetEncoding(Encoding.PcmFloat)!.SetSampleRate(wf.SampleRate)!.SetChannelMask(ChannelOut.Stereo)!.Build()!;
        _track = new AudioTrack.Builder()
            .SetAudioAttributes(attrs)
            .SetAudioFormat(format)
            .SetBufferSizeInBytes(Math.Max(min, room))
            .SetTransferMode(AudioTrackMode.Stream)
            .Build();
        if (_track.State != AudioTrackState.Initialized) throw new InvalidOperationException("AudioTrack");
        lock (Live)
        {
            Live.Add(this);
            ApplySize();
        }
        _thread = new Thread(Run) { IsBackground = true, Name = "Audio out", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    // How much of the track's buffer the writer fills (the rest stays empty): little on screen, a lot while away.
    private void ApplySize()
    {
        try { _track?.SetBufferSizeInFrames((int)((long)_rate * (_background ? BackgroundMs : _latencyMs) / 1000)); }
        catch (Exception ex) { App.Log(ex); }
    }

    private void Run()
    {
        try { Android.OS.Process.SetThreadPriority(Android.OS.ThreadPriority.UrgentAudio); } catch { }
        var src = _source!;
        int frame = src.WaveFormat.BlockAlign;
        // 20 ms at a time on screen: the mix still answers quickly to a pause or a seek, and the phone is woken half as
        // often as with 10 (each write is a trip into Java with a copy of the samples). Away, 100 ms: 10 wake-ups a second.
        int small = Math.Max(frame, src.WaveFormat.AverageBytesPerSecond / 50 / frame * frame);
        int big = Math.Max(frame, src.WaveFormat.AverageBytesPerSecond / 10 / frame * frame);
        var bytes = new byte[big];
        float[]? floats = null;
        // The samples reach Android through one buffer outside .NET's memory, filled in place: no array made in Java and
        // copied there and back at every write (50 times a second, for as long as the music plays).
        Java.Nio.ByteBuffer? direct = null;
        IntPtr address = IntPtr.Zero;
        try
        {
            direct = Java.Nio.ByteBuffer.AllocateDirect(big);
            direct?.Order(Java.Nio.ByteOrder.NativeOrder()!);
            if (direct != null) address = Android.Runtime.JNIEnv.GetDirectBufferAddress(direct.Handle);
        }
        catch { address = IntPtr.Zero; }
        if (address == IntPtr.Zero) floats = new float[big / 4];
        while (!_closed)
        {
            // Paused: asleep until Play (or closing) wakes it; the long timeout only in case a wake-up got lost.
            if (_paused)
            {
                _go.Wait(5000);
                continue;
            }
            int chunk = _background ? big : small;
            int got;
            try { got = src.Read(bytes, 0, chunk); }
            catch { got = 0; }
            if (got < chunk) Array.Clear(bytes, got, chunk - got);
            var f = MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, chunk));
            float gain = Volume * Duck;
            if (gain < 0.999f)
                for (int i = 0; i < f.Length; i++) f[i] *= gain;
            try
            {
                var t = _track;
                if (t == null || _closed) break;
                int flushes = Volatile.Read(ref _flushes);
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
                    written = t.Write(floats!, 0, f.Length, WriteMode.Blocking) * 4;
                }
                if (written < 0) throw new InvalidOperationException("AudioTrack " + written);
                lock (_sync)
                    if (flushes == _flushes) _written += (uint)(written / frame);
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

    // Written and not played yet.
    public int LatencyMs
    {
        get
        {
            lock (_sync)
            {
                var t = _track;
                if (t == null || _closed) return 0;
                int frames;
                try { frames = (int)(_written - (uint)t.PlaybackHeadPosition); }
                catch { return 0; }
                return frames <= 0 ? 0 : (int)Math.Min(5000, (long)frames * 1000 / _rate);
            }
        }
    }

    public void Drop()
    {
        lock (_sync)
        {
            var t = _track;
            if (t == null || _closed || _paused) return;
            try
            {
                // (a paused track can be emptied; the writer's Write returns early and its sound is dropped too)
                t.Pause();
                t.Flush();
                _written = 0;
                _flushes++;
                t.Play();
            }
            catch (Exception ex) { App.Log(ex); }
        }
    }

    public int Flush()
    {
        lock (_sync)
        {
            var t = _track;
            if (t == null || _closed || !_paused) return 0;
            int ms;
            try { ms = (int)Math.Max(0, (long)(int)(_written - (uint)t.PlaybackHeadPosition) * 1000 / _rate); }
            catch { ms = 0; }
            try { t.Flush(); }
            catch { return 0; }
            _written = 0;
            _flushes++;
            return ms;
        }
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
        lock (_sync)
        {
            try { _track?.Flush(); } catch { }
            _written = 0;
            _flushes++;
        }
        PlaybackState = PlaybackState.Stopped;
        PlaybackStopped?.Invoke(this, new StoppedEventArgs());
    }

    public void Dispose()
    {
        if (_closed) return;
        lock (Live) Live.Remove(this);
        _closed = true;
        _go.Set();
        AudioTrack? t;
        lock (_sync)
        {
            t = _track;
            _track = null;
        }
        if (t == null) return;
        try { t.Pause(); t.Flush(); } catch { }
        // The writer thread may be inside Write: released once it's out.
        _thread?.Join(500);
        try { t.Release(); } catch { }
        t.Dispose();
        PlaybackState = PlaybackState.Stopped;
    }
}
