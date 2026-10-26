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

// Where one request's songs go (the playlists, each in the original order) and the tags they get.
public sealed class DownloadBatch
{
    public DownloadBatch(Profile profile, IReadOnlyList<string>? playlistIds, int size, IReadOnlyList<string>? tagIds = null)
    {
        Profile = profile;
        PlaylistIds = playlistIds ?? Array.Empty<string>();
        Slots = new string?[size];
        TagIds = tagIds ?? Array.Empty<string>();
    }

    public string Id { get; } = Ids.New();
    public Profile Profile { get; }
    public IReadOnlyList<string> PlaylistIds { get; }
    public string?[] Slots { get; }
    public IReadOnlyList<string> TagIds { get; }
    // Songs of an imported pack: they go into their playlists and tags (index, library id).
    public Action<int, string>? Placed { get; init; }
    // What the small panel calls it when it has several songs (a playlist's name), and the playlist made for it (a canceled
    // download takes it away again).
    public string? Title { get; init; }
    public string? NewPlaylist { get; init; }
    public bool Canceled { get; set; }

    public void Place(int index, string trackId)
    {
        if (Canceled) return;
        Slots[index] = trackId;
        // A tag deleted meanwhile is skipped.
        foreach (var tag in TagIds)
            if (Profile.GetTag(tag) != null) Profile.SetTag(new[] { trackId }, tag, true);
        Placed?.Invoke(index, trackId);
        // (a playlist deleted meanwhile is skipped)
        foreach (var id in PlaylistIds)
        {
            if (Profile.GetPlaylist(id) is not { } pl || Profile.Contains(pl, trackId)) continue;
            int? at = null;
            for (int j = index - 1; j >= 0 && at == null; j--)
                if (Slots[j] is { } prev && pl.Tracks.IndexOf(prev) is int p and >= 0) at = p + 1;
            for (int j = index + 1; j < Slots.Length && at == null; j++)
                if (Slots[j] is { } next && pl.Tracks.IndexOf(next) is int p and >= 0) at = p;
            Profile.AddTrack(pl, trackId, at);
        }
    }
}

public enum JobState { Queued, Running, Done, Present, Failed, Canceled }

public sealed class DownloadJobViewModel : Observable
{
    private readonly DownloadQueue _queue;
    private CancellationTokenSource? _cts;
    private int _rateLimits;

    // attachTo: a library song this is for (one in the cloud saved on the device, or its video: videoOnly; replace: its files
    // downloaded again). audio / videoRes: the quality chosen for it (null: the song's own, or the one in Settings; videoRes 0 =
    // the best).
    public DownloadJobViewModel(MediaItem item, DownloadBatch batch, int index, bool wantVideo, DownloadQueue queue, string? attachTo = null, bool videoOnly = false,
        string? audio = null, int? videoRes = null, bool replace = false)
    {
        Item = item;
        Batch = batch;
        Index = index;
        WantVideo = wantVideo;
        AttachTo = attachTo;
        VideoOnly = videoOnly;
        AudioQuality = audio;
        VideoRes = videoRes;
        Replace = replace;
        _queue = queue;
        CancelCommand = new RelayCommand(Cancel);
        RetryCommand = new RelayCommand(Retry);
        RetryYouTubeCommand = new RelayCommand(RetryOnYouTube);
        RemoveCommand = new RelayCommand(() => _queue.Remove(this));
        PlayCommand = new RelayCommand(Play);
        LoginCommand = new RelayCommand(() => _queue.Host.Session?.SearchSettings("cookie"));
        StatusText = L.T("Queued");
    }

