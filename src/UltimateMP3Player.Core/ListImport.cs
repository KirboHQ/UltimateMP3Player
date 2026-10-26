using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace UltimateMP3Player.Core;

// A list of songs instead of one link: links pasted together (Spotify's app copies the selected songs as their links), a
// playlist exported from Apple Music or iTunes (text, XML, M3U), a CSV (Exportify, TuneMyMusic) or "Artist - Title" lines.
// It becomes one card, like a playlist's link, without the limit of the shared playlists' pages (Spotify 100, Apple 300).
public static class ListImport
{
    public sealed record Entry(string? Url, string? Title, string? Artist = null, string? Album = null, double? Duration = null, string? SpotifyId = null);

    private static readonly Regex LinkRx = new(@"(?:https?://|spotify:)\S+", RegexOptions.IgnoreCase);
    private static readonly Regex DashRx = new(@"\s[-–—]\s");
    private static readonly Regex SpotifyIdRx = new(@"(?:spotify:track:|open\.spotify\.com/(?:intl-[a-z-]+/)?track/)([A-Za-z0-9]{22})|^([A-Za-z0-9]{22})$", RegexOptions.IgnoreCase);

    // The column names of the exports (Apple Music and iTunes in their languages, Exportify, TuneMyMusic).
    private static readonly string[] TitleCols = { "name", "nome", "nom", "titel", "nombre", "title", "titolo", "titre", "track name", "track", "song", "song name", "brano" };
    private static readonly string[] ArtistCols = { "artist", "artista", "artiste", "interpret", "künstler", "interprete", "artist name(s)", "artist name", "artists", "artist(s)" };
    private static readonly string[] AlbumCols = { "album", "album name" };
    private static readonly string[] TimeCols = { "time", "durata", "durée", "dauer", "duración", "duur", "tempo", "duration (ms)", "duration", "length", "duration_ms" };
    private static readonly string[] LinkCols = { "track uri", "spotify - id", "spotify id", "spotify uri", "uri", "url", "link" };

