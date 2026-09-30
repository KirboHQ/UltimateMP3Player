using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltimateMP3Player.Core;

// What the lyrics search found for a song (Track.Lyrics; null = never searched).
public enum LyricsKind { None, Plain, Synced, Instrumental }

// One line of the lyrics: when it's sung (seconds, -1 without times). Empty text = a pause in the singing.
public sealed record LyricLine(double Time, string Text);

public sealed class LyricsText
{
    public bool Synced { get; init; }
    public List<LyricLine> Lines { get; init; } = new();
}

// "[mm:ss.xx] text" lines (LRC): several stamps on one line, [offset:ms], word stamps <mm:ss.xx> dropped.
public static class Lrc
{
    private static readonly Regex Stamp = new(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]");
    private static readonly Regex Offset = new(@"^\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex WordStamp = new(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>");

    public static List<LyricLine> Parse(string lrc)
    {
        var lines = new List<LyricLine>();
        // A positive offset makes the lyrics come earlier.
        double offset = Offset.Match(lrc) is { Success: true } o ? int.Parse(o.Groups[1].Value, CultureInfo.InvariantCulture) / 1000.0 : 0;
        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var stamps = new List<double>();
            int at = 0;
            Match m;
            while ((m = Stamp.Match(line, at)).Success && m.Index == at)
            {
                double sec = double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                stamps.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60 + sec - offset);
                at = m.Index + m.Length;
            }
            if (stamps.Count == 0) continue;
            var text = WordStamp.Replace(line[at..], "").Trim();
            text = Regex.Replace(text, @"\s{2,}", " ");
            foreach (var t in stamps) lines.Add(new LyricLine(Math.Max(0, t), text));
        }
        lines = lines.OrderBy(l => l.Time).ToList();
        // Pauses only matter between sung lines: none at the start, none twice in a row.
        var clean = new List<LyricLine>();
        foreach (var l in lines)
        {
            if (l.Text.Length == 0 && (clean.Count == 0 || clean[^1].Text.Length == 0)) continue;
            clean.Add(l);
        }
        while (clean.Count > 0 && clean[^1].Text.Length == 0) clean.RemoveAt(clean.Count - 1);
        return clean;
    }

    // The words only, for a plain copy of synced lyrics.
    public static string ToPlain(string lrc) => string.Join("\n", Parse(lrc).Select(l => l.Text));
}

// The lyrics files: DataDir\lyrics\<track id>.lrc (with times) or .txt (plain), shared by every profile like the songs.
public static class LyricsStore
{
    public static string PathFor(string trackId, bool synced) => Path.Combine(AppPaths.LyricsDir, trackId + (synced ? ".lrc" : ".txt"));

    public static LyricsText? Load(Track t)
    {
        try
        {
            if (t.Lyrics == LyricsKind.Synced && File.Exists(PathFor(t.Id, true)))
            {
                var lines = Lrc.Parse(File.ReadAllText(PathFor(t.Id, true)));
                if (lines.Count > 0) return new LyricsText { Synced = true, Lines = lines };
            }
            if (t.Lyrics is LyricsKind.Plain or LyricsKind.Synced && File.Exists(PathFor(t.Id, false)))
            {
                var lines = File.ReadAllText(PathFor(t.Id, false)).Replace("\r", "").Split('\n').Select(s => new LyricLine(-1, s.Trim())).ToList();
                while (lines.Count > 0 && lines[0].Text.Length == 0) lines.RemoveAt(0);
                while (lines.Count > 0 && lines[^1].Text.Length == 0) lines.RemoveAt(lines.Count - 1);
                if (lines.Count > 0) return new LyricsText { Synced = false, Lines = lines };
            }
        }
        catch { }
        return null;
    }

    // Writes what was found and sets Track.Lyrics (the caller saves the library).
    public static void Save(Track t, LyricsResult r)
    {
        Delete(t.Id);
        if (r.Kind == LyricsKind.Synced && r.Synced != null) File.WriteAllText(PathFor(t.Id, true), r.Synced);
        if (r.Kind is LyricsKind.Synced or LyricsKind.Plain && r.Plain != null) File.WriteAllText(PathFor(t.Id, false), r.Plain);
        t.Lyrics = r.Kind;
    }

