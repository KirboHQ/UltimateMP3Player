using System.Globalization;
using System.Text.Json;

namespace UltimateMP3Player.Core;

public sealed record DlProgress(string FormatId, string Status, long Done, long? Total, double? Speed, double? Eta);

public static class YtDlp
{
    private const string Marker = "[UD]";

    // Set per download (flows with async calls): pause between requests after a rate limit.
    private static readonly AsyncLocal<bool> GentleFlow = new();
    public static bool Gentle { get => GentleFlow.Value; set => GentleFlow.Value = value; }

    private static List<string> BaseArgs(string? cookiesBrowser, string? cookieCopy)
    {
        var a = new List<string>
        {
            "--ignore-config", "--no-colors",
            "--js-runtimes", Engines.JsRuntime,
            "--ffmpeg-location", Engines.Dir,
        };
        if (Gentle) a.AddRange(new[] { "--sleep-requests", "1.5" });
        if (cookieCopy != null) { a.Add("--cookies"); a.Add(cookieCopy); }
        else if (!string.IsNullOrEmpty(cookiesBrowser) && !CookieFile.IsFile(cookiesBrowser)) { a.Add("--cookies-from-browser"); a.Add(cookiesBrowser); }
        return a;
    }

    // "yt-dlp -J": a video or a playlist.
    public static async Task<JsonDocument> DumpAsync(string url, string? cookies, CancellationToken ct,
        bool flat = true, bool noPlaylist = true, IEnumerable<string>? extra = null)
    {
        var copy = CookieFile.Copy(cookies);
        var args = BaseArgs(cookies, copy);
        args.Add("-J");
        if (flat) args.Add("--flat-playlist");
        args.Add(noPlaylist ? "--no-playlist" : "--yes-playlist");
        if (extra != null) args.AddRange(extra);
        args.Add("--");
        args.Add(url);

        ProcResult r;
        try { r = await ProcRunner.RunAsync(Engines.YtDlp, args, ct: ct, timeout: TimeSpan.FromMinutes(5)); }
        finally { CookieFile.Delete(copy); }
        var json = r.StdOut.Trim();
        int start = json.IndexOf('{');
        if (start >= 0)
        {
            try { return JsonDocument.Parse(json[start..]); }
            catch (JsonException) { }
        }
        throw ErrorText.FromEngine("yt-dlp", r.StdErr, r.ExitCode);
    }

    public static async Task<YtInfo> GetInfoAsync(string url, string? cookies, CancellationToken ct)
    {
        using var doc = await DumpAsync(url, cookies, ct, flat: false, noPlaylist: true);
        var root = doc.RootElement;
        if (root.Str("_type") is "playlist" or "multi_video")
        {
            var first = root.Arr("entries").FirstOrDefault(e => e.ValueKind == JsonValueKind.Object);
            if (first.ValueKind != JsonValueKind.Object) throw new EngineException(L.T("Nessun contenuto trovato.")) { NoMedia = true };
            return YtInfo.Parse(first);
        }
        return YtInfo.Parse(root);
    }

    // Downloads a format spec into dir, returns the file.
    public static async Task<string> DownloadAsync(string url, string spec, bool merge, string dir, string? cookies,
        Action<DlProgress> progress, CancellationToken ct, IEnumerable<string>? extra = null)
    {
        var copy = CookieFile.Copy(cookies);
        var args = BaseArgs(cookies, copy);
        args.AddRange(new[]
        {
            "-f", spec,
            "-o", Path.Combine(dir, "media.%(ext)s"),
            "--no-playlist", "--no-mtime", "--newline", "--no-continue",
            "-N", "4",
            "--progress-template",
            "download:" + Marker + "%(info.format_id)s|%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s",
        });
        if (merge) { args.Add("--merge-output-format"); args.Add("mkv"); }
        if (extra != null) args.AddRange(extra);
        args.Add("--");
        args.Add(url);

        ProcResult r;
        try
        {
            r = await ProcRunner.RunAsync(Engines.YtDlp, args, onOut: line =>
            {
                if (!line.StartsWith(Marker, StringComparison.Ordinal)) return;
                var p = line[Marker.Length..].Split('|');
                if (p.Length < 7) return;
                progress(new DlProgress(p[0], p[1], ParseL(p[2]) ?? 0, ParseL(p[3]) ?? ParseL(p[4]), ParseD(p[5]), ParseD(p[6])));
            }, ct: ct, captureOut: false);
        }
        finally { CookieFile.Delete(copy); }

        if (r.ExitCode != 0) throw ErrorText.FromEngine("yt-dlp", r.StdErr, r.ExitCode);
        return FindOutput(dir, "media.") ?? throw new EngineException(L.T("yt-dlp non ha prodotto alcun file."), r.StdErr);
    }

