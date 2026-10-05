using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltimateMP3Player.Core;

// DRM-protected: metadata from embeds, audio from YouTube Music.
public static class Spotify
{
    private static readonly Regex UrlRx = new(@"(?:open\.spotify\.com/(?:intl-[a-z-]+/)?(?:embed/)?|spotify:)(track|album|playlist|artist|episode|show)[/:]([A-Za-z0-9]+)", RegexOptions.IgnoreCase);

    public static bool IsMatch(string url) => Sites.HostOf(url) is "open.spotify.com" or "spotify.link" or "spotify.com" || url.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase);

    private static async Task<JsonDocument> EmbedAsync(string type, string id, CancellationToken ct)
    {
        var html = await Http.GetStringAsync($"https://open.spotify.com/embed/{type}/{id}", ct: ct);
        var m = Regex.Match(html, "<script id=\"__NEXT_DATA__\" type=\"application/json\">(.*?)</script>", RegexOptions.Singleline);
        if (!m.Success) throw new EngineException(L.T("Spotify non ha restituito i dati di questo link."));
        return JsonDocument.Parse(m.Groups[1].Value);
    }

    private static JsonElement Entity(JsonDocument doc)
    {
        var e = doc.RootElement.Prop("props")?.Prop("pageProps")?.Prop("state")?.Prop("data")?.Prop("entity");
        return e ?? throw new EngineException(L.T("Contenuto Spotify non trovato (link privato o rimosso?)."));
    }

    private static string? Cover(JsonElement entity)
        => entity.Prop("visualIdentity")?.Arr("image")
               .OrderByDescending(i => i.Int("maxWidth") ?? 0)
               .Select(i => i.Str("url")).FirstOrDefault(u => u != null)
           ?? entity.Prop("coverArt")?.Arr("sources").OrderByDescending(i => i.Int("width") ?? 0).Select(i => i.Str("url")).FirstOrDefault();

    private static string? SmallCover(JsonElement entity)
        => entity.Prop("visualIdentity")?.Arr("image")
            .OrderBy(i => Math.Abs((i.Int("maxWidth") ?? 0) - 300))
            .Select(i => i.Str("url")).FirstOrDefault(u => u != null);

    public static async Task<AnalysisResult> AnalyzeAsync(string url, CancellationToken ct)
    {
        if (Sites.HostOf(url) == "spotify.link")
            url = (await Http.ProbeAsync(url, ct)).Url;
        var m = UrlRx.Match(url);
        if (!m.Success) throw new EngineException(L.T("Link Spotify non riconosciuto: usa il link di un brano, un album o una playlist."));
        var type = m.Groups[1].Value.ToLowerInvariant();
        var id = m.Groups[2].Value;
        if (type is "episode" or "show")
            throw new EngineException(L.T("I podcast di Spotify non si possono scaricare (sono protetti)."));

        using var doc = await EmbedAsync(type, id, ct);
        var ent = Entity(doc);
        var result = new AnalysisResult { Site = "Spotify", SourceUrl = url, PreferAudio = true };

        if (type == "track")
        {
            var artists = ent.Arr("artists").Select(a => a.Str("name")).Where(n => n != null).ToList();
            var item = new MediaItem
            {
                Title = ent.Str("name") ?? ent.Str("title") ?? L.T("Brano"),
                Artist = string.Join(", ", artists),
                Duration = ent.Num("duration") / 1000.0,
                Year = ent.Prop("releaseDate")?.Str("isoString") is { Length: >= 4 } d ? d[..4] : null,
                CoverUrl = Cover(ent),
                Kind = MediaKind.Audio,
                Source = SourceKind.Search,
                Url = $"https://open.spotify.com/track/{id}",
                PageUrl = $"https://open.spotify.com/track/{id}",
                SpotifyTrackId = id,
                SiteName = "Spotify",
            };
            if (SmallCover(ent) is { } sc) item.Thumbnails.Add(sc);
            result.Title = item.Title;
            result.Uploader = item.Artist;
            result.Thumbnails = item.Thumbnails.ToList();
            result.Items.Add(item);
            return result;
        }

        result.IsCollection = true;
        result.Title = ent.Str("name") ?? ent.Str("title") ?? "Spotify";
        result.Uploader = ent.Str("subtitle");
        if (SmallCover(ent) is { } cover) result.Thumbnails.Add(cover);
        string? albumCover = type == "album" ? Cover(ent) : null;
        string? albumYear = type == "album" && ent.Prop("releaseDate")?.Str("isoString") is { Length: >= 4 } y ? y[..4] : null;

        int n = 0;
        foreach (var t in ent.Arr("trackList"))
        {
            n++;
            var uri = t.Str("uri") ?? "";
            var trackId = uri.StartsWith("spotify:track:") ? uri["spotify:track:".Length..] : null;
            if (trackId == null) continue; // podcast episodes, local files
            var item = new MediaItem
            {
                Title = t.Str("title") ?? L.T("Brano"),
                Artist = t.Str("subtitle")?.Replace(" ", " "),
                Duration = t.Num("duration") / 1000.0,
                Kind = MediaKind.Audio,
                Source = SourceKind.Search,
                Url = $"https://open.spotify.com/track/{trackId}",
                PageUrl = $"https://open.spotify.com/track/{trackId}",
                SpotifyTrackId = trackId,
                SiteName = "Spotify",
            };
            if (type == "album")
            {
                item.Album = result.Title;
                item.TrackNo = n;
                item.CoverUrl = albumCover;
                item.Year = albumYear;
                if (result.Thumbnails.FirstOrDefault() is { } th) item.Thumbnails.Add(th);
            }
            result.Items.Add(item);
        }
        if (result.Items.Count == 0) throw new EngineException(L.T("Nessun brano scaricabile trovato in questo link Spotify."));
        if (type == "playlist" && result.Items.Count >= 100)
            result.Notes.Add(L.T("Spotify mostra al massimo 100 brani per le playlist condivise: sono elencati i primi 100."));
        return result;
    }

    // Fills album, cover, year from the track's Open Graph tags.
    public static async Task EnrichAsync(MediaItem item, CancellationToken ct)
    {
        if (item.SpotifyTrackId == null || (item.Album != null && item.CoverUrl != null && item.Year != null)) return;
        try
        {
            var html = await Http.GetStringAsync($"https://open.spotify.com/track/{item.SpotifyTrackId}", ua: Http.PreviewUA, ct: ct);
            var meta = OpenGraph.MetaTags(html);
            if (item.CoverUrl == null && meta.TryGetValue("og:image", out var img)) item.CoverUrl = img;
            if (item.Year == null && meta.TryGetValue("music:release_date", out var date) && date.Length >= 4) item.Year = date[..4];
            if (item.Album == null && meta.TryGetValue("og:description", out var desc))
            {
                // "Artist · Album · Song · 1987"
                var parts = desc.Split(" · ");
                if (parts.Length >= 4) item.Album = parts[1].Trim();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* metadata is optional */ }
    }
}

