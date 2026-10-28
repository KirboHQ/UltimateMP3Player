using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltimateMP3Player.Core;

// An artist the profile follows (Library › Artists): its page, its new releases.
public sealed class FollowedArtist
{
    public string Name { get; set; } = "";
    // Deezer's id: its picture, its releases (null: Deezer doesn't know it, only its name is followed).
    public string? DeezerId { get; set; }
    public string? Picture { get; set; }
    public DateTime Followed { get; set; } = DateTime.Now;
}

// An album, EP or single of a followed artist that came out lately.
public sealed class Release
{
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    // "album", "ep", "single".
    public string Kind { get; set; } = "album";
    public DateTime Date { get; set; }
    public string? Cover { get; set; }
    public string Url { get; set; } = "";
    // Shown already (the dot on Artists, the notification).
    public bool Seen { get; set; }
}

public sealed record ArtistInfo(string Id, string Name, string? Picture, int Fans, int Albums);

// Who sings a song: the names in its artist field ("A, B", "A & B", "A feat. B", "A x B"), and whether a name is one of them.
public static class ArtistNames
{
    private static readonly Regex Split = new(@"\s*(?:,|;|/|&|\+|\bx\b|\bvs\.?|\bfeat\.?|\bft\.?|\bfeaturing\b|\bcon\b|\bwith\b)\s*", RegexOptions.IgnoreCase);

    public static IEnumerable<string> Of(string? artist) => string.IsNullOrWhiteSpace(artist) ? Array.Empty<string>() : Parse(artist).Names;

    public static bool Has(string? artist, string name)
    {
        var key = KeyOf(name);
        return key.Length > 0 && !string.IsNullOrWhiteSpace(artist) && Array.IndexOf(Parse(artist).Keys, key) >= 0;
    }

    // An artist line's names and their keys (the whole line's first), kept: the same lines come back on every song of a library,
    // and an artist's page, the mixes, the search and the pictures go through all of them.
    private static readonly ConcurrentDictionary<string, (string[] Names, string[] Keys)> Parsed = new();

    private static (string[] Names, string[] Keys) Parse(string artist)
    {
        if (Parsed.TryGetValue(artist, out var known)) return known;
        // (a library has some thousands of lines: past that, the oldest ones are no better than the others)
        if (Parsed.Count > 50_000) Parsed.Clear();
        var names = Split.Split(artist).Select(a => a.Trim()).Where(a => a.Length > 0).ToArray();
        return Parsed[artist] = (names, names.Select(Text.Normalize).Prepend(Text.Normalize(artist)).ToArray());
    }

    // The name looked for, normalized once for a whole loop over the library.
    [ThreadStatic] private static string? _lastName, _lastKey;

    private static string KeyOf(string name)
    {
        if (!ReferenceEquals(name, _lastName))
        {
            _lastKey = Text.Normalize(name);
            _lastName = name;
        }
        return _lastKey!;
    }

    // The first name, as the library pages group songs.
    public static string? First(string? artist) => Of(artist).FirstOrDefault();

    // The line with one of its names changed, the others and what's between them as they were ("A feat. B": B → C).
    public static string Replace(string artist, string name, string to)
    {
        var key = Text.Normalize(name);
        if (Text.Normalize(artist) == key) return to;
        var parts = new List<string>();
        int at = 0;
        foreach (Match m in Split.Matches(artist))
        {
            parts.Add(artist[at..m.Index]);
            parts.Add(m.Value);
            at = m.Index + m.Length;
        }
        parts.Add(artist[at..]);
        for (int i = 0; i < parts.Count; i += 2)
            if (Text.Normalize(parts[i]) == key) parts[i] = to;
        return string.Concat(parts);
    }

    // A name written like a handle or a link ("neobliviscaris", "ne obliviscaris": no capitals, as SoundCloud and Bandcamp give
    // them) the way a hint (the song found on YouTube Music), the library or Deezer write it, when the letters are the same;
    // otherwise as it is. Never fails: Deezer unreachable = the name as it came.
    public static async Task<string?> SpellAsync(Library lib, string? artist, CancellationToken ct, params string?[] hints)
    {
        if (artist == null || !artist.Any(char.IsLower) || artist.Any(char.IsUpper)) return artist;
        var key = Squash(artist);
        bool Same(string? n) => n != null && n.Any(char.IsUpper) && Squash(n) == key;
        if (hints.FirstOrDefault(Same) is { } hint) return hint.Trim();
        if (lib.Snapshot().SelectMany(t => Of(t.Artist)).FirstOrDefault(Same) is { } mine) return mine;
        try { return (await OnlineSearchServices.SearchArtistsAsync(artist, 5, ct)).Select(a => a.Name).FirstOrDefault(Same) ?? artist; }
        catch (Exception e) when (e is not OperationCanceledException) { return artist; }
    }

    private static string Squash(string? s) => Text.Normalize(s).Replace(" ", "");
}

