namespace UltimateMP3Player.Core.Together;

public sealed record FetchOptions(string AudioFormat, string? Cookies, int CacheSize);

// Gets this computer the files of the room's song and the next two (so nobody has to wait for anyone):
// from the library if the song is already there, from the cache of past rooms, downloaded from its link,
// and as a last resort sent over by someone in the room who has it.
public sealed class TogetherFetcher : IDisposable
{
    public const int Ahead = 2;

    private readonly TogetherSession _s;
    private readonly Library _lib;
    private readonly TogetherCache _cache;
    private readonly Func<FetchOptions> _options;
    private readonly Dictionary<string, FileStatus> _status = new();
    private readonly Dictionary<string, string> _paths = new();
    private readonly Dictionary<string, Track> _libraryMatch = new();
    private readonly HashSet<string> _noLink = new();
    private readonly Dictionary<string, long> _retryAt = new();
    private readonly Dictionary<string, TaskCompletionSource<string?>> _transfers = new();
    private CancellationTokenSource? _workCts;
    private string? _working;
    private bool _disposed;
    private long _lastReport;
    private System.Threading.Timer? _retry;

    public TogetherFetcher(TogetherSession s, Library lib, TogetherCache cache, Func<FetchOptions> options)
    {
        _s = s;
        _lib = lib;
        _cache = cache;
        _options = options;
        s.QueueChanged += Refresh;
        s.FileReceived += OnFileReceived;
        s.FileProgress += OnFileProgress;
        s.FileUnavailable += OnFileUnavailable;
        s.LocalFile = PathFor;
        s.TempFile = (id, ext) => Path.Combine(_cache.TempDir, id + "-" + Ids.New() + ext);
        // Failed songs are tried again now and then (someone who has them may have arrived).
        _retry = new System.Threading.Timer(_ => s.Post(Refresh), null, 5000, 5000);
    }

    public event Action? Changed;

    public string? PathFor(string itemId) => _paths.TryGetValue(itemId, out var p) && File.Exists(p) ? p : null;
    public FileStatus? StatusOf(string itemId) => _status.GetValueOrDefault(itemId);
    // The library song that is the same as this room song, if any.
    public Track? LibraryTrack(string itemId) => _libraryMatch.GetValueOrDefault(itemId);
    // Files the room is using right now (the cache keeps them).
    public HashSet<string> InUse => _paths.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private List<RoomTrack> Window()
    {
        var list = new List<RoomTrack>();
        if (_s.Current != null) list.Add(_s.Current);
        list.AddRange(_s.Queue.Take(Ahead));
        return list;
    }

    // The queue changed (or a retry is due): what to get next.
    public void Refresh()
    {
        if (_disposed) return;
        var inRoom = new[] { _s.Current }.Concat(_s.Queue).OfType<RoomTrack>().Select(t => t.Id).ToHashSet();
        foreach (var id in _status.Keys.Where(k => !inRoom.Contains(k)).ToList())
        {
            _status.Remove(id);
            _paths.Remove(id);
            _libraryMatch.Remove(id);
            _libraryChecked.Remove(id);
            _retryAt.Remove(id);
            _noLink.Remove(id);
        }
        var window = Window();
        var ids = window.Select(t => t.Id).ToList();
        // The song playing comes first: work on a later one stops if that one isn't ready yet (and can be tried now).
        long now = Environment.TickCount64;
        bool currentDue = _s.Current is { } cur && !IsReady(cur.Id) && (!_retryAt.TryGetValue(cur.Id, out var due) || now >= due);
        if (_working != null && (!ids.Contains(_working) || (currentDue && _working != _s.Current!.Id)))
            _workCts?.Cancel();
        // Quick look for all of them: already in the library or the cache?
        foreach (var t in window)
            if (!IsReady(t.Id) && Local(t) is { } path) MarkReady(t, path);
        if (_working == null)
        {
            var next = window.FirstOrDefault(t => !IsReady(t.Id) && (!_retryAt.TryGetValue(t.Id, out var at) || now >= at));
            if (next != null) _ = Work(next);
        }
        Report(true);
    }

    private bool IsReady(string id) => _status.TryGetValue(id, out var s) && s.State == FileState.Ready && PathFor(id) != null;

    private readonly HashSet<string> _libraryChecked = new();

    private string? Local(RoomTrack t)
    {
        if (PathFor(t.Id) is { } known) return known;
        // The library once per song (matching by title and artist goes through every song).
        if (_libraryChecked.Add(t.Id))
        {
            var lt = _lib.FindByKeys(t.Keys);
            if (lt == null && t.Duration > 0) lt = _lib.FindSimilar(t.Title, t.Artist, t.Duration);
            if (lt != null && File.Exists(lt.Path))
            {
                _libraryMatch[t.Id] = lt;
                return lt.Path;
            }
        }
        else if (_libraryMatch.TryGetValue(t.Id, out var match) && File.Exists(match.Path)) return match.Path;
        return _cache.Find(t);
    }

    // A song saved to the library meanwhile: from now on it plays from there.
    public void UseLibrary(string itemId, Track t)
    {
        _libraryMatch[itemId] = t;
        _libraryChecked.Add(itemId);
        if (_s.Find(itemId) is { } item && File.Exists(t.Path)) MarkReady(item, t.Path);
    }

