using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// Web thumbnails; ffmpeg decodes what WPF can't.
public static class WebImages
{
    private static readonly SemaphoreSlim Gate = new(6);
    private static readonly Dictionary<string, WeakReference<BitmapSource>> Cache = new();

    public static async Task<ImageSource?> LoadAsync(IEnumerable<string> urls, int width, string? referer = null)
    {
        foreach (var url in urls.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct().Take(4))
        {
            var key = url + "|" + width;
            lock (Cache)
                if (Cache.TryGetValue(key, out var w) && w.TryGetTarget(out var hit)) return hit;
            await Gate.WaitAsync();
            try
            {
                var img = await Task.Run(async () =>
                {
                    var data = await Http.GetBytesAsync(url, referer);
                    return Decode(data, width) ?? await DecodeWithFfmpeg(data, width);
                });
                if (img != null)
                {
                    lock (Cache)
                    {
                        if (Cache.Count > 500) Cache.Clear();
                        Cache[key] = new WeakReference<BitmapSource>(img);
                    }
                    return img;
                }
            }
            catch { }
            finally { Gate.Release(); }
        }
        return null;
    }

    private static BitmapSource? Decode(byte[] data, int width) => Images.FromBytes(data, width);

    private static async Task<BitmapSource?> DecodeWithFfmpeg(byte[] data, int width)
    {
        if (!File.Exists(Engines.Ffmpeg)) return null;
        var id = Guid.NewGuid().ToString("N");
        var src = Path.Combine(AppPaths.TempDir, id + ".src");
        var dst = Path.Combine(AppPaths.TempDir, id + ".png");
        try
        {
            await File.WriteAllBytesAsync(src, data);
            var r = await ProcRunner.RunAsync(Engines.Ffmpeg, new[] { "-v", "error", "-y", "-i", src, "-frames:v", "1", "-vf", $"scale='min({width},iw)':-2", dst },
                timeout: TimeSpan.FromSeconds(20));
            return r.ExitCode == 0 && File.Exists(dst) ? Decode(await File.ReadAllBytesAsync(dst), 0) : null;
        }
        catch { return null; }
        finally
        {
            try { File.Delete(src); } catch { }
            try { File.Delete(dst); } catch { }
        }
    }
}

// Where one request's songs go, in the original order, and the tags they get.
public sealed class DownloadBatch
{
    public DownloadBatch(Profile profile, string? playlistId, int size, IReadOnlyList<string>? tagIds = null)
    {
        Profile = profile;
        PlaylistId = playlistId;
        Slots = new string?[size];
        TagIds = tagIds ?? Array.Empty<string>();
    }

    public Profile Profile { get; }
    public string? PlaylistId { get; }
    public string?[] Slots { get; }
    public IReadOnlyList<string> TagIds { get; }
    // Songs of an imported pack: they go into their playlists and tags (index, library id).
    public Action<int, string>? Placed { get; init; }

    public void Place(int index, string trackId)
    {
        Slots[index] = trackId;
        // A tag deleted meanwhile is skipped.
        foreach (var tag in TagIds)
            if (Profile.GetTag(tag) != null) Profile.SetTag(new[] { trackId }, tag, true);
        Placed?.Invoke(index, trackId);
        if (PlaylistId == null || Profile.GetPlaylist(PlaylistId) is not { } pl || Profile.Contains(pl, trackId)) return;
        int? at = null;
        for (int j = index - 1; j >= 0 && at == null; j--)
            if (Slots[j] is { } prev && pl.Tracks.IndexOf(prev) is int p and >= 0) at = p + 1;
        for (int j = index + 1; j < Slots.Length && at == null; j++)
            if (Slots[j] is { } next && pl.Tracks.IndexOf(next) is int p and >= 0) at = p;
        Profile.AddTrack(pl, trackId, at);
    }
}

public enum JobState { Queued, Running, Done, Present, Failed, Canceled }

public sealed class DownloadJobViewModel : Observable
{
    private readonly DownloadQueue _queue;
    private CancellationTokenSource? _cts;
    private int _rateLimits;

    // attachTo: a library song this is for (one in the cloud saved on the device, or its video: videoOnly).
    public DownloadJobViewModel(MediaItem item, DownloadBatch batch, int index, bool wantVideo, DownloadQueue queue, string? attachTo = null, bool videoOnly = false)
    {
        Item = item;
        Batch = batch;
        Index = index;
        WantVideo = wantVideo;
        AttachTo = attachTo;
        VideoOnly = videoOnly;
        _queue = queue;
        CancelCommand = new RelayCommand(Cancel);
        RetryCommand = new RelayCommand(Retry);
        RetryYouTubeCommand = new RelayCommand(RetryOnYouTube);
        RemoveCommand = new RelayCommand(() => _queue.Remove(this));
        PlayCommand = new RelayCommand(Play);
        LoginCommand = new RelayCommand(() => _queue.Host.Session?.SearchSettings("cookie"));
        StatusText = L.T("In coda");
    }

    public MediaItem Item { get; private set; }
    // Rate limits apply per service ("YouTube", "SoundCloud"...).
    public string Service => Sites.ServiceOf(Item);
    public DownloadBatch Batch { get; }
    public int Index { get; }
    public bool WantVideo { get; }
    public string? AttachTo { get; }
    public bool VideoOnly { get; }
    // Saving a song of the library that's in the cloud (its audio onto the device).
    public bool IsSave => AttachTo != null && !VideoOnly;
    public Track? Result { get; private set; }
    private IReadOnlyList<string>? _originalKeys;
    // Its own link didn't work: the same song found on YouTube instead (once, by itself, for a song being saved).
    private bool _foundElsewhere;

    public ICommand CancelCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand RetryYouTubeCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand PlayCommand { get; }
    // The settings on the browser's cookies (the phone opens the site's page to sign in instead: DownloadsPage).
    public ICommand LoginCommand { get; }

    public bool ViaYouTube => _originalKeys != null;
    // The page of the site that asked for a login (the one actually downloaded from, e.g. YouTube for a Spotify song).
    public string LoginUrl => Item.Info?.WebpageUrl is { Length: > 0 } page ? page : Item.PageUrl ?? Item.Url;
    public string Title => Result?.Title is { Length: > 0 } t ? t : !string.IsNullOrWhiteSpace(Item.Title) ? Item.Title : TitleFromLink(Item.PageUrl ?? Item.Url);

