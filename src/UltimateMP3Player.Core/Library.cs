using System.Text.RegularExpressions;

namespace UltimateMP3Player.Core;

public sealed class Track
{
    public string Id { get; set; } = Ids.New();
    public string Title { get; set; } = "";
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? Year { get; set; }
    public double Duration { get; set; }
    // The audio file saved on the device; "" = not saved (in the cloud: heard from its link, through the temporary
    // cache, and saved again whenever wanted).
    public string Path { get; set; } = "";
    public string? VideoPath { get; set; }
    public string? SourceUrl { get; set; }
    public string? Site { get; set; }
    // Its link no longer works and nothing like it was found elsewhere (shown, skipped when its turn comes).
    public bool Unavailable { get; set; }
    // Same-song identities: "youtube:ID", "spotify:ID", "file:PATH".
    public List<string> Keys { get; set; } = new();
    public bool HasCover { get; set; }
    // Bumped on cover change to refresh caches.
    public int CoverVersion { get; set; }
    // Public cover URL (Discord presence).
    public string? ArtUrl { get; set; }
    // Loudness per bar (0-255) for the waveform.
    public byte[]? Wave { get; set; }
    // Integrated loudness (LUFS) for normalization.
    public double? Loudness { get; set; }
    // Sample peak (dBFS), caps the normalization boost.
    public double? Peak { get; set; }
    // Beats per minute: detected, tapped or typed.
    public double? Bpm { get; set; }
    // Where the beat grid starts (seconds): detected, or set with "1st beat here".
    public double? BeatOffset { get; set; }
    // Lyrics found online (the text is in LyricsStore); null = never searched.
    public LyricsKind? Lyrics { get; set; }
    public DateTime Added { get; set; } = DateTime.Now;

    public bool HasVideo => !string.IsNullOrEmpty(VideoPath);
    [System.Text.Json.Serialization.JsonIgnore] public bool HasLyrics => Lyrics is LyricsKind.Synced or LyricsKind.Plain;
    public bool IsLocal => Site == "File locale";

    // Saved on the device (its file may still have gone missing: AudioPath tells).
    [System.Text.Json.Serialization.JsonIgnore] public bool IsSaved => Path.Length > 0;
    // A copy in the temporary cache, while there is one (a song in the cloud just heard, or about to be).
    [System.Text.Json.Serialization.JsonIgnore] public string? CachePath { get; set; }
    // The file to play: the saved one, otherwise the cache's; null = it has to be fetched from its link first.
    [System.Text.Json.Serialization.JsonIgnore] public string? AudioPath =>
        IsSaved && File.Exists(Path) ? Path : CachePath is { Length: > 0 } c && File.Exists(c) ? c : null;
    // Its saved file is really there.
    [System.Text.Json.Serialization.JsonIgnore] public bool HasSavedFile => IsSaved && File.Exists(Path);
    // A web page it can be heard (and saved) from again.
    [System.Text.Json.Serialization.JsonIgnore] public bool HasLink => SourceUrl is { } u && u.StartsWith("http", StringComparison.OrdinalIgnoreCase);
    // Its audio can go back to the cloud: a link to get it again, and a file of the app's (not one of the computer's own
    // folders, added from there).
    [System.Text.Json.Serialization.JsonIgnore] public bool CanUnsave => IsSaved && HasLink && !IsLocal;
    // A separate video file (not the audio's own file, as for a video added from the computer).
    [System.Text.Json.Serialization.JsonIgnore] public bool HasOwnVideo => HasVideo && !string.Equals(VideoPath, Path, StringComparison.OrdinalIgnoreCase);
    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? L.T("Artista sconosciuto") : Artist!;
}

public static class Ids
{
    public static string New() => Guid.NewGuid().ToString("N")[..12];
}

public sealed class LibraryFile
{
    public List<Track> Tracks { get; set; } = new();
}