    public MediaItem Item { get; private set; }
    // Rate limits apply per service ("YouTube", "SoundCloud"...).
    public string Service => Sites.ServiceOf(Item);
    public DownloadBatch Batch { get; }
    public int Index { get; }
    public bool WantVideo { get; }
    public string? AttachTo { get; }
    public bool VideoOnly { get; }
    public string? AudioQuality { get; }
    public int? VideoRes { get; }
    // "Download again": the song's files replaced by new ones (in another quality).
    public bool Replace { get; }
    // Saving a song of the library that's in the cloud (its audio onto the device).
    public bool IsSave => AttachTo != null && !VideoOnly && !Replace;
    public Track? Result { get; private set; }
    public TrackOutcome? Outcome { get; private set; }
    // It brought something into the library (a new song, or the audio of one in the cloud): what canceling its group takes back.
    public bool Brought => Outcome is TrackOutcome.Downloaded or TrackOutcome.Saved;
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
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return L.T("Untitled song");
        var last = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrEmpty(last) || last is "watch" or "track" or "tracks") return L.T("Untitled song");
        return Uri.UnescapeDataString(last).Replace('-', ' ').Replace('_', ' ').Trim();
    }
    public string Subtitle => string.Join(" · ", new[] { Result?.Artist ?? Item.Artist, Item.SiteName,
            VideoOnly ? L.T("video only") : Replace ? L.T("downloaded again") : IsSave ? L.T("saving to device") : WantVideo ? L.T("with video") : null,
            ViaYouTube ? L.T("searched on YouTube") : null }
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

    internal void Restyle() => OnChanged(nameof(StatusBrush));

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
        StatusText = L.T("Starting…");
        Indeterminate = true;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var s = _queue.Host.Settings;
        // The quality chosen for this download, or the song's own, or the one in Settings.
        var song = AttachTo != null ? _queue.Host.Library.Get(AttachTo) : null;
        var audio = AudioQuality ?? Quality.For(song, s.AudioFormat);
        var maxRes = VideoRes is int r ? (r > 0 ? r : null) : Quality.VideoFor(song, s.VideoMaxRes);
        var req = new TrackRequest(Item, WantVideo, maxRes, audio, s.MusicDir, s.CookiesBrowserOrNull, _originalKeys, AttachTo, VideoOnly, Replace);
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
            Outcome = res.Outcome;
            // Its group was canceled while it was finishing: what it brought goes away like the others'.
            if (Batch.Canceled)
            {
                State = JobState.Canceled;
                StatusText = L.T("Canceled");
                _ = _queue.TakeBackAsync(new[] { this });
                return;
            }
            Batch.Place(Index, res.Track.Id);
            // A quality chosen on the card stays the song's own, for its next downloads (and for hearing it from the cloud).
            if (!Replace && (AudioQuality != null || VideoRes != null) && _queue.Host.Library.Get(res.Track.Id) is { } got)
            {
                if (AudioQuality != null) got.AudioQuality = AudioQuality;
                if (VideoRes != null && WantVideo) got.VideoRes = VideoRes;
                _queue.Host.Library.Changed(got);
            }
            Percent = 100;
            State = res.Outcome == TrackOutcome.AlreadyPresent ? JobState.Present : JobState.Done;
            StatusText = res.Outcome switch
            {
                TrackOutcome.AlreadyPresent when VideoOnly => L.T("The song already has a video"),
                TrackOutcome.AlreadyPresent when IsSave => L.T("It was already saved on your device"),
                TrackOutcome.AlreadyPresent => L.T("Already in your library: not downloaded again"),
                TrackOutcome.VideoAdded => VideoOnly ? L.T("Video added to the song") : L.T("Video added to the existing song"),
                TrackOutcome.Replaced => L.T("Downloaded again"),
                TrackOutcome.Saved => L.T("Saved on your device") + (_foundElsewhere ? " · " + L.F("from {0}: the previous link no longer worked", Sites.NameFor(res.Track.SourceUrl ?? "")) : ""),
                _ => L.T("Completed") + (res.Note != null ? " · " + res.Note : ""),
            };
            if (_foundElsewhere && _queue.Host.Session is { } main)
                main.Toast(L.F("“{0}” was gone from its link: saved from {1}", res.Track.Title, Sites.NameFor(res.Track.SourceUrl ?? "")));
            _thumbRequested = false;
            OnChanged(nameof(Title), nameof(Subtitle), nameof(Thumb));
            _queue.OnSucceeded(service);
        }
        catch (OperationCanceledException)
        {
            State = JobState.Canceled;
            StatusText = L.T("Canceled");
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
                    StatusText = L.T("Canceled");
                    return;
                }
                if (ex.Gone && _queue.Host.Library.Get(AttachTo!) is { Unavailable: false } gone)
                {
                    gone.Unavailable = true;
                    _queue.Host.Library.Changed(gone);
                }
            }
            ForbiddenAt = ex.Forbidden ? DateTime.Now : null;
#if ANDROID_APP || IOS_APP
            // The phone's logins are per site: signed in somewhere else doesn't help this one.
            _loginFailure = ex.NeedsLogin;
#else
            // With the cookies already on the hint would be wrong: the login is missing in the browser itself.
            _loginFailure = ex.NeedsLogin && req.Cookies == null;
