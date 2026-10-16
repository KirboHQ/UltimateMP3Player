namespace UltimateMP3Player.Core.Together;

// Ahead / HostAhead: songs after the one playing to get ready, as a guest and as the host.
// TakeFromHost: as a guest, songs come from the host first when the host sends them (P2P).
// P2P off: nothing comes from the others, only from the library, the cache and the links.
// Parallel: downloads at a time (the "simultaneous downloads" setting).
public sealed record FetchOptions(string AudioFormat, string? Cookies, int CacheSize, int Ahead, int HostAhead, bool TakeFromHost, bool P2P = true, int Parallel = 1);

// Gets this computer the files of the room's song and the next ones (so nobody has to wait for anyone):
// from the library if the song is already there, from the cache of past rooms, downloaded from its link,
// and as a last resort sent over by someone in the room who has it.
// When the host sends songs and this guest takes them (both chose so), it goes the other way round: the songs the host
// gets ready come from the host; the ones further on are downloaded here and, if that fails, come from the host once it has them.
// Downloads (as many at a time as the settings allow) and transfers (one at a time) go on side by side, in the order of the queue.
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
    private readonly SongCache _cache;
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
    private readonly Lane _transfer = new();
    // One lane per download allowed at a time (more are added when the setting grows).
    private readonly List<Lane> _downloads = new() { new Lane() };
    private Dictionary<string, FileStatus>? _reported;
    private bool _disposed, _refreshPosted;
    private long _lastReport;
    private System.Threading.Timer? _retry;

    public TogetherFetcher(TogetherSession s, Library lib, SongCache cache, Func<FetchOptions> options)
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
        cache.Use(this, () => InUse);
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
        bool p2p = o.P2P && _s.P2PAllowed;
        // Work on songs no longer wanted here stops.
        foreach (var lane in _downloads) if (lane.Id != null && !ids.Contains(lane.Id)) lane.Cancel();
        if (_transfer.Id != null && (!ids.Contains(_transfer.Id) || !p2p)) _transfer.Cancel();
        // Quick look for all of them: already in the library or the cache?
        foreach (var (t, _) in window)
            if (!IsReady(t.Id) && Local(t) is { } path) MarkReady(t, path);

        bool fromHost = p2p && !_s.IsHost && o.TakeFromHost && _s.HostSendsFiles;
        int hostAhead = _s.Host is { } h ? _s.AheadOf(h) : TogetherSession.DefaultAhead;
        long now = Environment.TickCount64;
        (RoomTrack T, int Index)? transfer = null;
        var downloads = new List<(RoomTrack T, int Index)>();
        var waiting = new HashSet<string>();
        foreach (var w in window)
        {
            if (IsReady(w.T.Id)) continue;
            switch (RouteOf(w.T, w.Index, fromHost, p2p, hostAhead, now))
            {
                case Route.Download when _transfer.Id != w.T.Id:
                    if (!Downloading(w.T.Id)) downloads.Add(w);
                    break;
                case Route.Transfer when !Downloading(w.T.Id):
                    transfer ??= w;
                    break;
                // (One already on its way goes on.)
                case Route.WaitHost when !Downloading(w.T.Id) && _transfer.Id != w.T.Id:
                    waiting.Add(w.T.Id);
                    break;
                // Without P2P a song whose link didn't work has nowhere else to come from.
                case Route.Later when !p2p && !Downloading(w.T.Id) && StatusOf(w.T.Id)?.State != FileState.Failed:
                    Set(w.T.Id, FileState.Failed, 0);
                    break;
            }
        }
        Waiting(waiting, transfer?.T.Id);
        // The song playing comes first: a lane busy with a later one lets go of it.
        StartDownloads(downloads, o);
        Start(_transfer, transfer, t => Transfer(t, fromHost && _s.StatusOf(_s.HostId, t.Id)?.State == FileState.Ready));
        Report(true);
    }

    // Where this song comes from now.
    private Route RouteOf(RoomTrack t, int index, bool fromHost, bool p2p, int hostAhead, long now)
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
        return p2p && (!_retryAt.TryGetValue(t.Id, out var at) || now >= at) ? Route.Transfer : Route.Later;
    }

    private bool Downloading(string id) => _downloads.Any(l => l.Id == id);

    // The songs to download, in the order of the queue, into the free lanes. With none free the song playing (if it's one of
    // them) takes the lane of the furthest one.
    private void StartDownloads(List<(RoomTrack T, int Index)> todo, FetchOptions o)
    {
        int limit = Math.Clamp(o.Parallel, 1, 6);
        while (_downloads.Count < limit) _downloads.Add(new Lane());
        foreach (var w in todo)
        {
            // (lanes beyond a lowered limit finish their song and stay unused)
            var free = _downloads.Take(limit).FirstOrDefault(l => l.Id == null);
            if (free != null)
            {
                _ = Download(w.T, free);
                continue;
            }
            if (w.Index == 0 && _downloads.Where(l => l.Id != null).OrderByDescending(l => WindowIndex(l.Id!)).FirstOrDefault() is { } furthest &&
                WindowIndex(furthest.Id!) > 0)
                furthest.Cancel();
            break;
        }
    }

    private int WindowIndex(string id) => _s.Current?.Id == id ? 0 : _s.Queue.FindIndex(t => t.Id == id) is var i and >= 0 ? i + 1 : int.MaxValue;

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
            if (!IsReady(id) && id != arriving && _transfer.Id != id && !Downloading(id)) Set(id, FileState.None, 0);
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
        foreach (var lane in _downloads) lane.Cancel(t.Id);
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
    private async Task Download(RoomTrack t, Lane lane)
    {
        var cts = Begin(lane, t.Id);
        try
        {
            Set(t.Id, FileState.Downloading, 0);
            var file = await DownloadFile(t, lane, cts.Token);
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
        finally { End(lane, cts); }
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

    private async Task<string> DownloadFile(RoomTrack t, Lane lane, CancellationToken ct)
    {
        var o = _options();
        // A link that can't be downloaded (DRM, removed, private): the same song from YouTube, like everywhere else.
        var (file, _) = await CloudSongs.DownloadAsync(t.SourceUrl!, t.Title, t.Artist, t.Duration, o.AudioFormat, o.Cookies, _cache.TempDir, pct => _s.Post(() =>
        {
            if (lane.Id == t.Id && Math.Abs((StatusOf(t.Id)?.Pct ?? 0) - pct) >= 2) Set(t.Id, FileState.Downloading, pct);
        }), ct);
        return file;
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
        foreach (var lane in _downloads) lane.Cancel();
        _transfer.Cancel();
        foreach (var tcs in _transfers.Values) tcs.TrySetResult(null);
        _transfers.Clear();
        _s.QueueChanged -= Refresh;
        _s.FilesChanged -= RefreshSoon;
        _s.RoomChanged -= RefreshSoon;
        _s.FileReceived -= OnFileReceived;
        _s.FileProgress -= OnFileProgress;
        _s.FileUnavailable -= OnFileUnavailable;
        _cache.Release(this);
        Trim();
    }
}
