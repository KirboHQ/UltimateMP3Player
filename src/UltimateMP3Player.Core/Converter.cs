using System.Globalization;
using System.Text;
using System.Text.Json;

namespace UltimateMP3Player.Core;

public sealed class ProbeStream
{
    public int Index { get; init; }
    public string Codec { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public string? PixFmt { get; init; }
    public long Packets { get; init; }
}

public sealed class ProbeInfo
{
    public string FormatName { get; init; } = "";
    public double? Duration { get; init; }
    public ProbeStream? Video { get; init; }
    public ProbeStream? Audio { get; init; }
    public bool Animated => Video != null && (Video.Packets > 1 || (Duration is > 0.2 && Video.Codec is "gif" or "webp" or "apng"));
}

public sealed record Tags(string? Title, string? Artist, string? Album, string? Year, int? Track, string? Comment);

public abstract record Target;
public sealed record VideoTarget(string Container, int? Res, double? Fps, VideoCompat Compat) : Target;
public sealed record AudioTarget(string Format, int? Kbps, int? SampleRate, int? Channels, int BitDepth) : Target;
public sealed record ImageTarget(string Format, int? MaxSide) : Target;

public static class Ffmpeg
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static async Task<ProbeInfo> ProbeAsync(string file, CancellationToken ct, bool countPackets = false)
    {
        var args = new List<string> { "-v", "error", "-print_format", "json", "-show_format", "-show_streams" };
        if (countPackets) args.Add("-count_packets");
        args.Add(file);
        var r = await ProcRunner.RunAsync(Engines.Ffprobe, args, ct: ct, timeout: TimeSpan.FromMinutes(2));
        if (r.ExitCode != 0 || !r.StdOut.TrimStart().StartsWith("{"))
            throw new EngineException(L.T("Il file scaricato non è leggibile (formato sconosciuto o danneggiato)."), r.StdErr);
        using var doc = JsonDocument.Parse(r.StdOut);
        var root = doc.RootElement;
        ProbeStream? video = null, audio = null;
        foreach (var s in root.Arr("streams"))
        {
            var type = s.Str("codec_type");
            bool cover = s.Prop("disposition")?.Int("attached_pic") == 1;
            if (type == "video" && video == null && !cover)
            {
                video = new ProbeStream
                {
                    Index = s.Int("index") ?? 0,
                    Codec = s.Str("codec_name") ?? "",
                    Width = s.Int("width") ?? 0,
                    Height = s.Int("height") ?? 0,
                    Fps = Rate(s.Str("avg_frame_rate")) is > 0 and < 1000 and var f ? f : Rate(s.Str("r_frame_rate")),
                    PixFmt = s.Str("pix_fmt"),
                    Packets = s.Long("nb_read_packets") ?? s.Long("nb_frames") ?? 0,
                };
            }
            else if (type == "audio" && audio == null)
            {
                audio = new ProbeStream
                {
                    Index = s.Int("index") ?? 0,
                    Codec = s.Str("codec_name") ?? "",
                    SampleRate = s.Int("sample_rate") ?? 0,
                    Channels = s.Int("channels") ?? 0,
                };
            }
        }
        var fmt = root.Prop("format");
        return new ProbeInfo
        {
            FormatName = fmt?.Str("format_name") ?? "",
            Duration = fmt?.Num("duration") ?? null,
            Video = video,
            Audio = audio,
        };

        static double Rate(string? r)
        {
            if (string.IsNullOrEmpty(r)) return 0;
            var p = r.Split('/');
            if (p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, Inv, out var a) &&
                double.TryParse(p[1], NumberStyles.Float, Inv, out var b) && b > 0) return a / b;
            return double.TryParse(r, NumberStyles.Float, Inv, out var d) ? d : 0;
        }
    }

    // Runs ffmpeg; progress 0..1 when duration is known.
    public static async Task RunAsync(IEnumerable<string> args, double? duration, Action<double>? progress, CancellationToken ct)
    {
        var all = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats" };
        all.AddRange(args);
        var r = await ProcRunner.RunAsync(Engines.Ffmpeg, all, onOut: line =>
        {
            if (progress == null || duration is not > 0) return;
            if (line.StartsWith("out_time_us="))
            {
                if (long.TryParse(line.AsSpan(12), NumberStyles.Integer, Inv, out var us) && us > 0)
                    progress(Math.Clamp(us / 1_000_000.0 / duration.Value, 0, 1));
            }
        }, ct: ct, captureOut: false);
        if (r.ExitCode != 0)
        {
            var last = r.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
            throw new EngineException(L.T("Conversione non riuscita") + (last.Length > 0 ? ": " + (last.Length > 160 ? last[..160] + "…" : last) : "."), r.StdErr);
        }
    }

    // Picture to cover-art JPEG, optionally cropped square.
    public static async Task<string?> MakeCoverAsync(byte[] data, string dir, bool square, int maxSize, CancellationToken ct)
    {
        var src = Path.Combine(dir, "cover.src");
        var dst = Path.Combine(dir, "cover.jpg");
        await File.WriteAllBytesAsync(src, data, ct);
        var vf = (square ? "crop='min(iw,ih)':'min(iw,ih)'," : "") + $"scale='min({maxSize},iw)':-2:flags=lanczos,format=yuvj420p";
        try
        {
            await RunAsync(new[] { "-i", src, "-frames:v", "1", "-vf", vf, "-q:v", "2", dst }, null, null, ct);
            return File.Exists(dst) ? dst : null;
        }
        catch (EngineException) { return null; }
    }

    // ffmetadata file avoids command-line quoting and length limits.
    public static string WriteMetadata(string dir, Tags t, byte[]? oggPicture = null)
    {
        var sb = new StringBuilder(";FFMETADATA1\n");
        void Add(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var v = value.Replace("\\", "\\\\").Replace("=", "\\=").Replace(";", "\\;").Replace("#", "\\#").Replace("\r", "").Replace("\n", "\\\n");
            sb.Append(key).Append('=').Append(v).Append('\n');
        }
        Add("title", t.Title);
        Add("artist", t.Artist);
        Add("album_artist", t.Album != null ? t.Artist?.Split(',')[0].Trim() : null);
        Add("album", t.Album);
        Add("date", t.Year);
        Add("track", t.Track?.ToString(Inv));
        Add("comment", t.Comment);
        if (oggPicture != null) Add("METADATA_BLOCK_PICTURE", Convert.ToBase64String(oggPicture));
        var path = Path.Combine(dir, "meta.txt");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    // FLAC picture block: how Ogg/Opus files carry covers.
    public static byte[] OggPictureBlock(byte[] jpeg, int width, int height)
    {
        using var ms = new MemoryStream();
        void U32(uint v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
        var mime = Encoding.ASCII.GetBytes("image/jpeg");
        U32(3); // front cover
        U32((uint)mime.Length); ms.Write(mime);
        U32(0); // description
        U32((uint)width); U32((uint)height); U32(24); U32(0);
        U32((uint)jpeg.Length); ms.Write(jpeg);
        return ms.ToArray();
    }

    public sealed record Plan(List<string> Args, string Ext, bool JustMove, string Description);

    public static Plan ForVideo(string input, ProbeInfo p, VideoTarget t, string? metaFile, string output)
    {
        var v = p.Video!;
        var a = new List<string> { "-i", input };
        if (metaFile != null) a.AddRange(new[] { "-f", "ffmetadata", "-i", metaFile });

        var filters = new List<string>();
        bool portrait = v.Height > v.Width;
        int shortSide = Math.Min(v.Width, v.Height);
        var notes = new List<string>();
        if (t.Res is int r && shortSide > r + 1)
        {
            filters.Add(portrait ? $"scale={r}:-2:flags=lanczos" : $"scale=-2:{r}:flags=lanczos");
            notes.Add(L.F("ridimensionato a {0}p", r));
        }
        double? fpsOut = null;
        if (t.Container == "gif")
        {
            double want = t.Fps ?? Math.Min(v.Fps > 0 ? v.Fps : 30, 50);
            if (v.Fps <= 0 || v.Fps > want + 0.5) fpsOut = want;
        }
        else if (t.Fps is double f && v.Fps > f + 0.5) { fpsOut = f; notes.Add($"{f:0.##} fps"); }
        if (fpsOut is double fo) filters.Insert(0, $"fps={fo.ToString("0.###", Inv)}");

        if (t.Container == "gif")
        {
            var chain = string.Join(",", filters.Append("split[s0][s1]"));
            a.AddRange(new[] { "-map", $"0:{v.Index}", "-vf", chain + ";[s0]palettegen=stats_mode=diff[p];[s1][p]paletteuse=dither=sierra2_4a", "-loop", "0", "-an", output });
            return new Plan(a, "gif", false, L.T("conversione in GIF"));
        }

        var vf = Codecs.Family(v.Codec);
        bool vEnc = filters.Count > 0 || !Codecs.VideoFits(t.Container, vf, t.Compat);
        a.AddRange(new[] { "-map", $"0:{v.Index}" });
        if (p.Audio != null) a.AddRange(new[] { "-map", $"0:{p.Audio.Index}" });
        if (vEnc)
        {
            if (filters.Count > 0) a.AddRange(new[] { "-vf", string.Join(",", filters) });
            // Downscaling means smaller files wanted: slightly higher CRF.
            bool scaled = filters.Any(f => f.StartsWith("scale", StringComparison.Ordinal));
            if (t.Container == "webm")
                a.AddRange(new[] { "-c:v", "libvpx-vp9", "-crf", scaled ? "33" : "31", "-b:v", "0", "-row-mt", "1", "-cpu-used", "4", "-pix_fmt", "yuv420p" });
            else
                a.AddRange(new[] { "-c:v", "libx264", "-preset", "medium", "-crf", scaled ? "21" : "18", "-pix_fmt", "yuv420p" });
            notes.Add(L.F("video ricodificato in {0}", t.Container == "webm" ? "VP9" : "H.264"));
        }
        else
        {
            a.AddRange(new[] { "-c:v", "copy" });
            if (vf == "hevc" && t.Container is "mp4" or "mov") a.AddRange(new[] { "-tag:v", "hvc1" });
        }

        bool aEnc = false;
        if (p.Audio != null)
        {
            var af = Codecs.Family(p.Audio.Codec);
            if (Codecs.AudioFits(t.Container, af)) a.AddRange(new[] { "-c:a", "copy" });
            else
            {
                aEnc = true;
                if (t.Container == "webm") a.AddRange(new[] { "-c:a", "libopus", "-b:a", "160k" });
                else a.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
                notes.Add(L.F("audio convertito in {0}", t.Container == "webm" ? "Opus" : "AAC"));
            }
        }
        if (metaFile != null) a.AddRange(new[] { "-map_metadata", "1" });
        if (t.Container is "mp4" or "mov") a.AddRange(new[] { "-movflags", "+faststart" });
        a.Add(output);

        var desc = notes.Count > 0 ? string.Join(", ", notes) : L.T("nessuna ricodifica");
        bool justMove = !vEnc && !aEnc && metaFile == null &&
                        Path.GetExtension(input).TrimStart('.').Equals(t.Container, StringComparison.OrdinalIgnoreCase);
        return new Plan(a, t.Container, justMove, desc);
    }

    public static Plan ForAudio(string input, ProbeInfo p, AudioTarget t, string? metaFile, string? coverJpg, string output)
    {
        var s = p.Audio ?? throw new EngineException(L.T("Il file scaricato non contiene audio."));
        var fam = Codecs.Family(s.Codec);
        bool original = t.Format == "original";
        bool copy = FormatSelector.CanCopyAudio(fam, new AudioOptions(t.Format, t.Kbps, t.SampleRate, t.Channels, t.BitDepth), s.SampleRate, s.Channels);
        string ext = original ? Codecs.ExtForAudioFamily(fam) : t.Format;
        bool coverOk = coverJpg != null && ext is "mp3" or "m4a" or "flac";

        var a = new List<string> { "-i", input };
        int metaIdx = -1, coverIdx = -1, next = 1;
        if (metaFile != null) { a.AddRange(new[] { "-f", "ffmetadata", "-i", metaFile }); metaIdx = next++; }
        if (coverOk) { a.AddRange(new[] { "-i", coverJpg! }); coverIdx = next++; }

        a.AddRange(new[] { "-map", $"0:{s.Index}" });
        if (coverIdx > 0)
        {
            a.AddRange(new[] { "-map", $"{coverIdx}:0", "-c:v", "copy", "-disposition:v:0", "attached_pic" });
            if (ext == "mp3") a.AddRange(new[] { "-metadata:s:v", "title=Album cover", "-metadata:s:v", "comment=Cover (front)" });
        }

        string desc = "";
        if (copy)
        {
            a.AddRange(new[] { "-c:a", "copy" });
            desc = L.T("audio originale senza perdita");
        }
        else
        {
            switch (t.Format)
            {
                case "mp3": a.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", $"{t.Kbps ?? 320}k" }); break;
                case "m4a": a.AddRange(new[] { "-c:a", "aac", "-b:a", $"{t.Kbps ?? 256}k" }); break;
                case "opus": a.AddRange(new[] { "-c:a", "libopus", "-b:a", $"{Math.Min(t.Kbps ?? 256, 256)}k" }); break;
                case "ogg":
                    if (t.Kbps is int kb) a.AddRange(new[] { "-c:a", "libvorbis", "-b:a", $"{kb}k" });
                    else a.AddRange(new[] { "-c:a", "libvorbis", "-q:a", "8" });
                    break;
                case "wav":
                    a.AddRange(new[] { "-c:a", t.BitDepth switch { 24 => "pcm_s24le", 32 => "pcm_f32le", _ => "pcm_s16le" } });
                    break;
                case "flac":
                    a.AddRange(t.BitDepth >= 24
                        ? new[] { "-c:a", "flac", "-sample_fmt", "s32", "-bits_per_raw_sample", "24" }
                        : new[] { "-c:a", "flac", "-sample_fmt", "s16" });
                    break;
                default: a.AddRange(new[] { "-c:a", "copy" }); break;
            }
            if (t.SampleRate is int sr) a.AddRange(new[] { "-ar", sr.ToString(Inv) });
            if (t.Channels is int ch) a.AddRange(new[] { "-ac", ch.ToString(Inv) });
        }
        if (metaIdx > 0) a.AddRange(new[] { "-map_metadata", metaIdx.ToString(Inv) });
        if (ext == "mp3") a.AddRange(new[] { "-id3v2_version", "3" });
        if (ext == "m4a") a.AddRange(new[] { "-movflags", "+faststart" });
        a.Add(Path.ChangeExtension(output, ext));
        return new Plan(a, ext, false, desc);
    }

    public static Plan ForImage(string input, ProbeInfo p, ImageTarget t, string output)
    {
        var v = p.Video ?? throw new EngineException(L.T("Il file scaricato non è un'immagine leggibile."));
        var inExt = Path.GetExtension(input).TrimStart('.').ToLowerInvariant();
        if (inExt == "jpeg" || inExt == "jfif") inExt = "jpg";
        bool animated = p.Animated;

        string fmt = t.Format;
        if (fmt == "original") fmt = inExt switch
        {
            "png" or "jpg" or "webp" or "gif" or "bmp" => inExt,
            _ => v.Codec switch { "mjpeg" => "jpg", "png" => "png", "webp" => "webp", "gif" => "gif", _ => "png" },
        };
        if (fmt == "mp4" && !animated) fmt = inExt is "png" or "jpg" or "webp" or "gif" or "bmp" ? inExt : "png";

        bool resize = t.MaxSide is int maxSide && Math.Max(v.Width, v.Height) > maxSide;
        if (!resize && fmt == inExt)
            return new Plan(new List<string>(), inExt, true, L.F("{0}×{1}, file originale", v.Width, v.Height));

        int nw = v.Width, nh = v.Height;
        if (resize)
        {
            int L2 = t.MaxSide!.Value;
            if (v.Width >= v.Height) { nw = L2; nh = Math.Max(1, (int)Math.Round(v.Height * (double)L2 / v.Width)); }
            else { nh = L2; nw = Math.Max(1, (int)Math.Round(v.Width * (double)L2 / v.Height)); }
        }
        string scale = resize ? $"scale={nw}:{nh}:flags=lanczos" : "";
        var a = new List<string> { "-i", input };
        string outFile = Path.ChangeExtension(output, fmt);

        switch (fmt)
        {
            case "jpg":
                a.AddRange(new[]
                {
                    "-filter_complex",
                    $"color=c=white:s={nw}x{nh}[bg];[0:v:0]{(resize ? scale + "," : "")}format=rgba[fg];[bg][fg]overlay=shortest=1:format=auto,format=yuvj444p[out]",
                    "-map", "[out]", "-frames:v", "1", "-q:v", "2", outFile,
                });
                break;
            case "png":
                if (resize) a.AddRange(new[] { "-vf", scale });
                a.AddRange(new[] { "-frames:v", "1", "-c:v", "png", outFile });
                break;
            case "bmp":
                if (resize) a.AddRange(new[] { "-vf", scale });
                a.AddRange(new[] { "-frames:v", "1", "-c:v", "bmp", outFile });
                break;
            case "webp":
                if (resize) a.AddRange(new[] { "-vf", scale });
                // libwebp reads quality from -q:v, not -quality.
                if (animated) a.AddRange(new[] { "-c:v", "libwebp_anim", "-loop", "0", "-q:v", "90", outFile });
                else a.AddRange(new[] { "-frames:v", "1", "-c:v", "libwebp", "-q:v", "95", outFile });
                break;
            case "gif":
                {
                    var chain = (resize ? scale + "," : "") + "split[s0][s1];[s0]palettegen=stats_mode=diff[p];[s1][p]paletteuse=dither=sierra2_4a";
                    a.AddRange(new[] { "-vf", chain });
                    if (!animated) a.AddRange(new[] { "-frames:v", "1" });
                    a.AddRange(new[] { "-loop", "0", outFile });
                    break;
                }
            case "mp4":
                {
                    var chain = (resize ? scale + "," : "") + "scale=trunc(iw/2)*2:trunc(ih/2)*2";
                    a.AddRange(new[] { "-vf", chain, "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-an", outFile });
                    break;
                }
            default:
                throw new EngineException(L.F("Formato immagine non supportato: {0}", fmt));
        }
        return new Plan(a, fmt, false, resize ? $"{nw}×{nh}" : $"{v.Width}×{v.Height}");
    }
}
