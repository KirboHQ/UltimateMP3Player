namespace UltimateMP3Player.Core.Together;

// Ahead / HostAhead: songs after the one playing to get ready, as a guest and as the host.
// TakeFromHost: as a guest, songs come from the host first when the host sends them (P2P).
public sealed record FetchOptions(string AudioFormat, string? Cookies, int CacheSize, int Ahead, int HostAhead, bool TakeFromHost);

// Gets this computer the files of the room's song and the next ones (so nobody has to wait for anyone):
// from the library if the song is already there, from the cache of past rooms, downloaded from its link,
// and as a last resort sent over by someone in the room who has it.
// When the host sends songs and this guest takes them (both chose so), it goes the other way round: the songs the host
// gets ready come from the host; the ones further on are downloaded here and, if that fails, come from the host once it has them.
// Downloads and transfers go on side by side, one of each at a time, in the order of the queue.
public sealed class TogetherFetcher : IDisposable
{
    private enum Route { Download, Transfer, WaitHost, Later }

    // One thing at a time: a download or a transfer, of one song.
    private sealed class Lane
    {
        public string? Id;
        public CancellationTokenSource? Cts;

        public void Cancel(string? id = null)
        {
            if (Id != null && (id == null || Id == id)) Cts?.Cancel();
        }
    }

    private readonly TogetherSession _s;
    private readonly Library _lib;
    private readonly TogetherCache _cache;
    private readonly Func<FetchOptions> _options;
    private readonly Dictionary<string, FileStatus> _status = new();
    private readonly Dictionary<string, string> _paths = new();
    private readonly Dictionary<string, Track> _libraryMatch = new();
    private readonly HashSet<string> _libraryChecked = new();
    // Its link didn't work here (or it has none).
    private readonly HashSet<string> _noLink = new();
    // Asked to the host while it had it, and it didn't arrive: not from the host again.
    private readonly HashSet<string> _hostFailed = new();
    // Waiting for the host to have it, to take it from there.
    private readonly HashSet<string> _waitHost = new();
    private readonly Dictionary<string, long> _retryAt = new();
    private readonly Dictionary<string, TaskCompletionSource<string?>> _transfers = new();
    private readonly Lane _download = new(), _transfer = new();
    private Dictionary<string, FileStatus>? _reported;
    private bool _disposed, _refreshPosted;
    private long _lastReport;
    private System.Threading.Timer? _retry;

