using System.IO;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// What "Delete…" does to the chosen songs.
public enum RemoveKind { Video, Lyrics, Device, Everything }

// One choice of the window: what it does, to how many of the songs, and the space it frees.
public sealed class RemoveOption : Observable
{
    public RemoveOption(RemoveKind kind, string title, string detail, string? size, bool enabled)
    {
        Kind = kind;
        Title = title;
        Detail = detail;
        Size = size;
        Enabled = enabled;
    }

    public RemoveKind Kind { get; }
    public string Title { get; }
    public string Detail { get; }
    public string? Size { get; }
    public bool HasSize => !string.IsNullOrEmpty(Size);
    public bool Enabled { get; }
    public bool Danger => Kind == RemoveKind.Everything;

    private bool _isChosen;
    public bool IsChosen { get => _isChosen; set => Set(ref _isChosen, value); }
}

// The choices "Delete…" offers for these songs (one or many), worked out from what they have: the video and the lyrics
// alone, the audio from the device (they stay in the cloud, when there's a link to hear them from), or everything.
public sealed class RemovePlan
{
    public RemovePlan(IReadOnlyList<TrackViewModel> tracks)
    {
        Tracks = tracks;
        var main = tracks[0].Main;
        bool one = tracks.Count == 1;
        var lib = tracks.Where(t => main.Library.Get(t.Id) == t.T).Select(t => t.T).ToList();

        var videos = lib.Where(t => t.HasOwnVideo).ToList();
        int sameFile = lib.Count(t => t.HasVideo && !t.HasOwnVideo);
        long videoBytes = videos.Sum(t => SizeOf(t.VideoPath));
        string videoDetail = videos.Count > 0
            ? one ? L.T("L'audio resta: senti il brano come prima, senza il video.")
                : L.F("{0} hanno un video: resta l'audio.", L.Count(videos.Count, "1 brano", "{0} brani"))
            : sameFile > 0 ? L.T("Il video è nello stesso file dell'audio (aggiunto dal computer): non si può togliere da solo.")
            : one ? L.T("Il brano non ha un video.") : L.T("Nessuno di questi brani ha un video.");

        int lyrics = tracks.Count(t => t.HasLyrics);
        string lyricsDetail = lyrics > 0
            ? (one ? L.T("Lo puoi cercare di nuovo online quando vuoi.") : L.F("{0} hanno il testo: li puoi cercare di nuovo online quando vuoi.", L.Count(lyrics, "1 brano", "{0} brani")))
            : one ? L.T("Il brano non ha un testo.") : L.T("Nessuno di questi brani ha un testo.");

        var device = lib.Where(t => t.CanUnsave).ToList();
        int cloud = tracks.Count(t => !t.T.IsSaved);
        int noLink = lib.Count(t => t.IsSaved && !t.CanUnsave);
        long deviceBytes = device.Sum(t => SizeOf(t.Path) + (t.HasOwnVideo ? SizeOf(t.VideoPath) : 0));
        string deviceDetail;
        if (device.Count == 0)
            deviceDetail = one
                ? !tracks[0].T.IsSaved ? L.T("Non è salvato sul dispositivo: lo ascolti già dal suo link.")
                    : L.T("Non ha un link da cui ascoltarlo di nuovo (un file aggiunto dal dispositivo o un mix del DJ): toglierlo vorrebbe dire perderlo.")
                : L.T("Nessuno di questi brani si può togliere: non sono salvati, oppure non hanno un link da cui ascoltarli di nuovo.");
        else
        {
            deviceDetail = L.T("Resta nella libreria, nelle playlist, nei Preferiti e nelle statistiche con la nuvola: lo ascolti dal suo link e lo salvi di nuovo quando vuoi.");
            if (!one && noLink + cloud > 0)
                deviceDetail += " " + L.F("Vale per {0}: gli altri non sono salvati o non hanno un link.", L.Count(device.Count, "1 brano", "{0} brani"));
        }

        bool anyLocal = lib.Any(t => t.IsLocal);
        long allBytes = lib.Where(t => !t.IsLocal).Sum(t => SizeOf(t.Path) + (t.HasOwnVideo ? SizeOf(t.VideoPath) : 0));
        bool onlyHeard = lib.Count == 0;
        string allDetail = onlyHeard
            ? L.T("Sparisce dagli ascoltati di recente e dalle statistiche.")
            : L.T("Sparisce anche dalle playlist, dai Preferiti e dalle statistiche di tutti i profili.") +
              (anyLocal ? " " + L.T("I file aggiunti dal dispositivo restano nella loro cartella.") : "");

        Options = new List<RemoveOption>
        {
            new(RemoveKind.Video, L.T("Solo il video"), videoDetail, Text.Size(videoBytes), videos.Count > 0),
            new(RemoveKind.Lyrics, L.T("Solo il testo"), lyricsDetail, null, lyrics > 0),
            new(RemoveKind.Device, L.T("Dal dispositivo"), deviceDetail, Text.Size(deviceBytes), device.Count > 0),
            new(RemoveKind.Everything, L.T("Completamente"), allDetail, Text.Size(allBytes), true),
        };
        var first = Options.First(o => o.Kind == (device.Count > 0 ? RemoveKind.Device : RemoveKind.Everything));
        first.IsChosen = true;
    }

    public IReadOnlyList<TrackViewModel> Tracks { get; }
    public List<RemoveOption> Options { get; }
    public TrackViewModel First => Tracks[0];
    public string Title => Tracks.Count == 1 ? L.T("Cosa vuoi eliminare?") : L.F("Cosa vuoi eliminare di {0} brani?", Tracks.Count);
    public string Subtitle => Tracks.Count == 1 ? $"{Tracks[0].Title} · {Tracks[0].Artist}" : string.Join(", ", Tracks.Take(3).Select(t => t.Title)) + (Tracks.Count > 3 ? "…" : "");
    public RemoveOption? Chosen => Options.FirstOrDefault(o => o.IsChosen && o.Enabled);

    // The button: what pressing it does.
    public string ButtonText(RemoveOption? o) => o?.Kind switch
    {
        RemoveKind.Video => L.T("Elimina il video"),
        RemoveKind.Lyrics => L.T("Elimina il testo"),
        RemoveKind.Device => L.T("Togli dal dispositivo"),
        RemoveKind.Everything => L.T("Elimina completamente"),
        _ => L.T("Elimina"),
    };

    private static long SizeOf(string? path)
    {
        try { return !string.IsNullOrEmpty(path) && File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }
}