// Free public API; audio comes from YouTube Music.
public static class Deezer
{
    private static readonly Regex UrlRx = new(@"deezer\.com/(?:[a-z]{2}(?:-[a-z]{2})?/)?(track|album|playlist)/(\d+)", RegexOptions.IgnoreCase);

    public static bool IsMatch(string url) => Sites.Find(url)?.Name == "Deezer";

    public static async Task<AnalysisResult> AnalyzeAsync(string url, CancellationToken ct)
    {
        if (!UrlRx.IsMatch(url)) url = (await Http.ProbeAsync(url, ct)).Url;
        var m = UrlRx.Match(url);
        if (!m.Success) throw new EngineException(L.T("Link Deezer non riconosciuto: usa il link di un brano, un album o una playlist."));
        var type = m.Groups[1].Value.ToLowerInvariant();
        var id = m.Groups[2].Value;
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.deezer.com/{type}/{id}", ct: ct));
        var e = doc.RootElement;
        if (e.Prop("error") != null) throw new EngineException(L.T("Contenuto Deezer non trovato."));

        var result = new AnalysisResult { Site = "Deezer", SourceUrl = url, PreferAudio = true };
        MediaItem Track(JsonElement t, JsonElement? album, int? no) => new()
        {
            Title = t.Str("title") ?? L.T("Brano"),
            Artist = t.Prop("artist")?.Str("name"),
            Album = (album ?? t.Prop("album"))?.Str("title"),
            CoverUrl = (album ?? t.Prop("album"))?.Str("cover_xl"),
            Thumbnails = (album ?? t.Prop("album"))?.Str("cover_medium") is { } c ? new List<string> { c } : new(),
            Year = (e.Str("release_date") ?? t.Str("release_date")) is { Length: >= 4 } d ? d[..4] : null,
            TrackNo = no ?? t.Int("track_position"),
            Duration = t.Num("duration"),
            Kind = MediaKind.Audio,
            Source = SourceKind.Search,
            Url = t.Str("link") ?? url,
            PageUrl = t.Str("link") ?? url,
            SiteName = "Deezer",
        };

        if (type == "track")
        {
            var item = Track(e, null, null);
            result.Title = item.Title;
            result.Uploader = item.Artist;
            result.Thumbnails = item.Thumbnails.ToList();
            result.Items.Add(item);
            return result;
        }

        result.IsCollection = true;
        result.Title = e.Str("title") ?? "Deezer";
        result.Uploader = type == "album" ? e.Prop("artist")?.Str("name") : e.Prop("creator")?.Str("name");
        if ((e.Str("cover_medium") ?? e.Str("picture_medium")) is { } pic) result.Thumbnails.Add(pic);
        int n = 0;
        foreach (var t in e.Prop("tracks")?.Arr("data") ?? Enumerable.Empty<JsonElement>())
        {
            n++;
            result.Items.Add(type == "album" ? Track(t, e, n) : Track(t, null, null));
        }
        if (result.Items.Count == 0) throw new EngineException(L.T("Nessun brano trovato in questo link Deezer."));
        return result;
    }
}

