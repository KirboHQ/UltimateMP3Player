using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltimateMP3Player.Core;

// A song found on a music site from the search box: downloading it is the same as pasting its link.
public sealed class SearchHit
{
    public string Service { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public double? Duration { get; init; }
    public string Url { get; init; } = "";
    public string? Thumb { get; init; }
    // Place in its service's own results (0 = first).
    public int Rank { get; init; }
    // Its id on its site when the link doesn't show it ("soundcloud:123").
    public string? SiteKey { get; init; }
    // Its artist's id on its site (Deezer's artist, SoundCloud's uploader when it's the artist, YouTube Music's channel): who
    // has a song of the library (ArtistSites).
    public string? ArtistId { get; init; }
    // An album, an EP or a playlist instead of a song ("album", "ep", "playlist"): opened like its link, a card with its
    // songs to save into a playlist. Its year and how many songs, when the site says.
    public string? Collection { get; init; }
    public string? Year { get; init; }
    public int? Songs { get; init; }

    // What a download of it would be known by: found again in the library, and the suggestions that start from it ask its
    // site by its id (a SoundCloud link alone would send them to YouTube Music).
    public List<string> Keys()
    {
        var keys = SourceKeys.ForItem(new MediaItem { Url = Url, PageUrl = Url });
        if (SiteKey != null) keys.Add(SiteKey);
        return keys;
    }
}

// The sites the search box can search, without accounts or keys: YouTube Music (official songs), YouTube (videos,
// covers, live), SoundCloud (remixes, mashups) and Deezer (a catalogue like Spotify's; the audio comes from YouTube
// Music, as for Deezer and Spotify links). Spotify refuses anonymous searches: only with the user's own key (SpotifyKey).
public static partial class OnlineSearchServices
{
    public const string YouTubeMusic = "YouTube Music", YouTube = "YouTube", SoundCloud = "SoundCloud", Deezer = "Deezer", Spotify = "Spotify";
    public static readonly string[] Default = { YouTubeMusic, SoundCloud, YouTube, Deezer, Spotify };
    // Not searched for songs: only an artist's albums come from its catalogue (ArtistAlbumsAsync).
    public const string AppleMusic = "Apple Music";

    // The user's own Spotify app (a free one on developer.spotify.com), from the settings: without it Spotify isn't
    // searched (its tab says what it takes).
    public static (string Id, string Secret)? SpotifyKey { get; private set; }

    public static void UseSpotifyKey(string? id, string? secret)
    {
        SpotifyKey = !string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(secret) ? (id.Trim(), secret.Trim()) : null;
        _spotifyToken = null;
    }

    // Spotify said no to the key itself (wrong, or its account without Premium), not "no answer".
    public static bool SpotifyRefused(Exception ex)
        => ex is HttpRequestException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden };

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // A result as the item of a link, like a playlist's entry not opened yet: shown, downloaded, added to a room or heard
    // without reading its link first (its details come with the download). Not for Deezer's, a catalogue read through its
    // own resolver.
    public static MediaItem ToItem(SearchHit h)
    {
        var item = new MediaItem
        {
            Title = h.Title,
            // (a YouTube video's "artist" is its channel: the title says who sings, as when its link is read)
            Artist = h.Service == YouTube ? null : h.Artist,
            Album = h.Album,
            Duration = h.Duration,
            Kind = h.Service is YouTube or YouTubeMusic ? MediaKind.Video : MediaKind.Audio,
            // (a catalogue's song is found on YouTube Music: the same song from another site counts as had, as for its link)
            Source = h.Service is Deezer or Spotify ? SourceKind.Search : SourceKind.YtDlp,
            Url = h.Url,
            PageUrl = h.Url,
            SpotifyTrackId = h.Service == Spotify ? h.Url[(h.Url.LastIndexOf('/') + 1)..] : null,
            SiteName = Sites.NameFor(h.Url),
        };
        if (h.Thumb != null) item.Thumbnails.Add(h.Thumb);
        return item;
    }

    // The enabled ones, in the chosen order (unknown names dropped, services added later go at the end).
    public static List<string> Ordered(IEnumerable<string> order, IEnumerable<string> off)
    {
        var offSet = off.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return order.Where(Default.Contains).Concat(Default).Distinct().Where(s => !offSet.Contains(s)).ToList();
    }

    public static Task<List<SearchHit>> SearchAsync(string service, string query, int limit, CancellationToken ct) => service switch
    {
        YouTubeMusic => YouTubeMusicAsync(query, limit, ct),
        YouTube => YouTubeAsync(query, limit, ct),
        SoundCloud => SoundCloudAsync(query, limit, ct),
        Deezer => DeezerAsync(query, limit, ct),
        Spotify => SpotifyAsync(query, limit, ct),
        _ => Task.FromResult(new List<SearchHit>()),
    };

    private static string Lang => L.English ? "en" : "it";

    private static async Task<string> PostJsonAsync(string url, string json, string origin, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("User-Agent", Http.BrowserUA);
        req.Headers.TryAddWithoutValidation("Origin", origin);
        req.Headers.TryAddWithoutValidation("Referer", origin + "/");
        using var resp = await Http.Client.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(cts.Token);
    }

