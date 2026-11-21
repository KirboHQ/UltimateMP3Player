namespace UltimateMP3Player.Core;

// Music videos of the songs (5.5): the link found for a song (or that it has none), and the video file of a song not saved on
// the device (in the cloud, suggested, of a room): the last Max files stay, the oldest go, like the songs heard (SongCache). A
// saved song keeps its video beside its audio instead (Track.VideoPath). Found again by the song's keys.
public sealed class VideoCache
{
    public const int Max = 10;
    // How long "no video found" holds before the song is looked for again; how many links are remembered.
    private static readonly TimeSpan NoneFor = TimeSpan.FromDays(30);
    private const int MaxLinks = 5000;

    private sealed class Entry
    {
        public List<string> Keys { get; set; } = new();
        // The video's page, and whether it was chosen by hand (then it comes back whenever the song plays, like the song's
        // audio from its link, even with the automatic videos off).
        public string? Url { get; set; }
        public bool Chosen { get; set; }
        // Looked for, nothing good enough: when.
        public DateTime? None { get; set; }
        // Its file here; "": none (never brought, or gone to make room).
        public string File { get; set; } = "";
        public DateTime Used { get; set; }
    }

    private sealed class IndexFile
    {
        public List<Entry> Entries { get; set; } = new();
    }

    private readonly object _lock = new();
    private readonly List<Entry> _entries;
    private readonly string _index;
    private readonly DebouncedSaver _saver;

    public VideoCache(string dir)
    {
        Dir = dir;
        Directory.CreateDirectory(dir);
        _index = Path.Combine(dir, "videos.json");
        _entries = JsonStore.Load<IndexFile>(_index).Entries;
        foreach (var e in _entries)
            if (e.File.Length > 0 && !System.IO.File.Exists(Path.Combine(Dir, e.File))) e.File = "";
        _saver = new DebouncedSaver(SaveNow, 1500);
        CleanUp();
    }

    public string Dir { get; }