    public TogetherFetcher(TogetherSession s, Library lib, TogetherCache cache, Func<FetchOptions> options)
    {
        _s = s;
        _lib = lib;
        _cache = cache;
        _options = options;
        s.QueueChanged += Refresh;
        // What the others have (the host above all) and who the host is decide where songs come from.
        s.FilesChanged += RefreshSoon;
        s.RoomChanged += RefreshSoon;
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
    // Waiting for the host to get it ready, to receive it from there.
    public bool WaitingHost(string itemId) => _waitHost.Contains(itemId);
    // The library song that is the same as this room song, if any.
    public Track? LibraryTrack(string itemId) => _libraryMatch.GetValueOrDefault(itemId);
    // Files the room is using right now (the cache keeps them).
    public HashSet<string> InUse => _paths.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private int MyAhead(FetchOptions o) => Math.Clamp(_s.IsHost ? o.HostAhead : o.Ahead, 1, TogetherSession.MaxAhead);

    // The song playing (0) and the next ones this computer gets ready, with their place.
    private List<(RoomTrack T, int Index)> Window(FetchOptions o)
    {
        var list = new List<(RoomTrack, int)>();
        if (_s.Current != null) list.Add((_s.Current, 0));
        list.AddRange(_s.Queue.Take(MyAhead(o)).Select((t, i) => (t, i + 1)));
        return list;
    }

    private void RefreshSoon()
    {
        if (_disposed || _refreshPosted) return;
        _refreshPosted = true;
        _s.Post(() =>
        {
            _refreshPosted = false;
            Refresh();
        });
    }

    // The queue changed (or a retry is due, or the host got a song): what to get next.
    public void Refresh()
    {
        if (_disposed) return;
        var inRoom = new[] { _s.Current }.Concat(_s.Queue).OfType<RoomTrack>().Select(t => t.Id).ToHashSet();
        foreach (var id in _status.Keys.Concat(_waitHost).Where(k => !inRoom.Contains(k)).ToList())
        {
            _status.Remove(id);
            _paths.Remove(id);
            _libraryMatch.Remove(id);
            _libraryChecked.Remove(id);
            _retryAt.Remove(id);
            _noLink.Remove(id);
            _hostFailed.Remove(id);
            _waitHost.Remove(id);
        }
        var o = _options();
        var window = Window(o);
        var ids = window.Select(w => w.T.Id).ToHashSet();
        // Work on songs no longer wanted here stops.
        if (_download.Id != null && !ids.Contains(_download.Id)) _download.Cancel();
        if (_transfer.Id != null && !ids.Contains(_transfer.Id)) _transfer.Cancel();
        // Quick look for all of them: already in the library or the cache?
        foreach (var (t, _) in window)
            if (!IsReady(t.Id) && Local(t) is { } path) MarkReady(t, path);

        bool fromHost = !_s.IsHost && o.TakeFromHost && _s.HostSendsFiles;
        int hostAhead = _s.Host is { } h ? _s.AheadOf(h) : TogetherSession.DefaultAhead;
        long now = Environment.TickCount64;
        (RoomTrack T, int Index)? download = null, transfer = null;
        var waiting = new HashSet<string>();
        foreach (var w in window)
        {
            if (IsReady(w.T.Id)) continue;
            switch (RouteOf(w.T, w.Index, fromHost, hostAhead, now))
            {
                case Route.Download when _transfer.Id != w.T.Id:
                    download ??= w;
                    break;
                case Route.Transfer when _download.Id != w.T.Id:
                    transfer ??= w;
                    break;
                // (One already on its way goes on.)
                case Route.WaitHost when _download.Id != w.T.Id && _transfer.Id != w.T.Id:
                    waiting.Add(w.T.Id);
                    break;
            }
        }
        Waiting(waiting, transfer?.T.Id);
        // The song playing comes first: a lane busy with a later one lets go of it.
        Start(_download, download, t => Download(t));
        Start(_transfer, transfer, t => Transfer(t, fromHost && _s.StatusOf(_s.HostId, t.Id)?.State == FileState.Ready));
        Report(true);
    }

    // Where this song comes from now.
    private Route RouteOf(RoomTrack t, int index, bool fromHost, int hostAhead, long now)
    {
        bool link = t.SourceUrl != null && !_noLink.Contains(t.Id);
        if (fromHost && !_hostFailed.Contains(t.Id))
        {
            var host = _s.StatusOf(_s.HostId, t.Id)?.State;
            // The host has it: straight from it.
            if (host == FileState.Ready) return Route.Transfer;
            // One of the songs the host gets ready, or one that couldn't be downloaded here: wait for the host
            // (unless it can't get it either).
            if (host != FileState.Failed && (index <= hostAhead || !link)) return Route.WaitHost;
        }
        if (link) return Route.Download;
        // No link, or it didn't work: from someone in the room, now and then.
        return !_retryAt.TryGetValue(t.Id, out var at) || now >= at ? Route.Transfer : Route.Later;
    }

    private void Start(Lane lane, (RoomTrack T, int Index)? next, Func<RoomTrack, Task> work)
    {
        if (next is not { } n) return;
        if (lane.Id == null) _ = work(n.T);
        else if (n.Index == 0 && lane.Id != n.T.Id) lane.Cancel();
    }

    // Songs waiting for the host show as "on the way" (to the others too).
    private void Waiting(HashSet<string> now, string? arriving)
    {
        foreach (var id in _waitHost.Where(id => !now.Contains(id)).ToList())
        {
            _waitHost.Remove(id);
            if (!IsReady(id) && id != arriving && _transfer.Id != id && _download.Id != id) Set(id, FileState.None, 0);
        }
        foreach (var id in now)
            if (_waitHost.Add(id)) Set(id, FileState.Transfer, 0);
    }

    private bool IsReady(string id) => _status.TryGetValue(id, out var s) && s.State == FileState.Ready && PathFor(id) != null;

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
        _waitHost.Remove(t.Id);
        Set(t.Id, FileState.Ready, 100, t.Duration);
        // Got it one way: the other way stops.
        _download.Cancel(t.Id);
        _transfer.Cancel(t.Id);
    }

