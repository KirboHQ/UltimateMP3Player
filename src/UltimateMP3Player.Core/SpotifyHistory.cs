using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace UltimateMP3Player.Core;

// A play from another service, ready for a profile's statistics (Profile.AddImported).
public sealed record ImportedPlay(DateTime At, string Id, string? Title, string? Artist, string? Url, double Seconds, bool Play, bool InLibrary);

// A time imported from a service before 4.3 (Profile.AddImported now keeps counts per song and month).
public sealed class ImportedRange
{
    public string Source { get; set; } = "";
    public DateTime From { get; set; }
    public DateTime To { get; set; }
}

// Spotify's listening history (spotify.com › Account › Privacy › Download your data): the extended one
// (Streaming_History_Audio_*.json, every play since the account began, "ts" and "ms_played") or the year's
// (StreamingHistory_music_*.json, "endTime" and "msPlayed"), as its JSON files or the zip Spotify sends. Songs only.
// The "Account data" zip also has the library: the liked songs (YourLibrary.json) and the playlists (Playlist1.json…).
public static class SpotifyHistory
{
    public const string Source = "spotify";
    public const string PrivacyPage = "https://www.spotify.com/account/privacy/";
    // As Spotify counts a stream: half a minute.
    public const double PlaySeconds = 30;

    public sealed record Play(DateTime At, string Title, string Artist, string? SpotifyId, double Seconds);

    public static bool IsHistory(string name)
    {
        name = Path.GetFileName(name);
        return name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
               && (name.StartsWith("Streaming_History_Audio", StringComparison.OrdinalIgnoreCase) || name.StartsWith("StreamingHistory", StringComparison.OrdinalIgnoreCase))
               && !name.Contains("podcast", StringComparison.OrdinalIgnoreCase) && !name.Contains("video", StringComparison.OrdinalIgnoreCase);
    }

    // The plays in the files, the oldest first (a zip: its history files; a .json not named like one is read too).
    public static List<Play> Read(IEnumerable<string> paths)
    {
        var plays = new List<Play>();
        Each(paths, IsHistory, s => plays.AddRange(Parse(s)));
        return plays.OrderBy(p => p.At).ToList();
    }

