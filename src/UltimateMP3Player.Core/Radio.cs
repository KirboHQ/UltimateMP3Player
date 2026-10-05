using UltimateMP3Player.Core.Together;

namespace UltimateMP3Player.Core;

// A song suggested online that isn't in the library: what's needed to show it in the queue, find it again and download
// it. Its id ("rc-…") is the one in the queue.
public sealed class RadioSong
{
    public const string Prefix = "rc-";

    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public double Duration { get; set; }
    public string Url { get; set; } = "";
    public string Service { get; set; } = "";
    public string? Thumb { get; set; }
    public List<string> Keys { get; set; } = new();

    public static bool IsRadio(string id) => id.StartsWith(Prefix, StringComparison.Ordinal);

    public static RadioSong From(SearchHit h) => new()
    {
        Id = Prefix + Ids.New(), Title = h.Title, Artist = h.Artist, Album = h.Album, Duration = h.Duration ?? 0, Url = h.Url, Service = h.Service,
        Thumb = h.Thumb, Keys = SourceKeys.ForItem(new MediaItem { Url = h.Url, PageUrl = h.Url }),
    };
}

// Gets the files of the suggested songs about to play, like the rooms do: the one playing and the next few, one download
// at a time in the order of the queue, into the same cache (so a song heard in a room is ready here, and the other way).
// The files of the songs still in the queue are never trimmed from the cache.
public sealed class RadioFetcher : IDisposable
{
    private readonly TogetherCache _cache;
    private readonly Func<(string AudioFormat, string? Cookies, int CacheSize)> _options;
    private readonly Action<Action> _post;
    private readonly Dictionary<string, FileStatus> _status = new();
    private readonly Dictionary<string, string> _paths = new();
    private List<RadioSong> _window = new();
    private HashSet<string> _inUse = new(StringComparer.OrdinalIgnoreCase);
    private string? _busy;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public RadioFetcher(TogetherCache cache, Func<(string AudioFormat, string? Cookies, int CacheSize)> options, Action<Action> post)
    {
        _cache = cache;
        _options = options;
        _post = post;
        cache.Use(this, () => _inUse);
    }

    // A song got ready, failed or moved on with its download.
    public event Action<string>? Changed;

    public string? PathFor(string id) => _paths.TryGetValue(id, out var p) && File.Exists(p) ? p : null;
    public FileStatus? StatusOf(string id) => _status.GetValueOrDefault(id);
    public bool IsReady(string id) => PathFor(id) != null;
    public bool IsFailed(string id) => StatusOf(id)?.State == FileState.Failed;

    // The suggested songs to have ready, the one playing (or next to play) first.
    public void Want(IReadOnlyList<RadioSong> window)
    {
        if (_disposed) return;
        _window = window.ToList();
        foreach (var s in _window)
            if (!IsReady(s.Id) && _cache.Find(s.Keys) is { } path) MarkReady(s, path);
        // Work on a song no longer wanted stops; the first one comes before a later one already on its way.
        if (_busy != null && (_window.All(s => s.Id != _busy) || _window.Count > 0 && _window[0].Id != _busy && !IsReady(_window[0].Id) && !IsFailed(_window[0].Id)))
            _cts?.Cancel();
        Next();
    }

    // A failed song is tried again when asked (it may have been a hiccup of the connection).
    public void Retry(string id)
    {
        if (!IsFailed(id)) return;
        _status.Remove(id);
        Next();
    }

    private void Next()
    {
        if (_busy != null || _disposed) return;
        var next = _window.FirstOrDefault(s => !IsReady(s.Id) && !IsFailed(s.Id));
        if (next != null) _ = Download(next);
    }

    private async Task Download(RadioSong s)
    {
        _busy = s.Id;
        var cts = _cts = new CancellationTokenSource();
        Set(s.Id, FileState.Downloading, 0);
        var o = _options();
        try
        {
            var file = await TogetherCache.DownloadAsync(s.Url, o.AudioFormat, o.Cookies, _cache.TempDir, pct => _post(() =>
            {
                if (_busy == s.Id && Math.Abs((StatusOf(s.Id)?.Pct ?? 0) - pct) >= 2) Set(s.Id, FileState.Downloading, pct);
            }), cts.Token);
            if (_disposed || cts.IsCancellationRequested)
            {
                try { File.Delete(file); } catch { }
            }
            else
            {
                MarkReady(s, _cache.Adopt(s.Keys, s.Title, s.Artist, file));
                try { _cache.Trim(o.CacheSize, _inUse); } catch { }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (!IsReady(s.Id)) _status.Remove(s.Id);
            Changed?.Invoke(s.Id);
        }
        catch
        {
            if (!IsReady(s.Id)) Set(s.Id, FileState.Failed, 0);
        }
        finally
        {
            if (_cts == cts)
            {
                _busy = null;
                _cts = null;
            }
            cts.Dispose();
            if (!_disposed) _post(Next);
        }
    }

    private void MarkReady(RadioSong s, string path)
    {
        _paths[s.Id] = path;
        _inUse = _paths.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Set(s.Id, FileState.Ready, 100);
    }

    private void Set(string id, FileState state, double pct)
    {
        _status[id] = new FileStatus { State = state, Pct = Math.Round(pct, 1) };
        Changed?.Invoke(id);
    }

    // Out of the queue, or saved to the library: its file in the cache is free to go.
    public void Forget(string id)
    {
        _status.Remove(id);
        if (_paths.Remove(id)) _inUse = _paths.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_busy == id) _cts?.Cancel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _cache.Release(this);
    }
}
