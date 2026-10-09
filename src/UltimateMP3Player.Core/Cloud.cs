using System.Text.RegularExpressions;

namespace UltimateMP3Player.Core;

// Songs in the cloud: in the library (playlists, favourites, statistics, lyrics, cover) without their audio on the device.
// They're heard from their link through the temporary cache (like the suggested songs), and saved whenever wanted.
public static class CloudSongs
{
    private static readonly SemaphoreSlim CoverGate = new(3);

    // A song of an analyzed link as a download would describe it (title and artist cleaned the same way), without its audio.
    public static Track FromItem(MediaItem item)
    {
        var meta = SongMeta.From(item);
        return new Track
        {
            Title = meta.Title,
            Artist = meta.Artist,
            Album = meta.Album,
            Year = meta.Year,
            Duration = item.Duration ?? item.Info?.Duration ?? 0,
            SourceUrl = item.PageUrl ?? item.Info?.WebpageUrl ?? item.Url,
            Site = item.SiteName,
            Keys = SourceKeys.ForItem(item),
            ArtUrl = TrackDownloader.PublicArt(item),
        };
    }

    // A suggested song (or one heard from a link, or in a room) kept in the library without its audio.
    public static Track FromRadio(RadioSong s, double duration) => new()
    {
        Title = s.Title,
        Artist = s.Artist,
        Album = s.Album,
        Duration = duration > 0 ? duration : s.Duration,
        SourceUrl = s.Url,
        Site = s.Service,
        Keys = s.Keys.ToList(),
    };

    // The pictures of an item, the best first.
    public static List<string> Pictures(MediaItem item) =>
        RadioSong.Pictures(item).Concat(item.Info?.Thumbnails ?? new List<string>()).Distinct().ToList();

    // The site's picture made square like the covers of the library: the first of these that can be had. A few at a time
    // (a whole playlist added to the cloud asks for hundreds).
    public static async Task<string?> MakeCoverAsync(string trackId, IReadOnlyList<string> pictures, CancellationToken ct = default)
    {
        if (pictures.Count == 0) return null;
        await CoverGate.WaitAsync(ct);
        try
        {
            return await Task.Run(async () =>
            {
                var path = AppPaths.TrackCover(trackId);
                var tmp = Path.Combine(AppPaths.TempDir, trackId + "-" + Ids.New() + ".thumb");
                foreach (var url in pictures.Take(4))
                {
                    try
                    {
                        await File.WriteAllBytesAsync(tmp, await Http.GetBytesAsync(url, ct: ct), ct);
                        if (await AudioAnalysis.MakeCoverFromImageAsync(tmp, path, ct)) return url;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { }
                    finally
                    {
                        try { File.Delete(tmp); } catch { }
                    }
                }
                return null;
            }, ct);
        }
        finally { CoverGate.Release(); }
    }

    // Where to hear it from. A song found through a catalogue (Spotify, Deezer, Apple Music) that was already matched on
    // YouTube is heard from there, without searching for it again every time.
    public static string StreamUrl(string url, IEnumerable<string> keys)
    {
        if (!(Spotify.IsMatch(url) || Deezer.IsMatch(url) || AppleMusic.IsMatch(url))) return url;
        var yt = keys.FirstOrDefault(k => k.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase));
        return yt != null ? "https://music.youtube.com/watch?v=" + yt["youtube:".Length..] : url;
    }

