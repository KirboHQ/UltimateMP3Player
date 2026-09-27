namespace UltimateMP3Player.Core;

// yt-dlp formats to download for one item.
public sealed class Selection
{
    public YtFormat? Video { get; init; }
    public YtFormat? Audio { get; init; }

    public string Spec => Video != null && Audio != null ? $"{Video.Id}+{Audio.Id}" : (Video ?? Audio)!.Id;
    public bool Merge => Video != null && Audio != null;

    public long? EstimatedSize
    {
        get
        {
            long total = 0;
            foreach (var f in new[] { Video, Audio })
            {
                if (f == null) continue;
                if (f.Size is not > 0) return null;
                total += f.Size.Value;
            }
            return total > 0 ? total : null;
        }
    }
}

// Picks yt-dlp format ids for the user's options.
public static class FormatSelector
{
    public static readonly int[] StandardRes = { 4320, 2160, 1440, 1080, 720, 480, 360, 240, 144 };
    public static readonly int[] StandardFps = { 60, 50, 30, 25, 24, 15 };

    public static IEnumerable<YtFormat> VideoFormats(YtInfo i) => i.Formats.Where(f => f.HasVideo && !f.IsStoryboard);
    public static IEnumerable<YtFormat> AudioOnlyFormats(YtInfo i) => i.Formats.Where(f => f.HasAudio && !f.HasVideo && !f.IsStoryboard);

    // Native resolutions (short side), highest first.
    public static List<int> NativeRes(YtInfo i)
        => VideoFormats(i).Where(f => f.Res is > 0).Select(f => f.Res!.Value).Distinct().OrderByDescending(r => r).ToList();

    // Native frame rates at a resolution (null = highest).
    public static List<double> NativeFps(YtInfo i, int? res)
        => AtRes(i, res).Where(f => f.Fps > 0).Select(f => Math.Round(f.Fps!.Value)).Distinct().OrderByDescending(x => x).ToList();

    // Formats at the native res used; SDR preferred (HDR looks washed out).
    private static List<YtFormat> AtRes(YtInfo info, int? res)
    {
        var vids = VideoFormats(info).ToList();
        var natives = vids.Where(f => f.Res is > 0).Select(f => f.Res!.Value).Distinct().OrderByDescending(r => r).ToList();
        if (natives.Count > 0)
        {
            int pick = PickNative(natives, res);
            vids = vids.Where(f => f.Res == pick).ToList();
        }
        var sdr = vids.Where(f => !f.IsHdr).ToList();
        return sdr.Count > 0 ? sdr : vids;
    }

    // Wanted value if native, else next above (then downscaled).
    private static int PickNative(List<int> nativesDesc, int? wanted)
    {
        if (wanted is not int w || w >= nativesDesc[0]) return nativesDesc[0];
        if (nativesDesc.Contains(w)) return w;
        return nativesDesc.Where(n => n > w).Min();
    }

    private static double PickNativeFps(List<double> nativesDesc, double? wanted)
    {
        if (wanted is not double w || w >= nativesDesc[0] - 0.5) return nativesDesc[0];
        var exact = nativesDesc.FirstOrDefault(x => Math.Abs(x - w) <= 1);
        if (exact > 0) return exact;
        return nativesDesc.Where(x => x > w).Min();
    }

    private static string[] VideoCodecOrder(string container, VideoCompat compat)
    {
        if (container == "webm") return new[] { "vp9", "av1", "vp8", "h264", "hevc" };
        if (compat == VideoCompat.H264) return new[] { "h264", "vp9", "av1", "hevc", "vp8" };
        if (compat == VideoCompat.Original || container == "mkv") return new[] { "av1", "vp9", "hevc", "h264", "vp8" };
        // Compatible mode: H.264 first, then what Windows plays natively.
        return new[] { "h264", "vp9", "av1", "hevc", "vp8" };
    }

    private static int Rank(string[] order, string? codec)
    {
        int i = Array.IndexOf(order, Codecs.Family(codec));
        return i < 0 ? order.Length : i;
    }