// DRM-protected: metadata from the iTunes API (songs, albums) or the web page (playlists), audio from YouTube Music.
public static class AppleMusic
{
    private static readonly Regex UrlRx = new(@"(?:music|itunes)\.apple\.com/(?:([a-z]{2})/)?(album|song|playlist)/(?:[^/?#]+/)?(?:id)?([\w.-]+)", RegexOptions.IgnoreCase);

    public static bool IsMatch(string url) => Sites.Find(url)?.Name == "Apple Music";

    public static async Task<AnalysisResult> AnalyzeAsync(string url, CancellationToken ct)
    {
        if (!UrlRx.IsMatch(url)) url = (await Http.ProbeAsync(url, ct)).Url;
        var m = UrlRx.Match(url);
        if (!m.Success) throw new EngineException(L.T("Link Apple Music non riconosciuto: usa il link di un brano, un album o una playlist."));
        var country = m.Groups[1].Success ? m.Groups[1].Value.ToLowerInvariant() : "us";
        var type = m.Groups[2].Value.ToLowerInvariant();
        var id = m.Groups[3].Value;
        // An album link with ?i= is one of its songs.
        if (Regex.Match(url, @"[?&]i=(\d+)") is { Success: true } song)
        {
            type = "song";
            id = song.Groups[1].Value;
        }
        var result = new AnalysisResult { Site = "Apple Music", SourceUrl = url, PreferAudio = true };
        if (type == "playlist") return await PlaylistAsync(url, result, ct);

        using var doc = await LookupAsync(id, type == "album", country, ct);
        var all = doc.RootElement.Arr("results").ToList();
        // Some albums also have music videos among their tracks.
        var songs = all.Where(r => r.Str("wrapperType") == "track" && r.Str("kind") is "song" or "music-video").ToList();
        if (type == "song")
        {
            if (songs.Count == 0) throw new EngineException(L.T("Contenuto Apple Music non trovato (link privato o rimosso?)."));
            var item = Track(songs[0]);
            result.Title = item.Title;
            result.Uploader = item.Artist;
            result.Thumbnails = item.Thumbnails.ToList();
            result.Items.Add(item);
            return result;
        }

        var album = all.FirstOrDefault(r => r.Str("wrapperType") == "collection");
        result.IsCollection = true;
        result.Title = album.Str("collectionName") ?? "Apple Music";
        result.Uploader = album.Str("artistName");
        if (Art(album.Str("artworkUrl100"), 300) is { } cover) result.Thumbnails.Add(cover);
        foreach (var t in songs.OrderBy(s => s.Int("discNumber") ?? 1).ThenBy(s => s.Int("trackNumber") ?? 0)) result.Items.Add(Track(t));
        if (result.Items.Count == 0) throw new EngineException(L.T("Nessun brano trovato in questo link Apple Music."));
        return result;
    }

