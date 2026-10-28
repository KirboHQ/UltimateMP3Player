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
        foreach (var path in paths)
        {
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(path);
                foreach (var e in zip.Entries.Where(e => IsHistory(e.FullName)))
                {
                    using var s = e.Open();
                    plays.AddRange(Parse(s));
                }
            }
            else
            {
                using var s = File.OpenRead(path);
                plays.AddRange(Parse(s));
            }
        }
        return plays.OrderBy(p => p.At).ToList();
    }

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
    {
        static string Cell(string? s) => (s ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        return "Track Name\tArtist Name(s)\tTrack URI\n" + string.Join("\n", songs.Select(s => $"{Cell(s.Title)}\t{Cell(s.Artist)}\t{Cell(s.Url)}"));
    }
}
