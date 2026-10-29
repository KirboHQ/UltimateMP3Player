using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltimateMP3Player.Core;

public sealed record AnalyzeRequest(string Url, string? CookiesBrowser, bool WholePlaylist = false);

// Turns a link into downloadable items, picking the engine.
public static class Analyzer
{
    public static string NormalizeUrl(string input)
    {
        var s = input.Trim().Trim('"', '\'', '<', '>');
        var m = Regex.Match(s, @"(https?://|spotify:)\S+", RegexOptions.IgnoreCase);
        if (m.Success) s = m.Value;
        else if (Regex.IsMatch(s, @"^[\w-]+(\.[\w-]+)+(/\S*)?$")) s = "https://" + s;
        if (!s.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase) &&
            (!Uri.TryCreate(s, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")))
            throw new EngineException(L.T("This doesn't look like a valid link. Copy the full address (https://…)."));
        return s;
    }

    public static async Task<AnalysisResult> AnalyzeAsync(AnalyzeRequest req, CancellationToken ct)
    {
        var url = NormalizeUrl(req.Url);
        if (Spotify.IsMatch(url)) return await Spotify.AnalyzeAsync(url, ct);
        if (Deezer.IsMatch(url)) return await Deezer.AnalyzeAsync(url, ct);
        if (AppleMusic.IsMatch(url)) return await AppleMusic.AnalyzeAsync(url, ct);
        if (Giphy.IsMatch(url) && !url.Contains("/clips/")) return await Giphy.AnalyzeAsync(url, ct);
        if (Direct.LooksDirect(url) && Sites.Find(url) is null or { GalleryFirst: false }) return Direct.Analyze(url);

        var site = Sites.Find(url);
        bool galleryFirst = site?.GalleryFirst == true ||
                            (site?.Name == "TikTok" && url.Contains("/photo/", StringComparison.OrdinalIgnoreCase));
        var errors = new List<EngineException>();

        if (galleryFirst)
        {
            var g = await TryAsync(() => FromGalleryDl(url, req.CookiesBrowser, ct), errors);
            if (g != null)
            {
                if (g.Items.Any(i => i.Kind is MediaKind.Image or MediaKind.Animated)) return g;
                // Only videos: yt-dlp knows all qualities, gallery-dl one.
                var y = await TryAsync(() => FromYtDlp(url, req, ct), errors);
                return y ?? g;
            }
            var y2 = await TryAsync(() => FromYtDlp(url, req, ct), errors);
            if (y2 != null) return y2;
        }
        else
        {
            var y = await TryAsync(() => FromYtDlp(url, req, ct), errors);
            if (y != null) return y;
            if (errors.Any(e => e.NeedsLogin) && site?.Name is "YouTube" or "YouTube Music" or "Twitch" or "SoundCloud")
                throw errors.First(e => e.NeedsLogin);
            var g = await TryAsync(() => FromGalleryDl(url, req.CookiesBrowser, ct), errors);
            if (g != null) return g;
        }

        // Unknown sites only: elsewhere the engine error is more useful.
        if (site == null && errors.All(e => !e.NeedsLogin))
        {
            var og = await OpenGraph.TryAnalyzeAsync(url, ct);
            if (og != null) return og;
        }

        // Most meaningful error: login > specific > "unsupported".
        throw errors.FirstOrDefault(e => e.NeedsLogin)
              ?? errors.FirstOrDefault(e => !e.Unsupported && !e.NoMedia)
              ?? errors.FirstOrDefault(e => e.NoMedia)
              ?? errors.FirstOrDefault()
              ?? new EngineException(L.T("No downloadable content found in this link."));
    }

    private static async Task<AnalysisResult?> TryAsync(Func<Task<AnalysisResult>> f, List<EngineException> errors)
    {
        try { return await f(); }
        catch (EngineException ex) { errors.Add(ex); return null; }
    }

    private static readonly HashSet<string> AudioExtractors = new(StringComparer.OrdinalIgnoreCase)
    {
        "soundcloud", "bandcamp", "mixcloud", "audiomack", "soundcloudset", "soundcloudplaylist", "bandcampalbum",
    };

    public static async Task<AnalysisResult> FromYtDlp(string url, AnalyzeRequest req, CancellationToken ct)
    {
        bool playlistInVideo = IsVideoInPlaylist(url);
        using var doc = await YtDlp.DumpAsync(url, req.CookiesBrowser, ct, flat: true, noPlaylist: !req.WholePlaylist);
        var root = doc.RootElement;
        var siteName = Sites.NameFor(url);
        bool music = Sites.Find(url)?.Music == true;
        var result = new AnalysisResult { SourceUrl = url, Site = siteName, PreferAudio = music };

        var type = root.Str("_type");
        if (type is "playlist" or "multi_video")
        {
            result.IsCollection = true;
            result.Title = Text.FirstNonEmpty(root.Str("title"), root.Str("id")) ?? siteName;
            result.Uploader = Text.FirstNonEmpty(root.Str("uploader"), root.Str("channel"));
            result.Thumbnails = YtInfo.Thumbs(root);
            foreach (var e in root.Arr("entries"))
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                var item = e.Prop("formats") != null ? ItemFromInfo(YtInfo.Parse(e), siteName) : ItemFromFlat(e, siteName);
                if (item != null) result.Items.Add(item);
            }
            if (result.Items.Count == 0) throw new EngineException(L.T("The playlist is empty or not accessible.")) { NoMedia = true };
            if (siteName == "SoundCloud") await FillFromSoundCloudAsync(result.Items, ct);
            if (result.Thumbnails.Count == 0 && result.Items[0].Thumbnails.Count > 0) result.Thumbnails = result.Items[0].Thumbnails.ToList();
            if (result.Items.Count == 1 && root.Str("_type") == "multi_video") result.IsCollection = false;
            if (!result.IsCollection) { result.Title = result.Items[0].Title; }
            return result;
        }

        var info = YtInfo.Parse(root);
        if (info.Formats.Count == 0) throw new EngineException(L.T("No downloadable format found.")) { NoMedia = true };
        // (for the download: yt-dlp doesn't analyze it again)
        info.Json = root.GetRawText();
        var single = ItemFromInfo(info, siteName);
        result.Title = single.Title;
        result.Uploader = single.Artist ?? info.Uploader;
        result.Thumbnails = single.Thumbnails.ToList();
        result.Items.Add(single);
        result.PartOfPlaylist = playlistInVideo && !req.WholePlaylist;
        if (!info.HasVideo) result.PreferAudio = true;
        if (info.IsLive) result.Notes.Add(L.T("It's a live stream: it'll be recorded from now until the broadcast ends."));
        return result;
    }

    public static bool IsVideoInPlaylist(string url)
        => Regex.IsMatch(url, @"[?&]list=[\w-]+", RegexOptions.IgnoreCase) && Regex.IsMatch(url, @"[?&]v=[\w-]+|youtu\.be/", RegexOptions.IgnoreCase);

    public static MediaItem ItemFromInfo(YtInfo info, string siteName) => new()
    {
        Title = info.Track ?? info.Title,
        Artist = info.Track != null ? info.Artist : null,
        Album = info.Album,
        Year = info.Year,
        Duration = info.Duration,
        Thumbnails = info.Thumbnails,
        Kind = info.HasVideo ? MediaKind.Video : MediaKind.Audio,
        Source = SourceKind.YtDlp,
        Url = info.WebpageUrl,
        PageUrl = info.WebpageUrl,
        Info = info,
        SiteName = siteName,
    };

    private static MediaItem? ItemFromFlat(JsonElement e, string siteName)
    {
        var url = Text.FirstNonEmpty(e.Str("url"), e.Str("webpage_url"));
        if (url == null) return null;
        var ie = e.Str("ie_key") ?? "";
        if (!url.Contains("://"))
        {
            // Some extractors return bare ids in flat mode.
            if (ie.StartsWith("Youtube", StringComparison.OrdinalIgnoreCase)) url = "https://www.youtube.com/watch?v=" + url;
            else return null;
        }
        bool audio = AudioExtractors.Contains(ie) || Sites.Find(url)?.Music == true && Sites.Find(url)?.Name != "YouTube Music";
        var title = e.Str("title");
        bool guess = string.IsNullOrWhiteSpace(title) || title == e.Str("id");
        if (guess) title = TitleFromUrl(url) ?? e.Str("id") ?? "video";
        return new MediaItem
        {
            Title = title!,
            TitleIsGuess = guess,
            Artist = null,
            Duration = e.Num("duration"),
            Thumbnails = YtInfo.Thumbs(e),
            Kind = audio ? MediaKind.Audio : MediaKind.Video,
            Source = SourceKind.YtDlp,
            Url = url,
            PageUrl = url,
            SiteName = siteName,
        };
    }

    // ------------------------------------------------------------------ the same song on YouTube Music

    // A song its site won't give (DRM, removed, private: the reading of the link failed): its title, artist and cover from
    // the page (SoundCloud's oEmbed, the Open Graph tags, at worst the link itself) and an item searched on YouTube Music when
    // it's downloaded, like Spotify's songs. The original link stays its key.
    public static async Task<AnalysisResult> OnYouTubeAsync(string url, CancellationToken ct)
    {
        url = NormalizeUrl(url);
        var site = Sites.NameFor(url);
        string? title = null, artist = null, cover = null;
        if (site == "SoundCloud")
        {
            try { (title, artist, cover) = await SoundCloudCardAsync(url, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or JsonException or EngineException or OperationCanceledException) { }
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            try
            {
                var meta = OpenGraph.MetaTags(await Http.GetStringAsync(url, ct: ct));
                string? Get(params string[] keys) => keys.Select(k => meta.TryGetValue(k, out var v) ? v : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                title = Get("og:title", "twitter:title");
                artist ??= Get("music:musician_description", "twitter:audio:artist_name", "soundcloud:user");
                cover ??= Get("og:image:secure_url", "og:image", "twitter:image");
                // Bandcamp: "Song, by Artist"
                if (artist == null && title != null && Regex.Match(title, @"^(.+), by (.+)$") is { Success: true } m)
                {
                    title = m.Groups[1].Value;
                    artist = m.Groups[2].Value;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or EngineException or OperationCanceledException) { }
        }
        title = Text.FirstNonEmpty(title, TitleFromUrl(url));
        if (title == null) throw new EngineException(L.T("Can't find this link's title and artist: look the song up from the Search page."));
        if (cover != null && !cover.StartsWith("http", StringComparison.OrdinalIgnoreCase)) cover = null;
        var found = new MediaItem { Title = title, Artist = artist, CoverUrl = cover, Kind = MediaKind.Audio, Url = url, PageUrl = url, SiteName = site };
        if (cover != null) found.Thumbnails.Add(cover);
        var item = TrackDownloader.YouTubeFallback(found);
        var result = new AnalysisResult
        {
            Site = "YouTube Music", SourceUrl = url, PreferAudio = true, Title = item.Title, Uploader = item.Artist, Thumbnails = item.Thumbnails.ToList(),
            Notes = { L.F("It can't be downloaded from {0}: the same song will be looked up on YouTube Music.", site) },
        };
        result.Items.Add(item);
        return result;
    }

    // SoundCloud's own card of a song (its oEmbed, no key needed): title, uploader and cover.
    private static async Task<(string? Title, string? Artist, string? Cover)> SoundCloudCardAsync(string url, CancellationToken ct)
    {
        var json = await Http.GetStringAsync("https://soundcloud.com/oembed?format=json&url=" + Uri.EscapeDataString(url), ct: ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var artist = Text.FirstNonEmpty(root.Str("author_name"));
        var title = root.Str("title");
        // "Song by Artist"
        if (title != null && artist != null && title.EndsWith(" by " + artist, StringComparison.OrdinalIgnoreCase))
            title = title[..^(" by " + artist).Length];
        return (title, artist, root.Str("thumbnail_url"));
    }

    // yt-dlp reads a SoundCloud playlist as bare links (the title only from the link, no artist, no cover; they used to come
    // with the download, but the songs go to the cloud now): each song's card instead, a few at a time.
    private static Task FillFromSoundCloudAsync(List<MediaItem> items, CancellationToken ct) =>
        Parallel.ForEachAsync(items.Where(i => i.Info == null), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (item, token) =>
        {
            // (past the first songs the links are api-v2.soundcloud.com/tracks/<id>: the card knows them as api.soundcloud.com)
            var url = item.Url.Replace("://api-v2.soundcloud.com/", "://api.soundcloud.com/", StringComparison.OrdinalIgnoreCase);
            try
            {
                var (title, artist, cover) = await SoundCloudCardAsync(url, token);
                if (string.IsNullOrWhiteSpace(title)) return;
                item.Title = title;
                item.TitleIsGuess = false;
                // The uploader is the artist unless the title names one ("Artist - Song"), as for a downloaded song (SongMeta.From).
                if (!SongMeta.ArtistTitle.IsMatch(title)) item.Artist = artist;
                if (cover != null && cover.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) item.Thumbnails.Insert(0, cover);
            }
            catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or JsonException or EngineException or OperationCanceledException) { }
        });

    // "…/world-on-fire-1" → "World on fire 1", until the real title.
    private static string? TitleFromUrl(string url)
    {
        try
        {
            var seg = new Uri(url).AbsolutePath.TrimEnd('/').Split('/').LastOrDefault();
            if (string.IsNullOrEmpty(seg) || Regex.IsMatch(seg, @"^\d+$") || seg.Length < 3) return null;
            seg = Uri.UnescapeDataString(seg).Replace('-', ' ').Replace('_', ' ').Trim();
            return seg.Length == 0 ? null : char.ToUpper(seg[0]) + seg[1..];
        }
        catch { return null; }
    }

    public static async Task<AnalysisResult> FromGalleryDl(string url, string? cookies, CancellationToken ct)
    {
        var (dir, files, doc) = await GalleryDl.ListAsync(url, cookies, ct);
        using var _ = doc;
        var siteName = Sites.NameFor(url);
        var result = new AnalysisResult { SourceUrl = url, Site = siteName };

        int index = 0;
        foreach (var f in files)
        {
            index++;
            var m = f.Meta;
            bool viaYtDlp = f.Url.StartsWith("ytdl:", StringComparison.OrdinalIgnoreCase);
            var fileUrl = viaYtDlp ? f.Url[5..] : f.Url;
            var ext = (m.Str("extension") ?? Direct.ExtOf(fileUrl) ?? "").ToLowerInvariant();
            var kind = viaYtDlp ? MediaKind.Video : Direct.KindOfExt(ext) ?? MediaKind.Image;
            if (kind == MediaKind.Animated && ext == "gif" && m.Bool("is_animated") == false && m.Prop("is_animated") != null)
                kind = MediaKind.Image;

            var title = CleanTitle(Text.FirstNonEmpty(m.Str("title"), m.Str("description"), m.Str("content"))) ??
                        m.Str("filename") ?? siteName;

            var item = new MediaItem
            {
                Title = title,
                Kind = kind,
                Source = viaYtDlp ? SourceKind.YtDlp : SourceKind.Direct,
                Url = fileUrl,
                PageUrl = url,
                Ext = viaYtDlp ? null : ext,
                Width = m.Int("width") is > 0 and var w ? w : null,
                Height = m.Int("height") is > 0 and var h ? h : null,
                Size = m.Long("size") ?? m.Long("filesize"),
                Duration = m.Num("duration") is > 0 and var d ? d : null,
                GalleryUrl = url,
                GalleryIndex = index,
                FileNameHint = m.Str("filename"),
                SiteName = siteName,
            };
            if (kind is MediaKind.Image or MediaKind.Animated) item.Thumbnails.Add(ThumbFor(fileUrl));
            result.Items.Add(item);
        }

        var first = result.Items[0];
        result.IsCollection = result.Items.Count > 1;
        result.Thumbnails = result.Items.SelectMany(i => i.Thumbnails).Take(1).ToList();
        if (dir is { } dm)
        {
            result.Uploader = Text.FirstNonEmpty(
                dm.Prop("user")?.Str("name"), dm.Prop("author")?.Str("name"), dm.Prop("author")?.Str("nick"),
                dm.Prop("owner")?.Str("username"), dm.Str("username"), dm.Str("author"), dm.Str("uploader"), dm.Str("blog_name"));
            var title = CleanTitle(Text.FirstNonEmpty(dm.Str("title"), dm.Prop("album")?.Str("title"), dm.Str("album"),
                dm.Prop("board")?.Str("name"), dm.Prop("gallery")?.Str("title"), dm.Str("gallery"),
                dm.Prop("post")?.Str("title"), dm.Str("description"), dm.Str("content")));
            result.Title = title ?? first.Title;
        }
        else result.Title = first.Title;
        if (!result.IsCollection && result.Title.Length > 0) first.Title = result.Title;
        return result;
    }

    // Post text as title: no links/hashtags, one short line.
    private static string? CleanTitle(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = Regex.Replace(s, @"https?://\S+", " ");
        s = Regex.Replace(s, @"(\s#[\p{L}\p{N}_]+){3,}\s*$", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim(' ', '-', '|', '·');
        if (s.Length == 0) return null;
        if (s.Length > 120) s = s[..120].TrimEnd() + "…";
        return s;
    }

    // Smaller preview image, where the site offers one.
    private static string ThumbFor(string url)
    {
        if (url.Contains("pbs.twimg.com", StringComparison.OrdinalIgnoreCase))
            return Regex.Replace(url, @"name=\w+", "name=small");
        var m = Regex.Match(url, @"^(https?://i\.imgur\.com/\w{7})\.(jpe?g|png|webp)$", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value + "m.jpg";
        return url;
    }
}

// Finds a song on YouTube Music by title/artist/duration.
public static class MusicMatcher
{
    private static readonly string[] Unwanted = { "instrumental", "karaoke", "cover", "remix", "live", "acoustic", "sped", "slowed", "reverb", "nightcore", "8d", "lyrics", "tutorial", "reaction" };

    internal static double TitleScore(string wantedTitle, string wantedArtist, string candidate)
    {
        var w = Text.Normalize(wantedTitle);
        var c = Text.Normalize(candidate);
        double score = Text.Coverage(Regex.Replace(w, @"\b(feat|ft|with)\b.*", ""), c);
        var wTokens = Text.Tokens(wantedTitle + " " + wantedArtist);
        foreach (var bad in Unwanted)
            if (Text.Tokens(candidate).Contains(bad) && !wTokens.Contains(bad)) score -= 0.4;
        return score;
    }

    public static async Task<YtInfo> FindAsync(MediaItem item, string? cookies, Action<string> status, CancellationToken ct)
    {
        var artist = item.Artist?.Split(',')[0].Trim() ?? "";
        var query = $"{artist} {item.Title}".Trim();
        status(L.T("Searching YouTube Music…"));

        var candidates = new List<(string Id, string Title, int Rank)>();
        try
        {
            var music = await SearchMusicAsync(query, cookies, ct);
            candidates = music.Select((r, i) => (r.Id, r.Title, i)).OrderByDescending(c => ResultScore(item.Title, artist, c.Title, c.i)).ToList();
        }
        catch (EngineException) { }

        YtInfo? best = null;
        double bestScore = double.MinValue;
        foreach (var (id, title, rank) in candidates.Take(3))
        {
            YtInfo info;
            try { info = await YtDlp.GetInfoAsync("https://music.youtube.com/watch?v=" + id, cookies, ct); }
            catch (EngineException) { continue; }
            var (total, sure) = Score(item.Title, artist, item.Duration, title, rank, info);
            if (total > bestScore) { best = info; bestScore = total; }
            if (sure) return info;
        }
        if (best != null && bestScore > MinScore) return best;

        status(L.T("Searching YouTube…"));
        var yt = await YtDlp.SearchYouTubeAsync($"{artist} - {item.Title} audio", 5, cookies, ct);
        var pick = yt
            .Select((r, i) => (r, s: TitleScore(item.Title, artist, r.Title) + DurationScore(item.Duration, r.Duration) +
                                     (Text.Coverage(artist, r.Title + " " + r.Channel) > 0.5 ? 0.3 : 0) - i * 0.05))
            .OrderByDescending(x => x.s)
            .FirstOrDefault();
        // Nothing that looks like the song: not found, rather than another song under this one's title.
        if (pick.r.Id != null && pick.s > MinScore)
            return await YtDlp.GetInfoAsync("https://www.youtube.com/watch?v=" + pick.r.Id, cookies, ct);
        throw new EngineException(L.T("Song not found on YouTube Music."));
    }

    private const double MinScore = 0.3;

    // YouTube Music's "Songs" results over HTTP (half a second, not a yt-dlp started for each song); yt-dlp's when that fails.
    private static async Task<List<(string Id, string Title)>> SearchMusicAsync(string query, string? cookies, CancellationToken ct)
    {
        try
        {
            var hits = await OnlineSearchServices.MatchCandidatesAsync(query, 5, ct);
            if (hits.Count > 0) return hits.Select(h => (h.Url[(h.Url.IndexOf("v=") + 2)..], h.Title)).ToList();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or JsonException or OperationCanceledException) { }
        return await YtDlp.SearchMusicAsync(query, 5, cookies, ct);
    }

    // Written in different alphabets ("Racing Into The Night" and "夜に駆ける"): their words can't be compared.
    private static bool OtherScript(string a, string b) => HasLatin(a) != HasLatin(b);
    private static bool HasLatin(string s) => s.Any(c => c < (char)0x250 && char.IsLetter(c));

    // YouTube Music's first result in another alphabet is its own guess for the song's other name (the English title
    // of a Japanese song): nearly a match, if the artist and the length agree (Score).
    private static bool OtherName(string wantedTitle, string candidate, int rank) => rank == 0 && OtherScript(wantedTitle, candidate);

    // A YouTube Music result's title against the song's, its place among the results counted (first = 0).
    public static double ResultScore(string wantedTitle, string artist, string candidate, int rank)
        => (OtherName(wantedTitle, candidate, rank) ? 0.8 : TitleScore(wantedTitle, artist, candidate)) - rank * 0.05;

    // A result read (its length and artist known) against the song: its total, and whether it's surely the song (the
    // others needn't be read).
    public static (double Total, bool Sure) Score(string wantedTitle, string artist, double? duration, string candidate, int rank, YtInfo info)
    {
        double title = ResultScore(wantedTitle, artist, candidate, rank), length = DurationScore(duration, info.Duration), who = ArtistScore(artist, info, lenient: true);
        if (OtherName(wantedTitle, candidate, rank)) return (length < 0 || who < 0 ? length + who : title + length + who, false);
        return (title + length + who, title > 0.7 && length >= 0 && who > 0);
    }

    // Only a close match (the same title and artist, the length within a few seconds when known), or null: for a song whose
    // own link no longer works, so a remix isn't replaced by the original. A connection that fails throws.
    public static async Task<YtInfo?> FindCloseAsync(MediaItem item, string? cookies, CancellationToken ct)
    {
        var artist = item.Artist?.Split(',')[0].Trim() ?? "";
        var query = $"{artist} {item.Title}".Trim();
        bool Close(double? duration) => item.Duration is not > 0 || duration is not > 0 || Math.Abs(item.Duration.Value - duration.Value) <= 8;

        var music = await SearchMusicAsync(query, cookies, ct);
        foreach (var (id, score) in music.Select((r, i) => (r.Id, TitleScore(item.Title, artist, r.Title) - i * 0.05)).OrderByDescending(c => c.Item2).Take(3))
        {
            if (score < 0.6) continue;
            YtInfo info;
            try { info = await YtDlp.GetInfoAsync("https://music.youtube.com/watch?v=" + id, cookies, ct); }
            catch (EngineException ex) when (!ex.Offline) { continue; }
            if (Close(info.Duration) && ArtistScore(artist, info) >= 0) return info;
        }
        var yt = await YtDlp.SearchYouTubeAsync($"{artist} - {item.Title}", 5, cookies, ct);
        foreach (var (r, s) in yt.Select((r, i) => (r, s: TitleScore(item.Title, artist, r.Title) - i * 0.05)).OrderByDescending(x => x.s).Take(3))
        {
            if (s < 0.6 || !Close(r.Duration)) continue;
            if (artist.Length > 0 && Text.Coverage(artist, r.Title + " " + r.Channel) < 0.5) continue;
            try { return await YtDlp.GetInfoAsync("https://www.youtube.com/watch?v=" + r.Id, cookies, ct); }
            catch (EngineException ex) when (!ex.Offline) { }
        }
        return null;
    }

    private static double DurationScore(double? wanted, double? got)
    {
        if (wanted is not > 0 || got is not > 0) return 0;
        var diff = Math.Abs(wanted.Value - got.Value);
        if (diff <= 3) return 0.5;
        if (diff <= 8) return 0.2;
        if (diff <= 20) return -0.2;
        return -1;
    }

    // lenient: an artist written in another alphabet ("Kenshi Yonezu" and "米津玄師") is neither confirmed nor refused.
    private static double ArtistScore(string artist, YtInfo info, bool lenient = false)
    {
        if (artist.Length == 0) return 0;
        var who = $"{info.Artist} {info.Uploader} {info.Title}";
        if (Text.Coverage(artist, who) >= 0.5) return 0.4;
        return lenient && OtherScript(artist, info.Artist ?? info.Uploader ?? "") ? 0 : -0.3;
    }
}