    private static async Task<string> GetAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        return await Http.GetStringAsync(url, ct: cts.Token);
    }

    // local: in the app's language from Italy (the search box); otherwise as yt-dlp asks (English, the country from the address).
    private static string InnerTubeBody(string client, string version, string query, string filter, bool local = true) => JsonSerializer.Serialize(new
    {
        context = new { client = local ? (object)new { clientName = client, clientVersion = version, hl = Lang, gl = "IT" } : new { clientName = client, clientVersion = version, hl = "en" } },
        query,
        @params = filter,
    });

    private static double? ParseClock(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || !Regex.IsMatch(s.Trim(), @"^\d+(:\d{1,2}){1,2}$")) return null;
        double total = 0;
        foreach (var part in s.Trim().Split(':')) total = total * 60 + int.Parse(part);
        return total;
    }

    private static string Runs(JsonElement? text)
        => text is { } t ? string.Concat(t.Arr("runs").Select(r => r.Str("text"))) : "";

    // What a link of YouTube Music points to: "MUSIC_PAGE_TYPE_ALBUM", "…_ARTIST"...
    private static string? PageType(JsonElement run) => run.Prop("navigationEndpoint")?.Prop("browseEndpoint")?.Prop("browseEndpointContextSupportedConfigs")
        ?.Prop("browseEndpointContextMusicConfig")?.Str("pageType");

    // ------------------------------------------------------------------ YouTube Music: the "Songs" results

    // The songs MusicMatcher picks from, as yt-dlp's search finds them (the same results, measured: its lower places change
    // from one search to the next anyway), without starting yt-dlp for each song of a playlist.
    public static Task<List<SearchHit>> MatchCandidatesAsync(string query, int limit, CancellationToken ct) => YouTubeMusicAsync(query, limit, ct, local: false);

    private static async Task<List<SearchHit>> YouTubeMusicAsync(string query, int limit, CancellationToken ct, bool local = true)
    {
        var json = await PostJsonAsync("https://music.youtube.com/youtubei/v1/search?prettyPrint=false",
            InnerTubeBody("WEB_REMIX", "1.20240904.01.00", query, "EgWKAQIIAWoKEAkQBRAKEAMQBA%3D%3D", local), "https://music.youtube.com", ct);
        using var doc = JsonDocument.Parse(json);
        var hits = new List<SearchHit>();
        foreach (var row in MusicRows(doc.RootElement))
        {
            if (hits.Count >= limit) break;
            var cols = row.Arr("flexColumns").Select(c => c.Prop("musicResponsiveListItemFlexColumnRenderer")?.Prop("text")).ToList();
            if (cols.Count < 2) continue;
            var titleRun = cols[0]?.Arr("runs").FirstOrDefault();
            var id = row.Prop("playlistItemData")?.Str("videoId") ?? titleRun?.Prop("navigationEndpoint")?.Prop("watchEndpoint")?.Str("videoId");
            var title = Runs(cols[0]);
            if (id == null || title.Length == 0) continue;
            // "Artist(s) • Album • 3:59": groups split by the dots.
            var groups = new List<List<JsonElement>> { new() };
            foreach (var r in cols[1]?.Arr("runs") ?? Enumerable.Empty<JsonElement>())
            {
                if (r.Str("text")?.Trim() == "•") groups.Add(new List<JsonElement>());
                else groups[^1].Add(r);
            }
            string Join(List<JsonElement> g) => string.Concat(g.Select(r => r.Str("text"))).Trim();
            double? duration = groups.Select(g => ParseClock(Join(g))).LastOrDefault(d => d != null);
            var album = groups.Skip(1).FirstOrDefault(g => g.Any(r => PageType(r) == "MUSIC_PAGE_TYPE_ALBUM")) is { } ag ? Join(ag) : null;
            var artist = groups[0].Count > 0 && ParseClock(Join(groups[0])) == null ? Join(groups[0]) : null;
            var artistId = groups[0].Where(r => PageType(r) == "MUSIC_PAGE_TYPE_ARTIST").Select(r => r.Prop("navigationEndpoint")?.Prop("browseEndpoint")?.Str("browseId"))
                .FirstOrDefault(x => x != null);
            hits.Add(new SearchHit
            {
                Service = YouTubeMusic, Title = title, Artist = artist, Album = album, Duration = duration, Thumb = MusicThumb(row), Rank = hits.Count,
                Url = "https://music.youtube.com/watch?v=" + id, ArtistId = artistId,
            });
        }
        return hits;
    }

    // The rows of a YouTube Music search, shelf after shelf.
    private static IEnumerable<JsonElement> MusicRows(JsonElement root)
        => (root.Prop("contents")?.Prop("tabbedSearchResultsRenderer")?.Arr("tabs").FirstOrDefault().Prop("tabRenderer")?.Prop("content")
                ?.Prop("sectionListRenderer")?.Arr("contents") ?? Enumerable.Empty<JsonElement>())
            .Select(s => s.Prop("musicShelfRenderer")).OfType<JsonElement>()
            .SelectMany(shelf => shelf.Arr("contents").Select(c => c.Prop("musicResponsiveListItemRenderer")).OfType<JsonElement>());

    private static string? MusicThumb(JsonElement row)
        => row.Prop("thumbnail")?.Prop("musicThumbnailRenderer")?.Prop("thumbnail")?.Arr("thumbnails").LastOrDefault().Str("url") is { } thumb
            ? Regex.Replace(thumb, @"=w\d+-h\d+", "=w300-h300") : null;

    // ------------------------------------------------------------------ YouTube: videos

    private static async Task<List<SearchHit>> YouTubeAsync(string query, int limit, CancellationToken ct)
    {
        var json = await PostJsonAsync("https://www.youtube.com/youtubei/v1/search?prettyPrint=false",
            InnerTubeBody("WEB", "2.20240904.01.00", query, "EgIQAQ%3D%3D"), "https://www.youtube.com", ct);
        using var doc = JsonDocument.Parse(json);
        var hits = new List<SearchHit>();
        var sections = doc.RootElement.Prop("contents")?.Prop("twoColumnSearchResultsRenderer")?.Prop("primaryContents")?.Prop("sectionListRenderer")
            ?.Arr("contents") ?? Enumerable.Empty<JsonElement>();
        foreach (var v in sections.SelectMany(s => s.Prop("itemSectionRenderer")?.Arr("contents") ?? Enumerable.Empty<JsonElement>())
                     .Select(c => c.Prop("videoRenderer")).OfType<JsonElement>())
        {
            if (hits.Count >= limit) break;
            var id = v.Str("videoId");
            var title = Runs(v.Prop("title"));
            var length = v.Prop("lengthText")?.Str("simpleText");
            // No length = a live stream or a premiere: nothing to download as a song.
            if (id == null || title.Length == 0 || length == null) continue;
            hits.Add(new SearchHit
            {
                Service = YouTube, Title = title, Artist = Runs(v.Prop("ownerText")) is { Length: > 0 } ch ? ch : null, Duration = ParseClock(length),
                Thumb = $"https://i.ytimg.com/vi/{id}/mqdefault.jpg", Url = "https://www.youtube.com/watch?v=" + id, Rank = hits.Count,
            });
        }
        return hits;
    }

    // ------------------------------------------------------------------ SoundCloud

    // The public web app's key, read from its scripts (like yt-dlp does); kept until SoundCloud refuses it.
    private static string? _scClient;
    private static readonly SemaphoreSlim ScGate = new(1);

    private static async Task<string> SoundCloudClientAsync(bool fresh, CancellationToken ct)
    {
        await ScGate.WaitAsync(ct);
        try
        {
            if (!fresh && _scClient != null) return _scClient;
            var html = await GetAsync("https://soundcloud.com/", ct);
            var scripts = Regex.Matches(html, @"<script[^>]+src=""(https://a-v2\.sndcdn\.com/assets/[^""]+\.js)""").Select(m => m.Groups[1].Value).Reverse().ToList();
            foreach (var src in scripts)
            {
                var js = await GetAsync(src, ct);
                var m = Regex.Match(js, @"client_id\s*:\s*""([0-9A-Za-z]{32})""");
                if (m.Success) return _scClient = m.Groups[1].Value;
            }
            throw new EngineException(L.T("SoundCloud didn't answer as expected."));
        }
        finally { ScGate.Release(); }
    }

    // An API call with the web app's key (a fresh one if SoundCloud refuses it).
    private static async Task<string> SoundCloudGetAsync(Func<string, string> url, CancellationToken ct)
    {
        var client = await SoundCloudClientAsync(false, ct);
        try { return await GetAsync(url(client), ct); }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return await GetAsync(url(await SoundCloudClientAsync(true, ct)), ct);
        }
    }

    private static async Task<List<SearchHit>> SoundCloudAsync(string query, int limit, CancellationToken ct)
    {
        var json = await SoundCloudGetAsync(c => $"https://api-v2.soundcloud.com/search/tracks?q={Uri.EscapeDataString(query)}&client_id={c}&limit={Math.Min(50, limit * 2)}", ct);
        using var doc = JsonDocument.Parse(json);
        return SoundCloudHits(doc.RootElement, limit);
    }

    private static List<SearchHit> SoundCloudHits(JsonElement root, int limit)
    {
        var hits = new List<SearchHit>();
        foreach (var t in root.Arr("collection"))
        {
            if (hits.Count >= limit) break;
            // SNIP = only a 30-second preview without SoundCloud Go, BLOCK = not playable here.
            if (t.Str("policy") is "SNIP" or "BLOCK" || t.Str("permalink_url") is not { } url || t.Str("title") is not { Length: > 0 } title) continue;
            var art = t.Str("artwork_url") ?? t.Prop("user")?.Str("avatar_url");
            var user = t.Prop("user");
            var artist = t.Prop("publisher_metadata")?.Str("artist") is { Length: > 0 } a ? a : user?.Str("username");
            // (the uploader is the artist only when it has the artist's name: not a label's or a fan's upload)
            bool own = user is { } u && ArtistNames.Of(artist).Any(n => Text.Normalize(n) is var k && (k == Text.Normalize(u.Str("username")) || k == Text.Normalize(u.Str("full_name"))));
            hits.Add(new SearchHit
            {
                Service = SoundCloud, Title = title, Artist = artist,
                Duration = t.Num("duration") / 1000.0, Thumb = art?.Replace("-large.", "-t300x300."), Url = url, Rank = hits.Count,
                SiteKey = t.Str("id") is { } id ? "soundcloud:" + id : null, ArtistId = own ? user?.Str("id") : null,
            });
        }
        return hits;
    }

    // ------------------------------------------------------------------ Deezer (free public API)

    private static async Task<List<SearchHit>> DeezerAsync(string query, int limit, CancellationToken ct)
    {
        var json = await GetAsync($"https://api.deezer.com/search/track?q={Uri.EscapeDataString(query)}&limit={limit}", ct);
        using var doc = JsonDocument.Parse(json);
        return DeezerHits(doc.RootElement, limit);
    }

    private static List<SearchHit> DeezerHits(JsonElement root, int limit)
    {
        var hits = new List<SearchHit>();
        foreach (var t in root.Arr("data"))
        {
            if (hits.Count >= limit) break;
            // The radio's songs come without their link.
            var url = t.Str("link") ?? (t.Str("id") is { } id ? "https://www.deezer.com/track/" + id : null);
            if (url == null || t.Str("title") is not { Length: > 0 } title) continue;
            hits.Add(new SearchHit
            {
                Service = Deezer, Title = title, Artist = t.Prop("artist")?.Str("name"), Album = t.Prop("album")?.Str("title"), Duration = t.Num("duration"),
                Thumb = t.Prop("album")?.Str("cover_medium"), Url = url, Rank = hits.Count, ArtistId = t.Prop("artist")?.Str("id"),
            });
        }
        return hits;
    }

    // ------------------------------------------------------------------ Spotify (the user's key)

    // The key's access token (an hour), kept until it expires or Spotify refuses it.
    private static string? _spotifyToken;
    private static DateTime _spotifyTokenEnd;
    private static readonly SemaphoreSlim SpotifyGate = new(1);

    private static async Task<string> SpotifyTokenAsync(bool fresh, CancellationToken ct)
    {
        var key = SpotifyKey ?? throw new EngineException(L.T("Spotify needs your key: see Settings › Online search."));
        await SpotifyGate.WaitAsync(ct);
        try
        {
            if (!fresh && _spotifyToken != null && DateTime.UtcNow < _spotifyTokenEnd) return _spotifyToken;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }),
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(key.Id + ":" + key.Secret)));
            using var resp = await Http.Client.SendAsync(req, cts.Token);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            _spotifyTokenEnd = DateTime.UtcNow.AddSeconds((doc.RootElement.Num("expires_in") ?? 3600) - 60);
            return _spotifyToken = doc.RootElement.Str("access_token") ?? throw new EngineException(L.T("Spotify didn't answer as expected."));
        }
        finally { SpotifyGate.Release(); }
    }

    // An API call with the key's token.
    private static async Task<string> SpotifyGetAsync(string url, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await SpotifyTokenAsync(attempt > 0, ct));
            using var resp = await Http.Client.SendAsync(req, cts.Token);
            // (a token that expired early: a fresh one, once)
            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(cts.Token);
        }
    }

    private static async Task<List<SearchHit>> SpotifyAsync(string query, int limit, CancellationToken ct)
    {
        // shortcut: one page of at most 10 (the most Spotify gives a key since 2026); ask for a second page (&offset=10) if 10 are too few.
        using var doc = JsonDocument.Parse(await SpotifyGetAsync($"https://api.spotify.com/v1/search?type=track&limit={Math.Min(10, limit)}&q={Uri.EscapeDataString(query)}", ct));
        var hits = new List<SearchHit>();
        foreach (var t in doc.RootElement.Prop("tracks")?.Arr("items") ?? Enumerable.Empty<JsonElement>())
            if (hits.Count < limit && SpotifyHit(t, hits.Count) is { } h) hits.Add(h);
        return hits;
    }

    private static SearchHit? SpotifyHit(JsonElement t, int rank)
    {
        if (t.Str("id") is not { } id || t.Str("name") is not { Length: > 0 } title) return null;
        var album = t.Prop("album");
        return new SearchHit
        {
            Service = Spotify, Title = title, Artist = string.Join(", ", t.Arr("artists").Select(a => a.Str("name")).OfType<string>()) is { Length: > 0 } who ? who : null,
            Album = album?.Str("name"), Duration = t.Num("duration_ms") / 1000.0, Rank = rank,
            Thumb = album?.Arr("images").OrderBy(i => Math.Abs((i.Int("width") ?? 0) - 300)).Select(i => i.Str("url")).FirstOrDefault(u => u != null),
            Url = "https://open.spotify.com/track/" + id,
        };
    }

    // The artist the words name on Spotify, with their songs, the most played first. A key can't ask for an artist's top
    // songs any more (2026): it's a search of their name, kept to their own songs, without doubles. Null when there's none.
    public static async Task<(string Name, List<SearchHit> Hits)?> SpotifyArtistAsync(string name, int limit, CancellationToken ct)
    {
        if (await SpotifyFindArtistAsync(name, ct) is not { } artist) return null;
        var (id, found) = artist;
        var hits = new List<SearchHit>();
        var seen = new HashSet<string>();
        var q = Uri.EscapeDataString($"artist:\"{found}\"");
        // shortcut: pages of 10 one after the other, at most 10 of them (Deezer's top is a hundred too); more pages if fans ask for every song.
        for (int offset = 0; offset < 100 && hits.Count < limit; offset += 10)
        {
            using var doc = JsonDocument.Parse(await SpotifyGetAsync($"https://api.spotify.com/v1/search?type=track&limit=10&offset={offset}&q={q}", ct));
            var page = doc.RootElement.Prop("tracks")?.Arr("items").ToList() ?? new List<JsonElement>();
            foreach (var t in page)
                if (hits.Count < limit && t.Arr("artists").Any(a => a.Str("id") == id) && SpotifyHit(t, hits.Count) is { } h && seen.Add(SongMeta.Key(h.Title, found)))
                    hits.Add(h);
            if (page.Count < 10) break;
        }
        return (found, hits);
    }

    // The artist the words name on Spotify: exactly that name, or the first one when it's nearly that name.
    private static async Task<(string Id, string Name)?> SpotifyFindArtistAsync(string name, CancellationToken ct)
    {
        using var s = JsonDocument.Parse(await SpotifyGetAsync($"https://api.spotify.com/v1/search?type=artist&limit=10&q={Uri.EscapeDataString(name)}", ct));
        // (the best known first: Spotify no longer tells a key how many follow each)
        var artists = s.RootElement.Prop("artists")?.Arr("items").ToList() ?? new List<JsonElement>();
        var artist = artists.FirstOrDefault(a => Text.Normalize(a.Str("name")) == Text.Normalize(name));
        if (artist.ValueKind != JsonValueKind.Object && artists.Count > 0 && Text.Coverage(name, artists[0].Str("name") ?? "") >= 0.8) artist = artists[0];
        return artist.ValueKind == JsonValueKind.Object && artist.Str("id") is { } id ? (id, artist.Str("name") ?? name) : null;
    }

    // ------------------------------------------------------------------ similar songs (the suggested queue)

    // Songs like the seed, without the seed itself and without doubles: from its own site first, every site taking
    // turns, or the site chosen (the others when it has nothing).
    public static async Task<List<SearchHit>> SimilarAsync(RadioSeed seed, string source, int limit, CancellationToken ct)
    {
        var hits = new List<SearchHit>();
        if (source == RadioSources.Mix)
        {
            var each = await Task.WhenAll(RadioSources.Sites.Select(s => SimilarOn(s, seed, limit, ct)));
            hits = each.SelectMany(h => h).OrderBy(h => h.Rank).ThenBy(h => Array.IndexOf(RadioSources.Sites, h.Service)).ToList();
        }
        else
            foreach (var s in RadioSources.Order(source, seed))
                if ((hits = await SimilarOn(s, seed, limit, ct)).Count > 0) break;
        var seen = new HashSet<string> { SongMeta.Key(seed.Title, seed.Artist) };
        return hits.Where(h => seen.Add(SongMeta.Key(h.Title, h.Artist))).Take(limit).ToList();
    }

    private static async Task<List<SearchHit>> SimilarOn(string service, RadioSeed seed, int limit, CancellationToken ct)
    {
        try
        {
            return await (service switch
            {
                YouTubeMusic => YouTubeMusicRadioAsync(seed, limit, ct),
                SoundCloud => SoundCloudRadioAsync(seed, limit, ct),
                Deezer => DeezerRadioAsync(seed, limit, ct),
                _ => Task.FromResult(new List<SearchHit>()),
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new List<SearchHit>(); }
    }

    // The song's radio, as in YouTube Music's player.
    private static async Task<List<SearchHit>> YouTubeMusicRadioAsync(RadioSeed seed, int limit, CancellationToken ct)
    {
        var id = seed.KeyOf("youtube");
        if (id == null && (await YouTubeMusicAsync(seed.Query, 1, ct)).FirstOrDefault()?.Url is { } found) id = Regex.Match(found, @"[?&]v=([\w-]{11})").Groups[1].Value;
        if (string.IsNullOrEmpty(id)) return new List<SearchHit>();
        var body = JsonSerializer.Serialize(new
        {
            context = new { client = new { clientName = "WEB_REMIX", clientVersion = "1.20240904.01.00", hl = Lang, gl = "IT" } },
            videoId = id,
            playlistId = "RDAMVM" + id,
            isAudioOnly = true,
        });
        using var doc = JsonDocument.Parse(await PostJsonAsync("https://music.youtube.com/youtubei/v1/next?prettyPrint=false", body, "https://music.youtube.com", ct));
        var rows = doc.RootElement.Prop("contents")?.Prop("singleColumnMusicWatchNextResultsRenderer")?.Prop("tabbedRenderer")?.Prop("watchNextTabbedResultsRenderer")
            ?.Arr("tabs").FirstOrDefault().Prop("tabRenderer")?.Prop("content")?.Prop("musicQueueRenderer")?.Prop("content")?.Prop("playlistPanelRenderer")
            ?.Arr("contents") ?? Enumerable.Empty<JsonElement>();
        var hits = new List<SearchHit>();
        foreach (var row in rows)
        {
            if (hits.Count >= limit) break;
            // A song that also has its video: the song.
            var v = row.Prop("playlistPanelVideoRenderer") ?? row.Prop("playlistPanelVideoWrapperRenderer")?.Prop("primaryRenderer")?.Prop("playlistPanelVideoRenderer");
            if (v is not { } r || r.Str("videoId") is not { } vid || vid == id || Runs(r.Prop("title")) is not { Length: > 0 } title) continue;
            // "Artist • Album • 2019", or "Artist • 2 M views • 14 K likes" for a video.
            var byline = r.Prop("longBylineText")?.Arr("runs").ToList() ?? new List<JsonElement>();
            var artist = string.Concat(byline.TakeWhile(x => x.Str("text")?.Trim() != "•").Select(x => x.Str("text"))).Trim();
            var thumb = r.Prop("thumbnail")?.Arr("thumbnails").LastOrDefault().Str("url");
            hits.Add(new SearchHit
            {
                Service = YouTubeMusic, Title = title, Artist = artist.Length > 0 ? artist : null,
                Album = byline.FirstOrDefault(x => PageType(x) == "MUSIC_PAGE_TYPE_ALBUM").Str("text"), Duration = ParseClock(Runs(r.Prop("lengthText"))),
                Thumb = thumb != null && thumb.Contains("googleusercontent") ? Regex.Replace(thumb, @"=w\d+-h\d+", "=w544-h544") : $"https://i.ytimg.com/vi/{vid}/mqdefault.jpg",
                Url = "https://music.youtube.com/watch?v=" + vid, Rank = hits.Count,
            });
        }
        return hits;
    }

    // The tracks SoundCloud relates to this one.
    private static async Task<List<SearchHit>> SoundCloudRadioAsync(RadioSeed seed, int limit, CancellationToken ct)
    {
        var id = seed.KeyOf("soundcloud");
        if (id == null)
        {
            using var found = JsonDocument.Parse(await SoundCloudGetAsync(c => $"https://api-v2.soundcloud.com/search/tracks?q={Uri.EscapeDataString(seed.Query)}&client_id={c}&limit=1", ct));
            id = found.RootElement.Arr("collection").FirstOrDefault().Str("id");
        }
        if (id == null) return new List<SearchHit>();
        using var doc = JsonDocument.Parse(await SoundCloudGetAsync(c => $"https://api-v2.soundcloud.com/tracks/{id}/related?client_id={c}&limit={Math.Min(50, limit * 2)}", ct));
        return SoundCloudHits(doc.RootElement, limit);
    }

    // ------------------------------------------------------------------ the artist's songs ("Play the artist's songs")

    // Songs of the seed's artist, the most listened first (the artist's top on Deezer; YouTube Music's songs of the artist
    // when Deezer doesn't know them), without the seed and without doubles. skip: how many were given already (the next
    // ones, when the queue asks for more).
    public static async Task<List<SearchHit>> ArtistSongsAsync(RadioSeed seed, int limit, int skip, CancellationToken ct)
    {
        var name = seed.Artist?.Split(',')[0].Trim();
        if (string.IsNullOrEmpty(name)) return new List<SearchHit>();
        var hits = new List<SearchHit>();
        // (the artist the library's songs proved, or the one chosen by hand: Envy on SoundCloud, not Deezer's namesake)
        try { hits = ArtistSites.Get(name) is { } site ? await ArtistSites.TopAsync(site, limit, skip, ct) : await DeezerArtistTopAsync(name, limit, skip, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        if (hits.Count == 0 && skip == 0)
        {
            try { hits = (await YouTubeMusicAsync(name, Math.Min(50, limit * 2), ct)).Where(h => Text.Coverage(name, h.Artist ?? "") >= 0.5).ToList(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        }
        var seen = new HashSet<string> { SongMeta.Key(seed.Title, seed.Artist) };
        return hits.Where(h => seen.Add(SongMeta.Key(h.Title, h.Artist))).Take(limit).ToList();
    }

    private static async Task<List<SearchHit>> DeezerArtistTopAsync(string name, int limit, int skip, CancellationToken ct)
        => await FindArtistAsync(name, ct) is { } a ? await ArtistTopAsync(a.Id, limit, skip, ct) : new List<SearchHit>();

    // The artist on Deezer with exactly that name (the best known one: "Queen" has namesakes), not the first one that only
    // looks like it; or the first one when it's nearly that name. Null when there's none.
    public static async Task<(string Id, string Name)?> FindArtistAsync(string name, CancellationToken ct)
    {
        using var s = JsonDocument.Parse(await GetAsync($"https://api.deezer.com/search/artist?q={Uri.EscapeDataString(name)}&limit=10", ct));
        var artists = s.RootElement.Arr("data").ToList();
        var artist = artists.Where(a => Text.Normalize(a.Str("name")) == Text.Normalize(name)).OrderByDescending(a => a.Num("nb_fan") ?? 0).FirstOrDefault();
        if (artist.ValueKind != JsonValueKind.Object && artists.Count > 0 && Text.Coverage(name, artists[0].Str("name") ?? "") >= 0.8) artist = artists[0];
        return artist.ValueKind == JsonValueKind.Object && artist.Str("id") is { } id ? (id, artist.Str("name") ?? name) : null;
    }

    // An artist's songs on Deezer, the most listened first (up to about a hundred).
    public static async Task<List<SearchHit>> ArtistTopAsync(string id, int limit, int skip, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await GetAsync($"https://api.deezer.com/artist/{id}/top?limit={Math.Min(100, limit)}&index={skip}", ct));
        return DeezerHits(doc.RootElement, limit);
    }

    // ------------------------------------------------------------------ albums and playlists (opened like their link)

    // An artist's albums and EPs on one site (YouTube's are YouTube Music's: the same playlists), with the artist's name as
    // the site writes it: the artist named exactly so, the best known one. The singles are left out: they're among the
    // artist's songs. Null when the site doesn't know them.
    public static async Task<(string Name, List<SearchHit> Hits)?> ArtistAlbumsAsync(string service, string name, CancellationToken ct)
    {
        var found = await (service switch
        {
            Deezer => DeezerArtistAlbumsAsync(name, ct),
            YouTubeMusic => YouTubeMusicArtistAlbumsAsync(name, ct),
            SoundCloud => SoundCloudArtistAlbumsAsync(name, ct),
            Spotify => SpotifyArtistAlbumsAsync(name, ct),
            AppleMusic => AppleArtistAlbumsAsync(name, ct),
            _ => Task.FromResult<(string, List<SearchHit>)?>(null),
        });
        return found is { Item2.Count: > 0 } ? found : null;
    }

    // Albums from several sites as one list: the albums, then the EPs, each the newest first. One on more than one site
    // is kept once, the first site's (the sites in the order given).
    public static List<SearchHit> MergeAlbums(IEnumerable<List<SearchHit>> sites)
    {
        var seen = new HashSet<string>();
        // ("LeftRightLeftRightLeft" is "Left Right Left Right Left", "Kaleidoscope EP" Apple's "Kaleidoscope - EP"; titles of
        // symbols only, "÷" and "×", are their own key)
        string Key(SearchHit h) => Regex.Replace(Text.Normalize(h.Title), " ep$", "").Replace(" ", "") is { Length: > 0 } key ? key : h.Title.Trim();
        return sites.SelectMany(s => s).Where(h => seen.Add(Key(h)))
            .OrderBy(h => h.Collection == "ep").ThenByDescending(h => h.Year ?? "").ToList();
    }

    // Deezer's: the albums first, each the newest first (Deezer's own order).
    private static async Task<(string, List<SearchHit>)?> DeezerArtistAlbumsAsync(string name, CancellationToken ct)
    {
        if (await FindArtistAsync(name, ct) is not { } artist) return null;
        return (artist.Name, await DeezerAlbumsOfAsync(artist.Id, ct));
    }

    public static async Task<List<SearchHit>> DeezerAlbumsOfAsync(string id, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await GetAsync($"https://api.deezer.com/artist/{id}/albums?limit=300", ct));
        var hits = new List<SearchHit>();
        foreach (var a in doc.RootElement.Arr("data"))
            if (a.Str("record_type") is "album" or "ep" && a.Str("id") is { } aid && a.Str("title") is { Length: > 0 } title)
                hits.Add(new SearchHit
                {
                    Service = Deezer, Collection = a.Str("record_type"), Title = title, Year = a.Str("release_date") is { Length: >= 4 } d ? d[..4] : null,
                    Thumb = a.Str("cover_medium"), Url = a.Str("link") ?? "https://www.deezer.com/album/" + aid, Rank = hits.Count,
                });
        return hits;
    }

    // YouTube Music's: the artist's page (the first artist named exactly so, the best known), all of its albums (the
    // "more" of its albums shelf) and the EPs among its latest singles. Its albums search for the name finds only a few.
    private static async Task<(string, List<SearchHit>)?> YouTubeMusicArtistAlbumsAsync(string name, CancellationToken ct)
    {
        string id, found;
        using (var s = JsonDocument.Parse(await PostJsonAsync("https://music.youtube.com/youtubei/v1/search?prettyPrint=false",
                   InnerTubeBody("WEB_REMIX", "1.20240904.01.00", name, "EgWKAQIgAWoKEAkQBRAKEAMQBA%3D%3D"), "https://music.youtube.com", ct)))
        {
            string RowName(JsonElement r) => Runs(r.Arr("flexColumns").FirstOrDefault().Prop("musicResponsiveListItemFlexColumnRenderer")?.Prop("text"));
            var row = MusicRows(s.RootElement).FirstOrDefault(r => Text.Normalize(RowName(r)) == Text.Normalize(name));
            if (row.ValueKind != JsonValueKind.Object || row.Prop("navigationEndpoint")?.Prop("browseEndpoint")?.Str("browseId") is not { } page) return null;
            (id, found) = (page, RowName(row));
        }
        return (found, await MusicAlbumsOfAsync(id, ct));
    }

    public static async Task<List<SearchHit>> MusicAlbumsOfAsync(string id, CancellationToken ct)
    {
        using var artist = JsonDocument.Parse(await MusicBrowseAsync(id, null, ct));
        var hits = new List<SearchHit>();
        // (only the discography's shelves have albums: the albums, the singles and EPs)
        foreach (var shelf in BrowseSections(artist.RootElement).Select(x => x.Prop("musicCarouselShelfRenderer")).OfType<JsonElement>())
        {
            var shown = shelf.Arr("contents").Select(c => MusicAlbum(c.Prop("musicTwoRowItemRenderer"))).OfType<SearchHit>().ToList();
            // ("more" only when the shelf doesn't show them all)
            var more = shelf.Prop("header")?.Prop("musicCarouselShelfBasicHeaderRenderer")?.Prop("title")?.Arr("runs").FirstOrDefault()
                .Prop("navigationEndpoint")?.Prop("browseEndpoint");
            if (shown.Any(h => h.Collection == "album") && more?.Str("browseId") is { } moreId
                && more?.Prop("browseEndpointContextSupportedConfigs")?.Prop("browseEndpointContextMusicConfig")?.Str("pageType") == "MUSIC_PAGE_TYPE_ARTIST_DISCOGRAPHY")
                try
                {
                    using var all = JsonDocument.Parse(await MusicBrowseAsync(moreId, more?.Str("params"), ct));
                    var full = BrowseSections(all.RootElement).Select(x => x.Prop("gridRenderer")).OfType<JsonElement>().FirstOrDefault().Arr("items")
                        .Select(c => MusicAlbum(c.Prop("musicTwoRowItemRenderer"))).OfType<SearchHit>().ToList();
                    if (full.Count > 0) shown = full;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { } // (the shelf's own, the latest)
            hits.AddRange(shown);
        }
        return hits;
    }

    private static Task<string> MusicBrowseAsync(string id, string? more, CancellationToken ct) => PostJsonAsync("https://music.youtube.com/youtubei/v1/browse?prettyPrint=false",
        JsonSerializer.Serialize(new
        {
            context = new { client = new { clientName = "WEB_REMIX", clientVersion = "1.20240904.01.00", hl = Lang, gl = "IT" } },
            browseId = id,
            @params = more,
        }), "https://music.youtube.com", ct);

    private static IEnumerable<JsonElement> BrowseSections(JsonElement root)
        => root.Prop("contents")?.Prop("singleColumnBrowseResultsRenderer")?.Arr("tabs").FirstOrDefault().Prop("tabRenderer")?.Prop("content")
            ?.Prop("sectionListRenderer")?.Arr("contents") ?? Enumerable.Empty<JsonElement>();

    // An album or an EP of an artist's page ("Album • 2014", the year alone on the page itself; "EP • 2017"), as its
    // playlist; null for a single or anything else.
    private static SearchHit? MusicAlbum(JsonElement? item)
    {
        if (item is not { } r || r.Prop("title")?.Arr("runs").FirstOrDefault() is not { ValueKind: JsonValueKind.Object } run
            || PageType(run) != "MUSIC_PAGE_TYPE_ALBUM" || run.Str("text") is not { Length: > 0 } title) return null;
        var parts = Runs(r.Prop("subtitle")).Split('•').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        var kind = parts.FirstOrDefault()?.ToLowerInvariant() switch { "ep" => "ep", "single" or "singolo" => null, _ => "album" };
        var list = r.Prop("thumbnailOverlay")?.Prop("musicItemThumbnailOverlayRenderer")?.Prop("content")?.Prop("musicPlayButtonRenderer")
            ?.Prop("playNavigationEndpoint")?.Prop("watchPlaylistEndpoint")?.Str("playlistId");
        if (kind == null || list == null) return null;
        var thumb = r.Prop("thumbnailRenderer")?.Prop("musicThumbnailRenderer")?.Prop("thumbnail")?.Arr("thumbnails").LastOrDefault().Str("url");
        return new SearchHit
        {
            Service = YouTubeMusic, Collection = kind, Title = title, Year = parts.LastOrDefault(p => Regex.IsMatch(p, @"^\d{4}$")),
            Thumb = thumb != null ? Regex.Replace(thumb, @"=w\d+-h\d+", "=w300-h300") : null, Url = "https://music.youtube.com/playlist?list=" + list,
        };
    }

    // SoundCloud's: the user with exactly that name (or profile name), the most followed.
    private static async Task<(string, List<SearchHit>)?> SoundCloudArtistAlbumsAsync(string name, CancellationToken ct)
    {
        string id, found;
        using (var users = JsonDocument.Parse(await SoundCloudGetAsync(c =>
                   $"https://api-v2.soundcloud.com/search/users?q={Uri.EscapeDataString(name)}&client_id={c}&limit=10", ct)))
        {
            var key = Text.Normalize(name);
            var user = users.RootElement.Arr("collection").Where(u => Text.Normalize(u.Str("username")) == key || Text.Normalize(u.Str("full_name")) == key)
                .OrderByDescending(u => u.Num("followers_count") ?? 0).FirstOrDefault();
            if (user.ValueKind != JsonValueKind.Object || user.Str("id") is not { } uid) return null;
            (id, found) = (uid, (Text.Normalize(user.Str("full_name")) == key ? user.Str("full_name") : user.Str("username")) ?? name);
        }
        return (found, await SoundCloudAlbumsOfAsync(id, ct));
    }

    public static async Task<List<SearchHit>> SoundCloudAlbumsOfAsync(string id, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await SoundCloudGetAsync(c => $"https://api-v2.soundcloud.com/users/{id}/albums?client_id={c}&limit=50", ct));
        var hits = new List<SearchHit>();
        // (not a single with its remixes, filed as an album; not one with previews only of some songs, as labels' often are)
        foreach (var p in doc.RootElement.Arr("collection"))
            if (SoundCloudSet(p, hits.Count, owner: false) is { Collection: not "playlist" } h && !(h.Collection == "album" && h.Songs < 4)
                && !p.Arr("tracks").Any(t => t.Str("policy") is "SNIP" or "BLOCK"))
                hits.Add(h);
        return hits;
    }

    // Spotify's (with the user's key): EPs are among its "singles", with four songs or more.
    private static async Task<(string, List<SearchHit>)?> SpotifyArtistAlbumsAsync(string name, CancellationToken ct)
    {
        if (await SpotifyFindArtistAsync(name, ct) is not { } artist) return null;
        var hits = new List<SearchHit>();
        // shortcut: pages of 10 as for a key's searches, at most 10 of them; more pages for an artist with over a hundred releases.
        for (int offset = 0; offset < 100; offset += 10)
        {
            using var doc = JsonDocument.Parse(await SpotifyGetAsync(
                $"https://api.spotify.com/v1/artists/{artist.Id}/albums?include_groups=album,single&limit=10&offset={offset}", ct));
            foreach (var a in doc.RootElement.Arr("items"))
            {
                int songs = a.Int("total_tracks") ?? 0;
                var kind = a.Str("album_type") switch { "album" or "compilation" => "album", "single" when songs >= 4 => "ep", _ => null };
                if (kind == null || a.Str("id") is not { } aid || a.Str("name") is not { Length: > 0 } title) continue;
                hits.Add(new SearchHit
                {
                    Service = Spotify, Collection = kind, Title = title, Songs = songs > 0 ? songs : null, Year = a.Str("release_date") is { Length: >= 4 } d ? d[..4] : null,
                    Thumb = a.Arr("images").OrderBy(i => Math.Abs((i.Int("width") ?? 0) - 300)).Select(i => i.Str("url")).FirstOrDefault(u => u != null),
                    Url = "https://open.spotify.com/album/" + aid, Rank = hits.Count,
                });
            }
            if (doc.RootElement.Str("next") == null) break;
        }
        return (artist.Name, hits);
    }

    // Apple Music's (the iTunes catalogue, free like Deezer's): the most relevant artist with exactly that name ("Ultimo"
    // has namesakes). Its EPs end in " - EP", its singles in " - Single" (or not: "(Single Version)", fewer than four songs).
    private static async Task<(string, List<SearchHit>)?> AppleArtistAlbumsAsync(string name, CancellationToken ct)
    {
        var country = L.English ? "us" : "it";
        string id, found;
        using (var s = JsonDocument.Parse(await GetAsync($"https://itunes.apple.com/search?term={Uri.EscapeDataString(name)}&entity=musicArtist&limit=10&country={country}", ct)))
        {
            var artist = s.RootElement.Arr("results").FirstOrDefault(a => Text.Normalize(a.Str("artistName")) == Text.Normalize(name));
            if (artist.ValueKind != JsonValueKind.Object || artist.Str("artistId") is not { } aid) return null;
            (id, found) = (aid, artist.Str("artistName") ?? name);
        }
        using var doc = JsonDocument.Parse(await GetAsync($"https://itunes.apple.com/lookup?id={id}&entity=album&limit=200&country={country}", ct));
        var hits = new List<SearchHit>();
        foreach (var a in doc.RootElement.Arr("results"))
        {
            if (a.Str("wrapperType") != "collection" || a.Str("artistId") != id || a.Str("collectionName") is not { Length: > 0 } title
                || a.Str("collectionViewUrl") is not { } url || title.EndsWith(" - Single")) continue;
            bool ep = title.EndsWith(" - EP");
            if (!ep && a.Int("trackCount") < 4) continue;
            hits.Add(new SearchHit
            {
                Service = AppleMusic, Collection = ep ? "ep" : "album", Title = ep ? title[..^5] : title, Songs = a.Int("trackCount"),
                Year = a.Str("releaseDate") is { Length: >= 4 } d ? d[..4] : null, Thumb = a.Str("artworkUrl100")?.Replace("100x100bb", "300x300bb"),
                Url = Regex.Replace(url, @"[?&]uo=\d+", ""), Rank = hits.Count,
            });
        }
        return (found, hits);
    }

    // Albums, EPs and playlists named by the words on one site (none on a site without them), each kind's best first:
    // opened like their link. Singles are left out, as for an artist.
    public static async Task<List<SearchHit>> CollectionsAsync(string service, string query, int limit, CancellationToken ct)
    {
        var lists = service switch
        {
            Deezer => new[] { DeezerAlbumsAsync(query, limit, ct), DeezerPlaylistsAsync(query, limit, ct) },
            YouTubeMusic => new[] { YouTubeMusicListsAsync(query, true, limit, ct), YouTubeMusicListsAsync(query, false, limit, ct) },
            YouTube => new[] { YouTubePlaylistsAsync(query, limit, ct) },
            SoundCloud => new[] { SoundCloudSetsAsync(query, limit, ct) },
            _ => Array.Empty<Task<List<SearchHit>>>(),
        };
        // (an album, a playlist, the next album…; not the lists the sites give for a song in them, without any of the words)
        var words = Text.Tokens(query);
        return (await Task.WhenAll(lists)).SelectMany(l => l).Where(h => Text.Tokens(h.Title + " " + h.Artist).Overlaps(words))
            .OrderBy(h => h.Rank).Take(limit).ToList();
    }

    // "60 songs", "303 videos", "1.234 brani": the number.
    private static int? CountIn(string? s)
        => s != null && Regex.Match(s, @"^\d[\d.,\u00a0]*") is { Success: true } m && int.TryParse(Regex.Replace(m.Value, @"\D", ""), out var n) ? n : null;

    private static async Task<List<SearchHit>> DeezerAlbumsAsync(string query, int limit, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await GetAsync($"https://api.deezer.com/search/album?q={Uri.EscapeDataString(query)}&limit={limit}", ct));
        var hits = new List<SearchHit>();
        foreach (var a in doc.RootElement.Arr("data"))
            if (a.Str("record_type") is "album" or "ep" && a.Str("id") is { } aid && a.Str("title") is { Length: > 0 } title)
                hits.Add(new SearchHit
                {
                    Service = Deezer, Collection = a.Str("record_type"), Title = title, Artist = a.Prop("artist")?.Str("name"), Songs = a.Int("nb_tracks"),
                    Thumb = a.Str("cover_medium"), Url = a.Str("link") ?? "https://www.deezer.com/album/" + aid, Rank = hits.Count,
                });
        return hits;
    }

    // YouTube Music's albums and EPs ("Album • Coldplay • 2014", the kind in the page's language), or its own playlists
    // ("YouTube Music • 60 songs"; the users' ones are YouTube's), as playlist links.
    private static async Task<List<SearchHit>> YouTubeMusicListsAsync(string query, bool albums, int limit, CancellationToken ct)
    {
        var json = await PostJsonAsync("https://music.youtube.com/youtubei/v1/search?prettyPrint=false", InnerTubeBody("WEB_REMIX", "1.20240904.01.00", query,
            albums ? "EgWKAQIYAWoKEAkQBRAKEAMQBA%3D%3D" : "EgeKAQQoADgBagwQDhAKEAMQBRAJEAQ%3D"), "https://music.youtube.com", ct);
        using var doc = JsonDocument.Parse(json);
        var hits = new List<SearchHit>();
        foreach (var row in MusicRows(doc.RootElement))
        {
            if (hits.Count >= limit) break;
            var cols = row.Arr("flexColumns").Select(c => c.Prop("musicResponsiveListItemFlexColumnRenderer")?.Prop("text")).ToList();
            if (cols.Count < 2) continue;
            var title = Runs(cols[0]);
            var parts = Runs(cols[1]).Split('•').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            var list = row.Prop("overlay")?.Prop("musicItemThumbnailOverlayRenderer")?.Prop("content")?.Prop("musicPlayButtonRenderer")
                ?.Prop("playNavigationEndpoint")?.Prop("watchPlaylistEndpoint")?.Str("playlistId");
            var kind = !albums ? "playlist" : parts.FirstOrDefault()?.ToLowerInvariant() switch { "ep" => "ep", "single" or "singolo" => null, _ => "album" };
            if (title.Length == 0 || parts.Count == 0 || list == null || kind == null) continue;
            hits.Add(new SearchHit
            {
                Service = YouTubeMusic, Collection = kind, Title = title, Artist = albums ? parts.ElementAtOrDefault(1) : parts[0],
                Year = albums && Regex.IsMatch(parts[^1], @"^\d{4}$") ? parts[^1] : null, Songs = albums ? null : CountIn(parts[^1]),
                Thumb = MusicThumb(row), Url = "https://music.youtube.com/playlist?list=" + list, Rank = hits.Count,
            });
        }
        return hits;
    }

    // YouTube's playlists (its users', the artists' own). Not a mix: it never ends.
    private static async Task<List<SearchHit>> YouTubePlaylistsAsync(string query, int limit, CancellationToken ct)
    {
        var json = await PostJsonAsync("https://www.youtube.com/youtubei/v1/search?prettyPrint=false",
            InnerTubeBody("WEB", "2.20240904.01.00", query, "EgIQAw%3D%3D"), "https://www.youtube.com", ct);
        using var doc = JsonDocument.Parse(json);
        var hits = new List<SearchHit>();
        var sections = doc.RootElement.Prop("contents")?.Prop("twoColumnSearchResultsRenderer")?.Prop("primaryContents")?.Prop("sectionListRenderer")
            ?.Arr("contents") ?? Enumerable.Empty<JsonElement>();
        foreach (var l in sections.SelectMany(s => s.Prop("itemSectionRenderer")?.Arr("contents") ?? Enumerable.Empty<JsonElement>())
                     .Select(c => c.Prop("lockupViewModel")).OfType<JsonElement>())
        {
            if (hits.Count >= limit) break;
            if (l.Str("contentType") != "LOCKUP_CONTENT_TYPE_PLAYLIST" || l.Str("contentId") is not { } id || id.StartsWith("RD") && !id.StartsWith("RDCLAK")) continue;
            var meta = l.Prop("metadata")?.Prop("lockupMetadataViewModel");
            if (meta?.Prop("title")?.Str("content") is not { Length: > 0 } title) continue;
            var owner = meta?.Prop("metadata")?.Prop("contentMetadataViewModel")?.Arr("metadataRows").FirstOrDefault().Arr("metadataParts").FirstOrDefault()
                .Prop("text")?.Str("content");
            var image = l.Prop("contentImage")?.Prop("collectionThumbnailViewModel")?.Prop("primaryThumbnail")?.Prop("thumbnailViewModel");
            // (the badge on the picture: "303 videos")
            var badge = (image?.Arr("overlays") ?? Enumerable.Empty<JsonElement>()).SelectMany(o => o.Prop("thumbnailOverlayBadgeViewModel")?.Arr("thumbnailBadges")
                ?? Enumerable.Empty<JsonElement>()).Select(b => b.Prop("thumbnailBadgeViewModel")?.Str("text")).FirstOrDefault(t => t != null);
            hits.Add(new SearchHit
            {
                Service = YouTube, Collection = "playlist", Title = title, Artist = owner, Songs = CountIn(badge),
                Thumb = image?.Prop("image")?.Arr("sources").FirstOrDefault().Str("url"), Url = "https://www.youtube.com/playlist?list=" + id, Rank = hits.Count,
            });
        }
        return hits;
    }

    // SoundCloud's sets: albums, EPs and the users' playlists.
    private static async Task<List<SearchHit>> SoundCloudSetsAsync(string query, int limit, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await SoundCloudGetAsync(c =>
            $"https://api-v2.soundcloud.com/search/playlists?q={Uri.EscapeDataString(query)}&client_id={c}&limit={Math.Min(50, limit * 2)}", ct));
        var hits = new List<SearchHit>();
        foreach (var p in doc.RootElement.Arr("collection"))
        {
            if (hits.Count >= limit) break;
            if (SoundCloudSet(p, hits.Count) is { } h) hits.Add(h);
        }
        return hits;
    }

    // A set as a hit, or null for an empty one or one of the other kinds (owner: false leaves who made it out, for an
    // artist's own).
    private static SearchHit? SoundCloudSet(JsonElement p, int rank, bool owner = true)
    {
        // (no kind: a playlist)
        var kind = p.Str("set_type") switch { "album" or "compilation" => "album", "ep" => "ep", null or "" => "playlist", _ => null };
        if (kind == null || p.Str("permalink_url") is not { } url || p.Str("title") is not { Length: > 0 } title || p.Int("track_count") is not > 0) return null;
        var art = p.Str("artwork_url") ?? p.Arr("tracks").Select(t => t.Str("artwork_url")).FirstOrDefault(a => a != null) ?? p.Prop("user")?.Str("avatar_url");
        return new SearchHit
        {
            Service = SoundCloud, Collection = kind, Title = title, Artist = owner ? p.Prop("user")?.Str("username") : null, Songs = p.Int("track_count"),
            Year = kind != "playlist" && (p.Str("release_date") ?? p.Str("display_date")) is { Length: >= 4 } d ? d[..4] : null,
            Thumb = art?.Replace("-large.", "-t300x300."), Url = url, Rank = rank,
        };
    }

    // Deezer's public playlists for the words (its editors' and its users'), the best match first.
    private static async Task<List<SearchHit>> DeezerPlaylistsAsync(string query, int limit, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await GetAsync($"https://api.deezer.com/search/playlist?q={Uri.EscapeDataString(query)}&limit={limit}", ct));
        var hits = new List<SearchHit>();
        foreach (var p in doc.RootElement.Arr("data"))
            if (p.Str("id") is { } pid && p.Str("title") is { Length: > 0 } title && p.Int("nb_tracks") is not 0)
                hits.Add(new SearchHit
                {
                    Service = Deezer, Collection = "playlist", Title = title, Artist = p.Prop("user")?.Str("name"), Songs = p.Int("nb_tracks"),
                    Thumb = p.Str("picture_medium"), Url = p.Str("link") ?? "https://www.deezer.com/playlist/" + pid, Rank = hits.Count,
                });
        return hits;
    }

    // The radio of the song's artist.
    private static async Task<List<SearchHit>> DeezerRadioAsync(RadioSeed seed, int limit, CancellationToken ct)
    {
        string? artist = null;
        if (seed.KeyOf("deezer") is { } track)
        {
            using var t = JsonDocument.Parse(await GetAsync("https://api.deezer.com/track/" + track, ct));
            artist = t.RootElement.Prop("artist")?.Str("id");
        }
        if (artist == null)
        {
            using var s = JsonDocument.Parse(await GetAsync($"https://api.deezer.com/search/track?q={Uri.EscapeDataString(seed.Query)}&limit=1", ct));
            artist = s.RootElement.Arr("data").FirstOrDefault().Prop("artist")?.Str("id");
        }
        if (artist == null) return new List<SearchHit>();
        using var doc = JsonDocument.Parse(await GetAsync($"https://api.deezer.com/artist/{artist}/radio?limit={Math.Min(50, limit)}", ct));
        return DeezerHits(doc.RootElement, limit);
    }
}

