using System.Diagnostics;
using System.IO;
using System.Net.Http;
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

// An internet radio's MP3 stream, decoded here (NLayer) on every system (the iPhone has no ffmpeg): the song on air comes with
// it (ICY metadata, taken out of the sound) and is told to onAir. Live: no end, no seeking; a lost connection is made again
// (meanwhile silence). Another format (AAC, Ogg, HLS), or a server it can't talk to, throws at once: ffmpeg takes it where
// there is one (AudioEngine.OpenLiveAsync).
public sealed class LiveMp3Source : ITrackSource, ISampleProvider
{
    private const int BufferSeconds = 8;
    private readonly SongStream _link;
    private readonly Action<string?> _onAir;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly float[] _ring;
    private readonly int _rate;
    private int _head, _count;
    private long _frames;
    private volatile bool _disposed;
    private Stream? _stream;
    private string? _title;

    public LiveMp3Source(SongStream link, Action<string?> onAir)
    {
        _link = link;
        _onAir = onAir;
        var (response, stream, frame) = Connect();
        _rate = frame.SampleRate;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(_rate, 2);
        _ring = new float[_rate * 2 * BufferSeconds];
        _ = Task.Run(() => Pump(response, stream, frame));
    }

    public WaveFormat WaveFormat { get; }
    public ISampleProvider Samples => this;
    public TimeSpan Duration => TimeSpan.Zero;

    public TimeSpan Position
    {
        get
        {
            lock (_lock) return TimeSpan.FromSeconds(_frames / (double)_rate);
        }
    }

    public void Seek(TimeSpan t) { }

    // The stream asked for with its metadata, and its first frame (the format). 15 s to answer.
    private (HttpResponseMessage, Stream, Mp3Frame) Connect()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var req = new HttpRequestMessage(HttpMethod.Get, _link.Url);
        req.Headers.TryAddWithoutValidation("User-Agent", "UltimateMP3Player/4.3");
        req.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
        var response = Http.Client.Send(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        try
        {
            response.EnsureSuccessStatusCode();
            var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (type != null && !(type.Contains("mpeg") && !type.Contains("mpegurl")) && !type.Contains("mp3"))
                throw new InvalidDataException(L.T("The station doesn't send MP3"));
            int metaint = response.Headers.TryGetValues("icy-metaint", out var v) && int.TryParse(v.FirstOrDefault(), out var m) ? m : 0;
            // (a frame within the first 64 KB, or it isn't MP3)
            var stream = new IcyStream(response.Content.ReadAsStream(timeout.Token), metaint, OnMeta) { Limit = 1 << 16 };
            _stream = stream;
            using (timeout.Token.Register(stream.Dispose))
            {
                var frame = Mp3Frame.LoadFromStream(stream) ?? throw new InvalidDataException(L.T("The station doesn't send MP3"));
                stream.Limit = 0;
                return (response, stream, frame);
            }
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private void Pump(HttpResponseMessage? response, Stream? stream, Mp3Frame? frame)
    {
        var pcm = new byte[1152 * 2 * 4 * 2];
        int failures = 0;
        while (!_disposed)
        {
            if (stream != null)
            {
                IMp3FrameDecompressor? decoder = null;
                try
                {
                    while (!_disposed && (frame ??= Mp3Frame.LoadFromStream(stream)) != null)
                    {
                        decoder ??= new NLayer.NAudioSupport.Mp3FrameDecompressor(
                            new Mp3WaveFormat(frame.SampleRate, frame.ChannelMode == ChannelMode.Mono ? 1 : 2, frame.FrameLength, frame.BitRate));
                        int n;
                        // (a damaged frame is only skipped)
                        try { n = decoder.DecompressFrame(frame, pcm, 0); }
                        catch (Exception) when (!_disposed) { n = 0; }
                        frame = null;
                        failures = 0;
                        if (n > 0) Write(pcm, n, decoder.OutputFormat);
                    }
                }
                catch (Exception) when (!_disposed) { }
                finally
                {
                    decoder?.Dispose();
                    stream.Dispose();
                    response?.Dispose();
                }
            }
            if (_disposed) return;
            // Cut off (or not answering): again, a little later each time.
            // shortcut: a station coming back at another sample rate plays at the first one's; reopen it if that turns up.
            _stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Min(10, 1 << Math.Min(failures++, 4))));
            try { (response, stream, frame) = Connect(); }
            catch (Exception) when (!_disposed) { response = null; stream = null; frame = null; }
        }
    }

