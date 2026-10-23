using System.Diagnostics;
using System.IO;
using NAudio.Wave;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Audio;

// A decoded song: stereo float at its own rate.
public interface ITrackSource : IDisposable
{
    ISampleProvider Samples { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    void Seek(TimeSpan t);
}

// ffmpeg PCM stream for what the platform can't decode itself (Opus, Ogg, WebM, MKV...).
public sealed class FfmpegTrackSource : ITrackSource, ISampleProvider
{
    private const int Rate = 48000;
    private readonly string _path;
    private Process? _proc;
    private Stream? _out;
    private TimeSpan _start;
    private long _frames;
    private byte[] _bytes = Array.Empty<byte>();
    private int _leftover;

    public FfmpegTrackSource(string path, TimeSpan duration)
    {
        _path = path;
        Duration = duration;
        Start(TimeSpan.Zero);
    }

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
    public ISampleProvider Samples => this;
    public TimeSpan Duration { get; }
    public TimeSpan Position => _start + TimeSpan.FromSeconds(_frames / (double)Rate);

    private void Start(TimeSpan at)
    {
        Kill();
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
        _proc = Process.Start(psi);
        _out = _proc?.StandardOutput.BaseStream;
        _start = at;
        _frames = 0;
        _leftover = 0;
    }

    public int Read(float[] buffer, int offset, int count)
    {
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

    public void Seek(TimeSpan t) => Start(t < TimeSpan.Zero ? TimeSpan.Zero : t);

    private void Kill()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(); } catch { }
        _proc?.Dispose();
        _proc = null;
        _out = null;
    }

    public void Dispose() => Kill();
}

// A song still downloading, heard meanwhile from its site (ffmpeg reads the link, SongStream). The audio thread never
// waits for the network: what has come is played, silence while more is on its way (the position stays put), and a jump
// starts ffmpeg again there. The link refused, or cut off before the end: refused is called (the song waits for its file).
public sealed class LinkTrackSource : ITrackSource, ISampleProvider
{
    private const int Rate = 48000, FrameBytes = 8;
    // About ten seconds decoded ahead at most: beyond that ffmpeg (and its download) waits.
    private const int Capacity = Rate * FrameBytes * 10;
    private readonly SongStream _link;
    private readonly Action _refused;
    private readonly object _lock = new();
    private readonly byte[] _ring = new byte[Capacity];
    private int _head, _count;
    // Which ffmpeg fills the ring (a jump starts another one): the old one's sound is thrown away.
    private int _run;
    private ChildProcess? _proc;
    private bool _eof, _told, _disposed;
    private TimeSpan _start;
    private long _frames;

    public LinkTrackSource(SongStream link, TimeSpan duration, Action refused)
    {
        _link = link;
        _refused = refused;
        Duration = duration;
        Start(TimeSpan.Zero);
    }

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
    public ISampleProvider Samples => this;
    public TimeSpan Duration { get; }

    public TimeSpan Position
    {
        get
        {
            lock (_lock) return _start + TimeSpan.FromSeconds(_frames / (double)Rate);
        }
    }

    private void Start(TimeSpan at)
    {
        int run;
        ChildProcess? old;
        lock (_lock)
        {
            run = ++_run;
            old = _proc;
            _proc = null;
            _head = _count = 0;
            _eof = _told = false;
            _start = at;
            _frames = 0;
            Monitor.PulseAll(_lock);
        }
        _ = Task.Run(() =>
        {
            Stop(old);
            Pump(run, at);
        });
    }

    private void Pump(int run, TimeSpan at)
    {
        ChildProcess p;
        try
        {
            p = ChildProcess.Start(Engines.Ffmpeg, new[] { "-nostdin", "-loglevel", "quiet", "-headers", _link.Headers, "-reconnect", "1",
                "-reconnect_streamed", "1", "-reconnect_delay_max", "4", "-ss", at.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "-i", _link.Url, "-vn", "-f", "f32le", "-ac", "2", "-ar", Rate.ToString(), "pipe:1" }, errors: false);
        }
        catch
        {
            lock (_lock)
                if (run == _run) _eof = true;
            return;
        }
        lock (_lock)
        {
            if (run != _run || _disposed)
            {
                Stop(p);
                return;
            }
            _proc = p;
        }
        var buf = new byte[1 << 16];
        try
        {
            int n;
            while ((n = p.StandardOutput.Read(buf, 0, buf.Length)) > 0)
            {
                lock (_lock)
                {
                    for (int off = 0; off < n;)
                    {
                        while (_count == Capacity && run == _run && !_disposed) Monitor.Wait(_lock);
                        if (run != _run || _disposed) return;
                        int tail = (_head + _count) % Capacity;
                        int k = Math.Min(n - off, Math.Min(Capacity - _count, Capacity - tail));
                        Buffer.BlockCopy(buf, off, _ring, tail, k);
                        _count += k;
                        off += k;
                    }
                }
            }
        }
        catch { }
        lock (_lock)
            if (run == _run) _eof = true;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        bool refused = false;
        lock (_lock)
        {
            int take = Math.Min(count * 4, _count) / FrameBytes * FrameBytes;
            for (int done = 0; done < take;)
            {
                int k = Math.Min(take - done, Capacity - _head);
                Buffer.BlockCopy(_ring, _head, buffer, offset * 4 + done, k);
                _head = (_head + k) % Capacity;
                _count -= k;
                done += k;
            }
            _frames += take / FrameBytes;
            if (take > 0) Monitor.PulseAll(_lock);
            if (_eof && _count < FrameBytes && take < count * 4)
            {
                // The end of the song (as long as it was said to be, give or take): what's left is all there is.
                var at = _start + TimeSpan.FromSeconds(_frames / (double)Rate);
                if (Duration <= TimeSpan.Zero || at >= Duration - TimeSpan.FromSeconds(3)) return take / 4;
                refused = !_told;
                _told = true;
            }
            // (element by element: NAudio's buffer may be a byte[] seen as float[])
            for (int i = offset + take / 4; i < offset + count; i++) buffer[i] = 0;
        }
        if (refused) ThreadPool.QueueUserWorkItem(_ => _refused());
        return count;
    }

    public void Seek(TimeSpan t) => Start(t < TimeSpan.Zero ? TimeSpan.Zero : t);

    private static void Stop(ChildProcess? p)
    {
        if (p == null) return;
        try { p.Kill(); } catch { }
        try { p.Dispose(); } catch { }
    }

    public void Dispose()
    {
        ChildProcess? p;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _run++;
            p = _proc;
            _proc = null;
            Monitor.PulseAll(_lock);
        }
        _ = Task.Run(() => Stop(p));
    }
}