    private void MarkReady(RoomTrack t, string path)
    {
        _paths[t.Id] = path;
        _retryAt.Remove(t.Id);
        Set(t.Id, FileState.Ready, 100, t.Duration);
    }

    private void Set(string id, FileState state, double pct, double duration = 0)
    {
        _status[id] = new FileStatus { State = state, Pct = Math.Round(pct, 1), Duration = duration };
        Changed?.Invoke();
        Report(state is FileState.Ready or FileState.Failed or FileState.None);
    }

    private void Report(bool now)
    {
        long t = Environment.TickCount64;
        if (!now && t - _lastReport < 400) return;
        _lastReport = t;
        _s.ReportFiles(_status);
    }

    private async Task Work(RoomTrack t)
    {
        _working = t.Id;
        var cts = _workCts = new CancellationTokenSource();
        try
        {
            // From its link (unless that already failed here).
            if (t.SourceUrl != null && !_noLink.Contains(t.Id))
            {
                Set(t.Id, FileState.Downloading, 0);
                try
                {
                    var file = await Download(t, cts.Token);
                    if (!_disposed && !cts.IsCancellationRequested && _s.Find(t.Id) != null)
                    {
                        MarkReady(t, _cache.Adopt(t, file));
                        Trim();
                        return;
                    }
                    try { File.Delete(file); } catch { }
                    return;
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    Set(t.Id, FileState.None, 0);
                    return;
                }
                catch { _noLink.Add(t.Id); }
            }
            // From someone in the room.
            if (_disposed || cts.IsCancellationRequested) return;
            Set(t.Id, FileState.Transfer, 0);
            var tcs = new TaskCompletionSource<string?>();
            _transfers[t.Id] = tcs;
            _s.RequestFile(t.Id);
            string? got;
            try { got = await tcs.Task.WaitAsync(TimeSpan.FromMinutes(5), cts.Token); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                _transfers.Remove(t.Id);
                if (!IsReady(t.Id)) Set(t.Id, FileState.None, 0);
                return;
            }
            catch (TimeoutException) { got = null; }
            _transfers.Remove(t.Id);
            if (got == null && !IsReady(t.Id))
            {
                _retryAt[t.Id] = Environment.TickCount64 + 12000;
                Set(t.Id, FileState.Failed, 0);
            }
        }
        finally
        {
            if (_working == t.Id) _working = null;
            if (_workCts == cts) _workCts = null;
            cts.Dispose();
            if (!_disposed) _s.Post(Refresh);
        }
    }

    private async Task<string> Download(RoomTrack t, CancellationToken ct)
    {
        var o = _options();
        var analysis = await Analyzer.AnalyzeAsync(new AnalyzeRequest(t.SourceUrl!, o.Cookies), ct);
        var item = analysis.Items.FirstOrDefault() ?? throw new EngineException(L.T("Nessun contenuto scaricabile trovato in questo link."));
        if (item.Kind == MediaKind.Video) item.Kind = MediaKind.Audio;
        var job = new DownloadJob(item, new DownloadOptions
        {
            AudioOnly = true,
            Audio = new AudioOptions(o.AudioFormat == "original" ? "original" : "mp3"),
            EmbedMetadata = true,
            CookiesBrowser = o.Cookies,
        }, _cache.TempDir);
        job.Progress += p => _s.Post(() =>
        {
            if (_working != t.Id || p.Percent is not double pct) return;
            double overall = p.Phase == JobPhase.Converting ? 85 + pct * 0.15 : Math.Min(85, pct * 0.85);
            if (Math.Abs((StatusOf(t.Id)?.Pct ?? 0) - overall) >= 2) Set(t.Id, FileState.Downloading, overall);
        });
        await job.RunAsync(ct);
        return job.ResultPath ?? throw new EngineException(L.T("La conversione non ha prodotto alcun file."));
    }

    // A file sent by someone in the room (or, on the host, one it asked for on behalf of others).
    private void OnFileReceived(string id, string path)
    {
        if (_s.Find(id) is not { } t)
        {
            try { File.Delete(path); } catch { }
            return;
        }
        string final;
        try { final = _cache.Adopt(t, path); }
        catch
        {
            try { File.Delete(path); } catch { }
            return;
        }
        MarkReady(t, final);
        if (_transfers.Remove(id, out var tcs)) tcs.TrySetResult(final);
        Trim();
    }

    private void OnFileProgress(string id, double pct)
    {
        if (_transfers.ContainsKey(id)) Set(id, FileState.Transfer, pct);
    }

    private void OnFileUnavailable(string id)
    {
        if (_transfers.Remove(id, out var tcs)) tcs.TrySetResult(null);
    }

    // At most the chosen number of songs in the cache (the room's own always stay).
    public void Trim()
    {
        try { _cache.Trim(_options().CacheSize, InUse); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _retry?.Dispose();
        _retry = null;
        _workCts?.Cancel();
        foreach (var tcs in _transfers.Values) tcs.TrySetResult(null);
        _transfers.Clear();
        _s.QueueChanged -= Refresh;
        _s.FileReceived -= OnFileReceived;
        _s.FileProgress -= OnFileProgress;
        _s.FileUnavailable -= OnFileUnavailable;
        Trim();
    }
}