    public string TempDir
    {
        get
        {
            var d = Path.Combine(Dir, "tmp");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    // The app closes or goes to the background.
    public void Flush() => _saver.Flush();

    // A song's keys as this cache knows it (its name, when it has none).
    public static List<string> KeysOf(Track t) => t.Keys.Count > 0 ? t.Keys.ToList() : new List<string> { "name:" + SongMeta.Key(t.Title, t.Artist) };

    private Entry? Find(IReadOnlyCollection<string> keys) =>
        _entries.FirstOrDefault(e => e.Keys.Any(k => keys.Contains(k, StringComparer.OrdinalIgnoreCase)));

    private Entry Get(IReadOnlyCollection<string> keys)
    {
        var e = Find(keys);
        if (e == null) _entries.Add(e = new Entry { Keys = keys.ToList() });
        else
            foreach (var k in keys)
                if (!e.Keys.Contains(k, StringComparer.OrdinalIgnoreCase)) e.Keys.Add(k);
        e.Used = DateTime.Now;
        return e;
    }

    // Its video file here, if there is one.
    public string? FileFor(IReadOnlyCollection<string> keys)
    {
        lock (_lock)
        {
            if (Find(keys) is not { File.Length: > 0 } e) return null;
            var path = Path.Combine(Dir, e.File);
            if (!System.IO.File.Exists(path)) e.File = "";
            else e.Used = DateTime.Now;
            _saver.Request();
            return e.File.Length > 0 ? path : null;
        }
    }

    // What's known of its video: the link found (and whether it was chosen by hand), or that it has none (looked for lately).
    public (string? Url, bool Chosen, bool None) Known(IReadOnlyCollection<string> keys)
    {
        lock (_lock)
        {
            if (Find(keys) is not { } e) return (null, false, false);
            return (e.Url, e.Chosen, e.Url == null && e.None is DateTime at && DateTime.Now - at < NoneFor);
        }
    }

    public void SetUrl(IReadOnlyCollection<string> keys, string url, bool chosen)
    {
        lock (_lock)
        {
            var e = Get(keys);
            e.Url = url;
            e.Chosen |= chosen;
            e.None = null;
        }
        _saver.Request();
    }

    public void SetNone(IReadOnlyCollection<string> keys)
    {
        lock (_lock) Get(keys).None = DateTime.Now;
        _saver.Request();
    }

    // A video brought for a song not saved: moved in (in place of the one it had); the oldest beyond Max go, never the ones in
    // keep (the song playing, the next one) nor this one.
    public string Adopt(IReadOnlyCollection<string> keys, string title, string? artist, string file, ISet<string>? keep = null)
    {
        lock (_lock)
        {
            var e = Get(keys);
            var name = Text.SafeFileName(string.IsNullOrWhiteSpace(artist) ? title : $"{artist} - {title}", 90);
            var target = Text.UniquePath(Dir, name, Path.GetExtension(file).TrimStart('.'));
            System.IO.File.Move(file, target);
            if (e.File.Length > 0) TryDelete(Path.Combine(Dir, e.File));
            e.File = Path.GetFileName(target);
            Trim(keep ?? new HashSet<string>(), target);
            _saver.Request();
            return target;
        }
    }

    // The song saved on the device: its video leaves for target, beside its audio. Null when it has none here (or it can't move
    // now: then it stays here).
    public string? MoveOut(IReadOnlyCollection<string> keys, string target)
    {
        lock (_lock)
        {
            if (Find(keys) is not { File.Length: > 0 } e) return null;
            try
            {
                System.IO.File.Move(Path.Combine(Dir, e.File), target);
            }
            catch { return null; }
            e.File = "";
            _saver.Request();
            return target;
        }
    }

    // The song gone (deleted by the last profile that had it): its video and what was found for it.
    public void Forget(IReadOnlyCollection<string> keys)
    {
        lock (_lock)
        {
            if (Find(keys) is not { } e) return;
            if (e.File.Length > 0) TryDelete(Path.Combine(Dir, e.File));
            _entries.Remove(e);
        }
        _saver.Request();
    }

    // (under the lock)
    private void Trim(ISet<string> keep, string newest)
    {
        var files = _entries.Where(e => e.File.Length > 0).OrderBy(e => e.Used).ToList();
        int extra = files.Count - Max;
        foreach (var e in files)
        {
            if (extra <= 0) break;
            var path = Path.Combine(Dir, e.File);
            if (keep.Contains(path) || string.Equals(path, newest, StringComparison.OrdinalIgnoreCase)) continue;
            if (TryDelete(path))
            {
                e.File = "";
                extra--;
            }
        }
        // (the links stay, they're small: so many of them)
        if (_entries.Count > MaxLinks)
            foreach (var e in _entries.Where(e => e.File.Length == 0).OrderBy(e => e.Used).Take(_entries.Count - MaxLinks).ToList())
                _entries.Remove(e);
    }

    // Settings › Temporary storage: the files go (not the ones in keep), the links found stay.
    public void Clear(ISet<string> keep)
    {
        lock (_lock)
            foreach (var e in _entries.Where(e => e.File.Length > 0))
            {
                var path = Path.Combine(Dir, e.File);
                if (!keep.Contains(path) && TryDelete(path)) e.File = "";
            }
        _saver.Request();
        CleanUp();
    }

    public (int Count, long Bytes) Stats()
    {
        lock (_lock)
        {
            long bytes = 0;
            int count = 0;
            foreach (var e in _entries.Where(e => e.File.Length > 0))
            {
                try
                {
                    bytes += new FileInfo(Path.Combine(Dir, e.File)).Length;
                    count++;
                }
                catch { }
            }
            return (count, bytes);
        }
    }

    // Leftovers: downloads stopped halfway, files no longer listed.
    private void CleanUp()
    {
        lock (_lock)
        {
            try { Directory.Delete(Path.Combine(Dir, "tmp"), true); } catch { }
            var listed = _entries.Where(e => e.File.Length > 0).Select(e => e.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var f in Directory.GetFiles(Dir))
                {
                    var name = Path.GetFileName(f);
                    if (name.StartsWith("videos.json", StringComparison.OrdinalIgnoreCase) || listed.Contains(name)) continue;
                    TryDelete(f);
                }
            }
            catch { }
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            return true;
        }
        catch { return false; }
    }

    private void SaveNow()
    {
        lock (_lock)
        {
            try { JsonStore.Save(_index, new IndexFile { Entries = _entries.ToList() }); } catch { }
        }
    }
}

// A song's music video to show with it: only the picture (the song's own audio plays apart), H.264 at most res (what Windows,
// the iPhone and ffmpeg all decode), fetched in one piece when the site allows it. Nothing converted: it starts sooner, and the
// iPhone has no ffmpeg (where there is one, the file is only rewritten whole, its index first, the video copied as it is).
public static class MutedVideo
{
    public static async Task<string> DownloadAsync(string url, int? res, string? cookies, string dir, CancellationToken ct)
    {
        var info = await YtDlp.GetInfoAsync(url, cookies, ct);
        var f = FormatSelector.ForMutedVideo(info, res) ?? throw new EngineException(L.T("No downloadable format found."));
        var temp = Path.Combine(dir, Ids.New());
        Directory.CreateDirectory(temp);
        try
        {
            string file;
            if (f.Url != null && !f.NeedsCookies && f.Protocol is "https" or "http")
            {
                file = Path.Combine(temp, "video." + (string.IsNullOrEmpty(f.Ext) ? "mp4" : f.Ext));
                await DownloadJob.AnswersAsync(f, ct);
                await Http.DownloadFileAsync(f.Url, file, f.Headers, null, ct, chunk: f.ChunkSize ?? 10 << 20);
            }
            else file = await YtDlp.DownloadAsync(info.WebpageUrl, f.Id, false, temp, cookies, _ => { }, ct);
            if (Engines.VideoDownloads)
            {
                var whole = Path.Combine(temp, "whole.mp4");
                try
                {
                    await Ffmpeg.RunAsync(new[] { "-i", file, "-map", "0:v:0", "-c", "copy", "-movflags", "+faststart", whole }, info.Duration, null, ct);
                    if (File.Exists(whole) && new FileInfo(whole).Length > 0) file = whole;
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            var result = Path.Combine(dir, Ids.New() + Path.GetExtension(file));
            File.Move(file, result);
            return result;
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }
}

// Whether the connection now costs (mobile data, a metered hotspot): set by the phones, null elsewhere.
public static class NetworkCost
{
    public static Func<bool>? Metered { get; set; }

    public static bool IsMetered
    {
        get
        {
            try { return Metered?.Invoke() == true; }
            catch { return false; }
        }
    }
}