    public static void Delete(string trackId)
    {
        foreach (var synced in new[] { true, false })
            try { File.Delete(PathFor(trackId, synced)); } catch { }
    }

    // A song of "Listen together" saved in the library takes its lyrics along.
    public static void Copy(string fromId, Track to, LyricsKind? kind)
    {
        if (kind == null) return;
        try
        {
            foreach (var synced in new[] { true, false })
                if (File.Exists(PathFor(fromId, synced))) File.Copy(PathFor(fromId, synced), PathFor(to.Id, synced), true);
            to.Lyrics = kind;
        }
        catch { }
    }

    // Files of songs no longer in the library (deleted elsewhere, rooms left while the app crashed).
    public static void Sweep(Func<string, bool> exists)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.LyricsDir))
            {
                var id = Path.GetFileNameWithoutExtension(f);
                if (!exists(id) && DateTime.Now - File.GetLastWriteTime(f) > TimeSpan.FromDays(1)) File.Delete(f);
            }
        }
        catch { }
    }
}

public sealed class LyricsResult
{
    public LyricsKind Kind { get; init; }
    public string? Synced { get; init; }
    public string? Plain { get; init; }
    // The song it was matched with (for the tests and the log).
    public string? Match { get; init; }
}

// Lyrics from LRCLIB (lrclib.net): a free, open archive of lyrics with times, no account or key.
// The song is matched on title, artist and length. The times are kept only for the same recording: a version of
// another length, or a remix matched to the original, gets the words without the times (they would be off).
public static class LyricsFinder
{
    private const string Api = "https://lrclib.net/api/";
    // How far the lengths may differ for the times to still be right.
    private const double SyncSlack = 5;
    // Artist not confirmed (the "artist" is often who uploaded it): same title, same version and same length only.
    private const double StrictSlack = 2.5;

    public static string UserAgent { get; set; } = "UltimateMP3Player (https://github.com/KirboHQ/UltimateMP3Player)";

    private sealed record Candidate(string Title, string Artist, double? Duration, bool Instrumental, string? Plain, string? Synced);

    // One way to read the song. Named: the artist is written in the title ("Artist - Song"), so an answer by someone
    // else is another song; otherwise the artist may just be who uploaded it. Loose: the title up to its first bracket
    // ("The Hills (Amice remix) dbd version" → "The Hills"), only with the artist confirmed.
    private sealed record Guess(string Query, string Core, string? LooseQuery, string? Loose, List<string> Artists, string? Artist, bool Named);

    private static readonly Regex Brackets = new(@"\s*[\(\[\{【<][^\)\]\}】>]*[\)\]\}】>]");
    private static readonly Regex Feat = new(@"\s+(feat|ft|featuring|prod)\.?\s.*$", RegexOptions.IgnoreCase);
    private static readonly Regex ArtistDash = new(@"^(.{1,80}?)\s+[-–—]\s+(.+)$");
    private static readonly Regex ArtistSplit = new(@",|&|\+|/| x | e | and | feat\.? | ft\.? | featuring | with | w/ ", RegexOptions.IgnoreCase);
    // Versions without singing.
    private static readonly Regex NoVocals = new(@"\b(instrumental|karaoke|backing track|8[\s-]?bit)\b", RegexOptions.IgnoreCase);
    // Words that make it another version of a song.
    private static readonly Regex VersionWords = new(
        @"\b(remix|rmx|edit|edition|rework|bootleg|flip|vip|mashup|cover|sped|slowed|reverb|nightcore|daycore|hardstyle|hardtekk|hardtek|frenchcore|schranz|phonk|acoustic|live|extended|version|mix)\b");
    private static readonly Regex SameVersionWords = new(@"\b(original mix|radio edit|album version|original version|version original|versione originale)\b");

    // The title without "(Official Video)", "(feat. X)", "[Remix]"...: what the archive calls the song.
    public static string CoreTitle(string title) => Text.Normalize(Feat.Replace(Brackets.Replace(SongMeta.CleanTitle(title), ""), ""));

    private static string Squash(string? s) => Text.Normalize(s).Replace(" ", "");

