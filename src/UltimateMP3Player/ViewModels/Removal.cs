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
            ? one ? L.T("The audio stays: you hear the song as before, without the video.")
                : L.F("{0} have a video: the audio stays.", L.Count(videos.Count, "1 song", "{0} songs"))
            : sameFile > 0 ? L.T("The video is in the same file as the audio (added from the computer): it can't be removed on its own.")
            : one ? L.T("The song has no video.") : L.T("None of these songs has a video.");

        int lyrics = tracks.Count(t => t.HasLyrics);
        string lyricsDetail = lyrics > 0
            ? (one ? L.T("You can look it up online again whenever you like.") : L.F("{0} have lyrics: you can look them up online again whenever you like.", L.Count(lyrics, "1 song", "{0} songs")))
            : one ? L.T("The song has no lyrics.") : L.T("None of these songs has lyrics.");

        var device = lib.Where(t => t.CanUnsave).ToList();
        int cloud = tracks.Count(t => !t.T.IsSaved);
        int noLink = lib.Count(t => t.IsSaved && !t.CanUnsave);
        long deviceBytes = device.Sum(t => SizeOf(t.Path) + (t.HasOwnVideo ? SizeOf(t.VideoPath) : 0));
        string deviceDetail;
        if (device.Count == 0)
            deviceDetail = one
                ? !tracks[0].T.IsSaved ? L.T("It isn't saved on your device: you already listen to it from its link.")
                    : L.T("It has no link to play it from again (a file added from the device or a DJ mix): removing it would mean losing it.")
                : L.T("None of these songs can be removed: they aren't saved, or they have no link to play them from again.");
        else
        {
            deviceDetail = L.T("It stays in your library, playlists, Favorites and statistics with the cloud icon: you listen from its link and save it again whenever you like.");
            if (!one && noLink + cloud > 0)
                deviceDetail += " " + L.F("Applies to {0}: the others aren't saved or have no link.", L.Count(device.Count, "1 song", "{0} songs"));
        }

        bool anyLocal = lib.Any(t => t.IsLocal);
        long allBytes = lib.Where(t => !t.IsLocal).Sum(t => SizeOf(t.Path) + (t.HasOwnVideo ? SizeOf(t.VideoPath) : 0));
        bool onlyHeard = lib.Count == 0;
        string allDetail = onlyHeard
            ? L.T("It disappears from recently played and from statistics.")
            : L.T("It also disappears from playlists, Favorites and statistics of every profile.") +
              (anyLocal ? " " + L.T("Files added from the device stay in their folder.") : "");

        Options = new List<RemoveOption>
        {
            new(RemoveKind.Video, L.T("Video only"), videoDetail, Text.Size(videoBytes), videos.Count > 0),
            new(RemoveKind.Lyrics, L.T("Lyrics only"), lyricsDetail, null, lyrics > 0),
            new(RemoveKind.Device, L.T("From the device"), deviceDetail, Text.Size(deviceBytes), device.Count > 0),
            new(RemoveKind.Everything, L.T("Completely"), allDetail, Text.Size(allBytes), true),
        };
        var first = Options.First(o => o.Kind == (device.Count > 0 ? RemoveKind.Device : RemoveKind.Everything));
        first.IsChosen = true;
    }

    public IReadOnlyList<TrackViewModel> Tracks { get; }
    public List<RemoveOption> Options { get; }
    public TrackViewModel First => Tracks[0];
    public string Title => Tracks.Count == 1 ? L.T("What do you want to delete?") : L.F("What do you want to delete from {0} songs?", Tracks.Count);
    public string Subtitle => Tracks.Count == 1 ? $"{Tracks[0].Title} · {Tracks[0].Artist}" : string.Join(", ", Tracks.Take(3).Select(t => t.Title)) + (Tracks.Count > 3 ? "…" : "");
    public RemoveOption? Chosen => Options.FirstOrDefault(o => o.IsChosen && o.Enabled);

    // The button: what pressing it does.
    public string ButtonText(RemoveOption? o) => o?.Kind switch
    {
        RemoveKind.Video => L.T("Delete video"),
        RemoveKind.Lyrics => L.T("Delete lyrics"),
        RemoveKind.Device => L.T("Remove from device"),
        RemoveKind.Everything => L.T("Delete completely"),
        _ => L.T("Delete"),
    };

    private static long SizeOf(string? path)
    {
        try { return !string.IsNullOrEmpty(path) && File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }
}