    // A song the list gave without a name (SoundCloud's Go+ ones): the last part of its link, "hide-n-seek" → "hide n seek".
    private static string TitleFromLink(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return L.T("Brano senza titolo");
        var last = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrEmpty(last) || last is "watch" or "track" or "tracks") return L.T("Brano senza titolo");
        return Uri.UnescapeDataString(last).Replace('-', ' ').Replace('_', ' ').Trim();
    }
    public string Subtitle => string.Join(" · ", new[] { Result?.Artist ?? Item.Artist, Item.SiteName,
            VideoOnly ? L.T("solo il video") : IsSave ? L.T("salvataggio sul dispositivo") : WantVideo ? L.T("con video") : null,
            ViaYouTube ? L.T("cercato su YouTube") : null }
        .Where(s => !string.IsNullOrWhiteSpace(s)));

    // The downloaded song, if it is still in the library.
    public TrackViewModel? Song => CanPlay && _queue.Host.Session is { } s && s.Library.Get(Result!.Id) is { } t ? s.Vm(t) : null;

    private void Play()
    {
        if (Song is { } t) t.Main.PlayInLibrary(t);
    }

    private ImageSource? _thumb;
    private bool _thumbRequested;
    public ImageSource? Thumb
    {
        get
        {
            if (!_thumbRequested)
            {
                _thumbRequested = true;
                _ = LoadThumb();
            }
            return _thumb;
        }
    }

    private async Task LoadThumb()
    {
        if (Result is { HasCover: true } r)
            _thumb = await Images.LoadAsync(AppPaths.TrackCover(r.Id), 96, r.CoverVersion);
        else
        {
            var urls = Item.Thumbnails.Concat(Item.Info?.Thumbnails ?? new List<string>()).ToList();
            if (Item.CoverUrl != null) urls.Insert(0, Item.CoverUrl);
            if (urls.Count == 0) return;
            _thumb = await WebImages.LoadAsync(urls, 96, Item.PageUrl);
        }
        OnChanged(nameof(Thumb));
    }

    private JobState _state = JobState.Queued;
    public JobState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            OnChanged(nameof(IsActive), nameof(IsFinished), nameof(IsFailed), nameof(CanRetry), nameof(CanRetryYouTube), nameof(IsRunning),
                nameof(StatusBrush), nameof(CanPlay), nameof(Indeterminate), nameof(NeedsLogin));
            if (IsSave) _queue.OnSaving(this);
        }
    }

    public bool IsActive => State is JobState.Queued or JobState.Running;
    public bool IsFinished => !IsActive;
    public bool IsDone => State is JobState.Done or JobState.Present;
    public bool IsFailed => State is JobState.Failed or JobState.Canceled;
    public bool IsRunning => State == JobState.Running;
    public bool CanRetry => IsFailed;
    public bool CanRetryYouTube => State == JobState.Failed && !ViaYouTube && !VideoOnly;
    public bool CanPlay => IsDone && Result != null;

    // Failed because the site wants a login (private, age-restricted, members only, YouTube's bot check) the download
    // didn't have: a button next to the error leads to it.
    private bool _loginFailure;
    public bool NeedsLogin => State == JobState.Failed && _loginFailure;

    public Brush StatusBrush => State switch
    {
        JobState.Done or JobState.Present => (Brush)Application.Current.Resources["SuccessBrush"],
        JobState.Failed => (Brush)Application.Current.Resources["ErrorBrush"],
        _ => (Brush)Application.Current.Resources["SubTextBrush"],
    };

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private string? _details;
    public string? Details { get => _details; private set => Set(ref _details, value); }

    private double _percent;
    public double Percent
    {
        get => _percent;
        private set
        {
            if (Set(ref _percent, value) && IsSave) _queue.OnSaving(this);
        }
    }

    // Only a running download's bar goes back and forth: the bar of one waiting or finished is hidden, but its animation
    // would still run (a frame every few milliseconds for each row, the app never idle while the list is full).
    private bool _indeterminate = true;
    public bool Indeterminate
    {
        get => _indeterminate && IsRunning;
        private set
        {
            if (_indeterminate == value) return;
            _indeterminate = value;
            OnChanged(nameof(Indeterminate));
        }
    }

    public void Start()
    {
        if (State != JobState.Queued) return;
        _loginFailure = false;
        State = JobState.Running;
        StatusText = L.T("Avvio…");
        Indeterminate = true;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var s = _queue.Host.Settings;
        var req = new TrackRequest(Item, WantVideo, s.VideoMaxRes, s.AudioFormat, s.MusicDir, s.CookiesBrowserOrNull, _originalKeys, AttachTo, VideoOnly);
        var ui = Application.Current.Dispatcher;
        var service = Service;
        bool gentle = _queue.IsGentle(service);
        try
        {
            Directory.CreateDirectory(s.MusicDir);
            var res = await Task.Run(() =>
            {
                YtDlp.Gentle = gentle;
                return TrackDownloader.RunAsync(_queue.Host.Library, req, p => ui.BeginInvoke(() => Apply(p)), ct);
            }, ct);
            Result = res.Track;
            Batch.Place(Index, res.Track.Id);
            Percent = 100;
            State = res.Outcome == TrackOutcome.AlreadyPresent ? JobState.Present : JobState.Done;
            StatusText = res.Outcome switch
            {
                TrackOutcome.AlreadyPresent when VideoOnly => L.T("Il brano ha già un video"),
                TrackOutcome.AlreadyPresent when IsSave => L.T("Era già salvato sul dispositivo"),
                TrackOutcome.AlreadyPresent => L.T("Già nella libreria: non riscaricato"),
                TrackOutcome.VideoAdded => VideoOnly ? L.T("Video aggiunto al brano") : L.T("Video aggiunto al brano già presente"),
                TrackOutcome.Saved => L.T("Salvato sul dispositivo") + (_foundElsewhere ? " · " + L.F("da {0}: il link di prima non funzionava più", Sites.NameFor(res.Track.SourceUrl ?? "")) : ""),
                _ => L.T("Completato") + (res.Note != null ? " · " + res.Note : ""),
            };
            if (_foundElsewhere && _queue.Host.Session is { } main)
                main.Toast(L.F("«{0}» non c'era più al suo link: salvato da {1}", res.Track.Title, Sites.NameFor(res.Track.SourceUrl ?? "")));
            _thumbRequested = false;
            OnChanged(nameof(Title), nameof(Subtitle), nameof(Thumb));
            _queue.OnSucceeded(service);
        }
        catch (OperationCanceledException)
        {
            State = JobState.Canceled;
            StatusText = L.T("Annullato");
        }
        catch (EngineException ex) when ((ex.RateLimited || (ex.Forbidden && _queue.OnForbidden(service))) && ++_rateLimits <= 6)
        {
            // Back in the queue: it restarts on its own when the site allows it.
            WaitForSite(ex.Details);
            _queue.OnRateLimited(service);
        }
        catch (EngineException ex)
        {
            // A song of the library being saved whose link no longer works: the same song on YouTube (a close match), saved
            // from there, and that becomes its link.
            bool searched = false;
            if (IsSave && ex.LinkProblem && !_foundElsewhere && !ViaYouTube && !ct.IsCancellationRequested)
            {
                searched = true;
                if (await FindElsewhere(ct))
                {
                    await RunAsync(ct);
                    return;
                }
                if (ct.IsCancellationRequested)
                {
                    State = JobState.Canceled;
                    StatusText = L.T("Annullato");
                    return;
                }
                if (ex.Gone && _queue.Host.Library.Get(AttachTo!) is { Unavailable: false } gone)
                {
                    gone.Unavailable = true;
                    _queue.Host.Library.Changed(gone);
                }
            }
            ForbiddenAt = ex.Forbidden ? DateTime.Now : null;
#if ANDROID_APP
            // The phone's logins are per site: signed in somewhere else doesn't help this one.
            _loginFailure = ex.NeedsLogin;
#else
            // With the cookies already on the hint would be wrong: the login is missing in the browser itself.
            _loginFailure = ex.NeedsLogin && req.Cookies == null;
#endif
            State = JobState.Failed;
            StatusText = ex.Message + (searched ? " · " + L.T("non l'ho trovato nemmeno su YouTube") : "");
            Details = string.IsNullOrWhiteSpace(ex.Details) ? ex.Message : ex.Details.Trim();
        }
        catch (Exception ex)
        {
            State = JobState.Failed;
            StatusText = L.T("Errore imprevisto:") + " " + ex.Message;
            Details = ex.ToString();
        }
        finally
        {
            _queue.Pump();
        }
    }

    // The same song on YouTube for a song being saved whose link failed: true when found (the job goes on with it).
    private async Task<bool> FindElsewhere(CancellationToken ct)
    {
        StatusText = L.T("Il link non funziona più: cerco lo stesso brano su YouTube…");
        Indeterminate = true;
        try
        {
            var t = _queue.Host.Library.Get(AttachTo!);
            string title = t?.Title ?? Item.Title;
            string? artist = t?.Artist ?? Item.Artist;
            double duration = t?.Duration ?? Item.Duration ?? 0;
            var cookies = _queue.Host.Settings.CookiesBrowserOrNull;
            var info = await Task.Run(() => CloudSongs.FindElsewhereAsync(title, artist, duration, cookies, ct), ct);
            if (info == null) return false;
            _originalKeys = SourceKeys.ForItem(Item);
            var item = Analyzer.ItemFromInfo(info, Sites.NameFor(info.WebpageUrl));
            item.Kind = MediaKind.Audio;
            Item = item;
            _foundElsewhere = true;
            _refreshed = false;
            OnChanged(nameof(Item), nameof(Title), nameof(Subtitle), nameof(ViaYouTube), nameof(Service));
            return true;
        }
        catch { return false; }
    }

    private void WaitForSite(string? details)
    {
        State = JobState.Queued;
        Indeterminate = true;
        StatusText = L.F("In attesa: {0} sta limitando i download", Service);
        Details = details?.Trim();
    }

    // When it failed with a 403 (null: not that way).
    internal DateTime? ForbiddenAt { get; private set; }

    // It failed with a 403 just before the site turned out to be refusing everything: it waits with the others.
    internal void WaitAgain()
    {
        if (State != JobState.Failed || ++_rateLimits > 6) return;
        ForbiddenAt = null;
        WaitForSite(Details);
    }

    private bool _refreshed;

    private void Apply(JobProgress p)
    {
        if (State != JobState.Running) return;
        if (p.Phase != JobPhase.Resolving && !_refreshed)
        {
            _refreshed = true;
            OnChanged(nameof(Title));
            if (_thumb == null) { _thumbRequested = false; OnChanged(nameof(Thumb)); }
        }
        StatusText = p.Text;
        if (p.Percent is double d)
        {
            Indeterminate = false;
            Percent = Math.Clamp(d, 0, 100);
        }
        else Indeterminate = true;
    }

    public void Cancel()
    {
        if (State == JobState.Queued)
        {
            State = JobState.Canceled;
            StatusText = L.T("Annullato");
            _queue.Pump();
            return;
        }
        _cts?.Cancel();
    }

    public void Retry()
    {
        if (!CanRetry) return;
        State = JobState.Queued;
        StatusText = L.T("In coda");
        Details = null;
        Percent = 0;
        _rateLimits = 0;
        ForbiddenAt = null;
        _queue.Pump();
    }

    // Same song searched on YouTube Music; the original link stays known.
    public void RetryOnYouTube()
    {
        if (!CanRetryYouTube) return;
        _originalKeys = SourceKeys.ForItem(Item);
        Item = TrackDownloader.YouTubeFallback(Item);
        _refreshed = false;
        OnChanged(nameof(Item), nameof(Title), nameof(Subtitle), nameof(ViaYouTube));
        Retry();
    }
}