    // Decoded sound (16-bit or float, mono or stereo) into the ring as stereo float; waits while it's full.
    private void Write(byte[] pcm, int bytes, WaveFormat format)
    {
        bool floats = format.Encoding == WaveFormatEncoding.IeeeFloat;
        int width = floats ? 4 : 2, channels = format.Channels, frames = bytes / width / channels;
        lock (_lock)
        {
            for (int f = 0; f < frames; f++)
            {
                while (_count + 2 > _ring.Length && !_disposed) Monitor.Wait(_lock);
                if (_disposed) return;
                for (int c = 0; c < 2; c++)
                {
                    int i = (f * channels + Math.Min(c, channels - 1)) * width;
                    float s = floats ? BitConverter.ToSingle(pcm, i) : BitConverter.ToInt16(pcm, i) / 32768f;
                    _ring[(_head + _count) % _ring.Length] = s;
                    _count++;
                }
            }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            int take = Math.Min(count, _count) / 2 * 2;
            for (int i = 0; i < take; i++)
            {
                buffer[offset + i] = _ring[_head];
                _head = (_head + 1) % _ring.Length;
            }
            _count -= take;
            _frames += take / 2;
            if (take > 0) Monitor.PulseAll(_lock);
            for (int i = offset + take; i < offset + count; i++) buffer[i] = 0;
        }
        return count;
    }

    // "StreamTitle='Artist - Title';StreamUrl='...';"
    private void OnMeta(string meta)
    {
        int at = meta.IndexOf("StreamTitle='", StringComparison.Ordinal);
        if (at < 0) return;
        at += 13;
        int end = meta.IndexOf("';", at, StringComparison.Ordinal);
        string? title = (end < 0 ? meta[at..].TrimEnd('\'') : meta[at..end]).Trim();
        if (title.Length == 0 || title == "-") title = null;
        if (title == _title) return;
        _title = title;
        _onAir(title);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            Monitor.PulseAll(_lock);
        }
        _stop.Cancel();
        // (a read waiting for the network ends at once)
        try { _stream?.Dispose(); } catch { }
    }

    // The response without its ICY metadata (every metaint bytes of sound: a length byte, x16, and that much text). A read
    // fills what's asked unless the stream ends (NAudio's frame reader counts on it); past Limit bytes it ends.
    private sealed class IcyStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _metaint;
        private readonly Action<string> _meta;
        private int _untilMeta;
        private long _read;
        public long Limit;

        public IcyStream(Stream inner, int metaint, Action<string> meta)
        {
            _inner = inner;
            _metaint = metaint;
            _untilMeta = metaint;
            _meta = meta;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int done = 0;
            while (done < count && (Limit <= 0 || _read < Limit))
            {
                if (_metaint > 0 && _untilMeta == 0)
                {
                    if (!ReadMeta()) break;
                    _untilMeta = _metaint;
                }
                int want = _metaint > 0 ? Math.Min(count - done, _untilMeta) : count - done;
                int n = _inner.Read(buffer, offset + done, want);
                if (n <= 0) break;
                done += n;
                _read += n;
                if (_metaint > 0) _untilMeta -= n;
            }
            return done;
        }

        private bool ReadMeta()
        {
            int length = _inner.ReadByte();
            if (length < 0) return false;
            if (length == 0) return true;
            var bytes = new byte[length * 16];
            for (int got = 0; got < bytes.Length;)
            {
                int n = _inner.Read(bytes, got, bytes.Length - got);
                if (n <= 0) return false;
                got += n;
            }
            // (UTF-8 mostly; the older servers send Latin-1)
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            if (text.Contains((char)0xFFFD)) text = System.Text.Encoding.Latin1.GetString(bytes);
            _meta(text.TrimEnd('\0'));
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
