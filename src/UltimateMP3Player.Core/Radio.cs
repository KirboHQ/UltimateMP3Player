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
    // Not suggested: a song of a link you chose to hear without downloading it into the library.
    public bool FromLink { get; set; }
    // The audio quality chosen for it (a library song in the cloud: Track.AudioQuality); null: the one in Settings.
    public string? Quality { get; set; }
    // A song of the connected phone (Connect phone): its id there. Its file comes from the phone; once the phone is gone,
    // from its link (if it has one).
    public string? Phone { get; set; }

    public static bool IsRadio(string id) => id.StartsWith(Prefix, StringComparison.Ordinal);

    // A song of an analyzed link (its page is what gets downloaded into the cache, found again from there), with the
    // title and artist a download would give it ("Janji - Heroes Tonight" on a channel: artist Janji).
    public static RadioSong FromItem(MediaItem item)
    {
        var meta = SongMeta.From(item);
        return new RadioSong
        {
            Id = Prefix + Ids.New(), Title = meta.Title, Artist = meta.Artist, Album = meta.Album, Duration = item.Duration ?? 0,
            Url = item.PageUrl ?? item.Url, Service = Sites.ServiceOf(item), Thumb = Pictures(item).FirstOrDefault(),
            Keys = SourceKeys.ForItem(item), FromLink = true,
        };
    }

    // Its pictures, the best first (the biggest one of a video isn't always there).
    public static List<string> Pictures(MediaItem item) =>
        (item.CoverUrl != null ? new[] { item.CoverUrl } : Array.Empty<string>()).Concat(item.Thumbnails).Distinct().ToList();

    public static RadioSong From(SearchHit h) => new()
    {
        Id = Prefix + Ids.New(), Title = h.Title, Artist = h.Artist, Album = h.Album, Duration = h.Duration ?? 0, Url = h.Url, Service = h.Service,
        Thumb = h.Thumb, Keys = h.Keys(),
    };
}

// Parallel: downloads at a time (the "simultaneous downloads" setting).
public sealed record RadioFetchOptions(string AudioFormat, string? Cookies, int CacheSize, int Parallel);

// Gets the files of the suggested songs about to play, like the rooms do: the one playing and the next few, in the order
// of the queue (as many at a time as the settings allow), into the same cache (so a song heard in a room is ready here, and
// the other way). The files of the songs still in the queue are never trimmed from the cache.
public sealed class RadioFetcher : IDisposable
{
    private readonly SongCache _cache;
    private readonly Func<RadioFetchOptions> _options;
    private readonly Action<Action> _post;
    private readonly Dictionary<string, FileStatus> _status = new();
    private readonly Dictionary<string, string> _paths = new();
    private List<RadioSong> _window = new();
    private HashSet<string> _inUse = new(StringComparer.OrdinalIgnoreCase);
    // The downloads going on (song id → how to stop it).
    private readonly Dictionary<string, CancellationTokenSource> _running = new();
    // Songs still downloading whose audio can be heard from their site meanwhile (the analysis is done).
    private readonly Dictionary<string, SongStream> _streams = new();
    private bool _disposed;

    public RadioFetcher(SongCache cache, Func<RadioFetchOptions> options, Action<Action> post)
    {
        _cache = cache;
        _options = options;
        _post = post;
        cache.Use(this, () => _inUse);
    }

    // A song got ready, failed or moved on with its download.
    public event Action<string>? Changed;
    // Gets a song of the connected phone from the phone, into this folder (progress 0-100): its file, or null when it
    // can't (no phone).
    public Func<RadioSong, string, Action<double>, CancellationToken, Task<string?>>? FromPhone { get; set; }
    // Its link didn't work and the same song was found elsewhere (id, the new link, its keys): heard from there from now on.
    public event Action<string, string, List<string>>? Replaced;

    private readonly HashSet<string> _gone = new(), _offline = new();

    public string? PathFor(string id) => _paths.TryGetValue(id, out var p) && File.Exists(p) ? p : null;
    public FileStatus? StatusOf(string id) => _status.GetValueOrDefault(id);
    public bool IsReady(string id) => PathFor(id) != null;
    public SongStream? StreamOf(string id) => _streams.GetValueOrDefault(id);
    // Its site refused it: the song waits for its file.
    public void DropStream(string id) => _streams.Remove(id);
    public bool IsFailed(string id) => StatusOf(id)?.State == FileState.Failed;
    // Failed because the song is gone from its site and nothing like it was found elsewhere (not a connection problem).
    public bool IsGone(string id) => _gone.Contains(id);
    // Failed because the site couldn't be reached (no connection).
    public bool IsOffline(string id) => _offline.Contains(id);