// All downloads; they survive profile switches and closing.
public sealed class DownloadQueue : Observable
{
    // One service that answered "too many requests".
    private sealed class Throttle
    {
        public DateTime PauseUntil;
        public DateTime LastLimit;
        public DateTime LastStart = DateTime.MinValue;
        public int Strikes;
    }

    private static readonly TimeSpan CalmAfter = TimeSpan.FromMinutes(10);
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, Throttle> _throttles = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastStart = DateTime.MinValue;

    public DownloadQueue(AppHost host)
    {
        Host = host;
        ClearFinishedCommand = new RelayCommand(() => RemoveWhere(j => j.IsDone), () => Jobs.Any(j => j.IsDone));
        DiscardFailedCommand = new RelayCommand(() => RemoveWhere(j => j.IsFailed), () => Jobs.Any(j => j.IsFailed));
        RetryFailedCommand = new RelayCommand(() => { foreach (var j in Jobs.Where(j => j.CanRetry).ToList()) j.Retry(); }, () => Jobs.Any(j => j.CanRetry));
        CancelAllCommand = new RelayCommand(CancelAll, () => HasActive);
        RetryFailedOnYouTubeCommand = new RelayCommand(() =>
        {
            foreach (var j in Jobs.Where(j => j.CanRetryYouTube).ToList()) j.RetryOnYouTube();
        });
        NormalSpeedCommand = new RelayCommand(() =>
        {
            _throttles.Clear();
            Pump();
        });
        Jobs.CollectionChanged += (_, _) => { if (!_adding) Notify(); };
        _tick.Tick += (_, _) => Pump();
    }

