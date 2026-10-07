using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Views;

// The song's video, muted, following the audio (the Windows app's MediaElement): ffmpeg (the one the downloads use)
// decodes it into frames, and every frame on screen is the one at the audio's position, at any speed. A seek starts the
// decoding again from there.
public sealed class VideoView : Control
{
    private const int Ahead = 6;

    // The frames' size and rate at most (the phone decodes smaller ones: its processor and its battery).
    public static int FrameMaxWidth { get; set; } = 1280;
    public static int FrameMaxHeight { get; set; } = 720;
    public static int FrameMaxFps { get; set; } = 30;

    private sealed record Frame(double Time, byte[] Pixels);

    private readonly object _lock = new();
    private string? _path;
    private int _width, _height;
    private double _fps = 25;
    private WriteableBitmap? _bitmap;
    private Decoder? _decoder;
    private Frame? _next;
    private double _shownTime = double.NaN;
    private bool _ticking;
    private readonly ConcurrentBag<byte[]> _pool = new();

    // The audio's position, read every frame.
    public Func<TimeSpan>? Clock { get; set; }

    public int NaturalVideoWidth { get; private set; }
    public int NaturalVideoHeight { get; private set; }

    // The picture, for the copies around it (the blurred backdrop); FrameChanged when a new frame is in.
    public IImage? Picture => _bitmap;
    public event Action? FrameChanged;
    public event Action? MediaOpened;

    public string? Source
    {
        get => _path;
        set
        {
            if (value == _path) return;
            Close();
            _path = value;
            if (value != null) _ = OpenAsync(value);
        }
    }

    private async Task OpenAsync(string path)
    {
        var info = await Task.Run(() => Probe(path));
        if (_path != path) return;
        if (info == null) return;
        (NaturalVideoWidth, NaturalVideoHeight, double fps) = info.Value;
        double scale = Math.Min(1, Math.Min((double)FrameMaxWidth / NaturalVideoWidth, (double)FrameMaxHeight / NaturalVideoHeight));
        _width = Math.Max(2, (int)Math.Round(NaturalVideoWidth * scale / 2) * 2);
        _height = Math.Max(2, (int)Math.Round(NaturalVideoHeight * scale / 2) * 2);
        _fps = Math.Clamp(fps, 1, FrameMaxFps);
        _bitmap = new WriteableBitmap(new PixelSize(_width, _height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        _shownTime = double.NaN;
        MediaOpened?.Invoke();
        StartTicking();
    }

    // Size and frame rate of the first video stream.
    private static (int W, int H, double Fps)? Probe(string path)
    {
        try
        {
            using var p = ChildProcess.Start(Engines.Ffprobe, new[]
            {
                "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,r_frame_rate", "-of", "default=nw=1", path,
            }, errors: false);
            var output = new StreamReader(p.StandardOutput).ReadToEnd();
            p.WaitForExitAsync().Wait(5000);
            int w = 0, h = 0;
            double fps = 25;
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var kv = line.Split('=', 2);
                if (kv.Length != 2) continue;
                switch (kv[0])
                {
                    case "width": int.TryParse(kv[1], out w); break;
                    case "height": int.TryParse(kv[1], out h); break;
                    case "r_frame_rate":
                        var parts = kv[1].Split('/');
                        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
                            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b > 0) fps = a / b;
                        break;
                }
            }
            return w > 0 && h > 0 ? (w, h, fps) : null;
        }
        catch { return null; }
    }

    public void Close()
    {
        _path = null;
        StopDecoder();
        _bitmap = null;
        _next = null;
        _shownTime = double.NaN;
        _ticking = false;
        InvalidateVisual();
        FrameChanged?.Invoke();
    }

    // ------------------------------------------------------------------ frames on screen

    private void StartTicking()
    {
        if (_ticking) return;
        _ticking = true;
        RequestFrame();
    }

    // Looked at twice per frame of the video (a 30 fps video on a 120 Hz screen: every other frame of the screen).
    private readonly FramePacer _pacer = new(15);

