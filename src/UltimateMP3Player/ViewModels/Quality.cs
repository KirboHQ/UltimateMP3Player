using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// The audio and video qualities to choose from: in Settings, on the download card and in "Download again / quality…".
public static class QualityChoices
{
    public static List<Choice> Audio() => new()
    {
        new(L.T("MP3 · 320 kbps"), Quality.Mp3, L.T("si apre ovunque")),
        new("MP3 · 256 kbps", "mp3-256"),
        new("MP3 · 192 kbps", "mp3-192"),
        new("MP3 · 128 kbps", "mp3-128", L.T("occupa meno spazio")),
        new(L.T("Originale (M4A/Opus)"), Quality.Original, L.T("nessuna conversione")),
    };

    // (null: the best there is)
    public static List<Choice> Video() => new()
    {
        new("360p", 360), new("480p", 480), new("720p", 720), new("1080p (Full HD)", 1080, L.T("consigliata")), new("1440p (2K)", 1440),
        new("2160p (4K)", 2160), new(L.T("Massima disponibile"), null),
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
    public string OkText => Saved ? L.T("Scarica di nuovo") : L.T("Salva");
    public string Hint => Saved
        ? L.T("I brani si scaricano di nuovo dal loro link in questa qualità e prendono il posto dei file di adesso: copertina, testo, statistiche e playlist restano.")
        : L.T("Nel cloud: la qualità con cui li ascolti dal loro link, e con cui verranno salvati sul dispositivo.");
}