    public AppHost Host { get; }
    public ObservableCollection<DownloadJobViewModel> Jobs { get; } = new();
    public ICommand ClearFinishedCommand { get; }
    public ICommand DiscardFailedCommand { get; }
    public ICommand RetryFailedCommand { get; }
    public ICommand CancelAllCommand { get; }
    public ICommand RetryFailedOnYouTubeCommand { get; }
    public ICommand NormalSpeedCommand { get; }

    public event Action? Finished;

    public void Add(DownloadBatch batch, IEnumerable<(MediaItem Item, int Index)> items, bool video)
    {
        // A whole playlist: the counts are told once at the end (Pump), not once per song.
        _adding = true;
        try
        {
            foreach (var (item, index) in items)
                Jobs.Add(new DownloadJobViewModel(item, batch, index, video, this));
        }
        finally { _adding = false; }
        Pump();
    }

    private bool _adding;

    // ------------------------------------------------------------------ songs of the library saved on the device

    private readonly Dictionary<string, DownloadJobViewModel> _saving = new();

    // A song's save is waiting, going, done or failed (its id): the song's rows show how far it is.
    public event Action<string>? SavingChanged;

    // The save still waiting or going for this song of the library, if any.
    public DownloadJobViewModel? SavingOf(string trackId) => _saving.TryGetValue(trackId, out var j) && j.IsActive ? j : null;

    internal void OnSaving(DownloadJobViewModel job)
    {
        if (job.AttachTo is not { } id) return;
        if (job.IsActive) _saving[id] = job;
        else if (_saving.TryGetValue(id, out var j) && j == job) _saving.Remove(id);
        SavingChanged?.Invoke(id);
    }

    // Songs of the library in the cloud: their audio downloaded onto the device (the songs stay the same, in their
    // playlists, with their statistics). One already being saved isn't added twice. How many were added.
    public int Save(Profile profile, IReadOnlyList<Track> tracks)
    {
        var todo = tracks.Where(t => SavingOf(t.Id) == null).ToList();
        if (todo.Count == 0) return 0;
        var batch = new DownloadBatch(profile, null, todo.Count);
        _adding = true;
        try
        {
            for (int i = 0; i < todo.Count; i++)
            {
                var job = new DownloadJobViewModel(CloudSongs.ToMediaItem(todo[i]), batch, i, false, this, todo[i].Id);
                Jobs.Add(job);
                OnSaving(job);
            }
        }
        finally { _adding = false; }
        Pump();
        return todo.Count;
    }

    // A video for a song saved without one: from this link (the song's own, or a result chosen from a search).
    public void AddVideo(Profile profile, Track t, MediaItem video)
    {
        video.Kind = MediaKind.Video;
        Jobs.Add(new DownloadJobViewModel(video, new DownloadBatch(profile, null, 1), 0, true, this, t.Id, videoOnly: true));
        Pump();
    }

    // ------------------------------------------------------------------ pacing

    // Slowed down (one at a time, pauses between requests) until it stays calm for a while.
    public bool IsGentle(string service) => _throttles.ContainsKey(service);

    private bool IsPaused(string service) => _throttles.TryGetValue(service, out var t) && DateTime.Now < t.PauseUntil;

    public bool IsThrottled => _throttles.Count > 0;

    public string? ThrottleText
    {
        get
        {
            if (_throttles.Count == 0) return null;
            var parts = _throttles.OrderBy(t => t.Key).Select(t => DateTime.Now < t.Value.PauseUntil
                ? L.F("{0} sta limitando i download: riprendo tra {1}, più lentamente", t.Key, Text.Duration(Math.Max(1, (t.Value.PauseUntil - DateTime.Now).TotalSeconds)))
                : L.F("{0}: download rallentati per evitare nuovi blocchi", t.Key));
            return string.Join("  ·  ", parts) + ". " + L.T("Gli altri siti continuano normalmente.");
        }
    }

    // Too many requests: only that service waits, longer each time.
    internal void OnRateLimited(string service)
    {
        if (!_throttles.TryGetValue(service, out var t)) _throttles[service] = t = new Throttle();
        t.Strikes++;
        t.LastLimit = DateTime.Now;
        var wait = TimeSpan.FromSeconds(Math.Min(900, 60 * Math.Pow(2, t.Strikes - 1)));
        if (DateTime.Now + wait > t.PauseUntil) t.PauseUntil = DateTime.Now + wait;
    }

    internal void OnSucceeded(string service)
    {
        if (_throttles.TryGetValue(service, out var t)) t.Strikes = 0;
    }

    private readonly Dictionary<string, List<DateTime>> _forbidden = new(StringComparer.OrdinalIgnoreCase);

