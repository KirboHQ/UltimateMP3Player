using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace UltimateMP3Player.Core;

// Who an artist of the library is on a music site: Deezer's (most of them), SoundCloud's or YouTube Music's (one Deezer doesn't
// know, or knows only as a namesake: Envy on SoundCloud is not "envy" the Japanese band). Its picture, its popular songs, its
// albums and releases come from there.
public sealed class ArtistSite
{
    public string Service { get; set; } = OnlineSearchServices.Deezer;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Picture { get; set; }
    public int Fans { get; set; }
    public string? Url { get; set; }
    // Chosen by hand ("Choose the artist…"): never looked for again.
    public bool Mine { get; set; }
    public DateTime Found { get; set; } = DateTime.Now;
    // How many of the library's songs of the name it has (the proof it's them; 0 = picked by its name alone), and how many
    // the library had then: more now and none proved, it's looked for again.
    public int Songs { get; set; }
    public int Had { get; set; }

    // Its picture's file (covers/artists): Deezer's by its id alone, as before 4.3.1.
    [JsonIgnore]
    public string PictureKey => Service switch { OnlineSearchServices.SoundCloud => "sc-" + Id, OnlineSearchServices.YouTubeMusic => "ym-" + Id, _ => Id };
    [JsonIgnore] public string? DeezerId => Service == OnlineSearchServices.Deezer ? Id : null;
    [JsonIgnore] public string Key => Service + "|" + Id;
}

// The artist each name of the library is, app-wide like the library (artists.json): found by its songs, or chosen by hand.
public static class ArtistSites
{
    private static readonly object Lock = new();
    private static Dictionary<string, ArtistSite>? _map;
    private static readonly DebouncedSaver Saver = new(Save);
    private static readonly SemaphoreSlim Gate = new(2);
    private static readonly Dictionary<string, Task<ArtistSite?>> Running = new();
    // Names that couldn't be looked for this session (no connection): not asked again at every list shown.
    private static readonly HashSet<string> Failed = new();
    public static readonly TimeSpan Every = TimeSpan.FromDays(30);

    // A name's artist changed (chosen by hand, or found): its pictures and pages show the new one.
    public static event Action<string>? Changed;

    private static string FilePath => Path.Combine(AppPaths.DataDir, "artists.json");
    private static Dictionary<string, ArtistSite> Map => _map ??= JsonStore.Load<Dictionary<string, ArtistSite>>(FilePath);

    public static ArtistSite? Get(string? name)
    {
        var key = Text.Normalize(name);
        lock (Lock) return key.Length > 0 ? Map.GetValueOrDefault(key) : null;
    }

    public static void Set(string name, ArtistSite site)
    {
        var key = Text.Normalize(name);
        if (key.Length == 0) return;
        lock (Lock) Map[key] = site;
        Saver.Request();
        Changed?.Invoke(name);
    }

    // The artist renamed in the library: what was found or chosen for it goes along.
    public static void Rename(string from, string to)
    {
        lock (Lock)
        {
            if (!Map.Remove(Text.Normalize(from), out var site)) return;
            Map[Text.Normalize(to)] = site;
        }
        Saver.Request();
    }

    public static void Flush() => Saver.Flush();

    private static void Save()
    {
        Dictionary<string, ArtistSite> copy;
        lock (Lock) copy = new Dictionary<string, ArtistSite>(Map);
        JsonStore.Save(FilePath, copy);
    }

    private static bool Stale(ArtistSite s, int songs) => !s.Mine && (DateTime.Now - s.Found > Every || s.Songs == 0 && songs > s.Had);

