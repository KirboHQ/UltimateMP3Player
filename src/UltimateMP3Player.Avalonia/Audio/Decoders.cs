using System.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Audio;

// The decoders of the Linux, macOS and Android apps (AudioPlatform.Open): MP3 in managed code, WAV directly, everything
// else through ffmpeg.

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