// The song suggestions start from: what it is, and its ids on the sites it came from ("youtube:ID", "soundcloud:ID"...).
public sealed record RadioSeed(string Title, string? Artist, IReadOnlyList<string> Keys)
{
    public string Query => $"{Artist?.Split(',')[0].Trim()} {SongMeta.CleanTitle(Title)}".Trim();

    public string? KeyOf(string site) => Keys.FirstOrDefault(k => k.StartsWith(site + ":", StringComparison.OrdinalIgnoreCase))?[(site.Length + 1)..];
}

// Where suggested songs come from: the song's own site ("auto": SoundCloud for SoundCloud songs, YouTube Music for the
// others), every site taking turns ("mix"), or one site.
public static class RadioSources
{
    public const string Auto = "auto", Mix = "mix";
    public static readonly string[] Sites = { OnlineSearchServices.YouTubeMusic, OnlineSearchServices.SoundCloud, OnlineSearchServices.Deezer };
    public static readonly string[] All = [Auto, Mix, .. Sites];

    // The sites to ask, one after the other until one has something.
    public static IEnumerable<string> Order(string source, RadioSeed seed)
    {
        var first = source != Auto ? source : seed.KeyOf("soundcloud") != null ? OnlineSearchServices.SoundCloud : OnlineSearchServices.YouTubeMusic;
        return new[] { first }.Concat(Sites).Distinct();
    }
}