    // The artist the name is for these songs of the library: the one chosen by hand, else the one found before (looked for again
    // now and then, or when new songs of it may prove another one), else looked for now. Null: none found, or no connection.
    public static Task<ArtistSite?> ResolveAsync(string name, IReadOnlyCollection<Track> songs)
    {
        var key = Text.Normalize(name);
        if (key.Length == 0) return Task.FromResult<ArtistSite?>(null);
        var had = Get(name);
        if (had != null && !Stale(had, songs.Count)) return Task.FromResult<ArtistSite?>(had);
        lock (Running)
        {
            if (Failed.Contains(key)) return Task.FromResult(had);
            if (Running.TryGetValue(key, out var running)) return running;
            return Running[key] = Find();
        }

        async Task<ArtistSite?> Find()
        {
            await Task.Yield();
            try
            {
                await Gate.WaitAsync();
                try
                {
                    var best = (await CandidatesAsync(name, songs, CancellationToken.None)).FirstOrDefault();
                    if (best == null) return had;
                    best.Had = songs.Count;
                    // (looked for again, the same one: only its date and details change)
                    Set(name, best);
                    return best;
                }
                finally { Gate.Release(); }
            }
            catch
            {
                lock (Running) Failed.Add(key);
                return had;
            }
            finally { lock (Running) Running.Remove(key); }
        }
    }

    // Chosen by hand: kept for good.
    public static void Choose(string name, ArtistSite site)
    {
        site.Mine = true;
        site.Found = DateTime.Now;
        lock (Running) Failed.Remove(Text.Normalize(name));
        Set(name, site);
    }