    // The suggested songs to have ready, the one playing (or next to play) first.
    public void Want(IReadOnlyList<RadioSong> window)
    {
        if (_disposed) return;
        _window = window.ToList();
        foreach (var s in _window)
            if (!IsReady(s.Id) && _cache.Find(s.Keys) is { } path) MarkReady(s, path);
        // Work on a song no longer wanted stops.
        foreach (var id in _running.Keys.Where(id => _window.All(s => s.Id != id)).ToList()) _running[id].Cancel();
        // The first one comes before a later one already on its way, when there's no room for both.
        if (_window.Count > 0 && _window[0] is var first && !IsReady(first.Id) && !IsFailed(first.Id) && !_running.ContainsKey(first.Id) &&
            Active() >= Limit() && _window.LastOrDefault(s => _running.TryGetValue(s.Id, out var c) && !c.IsCancellationRequested) is { } later)
            _running[later.Id].Cancel();
        Next();
    }

    private int Limit() => Math.Clamp(_options().Parallel, 1, 6);

    // The downloads going on that weren't asked to stop.
    private int Active() => _running.Values.Count(c => !c.IsCancellationRequested);

    // A failed song is tried again when asked (it may have been a hiccup of the connection).
    public void Retry(string id)
    {
        if (!IsFailed(id)) return;
        _status.Remove(id);
        _gone.Remove(id);
        _offline.Remove(id);
        Next();
    }

    private void Next()
    {
        if (_disposed) return;
        int free = Limit() - Active();
        foreach (var s in _window.Where(s => !IsReady(s.Id) && !IsFailed(s.Id) && !_running.ContainsKey(s.Id)).Take(Math.Max(0, free)).ToList())
            _ = Download(s);
    }

    private async Task Download(RadioSong s)
    {
        var cts = _running[s.Id] = new CancellationTokenSource();
        Set(s.Id, FileState.Downloading, 0);
        var o = _options();
        Action<double> progress = pct => _post(() =>
        {
            if (_running.GetValueOrDefault(s.Id) == cts && Math.Abs((StatusOf(s.Id)?.Pct ?? 0) - pct) >= 2) Set(s.Id, FileState.Downloading, pct);
        });
        Action<SongStream> stream = link => _post(() =>
        {
            if (_running.GetValueOrDefault(s.Id) != cts || IsReady(s.Id)) return;
            _streams[s.Id] = link;
            Changed?.Invoke(s.Id);
        });
        try
        {
            var keys = s.Keys;
            string? file = null;
            YtInfo? info = null;
            // A song of the connected phone comes from the phone; without the phone, from its link (if it has one).
            if (s.Phone != null && FromPhone != null)
            {
                file = await FromPhone(s, _cache.TempDir, progress, cts.Token);
                cts.Token.ThrowIfCancellationRequested();
            }
            if (file == null)
            {
                if (s.Phone != null && !s.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) throw new IOException("The phone isn't connected.");
                try
                {
                    // Its link doesn't work (removed, private, DRM): the same song from YouTube.
                    (file, info) = await CloudSongs.DownloadAsync(s.Url, s.Title, s.Artist, s.Duration, s.Quality ?? o.AudioFormat, o.Cookies, _cache.TempDir, progress, cts.Token,
                        stream);
                }
                catch (EngineException ex) when (ex is { NotFoundElsewhere: true, Gone: true })
                {
                    _gone.Add(s.Id);
                    throw;
                }
            }
            if (info != null)
            {
                var found = SourceKeys.ForInfo(info);
                keys = s.Keys.Concat(found).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (!_disposed && !cts.IsCancellationRequested)
                {
                    s.Url = info.WebpageUrl;
                    s.Service = Sites.NameFor(info.WebpageUrl);
                    s.Keys = keys;
                    Replaced?.Invoke(s.Id, info.WebpageUrl, found);
                }
            }
            if (_disposed || cts.IsCancellationRequested)
            {
                try { File.Delete(file); } catch { }
            }
            else
            {
                MarkReady(s, _cache.Adopt(keys, s.Title, s.Artist, file));
                try { _cache.Trim(o.CacheSize, _inUse); } catch { }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (!IsReady(s.Id)) _status.Remove(s.Id);
            Changed?.Invoke(s.Id);
        }
        catch (Exception ex)
        {
            if (ex is EngineException { Offline: true } or HttpRequestException) _offline.Add(s.Id);
            if (!IsReady(s.Id)) Set(s.Id, FileState.Failed, 0);
        }
        finally
        {
            if (_running.GetValueOrDefault(s.Id) == cts)
            {
                _running.Remove(s.Id);
                _streams.Remove(s.Id);
            }
            cts.Dispose();
            if (!_disposed) _post(Next);
        }
    }

    // A file that's already in the cache (a saved song deleted while it played).
    public void Have(RadioSong s, string path)
    {
        if (!_disposed) MarkReady(s, path);
    }

    private void MarkReady(RadioSong s, string path)
    {
        _paths[s.Id] = path;
        _streams.Remove(s.Id);
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
        _streams.Remove(id);
        _gone.Remove(id);
        _offline.Remove(id);
        if (_paths.Remove(id)) _inUse = _paths.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_running.TryGetValue(id, out var cts)) cts.Cancel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var cts in _running.Values) cts.Cancel();
        _cache.Release(this);
    }
}