    private static List<string> Artists(string? s)
        => string.IsNullOrWhiteSpace(s) ? new List<string>()
            : ArtistSplit.Split(" " + s + " ").Select(a => Squash(SongMeta.CleanChannel(a))).Where(a => a.Length > 0).Distinct().ToList();

    private static bool SameArtist(List<string> wanted, string candidate)
    {
        var have = Artists(candidate);
        return wanted.Any(w => have.Any(h => h == w || (Math.Min(h.Length, w.Length) >= 4 && (h.Contains(w) || w.Contains(h)))));
    }

    private static bool SameTitle(string? wantedCore, string candidateTitle)
    {
        if (string.IsNullOrEmpty(wantedCore)) return false;
        // "Creep;Creep" and other doubled names in the archive.
        return candidateTitle.Split(';').Append(candidateTitle).Select(CoreTitle).Where(c => c.Length > 0)
            .Any(c => c == wantedCore || c.Replace(" ", "") == wantedCore.Replace(" ", ""));
    }

    private static HashSet<string> Versions(string title)
        => VersionWords.Matches(SameVersionWords.Replace(Text.Normalize(title), " ")).Select(m => m.Value).ToHashSet();

    private static List<Guess> Guesses(string title, string? artist)
    {
        var list = new List<Guess>();
        var clean = SongMeta.CleanTitle(title);
        var bare = Feat.Replace(Brackets.Replace(clean, ""), "").Trim();
        var uploader = string.IsNullOrWhiteSpace(artist) ? null : artist.Trim();
        string? LooseOf(string s)
        {
            int cut = s.IndexOfAny(new[] { '(', '[', '{', '【', '<' });
            var q = cut > 0 ? Feat.Replace(s[..cut], "").Trim() : null;
            return q is { Length: > 0 } && Text.Normalize(q) != Text.Normalize(Feat.Replace(Brackets.Replace(s, ""), "")) ? q : null;
        }
        if (ArtistDash.Match(bare) is { Success: true } m)
        {
            var named = m.Groups[1].Value.Trim();
            var song = m.Groups[2].Value.Trim();
            var songFull = ArtistDash.Match(clean) is { Success: true } full ? full.Groups[2].Value.Trim() : song;
            var loose = LooseOf(songFull);
            list.Add(new Guess(song, Text.Normalize(song), loose, loose != null ? Text.Normalize(loose) : null,
                Artists(named).Concat(Artists(uploader)).Distinct().ToList(), named, true));
        }
        var looseAll = LooseOf(clean);
        list.Add(new Guess(bare, Text.Normalize(bare), looseAll, looseAll != null ? Text.Normalize(looseAll) : null, Artists(uploader), uploader, false));
        return list;
    }

    public static async Task<LyricsResult> FindAsync(string title, string? artist, double duration, CancellationToken ct)
    {
        if (NoVocals.IsMatch(title)) return new LyricsResult { Kind = LyricsKind.Instrumental, Match = "title" };
        var guesses = Guesses(title, artist).Where(g => g.Core.Length > 0).ToList();
        if (guesses.Count == 0) return new LyricsResult { Kind = LyricsKind.None };
        var versions = Versions(title);

        var asked = new HashSet<string>();
        var pool = new List<Candidate>();
        LyricsResult? best = null;
        int bestScore = -1;
        // Enough: the times with the artist confirmed, or an instrumental.
        bool Done() => bestScore >= 5 || best?.Kind == LyricsKind.Instrumental;

        async Task Ask(string url)
        {
            if (Done() || !asked.Add(url)) return;
            var got = await GetAsync(url, ct);
            pool.AddRange(got);
            (best, bestScore) = Pick(pool, guesses, versions, duration);
        }

        var d = duration > 0 ? "&duration=" + Math.Round(duration).ToString(CultureInfo.InvariantCulture) : "";
        foreach (var g in guesses.Where(g => g.Artist != null))
        {
            var art = Uri.EscapeDataString(g.Artist!);
            await Ask($"{Api}get?track_name={Uri.EscapeDataString(g.Query)}&artist_name={art}{d}");
            await Ask($"{Api}search?track_name={Uri.EscapeDataString(g.Query)}&artist_name={art}");
            if (g.LooseQuery != null) await Ask($"{Api}search?track_name={Uri.EscapeDataString(g.LooseQuery)}&artist_name={art}");
        }
        // The title alone: the answers' artists are checked (or the length, when there's no artist to check).
        foreach (var g in guesses) await Ask($"{Api}search?track_name={Uri.EscapeDataString(g.Query)}");
        return best ?? new LyricsResult { Kind = LyricsKind.None };
    }