    // Every artist the name may be on Deezer, YouTube Music and SoundCloud, the likeliest first: the ones that have the most of
    // these songs (a few of them looked for on each site: "Envy HOLD ONTO ME"), then Deezer's, YouTube Music's, SoundCloud's,
    // each site's best known first. The songs' own site wins a tie (songs from SoundCloud: its artist).
    public static async Task<List<ArtistSite>> CandidatesAsync(string name, IReadOnlyCollection<Track> songs, CancellationToken ct)
    {
        var key = Text.Normalize(name);
        var found = new Dictionary<string, ArtistSite>();
        var votes = new Dictionary<string, int>();
        static async Task<List<T>> Try<T>(Func<Task<List<T>>> f)
        {
            try { return await f(); }
            catch (OperationCanceledException) { throw; }
            catch { return new List<T>(); }
        }

        var named = await Task.WhenAll(
            Try(() => OnlineSearchServices.DeezerArtistsNamedAsync(name, ct)),
            Try(() => OnlineSearchServices.MusicArtistsNamedAsync(name, ct)),
            Try(() => OnlineSearchServices.SoundCloudArtistsNamedAsync(name, ct)));
        foreach (var s in named.SelectMany(n => n)) found.TryAdd(s.Key, s);

        // (the songs played or added last are the likeliest to be found; three are enough to tell namesakes apart)
        var proofs = songs.Where(t => !string.IsNullOrWhiteSpace(t.Title)).OrderByDescending(t => t.Added).Take(3).ToList();
        var sites = new[] { OnlineSearchServices.Deezer, OnlineSearchServices.YouTubeMusic, OnlineSearchServices.SoundCloud };
        var searched = await Task.WhenAll(proofs.SelectMany(t => sites.Select(async site =>
        {
            // (plain words on Deezer too: its artist:"…" track:"…" search answers nothing since 2026; the hits are checked below)
            var query = $"{name} {SongMeta.CleanTitle(t.Title)}";
            var hits = await Try(() => OnlineSearchServices.SearchAsync(site, query, 10, ct));
            // (each artist counts a song once, whatever the copies of it)
            return hits.Where(h => h.ArtistId != null && ArtistNames.Has(h.Artist, name) && SameSong(h.Title, t.Title))
                .Select(h => h.Service + "|" + h.ArtistId).Distinct().ToList();
        })));
        foreach (var k in searched.SelectMany(s => s)) votes[k] = votes.GetValueOrDefault(k) + 1;

        // Proved but not among the ones named exactly so (Deezer's search for "envy" lists the band, not the other Envy).
        foreach (var k in votes.Keys.Where(k => !found.ContainsKey(k)).OrderByDescending(k => votes[k]).Take(3).ToList())
        {
            var (service, id) = (k[..k.IndexOf('|')], k[(k.IndexOf('|') + 1)..]);
            try
            {
                if (await OnlineSearchServices.ArtistSiteByIdAsync(service, id, ct) is { } s && Text.Normalize(s.Name) == key) found[k] = s;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        var from = songs.Select(t => Sites.Find(t.SourceUrl ?? "")?.Name).Where(n => n != null).GroupBy(n => n).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        foreach (var s in found.Values) s.Songs = votes.GetValueOrDefault(s.Key);
        return found.Values.Where(s => Text.Normalize(s.Name) == key)
            .OrderByDescending(s => s.Songs)
            .ThenByDescending(s => s.Songs > 0 && s.Service == from)
            .ThenBy(s => Array.IndexOf(sites, s.Service))
            .ThenByDescending(s => s.Fans).ToList();
    }

    // The same song under the site's title: the same clean title, or one the start of the other ("Song" and "Song (Remastered)").
    public static bool SameSong(string a, string b)
    {
        var (x, y) = (SongMeta.MatchKey(a), SongMeta.MatchKey(b));
        if (x.Length == 0 || y.Length == 0) return false;
        return x == y || x.Length >= 4 && y.Length >= 4 && (x.StartsWith(y + " ") || y.StartsWith(x + " "));
    }

    // ------------------------------------------------------------------ what the page shows of it

    // Its songs, the most listened first (skip: how many were given already; SoundCloud and YouTube Music give one page).
    public static Task<List<SearchHit>> TopAsync(ArtistSite s, int limit, int skip, CancellationToken ct) => s.Service switch
    {
        OnlineSearchServices.SoundCloud => OnlineSearchServices.SoundCloudTopAsync(s.Id, limit, skip, ct),
        OnlineSearchServices.YouTubeMusic => skip > 0 ? Task.FromResult(new List<SearchHit>()) : OnlineSearchServices.MusicTopAsync(s.Id, limit, ct),
        _ => OnlineSearchServices.ArtistTopAsync(s.Id, limit, skip, ct),
    };

    // Its releases, the newest first (YouTube Music says only their year: none).
    public static Task<List<Release>> ReleasesAsync(ArtistSite s, string name, int limit, CancellationToken ct) => s.Service switch
    {
        OnlineSearchServices.SoundCloud => OnlineSearchServices.SoundCloudReleasesAsync(s.Id, name, limit, ct),
        OnlineSearchServices.YouTubeMusic => Task.FromResult(new List<Release>()),
        _ => OnlineSearchServices.ReleasesAsync(s.Id, name, limit, ct),
    };

    // Its albums and EPs on its own site.
    public static async Task<List<SearchHit>> AlbumsAsync(ArtistSite s, CancellationToken ct) => s.Service switch
    {
        OnlineSearchServices.SoundCloud => await OnlineSearchServices.SoundCloudAlbumsOfAsync(s.Id, ct),
        OnlineSearchServices.YouTubeMusic => await OnlineSearchServices.MusicAlbumsOfAsync(s.Id, ct),
        _ => await OnlineSearchServices.DeezerAlbumsOfAsync(s.Id, ct),
    };
}

public static partial class OnlineSearchServices
{
    private static ArtistSite DeezerSite(ArtistInfo a) => new()
    {
        Service = Deezer, Id = a.Id, Name = a.Name, Picture = a.Picture, Fans = a.Fans, Url = "https://www.deezer.com/artist/" + a.Id,
    };

    public static async Task<List<ArtistSite>> DeezerArtistsNamedAsync(string name, CancellationToken ct)
        => (await SearchArtistsAsync(name, 10, ct)).Where(a => Text.Normalize(a.Name) == Text.Normalize(name)).Select(DeezerSite).ToList();

    private static ArtistSite? SoundCloudSite(JsonElement u, string? name)
    {
        if (u.Str("id") is not { } id) return null;
        var full = u.Str("full_name");
        var shown = name != null && Text.Normalize(full) == Text.Normalize(name) ? full! : u.Str("username") ?? full ?? "";
        // (SoundCloud's grey placeholder isn't a picture)
        var avatar = u.Str("avatar_url") is { } a && !a.Contains("default_avatar") ? a.Replace("-large.", "-t500x500.") : null;
        return new ArtistSite
        {
            Service = SoundCloud, Id = id, Name = shown, Picture = avatar, Fans = u.Int("followers_count") ?? 0, Url = u.Str("permalink_url"),
        };
    }

    public static async Task<List<ArtistSite>> SoundCloudArtistsNamedAsync(string name, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await SoundCloudGetAsync(c => $"https://api-v2.soundcloud.com/search/users?q={Uri.EscapeDataString(name)}&client_id={c}&limit=10", ct));
        var key = Text.Normalize(name);
        return doc.RootElement.Arr("collection").Where(u => Text.Normalize(u.Str("username")) == key || Text.Normalize(u.Str("full_name")) == key)
            .Select(u => SoundCloudSite(u, name)).OfType<ArtistSite>().ToList();
    }

    // "9.78K subscribers", "1,2 Mln di iscritti": about how many.
    private static int CountOf(string? text)
    {
        var m = Regex.Match(text ?? "", @"(\d+(?:[.,]\d+)?)\s*(K|M|B|Mln|Mrd|Mila)?\b", RegexOptions.IgnoreCase);
        if (!m.Success || !double.TryParse(m.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Float, Text.Inv, out var n)) return 0;
        return (int)Math.Min(int.MaxValue, n * m.Groups[2].Value.ToLowerInvariant() switch { "k" or "mila" => 1e3, "m" or "mln" => 1e6, "b" or "mrd" => 1e9, _ => 1 });
    }

    // YouTube Music's artists named exactly so (its artists search, in English: "Artist • 9.78K subscribers").
    public static async Task<List<ArtistSite>> MusicArtistsNamedAsync(string name, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await PostJsonAsync("https://music.youtube.com/youtubei/v1/search?prettyPrint=false",
            InnerTubeBody("WEB_REMIX", "1.20240904.01.00", name, "EgWKAQIgAWoKEAkQBRAKEAMQBA%3D%3D", local: false), "https://music.youtube.com", ct));
        var list = new List<ArtistSite>();
        foreach (var row in MusicRows(doc.RootElement))
        {
            var cols = row.Arr("flexColumns").Select(c => Runs(c.Prop("musicResponsiveListItemFlexColumnRenderer")?.Prop("text"))).ToList();
            if (cols.Count == 0 || Text.Normalize(cols[0]) != Text.Normalize(name)
                || row.Prop("navigationEndpoint")?.Prop("browseEndpoint")?.Str("browseId") is not { } id) continue;
            list.Add(new ArtistSite
            {
                Service = YouTubeMusic, Id = id, Name = cols[0], Fans = CountOf(cols.ElementAtOrDefault(1)?.Split('•').LastOrDefault()),
                Picture = MusicThumb(row) is { } t ? Regex.Replace(t, @"=w\d+-h\d+", "=w544-h544") : null, Url = "https://music.youtube.com/channel/" + id,
            });
        }
        return list;
    }

