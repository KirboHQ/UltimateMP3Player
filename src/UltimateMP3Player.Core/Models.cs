using System.Text.Json;

namespace UltimateMP3Player.Core;

public enum MediaKind { Video, Audio, Image, Animated }

public enum SourceKind
{
    // Handled by yt-dlp.
    YtDlp,
    // A plain file URL.
    Direct,
    // Metadata only (Spotify, Deezer): found on YouTube Music.
    Search,
}

public sealed class YtFormat
{
    public string Id { get; init; } = "";
    public string? Ext { get; init; }
    public string? VCodec { get; init; }
    public string? ACodec { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? Fps { get; init; }
    public double? Tbr { get; init; }
    public double? Abr { get; init; }
    public int? Asr { get; init; }
    public int? Channels { get; init; }
    public long? Size { get; init; }
    public string? DynamicRange { get; init; }
    public int LangPref { get; init; } = -1;
    public string? Protocol { get; init; }
    public string? Note { get; init; }
    public double? Quality { get; init; }
    public bool HasDrm { get; init; }

    private static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase)
        { "m4a", "mp3", "opus", "ogg", "oga", "wav", "flac", "aac", "weba", "mka" };

    public bool VideoNone => VCodec == "none";
    public bool AudioNone => ACodec == "none";
    public bool IsStoryboard => Protocol == "mhtml" || (VideoNone && AudioNone) || Note == "storyboard";

    public bool HasVideo => !VideoNone && !(VCodec == null && Width == null && Height == null &&
                                            ((Ext != null && AudioExts.Contains(Ext)) || (ACodec != null && !AudioNone)));

    public bool HasAudio => !AudioNone;
    public bool IsHdr => DynamicRange is not null && DynamicRange != "SDR";
    public bool IsDrc => Id.EndsWith("-drc", StringComparison.OrdinalIgnoreCase) || (Note?.Contains("DRC") ?? false);
    public bool IsM3u8 => Protocol?.StartsWith("m3u8", StringComparison.Ordinal) ?? false;

    // Short side, so vertical 1080×1920 is 1080p.
    public int? Res => Width is int w && Height is int h && w > 0 && h > 0 ? Math.Min(w, h) : Height;

    public static YtFormat Parse(JsonElement f) => new()
    {
        Id = f.Str("format_id") ?? "",
        Ext = f.Str("ext"),
        VCodec = f.Str("vcodec"),
        ACodec = f.Str("acodec"),
        Width = f.Int("width"),
        Height = f.Int("height"),
        Fps = f.Num("fps") is > 0 and var fps ? fps : null,
        Tbr = f.Num("tbr"),
        Abr = f.Num("abr") is > 0 and var abr ? abr : null,
        Asr = f.Int("asr") is > 0 and var asr ? asr : null,
        Channels = f.Int("audio_channels") is > 0 and var ch ? ch : null,
        Size = f.Long("filesize") ?? f.Long("filesize_approx"),
        DynamicRange = f.Str("dynamic_range"),
        LangPref = f.Int("language_preference") ?? -1,
        Protocol = f.Str("protocol"),
        Note = f.Str("format_note"),
        Quality = f.Num("quality"),
        HasDrm = f.Bool("has_drm"),
    };
}