    // In the link's country first: the catalogue changes from one to another.
    private static async Task<JsonDocument> LookupAsync(string id, bool album, string country, CancellationToken ct)
    {
        foreach (var c in new[] { country, "us" }.Distinct())
        {
            var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://itunes.apple.com/lookup?id={id}&country={c}" + (album ? "&entity=song&limit=200" : ""), ct: ct));
            if (doc.RootElement.Arr("results").Any()) return doc;
            doc.Dispose();
        }
        throw new EngineException(L.T("Contenuto Apple Music non trovato (link privato o rimosso?)."));
    }

    private static MediaItem Track(JsonElement t)
    {
        var url = Regex.Replace(t.Str("trackViewUrl") ?? "", @"[?&]uo=\d+", "");
        var item = new MediaItem
        {
            Title = t.Str("trackName") ?? L.T("Brano"),
            Artist = t.Str("artistName"),
            Album = t.Str("collectionName"),
            Year = t.Str("releaseDate") is { Length: >= 4 } d ? d[..4] : null,
            TrackNo = t.Int("trackNumber"),
            Duration = t.Num("trackTimeMillis") / 1000.0,
            CoverUrl = Art(t.Str("artworkUrl100"), 1200),
            Kind = MediaKind.Audio,
            Source = SourceKind.Search,
            Url = url,
            PageUrl = url,
            SiteName = "Apple Music",
        };
        if (Art(t.Str("artworkUrl100"), 300) is { } th) item.Thumbnails.Add(th);
        return item;
    }

    // Playlists aren't in the iTunes API: their page carries the songs.
    private static async Task<AnalysisResult> PlaylistAsync(string url, AnalysisResult result, CancellationToken ct)
    {
        var html = await Http.GetStringAsync(url, ct: ct);
        var m = Regex.Match(html, "<script type=\"application/json\" id=\"serialized-server-data\">(.*?)</script>", RegexOptions.Singleline);
        if (!m.Success) throw new EngineException(L.T("Apple Music non ha restituito i dati di questo link."));
        using var doc = JsonDocument.Parse(m.Groups[1].Value);
        var sections = doc.RootElement.Arr("data").FirstOrDefault().Prop("data")?.Arr("sections") ?? Enumerable.Empty<JsonElement>();
        result.IsCollection = true;
        result.Title = "Apple Music";
        foreach (var s in sections)
        {
            if (s.Str("itemKind") == "containerDetailHeaderLockup")
            {
                var head = s.Arr("items").FirstOrDefault();
                result.Title = head.Str("title") ?? result.Title;
                result.Uploader = head.Arr("subtitleLinks").Select(l => l.Str("title")).FirstOrDefault(n => n != null);
                if (Art(head.Prop("artwork")?.Prop("dictionary")?.Str("url"), 300) is { } cover) result.Thumbnails.Add(cover);
            }
            if (s.Str("itemKind") != "trackLockup") continue;
            foreach (var t in s.Arr("items"))
            {
                var art = t.Prop("artwork")?.Prop("dictionary")?.Str("url");
                var link = t.Prop("contentDescriptor")?.Str("url") ?? url;
                var item = new MediaItem
                {
                    Title = t.Str("title") ?? L.T("Brano"),
                    Artist = t.Str("artistName") ?? string.Join(", ", t.Arr("subtitleLinks").Select(l => l.Str("title")).OfType<string>()),
                    Album = t.Arr("tertiaryLinks").Select(l => l.Str("title")).FirstOrDefault(n => n != null),
                    Duration = t.Num("duration") / 1000.0,
                    CoverUrl = Art(art, 1200),
                    Kind = MediaKind.Audio,
                    Source = SourceKind.Search,
                    Url = link,
                    PageUrl = link,
                    SiteName = "Apple Music",
                };
                if (Art(art, 300) is { } th) item.Thumbnails.Add(th);
                result.Items.Add(item);
            }
        }
        if (result.Items.Count == 0) throw new EngineException(L.T("Nessun brano trovato in questo link Apple Music."));
        if (result.Thumbnails.Count == 0) result.Thumbnails = result.Items[0].Thumbnails.ToList();
        // A long playlist: the page has only its first songs.
        if (Regex.Match(html, "\"numTracks\":\\s*(\\d+)") is { Success: true } total && int.Parse(total.Groups[1].Value) > result.Items.Count)
            result.Notes.Add(L.F("Apple Music mostra solo i primi {0} brani di questa playlist: sono elencati quelli.", result.Items.Count));
        return result;
    }

