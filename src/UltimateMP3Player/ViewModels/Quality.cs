using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// The audio and video qualities to choose from: in Settings, on the download card and in "Download again / quality…".
public static class QualityChoices
{
    public static List<Choice> Audio() => new()
    {
        new(L.T("MP3 · 320 kbps"), Quality.Mp3, L.T("plays everywhere")),
        new("MP3 · 256 kbps", "mp3-256"),
        new("MP3 · 192 kbps", "mp3-192"),
        new("MP3 · 128 kbps", "mp3-128", L.T("takes less space")),
        new(L.T("Original (M4A/Opus)"), Quality.Original, L.T("no conversion")),
    };

    // (null: the best there is)
    public static List<Choice> Video() => new()
    {
        new("360p", 360), new("480p", 480), new("720p", 720), new("1080p (Full HD)", 1080, L.T("recommended|f")), new("1440p (2K)", 1440),
        new("2160p (4K)", 2160), new(L.T("Best available"), null),
    };

    public static Choice Pick(List<Choice> list, object? value) => list.FirstOrDefault(c => Equals(c.Value, value)) ?? list[0];
}

// What "Download again / quality…" asks for some songs: the audio quality, and the video's when they can have one. Saved:
// some are saved on the device (they're downloaded again); the others, in the cloud, only keep it for when they're heard.
public sealed class QualityPlan
{
    public QualityPlan(string title, string subtitle, string audio, int? videoRes, bool canVideo, bool withVideo, bool saved)
    {
        Title = title;
        Subtitle = subtitle;
        Audio = QualityChoices.Pick(AudioChoices, audio);
        Video = QualityChoices.Pick(VideoChoices, videoRes);
        CanVideo = canVideo;
        WithVideo = withVideo;
        Saved = saved;
    }

    public string Title { get; }
    public string Subtitle { get; }
    public List<Choice> AudioChoices { get; } = QualityChoices.Audio();
    public List<Choice> VideoChoices { get; } = QualityChoices.Video();
    public Choice Audio { get; set; }
    public Choice Video { get; set; }
    public bool CanVideo { get; }
    public bool WithVideo { get; set; }
    public bool Saved { get; }
    public string OkText => Saved ? L.T("Download again") : L.T("Save");
    public string Hint => Saved
        ? L.T("The songs are downloaded again from their link in this quality and take the place of their current files: cover, lyrics, statistics and playlists stay.")
        : L.T("In the cloud: the quality you hear them with from their link, and the one they'll be saved on the device with.");
}