    private void Set(string id, FileState state, double pct, double duration = 0)
    {
        _status[id] = new FileStatus { State = state, Pct = Math.Round(pct, 1), Duration = duration };
        Changed?.Invoke();
        Report(state is FileState.Ready or FileState.Failed or FileState.None);
    }

    // To the host (and from there to everyone), only when something changed.
    private void Report(bool now)
    {
        long t = Environment.TickCount64;
        if (!now && t - _lastReport < 400) return;
        if (_reported != null && _reported.Count == _status.Count
            && _status.All(kv => _reported.TryGetValue(kv.Key, out var was) && was.SameAs(kv.Value) && was.Duration == kv.Value.Duration)) return;
        _lastReport = t;
        _reported = new Dictionary<string, FileStatus>(_status);
        _s.ReportFiles(_status);
    }

    // From its link, into the cache.
    private async Task Download(RoomTrack t)
    {
        var cts = Begin(_download, t.Id);
        try
        {
            Set(t.Id, FileState.Downloading, 0);
            var file = await DownloadFile(t, cts.Token);
            if (!_disposed && !cts.IsCancellationRequested && _s.Find(t.Id) != null && !IsReady(t.Id))
            {
                MarkReady(t, _cache.Adopt(t, file));
                Trim();
            }
            else
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (!IsReady(t.Id)) Set(t.Id, FileState.None, 0);
        }
        catch
        {
            // Next: from someone in the room.
            _noLink.Add(t.Id);
            if (!IsReady(t.Id)) Set(t.Id, FileState.None, 0);
        }
        finally { End(_download, cts); }
    }

    // From someone in the room, through the host (from the host itself when it has it).
    private async Task Transfer(RoomTrack t, bool fromHost)
    {
        var cts = Begin(_transfer, t.Id);
        try
        {
            _waitHost.Remove(t.Id);
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
                // Didn't come from the host: from its link then, or from anyone now and then.
                if (fromHost) _hostFailed.Add(t.Id);
                bool link = t.SourceUrl != null && !_noLink.Contains(t.Id);
                if (!link) _retryAt[t.Id] = Environment.TickCount64 + 12000;
                Set(t.Id, link ? FileState.None : FileState.Failed, 0);
            }
        }
        finally { End(_transfer, cts); }
    }

    private static CancellationTokenSource Begin(Lane lane, string id)
    {
        lane.Id = id;
        return lane.Cts = new CancellationTokenSource();
    }

    private void End(Lane lane, CancellationTokenSource cts)
    {
        if (lane.Cts == cts)
        {
            lane.Id = null;
            lane.Cts = null;
        }
        cts.Dispose();
        if (!_disposed) _s.Post(Refresh);
    }

    private async Task<string> DownloadFile(RoomTrack t, CancellationToken ct)
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
            if (_download.Id != t.Id || p.Percent is not double pct) return;
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
        if (_transfers.Remove(id, out var tcs)) tcs.TrySetResult(final);
        MarkReady(t, final);
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
        _download.Cancel();
        _transfer.Cancel();
        foreach (var tcs in _transfers.Values) tcs.TrySetResult(null);
        _transfers.Clear();
        _s.QueueChanged -= Refresh;
        _s.FilesChanged -= RefreshSoon;
        _s.RoomChanged -= RefreshSoon;
        _s.FileReceived -= OnFileReceived;
        _s.FileProgress -= OnFileProgress;
        _s.FileUnavailable -= OnFileUnavailable;
        Trim();
    }
}