// All songs, shared by profiles; thread-safe.
public sealed class Library
{
    private readonly object _lock = new();
    private readonly List<Track> _tracks;
    private readonly Dictionary<string, Track> _byId = new();
    private readonly Dictionary<string, Track> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly DebouncedSaver _saver;

    public event Action<Track>? TrackAdded;
    public event Action<Track>? TrackChanged;
    public event Action<Track>? TrackRemoved;

    private Library(List<Track> tracks)
    {
        _tracks = tracks;
        foreach (var t in tracks) Index(t);
        _saver = new DebouncedSaver(SaveNow, 1500);
    }

    public static Library Load()
    {
        var file = JsonStore.Load<LibraryFile>(AppPaths.LibraryFile);
        foreach (var t in file.Tracks) t.Path ??= "";
        // A song in the cloud has no file, only its link.
        var tracks = file.Tracks.Where(t => !string.IsNullOrEmpty(t.Id) && (t.IsSaved || t.HasLink))
            .GroupBy(t => t.Id).Select(g => g.First()).ToList();
        return new Library(tracks);
    }

    private void Index(Track t)
    {
        _byId[t.Id] = t;
        foreach (var k in t.Keys) _byKey.TryAdd(k, t);
    }

    public int Count
    {
        get { lock (_lock) return _tracks.Count; }
    }

    public List<Track> Snapshot()
    {
        lock (_lock) return _tracks.ToList();
    }

    public Track? Get(string id)
    {
        lock (_lock) return _byId.GetValueOrDefault(id);
    }

    public Track? FindByKeys(IEnumerable<string> keys)
    {
        lock (_lock)
        {
            foreach (var k in keys)
                if (_byKey.TryGetValue(k, out var t)) return t;
        }
        return null;
    }

    // Same song from another source (title, artist, ±4 s).
    public Track? FindSimilar(string title, string? artist, double? duration)
    {
        if (duration is not > 0) return null;
        var wanted = SongMeta.Identity(title, artist);
        if (wanted.Titles.Count == 0 || wanted.Artists.Count == 0) return null;
        lock (_lock)
        {
            return _tracks.FirstOrDefault(x =>
                Math.Abs(x.Duration - duration.Value) <= 4 &&
                SongMeta.SameSong(wanted, SongMeta.Identity(x.Title, x.Artist)));
        }
    }