    // Pasted text that is a list (several links, an export, lines with "Artist - Title"), not one link or words to search.
    public static bool IsList(string? text)
    {
        if (text == null || !text.Contains('\n')) return false;
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count < 2) return false;
        if (lines[0].StartsWith('<') || lines.Any(l => l.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))) return true;
        if (TitleColumn(lines[0]) >= 0) return true;
        return lines.Count(l => LinkRx.IsMatch(l)) >= 2 || lines.Count(l => DashRx.IsMatch(l)) >= lines.Count * 0.8;
    }

    public static List<Entry> Parse(string text)
    {
        text = text.TrimStart('﻿');
        if (text.TrimStart().StartsWith('<')) return FromXml(text);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Any(l => l.TrimStart().StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))) return FromM3u(lines);
        if (lines.FirstOrDefault(l => l.Trim().Length > 0) is { } header && TitleColumn(header) >= 0) return FromTable(lines, header);
        return lines.Select(FromLine).OfType<Entry>().ToList();
    }

    // A link, or "Artist - Title" (a number in front and a length at the end are left out: "3. Artist - Title (3:45)").
    private static Entry? FromLine(string line)
    {
        line = line.Trim();
        if (line.Length == 0 || line.StartsWith('#')) return null;
        if (LinkRx.Match(line) is { Success: true } link) return new Entry(link.Value.TrimEnd(',', ';', ')', ']', '"', '\''), null);
        line = Regex.Replace(line, @"^\d{1,4}[.)]\s+", "");
        double? duration = null;
        if (Regex.Match(line, @"\s*[(\[]?(\d{1,2}):(\d{2})[)\]]?$") is { Success: true, Index: > 0 } d)
        {
            duration = int.Parse(d.Groups[1].Value) * 60 + int.Parse(d.Groups[2].Value);
            line = line[..d.Index].Trim();
        }
        var parts = DashRx.Split(line, 2);
        return parts.Length == 2 && parts[0].Trim().Length > 0 && parts[1].Trim().Length > 0
            ? new Entry(null, parts[1].Trim(), parts[0].Trim(), Duration: duration)
            : new Entry(null, line, Duration: duration);
    }

    private static char Separator(string header) => header.Contains('\t') ? '\t' : header.Count(c => c == ';') > header.Count(c => c == ',') ? ';' : ',';

    private static List<string> Head(string header) => Cells(header, Separator(header)).Select(h => h.Trim().ToLowerInvariant()).ToList();

    private static int Column(List<string> head, string[] names) => names.Select(n => head.IndexOf(n)).FirstOrDefault(i => i >= 0, -1);

    // Only a header with a title column and a separator makes a table (one word alone is a song).
    private static int TitleColumn(string header) => header.IndexOfAny(new[] { '\t', ',', ';' }) < 0 ? -1 : Column(Head(header), TitleCols);

    private static List<Entry> FromTable(List<string> lines, string header)
    {
        char sep = Separator(header);
        var head = Head(header);
        int title = Column(head, TitleCols), artist = Column(head, ArtistCols), album = Column(head, AlbumCols), time = Column(head, TimeCols), link = Column(head, LinkCols);
        bool ms = time >= 0 && head[time].Contains("ms");
        var list = new List<Entry>();
        foreach (var line in lines.SkipWhile(l => l != header).Skip(1))
        {
            var c = Cells(line, sep);
            string? At(int i) => i >= 0 && i < c.Count && c[i].Trim().Length > 0 ? c[i].Trim() : null;
            if (At(title) is not { } name) continue;
            var url = At(link);
            var id = url != null && SpotifyIdRx.Match(url) is { Success: true } m ? (m.Groups[1].Success ? m.Groups[1] : m.Groups[2]).Value : null;
            list.Add(new Entry(url != null && LinkRx.IsMatch(url) && id == null ? url : null, name, At(artist), At(album), Seconds(At(time), ms), id));
        }
        return list;
    }

    // CSV cells, quotes and all (a tab-separated export has no quoting: a title may start with one).
    private static List<string> Cells(string line, char sep)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (quoted)
            {
                if (ch != '"') cell.Append(ch);
                else if (i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else quoted = false;
            }
            else if (ch == '"' && sep != '\t' && cell.Length == 0) quoted = true;
            else if (ch == sep) { cells.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(ch);
        }
        cells.Add(cell.ToString());
        return cells;
    }

    // "3:45", "1:02:03", seconds (Apple's "Time"), or milliseconds (Exportify's "Duration (ms)").
    private static double? Seconds(string? s, bool ms)
    {
        if (s == null) return null;
        if (Regex.Match(s, @"^(?:(\d+):)?(\d{1,2}):(\d{2})$") is { Success: true } m)
            return (m.Groups[1].Success ? int.Parse(m.Groups[1].Value) * 3600 : 0) + int.Parse(m.Groups[2].Value) * 60 + int.Parse(m.Groups[3].Value);
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n <= 0) return null;
        return ms || n > 10000 ? n / 1000 : n;
    }

    // #EXTINF:215,Artist - Title, then where the song is: a link is read as a link, a file by the #EXTINF line or its name.
    private static List<Entry> FromM3u(List<string> lines)
    {
        var list = new List<Entry>();
        Entry? info = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                var text = line[(line.IndexOf(':') + 1)..];
                int comma = text.IndexOf(',');
                double? d = comma > 0 && double.TryParse(text[..comma], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : null;
                info = FromLine(comma >= 0 ? text[(comma + 1)..] : text) is { } e ? e with { Duration = d ?? e.Duration } : null;
            }
            else if (line.Length > 0 && !line.StartsWith('#'))
            {
                var entry = LinkRx.IsMatch(line) ? new Entry(line, null) : info ?? FromLine(Path.GetFileNameWithoutExtension(line.Split('/', '\\').Last()));
                if (entry != null) list.Add(entry);
                info = null;
            }
        }
        return list;
    }

    // Apple Music's and iTunes' XML (a property list): the songs in the order of the playlist exported.
    private static List<Entry> FromXml(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        var top = Dict(XDocument.Load(reader).Root?.Element("dict"));
        var tracks = top.TryGetValue("Tracks", out var t) ? Dict(t) : new();
        var playlist = top.TryGetValue("Playlists", out var p) ? p.Elements("dict").LastOrDefault() : null;
        var order = playlist != null && Dict(playlist).TryGetValue("Playlist Items", out var items)
            ? items.Elements("dict").Select(i => Dict(i).GetValueOrDefault("Track ID")?.Value).OfType<string>().ToList()
            : tracks.Keys.ToList();
        var list = new List<Entry>();
        foreach (var id in order)
        {
            if (!tracks.TryGetValue(id, out var song)) continue;
            var f = Dict(song);
            string? S(string key) => f.TryGetValue(key, out var v) && v.Value.Trim().Length > 0 ? v.Value.Trim() : null;
            if (S("Name") is not { } name) continue;
            list.Add(new Entry(null, name, S("Artist"), S("Album"), Seconds(S("Total Time"), true)));
        }
        return list;

        static Dictionary<string, XElement> Dict(XElement? d)
        {
            var r = new Dictionary<string, XElement>();
            var els = d?.Elements().ToList() ?? new();
            for (int i = 0; i + 1 < els.Count; i++)
                if (els[i].Name == "key") r[els[i].Value] = els[++i];
            return r;
        }
    }

    // ------------------------------------------------------------------ the songs

    // Deezer answers 50 searches every 5 seconds: one every 110 ms keeps a long list under it.
    private static readonly object PaceLock = new();
    private static DateTime _nextSearch;

    // The songs of the list as a playlist's card. Links are read like pasted ones (a playlist's link gives all its songs);
    // a title and artist without one is found on Deezer, a catalogue like the links of Spotify (its link and cover, so it
    // can also go into the library without being saved), or else searched on YouTube Music when downloaded.
    // progress: lines read, of how many.
    public static async Task<AnalysisResult> AnalyzeAsync(IReadOnlyList<Entry> entries, string title, string? cookies, Action<int, int> progress,
        CancellationToken ct)
    {
        var found = new List<MediaItem>?[entries.Count];
        int done = 0, failed = 0;
        Exception? firstError = null;
        // (yt-dlp is a process each: fewer at once than the pages of the catalogues)
        using var engines = new SemaphoreSlim(3);
        using var pages = new SemaphoreSlim(8);
        await Task.WhenAll(entries.Select(async (e, i) =>
        {
            try
            {
                found[i] = await ResolveAsync(e, cookies, engines, pages, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                firstError ??= ex;
            }
            progress(Interlocked.Increment(ref done), entries.Count);
        }));
        ct.ThrowIfCancellationRequested();
        var result = new AnalysisResult { IsCollection = true, Title = title, Site = L.T("List"), PreferAudio = true };
        foreach (var f in found)
            if (f != null) result.Items.AddRange(f.Where(i => i.Kind is MediaKind.Audio or MediaKind.Video));
        if (result.Items.Count == 0)
            throw firstError as EngineException ?? new EngineException(L.T("No songs found in this list.") + (firstError != null ? " " + firstError.Message : ""));
        if (failed > 0) result.Notes.Add(L.Count(failed, "1 line of the list couldn't be read: it was left out.", "{0} lines of the list couldn't be read: they were left out."));
        result.Thumbnails = result.Items.SelectMany(i => i.Thumbnails).Take(1).ToList();
        return result;
    }

    private static async Task<List<MediaItem>> ResolveAsync(Entry e, string? cookies, SemaphoreSlim engines, SemaphoreSlim pages, CancellationToken ct)
    {
        if (e.SpotifyId is { } id && e.Title != null)
        {
            var link = "https://open.spotify.com/track/" + id;
            return new() { new MediaItem { Title = e.Title, Artist = e.Artist, Album = e.Album, Duration = e.Duration, Kind = MediaKind.Audio,
                Source = SourceKind.Search, Url = link, PageUrl = link, SpotifyTrackId = id, SiteName = "Spotify" } };
        }
        if (e.Url != null)
        {
            var url = Analyzer.NormalizeUrl(e.Url);
            var gate = Spotify.IsMatch(url) || Deezer.IsMatch(url) || AppleMusic.IsMatch(url) ? pages : engines;
            await gate.WaitAsync(ct);
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    try { return (await Analyzer.AnalyzeAsync(new AnalyzeRequest(url, cookies), ct)).Items; }
                    // Asked for hundreds at once a site may answer "too many" (Spotify does after a hundred or so) or not at all:
                    // tried again after 2, 4, 8, 16 s, keeping its place so the others slow down too; another failure once more.
                    catch (Exception ex) when (!ct.IsCancellationRequested && attempt < 4 &&
                                               (attempt == 0 || ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } ||
                                                (int?)(ex as HttpRequestException)?.StatusCode >= 500))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2 << attempt), ct);
                    }
                }
            }
            finally { gate.Release(); }
        }
        var item = new MediaItem { Title = e.Title ?? "", Artist = e.Artist, Album = e.Album, Duration = e.Duration, Kind = MediaKind.Audio,
            Source = SourceKind.Search, SiteName = OnlineSearchServices.YouTubeMusic };
        await pages.WaitAsync(ct);
        try
        {
            TimeSpan wait;
            lock (PaceLock)
            {
                var now = DateTime.UtcNow;
                if (_nextSearch < now) _nextSearch = now;
                wait = _nextSearch - now;
                _nextSearch += TimeSpan.FromMilliseconds(110);
            }
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            var clean = Regex.Replace(item.Title, @"\s*[\(\[].*?[\)\]]|\s+-\s+.*$", "").Trim();
            var who = item.Artist?.Split(',', '&', ';')[0].Trim();
            var hits = await OnlineSearchServices.SearchAsync(OnlineSearchServices.Deezer, $"{who} {(clean.Length > 0 ? clean : item.Title)}".Trim(), 5, ct);
            var hit = hits.FirstOrDefault(h => Text.Coverage(clean.Length > 0 ? clean : item.Title, h.Title) >= 0.7 &&
                                               (string.IsNullOrEmpty(who) || Text.Coverage(who, h.Artist + " " + h.Title) >= 0.5) &&
                                               (item.Duration is not double d || h.Duration is not double hd || Math.Abs(d - hd) <= 10));
            if (hit != null)
            {
                var match = OnlineSearchServices.ToItem(hit);
                match.Album ??= item.Album;
                return new() { match };
            }
        }
        // (not found or no answer: searched on YouTube Music when it's downloaded, as before)
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        finally { pages.Release(); }
        return new() { item };
    }
}
