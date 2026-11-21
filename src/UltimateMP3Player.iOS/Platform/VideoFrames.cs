using AVFoundation;
using CoreMedia;
using CoreVideo;
using Foundation;
using Network;
using SkiaSharp;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Platform;

// The music videos on the iPhone (no ffmpeg): their size and their pictures through AVFoundation, given to the song's page
// (VideoView) the way ffmpeg's output is elsewhere.
public sealed partial class MediaTools
{
    public (int W, int H, double Fps)? ProbeVideo(string file)
    {
        try
        {
            using var asset = AVAsset.FromUrl(NSUrl.FromFilename(file));
            var tracks = asset.TracksWithMediaType(AVMediaTypes.Video.GetConstant()!);
            if (tracks.Length == 0) return null;
            var size = tracks[0].NaturalSize;
            double fps = tracks[0].NominalFrameRate > 0 ? tracks[0].NominalFrameRate : 25;
            return size.Width > 0 && size.Height > 0 ? ((int)size.Width, (int)size.Height, fps) : null;
        }
        catch { return null; }
    }

    public Stream? DecodeVideo(string file, double at, int w, int h, double fps)
    {
        try { return new VideoFrames(file, at, w, h, fps); }
        catch { return null; }
    }

    // A video's pictures from a time on as raw BGRA frames of w×h, one every 1/fps of a second (a picture repeated or skipped to
    // keep that pace, as ffmpeg's fps filter does), read like a stream.
    private sealed class VideoFrames : Stream
    {
        private readonly AVAsset _asset;
        private readonly AVAssetReader _reader;
        private readonly AVAssetReaderTrackOutput _output;
        private readonly int _w, _h;
        private readonly double _step;
        private double _next;
        // The picture showing and the one after it (decoded ahead), with their times.
        private byte[]? _shown, _ahead;
        private double _shownAt, _aheadAt;
        private bool _ended;
        private byte[]? _out;
        private int _outPos;

        public VideoFrames(string file, double at, int w, int h, double fps)
        {
            _w = w;
            _h = h;
            _step = 1 / fps;
            _next = at;
            _asset = AVAsset.FromUrl(NSUrl.FromFilename(file));
            var track = _asset.TracksWithMediaType(AVMediaTypes.Video.GetConstant()!).FirstOrDefault()
                        ?? throw new InvalidOperationException("No video in " + file);
            _reader = new AVAssetReader(_asset, out var error);
            if (error != null) throw new InvalidOperationException(error.LocalizedDescription);
            var settings = new CVPixelBufferAttributes { PixelFormatType = CVPixelFormatType.CV32BGRA };
            _output = new AVAssetReaderTrackOutput(track, settings.Dictionary) { AlwaysCopiesSampleData = false };
            _reader.AddOutput(_output);
            _reader.TimeRange = new CMTimeRange { Start = CMTime.FromSeconds(Math.Max(0, at), 600), Duration = CMTime.PositiveInfinity };
            if (!_reader.StartReading()) throw new InvalidOperationException(_reader.Error?.LocalizedDescription ?? "Can't read " + file);
        }

        // The picture showing at the next frame's time; null once the video is over.
        private byte[]? NextFrame()
        {
            while (!_ended && (_ahead == null || _aheadAt <= _next))
            {
                if (_ahead != null)
                {
                    _shown = _ahead;
                    _shownAt = _aheadAt;
                    _ahead = null;
                }
                using var sample = _output.CopyNextSampleBuffer();
                if (sample == null)
                {
                    _ended = true;
                    break;
                }
                using var image = sample.GetImageBuffer() as CVPixelBuffer;
                if (image == null) continue;
                _ahead = Scale(image);
                _aheadAt = sample.PresentationTimeStamp.Seconds;
            }
            // (the first picture, a little after the time asked for; the last ones, at the end)
            if (_ahead != null && (_shown == null || _ended && _aheadAt <= _next))
            {
                _shown = _ahead;
                _shownAt = _aheadAt;
                _ahead = null;
            }
            if (_shown == null || _ended && _ahead == null && _next > _shownAt + 2 * _step) return null;
            _next += _step;
            return _shown;
        }

        private unsafe byte[] Scale(CVPixelBuffer image)
        {
            var frame = new byte[_w * _h * 4];
            image.Lock(CVPixelBufferLock.ReadOnly);
            try
            {
                using var src = new SKPixmap(new SKImageInfo((int)image.Width, (int)image.Height, SKColorType.Bgra8888, SKAlphaType.Premul),
                    image.BaseAddress, (int)image.BytesPerRow);
                fixed (byte* p = frame)
                {
                    using var dst = new SKPixmap(new SKImageInfo(_w, _h, SKColorType.Bgra8888, SKAlphaType.Premul), (IntPtr)p, _w * 4);
#pragma warning disable CS0618
                    src.ScalePixels(dst, SKFilterQuality.Low);
#pragma warning restore CS0618
                }
            }
            finally { image.Unlock(CVPixelBufferLock.ReadOnly); }
            return frame;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_out == null || _outPos >= _out.Length)
            {
                _out = NextFrame();
                _outPos = 0;
                if (_out == null) return 0;
            }
            int n = Math.Min(count, _out.Length - _outPos);
            Buffer.BlockCopy(_out, _outPos, buffer, offset, n);
            _outPos += n;
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _reader.CancelReading(); } catch { }
                _output.Dispose();
                _reader.Dispose();
                _asset.Dispose();
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

// Whether the connection now costs (mobile data, a hotspot, Low Data Mode): the music videos wait for Wi-Fi when asked
// (NetworkCost, Settings › Music videos).
public static class NetworkWatch
{
    private static NWPathMonitor? _monitor;
    private static volatile bool _costly;

    public static void Start()
    {
        try
        {
            _monitor = new NWPathMonitor();
            _monitor.SnapshotHandler = path => _costly = path.IsExpensive || path.IsConstrained;
            _monitor.SetQueue(CoreFoundation.DispatchQueue.DefaultGlobalQueue);
            _monitor.Start();
            NetworkCost.Metered = () => _costly;
        }
        catch { }
    }
}
