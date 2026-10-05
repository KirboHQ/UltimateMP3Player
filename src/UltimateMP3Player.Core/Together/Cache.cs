namespace UltimateMP3Player.Core.Together;

// Songs heard in rooms or suggested online that aren't in the library: kept in their own folder, at most N (the oldest
// go), so the same song next time is ready at once. Saving one to the library takes it out of here.
public sealed class TogetherCache
{
    private sealed class Entry
    {
        public List<string> Keys { get; set; } = new();
        public string File { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Artist { get; set; }
        public DateTime Used { get; set; }
    }

    private sealed class IndexFile
    {
        public List<Entry> Entries { get; set; } = new();
    }

    private readonly object _lock = new();
    private readonly List<Entry> _entries;
    private readonly string _index;

    public TogetherCache(string dir)
    {
        Dir = dir;
        Directory.CreateDirectory(dir);
        _index = Path.Combine(dir, "cache.json");
        _entries = JsonStore.Load<IndexFile>(_index).Entries.Where(e => File.Exists(Path.Combine(Dir, e.File))).ToList();
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

    private static List<string> KeysOf(RoomTrack t) => t.Keys.Count > 0 ? t.Keys.ToList() : new List<string> { "item:" + t.Id };

    public string? Find(RoomTrack t) => Find(KeysOf(t));

    public string? Find(List<string> keys)
    {
        lock (_lock)
        {
            var e = _entries.FirstOrDefault(x => x.Keys.Any(k => keys.Contains(k, StringComparer.OrdinalIgnoreCase)));
            if (e == null) return null;
            var path = Path.Combine(Dir, e.File);
            if (!File.Exists(path))
            {
                _entries.Remove(e);
                Save();
                return null;
            }
            e.Used = DateTime.Now;
            Save();
            return path;
        }
    }

    public string Adopt(RoomTrack t, string file) => Adopt(KeysOf(t), t.Title, t.Artist, file);

    // Moves a downloaded or received file in, under a readable name.
    public string Adopt(List<string> keys, string title, string? artist, string file)
    {
        var name = Text.SafeFileName(string.IsNullOrWhiteSpace(artist) ? title : $"{artist} - {title}", 90);
        var ext = Path.GetExtension(file);
        lock (_lock)
        {
            var existing = FindEntry(keys);
            if (existing != null)
            {
                var had = Path.Combine(Dir, existing.File);
                if (File.Exists(had) && !string.Equals(had, file, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); } catch { }
                    existing.Used = DateTime.Now;
                    Save();
                    return had;
                }
                _entries.Remove(existing);
            }
            var target = Text.UniquePath(Dir, name, ext.TrimStart('.'));
            File.Move(file, target);
            _entries.Add(new Entry { Keys = keys.ToList(), File = Path.GetFileName(target), Title = title, Artist = artist, Used = DateTime.Now });
            Save();
            return target;
        }
    }

    private Entry? FindEntry(List<string> keys) => _entries.FirstOrDefault(x => x.Keys.Any(k => keys.Contains(k, StringComparer.OrdinalIgnoreCase)));

    public bool Contains(string path)
    {
        lock (_lock) return _entries.Any(e => string.Equals(Path.Combine(Dir, e.File), path, StringComparison.OrdinalIgnoreCase));
    }

    // Files a room or the suggested songs are using right now: never deleted.
    private readonly Dictionary<object, Func<IEnumerable<string>>> _users = new();

    public void Use(object user, Func<IEnumerable<string>> files)
    {
        lock (_lock) _users[user] = files;
    }

    public void Release(object user)
    {
        lock (_lock) _users.Remove(user);
    }

    private HashSet<string> Kept(ISet<string> keep)
    {
        var all = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
        foreach (var files in _users.Values)
        {
            try { all.UnionWith(files()); } catch { }
        }
        return all;
    }

    // Keeps the newest max songs (never the ones in keep, e.g. the room's current and next ones).
    public void Trim(int max, ISet<string> keep)
    {
        lock (_lock)
        {
            keep = Kept(keep);
            int extra = _entries.Count - Math.Max(1, max);
            foreach (var e in _entries.OrderBy(e => e.Used).ToList())
            {
                if (extra <= 0) break;
                var path = Path.Combine(Dir, e.File);
                if (keep.Contains(path)) continue;
                if (TryDelete(path))
                {
                    _entries.Remove(e);
                    extra--;
                }
            }
            Save();
        }
    }

    // A song saved to the library leaves the cache (the file may still be playing: deleted later then).
    public void Forget(string path)
    {
        lock (_lock)
        {
            _entries.RemoveAll(e => string.Equals(Path.Combine(Dir, e.File), path, StringComparison.OrdinalIgnoreCase));
            TryDelete(path);
            Save();
        }
    }

    public void Clear(ISet<string> keep)
    {
        lock (_lock)
        {
            keep = Kept(keep);
            foreach (var e in _entries.ToList())
            {
                var path = Path.Combine(Dir, e.File);
                if (!keep.Contains(path) && TryDelete(path)) _entries.Remove(e);
            }
            Save();
        }
        CleanUp();
    }

    public (int Count, long Bytes) Stats()
    {
        lock (_lock)
        {
            long bytes = 0;
            foreach (var e in _entries)
            {
                try { bytes += new FileInfo(Path.Combine(Dir, e.File)).Length; } catch { }
            }
            return (_entries.Count, bytes);
        }
    }

    // Leftovers: interrupted transfers, files of songs no longer listed.
    private void CleanUp()
    {
        lock (_lock)
        {
            try
            {
                var known = _entries.Select(e => e.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.EnumerateFiles(Dir))
                {
                    var n = Path.GetFileName(f);
                    if (n.StartsWith("cache.json", StringComparison.OrdinalIgnoreCase) || known.Contains(n)) continue;
                    TryDelete(f);
                }
                var tmp = Path.Combine(Dir, "tmp");
                if (Directory.Exists(tmp))
                    foreach (var f in Directory.EnumerateFiles(tmp))
                        if (File.GetLastWriteTime(f) < DateTime.Now.AddHours(-2)) TryDelete(f);
            }
            catch { }
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch { return false; }
    }

    private void Save()
    {
        try { JsonStore.Save(_index, new IndexFile { Entries = _entries }); } catch { }
    }

    // A song from its link, audio only with tags and cover, into dir; progress 0-100.
    public static async Task<string> DownloadAsync(string url, string audioFormat, string? cookies, string dir, Action<double> progress, CancellationToken ct)
    {
        var analysis = await Analyzer.AnalyzeAsync(new AnalyzeRequest(url, cookies), ct);
        var item = analysis.Items.FirstOrDefault() ?? throw new EngineException(L.T("Nessun contenuto scaricabile trovato in questo link."));
        if (item.Kind == MediaKind.Video) item.Kind = MediaKind.Audio;
        var job = new DownloadJob(item, new DownloadOptions
        {
            AudioOnly = true,
            Audio = new AudioOptions(audioFormat == "original" ? "original" : "mp3"),
            EmbedMetadata = true,
            CookiesBrowser = cookies,
        }, dir);
        job.Progress += p =>
        {
            if (p.Percent is double pct) progress(p.Phase == JobPhase.Converting ? 85 + pct * 0.15 : Math.Min(85, pct * 0.85));
        };
        await job.RunAsync(ct);
        return job.ResultPath ?? throw new EngineException(L.T("La conversione non ha prodotto alcun file."));
    }
}