public static partial class OnlineSearchServices
{
    // Deezer gives a grey placeholder for artists without a picture (an empty id in the address): none.
    private static string? Picture(JsonElement a)
        => a.Str("picture_md5") is { Length: > 0 } || a.Str("picture_xl") is { } u && !u.Contains("/artist//") ? a.Str("picture_xl") ?? a.Str("picture_big") : null;

    private static ArtistInfo? Artist(JsonElement a)
        => a.Str("id") is { } id && a.Str("name") is { Length: > 0 } name
            ? new ArtistInfo(id, name, Picture(a), (int)(a.Num("nb_fan") ?? 0), (int)(a.Num("nb_album") ?? 0)) : null;

    // Artists named like the words on Deezer, the best known first (the one named exactly so first of all).
    public static async Task<List<ArtistInfo>> SearchArtistsAsync(string query, int limit, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await GetAsync($"https://api.deezer.com/search/artist?q={Uri.EscapeDataString(query)}&limit={limit}", ct));
        var key = Text.Normalize(query);
        return doc.RootElement.Arr("data").Select(Artist).OfType<ArtistInfo>()
            .OrderByDescending(a => Text.Normalize(a.Name) == key).ThenByDescending(a => a.Fans).ToList();
    }

    // The artist named exactly so (the best known one: "Queen" has namesakes), with its picture; null when Deezer doesn't know it.
    public static async Task<ArtistInfo?> ArtistInfoAsync(string name, CancellationToken ct)
    {
        var found = await SearchArtistsAsync(name, 10, ct);
        var key = Text.Normalize(name);
        return found.FirstOrDefault(a => Text.Normalize(a.Name) == key) ?? found.FirstOrDefault(a => Text.Coverage(name, a.Name) >= 0.8);
    }

    public static async Task<ArtistInfo?> ArtistByIdAsync(string id, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await GetAsync($"https://api.deezer.com/artist/{id}", ct));
        return doc.RootElement.Str("error") == null ? Artist(doc.RootElement) : null;
    }

    // Artists like this one (Deezer's), the best known first.
    public static async Task<List<ArtistInfo>> RelatedArtistsAsync(string id, int limit, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await GetAsync($"https://api.deezer.com/artist/{id}/related?limit={limit}", ct));
        return doc.RootElement.Arr("data").Select(Artist).OfType<ArtistInfo>().ToList();
    }

    // An artist's latest releases on Deezer (albums, EPs and singles; not the compilations it's on), the newest first.
    public static async Task<List<Release>> ReleasesAsync(string id, string artist, int limit, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await GetAsync($"https://api.deezer.com/artist/{id}/albums?limit={limit}", ct));
        var list = new List<Release>();
        foreach (var a in doc.RootElement.Arr("data"))
        {
            if (a.Str("record_type") is not ("album" or "ep" or "single") || a.Str("title") is not { Length: > 0 } title
                || !DateTime.TryParse(a.Str("release_date"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
                continue;
            list.Add(new Release
            {
                Artist = artist, Title = title, Kind = a.Str("record_type")!, Date = date, Cover = a.Str("cover_big") ?? a.Str("cover_medium"),
                Url = a.Str("link") ?? "https://www.deezer.com/album/" + a.Str("id"),
            });
        }
        return list.OrderByDescending(r => r.Date).ToList();
    }
}

// The followed artists' new releases: looked for now and then (MainViewModel), kept in the profile.
public static class NewReleases
{
    // "New" = out in the last weeks; older ones drop off the list.
    public const int Days = 60;
    public static readonly TimeSpan Every = TimeSpan.FromHours(12);

    // The latest releases of every followed artist (one at a time, a little apart: Deezer's limits). The ones not known
    // before are returned (to tell the user); the ones out before the artist was followed count as seen.
    public static async Task<List<Release>> CheckAsync(Profile profile, CancellationToken ct)
    {
        var since = DateTime.Today.AddDays(-Days);
        var found = new List<Release>();
        foreach (var artist in profile.ArtistsSnapshot())
        {
            ct.ThrowIfCancellationRequested();
            // (the one chosen by hand wins over the Deezer id kept when it was followed; one not on Deezer, its own site's)
            var site = ArtistSites.Get(artist.Name) is { } s && (s.Mine || artist.DeezerId == null) ? s
                : artist.DeezerId != null ? new ArtistSite { Id = artist.DeezerId, Name = artist.Name } : null;
            if (site == null) continue;
            try
            {
                foreach (var r in await ArtistSites.ReleasesAsync(site, artist.Name, 12, ct))
                {
                    if (r.Date < since) break;
                    // (out before the follow, or announced for later: no news)
                    r.Seen = r.Date < artist.Followed.Date.AddDays(-1);
                    if (r.Date <= DateTime.Today) found.Add(r);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
            await Task.Delay(150, ct);
        }
        return profile.MergeReleases(found, since);
    }
}
