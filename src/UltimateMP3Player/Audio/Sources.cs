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