    private static (LyricsResult?, int) Pick(List<Candidate> pool, List<Guess> guesses, HashSet<string> versions, double duration)
    {
        LyricsResult? best = null;
        int bestScore = -1;
        double bestDiff = double.MaxValue;
        foreach (var c in pool)
        {
            double diff = duration > 0 && c.Duration is > 0 ? Math.Abs(c.Duration.Value - duration) : double.MaxValue;
            bool sameVersion = versions.IsSubsetOf(Versions(c.Title));
            // The best reading of the song this answer fits.
            bool matched = false, artistOk = false;
            foreach (var g in guesses)
            {
                bool exact = SameTitle(g.Core, c.Title);
                if (!exact && !SameTitle(g.Loose, c.Title)) continue;
                if (SameArtist(g.Artists, c.Artist)) { matched = artistOk = true; break; }
                if (!g.Named && exact && diff <= StrictSlack && sameVersion) matched = true;
            }
            if (!matched) continue;
            // A remix matched to the original song keeps only the words, even when the lengths happen to match.
            bool timed = diff <= SyncSlack && sameVersion;
            LyricsKind kind;
            int score;
            if (c.Instrumental)
            {
                if (!timed) continue;
                kind = LyricsKind.Instrumental;
                score = artistOk ? 3 : 2;
            }
            else if (timed && !string.IsNullOrWhiteSpace(c.Synced) && Lrc.Parse(c.Synced).Count(l => l.Text.Length > 0) >= 2)
            {
                kind = LyricsKind.Synced;
                score = artistOk ? 5 : 4;
            }
            else if (artistOk && (!string.IsNullOrWhiteSpace(c.Plain) || !string.IsNullOrWhiteSpace(c.Synced)))
            {
                kind = LyricsKind.Plain;
                score = 1;
            }
            else continue;
            if (score < bestScore || (score == bestScore && diff >= bestDiff)) continue;
            bestScore = score;
            bestDiff = diff;
            var plain = !string.IsNullOrWhiteSpace(c.Plain) ? c.Plain!.Replace("\r", "").Trim() : c.Synced != null ? Lrc.ToPlain(c.Synced) : null;
            best = new LyricsResult
            {
                Kind = kind,
                Synced = kind == LyricsKind.Synced ? c.Synced!.Replace("\r", "").Trim() : null,
                Plain = kind is LyricsKind.Synced or LyricsKind.Plain ? plain : null,
                Match = $"{c.Artist} – {c.Title} ({(c.Duration is double cd ? Text.Duration(cd) : "?")})",
            };
        }
        return (best, bestScore);
    }

    // One answer (get) or a list (search); not found = empty. Busy (503, 429): tried again a little later.
    // Other errors go up, so the song isn't marked as having no lyrics.
    private static async Task<List<Candidate>> GetAsync(string url, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var resp = await Http.Client.SendAsync(req, cts.Token);
            if (resp.StatusCode == HttpStatusCode.NotFound) return new List<Candidate>();
            if (resp.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout
                && attempt < 3)
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(new[] { 1.5, 4, 9 }[attempt]);
                await Task.Delay(wait > TimeSpan.FromSeconds(20) ? TimeSpan.FromSeconds(20) : wait, ct);
                continue;
            }
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            var root = doc.RootElement;
            var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : new List<JsonElement> { root };
            return items.Where(e => e.ValueKind == JsonValueKind.Object)
                .Select(e => new Candidate(e.Str("trackName") ?? e.Str("name") ?? "", e.Str("artistName") ?? "", e.Num("duration"), e.Bool("instrumental"),
                    e.Str("plainLyrics"), e.Str("syncedLyrics")))
                .Where(c => c.Title.Length > 0)
                .ToList();
        }
    }
}