    public static Selection? ForVideo(YtInfo info, VideoOptions o, VideoCompat compat)
    {
        var vids = AtRes(info, o.Res);
        if (vids.Count == 0) return null;

        var fpsList = vids.Where(f => f.Fps > 0).Select(f => Math.Round(f.Fps!.Value)).Distinct().OrderByDescending(x => x).ToList();
        if (fpsList.Count > 0)
        {
            double pickF = PickNativeFps(fpsList, o.Fps);
            vids = vids.Where(f => f.Fps > 0 && Math.Round(f.Fps!.Value) == pickF).ToList();
        }

        var order = VideoCodecOrder(o.Container, compat);
        var best = vids
            .OrderBy(f => Rank(order, f.VCodec))
            .ThenBy(f => f.IsM3u8 ? 1 : 0)
            .ThenByDescending(f => f.HasAudio && f.ACodec != null ? 1 : 0)
            .ThenByDescending(f => f.Tbr ?? 0)
            .ThenByDescending(f => f.Quality ?? 0)
            .First();

        if (o.Container == "gif") return new Selection { Video = best };

        bool audioOnlyExist = AudioOnlyFormats(info).Any();
        bool needAudio = best.AudioNone || (best.ACodec == null && audioOnlyExist);
        if (!needAudio) return new Selection { Video = best };

        var pref = o.Container switch
        {
            "mp4" or "mov" => new[] { "aac", "mp3", "opus", "vorbis" },
            "webm" => new[] { "opus", "vorbis", "aac", "mp3" },
            _ => new[] { "opus", "aac", "vorbis", "mp3" },
        };
        var audio = BestAudioOnly(info, pref);
        return new Selection { Video = best, Audio = audio };
    }

    private static readonly string[] BestAudioOrder = { "flac", "alac", "pcm", "opus", "vorbis", "aac", "mp3" };

    private static YtFormat? BestAudioOnly(YtInfo info, string[] codecOrder)
    {
        var auds = AudioOnlyFormats(info).ToList();
        if (auds.Count == 0) return null;
        var nonDrc = auds.Where(a => !a.IsDrc).ToList();
        if (nonDrc.Count > 0) auds = nonDrc;
        int maxLang = auds.Max(a => a.LangPref);
        auds = auds.Where(a => a.LangPref == maxLang).ToList();
        return auds
            .OrderBy(a => Rank(codecOrder, a.ACodec))
            .ThenBy(a => a.IsM3u8 ? 1 : 0)
            .ThenByDescending(a => a.Abr ?? a.Tbr ?? 0)
            .ThenByDescending(a => a.Quality ?? 0)
            .First();
    }

    public static bool CanCopyAudio(string sourceFamily, AudioOptions o, int? sourceRate, int? sourceChannels)
    {
        if (o.Format == "original") return true;
        if (o.Format is "wav" or "flac") return false;
        if (Codecs.ForAudioFormat(o.Format) != sourceFamily) return false;
        if (o.Kbps != null) return false;
        if (o.SampleRate is int sr && sr != sourceRate) return false;
        if (o.Channels is int ch && ch != sourceChannels) return false;
        return true;
    }

    public static Selection? ForAudio(YtInfo info, AudioOptions o)
    {
        var target = Codecs.ForAudioFormat(o.Format);
        string[] order = BestAudioOrder;
        if (target != "" && o.Kbps == null && o.SampleRate == null && o.Channels == null && o.Format is not ("wav" or "flac"))
            order = new[] { target }.Concat(BestAudioOrder.Where(c => c != target)).ToArray();

        var a = BestAudioOnly(info, order);
        if (a != null) return new Selection { Audio = a };

        // Only combined formats (TikTok, Twitter…): take the best audio.
        var combined = info.Formats.Where(f => f.HasVideo && f.HasAudio && !f.IsStoryboard).ToList();
        if (combined.Count == 0) combined = info.Formats.Where(f => !f.IsStoryboard).ToList();
        if (combined.Count == 0) return null;
        var best = combined
            .OrderByDescending(f => f.Abr ?? 0)
            .ThenBy(f => f.IsM3u8 ? 1 : 0)
            .ThenByDescending(f => f.Res ?? 0)
            .ThenByDescending(f => f.Tbr ?? 0)
            .First();
        return new Selection { Video = null, Audio = best };
    }
}
