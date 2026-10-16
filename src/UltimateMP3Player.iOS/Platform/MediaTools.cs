using AudioToolbox;
using AVFoundation;
using CoreGraphics;
using CoreMedia;
using Foundation;
using UIKit;
using UltimateMP3Player.Audio;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Platform;

// What ffmpeg and ffprobe do on the other systems, done by iOS itself: the tags and the cover of a song (AVFoundation), the
// waveform, the loudness (EBU R128, as ffmpeg's ebur128 filter measures it) and the peak from its decoded sound
// (AudioToolbox), a picture made into a cover; for the downloads, what's inside the file got, the original audio in a
// plain M4A with its tags, MP3s through LAME (Mp3).
public sealed class MediaTools : IMediaTools
{
    private const int CoverMax = 600;

    public Task<FileTags> ReadTagsAsync(string file, CancellationToken ct) => Task.Run(() =>
    {
        using var asset = AVAsset.FromUrl(NSUrl.FromFilename(file));
        if (asset.TracksWithMediaType(AVMediaTypes.Audio.GetConstant()!).Length == 0)
            throw new EngineException(L.T("The file has no audio."));
        bool video = asset.TracksWithMediaType(AVMediaTypes.Video.GetConstant()!).Length > 0;
        string? title = null, artist = null, album = null, year = null;
        bool cover = false;
        foreach (var item in asset.CommonMetadata)
        {
            var key = item.CommonKey;
            if (key == null) continue;
            if (key == AVMetadata.CommonKeyTitle) title ??= Clean(item.StringValue);
            else if (key == AVMetadata.CommonKeyArtist) artist ??= Clean(item.StringValue);
            else if (key == AVMetadata.CommonKeyAlbumName) album ??= Clean(item.StringValue);
            else if (key == AVMetadata.CommonKeyCreationDate) year ??= Clean(item.StringValue) is { Length: >= 4 } d ? d[..4] : null;
            else if (key == AVMetadata.CommonKeyArtwork) cover |= item.DataValue is { Length: > 0 };
        }
        double seconds = asset.Duration.Seconds;
        return new FileTags(title, artist, album, year, double.IsFinite(seconds) && seconds > 0 ? seconds : null, video, cover || video);
    }, ct);

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public Task<bool> ExtractCoverAsync(string file, string jpg, bool fromVideo, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            using var asset = AVAsset.FromUrl(NSUrl.FromFilename(file));
            UIImage? image = null;
            foreach (var item in asset.CommonMetadata)
                if (item.CommonKey == AVMetadata.CommonKeyArtwork && item.DataValue is { Length: > 0 } data)
                {
                    image = UIImage.LoadFromData(data);
                    if (image != null) break;
                }
            if (image == null && fromVideo)
            {
                // A frame of the video, 5 seconds in (or the first one).
                using var gen = new AVAssetImageGenerator(asset) { AppliesPreferredTrackTransform = true, MaximumSize = new CGSize(CoverMax, CoverMax) };
                double at = Math.Min(5, Math.Max(0, asset.Duration.Seconds / 2));
                using var frame = gen.CopyCGImageAtTime(CMTime.FromSeconds(at, 600), out _, out _);
                if (frame != null) image = UIImage.FromImage(frame);
            }
            return image != null && SaveJpeg(image, jpg, false, CoverMax);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return false;
        }
    }, ct);

    public Task<bool> MakeCoverFromImageAsync(string image, string jpg, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            using var picture = UIImage.FromFile(image);
            if (picture == null) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(jpg)!);
            var tmp = jpg + ".new.jpg";
            if (!SaveJpeg(picture, tmp, true, CoverMax)) return false;
            File.Move(tmp, jpg, true);
            return true;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return false;
        }
    }, ct);

    // At most max px (square: the middle of it), as a JPEG.
    private static bool SaveJpeg(UIImage image, string jpg, bool square, int max)
    {
        var size = image.Size;
        if (size.Width <= 0 || size.Height <= 0) return false;
        double side = Math.Min(size.Width, size.Height);
        double w = square ? side : size.Width, h = square ? side : size.Height;
        double scale = Math.Min(1, max / Math.Max(w, h));
        var target = new CGSize(Math.Max(1, Math.Round(w * scale)), Math.Max(1, Math.Round(h * scale)));
        var format = new UIGraphicsImageRendererFormat { Scale = 1, Opaque = true };
        using var renderer = new UIGraphicsImageRenderer(target, format);
        using var result = renderer.CreateImage(_ =>
        {
            // The picture drawn so that its middle fills the target (cropped when square).
            double s = Math.Max(target.Width / size.Width, target.Height / size.Height);
            double dw = size.Width * s, dh = size.Height * s;
            image.Draw(new CGRect((target.Width - dw) / 2, (target.Height - dh) / 2, dw, dh));
        });
        using var data = result.AsJPEG(0.85f);
        if (data == null) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(jpg)!);
        return data.Save(jpg, true, out _);
    }

    // ------------------------------------------------------------------ downloads

    // What AudioToolbox decodes (and the app plays): the downloads pick these formats ("": the site didn't say, tried).
    public bool CanDecode(string codecFamily) => codecFamily is "" or "aac" or "mp3" or "alac" or "flac" or "pcm";

    public Task<ProbeInfo> ProbeAsync(string file, CancellationToken ct) => Task.Run(() =>
    {
        using var asset = AVAsset.FromUrl(NSUrl.FromFilename(file));
        var audio = asset.TracksWithMediaType(AVMediaTypes.Audio.GetConstant()!);
        if (audio.Length == 0) throw new EngineException(L.F("This format can't be played on iPhone ({0}).", Path.GetExtension(file)));
        var format = audio[0].FormatDescriptions.OfType<CMAudioFormatDescription>().FirstOrDefault()?.AudioStreamBasicDescription;
        var video = asset.TracksWithMediaType(AVMediaTypes.Video.GetConstant()!);
        double seconds = asset.Duration.Seconds;
        return new ProbeInfo
        {
            FormatName = Path.GetExtension(file).TrimStart('.').ToLowerInvariant(),
            Duration = double.IsFinite(seconds) && seconds > 0 ? seconds : null,
            Audio = new ProbeStream
            {
                Index = 0,
                Codec = CodecOf(format?.Format),
                SampleRate = format is { SampleRate: > 0 } f ? (int)f.SampleRate : 0,
                Channels = format?.ChannelsPerFrame ?? 0,
            },
            Video = video.Length > 0 ? new ProbeStream { Index = 1, Codec = "video", Width = (int)video[0].NaturalSize.Width, Height = (int)video[0].NaturalSize.Height } : null,
        };
    }, ct);

    // AudioToolbox's formats as ffprobe names them (Codecs.Family knows those).
    private static string CodecOf(AudioFormatType? type) => type switch
    {
        AudioFormatType.MPEG4AAC or AudioFormatType.MPEG4AAC_HE or AudioFormatType.MPEG4AAC_HE_V2 or AudioFormatType.MPEG4AAC_LD
            or AudioFormatType.MPEG4AAC_ELD or AudioFormatType.MPEG4AAC_ELD_SBR or AudioFormatType.MPEG4AAC_ELD_V2 => "aac",
        AudioFormatType.MPEGLayer3 => "mp3",
        AudioFormatType.AppleLossless => "alac",
        AudioFormatType.Flac => "flac",
        AudioFormatType.Opus => "opus",
        AudioFormatType.LinearPCM => "pcm_s16le",
        AudioFormatType.AC3 => "ac3",
        AudioFormatType.EnhancedAC3 => "eac3",
        null => "",
        _ => type.Value.ToString().ToLowerInvariant(),
    };

    public Task<string?> MakeCoverAsync(byte[] data, string dir, bool square, int maxSize, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            using var image = UIImage.LoadFromData(NSData.FromArray(data));
            var jpg = Path.Combine(dir, "cover.jpg");
            return image != null && SaveJpeg(image, jpg, square, maxSize) ? jpg : null;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
    }, ct);

    // As ffmpeg's plan does (Ffmpeg.ForAudio): the site's own audio kept when the quality allows it (an MP3 only tagged
    // again, AAC and ALAC into a plain M4A), otherwise an MP3 at the bitrate asked.
    public async Task<(string File, string Description)> ConvertAudioAsync(string input, ProbeInfo probe, AudioTarget target, Tags? tags, string? coverJpg,
        string output, Action<double>? progress, CancellationToken ct)
    {
        var source = probe.Audio ?? throw new EngineException(L.T("The downloaded file has no audio."));
        var family = Codecs.Family(source.Codec);
        var cover = coverJpg != null && File.Exists(coverJpg) ? await File.ReadAllBytesAsync(coverJpg, ct) : null;
        bool copy = FormatSelector.CanCopyAudio(family, new AudioOptions(target.Format, target.Kbps, target.SampleRate, target.Channels, target.BitDepth),
            source.SampleRate, source.Channels);
        if (copy)
        {
            string file;
            if (family == "mp3")
            {
                file = Path.ChangeExtension(output, "mp3");
                await Task.Run(() => Mp3.Retag(input, file, tags, cover), ct);
            }
            else if (family is "aac" or "alac")
            {
                file = Path.ChangeExtension(output, "m4a");
                await ToM4aAsync(input, file, tags, cover, ct);
            }
            else
            {
                // (FLAC, WAV: as they came)
                file = Path.ChangeExtension(output, Codecs.ExtForAudioFamily(family));
                File.Copy(input, file, true);
            }
            progress?.Invoke(1);
            return (file, L.T("original lossless audio"));
        }
        if (target.Format != Quality.Mp3) throw new EngineException(L.F("Format not supported on iPhone: {0}", target.Format));
        var mp3 = Path.ChangeExtension(output, "mp3");
        var decode = input;
        if (family is "aac" or "alac")
        {
            // YouTube's M4A is fragmented (for streaming): AudioToolbox reads it whole only once it's a plain one.
            decode = Path.ChangeExtension(output, null) + "-source.m4a";
            await ToM4aAsync(input, decode, null, null, ct);
        }
        progress?.Invoke(0.05);
        await Task.Run(() => Mp3.Encode(decode, mp3, target.Kbps ?? 320, tags, cover, p => progress?.Invoke(0.05 + p * 0.95), ct), ct);
        if (decode != input) File.Delete(decode);
        return (mp3, "");
    }

    // The audio of a file AVFoundation reads (fragmented M4A, MP4 with a video) as a plain M4A, the samples copied as they
    // are, with the tags and the cover.
    private static async Task ToM4aAsync(string input, string output, Tags? tags, byte[]? cover, CancellationToken ct)
    {
        using var asset = AVAsset.FromUrl(NSUrl.FromFilename(input));
        var track = asset.TracksWithMediaType(AVMediaTypes.Audio.GetConstant()!).FirstOrDefault()
                    ?? throw new EngineException(L.T("The downloaded file has no audio."));
        // Only the audio: an M4A can't hold the video an MP4 may have.
        using var composition = new AVMutableComposition();
        var audio = composition.AddMutableTrack(AVMediaTypes.Audio.GetConstant()!, 0);
        if (audio == null || !audio.InsertTimeRange(track.TimeRange, track, CMTime.Zero, out var error))
            throw new EngineException(L.T("Conversion failed") + ".", "insert");
        using var session = new AVAssetExportSession(composition, AVAssetExportSessionPreset.Passthrough);
        session.OutputUrl = NSUrl.FromFilename(output);
        session.OutputFileType = AVFileTypes.AppleM4a.GetConstant();
        session.ShouldOptimizeForNetworkUse = true;
        var metadata = Metadata(tags, cover);
        if (metadata.Length > 0) session.Metadata = metadata;
        if (File.Exists(output)) File.Delete(output);
        using (ct.Register(session.CancelExport))
            await session.ExportTaskAsync();
        ct.ThrowIfCancellationRequested();
        if (session.Status != AVAssetExportSessionStatus.Completed || !File.Exists(output))
            throw new EngineException(L.T("Conversion failed") + ".", session.Error?.LocalizedDescription ?? session.Status.ToString());
    }

    // The tags as AVFoundation's common keys (an M4A gets them as iTunes's).
    private static AVMetadataItem[] Metadata(Tags? tags, byte[]? cover)
    {
        var items = new List<AVMetadataItem>();
        void Add(NSString key, NSObject value, string? dataType = null)
        {
            var item = new AVMutableMetadataItem { KeySpace = AVMetadata.KeySpaceCommon, Key = key, Value = value };
            if (dataType != null) item.DataType = new NSString(dataType);
            items.Add(item);
        }
        if (!string.IsNullOrWhiteSpace(tags?.Title)) Add(AVMetadata.CommonKeyTitle, new NSString(tags.Title.Trim()));
        if (!string.IsNullOrWhiteSpace(tags?.Artist)) Add(AVMetadata.CommonKeyArtist, new NSString(tags.Artist.Trim()));
        if (!string.IsNullOrWhiteSpace(tags?.Album)) Add(AVMetadata.CommonKeyAlbumName, new NSString(tags.Album.Trim()));
        if (!string.IsNullOrWhiteSpace(tags?.Year)) Add(AVMetadata.CommonKeyCreationDate, new NSString(tags.Year.Trim()));
        if (cover is { Length: > 0 }) Add(AVMetadata.CommonKeyArtwork, NSData.FromArray(cover), "com.apple.metadata.datatype.JPEG");
        return items.ToArray();
    }

    // ------------------------------------------------------------------ waveform, loudness, peak

    public Task<AnalysisOutput> AnalyzeAsync(string file, CancellationToken ct) => Task.Run(() =>
    {
        AppleTrackSource source;
        try { source = new AppleTrackSource(file); }
        catch (Exception ex) { throw new EngineException(L.T("The audio file can't be read."), ex.Message); }
        using (source)
        {
            int rate = source.Rate, ch = source.Channels;
            var loudness = new R128(rate, ch);
            // The waveform: the mean square of the sound (both channels together) every 20 ms.
            int block = Math.Max(1, rate / 50);
            var blocks = new List<double>(4096);
            double acc = 0, peak = 0;
            int inBlock = 0;
            long total = 0;
            var buf = new float[4096 * ch];
            int got;
            while ((got = source.ReadFrames(buf, 4096)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                loudness.Add(buf, got);
                for (int i = 0; i < got; i++)
                {
                    double m = 0;
                    for (int c = 0; c < ch; c++)
                    {
                        double s = buf[i * ch + c];
                        m += s;
                        double a = Math.Abs(s);
                        if (a > peak) peak = a;
                    }
                    m /= ch;
                    acc += m * m;
                    if (++inBlock == block)
                    {
                        blocks.Add(acc / block);
                        acc = 0;
                        inBlock = 0;
                    }
                }
                total += got;
            }
            if (inBlock > 0) blocks.Add(acc / inBlock);
            if (blocks.Count == 0) throw new EngineException(L.T("The audio file can't be read."));
            return new AnalysisOutput(AudioAnalysis.ToBars(blocks), loudness.Integrated(), peak > 0 ? 20 * Math.Log10(peak) : null, total / (double)rate);
        }
    }, ct);

    // Integrated loudness (ITU-R BS.1770 / EBU R128): each channel through the K-weighting filter (a high shelf and a
    // high-pass, their coefficients for this rate as libebur128 computes them), the mean square of 400 ms blocks every
    // 100 ms, the blocks under -70 LUFS left out, then those more than 10 LU under the average of the rest.
    private sealed class R128
    {
        private readonly int _ch, _step;
        private readonly double[] _pb = new double[3], _pa = new double[3], _rb = { 1, -2, 1 }, _ra = new double[3];
        // Per channel: the two filters' states.
        private readonly double[] _z1, _z2, _w1, _w2;
        private readonly List<double> _steps = new();
        private double _sum;
        private int _inStep;

        public R128(int rate, int channels)
        {
            _ch = channels;
            _step = Math.Max(1, rate / 10);
            _z1 = new double[channels];
            _z2 = new double[channels];
            _w1 = new double[channels];
            _w2 = new double[channels];
            double f0 = 1681.974450955533, g = 3.999843853973347, q = 0.7071752369554196;
            double k = Math.Tan(Math.PI * f0 / rate);
            double vh = Math.Pow(10, g / 20), vb = Math.Pow(vh, 0.4996667741545416);
            double a0 = 1 + k / q + k * k;
            _pb[0] = (vh + vb * k / q + k * k) / a0;
            _pb[1] = 2 * (k * k - vh) / a0;
            _pb[2] = (vh - vb * k / q + k * k) / a0;
            _pa[0] = 1;
            _pa[1] = 2 * (k * k - 1) / a0;
            _pa[2] = (1 - k / q + k * k) / a0;
            f0 = 38.13547087602444;
            q = 0.5003270373238773;
            k = Math.Tan(Math.PI * f0 / rate);
            _ra[0] = 1;
            _ra[1] = 2 * (k * k - 1) / (1 + k / q + k * k);
            _ra[2] = (1 - k / q + k * k) / (1 + k / q + k * k);
        }

        public void Add(float[] buf, int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                for (int c = 0; c < _ch; c++)
                {
                    double x = buf[i * _ch + c];
                    double y = _pb[0] * x + _z1[c];
                    _z1[c] = _pb[1] * x - _pa[1] * y + _z2[c];
                    _z2[c] = _pb[2] * x - _pa[2] * y;
                    double v = _rb[0] * y + _w1[c];
                    _w1[c] = _rb[1] * y - _ra[1] * v + _w2[c];
                    _w2[c] = _rb[2] * y - _ra[2] * v;
                    _sum += v * v;
                }
                if (++_inStep == _step)
                {
                    _steps.Add(_sum);
                    _sum = 0;
                    _inStep = 0;
                }
            }
        }

        public double? Integrated()
        {
            // 400 ms blocks: four steps of 100 ms.
            var blocks = new List<double>();
            for (int i = 0; i + 4 <= _steps.Count; i++) blocks.Add((_steps[i] + _steps[i + 1] + _steps[i + 2] + _steps[i + 3]) / (4.0 * _step));
            static double Lufs(double power) => -0.691 + 10 * Math.Log10(power);
            var loud = blocks.Where(p => p > 0 && Lufs(p) > -70).ToList();
            if (loud.Count == 0) return null;
            double gate = Lufs(loud.Average()) - 10;
            var kept = loud.Where(p => Lufs(p) > gate).ToList();
            if (kept.Count == 0) return null;
            double result = Lufs(kept.Average());
            return result > -70 ? result : null;
        }
    }
}