    // "…/100x100bb.jpg" from the API, "…/{w}x{h}bb.{f}" from the pages: at the wanted size.
    private static string? Art(string? url, int size)
        => url == null ? null : Regex.Replace(url.Replace("{w}", "100").Replace("{h}", "100").Replace("{f}", "jpg"), @"/\d+x\d+bb\.", $"/{size}x{size}bb.");
}

// Engines skip Giphy; GIF is at media.giphy.com/media/ID/giphy.gif.
public static class Giphy
{
    private static readonly Regex IdRx = new(@"giphy\.com/(?:gifs|stickers|embed|clips)/(?:[\w-]*-)?([A-Za-z0-9]{8,})(?:[/?#]|$)|giphy\.com/media/(?:v1\.[^/]+/)?([A-Za-z0-9]+)/|i\.giphy\.com/(?:media/)?([A-Za-z0-9]+)", RegexOptions.IgnoreCase);

    public static bool IsMatch(string url) => Sites.Find(url)?.Name == "Giphy";

    public static async Task<AnalysisResult> AnalyzeAsync(string url, CancellationToken ct)
    {
        if (Sites.HostOf(url) == "gph.is") url = (await Http.ProbeAsync(url, ct)).Url;
        var m = IdRx.Match(url);
        if (!m.Success) throw new EngineException(L.T("Link Giphy non riconosciuto: apri la GIF e copia il suo link."));
        var id = new[] { m.Groups[1], m.Groups[2], m.Groups[3] }.First(g => g.Success).Value;

        string title = "GIF " + id;
        int? w = null, h = null;
        try
        {
            var html = await Http.GetStringAsync($"https://giphy.com/gifs/{id}", ct: ct);
            foreach (Match ld in Regex.Matches(html, "<script type=\"application/ld\\+json\"[^>]*>(.*?)</script>", RegexOptions.Singleline))
            {
                using var d = JsonDocument.Parse(ld.Groups[1].Value);
                var root = d.RootElement;
                if (root.Str("headline") is { } head) title = Regex.Replace(head, @"\s+(GIF|Sticker)$", "", RegexOptions.IgnoreCase);
                var img = root.Prop("image");
                if (img != null)
                {
                    w = img.Value.Prop("width")?.Int("value");
                    h = img.Value.Prop("height")?.Int("value");
                }
                break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* title and size are optional */ }

        var item = new MediaItem
        {
            Title = title,
            Kind = MediaKind.Animated,
            Source = SourceKind.Direct,
            Url = $"https://media.giphy.com/media/{id}/giphy.gif",
            PageUrl = $"https://giphy.com/gifs/{id}",
            Ext = "gif",
            Width = w,
            Height = h,
            Thumbnails = new() { $"https://media.giphy.com/media/{id}/giphy_s.gif", $"https://media.giphy.com/media/{id}/200_s.gif" },
            SiteName = "Giphy",
        };
        return new AnalysisResult
        {
            Title = title, Site = "Giphy", SourceUrl = url, Thumbnails = item.Thumbnails.ToList(), Items = { item },
        };
    }
}

// Links straight to a file (…/photo.jpg, …/clip.mp4).
public static class Direct
{
    public static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase) { "jpg", "jpeg", "jfif", "png", "webp", "bmp", "avif", "tif", "tiff", "heic" };
    public static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase) { "mp4", "webm", "mov", "mkv", "m4v", "avi", "flv", "ts", "wmv", "3gp" };
    public static readonly HashSet<string> AudioExts = new(StringComparer.OrdinalIgnoreCase) { "mp3", "m4a", "wav", "flac", "ogg", "oga", "opus", "aac", "wma" };

    public static string? ExtOf(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var ext = Path.GetExtension(path).TrimStart('.');
            return ext.Length is > 0 and <= 5 ? ext.ToLowerInvariant() : null;
        }
        catch { return null; }
    }

    public static MediaKind? KindOfExt(string? ext)
    {
        if (ext == null) return null;
        if (ext.Equals("gif", StringComparison.OrdinalIgnoreCase)) return MediaKind.Animated;
        if (ImageExts.Contains(ext)) return MediaKind.Image;
        if (VideoExts.Contains(ext)) return MediaKind.Video;
        if (AudioExts.Contains(ext)) return MediaKind.Audio;
        return null;
    }

    public static bool LooksDirect(string url) => KindOfExt(ExtOf(url)) != null;

    public static AnalysisResult Analyze(string url)
    {
        var ext = ExtOf(url)!;
        var kind = KindOfExt(ext)!.Value;
        var name = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath));
        var item = new MediaItem
        {
            Title = string.IsNullOrWhiteSpace(name) ? "file" : name,
            Kind = kind,
            Source = SourceKind.Direct,
            Url = url,
            PageUrl = url,
            Ext = ext,
            SiteName = Sites.NameFor(url),
            FileNameHint = name,
        };
        if (kind is MediaKind.Image or MediaKind.Animated) item.Thumbnails.Add(url);
        return new AnalysisResult
        {
            Title = item.Title, Site = item.SiteName, SourceUrl = url, Thumbnails = item.Thumbnails.ToList(), Items = { item },
        };
    }
}