    private void RequestFrame()
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            _ticking = false;
            return;
        }
        _pacer.MinMs = Math.Max(4, 500 / _fps - 1);
        _pacer.Request(top, _ =>
        {
            if (!_ticking || _bitmap == null) return;
            // Hidden (the song's page closed): no frame asked for until it's shown again.
            if (!IsEffectivelyVisible)
            {
                _ticking = false;
                Ui.Later(() =>
                {
                    if (_bitmap != null && this.GetVisualRoot() != null) StartTicking();
                });
                return;
            }
            Step();
            RequestFrame();
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_bitmap != null) StartTicking();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _ticking = false;
    }

    // The audio's position moves in steps (the blocks the sound is written in, 10-20 ms): in between, the time since the
    // last step, so each frame comes out when it's due and not a screen refresh later now and then.
    private double _clockValue = double.NaN;
    private long _clockAt;

    private double SmoothClock(double raw)
    {
        long now = Stopwatch.GetTimestamp();
        if (raw != _clockValue)
        {
            _clockValue = raw;
            _clockAt = now;
            return raw;
        }
        return raw + Math.Min(0.05, (now - _clockAt) / (double)Stopwatch.Frequency);
    }

    private void Step()
    {
        if (_path == null || Clock == null) return;
        double target = SmoothClock(Math.Max(0, Clock().TotalSeconds));
        var d = _decoder;
        // Back in time, or far ahead of what's being decoded: decode again from there.
        double ahead = d == null ? double.NaN : (_next?.Time ?? d.Position);
        if (d == null || target < _shownTime - 0.3 || (!double.IsNaN(ahead) && target > ahead + 1.5 && !d.Finished))
        {
            Restart(target);
            return;
        }
        Frame? show = null;
        while (true)
        {
            _next ??= d.Take();
            if (_next == null || _next.Time > target + 0.5 / _fps) break;
            if (show != null) _pool.Add(show.Pixels);
            show = _next;
            _next = null;
        }
        if (show == null) return;
        Present(show);
        _pool.Add(show.Pixels);
    }

    private unsafe void Present(Frame f)
    {
        if (_bitmap == null) return;
        using (var fb = _bitmap.Lock())
        {
            int row = _width * 4;
            fixed (byte* src = f.Pixels)
                for (int y = 0; y < _height; y++)
                    Buffer.MemoryCopy(src + y * row, (byte*)fb.Address + y * fb.RowBytes, fb.RowBytes, row);
        }
        _shownTime = f.Time;
        InvalidateVisual();
        FrameChanged?.Invoke();
    }

    private void Restart(double at)
    {
        StopDecoder();
        _next = null;
        _shownTime = at;
        if (_path == null) return;
        _decoder = new Decoder(_path, at, _width, _height, _fps, _pool);
    }

    private void StopDecoder()
    {
        _decoder?.Dispose();
        _decoder = null;
    }

    public override void Render(DrawingContext context)
    {
        if (_bitmap != null && Bounds.Width > 0 && Bounds.Height > 0)
            context.DrawImage(_bitmap, new Rect(0, 0, _width, _height), new Rect(Bounds.Size));
    }

    // ------------------------------------------------------------------ ffmpeg

    private sealed class Decoder : IDisposable
    {
        private readonly ChildProcess? _process;
        private readonly BlockingCollection<Frame> _frames = new(Ahead);
        private readonly CancellationTokenSource _cts = new();
        private readonly double _start, _fps;
        private int _count;

        public Decoder(string path, double at, int w, int h, double fps, ConcurrentBag<byte[]> pool)
        {
            _start = at;
            _fps = fps;
            try
            {
                // (its few error lines go nowhere)
                _process = ChildProcess.Start(Engines.Ffmpeg, new[]
                {
                    "-nostdin", "-hide_banner", "-loglevel", "error",
                    "-ss", at.ToString("0.###", CultureInfo.InvariantCulture), "-i", path,
                    "-an", "-sn", "-dn",
                    "-vf", $"fps={fps.ToString("0.###", CultureInfo.InvariantCulture)},scale={w}:{h}:flags=bilinear",
                    "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1",
                }, errors: false);
            }
            catch { _process = null; }
            if (_process == null)
            {
                Finished = true;
                return;
            }
            var stream = _process.StandardOutput;
            int size = w * h * 4;
            var token = _cts.Token;
            new Thread(() =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        if (!pool.TryTake(out var buf) || buf.Length != size) buf = new byte[size];
                        int read = 0;
                        while (read < size)
                        {
                            int n = stream.Read(buf, read, size - read);
                            if (n <= 0) break;
                            read += n;
                        }
                        if (read < size) break;
                        var frame = new Frame(_start + _count / _fps, buf);
                        Interlocked.Increment(ref _count);
                        _frames.Add(frame, token);
                    }
                }
                catch { }
                finally { Finished = true; }
            }) { IsBackground = true, Name = "Video decoder" }.Start();
        }

        public volatile bool Finished;

        // The time of the next frame to come out.
        public double Position => _start + _count / _fps;

        public Frame? Take() => _frames.TryTake(out var f) ? f : null;

        public void Dispose()
        {
            _cts.Cancel();
            try { _process?.Kill(); }
            catch { }
            _process?.Dispose();
        }
    }
}