    // A song refused with "403": one closed to us, unless the same site refuses several in a row, or is already slowing
    // us down (SoundCloud answers 403 instead of "too many requests" when it has had enough). Then it's a limit like the
    // others: true, and the songs it just refused that way go back to waiting too.
    internal bool OnForbidden(string service)
    {
        if (_throttles.ContainsKey(service)) return true;
        if (!_forbidden.TryGetValue(service, out var times)) _forbidden[service] = times = new();
        var now = DateTime.Now;
        times.RemoveAll(t => now - t > TimeSpan.FromSeconds(90));
        times.Add(now);
        if (times.Count < 3) return false;
        times.Clear();
        foreach (var j in Jobs.Where(j => j.ForbiddenAt is { } at && now - at < TimeSpan.FromSeconds(120) &&
                                          j.Service.Equals(service, StringComparison.OrdinalIgnoreCase)).ToList())
            j.WaitAgain();
        return true;
    }

    public void Pump()
    {
        // Calm for a while: back to normal speed.
        foreach (var calm in _throttles.Where(t => DateTime.Now - t.Value.LastLimit > CalmAfter).Select(t => t.Key).ToList())
            _throttles.Remove(calm);
        int limit = Math.Clamp(Host.Settings.MaxParallel, 1, 6);
        int running = Jobs.Count(j => j.State == JobState.Running);
        bool waiting = false;
        foreach (var j in Jobs.Where(j => j.State == JobState.Queued).ToList())
        {
            if (running >= limit) break;
            var service = j.Service;
            if (_throttles.TryGetValue(service, out var t))
            {
                if (DateTime.Now < t.PauseUntil) { waiting = true; continue; }
                if (Jobs.Any(o => o.State == JobState.Running && o.Service.Equals(service, StringComparison.OrdinalIgnoreCase))) continue;
                if (DateTime.Now - t.LastStart < TimeSpan.FromSeconds(5)) { waiting = true; continue; }
                t.LastStart = DateTime.Now;
            }
            else if (DateTime.Now - _lastStart < TimeSpan.FromSeconds(0.7)) { waiting = true; break; }
            j.Start();
            _lastStart = DateTime.Now;
            running++;
        }
        if (IsThrottled || (waiting && Jobs.Any(j => j.State == JobState.Queued))) _tick.Start();
        else _tick.Stop();
        Notify();
        if (!HasActive) Finished?.Invoke();
        CommandManager.InvalidateRequerySuggested();
    }

    public void Remove(DownloadJobViewModel job)
    {
        if (job.IsActive) job.Cancel();
        Jobs.Remove(job);
    }

    private void RemoveWhere(Func<DownloadJobViewModel, bool> which)
    {
        _adding = true;
        try
        {
            foreach (var j in Jobs.Where(which).ToList()) Jobs.Remove(j);
        }
        finally { _adding = false; }
        Notify();
        CommandManager.InvalidateRequerySuggested();
    }

    public void CancelAll()
    {
        foreach (var j in Jobs.Where(j => j.IsActive).ToList()) j.Cancel();
    }

    // Several downloads chosen together.
    public void Retry(IEnumerable<DownloadJobViewModel> jobs)
    {
        foreach (var j in jobs.Where(j => j.CanRetry).ToList()) j.Retry();
    }

    public void RetryOnYouTube(IEnumerable<DownloadJobViewModel> jobs)
    {
        foreach (var j in jobs.Where(j => j.CanRetryYouTube).ToList()) j.RetryOnYouTube();
    }

    public void Cancel(IEnumerable<DownloadJobViewModel> jobs)
    {
        foreach (var j in jobs.Where(j => j.IsActive).ToList()) j.Cancel();
    }

    public void Remove(IEnumerable<DownloadJobViewModel> jobs)
    {
        var list = jobs.ToList();
        _adding = true;
        try
        {
            foreach (var j in list)
            {
                if (j.IsActive) j.Cancel();
                Jobs.Remove(j);
            }
        }
        finally { _adding = false; }
        Notify();
        CommandManager.InvalidateRequerySuggested();
    }

    public bool HasActive => Jobs.Any(j => j.IsActive);
    public bool HasJobs => Jobs.Count > 0;
    public bool HasDone => Jobs.Any(j => j.IsDone);
    public bool HasFailed => Jobs.Any(j => j.IsFailed);
    public bool HasFailedForYouTube => Jobs.Any(j => j.CanRetryYouTube);
    public int ActiveCount => Jobs.Count(j => j.IsActive);

    public string Summary
    {
        get
        {
            if (Jobs.Count == 0) return "";
            int running = Jobs.Count(j => j.State == JobState.Running);
            int queued = Jobs.Count(j => j.State == JobState.Queued);
            int done = Jobs.Count(j => j.IsDone);
            int failed = Jobs.Count(j => j.State == JobState.Failed);
            var parts = new List<string>();
            if (running > 0) parts.Add(L.F("{0} in corso", running));
            if (queued > 0) parts.Add(L.F("{0} in coda", queued));
            if (done > 0 || (parts.Count == 0 && failed == 0)) parts.Add(L.Count(done, "1 completato", "{0} completati"));
            if (failed > 0) parts.Add(L.Count(failed, "1 non riuscito", "{0} non riusciti"));
            return string.Join(" · ", parts);
        }
    }

    private void Notify() => OnChanged(nameof(HasActive), nameof(HasJobs), nameof(HasDone), nameof(HasFailed), nameof(ActiveCount), nameof(Summary),
        nameof(HasFailedForYouTube), nameof(IsThrottled), nameof(ThrottleText));
}

public sealed class LinkItemViewModel : Observable
{
    private readonly LinkViewModel _owner;

    public LinkItemViewModel(MediaItem item, int number, bool inLibrary, LinkViewModel owner)
    {
        Item = item;
        Number = number;
        InLibrary = inLibrary;
        _owner = owner;
    }

    public MediaItem Item { get; }
    public int Number { get; }
    public bool InLibrary { get; }
    public string Title => Item.Title;

    public string Meta
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Item.Artist)) parts.Add(Item.Artist!);
            if (Item.Duration is > 0) parts.Add(Text.Duration(Item.Duration));
            return string.Join(" · ", parts);
        }
    }

    private bool _selected = true;
    public bool IsSelected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) _owner.OnSelectionChanged(); }
    }

    private ImageSource? _thumb;
    private bool _requested;
    public ImageSource? Thumb
    {
        get
        {
            if (!_requested && (Item.Thumbnails.Count > 0 || Item.CoverUrl != null))
            {
                _requested = true;
                _ = Load();
            }
            return _thumb;
        }
    }

    private async Task Load()
    {
        var urls = Item.Thumbnails.ToList();
        if (Item.CoverUrl != null) urls.Add(Item.CoverUrl);
        _thumb = await WebImages.LoadAsync(urls, 96, Item.PageUrl);
        OnChanged(nameof(Thumb));
    }
}