// Last resort: the media a page declares via Open Graph.
public static class OpenGraph
{
    public static Dictionary<string, string> MetaTags(string html)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match tag in Regex.Matches(html, @"<meta\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var key = Regex.Match(tag.Value, @"(?:property|name)\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            var val = Regex.Match(tag.Value, @"content\s*=\s*""([^""]*)""|content\s*=\s*'([^']*)'", RegexOptions.IgnoreCase);
            if (!key.Success || !val.Success) continue;
            var v = WebUtility.HtmlDecode(val.Groups[1].Success ? val.Groups[1].Value : val.Groups[2].Value);
            d.TryAdd(key.Groups[1].Value, v);
        }
        return d;
    }

    public static async Task<AnalysisResult?> TryAnalyzeAsync(string url, CancellationToken ct)
    {
        string html;
        try { html = await Http.GetStringAsync(url, ct: ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (HttpRequestException) { return null; }

        var meta = MetaTags(html);
        string? Get(params string[] keys) => keys.Select(k => meta.TryGetValue(k, out var v) ? v : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var video = Get("og:video:secure_url", "og:video:url", "og:video", "twitter:player:stream");
        var image = Get("og:image:secure_url", "og:image:url", "og:image", "twitter:image", "twitter:image:src");
        var title = Get("og:title", "twitter:title") ?? Sites.HostOf(url);
        string? mediaUrl = null;
        MediaKind kind;
        if (video != null && Direct.KindOfExt(Direct.ExtOf(MakeAbsolute(url, video))) is MediaKind.Video)
        {
            mediaUrl = MakeAbsolute(url, video);
            kind = MediaKind.Video;
        }
        else if (image != null)
        {
            mediaUrl = MakeAbsolute(url, image);
            kind = Direct.ExtOf(mediaUrl) == "gif" ? MediaKind.Animated : MediaKind.Image;
        }
        else return null;

        var item = new MediaItem
        {
            Title = title,
            Kind = kind,
            Source = SourceKind.Direct,
            Url = mediaUrl,
            PageUrl = url,
            Ext = Direct.ExtOf(mediaUrl),
            Width = int.TryParse(Get(kind == MediaKind.Video ? "og:video:width" : "og:image:width"), out var w) ? w : null,
            Height = int.TryParse(Get(kind == MediaKind.Video ? "og:video:height" : "og:image:height"), out var h) ? h : null,
            SiteName = Sites.NameFor(url),
        };
        if (image != null) item.Thumbnails.Add(MakeAbsolute(url, image));
        return new AnalysisResult
        {
            Title = title, Site = item.SiteName, SourceUrl = url, Thumbnails = item.Thumbnails.ToList(), Items = { item },
            Notes = { L.T("Il sito non è supportato direttamente: è stato trovato il contenuto principale della pagina.") },
        };
    }

    private static string MakeAbsolute(string pageUrl, string url)
        => Uri.TryCreate(new Uri(pageUrl), url, out var abs) ? abs.ToString() : url;
}
