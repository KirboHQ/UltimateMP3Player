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
}

// The sites the search box can search, without accounts or keys: YouTube Music (official songs), YouTube (videos,
// covers, live), SoundCloud (remixes, mashups) and Deezer (a catalogue like Spotify's; the audio comes from YouTube
// Music, as for Deezer and Spotify links). Spotify itself refuses anonymous searches.
public static class OnlineSearchServices
{
    public const string YouTubeMusic = "YouTube Music", YouTube = "YouTube", SoundCloud = "SoundCloud", Deezer = "Deezer";
    public static readonly string[] Default = { YouTubeMusic, SoundCloud, YouTube, Deezer };

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

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

    private static string InnerTubeBody(string client, string version, string query, string filter) => JsonSerializer.Serialize(new
    {
        context = new { client = new { clientName = client, clientVersion = version, hl = Lang, gl = "IT" } },
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

    // ------------------------------------------------------------------ YouTube Music: the "Songs" results

    private static async Task<List<SearchHit>> YouTubeMusicAsync(string query, int limit, CancellationToken ct)
    {
        var json = await PostJsonAsync("https://music.youtube.com/youtubei/v1/search?prettyPrint=false",
            InnerTubeBody("WEB_REMIX", "1.20240904.01.00", query, "EgWKAQIIAWoKEAkQBRAKEAMQBA%3D%3D"), "https://music.youtube.com", ct);
        using var doc = JsonDocument.Parse(json);
        var hits = new List<SearchHit>();
        var sections = doc.RootElement.Prop("contents")?.Prop("tabbedSearchResultsRenderer")?.Arr("tabs").FirstOrDefault()
            .Prop("tabRenderer")?.Prop("content")?.Prop("sectionListRenderer")?.Arr("contents") ?? Enumerable.Empty<JsonElement>();
        foreach (var shelf in sections.Select(s => s.Prop("musicShelfRenderer")).OfType<JsonElement>())
            foreach (var row in shelf.Arr("contents").Select(c => c.Prop("musicResponsiveListItemRenderer")).OfType<JsonElement>())
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
                string? PageType(JsonElement r) => r.Prop("navigationEndpoint")?.Prop("browseEndpoint")?.Prop("browseEndpointContextSupportedConfigs")
                    ?.Prop("browseEndpointContextMusicConfig")?.Str("pageType");
                double? duration = groups.Select(g => ParseClock(Join(g))).LastOrDefault(d => d != null);
                var album = groups.Skip(1).FirstOrDefault(g => g.Any(r => PageType(r) == "MUSIC_PAGE_TYPE_ALBUM")) is { } ag ? Join(ag) : null;
                var artist = groups[0].Count > 0 && ParseClock(Join(groups[0])) == null ? Join(groups[0]) : null;
                var thumb = row.Prop("thumbnail")?.Prop("musicThumbnailRenderer")?.Prop("thumbnail")?.Arr("thumbnails").LastOrDefault().Str("url");
                if (thumb != null) thumb = Regex.Replace(thumb, @"=w\d+-h\d+", "=w300-h300");
                hits.Add(new SearchHit
                {
                    Service = YouTubeMusic, Title = title, Artist = artist, Album = album, Duration = duration, Thumb = thumb, Rank = hits.Count,
                    Url = "https://music.youtube.com/watch?v=" + id,
                });
            }
        return hits;
    }

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
            throw new EngineException(L.T("SoundCloud non ha risposto come previsto."));
        }
        finally { ScGate.Release(); }
    }

    private static async Task<List<SearchHit>> SoundCloudAsync(string query, int limit, CancellationToken ct)
    {
        string json;
        var client = await SoundCloudClientAsync(false, ct);
        string Url(string c) => $"https://api-v2.soundcloud.com/search/tracks?q={Uri.EscapeDataString(query)}&client_id={c}&limit={Math.Min(50, limit * 2)}";
        try { json = await GetAsync(Url(client), ct); }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            json = await GetAsync(Url(await SoundCloudClientAsync(true, ct)), ct);
        }
        using var doc = JsonDocument.Parse(json);
        var hits = new List<SearchHit>();
        foreach (var t in doc.RootElement.Arr("collection"))
        {
            if (hits.Count >= limit) break;
            // SNIP = only a 30-second preview without SoundCloud Go, BLOCK = not playable here.
            if (t.Str("policy") is "SNIP" or "BLOCK" || t.Str("permalink_url") is not { } url || t.Str("title") is not { Length: > 0 } title) continue;
            var art = t.Str("artwork_url") ?? t.Prop("user")?.Str("avatar_url");
            hits.Add(new SearchHit
            {
                Service = SoundCloud, Title = title,
                Artist = t.Prop("publisher_metadata")?.Str("artist") is { Length: > 0 } a ? a : t.Prop("user")?.Str("username"),
                Duration = t.Num("duration") / 1000.0, Thumb = art?.Replace("-large.", "-t300x300."), Url = url, Rank = hits.Count,
            });
        }
        return hits;
    }

    // ------------------------------------------------------------------ Deezer (free public API)

    private static async Task<List<SearchHit>> DeezerAsync(string query, int limit, CancellationToken ct)
    {
        var json = await GetAsync($"https://api.deezer.com/search/track?q={Uri.EscapeDataString(query)}&limit={limit}", ct);
        using var doc = JsonDocument.Parse(json);
        var hits = new List<SearchHit>();
        foreach (var t in doc.RootElement.Arr("data"))
        {
            if (hits.Count >= limit) break;
            if (t.Str("link") is not { } url || t.Str("title") is not { Length: > 0 } title) continue;
            hits.Add(new SearchHit
            {
                Service = Deezer, Title = title, Artist = t.Prop("artist")?.Str("name"), Album = t.Prop("album")?.Str("title"), Duration = t.Num("duration"),
                Thumb = t.Prop("album")?.Str("cover_medium"), Url = url, Rank = hits.Count,
            });
        }
        return hits;
    }
}