    // An artist found by its id on its site (one of its songs proved it).
    public static async Task<ArtistSite?> ArtistSiteByIdAsync(string service, string id, CancellationToken ct)
    {
        switch (service)
        {
            case Deezer:
                return await ArtistByIdAsync(id, ct) is { } a ? DeezerSite(a) : null;
            case SoundCloud:
                {
                    using var doc = JsonDocument.Parse(await SoundCloudGetAsync(c => $"https://api-v2.soundcloud.com/users/{id}?client_id={c}", ct));
                    return SoundCloudSite(doc.RootElement, null);
                }
            case YouTubeMusic:
                {
                    using var doc = JsonDocument.Parse(await MusicBrowseAsync(id, null, ct));
                    var h = doc.RootElement.Prop("header")?.Prop("musicImmersiveHeaderRenderer") ?? doc.RootElement.Prop("header")?.Prop("musicVisualHeaderRenderer");
                    if (h is not { } header || Runs(header.Prop("title")) is not { Length: > 0 } title) return null;
                    // (its header's picture is a wide banner: the middle square of it)
                    var pic = (header.Prop("foregroundThumbnail") ?? header.Prop("thumbnail"))?.Prop("musicThumbnailRenderer")?.Prop("thumbnail")?.Arr("thumbnails").LastOrDefault().Str("url");
                    return new ArtistSite
                    {
                        Service = YouTubeMusic, Id = id, Name = title, Url = "https://music.youtube.com/channel/" + id,
                        Fans = CountOf(Runs(header.Prop("subscriptionButton")?.Prop("subscribeButtonRenderer")?.Prop("subscriberCountText"))),
                        Picture = pic != null ? Regex.Replace(pic, @"=w\d+-h\d+.*$", "=w544-h544-p-l90-rj") : null,
                    };
                }
        }
        return null;
    }