    // Renames an artist on every song, returns them.
    public List<Track> RenameArtist(string from, string to)
    {
        List<Track> hit;
        lock (_lock)
        {
            hit = _tracks.Where(t => string.Equals(t.Artist?.Trim(), from.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList();
            foreach (var t in hit) t.Artist = string.IsNullOrWhiteSpace(to) ? null : to.Trim();
        }
        if (hit.Count == 0) return hit;
        _saver.Request();
        foreach (var t in hit) TrackChanged?.Invoke(t);
        return hit;
    }

    public Track? FindByPath(string path)
    {
        lock (_lock)
            return _tracks.FirstOrDefault(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    public void Add(Track t)
    {
        lock (_lock)
        {
            if (_byId.ContainsKey(t.Id)) return;
            _tracks.Add(t);
            Index(t);
        }
        _saver.Request();
        TrackAdded?.Invoke(t);
    }

    public void AddKeys(Track t, IEnumerable<string> keys)
    {
        lock (_lock)
        {
            foreach (var k in keys)
            {
                if (t.Keys.Contains(k, StringComparer.OrdinalIgnoreCase)) continue;
                t.Keys.Add(k);
                _byKey.TryAdd(k, t);
            }
        }
        _saver.Request();
    }

    public void Changed(Track t)
    {
        _saver.Request();
        TrackChanged?.Invoke(t);
    }

    public void Remove(Track t)
    {
        lock (_lock)
        {
            if (!_tracks.Remove(t)) return;
            _byId.Remove(t.Id);
            foreach (var k in t.Keys)
                if (_byKey.TryGetValue(k, out var x) && x == t) _byKey.Remove(k);
        }
        _saver.Request();
        TrackRemoved?.Invoke(t);
    }

    public void SaveNow()
    {
        LibraryFile copy;
        lock (_lock) copy = new LibraryFile { Tracks = _tracks.ToList() };
        JsonStore.Save(AppPaths.LibraryFile, copy);
    }

    public void Flush() => _saver.Flush();
}

public sealed record SongIdentity(HashSet<string> Titles, HashSet<string> Artists);

// Clean metadata from "Artist - Song (Official Video)" titles.
public static class SongMeta
{
    private static readonly Regex Noise = new(
        @"\s*[\(\[【]\s*[^\)\]】]*?\b(official|video|audio|lyrics?|visuali[sz]er|videoclip|clip|testo|hd|hq|4k|mv|m/v|music\s*video|video\s*ufficiale|ufficiale)\b[^\)\]】]*[\)\]】]",
        RegexOptions.IgnoreCase);

    private static readonly Regex ArtistTitle = new(@"^(.{1,80}?)\s+[-–—]\s+(.+)$");

    public static string CleanTitle(string title)
    {
        var t = Noise.Replace(title, "");
        t = Regex.Replace(t, @"\s*\|\s*.*\b(official|video|audio|lyrics?)\b.*$", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s+", " ").Trim(' ', '-', '|');
        return t.Length > 0 ? t : title.Trim();
    }

    public static string? CleanChannel(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel)) return null;
        var c = Regex.Replace(channel, @"\s*-\s*Topic$", "", RegexOptions.IgnoreCase);
        c = Regex.Replace(c, @"VEVO$", "", RegexOptions.IgnoreCase);
        c = Regex.Replace(c, @"\s*(Official|Ufficiale)(\s+(Channel|Canale))?$", "", RegexOptions.IgnoreCase);
        return c.Trim().Length > 0 ? c.Trim() : channel.Trim();
    }

    // Title, artist, album and year for tags and library.
    public static (string Title, string? Artist, string? Album, string? Year) From(MediaItem item)
    {
        var info = item.Info;
        if (item.Source == SourceKind.Search)
            return (item.Title, item.Artist, item.Album ?? info?.Album, item.Year ?? info?.Year);
        if (info?.Track != null)
            return (info.Track, info.Artist ?? item.Artist ?? CleanChannel(info.Uploader), info.Album ?? item.Album, item.Year ?? info.Year);

        var title = item.TitleIsGuess && info != null ? info.Title : Text.FirstNonEmpty(item.Title, info?.Title) ?? L.T("Brano");
        var artist = item.Artist ?? info?.Artist;
        if (ArtistTitle.Match(title) is { Success: true } m &&
            (artist == null || ArtistKey(m.Groups[1].Value) == ArtistKey(artist)))
        {
            artist ??= m.Groups[1].Value.Trim();
            title = m.Groups[2].Value.Trim();
        }
        artist ??= CleanChannel(info?.Uploader);
        return (CleanTitle(title), artist, item.Album ?? info?.Album, item.Year ?? info?.Year);
    }

    // "Artist - Song" also counts as "Song" by "Artist".
    public static SongIdentity Identity(string? title, string? artist)
    {
        var titles = new HashSet<string>();
        var artists = new HashSet<string>();
        if (MatchKey(title) is { Length: > 0 } full) titles.Add(full);
        if (title != null && ArtistTitle.Match(CleanTitle(title)) is { Success: true } m)
        {
            if (MatchKey(m.Groups[2].Value) is { Length: > 0 } rest) titles.Add(rest);
            if (ArtistKey(m.Groups[1].Value) is { Length: > 0 } a) artists.Add(a);
        }
        if (!string.IsNullOrWhiteSpace(artist))
            foreach (var part in Regex.Split(artist, @",|&| x | feat\.? | ft\.? ", RegexOptions.IgnoreCase))
                if (Text.Normalize(CleanChannel(part)) is { Length: > 0 } a) artists.Add(a);
        return new SongIdentity(titles, artists);
    }

    public static bool SameSong(SongIdentity a, SongIdentity b)
        => a.Titles.Overlaps(b.Titles) && a.Artists.Overlaps(b.Artists);

    public static string MatchKey(string? title)
    {
        var t = CleanTitle(title ?? "");
        t = Regex.Replace(t, @"\s*[\(\[]\s*(feat|ft|with)\.?\s[^\)\]]*[\)\]]", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s+(feat|ft)\.?\s.*$", "", RegexOptions.IgnoreCase);
        return Text.Normalize(t);
    }

    // The same song, whatever the site: first artist and clean title.
    public static string Key(string? title, string? artist) => ArtistKey(artist) + "|" + MatchKey(title);

    public static string ArtistKey(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return "";
        var first = Regex.Split(artist, @",|&| x | feat\.? | ft\.? ", RegexOptions.IgnoreCase)[0];
        return Text.Normalize(CleanChannel(first));
    }
}

// Stable site identities, to never download twice.
public static class SourceKeys
{
    private static readonly Regex YouTubeId = new(@"(?:[?&]v=|youtu\.be/|/shorts/|/embed/|/live/)([\w-]{11})(?![\w-])", RegexOptions.IgnoreCase);
    private static readonly Regex DeezerId = new(@"deezer\.com/(?:[a-z]{2}(?:-[a-z]{2})?/)?track/(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex AppleId = new(@"apple\.com/(?:[a-z]{2}/)?(?:song/(?:[^/?#]+/)?(\d+)|album/[^?#]*[?&]i=(\d+))", RegexOptions.IgnoreCase);

    public static List<string> ForItem(MediaItem item)
    {
        var keys = new List<string>();
        if (item.SpotifyTrackId != null) keys.Add("spotify:" + item.SpotifyTrackId);
        foreach (var url in new[] { item.Url, item.PageUrl }.Where(u => !string.IsNullOrEmpty(u)).Distinct())
        {
            if (YouTubeId.Match(url!) is { Success: true } y) keys.Add("youtube:" + y.Groups[1].Value);
            else if (DeezerId.Match(url!) is { Success: true } d) keys.Add("deezer:" + d.Groups[1].Value);
            else if (AppleId.Match(url!) is { Success: true } a) keys.Add("applemusic:" + (a.Groups[1].Success ? a.Groups[1] : a.Groups[2]).Value);
            else if (item.Source != SourceKind.Direct && UrlKey(url!) is { } u) keys.Add(u);
        }
        if (item.GalleryUrl != null && UrlKey(item.GalleryUrl) is { } g) keys.Add(g + "#" + item.GalleryIndex);
        if (item.Info is { } info) keys.AddRange(ForInfo(info));
        return keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<string> ForInfo(YtInfo info)
    {
        var keys = new List<string>();
        if (info.Id.Length > 0 && info.Extractor.Length > 0)
        {
            var ex = info.Extractor.ToLowerInvariant();
            if (ex.StartsWith("youtube")) ex = "youtube";
            keys.Add($"{ex}:{info.Id}");
        }
        if (UrlKey(info.WebpageUrl) is { } u && !u.Contains("youtube.com")) keys.Add(u);
        return keys;
    }

    public static string FileKey(string path) => "file:" + System.IO.Path.GetFullPath(path).ToLowerInvariant();

    private static string? UrlKey(string url)
    {
        try
        {
            var uri = new Uri(url);
            if (uri.Scheme is not ("http" or "https")) return null;
            var host = Sites.HostOf(url);
            var path = uri.AbsolutePath.TrimEnd('/').ToLowerInvariant();
            if (path.Length <= 1) return null;
            return $"url:{host}{path}";
        }
        catch { return null; }
    }
}