#endif
            State = JobState.Failed;
            StatusText = ex.Message + (searched ? " · " + L.T("couldn't find it on YouTube either") : "");
            Details = string.IsNullOrWhiteSpace(ex.Details) ? ex.Message : ex.Details.Trim();
            // Protected by DRM: the song exists, only its site won't give it, so it's taken from YouTube Music at once.
            if (ex.Drm && !searched && CanRetryYouTube) RetryOnYouTube();
        }
        catch (Exception ex)
        {
            State = JobState.Failed;
            StatusText = L.T("Unexpected error:") + " " + ex.Message;
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
        StatusText = L.T("The link no longer works: looking for the same song on YouTube…");
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
        StatusText = L.F("Waiting: {0} is limiting downloads", Service);
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
            StatusText = L.T("Canceled");
            _queue.Pump();
            return;
        }
        _cts?.Cancel();
    }

    public void Retry()
    {
        if (!CanRetry) return;
        State = JobState.Queued;
        StatusText = L.T("Queued");
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

// The downloads asked for together (one song, or the songs of a link), as the small panel shows them on any page while they
// go: how far they are and a button that cancels them all.
public sealed class DownloadGroupViewModel : Observable
{
    private readonly DownloadQueue _queue;

    public DownloadGroupViewModel(DownloadBatch batch, DownloadQueue queue)
    {
        Batch = batch;
        _queue = queue;
        CancelCommand = new RelayCommand(() => _ = queue.CancelGroupAsync(this));
        OpenCommand = new RelayCommand(() => { if (queue.Host.Session is { } main) main.Navigate(main.Downloads); });
    }

    public DownloadBatch Batch { get; }
    public ICommand CancelCommand { get; }
    // The download page, with every song of it.
    public ICommand OpenCommand { get; }

    private string _title = "", _detail = "";
    private double _percent;
    private bool _indeterminate;
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public double Percent { get => _percent; private set => Set(ref _percent, value); }
    public bool Indeterminate { get => _indeterminate; private set => Set(ref _indeterminate, value); }
    // Songs it already brought (canceling it asks first: they go away too).
    public int Brought { get; private set; }

    internal void Refresh(List<DownloadJobViewModel> jobs)
    {
        Brought = jobs.Count(j => j.Brought);
        if (jobs.Count == 1)
        {
            var j = jobs[0];
            Title = j.Title;
            Detail = j.StatusText;
            Percent = j.IsFinished ? 100 : j.Percent;
            Indeterminate = j.State == JobState.Queued || j.Indeterminate;
            return;
        }
        int finished = jobs.Count(j => j.IsFinished), failed = jobs.Count(j => j.State == JobState.Failed);
        Title = Batch.Title ?? L.Count(jobs.Count, "1 song", "{0} songs");
        Detail = L.F("{0} of {1}", finished, jobs.Count) + (failed > 0 ? " · " + L.Count(failed, "1 failed", "{0} failed") : "");
        Percent = jobs.Sum(j => j.IsFinished ? 100 : j.Percent) / jobs.Count;
        Indeterminate = false;
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
        // (the status colours are brushes of the light or dark look)
        Themes.Changed += () => { foreach (var j in Jobs) j.Restyle(); };
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
        _groupTick.Tick += (_, _) => RefreshGroups();
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

    // audio / videoRes: the quality chosen on the card (null: the one in Settings).
    public void Add(DownloadBatch batch, IEnumerable<(MediaItem Item, int Index)> items, bool video, string? audio = null, int? videoRes = null)
    {
        // A whole playlist: the counts are told once at the end (Pump), not once per song.
        _adding = true;
        try
        {
            foreach (var (item, index) in items)
                Jobs.Add(new DownloadJobViewModel(item, batch, index, video, this, audio: audio, videoRes: videoRes));
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

    // "Download again": songs of the library (saved, or in the cloud) get new files from their link, in the quality now
    // chosen (already set on them), in place of the ones they have. One already being saved or downloaded again isn't added.
    public int Redownload(Profile profile, IReadOnlyList<Track> tracks, bool video)
    {
        var todo = tracks.Where(t => t.HasLink && SavingOf(t.Id) == null && !Jobs.Any(j => j.IsActive && j.AttachTo == t.Id)).ToList();
        if (todo.Count == 0) return 0;
        var batch = new DownloadBatch(profile, null, todo.Count);
        _adding = true;
        try
        {
            for (int i = 0; i < todo.Count; i++)
                Jobs.Add(new DownloadJobViewModel(CloudSongs.ToMediaItem(todo[i]), batch, i, video, this, todo[i].Id, replace: true));
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
                ? L.F("{0} is limiting downloads: resuming in {1}, more slowly", t.Key, Text.Duration(Math.Max(1, (t.Value.PauseUntil - DateTime.Now).TotalSeconds)))
                : L.F("{0}: downloads slowed down to avoid new blocks", t.Key));
            return string.Join("  ·  ", parts) + ". " + L.T("Other sites keep going normally.");
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
        // (a group being canceled: its songs are leaving, none starts meanwhile)
        foreach (var j in Jobs.Where(j => j.State == JobState.Queued && !j.Batch.Canceled).ToList())
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
            if (running > 0) parts.Add(L.F("{0} in progress", running));
            if (queued > 0) parts.Add(L.F("{0} queued", queued));
            if (done > 0 || (parts.Count == 0 && failed == 0)) parts.Add(L.Count(done, "1 completed", "{0} completed"));
            if (failed > 0) parts.Add(L.Count(failed, "1 failed", "{0} failed"));
            return string.Join(" · ", parts);
        }
    }

    private void Notify()
    {
        OnChanged(nameof(HasActive), nameof(HasJobs), nameof(HasDone), nameof(HasFailed), nameof(ActiveCount), nameof(Summary),
            nameof(HasFailedForYouTube), nameof(IsThrottled), nameof(ThrottleText));
        RefreshGroups();
    }

    // ------------------------------------------------------------------ groups (the small panel, the phone's notification)

    // (the bars move twice a second: following every line of yt-dlp would redraw the panel for nothing)
    private readonly DispatcherTimer _groupTick = new() { Interval = TimeSpan.FromMilliseconds(500) };

    // The groups with something still to download, the oldest first.
    public ObservableCollection<DownloadGroupViewModel> Groups { get; } = new();
    public bool HasGroups => Groups.Count > 0;
    public event Action? GroupsRefreshed;

    private void RefreshGroups()
    {
        var byBatch = new Dictionary<DownloadBatch, List<DownloadJobViewModel>>();
        foreach (var j in Jobs)
        {
            if (!byBatch.TryGetValue(j.Batch, out var list)) byBatch[j.Batch] = list = new();
            list.Add(j);
        }
        bool had = HasGroups;
        foreach (var g in Groups.Where(g => !byBatch.TryGetValue(g.Batch, out var l) || !l.Any(j => j.IsActive)).ToList()) Groups.Remove(g);
        foreach (var (batch, list) in byBatch)
            if (list.Any(j => j.IsActive) && !Groups.Any(g => g.Batch == batch)) Groups.Add(new DownloadGroupViewModel(batch, this));
        foreach (var g in Groups) g.Refresh(byBatch[g.Batch]);
        if (HasGroups) _groupTick.Start();
        else _groupTick.Stop();
        if (had != HasGroups) OnChanged(nameof(HasGroups));
        GroupsRefreshed?.Invoke();
    }

    // The phone's notification: its Cancel asks in the app when the group has songs to take away, otherwise it cancels at once
    // (a song done in the second before goes too).
    public void CancelGroup(string id, bool ask = false)
    {
        if (Groups.FirstOrDefault(g => g.Batch.Id == id) is { } g) _ = CancelGroupAsync(g, ask);
    }

    // A group canceled: what's still to download stops, and the songs it already brought go away again, as if it had never
    // started (the new ones out of the library, the ones saved from the cloud back in the cloud; the ones that were already
    // there stay). The playlist made for it goes too. Asked first when there are songs to take away.
    public async Task CancelGroupAsync(DownloadGroupViewModel group, bool ask = true)
    {
        var batch = group.Batch;
        if (batch.Canceled) return;
        int done = Jobs.Count(j => j.Batch == batch && j.Brought);
        if (ask && done > 0 && !await Dialogs.ConfirmAsync(L.F("Stop downloading “{0}”?", group.Title),
                L.Count(done, "The song already downloaded will be removed too.", "The {0} songs already downloaded will be removed too."),
                L.T("Stop and remove"), true))
            return;
        if (batch.Canceled) return;
        batch.Canceled = true;
        // Taken after the question: the songs that finished while it was open go too.
        var jobs = Jobs.Where(j => j.Batch == batch).ToList();
        var brought = jobs.Where(j => j.Brought).ToList();
        Remove(jobs);
        if (brought.Count > 0) await TakeBackAsync(brought);
        // Even with the songs that were already in the library in it: it was made for this download.
        if (batch.NewPlaylist is { } id && batch.Profile.GetPlaylist(id) is { } pl) batch.Profile.DeletePlaylist(pl);
        if (brought.Count == 0) Host.Session?.Toast(L.F("“{0}” canceled", group.Title));
    }

    internal async Task TakeBackAsync(IReadOnlyList<DownloadJobViewModel> jobs)
    {
        if (Host.Session is not { } main) return;
        List<TrackViewModel> Songs(TrackOutcome o) => jobs.Where(j => j.Outcome == o && j.Result != null).Select(j => Host.Library.Get(j.Result!.Id))
            .OfType<Track>().Distinct().Select(main.Vm).ToList();
        var added = Songs(TrackOutcome.Downloaded);
        var saved = Songs(TrackOutcome.Saved);
        if (added.Count > 0) await main.RemoveNow(added, RemoveKind.Everything);
        if (saved.Count > 0) await main.RemoveFromDevice(saved);
    }
}

public sealed class LinkItemViewModel : Observable
{
    private readonly LinkViewModel _owner;

    public LinkItemViewModel(MediaItem item, int number, bool inLibrary, LinkViewModel owner)
    {
        Item = item;
        Number = number;
        _inLibrary = inLibrary;
        _owner = owner;
    }

    public MediaItem Item { get; }
    public int Number { get; }
    private bool _inLibrary;
    public bool InLibrary { get => _inLibrary; internal set => Set(ref _inLibrary, value); }
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
        HasVideo = Engines.VideoDownloads && playable.Any(i => i.Kind == MediaKind.Video);
        _withVideo = HasVideo && !main.Host.Settings.PreferAudioOnly;
        _saveAudio = main.Host.Settings.SaveLinkAudio;
        _audio = QualityChoices.Pick(AudioChoices, main.Host.Settings.AudioFormat);
        _video = QualityChoices.Pick(VideoChoices, main.Host.Settings.VideoMaxRes);

        PlaylistChoices = new List<Choice> { new(L.T("None (only in “All songs”)"), NoPlaylist), new(L.T("New playlist…"), NewPlaylist) };
        // (this device's, also while only the other device's music is shown: the songs are downloaded here)
        PlaylistChoices.AddRange(main.MenuPlaylists().Where(p => !p.IsOther).Select(p => new Choice(p.Name, p.Id, p.IsFavorites ? "♥" : null)));
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
    public string ListenLabel => InRoom ? L.T("Add to the room") : L.T("Listen without saving");
    public string ListenGlyph => InRoom ? "" : "";
    public string ListenTip => InRoom
        ? L.T("It goes into the room's queue without being saved: everyone gets it from the link, like suggested songs.")
        : L.T("Listen right away without adding it to your library: it stays in temporary storage for a while, and if you like it you can save it or add it to a playlist from the song's menu.");

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
            OnChanged(nameof(DownloadLabel), nameof(DownloadGlyph), nameof(ShowVideoChoice), nameof(ShowVideoQuality), nameof(SaveAudioHint));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string SaveAudioHint => SaveAudio
        ? L.T("The audio is downloaded and saved on your device: you can listen even without a connection.")
        : L.T("No download for now: the songs go straight into your library (and the playlist) with the cloud icon. You listen from their link and save them whenever you like.");
    // Download arrow, or the library icon (only added: the cloud is just the sign on the songs).
    public string DownloadGlyph => SaveAudio ? "" : "";
    // "With video" only means something when the songs are saved.
    public bool ShowVideoChoice => HasVideo && SaveAudio;
    public bool ShowVideoQuality => ShowVideoChoice && WithVideo;

    // The quality of these songs (the one in Settings to start with): one chosen here stays theirs, for their next downloads
    // and, in the cloud, for hearing them.
    public List<Choice> AudioChoices { get; } = QualityChoices.Audio();
    public List<Choice> VideoChoices { get; } = QualityChoices.Video();
    private Choice _audio, _video;
    public Choice Audio { get => _audio; set { if (value != null) Set(ref _audio, value); } }
    public Choice Video { get => _video; set { if (value != null) Set(ref _video, value); } }

    // What was chosen when it isn't the one in Settings (null: Settings decide).
    private string? ChosenAudio => Equals(_audio.Value, _main.Host.Settings.AudioFormat) ? null : _audio.Value as string;
    private int? ChosenVideo => Equals(_video.Value, _main.Host.Settings.VideoMaxRes) ? null : _video.Value as int? ?? 0;

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
            if (IsCollection) parts.Add(L.Count(Items.Count, "1 song", "{0} songs"));
            else if (Items.Count == 1 && Items[0].Item.Duration is > 0) parts.Add(Text.Duration(Items[0].Item.Duration));
            if (AlreadyCount > 0) parts.Add(AlreadyCount == Items.Count && !IsCollection ? L.T("already in your library") : L.F("{0} already in your library", AlreadyCount));
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

    // More playlists the songs go into as well (Favorites, say), ticked in a menu like the tags. Every card starts without.
    private readonly HashSet<string> _also = new();
    public ISet<string> AlsoIds => _also;
    public IEnumerable<Choice> AlsoChoices => PlaylistChoices.Skip(2);
    public string AlsoText => string.Join(", ", AlsoChoices.Where(c => _also.Contains((string)c.Value!)).Select(c => c.Label));
    public bool HasAlso => _also.Count > 0;
    public void OnAlsoChosen() => OnChanged(nameof(AlsoText), nameof(HasAlso));

    // Tags the downloaded songs get.
    private readonly HashSet<string> _tags = new();
    public ISet<string> TagIds => _tags;
    public List<TagViewModel> ChosenTags => _main.Tags.Where(t => _tags.Contains(t.Id)).ToList();
    public bool HasChosenTags => ChosenTags.Count > 0;
    public void OnTagsChosen() => OnChanged(nameof(ChosenTags), nameof(HasChosenTags));

    private string _newName;
    public string NewName { get => _newName; set { if (Set(ref _newName, value)) CommandManager.InvalidateRequerySuggested(); } }

    private bool _withVideo;
    public bool WithVideo { get => _withVideo; set { if (Set(ref _withVideo, value)) OnChanged(nameof(ShowVideoQuality)); } }

    public int SelectedCount => Items.Count(i => i.IsSelected);
    public string SelectionText => L.F("{0} of {1} selected", SelectedCount, Items.Count);
    public bool AllSelected => SelectedCount == Items.Count;
    public string ToggleAllLabel => L.T(AllSelected ? "Deselect all" : "Select all");
    public string DownloadLabel => SaveAudio
        ? !IsCollection ? L.T("Save") : L.Count(SelectedCount, "Save 1 song", "Save {0} songs")
        : !IsCollection ? L.T("Add without saving") : L.Count(SelectedCount, "Add 1 song", "Add {0} songs");

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
        string? playlistId = _playlist.Value as string, created = null;
        if (playlistId == NoPlaylist) playlistId = null;
        else if (playlistId == NewPlaylist)
        {
            // (a list of songs has no link of its own)
            var p = _main.Profile.CreatePlaylist(NewName, R.SourceUrl.Length > 0 ? R.SourceUrl : null);
            playlistId = created = p.Id;
        }
        var ids = new List<string>();
        if (playlistId != null) ids.Add(playlistId);
        ids.AddRange(_also.Where(id => !ids.Contains(id)));
        var batch = new DownloadBatch(_main.Profile, ids, Items.Count, ChosenTags.Select(t => t.Id).ToList())
        {
            Title = selected.Count == 1 ? null : Title,
            NewPlaylist = created,
        };
        var names = ids.Select(_main.Profile.GetPlaylist).OfType<Playlist>().Select(PlaylistViewModel.DisplayName).ToList();
        var target = names.Count switch { 0 => "", 1 => " " + L.F("in the playlist “{0}”", names[0]), _ => " " + L.F("in {0} playlists", names.Count) };
        if (!SaveAudio)
        {
            AddToCloud(selected, batch, target);
            return;
        }
        bool video = HasVideo && WithVideo;
        _main.Host.Downloads.Add(batch, selected.Select(i => (i.Item, i.Number - 1)), video, ChosenAudio, video ? ChosenVideo : null);
        var what = selected.Count == 1 ? L.F("Downloading “{0}”", selected[0].Title) : L.F("Downloading {0} songs", selected.Count);
        _main.Downloads.Queued(what + target + " ✓");
    }

    // The songs into the library (and the playlists, with the tags) without downloading them: in the cloud, at once. One the
    // library already has (saved or not) is that one; their covers come from the site in the background.
    private void AddToCloud(List<LinkItemViewModel> selected, DownloadBatch batch, string target)
    {
        var lib = _main.Library;
        int added = 0, noLink = 0;
        foreach (var it in selected)
        {
            var item = it.Item;
            var t = lib.FindByKeys(SourceKeys.ForItem(item)) ?? (item.Source == SourceKind.Search ? lib.FindSimilar(item.Title, item.Artist, item.Duration) : null);
            // A song of a list found nowhere by its name has no link to be heard from: only saving it searches for it.
            if (t == null && string.IsNullOrEmpty(item.PageUrl ?? item.Url))
            {
                noLink++;
                continue;
            }
            if (t == null)
            {
                t = CloudSongs.FromItem(item);
                lib.Add(t);
                _ = _main.FetchCover(t, CloudSongs.Pictures(item));
                added++;
            }
            // (the quality chosen here is the one it's heard with, and saved with later)
            if (ChosenAudio is { } q && !t.IsSaved && t.AudioQuality != q)
            {
                t.AudioQuality = q;
                lib.Changed(t);
            }
            batch.Place(it.Number - 1, t.Id);
            it.InLibrary = true;
        }
        OnChanged(nameof(Subtitle), nameof(AlreadyCount));
        int n = selected.Count - noLink;
        var what = selected.Count == 1 ? L.F("“{0}” added without saving it", selected[0].Title)
            : added == n ? L.F("{0} songs added without saving them", n)
            : L.F("{0} songs added without saving them ({1} were already there)", n, n - added);
        if (noLink > 0)
        {
            var missing = L.Count(noLink, "1 song has no link: save it to have it", "{0} songs have no link: save them to have them");
            what = n == 0 ? missing : what + " · " + missing;
        }
        // The card stays: the songs can be heard right away (Listen) or saved later.
        _main.Downloads.Added(what + target + " ☁");
    }

    private void Listen()
    {
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;
        if (InRoom)
        {
            _main.Together.AddFromLink(selected.Select(i => i.Item).ToList());
            _main.Downloads.Queued(selected.Count == 1
                ? L.F("“{0}” goes into the room's queue", selected[0].Title)
                : L.F("{0} songs go into the room's queue", selected.Count));
            return;
        }
        var ids = _main.Radio.AddFromLink(selected.Select(i => i.Item));
        _main.Player.PlayStream(ids, Title);
        _main.Downloads.Queued(selected.Count == 1
            ? L.F("Playing “{0}” without saving it to your library", selected[0].Title)
            : L.F("Playing {0} songs without saving them to your library", selected.Count));
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
        ImportListCommand = new RelayCommand(() => _ = PickListAsync());
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
    // A file with a list of songs (a playlist exported from Apple Music, iTunes, Exportify...).
    public ICommand ImportListCommand { get; }

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

    // Added to the library without downloading: the card stays, to hear the songs at once.
    public void Added(string message)
    {
        Feedback = message;
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
        AnalyzingText = L.T(wholePlaylist ? "Reading playlist…" : "Analyzing link…");
        Error = null;
        CanTryYouTube = false;
        Link = null;
        var s = _main.Host.Settings;
        try
        {
            if (Engines.Missing().Count > 0)
            {
                AnalyzingText = L.T("Preparing download engines…");
                await _main.Host.EnsureEnginesAsync();
            }
            var req = new AnalyzeRequest(url, s.CookiesBrowserOrNull, wholePlaylist);
            var r = await Task.Run(() => Analyzer.AnalyzeAsync(req, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            var playable = r.Items.Where(i => i.Kind is MediaKind.Audio or MediaKind.Video).ToList();
            if (playable.Count == 0) throw new EngineException(L.T("There's nothing to listen to in this link (images only)."));
            Link = new LinkViewModel(r, playable, _main);
        }
        catch (OperationCanceledException) { }
        catch (EngineException ex) when (!cts.IsCancellationRequested)
        {
            // Protected by DRM: no error, the card of the same song on YouTube Music at once.
            if (ex.Drm && Sites.NameFor(url) is not ("YouTube" or "YouTube Music"))
            {
                _ = AnalyzeOnYouTubeAsync(url);
                return;
            }
#if ANDROID_APP || IOS_APP
            // The phone has no browser to lend its cookies: the logins made inside the app (Settings › Download).
            bool logins = s.CookiesBrowserOrNull != null;
            SuggestCookies = ex.NeedsLogin && !logins;
            ErrorHint = ex.RateLimited
                ? L.T("Wait a few minutes and try again: the block lifts on its own. Downloading fewer songs at once helps.")
                : ex.NeedsLogin
                    ? L.T(logins
                        ? "The app already uses your sign-ins: check that you're signed in to this site. If it's YouTube's bot check, try again later or on another connection."
                        : "For private, members-only or age-restricted content, sign in to the site from the app: downloads will use your account.")
                    : null;
#else
            SuggestCookies = ex.NeedsLogin && !s.UseCookies;
            ErrorHint = ex.RateLimited
                ? L.T("Wait a few minutes and try again: the block lifts on its own. Downloading fewer songs at once helps.")
                : ex.NeedsLogin
                    ? L.T(s.UseCookies
                        ? "Browser cookies are on: make sure you're signed in to the site in that browser."
                        : "Turn on “Use browser cookies” in Settings: the app will use the login you already have in your browser.")
                    : null;
#endif
            Error = ex.Message;
            // Removed, private: like a download that failed, it can be searched on YouTube Music.
            CanTryYouTube = (ex.Gone || ex.NeedsLogin) && Sites.NameFor(url) is not ("YouTube" or "YouTube Music");
        }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            SuggestCookies = false;
            ErrorHint = null;
            Error = L.T("Unexpected error:") + " " + ex.Message;
        }
        finally
        {
            if (_cts == cts) IsAnalyzing = false;
        }
    }

    private async Task PickListAsync()
    {
        var file = await Dialogs.PickFileAsync(L.T("Import a list of songs"), (L.T("Lists of songs"), new[] { ".txt", ".csv", ".m3u", ".m3u8", ".xml" }));
        if (file == null) return;
        string text;
        try { text = await File.ReadAllTextAsync(file); }
        catch (Exception ex)
        {
            Link = null;
            Error = ex.Message;
            return;
        }
        await ImportListAsync(text, Path.GetFileNameWithoutExtension(file));
    }

    // A list of songs (pasted, or from a file): one card with all of them, like a playlist's link. name: the file's.
    public async Task ImportListAsync(string text, string? name = null)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _lastUrl = "";
        IsAnalyzing = true;
        AnalyzingText = L.T("Reading the list…");
        Error = null;
        ErrorHint = null;
        SuggestCookies = false;
        CanTryYouTube = false;
        Link = null;
        try
        {
            var entries = ListImport.Parse(text);
            if (entries.Count == 0) throw new EngineException(L.T("No songs found in this list."));
            if (Engines.Missing().Count > 0 && entries.Any(e => e.Url != null))
            {
                AnalyzingText = L.T("Preparing download engines…");
                await _main.Host.EnsureEnginesAsync();
            }
            var ui = Application.Current.Dispatcher;
            var cookies = _main.Host.Settings.CookiesBrowserOrNull;
            var r = await Task.Run(() => ListImport.AnalyzeAsync(entries, name ?? L.T("Imported list"), cookies,
                (done, total) => ui.BeginInvoke(() => { if (_cts == cts) AnalyzingText = L.F("Reading the list: {0} of {1}…", done, total); }), cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            Link = new LinkViewModel(r, r.Items, _main);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            Error = ex is EngineException or System.Xml.XmlException ? ex.Message : L.T("Unexpected error:") + " " + ex.Message;
        }
        finally
        {
            if (_cts == cts) IsAnalyzing = false;
        }
    }

    // A song chosen among the online search's results: its card at once, without reading the link again (the search already
    // said what it is). Deezer's and Spotify's, catalogues, are read like a pasted link (their audio comes from YouTube Music).
    public void ShowHit(SearchHit hit)
    {
        if (hit.Service is OnlineSearchServices.Deezer or OnlineSearchServices.Spotify)
        {
            _ = AnalyzeAsync(hit.Url);
            return;
        }
        _cts?.Cancel();
        _cts = null;
        _lastUrl = hit.Url;
        IsAnalyzing = false;
        Error = null;
        ErrorHint = null;
        SuggestCookies = false;
        CanTryYouTube = false;
        var item = OnlineSearchServices.ToItem(hit);
        var r = new AnalysisResult
        {
            Site = Sites.NameFor(hit.Url), SourceUrl = hit.Url, Title = hit.Title, Uploader = hit.Artist, Thumbnails = item.Thumbnails.ToList(),
            PreferAudio = hit.Service != OnlineSearchServices.YouTube,
        };
        r.Items.Add(item);
        Link = new LinkViewModel(r, r.Items, _main);
    }

    // The link that failed, as the same song on YouTube Music: its card like any other (playlist, save or not).
    private async Task AnalyzeOnYouTubeAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        IsAnalyzing = true;
        AnalyzingText = L.T("Looking up the song's title and artist…");
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
            Error = ex is EngineException ? ex.Message : L.T("Unexpected error:") + " " + ex.Message;
        }
        finally
        {
            if (_cts == cts) IsAnalyzing = false;
        }
    }
}