// One video as "yt-dlp -J" describes it.
public sealed class YtInfo
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string WebpageUrl { get; init; } = "";
    public string Extractor { get; init; } = "";
    public string? Uploader { get; init; }
    public string? Artist { get; init; }
    public string? Track { get; init; }
    public string? Album { get; init; }
    public string? Year { get; init; }
    public double? Duration { get; init; }
    public List<string> Thumbnails { get; init; } = new();
    public bool IsLive { get; init; }
    public List<YtFormat> Formats { get; init; } = new();

    public bool HasVideo => Formats.Any(f => f.HasVideo && !f.IsStoryboard);

    public static YtInfo Parse(JsonElement e)
    {
        var formats = e.Arr("formats").Select(YtFormat.Parse).Where(f => f.Id != "" && !f.HasDrm).ToList();
        if (formats.Count == 0 && e.Str("url") != null) formats.Add(YtFormat.Parse(e));

        var artists = e.Arr("artists").Select(a => a.ValueKind == JsonValueKind.String ? a.GetString() : null)
            .Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        string? artist = artists.Count > 0 ? string.Join(", ", artists) : e.Str("artist") ?? e.Str("creator");

        var date = e.Str("release_date") ?? e.Str("upload_date");
        return new YtInfo
        {
            Id = e.Str("id") ?? "",
            Title = Text.FirstNonEmpty(e.Str("title"), e.Str("fulltitle"), e.Str("id")) ?? "video",
            WebpageUrl = Text.FirstNonEmpty(e.Str("webpage_url"), e.Str("original_url"), e.Str("url")) ?? "",
            Extractor = Text.FirstNonEmpty(e.Str("extractor_key"), e.Str("extractor"), e.Str("ie_key")) ?? "",
            Uploader = Text.FirstNonEmpty(e.Str("uploader"), e.Str("channel"), e.Str("creator")),
            Artist = artist,
            Track = e.Str("track"),
            Album = e.Str("album"),
            Year = e.Str("release_year") ?? (date is { Length: >= 4 } ? date[..4] : null),
            Duration = e.Num("duration"),
            Thumbnails = Thumbs(e),
            IsLive = e.Bool("is_live"),
            Formats = formats,
        };
    }

    // Best first; WebP last (WPF may not decode it).
    public static List<string> Thumbs(JsonElement e)
    {
        var list = e.Arr("thumbnails")
            .Select(t => new
            {
                Url = t.Str("url"),
                W = t.Int("width") ?? 0,
                Pref = t.Int("preference") ?? -100,
            })
            .Where(t => !string.IsNullOrEmpty(t.Url))
            .OrderBy(t => t.Url!.Contains("webp", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(t => t.Pref)
            .ThenByDescending(t => t.W)
            .Select(t => t.Url!)
            .Distinct()
            .Take(4)
            .ToList();
        if (e.Str("thumbnail") is { } single && !list.Contains(single)) list.Add(single);
        return list;
    }
}

public sealed class MediaItem
{
    public string Title { get; set; } = "";
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? Year { get; set; }
    public int? TrackNo { get; set; }
    public List<string> Thumbnails { get; set; } = new();
    public double? Duration { get; set; }
    public MediaKind Kind { get; set; }
    public SourceKind Source { get; set; }

    // yt-dlp URL, file URL or song page.
    public string Url { get; set; } = "";
    // Source page: Referer and file metadata.
    public string? PageUrl { get; set; }
    public YtInfo? Info { get; set; }

    public string? Ext { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public long? Size { get; set; }

    // gallery-dl retry: gallery URL and 1-based index.
    public string? GalleryUrl { get; set; }
    public int GalleryIndex { get; set; }

    public string? CoverUrl { get; set; }
    // Placeholder title, replaced after analysis.
    public bool TitleIsGuess { get; set; }
    public string? SpotifyTrackId { get; set; }
    public string? FileNameHint { get; set; }
    public string SiteName { get; set; } = "";

    public string? Thumbnail => Thumbnails.FirstOrDefault();
}

public sealed class AnalysisResult
{
    public string Title { get; set; } = "";
    public string? Uploader { get; set; }
    public string Site { get; set; } = "";
    public List<string> Thumbnails { get; set; } = new();
    public bool IsCollection { get; set; }
    public List<MediaItem> Items { get; set; } = new();
    public string SourceUrl { get; set; } = "";
    // A video inside a playlist: offer the whole list.
    public bool PartOfPlaylist { get; set; }
    // Music services start in audio-only mode.
    public bool PreferAudio { get; set; }
    public List<string> Notes { get; set; } = new();
}

public enum VideoCompat { Auto, Original, H264 }

public sealed record VideoOptions(string Container = "mp4", int? Res = null, double? Fps = null);
public sealed record AudioOptions(string Format = "mp3", int? Kbps = null, int? SampleRate = null, int? Channels = null, int BitDepth = 16);
public sealed record ImageOptions(string Format = "original", int? MaxSide = null);

public sealed record DownloadOptions
{
    public bool AudioOnly { get; init; }
    public VideoOptions Video { get; init; } = new();
    public AudioOptions Audio { get; init; } = new();
    public ImageOptions Image { get; init; } = new();
    public VideoCompat Compat { get; init; } = VideoCompat.Auto;
    public bool EmbedMetadata { get; init; } = true;
    public string? CookiesBrowser { get; init; }
}

public static class Codecs
{
    // "avc1.64002a", "h264" and friends to one name.
    public static string Family(string? c)
    {
        if (string.IsNullOrEmpty(c) || c == "none") return c ?? "";
        c = c.ToLowerInvariant();
        if (c is "mp3" or "mp4a.6b" or "mp4a.69" or "mp4a.40.34") return "mp3";
        if (c.StartsWith("avc") || c == "h264") return "h264";
        if (c.StartsWith("hvc") || c.StartsWith("hev") || c is "h265" or "hevc") return "hevc";
        if (c.StartsWith("av01") || c == "av1") return "av1";
        if (c.StartsWith("vp09") || c.StartsWith("vp9")) return "vp9";
        if (c.StartsWith("vp08") || c == "vp8") return "vp8";
        if (c.StartsWith("mp4a") || c.StartsWith("aac")) return "aac";
        if (c.StartsWith("opus")) return "opus";
        if (c.StartsWith("vorbis")) return "vorbis";
        if (c.StartsWith("flac")) return "flac";
        if (c.StartsWith("alac")) return "alac";
        if (c.StartsWith("pcm") || c == "wav") return "pcm";
        if (c is "ac-3" or "ac3") return "ac3";
        if (c is "ec-3" or "eac3") return "eac3";
        if (c.StartsWith("mp4v") || c == "mpeg4") return "mpeg4";
        return c;
    }

    public static bool VideoFits(string container, string family, VideoCompat compat)
    {
        if (compat == VideoCompat.H264 && container is "mp4" or "mov" or "mkv") return family == "h264";
        return container switch
        {
            "mp4" => family is "h264" or "hevc" or "av1" or "vp9" or "mpeg4",
            "mov" => family is "h264" or "hevc" or "mpeg4" or "prores",
            "webm" => family is "vp9" or "vp8" or "av1",
            "mkv" => true,
            _ => false,
        };
    }

    public static bool AudioFits(string container, string family) => container switch
    {
        "mp4" => family is "aac" or "mp3" or "alac" or "ac3" or "eac3",
        "mov" => family is "aac" or "mp3" or "alac" or "pcm",
        "webm" => family is "opus" or "vorbis",
        "mkv" => true,
        _ => false,
    };

    // Codec of an audio format, "" for original.
    public static string ForAudioFormat(string format) => format switch
    {
        "mp3" => "mp3",
        "m4a" => "aac",
        "opus" => "opus",
        "ogg" => "vorbis",
        "flac" => "flac",
        "wav" => "pcm",
        _ => "",
    };

    // Extension for an audio stream kept as is.
    public static string ExtForAudioFamily(string family) => family switch
    {
        "aac" or "alac" => "m4a",
        "opus" => "opus",
        "vorbis" => "ogg",
        "mp3" => "mp3",
        "flac" => "flac",
        "pcm" => "wav",
        _ => "mka",
    };

    public static string Pretty(string? family) => family switch
    {
        "h264" => "H.264",
        "hevc" => "H.265",
        "av1" => "AV1",
        "vp9" => "VP9",
        "vp8" => "VP8",
        "aac" => "AAC",
        "opus" => "Opus",
        "vorbis" => "Vorbis",
        "mp3" => "MP3",
        "flac" => "FLAC",
        "alac" => "ALAC",
        "pcm" => "PCM",
        null or "" => "?",
        _ => family.ToUpperInvariant(),
    };
}