    // Each file wanted of a zip, or each file chosen by itself (read whatever its name).
    private static void Each(IEnumerable<string> paths, Func<string, bool> wanted, Action<Stream> read)
    {
        foreach (var path in paths)
        {
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(path);
                foreach (var e in zip.Entries.Where(e => wanted(e.FullName)))
                {
                    using var s = e.Open();
                    read(s);
                }
            }
            else
            {
                using var s = File.OpenRead(path);
                read(s);
            }
        }
    }

    // A song of the library: title, artist, album and its Spotify id (none for a file of the device in a playlist).
    public sealed record Song(string Title, string Artist, string? Album, string? SpotifyId);
    public sealed record SavedPlaylist(string Name, List<Song> Songs);

    private static bool IsLibrary(string name)
    {
        name = Path.GetFileName(name);
        return name.Equals("YourLibrary.json", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("Playlist", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    // The liked songs ("tracks" of YourLibrary.json: track, artist, album, uri) and the playlists ("playlists" of
    // Playlist*.json: name, items › track: trackName, artistName, albumName, trackUri; episodes and the device's files skipped).
    public static (List<Song> Liked, List<SavedPlaylist> Playlists) ReadLibrary(IEnumerable<string> paths)
    {
        var liked = new List<Song>();
        var playlists = new List<SavedPlaylist>();
        Each(paths, IsLibrary, s =>
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(s); }
            catch (JsonException) { return; }
            using (doc)
            {
                var root = doc.RootElement;
                foreach (var t in Objects(root, "tracks"))
                    if (SongOf(t.Str("track"), t.Str("artist"), t.Str("album"), t.Str("uri")) is { } song) liked.Add(song);
                foreach (var p in Objects(root, "playlists"))
                {
                    var songs = new List<Song>();
                    foreach (var item in Objects(p, "items"))
                        if (item.TryGetProperty("track", out var t) && t.ValueKind == JsonValueKind.Object
                            && SongOf(t.Str("trackName"), t.Str("artistName"), t.Str("albumName"), t.Str("trackUri")) is { } song) songs.Add(song);
                    if (songs.Count > 0) playlists.Add(new SavedPlaylist(p.Str("name") is { Length: > 0 } name ? name.Trim() : "Spotify", songs));
                }
            }
        });
        return (liked, playlists);
    }

    private static IEnumerable<JsonElement> Objects(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).ToList() : Enumerable.Empty<JsonElement>();

    private static Song? SongOf(string? title, string? artist, string? album, string? uri)
        => string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist) ? null
            : new Song(title.Trim(), artist.Trim(), string.IsNullOrWhiteSpace(album) ? null : album.Trim(),
                uri != null && uri.StartsWith("spotify:track:", StringComparison.Ordinal) ? uri["spotify:track:".Length..] : null);

    public static List<Play> Parse(Stream json)
    {
        var plays = new List<Play>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return plays; }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return plays;
            foreach (var e in doc.RootElement.EnumerateArray())
                if (Read(e) is { } p) plays.Add(p);
        }
        return plays;
    }

    private static Play? Read(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        string? title, artist, id = null;
        double ms;
        DateTime end;
        if (e.Str("ts") is { } ts)
        {
            // (the extended history: podcasts and audiobooks have no track name)
            title = e.Str("master_metadata_track_name");
            artist = e.Str("master_metadata_album_artist_name");
            ms = e.Num("ms_played") ?? 0;
            if (e.Str("spotify_track_uri") is { } uri && uri.StartsWith("spotify:track:", StringComparison.Ordinal)) id = uri["spotify:track:".Length..];
            if (!DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out end)) return null;
        }
        else if (e.Str("endTime") is { } endTime)
        {
            title = e.Str("trackName");
            artist = e.Str("artistName");
            ms = e.Num("msPlayed") ?? 0;
            if (!DateTime.TryParseExact(endTime, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out end))
                return null;
        }
        else return null;
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist) || ms <= 0) return null;
        // (the time is when it stopped: it started that long before, in this device's time)
        return new Play(DateTime.SpecifyKind(end, DateTimeKind.Utc).ToLocalTime().AddMilliseconds(-ms), title.Trim(), artist.Trim(), id, ms / 1000);
    }

    // The plays as a profile counts them: a song of the library by its id (its Spotify id, or the same title and artist),
    // the others by their Spotify id ("sp:…"), with a link to add them.
    public static List<ImportedPlay> Match(List<Play> plays, Library library)
    {
        var byName = new Dictionary<string, Track>();
        foreach (var t in library.Snapshot()) byName.TryAdd(SongMeta.Key(t.Title, t.Artist), t);
        var known = new Dictionary<string, (string Id, bool InLibrary)>();
        var list = new List<ImportedPlay>(plays.Count);
        foreach (var p in plays)
        {
            var key = p.SpotifyId ?? SongMeta.Key(p.Title, p.Artist);
            if (!known.TryGetValue(key, out var match))
            {
                var t = (p.SpotifyId != null ? library.FindByKeys(new[] { "spotify:" + p.SpotifyId }) : null) ?? byName.GetValueOrDefault(SongMeta.Key(p.Title, p.Artist));
                match = known[key] = t != null ? (t.Id, true) : ("sp:" + (p.SpotifyId ?? Text.Normalize(p.Artist + " - " + p.Title)), false);
            }
            list.Add(new ImportedPlay(p.At, match.Id, p.Title, p.Artist, p.SpotifyId != null ? "https://open.spotify.com/track/" + p.SpotifyId : null,
                p.Seconds, p.Seconds >= PlaySeconds, match.InLibrary));
        }
        return list;
    }

    // The songs not in the library, the most played first: what can be added to it.
    public static List<(string Title, string Artist, string? Url, int Plays)> Missing(IEnumerable<ImportedPlay> plays)
        => plays.Where(p => !p.InLibrary && p.Play).GroupBy(p => p.Id)
            .Select(g => (g.First().Title!, g.First().Artist!, g.First().Url, g.Count()))
            .OrderByDescending(x => x.Item4).ToList();

    // Those songs as a list for ListImport (a table: title, artist, Spotify link).
    public static string AsList(IEnumerable<(string Title, string Artist, string? Url, int Plays)> songs)
        => "Track Name\tArtist Name(s)\tTrack URI\n" + string.Join("\n", songs.Select(s => $"{Cell(s.Title)}\t{Cell(s.Artist)}\t{Cell(s.Url)}"));

    // Liked songs or a playlist the same way, with the album (with its Spotify id a song is known at once: nothing to look for).
    public static string AsList(IEnumerable<Song> songs)
        => "Track Name\tArtist Name(s)\tAlbum Name\tTrack URI\n" + string.Join("\n", songs.Select(s =>
            $"{Cell(s.Title)}\t{Cell(s.Artist)}\t{Cell(s.Album)}\t{Cell(s.SpotifyId == null ? null : "spotify:track:" + s.SpotifyId)}"));

    private static string Cell(string? s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}