    // SoundCloud's "popular tracks" of a user; its uploads by plays when it has none.
    public static async Task<List<SearchHit>> SoundCloudTopAsync(string id, int limit, int skip, CancellationToken ct)
    {
        using (var doc = JsonDocument.Parse(await SoundCloudGetAsync(c => $"https://api-v2.soundcloud.com/users/{id}/toptracks?client_id={c}&limit={limit}&offset={skip}", ct)))
            if (SoundCloudHits(doc.RootElement, limit) is { Count: > 0 } top) return top;
        if (skip > 0) return new List<SearchHit>();
        using var all = JsonDocument.Parse(await SoundCloudGetAsync(c => $"https://api-v2.soundcloud.com/users/{id}/tracks?client_id={c}&limit=50", ct));
        var hits = SoundCloudHits(all.RootElement, 50);
        var plays = all.RootElement.Arr("collection").Where(t => t.Str("permalink_url") != null)
            .ToDictionary(t => t.Str("permalink_url")!, t => t.Num("playback_count") ?? 0);
        return hits.OrderByDescending(h => plays.GetValueOrDefault(h.Url)).Take(limit).ToList();
    }

    // A SoundCloud user's tracks and albums of the last weeks (its singles are its tracks).
    public static async Task<List<Release>> SoundCloudReleasesAsync(string id, string artist, int limit, CancellationToken ct)
    {
        var list = new List<Release>();
        foreach (var (path, kind) in new[] { ("tracks", "single"), ("albums", "album") })
        {
            using var doc = JsonDocument.Parse(await SoundCloudGetAsync(c => $"https://api-v2.soundcloud.com/users/{id}/{path}?client_id={c}&limit={limit}", ct));
            foreach (var t in doc.RootElement.Arr("collection"))
            {
                if (t.Str("title") is not { Length: > 0 } title || t.Str("permalink_url") is not { } url || t.Str("policy") is "SNIP" or "BLOCK"
                    || !DateTime.TryParse(t.Str("display_date") ?? t.Str("release_date") ?? t.Str("created_at"), Text.Inv, System.Globalization.DateTimeStyles.AdjustToUniversal, out var date))
                    continue;
                list.Add(new Release
                {
                    Artist = artist, Title = title, Kind = kind == "album" && t.Str("set_type") == "ep" ? "ep" : kind, Date = date.Date, Url = url,
                    Cover = (t.Str("artwork_url") ?? t.Prop("user")?.Str("avatar_url"))?.Replace("-large.", "-t500x500."),
                });
            }
        }
        return list.OrderByDescending(r => r.Date).ToList();
    }

    // The "Top songs" shelf of a YouTube Music artist's page.
    public static async Task<List<SearchHit>> MusicTopAsync(string id, int limit, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await MusicBrowseAsync(id, null, ct));
        var hits = new List<SearchHit>();
        foreach (var row in BrowseSections(doc.RootElement).Select(s => s.Prop("musicShelfRenderer")).OfType<JsonElement>().Take(1)
                     .SelectMany(s => s.Arr("contents")).Select(c => c.Prop("musicResponsiveListItemRenderer")).OfType<JsonElement>())
        {
            if (hits.Count >= limit) break;
            var cols = row.Arr("flexColumns").Select(c => c.Prop("musicResponsiveListItemFlexColumnRenderer")?.Prop("text")).ToList();
            if (row.Prop("playlistItemData")?.Str("videoId") is not { } vid || cols.Count < 2 || Runs(cols[0]) is not { Length: > 0 } title) continue;
            hits.Add(new SearchHit
            {
                Service = YouTubeMusic, Title = title, Artist = Runs(cols[1]) is { Length: > 0 } a ? a : null, Album = cols.Count > 3 ? Runs(cols[3]) : null,
                Thumb = MusicThumb(row), Url = "https://music.youtube.com/watch?v=" + vid, Rank = hits.Count, ArtistId = id,
            });
        }
        return hits;
    }
}