    // Its audio to download (saving it on the device) from the link it's heard from. A song of a catalogue not matched on
    // YouTube yet is searched there by title, artist and length, as when its link was first downloaded.
    public static MediaItem ToMediaItem(Track t)
    {
        var url = t.SourceUrl ?? "";
        var stream = StreamUrl(url, t.Keys);
        bool catalog = stream == url && (Spotify.IsMatch(url) || Deezer.IsMatch(url) || AppleMusic.IsMatch(url));
        var item = new MediaItem
        {
            Title = t.Title,
            Artist = t.Artist,
            Album = t.Album,
            Year = t.Year,
            Duration = t.Duration > 0 ? t.Duration : null,
            Kind = MediaKind.Audio,
            Source = catalog ? SourceKind.Search : SourceKind.YtDlp,
            Url = stream,
            PageUrl = url,
            SpotifyTrackId = catalog ? t.Keys.FirstOrDefault(k => k.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))?["spotify:".Length..] : null,
            CoverUrl = t.ArtUrl,
            SiteName = !string.IsNullOrEmpty(t.Site) ? t.Site : Sites.NameFor(url),
        };
        if (t.ArtUrl != null) item.Thumbnails.Add(t.ArtUrl);
        return item;
    }

    // What the cache needs to get a library song's audio (its id stays the library's).
    public static RadioSong StreamOf(Track t) => new()
    {
        Id = t.Id,
        Title = t.Title,
        Artist = t.Artist,
        Album = t.Album,
        Duration = t.Duration,
        Url = StreamUrl(t.SourceUrl ?? "", t.Keys),
        Service = string.IsNullOrEmpty(t.Site) ? Sites.NameFor(t.SourceUrl ?? "") : t.Site,
        Keys = t.Keys.ToList(),
        FromLink = true,
    };

    // The same song on YouTube Music (or YouTube) when its own link no longer works: only a close match, null when there's
    // none. A connection that fails throws (a song isn't given up on just because the connection dropped).
    public static Task<YtInfo?> FindElsewhereAsync(string title, string? artist, double duration, string? cookies, CancellationToken ct)
        => MusicMatcher.FindCloseAsync(new MediaItem
        {
            Title = title,
            Artist = artist,
            Duration = duration > 0 ? duration : null,
            Kind = MediaKind.Audio,
            Source = SourceKind.Search,
            SiteName = "YouTube Music",
        }, cookies, ct);
}

// A video on YouTube for a song saved without one.
public sealed record VideoCandidate(string Url, string Title, string? Channel, double? Duration, string? Thumb, double Score)
{
    // Not the same length as the song: the video plays in time with the audio, a different version would drift.
    public bool LengthDiffers(double songDuration) => songDuration > 0 && Duration is double d && Math.Abs(d - songDuration) > 4;
}

public static class VideoFinder
{
    // The song's own link is a YouTube video (YouTube Music's tracks are often only the cover as a picture: those are searched).
    public static string? OwnVideo(Track t)
    {
        if (t.SourceUrl is not { } url) return null;
        var host = Sites.HostOf(url);
        return host is "youtube.com" or "youtu.be" ? url : null;
    }

    // The videos on YouTube that look like this song, the best first.
    public static async Task<List<VideoCandidate>> SearchAsync(string title, string? artist, double duration, CancellationToken ct)
    {
        var who = artist?.Split(',')[0].Trim() ?? "";
        var hits = await OnlineSearchServices.SearchAsync(OnlineSearchServices.YouTube, $"{who} {title}".Trim(), 12, ct);
        var clean = Regex.Replace(title, @"\s*[\(\[].*?[\)\]]", "").Trim();
        return hits
            .Select((h, i) =>
            {
                double s = MusicMatcher.TitleScore(clean.Length > 0 ? clean : title, who, h.Title) - i * 0.03;
                if (who.Length > 0) s += Text.Coverage(who, h.Title + " " + h.Artist) >= 0.5 ? 0.3 : -0.2;
                if (duration > 0 && h.Duration is double d) s += Math.Abs(d - duration) <= 4 ? 0.4 : Math.Abs(d - duration) <= 15 ? 0 : -0.3;
                if (Regex.IsMatch(h.Title, @"\b(official\s+(music\s+)?video|video\s+ufficiale|videoclip|\bmv\b)", RegexOptions.IgnoreCase)) s += 0.15;
                return new VideoCandidate(h.Url, h.Title, h.Artist, h.Duration, h.Thumb, s);
            })
            .OrderByDescending(c => c.Score)
            .ToList();
    }
}