// An analyzed link: what it contains and where to save it.
public sealed class LinkViewModel : Observable
{
    private readonly MainViewModel _main;
    private bool _bulk;
    private const string NoPlaylist = "\0none", NewPlaylist = "\0new";

    public AnalysisResult R { get; }
    public List<LinkItemViewModel> Items { get; }

    public LinkViewModel(AnalysisResult r, List<MediaItem> playable, MainViewModel main)
    {
        R = r;
        _main = main;
        var lib = main.Library;
        Items = playable.Select((it, i) =>
        {
            var known = lib.FindByKeys(SourceKeys.ForItem(it)) ??
                        (it.Source == SourceKind.Search ? lib.FindSimilar(it.Title, it.Artist, it.Duration) : null);
            return new LinkItemViewModel(it, i + 1, known != null, this);
        }).ToList();
        HasVideo = playable.Any(i => i.Kind == MediaKind.Video);
        _withVideo = HasVideo && !main.Host.Settings.PreferAudioOnly;
        _saveAudio = main.Host.Settings.SaveLinkAudio;

        PlaylistChoices = new List<Choice> { new(L.T("Nessuna (solo in «Tutti i brani»)"), NoPlaylist), new(L.T("Nuova playlist…"), NewPlaylist) };
        PlaylistChoices.AddRange(main.Playlists.Select(p => new Choice(p.Name, p.Id, p.IsFavorites ? "♥" : null)));
        Title = System.Text.RegularExpressions.Regex.Replace(r.Title, @"^(Album|EP|Single|Playlist)\s+-\s+", "");
        // The same playlist downloaded again (the songs added since): into the one already made from it, not a copy.
        _playlist = !IsCollection ? PlaylistChoices[0]
            : PlaylistChoices.Skip(2).FirstOrDefault(c => string.Equals(c.Label, Title, StringComparison.CurrentCultureIgnoreCase)) ?? PlaylistChoices[1];
        _newName = IsCollection ? Title : "";

        ToggleAllCommand = new RelayCommand(ToggleAll);
        DownloadCommand = new RelayCommand(Download, () => SelectedCount > 0 && (!ShowNewName || !string.IsNullOrWhiteSpace(NewName)));
        ListenCommand = new RelayCommand(Listen, () => SelectedCount > 0);
    }

    public ICommand ToggleAllCommand { get; }
    public ICommand DownloadCommand { get; }
    // Heard without saving it: kept in the cache for a while, saved (or kept in the library) from its menu. In a room of
    // Listen together: into the room's queue, the same way (everyone gets it from its link).
    public ICommand ListenCommand { get; }
    private bool InRoom => _main.InRoom;
    public string ListenLabel => InRoom ? L.T("Aggiungi alla stanza") : L.T("Ascolta senza salvare");
    public string ListenGlyph => InRoom ? "" : "";
    public string ListenTip => InRoom
        ? L.T("Va nella coda della stanza senza salvarlo: ognuno lo prende dal link, come i brani consigliati.")
        : L.T("Lo ascolti subito senza metterlo nella libreria: resta per un po' nella memoria temporanea, e se ti piace lo salvi o lo aggiungi a una playlist dal menu del brano.");

