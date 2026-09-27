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
            throw new EngineException(L.T("Questo non sembra un link valido. Copia l'indirizzo completo (https://…)."));
        return s;
    }

    public static async Task<AnalysisResult> AnalyzeAsync(AnalyzeRequest req, CancellationToken ct)
    {
        var url = NormalizeUrl(req.Url);
        if (Spotify.IsMatch(url)) return await Spotify.AnalyzeAsync(url, ct);
        if (Deezer.IsMatch(url)) return await Deezer.AnalyzeAsync(url, ct);
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
              ?? new EngineException(L.T("Nessun contenuto scaricabile trovato in questo link."));
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
            if (result.Items.Count == 0) throw new EngineException(L.T("La playlist è vuota o non accessibile.")) { NoMedia = true };
            if (result.Thumbnails.Count == 0 && result.Items[0].Thumbnails.Count > 0) result.Thumbnails = result.Items[0].Thumbnails.ToList();
            if (result.Items.Count == 1 && root.Str("_type") == "multi_video") result.IsCollection = false;
            if (!result.IsCollection) { result.Title = result.Items[0].Title; }
            return result;
        }

        var info = YtInfo.Parse(root);
        if (info.Formats.Count == 0) throw new EngineException(L.T("Nessun formato scaricabile trovato.")) { NoMedia = true };
        var single = ItemFromInfo(info, siteName);
        result.Title = single.Title;
        result.Uploader = single.Artist ?? info.Uploader;
        result.Thumbnails = single.Thumbnails.ToList();
        result.Items.Add(single);
        result.PartOfPlaylist = playlistInVideo && !req.WholePlaylist;
        if (!info.HasVideo) result.PreferAudio = true;
        if (info.IsLive) result.Notes.Add(L.T("È una diretta: verrà registrata da adesso fino alla fine della trasmissione."));
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

    private static double TitleScore(string wantedTitle, string wantedArtist, string candidate)
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
        status(L.T("Ricerca su YouTube Music…"));

        var candidates = new List<(string Id, double Score)>();
        try
        {
            var music = await YtDlp.SearchMusicAsync(query, 5, cookies, ct);
            candidates = music.Select((r, i) => (r.Id, TitleScore(item.Title, artist, r.Title) - i * 0.05)).OrderByDescending(c => c.Item2).ToList();
        }
        catch (EngineException) { }

        YtInfo? best = null;
        double bestScore = double.MinValue;
        foreach (var (id, score) in candidates.Take(3))
        {
            YtInfo info;
            try { info = await YtDlp.GetInfoAsync("https://music.youtube.com/watch?v=" + id, cookies, ct); }
            catch (EngineException) { continue; }
            double total = score + DurationScore(item.Duration, info.Duration) + ArtistScore(artist, info);
            if (total > bestScore) { best = info; bestScore = total; }
            if (score > 0.7 && DurationScore(item.Duration, info.Duration) >= 0 && ArtistScore(artist, info) > 0) return info;
        }
        if (best != null && bestScore > 0.3) return best;

        status(L.T("Ricerca su YouTube…"));
        var yt = await YtDlp.SearchYouTubeAsync($"{artist} - {item.Title} audio", 5, cookies, ct);
        var pick = yt
            .Select((r, i) => (r, s: TitleScore(item.Title, artist, r.Title) + DurationScore(item.Duration, r.Duration) +
                                     (Text.Coverage(artist, r.Title + " " + r.Channel) > 0.5 ? 0.3 : 0) - i * 0.05))
            .OrderByDescending(x => x.s)
            .FirstOrDefault();
        if (pick.r.Id != null && (best == null || pick.s > bestScore))
            return await YtDlp.GetInfoAsync("https://www.youtube.com/watch?v=" + pick.r.Id, cookies, ct);
        return best ?? throw new EngineException(L.T("Brano non trovato su YouTube Music."));
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

    private static double ArtistScore(string artist, YtInfo info)
    {
        if (artist.Length == 0) return 0;
        var who = $"{info.Artist} {info.Uploader} {info.Title}";
        return Text.Coverage(artist, who) >= 0.5 ? 0.4 : -0.3;
    }
}
