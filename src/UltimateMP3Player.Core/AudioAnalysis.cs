using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltimateMP3Player.Core;

public sealed record AnalysisOutput(byte[] Wave, double? Loudness, double? Peak, double Duration);

public sealed record FileTags(string? Title, string? Artist, string? Album, string? Year, double? Duration, bool HasVideo, bool HasCover);

public static class AudioAnalysis
{
    public const int Bars = 320;
    private const int Rate = 8000;

    // One ffmpeg pass: RMS waveform and EBU R128 loudness.
    public static async Task<AnalysisOutput> AnalyzeAsync(string file, CancellationToken ct)
    {
        var args = new[]
        {
            "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info", "-i", file,
            "-filter_complex", $"[0:a:0]asplit=2[w][l];[l]ebur128=peak=sample:framelog=quiet[lo];[w]aresample={Rate},aformat=sample_fmts=s16:channel_layouts=mono[wo]",
            "-map", "[lo]", "-f", "null", "-",
            "-map", "[wo]", "-f", "s16le", "pipe:1",
        };
        using var p = ChildProcess.Start(Engines.Ffmpeg, args);
        p.LowerPriority();
        using var reg = ct.Register(p.Kill);
        var errTask = new StreamReader(p.StandardError, Encoding.UTF8).ReadToEndAsync();

        var squares = new List<double>(4096);
        var stream = p.StandardOutput;
        var buf = new byte[1 << 16];
        int carry = 0;
        long samples = 0;
        double acc = 0;
        int inBlock = 0;
        const int block = Rate / 50; // 20 ms blocks, merged into bars afterwards
        int n;
        while ((n = await stream.ReadAsync(buf.AsMemory(carry, buf.Length - carry), ct)) > 0)
        {
            n += carry;
            int even = n & ~1;
            for (int i = 0; i < even; i += 2)
            {
                double s = (short)(buf[i] | (buf[i + 1] << 8)) / 32768.0;
                acc += s * s;
                if (++inBlock == block)
                {
                    squares.Add(acc / block);
                    acc = 0;
                    inBlock = 0;
                }
                samples++;
            }
            carry = n - even;
            if (carry > 0) buf[0] = buf[even];
        }
        if (inBlock > 0) squares.Add(acc / inBlock);
        await p.WaitForExitAsync(ct);
        var err = await errTask;
        if (squares.Count == 0) throw new EngineException(L.T("Il file audio non è leggibile."), err);

        double? lufs = null;
        var m = Regex.Matches(err, @"I:\s+(-?\d+(?:\.\d+)?)\s+LUFS");
        if (m.Count > 0 && double.TryParse(m[^1].Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var l) && l > -70)
            lufs = l;
        double? peak = null;
        var pm = Regex.Match(err, @"Sample peak:\s*Peak:\s*(-?\d+(?:\.\d+)?)\s*dBFS");
        if (pm.Success && double.TryParse(pm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pk)) peak = pk;

        return new AnalysisOutput(ToBars(squares), lufs, peak, samples / (double)Rate);
    }

    private static byte[] ToBars(List<double> blocks)
    {
        var bars = new double[Bars];
        for (int b = 0; b < Bars; b++)
        {
            int from = (int)((long)b * blocks.Count / Bars);
            int to = Math.Max(from + 1, (int)((long)(b + 1) * blocks.Count / Bars));
            double sum = 0;
            int cnt = 0;
            for (int i = from; i < to && i < blocks.Count; i++) { sum += blocks[i]; cnt++; }
            bars[b] = cnt > 0 ? Math.Sqrt(sum / cnt) : 0;
        }
        double max = bars.Max();
        var result = new byte[Bars];
        if (max <= 0) return result;
        for (int b = 0; b < Bars; b++)
            result[b] = (byte)Math.Clamp(Math.Round(Math.Pow(bars[b] / max, 1.4) * 255), 0, 255);
        return result;
    }

    public static async Task<FileTags> ReadTagsAsync(string file, CancellationToken ct)
    {
        var r = await ProcRunner.RunAsync(Engines.Ffprobe,
            new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", file }, ct: ct, timeout: TimeSpan.FromMinutes(1));
        if (r.ExitCode != 0 || !r.StdOut.TrimStart().StartsWith('{'))
            throw new EngineException(L.T("Il file non è un audio leggibile."), r.StdErr);
        using var doc = JsonDocument.Parse(r.StdOut);
        var root = doc.RootElement;
        bool hasAudio = false, hasVideo = false, hasCover = false;
        foreach (var s in root.Arr("streams"))
        {
            var type = s.Str("codec_type");
            bool pic = s.Prop("disposition")?.Int("attached_pic") == 1;
            if (type == "audio") hasAudio = true;
            else if (type == "video" && pic) hasCover = true;
            else if (type == "video" && s.Str("codec_name") is not ("mjpeg" or "png" or "bmp")) hasVideo = true;
        }
        if (!hasAudio) throw new EngineException(L.T("Il file non contiene audio."));
        var fmt = root.Prop("format");
        var tags = fmt?.Prop("tags");
        string? Tag(params string[] names)
        {
            if (tags is not { } t) return null;
            foreach (var prop in t.EnumerateObject())
                if (names.Any(n => prop.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) && prop.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(prop.Value.GetString()))
                    return prop.Value.GetString()!.Trim();
            return null;
        }
        var date = Tag("date", "year", "TYER", "TDRC");
        return new FileTags(Tag("title"), Tag("artist", "album_artist"), Tag("album"), date is { Length: >= 4 } ? date[..4] : null,
            fmt?.Num("duration"), hasVideo, hasCover || hasVideo);
    }

    // Embedded cover or video frame as a ≤600 px JPEG.
    public static async Task<bool> ExtractCoverAsync(string file, string jpg, bool fromVideo, CancellationToken ct)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error" };
        if (fromVideo) args.AddRange(new[] { "-ss", "5" });
        args.AddRange(new[] { "-i", file, "-map", "0:v:0", "-frames:v", "1", "-vf", "scale='min(600,iw)':-2:flags=lanczos,format=yuvj420p", "-q:v", "3", jpg });
        try
        {
            var r = await ProcRunner.RunAsync(Engines.Ffmpeg, args, ct: ct, timeout: TimeSpan.FromMinutes(1));
            if (r.ExitCode == 0 && File.Exists(jpg) && new FileInfo(jpg).Length > 0) return true;
            if (fromVideo)
            {
                args.RemoveRange(5, 2);
                r = await ProcRunner.RunAsync(Engines.Ffmpeg, args, ct: ct, timeout: TimeSpan.FromMinutes(1));
                return r.ExitCode == 0 && File.Exists(jpg) && new FileInfo(jpg).Length > 0;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return false;
    }

    // User-chosen picture to a square cover JPEG.
    public static async Task<bool> MakeCoverFromImageAsync(string image, string jpg, CancellationToken ct = default)
    {
        var tmp = jpg + ".new.jpg";
        Directory.CreateDirectory(Path.GetDirectoryName(jpg)!);
        var r = await ProcRunner.RunAsync(Engines.Ffmpeg, new[]
        {
            "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-i", image, "-frames:v", "1",
            "-vf", "crop='min(iw,ih)':'min(iw,ih)',scale='min(600,iw)':-2:flags=lanczos,format=yuvj420p", "-q:v", "3", tmp,
        }, ct: ct, timeout: TimeSpan.FromMinutes(1));
        if (r.ExitCode != 0 || !File.Exists(tmp)) return false;
        File.Move(tmp, jpg, true);
        return true;
    }
}