    // The audio saved on the device (the usual download), or the songs only added to the library and the playlist, in the
    // cloud: nothing downloaded now, heard from their link, saved whenever wanted. The choice is remembered.
    private bool _saveAudio;
    public bool SaveAudio
    {
        get => _saveAudio;
        set
        {
            if (!Set(ref _saveAudio, value)) return;
            _main.Host.Settings.SaveLinkAudio = value;
            _main.Host.Settings.Save();
            OnChanged(nameof(DownloadLabel), nameof(DownloadGlyph), nameof(ShowVideoChoice), nameof(SaveAudioHint));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string SaveAudioHint => SaveAudio
        ? L.T("L'audio viene scaricato e salvato sul dispositivo: lo ascolti anche senza connessione.")
        : L.T("Niente download adesso: i brani vanno subito nella libreria (e nella playlist) con la nuvola. Li ascolti dal loro link e li salvi quando vuoi.");
    // Download arrow, or the library icon (only added: the cloud is just the sign on the songs).
    public string DownloadGlyph => SaveAudio ? "" : "";
    // "With video" only means something when the songs are saved.
    public bool ShowVideoChoice => HasVideo && SaveAudio;

    public string Title { get; }
    public string SiteName => R.Site;
    public Brush SiteBrush => Ui.BrushFrom(Sites.ColorFor(R.Site));
    public bool IsCollection => R.IsCollection;
    public bool PartOfPlaylist => R.PartOfPlaylist;
    public List<string> Notes => R.Notes;
    public bool HasNotes => R.Notes.Count > 0;
    public bool HasVideo { get; }
    public int AlreadyCount => Items.Count(i => i.InLibrary);

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(R.Uploader)) parts.Add(R.Uploader!);
            if (IsCollection) parts.Add(L.Count(Items.Count, "1 brano", "{0} brani"));
            else if (Items.Count == 1 && Items[0].Item.Duration is > 0) parts.Add(Text.Duration(Items[0].Item.Duration));
            if (AlreadyCount > 0) parts.Add(AlreadyCount == Items.Count && !IsCollection ? L.T("già nella libreria") : L.F("{0} già nella libreria", AlreadyCount));
            return string.Join("  ·  ", parts);
        }
    }

    private ImageSource? _thumb;
    private bool _thumbRequested;
    public ImageSource? Thumb
    {
        get
        {
            if (!_thumbRequested)
            {
                _thumbRequested = true;
                _ = LoadThumb();
            }
            return _thumb;
        }
    }

    private async Task LoadThumb()
    {
        var urls = R.Thumbnails.Concat(Items.Take(3).SelectMany(i => i.Item.Thumbnails)).ToList();
        if (Items.FirstOrDefault()?.Item.CoverUrl is { } c && !IsCollection) urls.Insert(0, c);
        if (urls.Count == 0) return;
        _thumb = await WebImages.LoadAsync(urls, 400, R.SourceUrl);
        OnChanged(nameof(Thumb));
    }

    public List<Choice> PlaylistChoices { get; }

    private Choice _playlist;
    public Choice Playlist
    {
        get => _playlist;
        set { if (value != null && Set(ref _playlist, value)) OnChanged(nameof(ShowNewName)); }
    }

    public bool ShowNewName => Equals(_playlist.Value, NewPlaylist);

    // Tags the downloaded songs get.
    private readonly HashSet<string> _tags = new();
    public ISet<string> TagIds => _tags;
    public List<TagViewModel> ChosenTags => _main.Tags.Where(t => _tags.Contains(t.Id)).ToList();
    public bool HasChosenTags => ChosenTags.Count > 0;
    public void OnTagsChosen() => OnChanged(nameof(ChosenTags), nameof(HasChosenTags));

    private string _newName;
    public string NewName { get => _newName; set { if (Set(ref _newName, value)) CommandManager.InvalidateRequerySuggested(); } }

    private bool _withVideo;
    public bool WithVideo { get => _withVideo; set => Set(ref _withVideo, value); }

    public int SelectedCount => Items.Count(i => i.IsSelected);
    public string SelectionText => L.F("{0} di {1} selezionati", SelectedCount, Items.Count);
    public bool AllSelected => SelectedCount == Items.Count;
    public string ToggleAllLabel => L.T(AllSelected ? "Deseleziona tutto" : "Seleziona tutto");
    public string DownloadLabel => SaveAudio
        ? !IsCollection ? L.T("Salva") : L.Count(SelectedCount, "Salva 1 brano", "Salva {0} brani")
        : !IsCollection ? L.T("Aggiungi senza salvare") : L.Count(SelectedCount, "Aggiungi 1 brano", "Aggiungi {0} brani");

    internal void OnSelectionChanged()
    {
        if (_bulk) return;
        OnChanged(nameof(SelectedCount), nameof(SelectionText), nameof(AllSelected), nameof(ToggleAllLabel), nameof(DownloadLabel));
        CommandManager.InvalidateRequerySuggested();
    }

    private void ToggleAll()
    {
        bool target = !AllSelected;
        _bulk = true;
        foreach (var i in Items) i.IsSelected = target;
        _bulk = false;
        OnSelectionChanged();
    }

    private void Download()
    {
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;
        string? playlistId = _playlist.Value as string;
        if (playlistId == NoPlaylist) playlistId = null;
        else if (playlistId == NewPlaylist)
        {
            var p = _main.Profile.CreatePlaylist(NewName, R.SourceUrl);
            playlistId = p.Id;
        }
        var batch = new DownloadBatch(_main.Profile, playlistId, Items.Count, ChosenTags.Select(t => t.Id).ToList());
        var target = playlistId != null && _main.Profile.GetPlaylist(playlistId) is { } pl ? PlaylistViewModel.DisplayName(pl) : null;
        if (!SaveAudio)
        {
            AddToCloud(selected, batch, target);
            return;
        }
        _main.Host.Downloads.Add(batch, selected.Select(i => (i.Item, i.Number - 1)), HasVideo && WithVideo);
        var what = selected.Count == 1 ? L.F("«{0}» in download", selected[0].Title) : L.F("{0} brani in download", selected.Count);
        _main.Downloads.Queued(what + (target != null ? " " + L.F("nella playlist «{0}»", target) : "") + " ✓");
    }

    // The songs into the library (and the playlist, with the tags) without downloading them: in the cloud, at once. One the
    // library already has (saved or not) is that one; their covers come from the site in the background.
    private void AddToCloud(List<LinkItemViewModel> selected, DownloadBatch batch, string? target)
    {
        var lib = _main.Library;
        int added = 0;
        foreach (var it in selected)
        {
            var item = it.Item;
            var t = lib.FindByKeys(SourceKeys.ForItem(item)) ?? (item.Source == SourceKind.Search ? lib.FindSimilar(item.Title, item.Artist, item.Duration) : null);
            if (t == null)
            {
                t = CloudSongs.FromItem(item);
                lib.Add(t);
                _ = _main.FetchCover(t, CloudSongs.Pictures(item));
                added++;
            }
            batch.Place(it.Number - 1, t.Id);
        }
        var what = selected.Count == 1 ? L.F("«{0}» aggiunto senza salvarlo", selected[0].Title)
            : added == selected.Count ? L.F("{0} brani aggiunti senza salvarli", selected.Count)
            : L.F("{0} brani aggiunti senza salvarli ({1} c'erano già)", selected.Count, selected.Count - added);
        _main.Downloads.Queued(what + (target != null ? " " + L.F("nella playlist «{0}»", target) : "") + " ☁");
    }

    private void Listen()
    {
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;
        if (InRoom)
        {
            _main.Together.AddFromLink(selected.Select(i => i.Item).ToList());
            _main.Downloads.Queued(selected.Count == 1
                ? L.F("«{0}» va nella coda della stanza", selected[0].Title)
                : L.F("{0} brani vanno nella coda della stanza", selected.Count));
            return;
        }
        var ids = _main.Radio.AddFromLink(selected.Select(i => i.Item));
        _main.Player.PlayStream(ids, Title);
        _main.Downloads.Queued(selected.Count == 1
            ? L.F("«{0}» in ascolto, senza salvarlo nella libreria", selected[0].Title)
            : L.F("{0} brani in ascolto, senza salvarli nella libreria", selected.Count));
    }
}