    internal static string? FindOutput(string dir, string prefix)
    {
        string[] skip = { ".part", ".ytdl", ".temp", ".json", ".txt" };
        return Directory.EnumerateFiles(dir)
            .Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(f => !skip.Any(s => f.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            .Where(f => !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), @"\.f[\w-]+\.\w+$"))
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();
    }

    private static long? ParseL(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (long)d : null;
    private static double? ParseD(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    // YouTube Music "Songs" search: (id, title).
    public static async Task<List<(string Id, string Title)>> SearchMusicAsync(string query, int count, string? cookies, CancellationToken ct)
    {
        var url = "https://music.youtube.com/search?q=" + Uri.EscapeDataString(query) + "#songs";
        using var doc = await DumpAsync(url, cookies, ct, flat: true, noPlaylist: false, extra: new[] { "--playlist-items", $"1:{count}" });
        return doc.RootElement.Arr("entries")
            .Where(e => e.Str("id") != null)
            .Select(e => (e.Str("id")!, e.Str("title") ?? ""))
            .ToList();
    }

    // YouTube search: (id, title, duration, channel).
    public static async Task<List<(string Id, string Title, double? Duration, string? Channel)>> SearchYouTubeAsync(
        string query, int count, string? cookies, CancellationToken ct)
    {
        using var doc = await DumpAsync($"ytsearch{count}:{query}", cookies, ct, flat: true, noPlaylist: false);
        return doc.RootElement.Arr("entries")
            .Where(e => e.Str("id") != null)
            .Select(e => (e.Str("id")!, e.Str("title") ?? "", e.Num("duration"), e.Str("channel") ?? e.Str("uploader")))
            .ToList();
    }
}

// The cookies as "file:<cookies.txt>" (the phone's logins made in the app) rather than a browser to read them from. The
// engines write the cookies back into the file when they end: each run gets a copy of its own (several at once would write
// over each other, and a half-written file would be read by the next), thrown away afterwards.
public static class CookieFile
{
    private const string Prefix = "file:";
    private static bool _cleaned;

    public static bool IsFile(string? cookies) => cookies != null && cookies.StartsWith(Prefix, StringComparison.Ordinal);

    public static string? Copy(string? cookies)
    {
        if (!IsFile(cookies)) return null;
        var src = cookies![Prefix.Length..];
        try
        {
            if (!File.Exists(src)) return null;
            var tmp = Path.GetTempPath();
            if (!_cleaned)
            {
                // Copies left behind by an app closed in the middle of a download.
                _cleaned = true;
                foreach (var old in Directory.EnumerateFiles(tmp, "ump-cookies-*.txt"))
                    try { if (DateTime.UtcNow - File.GetLastWriteTimeUtc(old) > TimeSpan.FromHours(6)) File.Delete(old); } catch { }
            }
            var copy = Path.Combine(tmp, $"ump-cookies-{Guid.NewGuid():N}.txt");
            File.Copy(src, copy);
            return copy;
        }
        catch { return null; }
    }

    public static void Delete(string? copy)
    {
        if (copy == null) return;
        try { File.Delete(copy); } catch { }
    }
}

public sealed class GalleryEntry
{
    public string Url { get; init; } = "";
    public JsonElement Meta { get; init; }
}

public static class GalleryDl
{
    private static List<string> BaseArgs(string? cookiesBrowser, string? cookieCopy)
    {
        var a = new List<string> { "--config-ignore", "--no-colors" };
        if (cookieCopy != null) { a.Add("--cookies"); a.Add(cookieCopy); }
        else if (!string.IsNullOrEmpty(cookiesBrowser) && !CookieFile.IsFile(cookiesBrowser)) { a.Add("--cookies-from-browser"); a.Add(cookiesBrowser); }
        return a;
    }

    // "gallery-dl -J": directory metadata and files.
    public static async Task<(JsonElement? Dir, List<GalleryEntry> Files, JsonDocument Doc)> ListAsync(
        string url, string? cookies, CancellationToken ct, int max = 1000)
    {
        var copy = CookieFile.Copy(cookies);
        var args = BaseArgs(cookies, copy);
        args.AddRange(new[] { "-J", "--range", $"1-{max}", "--", url });
        ProcResult r;
        try { r = await ProcRunner.RunAsync(Engines.GalleryDl, args, ct: ct, timeout: TimeSpan.FromMinutes(5)); }
        finally { CookieFile.Delete(copy); }
        var json = r.StdOut.Trim();
        int start = json.IndexOf('[');
        JsonDocument? doc = null;
        if (start >= 0)
        {
            try { doc = JsonDocument.Parse(json[start..]); }
            catch (JsonException) { }
        }
        if (doc == null) throw ErrorText.FromEngine("gallery-dl", r.StdErr, r.ExitCode);

        JsonElement? dir = null;
        var files = new List<GalleryEntry>();
        string? error = null;
        foreach (var msg in doc.RootElement.EnumerateArray())
        {
            if (msg.ValueKind != JsonValueKind.Array || msg.GetArrayLength() < 2) continue;
            if (!msg[0].TryGetInt32(out int type)) continue;
            if (type == 2 && dir == null) dir = msg[1];
            else if (type == 3 && msg.GetArrayLength() >= 3 && msg[1].ValueKind == JsonValueKind.String)
                files.Add(new GalleryEntry { Url = msg[1].GetString()!, Meta = msg[2] });
            else if (type == -1 && msg[1].ValueKind == JsonValueKind.Object)
                error = Text.FirstNonEmpty(msg[1].Str("message"), msg[1].Str("error"));
        }
        if (files.Count == 0)
        {
            doc.Dispose();
            var stderr = r.StdErr + (error != null ? "\n[gallery-dl][error] " + error : "");
            var ex = ErrorText.FromEngine("gallery-dl", stderr, r.ExitCode);
            if (!ex.NeedsLogin && !ex.Unsupported && error == null && r.ExitCode == 0)
                throw new EngineException(L.T("Nessun file trovato in questo link."), stderr) { NoMedia = true };
            throw ex;
        }
        return (dir, files, doc);
    }

    // Downloads one file of a gallery into dir.
    public static async Task<string> DownloadOneAsync(string url, int index, string dir, string? cookies, CancellationToken ct)
    {
        var copy = CookieFile.Copy(cookies);
        var args = BaseArgs(cookies, copy);
        args.AddRange(new[] { "--range", index.ToString(CultureInfo.InvariantCulture), "-D", dir, "-f", "gdl_{num}.{extension}", "--no-mtime", "--", url });
        ProcResult r;
        try { r = await ProcRunner.RunAsync(Engines.GalleryDl, args, ct: ct); }
        finally { CookieFile.Delete(copy); }
        var file = Directory.EnumerateFiles(dir, "gdl_*").Where(f => !f.EndsWith(".part")).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
        if (file == null) throw ErrorText.FromEngine("gallery-dl", r.StdErr, r.ExitCode);
        return file;
    }
}