// Download page: link analysis on top, the app-wide queue below.
public sealed class DownloadsPageViewModel : Observable
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;
    private string _lastUrl = "";

    public DownloadsPageViewModel(MainViewModel main)
    {
        _main = main;
        CancelCommand = new RelayCommand(() => _cts?.Cancel());
        DismissCommand = new RelayCommand(() => { Error = null; Link = null; CanTryYouTube = false; });
        WholePlaylistCommand = new RelayCommand(() => _ = AnalyzeAsync(_lastUrl, true));
        TryYouTubeCommand = new RelayCommand(() => _ = AnalyzeOnYouTubeAsync(_lastUrl));
        // (shown when the link needs a login: the settings open on the browser's cookies)
        OpenSettingsCommand = new RelayCommand(() => _main.SearchSettings("cookie"));
        Queue.PropertyChanged += OnQueueChanged;
    }

    private void OnQueueChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadQueue.HasJobs)) OnChanged(nameof(ShowIntro));
    }

    public void Detach()
    {
        Queue.PropertyChanged -= OnQueueChanged;
        _cts?.Cancel();
    }

    public MainViewModel Main => _main;
    public DownloadQueue Queue => _main.Host.Downloads;
    // The link read last (the phone signs in to its site, then reads it again).
    public string LastUrl => _lastUrl;
    public ICommand CancelCommand { get; }
    public ICommand DismissCommand { get; }
    public ICommand WholePlaylistCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    // The link's site won't give it (DRM, removed, private): the same song searched on YouTube Music.
    public ICommand TryYouTubeCommand { get; }

    private bool _canTryYouTube;
    public bool CanTryYouTube { get => _canTryYouTube; private set => Set(ref _canTryYouTube, value); }

    private LinkViewModel? _link;
    public LinkViewModel? Link { get => _link; private set { if (Set(ref _link, value)) OnChanged(nameof(ShowIntro)); } }

    private bool _analyzing;
    public bool IsAnalyzing { get => _analyzing; private set { if (Set(ref _analyzing, value)) OnChanged(nameof(ShowIntro)); } }

    private string _analyzingText = "";
    public string AnalyzingText { get => _analyzingText; private set => Set(ref _analyzingText, value); }

    private string? _error;
    public string? Error { get => _error; private set { if (Set(ref _error, value)) OnChanged(nameof(ShowIntro)); } }

    private string? _errorHint;
    public string? ErrorHint { get => _errorHint; private set => Set(ref _errorHint, value); }

    private bool _suggestCookies;
    public bool SuggestCookies { get => _suggestCookies; private set => Set(ref _suggestCookies, value); }

    public bool ShowIntro => Link == null && !IsAnalyzing && Error == null && !Queue.HasJobs;

    private string? _feedback;
    public string? Feedback { get => _feedback; private set => Set(ref _feedback, value); }

    // The link is queued: the card goes away so the queue is visible.
    public void Queued(string message)
    {
        Link = null;
        Feedback = message;
        OnChanged(nameof(ShowIntro));
        _ = ClearFeedback(message);
    }

    private async Task ClearFeedback(string text)
    {
        await Task.Delay(6000);
        if (Feedback == text) Feedback = null;
    }

    public List<string> FeaturedSites { get; } = Sites.All.Where(s => s.Featured).Select(s => s.Name).ToList();

    public async Task AnalyzeAsync(string url, bool wholePlaylist = false)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _lastUrl = url;
        IsAnalyzing = true;
        AnalyzingText = L.T(wholePlaylist ? "Lettura della playlist…" : "Analisi del link…");
        Error = null;
        CanTryYouTube = false;
        Link = null;
        var s = _main.Host.Settings;
        try
        {
            if (Engines.Missing().Count > 0)
            {
                AnalyzingText = L.T("Preparazione dei motori di download…");
                await _main.Host.EnsureEnginesAsync();
            }
            var req = new AnalyzeRequest(url, s.CookiesBrowserOrNull, wholePlaylist);
            var r = await Task.Run(() => Analyzer.AnalyzeAsync(req, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            var playable = r.Items.Where(i => i.Kind is MediaKind.Audio or MediaKind.Video).ToList();
            if (playable.Count == 0) throw new EngineException(L.T("In questo link non c'è niente da ascoltare (solo immagini)."));
            Link = new LinkViewModel(r, playable, _main);
        }
        catch (OperationCanceledException) { }
        catch (EngineException ex) when (!cts.IsCancellationRequested)
        {
#if ANDROID_APP
            // The phone has no browser to lend its cookies: the logins made inside the app (Settings › Download).
            bool logins = s.CookiesBrowserOrNull != null;
            SuggestCookies = ex.NeedsLogin && !logins;
            ErrorHint = ex.RateLimited
                ? L.T("Aspetta qualche minuto e riprova: il blocco si toglie da solo. Scaricare meno brani insieme aiuta.")
                : ex.NeedsLogin
                    ? L.T(logins
                        ? "L'app usa già i tuoi accessi: controlla di aver fatto l'accesso a questo sito. Se è il controllo anti-bot di YouTube, riprova più tardi o con un'altra connessione."
                        : "Per i contenuti privati, per abbonati o con limite d'età accedi al sito dall'app: i download useranno il tuo account.")
                    : null;
#else
            SuggestCookies = ex.NeedsLogin && !s.UseCookies;
            ErrorHint = ex.RateLimited
                ? L.T("Aspetta qualche minuto e riprova: il blocco si toglie da solo. Scaricare meno brani insieme aiuta.")
                : ex.NeedsLogin
                    ? L.T(s.UseCookies
                        ? "I cookie del browser sono attivi: controlla di aver fatto l'accesso al sito in quel browser."
                        : "Attiva «Usa i cookie del browser» nelle impostazioni: il programma userà l'accesso che hai già fatto nel browser.")
                    : null;
#endif
            Error = ex.Message;
            // Protected (DRM), removed, private: like a download that failed, it can be searched on YouTube Music.
            CanTryYouTube = (ex.Gone || ex.NeedsLogin) && Sites.NameFor(url) is not ("YouTube" or "YouTube Music");
        }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            SuggestCookies = false;
            ErrorHint = null;
            Error = L.T("Errore imprevisto:") + " " + ex.Message;
        }
        finally
        {
            if (_cts == cts) IsAnalyzing = false;
        }
    }

    // The link that failed, as the same song on YouTube Music: its card like any other (playlist, save or not).
    private async Task AnalyzeOnYouTubeAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsAnalyzing = true;
        AnalyzingText = L.T("Cerco titolo e artista del brano…");
        Error = null;
        ErrorHint = null;
        SuggestCookies = false;
        CanTryYouTube = false;
        Link = null;
        try
        {
            var r = await Task.Run(() => Analyzer.OnYouTubeAsync(url, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            Link = new LinkViewModel(r, r.Items, _main);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            Error = ex is EngineException ? ex.Message : L.T("Errore imprevisto:") + " " + ex.Message;
        }
        finally
        {
            if (_cts == cts) IsAnalyzing = false;
        }
    }
}
