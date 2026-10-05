using System.IO;
using System.Windows;
using UltimateMP3Player.Core;
using UltimateMP3Player.Core.Together;

namespace UltimateMP3Player.ViewModels;

// A suggested song of the queue, and how far its download is.
public sealed class RadioItemViewModel : Observable
{
    public RadioItemViewModel(RadioSong song, Track track)
    {
        Song = song;
        T = track;
    }

    public RadioSong Song { get; }
    // Its stand-in for the rest of the app (the path is the cache file once it's here).
    public Track T { get; }
    public string Id => Song.Id;

    public FileState State { get; private set; }
    public double Pct { get; private set; }
    public bool IsReady => State == FileState.Ready;
    public bool IsBusy => State == FileState.Downloading;
    public bool IsFailed => State == FileState.Failed;
    public string PctText => IsBusy ? $"{Pct:0}%" : "";
    public string FromText => L.F("Consigliato da {0}", Song.Service);
    public string StateTip => State switch
    {
        FileState.Ready => FromText + " · " + L.T("pronto"),
        FileState.Downloading => FromText + " · " + L.F("lo sto scaricando, {0:0}%", Pct),
        FileState.Failed => FromText + " · " + L.T("non si riesce a scaricarlo"),
        _ => FromText + " · " + L.T("si scarica quando si avvicina il suo turno"),
    };

    public void Refresh(FileStatus? st)
    {
        State = st?.State ?? FileState.None;
        Pct = st?.Pct ?? 0;
        OnChanged(nameof(State), nameof(Pct), nameof(IsReady), nameof(IsBusy), nameof(IsFailed), nameof(PctText), nameof(StateTip));
    }
}

// Songs suggested online after a song ("Play similar songs", or a song played outside a list): they go into your queue
// without being in the library, get downloaded into the cache of "Listen together" a few at a time as their turn comes
// (the same "songs ready ahead" as the rooms), and can be kept with "Save to library". When few are left, more come.
public sealed class RadioViewModel : Observable
{
    // Songs asked for at a time, and how many still waiting before asking again.
    private const int Batch = 25, Low = 8;
    private static readonly SemaphoreSlim CoverGate = new(3);

    private readonly MainViewModel _main;
    private readonly Dictionary<string, RadioItemViewModel> _items = new();
    private readonly RadioFetcher _fetcher;
    // Lists that found nothing more: not asked again.
    private readonly HashSet<string> _exhausted = new();
    private CancellationTokenSource? _cts;

    public RadioViewModel(MainViewModel main)
    {
        _main = main;
        var host = main.Host;
        var ui = Application.Current.Dispatcher;
        _fetcher = new RadioFetcher(host.SongCache,
            () => (host.Settings.AudioFormat, host.Settings.CookiesBrowserOrNull, Math.Clamp(host.Settings.TogetherCacheSize, 1, 200)), a => ui.BeginInvoke(a));
        _fetcher.Changed += OnFile;
        // The ones of the last session: their files may still be in the cache (the player may even be playing one).
        foreach (var s in main.Profile.Data.Radio.Where(s => RadioSong.IsRadio(s.Id))) Add(s).T.Path = host.SongCache.Find(s.Keys) ?? "";
        var keep = _items.Keys.ToHashSet();
        _ = Task.Run(() => SweepCovers(keep));
    }

    // Looking for suggestions right now.
    public bool Busy => _cts != null;

    public RadioItemViewModel? Item(string id) => _items.GetValueOrDefault(id);
    public bool Has(string id) => _items.ContainsKey(id);
    public Track? Track(string id) => _items.TryGetValue(id, out var i) ? i.T : null;
    public string? PathFor(string id) => _fetcher.PathFor(id);
    public bool IsFailed(string id) => _fetcher.IsFailed(id);

    public void Detach()
    {
        _cts?.Cancel();
        _fetcher.Changed -= OnFile;
        _fetcher.Dispose();
    }

    public void Save() => _main.Profile.Data.Radio = _items.Values.Select(i => i.Song).ToList();

    public static RadioSeed SeedOf(Track t) => new(t.Title, t.Artist, t.Keys.ToList());

    private RadioItemViewModel Add(RadioSong s)
    {
        var t = new Track
        {
            Id = s.Id, Title = s.Title, Artist = s.Artist, Album = s.Album, Duration = s.Duration, SourceUrl = s.Url, Site = s.Service, Keys = s.Keys.ToList(),
            ArtUrl = s.Thumb is { Length: <= 250 } th && th.StartsWith("https://") && !th.Contains("webp") ? th : null,
            HasCover = File.Exists(AppPaths.TrackCover(s.Id)),
        };
        var item = new RadioItemViewModel(s, t);
        _items[s.Id] = item;
        if (!t.HasCover && s.Thumb != null) _ = LoadCover(item);
        return item;
    }

    // ------------------------------------------------------------------ asking for suggestions

    // A new list of suggestions after this song (the queue already plays it on its own).
    public void Begin(Track seed, string contextId)
    {
        _cts?.Cancel();
        _cts = null;
        _exhausted.Remove(contextId);
        _ = Fetch(SeedOf(seed), contextId, true);
    }

    private async Task Fetch(RadioSeed seed, string contextId, bool first)
    {
        var cts = _cts = new CancellationTokenSource();
        _main.Player.OnRadioBusy();
        var source = _main.Host.Settings.RadioSource;
        var lib = _main.Library;
        var known = _items.Values.Select(i => i.Song).ToList();
        List<(string Id, RadioSong? New)> picks = new();
        try
        {
            picks = await Task.Run(async () =>
            {
                var hits = await OnlineSearchServices.SimilarAsync(seed, source, Batch, cts.Token);
                // A song you already have plays from the library; one suggested before keeps its id.
                var list = new List<(string Id, RadioSong? New)>();
                foreach (var song in hits.Select(RadioSong.From))
                {
                    var have = lib.FindByKeys(song.Keys) ?? lib.FindSimilar(song.Title, song.Artist, song.Duration);
                    if (have != null && File.Exists(have.Path)) list.Add((have.Id, null));
                    else if (known.FirstOrDefault(k => k.Keys.Intersect(song.Keys, StringComparer.OrdinalIgnoreCase).Any()) is { } same) list.Add((same.Id, null));
                    else list.Add((song.Id, song));
                }
                return list;
            }, cts.Token);
        }
        catch { }
        // Not busy any more before the queue hears of it (an empty answer must not look like more coming).
        bool canceled = cts.IsCancellationRequested, mine = _cts == cts;
        if (mine) _cts = null;
        cts.Dispose();
        _main.Player.OnRadioBusy();
        if (canceled || !mine || _main.Player.ContextId != contextId) return;
        foreach (var p in picks)
            if (p.New != null) Add(p.New);
        if (picks.Count == 0) _exhausted.Add(contextId);
        _main.Player.ExtendRadio(contextId, picks.Select(p => p.Id).ToList(), first);
    }

    // "Generate" on a list of suggestions: more of them now.
    public bool More()
    {
        var p = _main.Player;
        if (Busy || p.ContextId is not { } contextId || p.RadioSeed() is not { } seed) return false;
        _exhausted.Remove(contextId);
        _ = Fetch(seed, contextId, false);
        return true;
    }

    // ------------------------------------------------------------------ the queue changed

    // Which suggested songs to get ready (the one playing, then the next ones as far as the settings say), more to ask
    // for when few are left, and the ones no longer in the queue forgotten.
    public void Refresh()
    {
        var p = _main.Player;
        if (p.InRoom)
        {
            _fetcher.Want(Array.Empty<RadioSong>());
            return;
        }
        int ahead = Math.Clamp(_main.Host.Settings.RadioAhead, 1, TogetherSession.MaxAhead);
        _fetcher.Want(p.Window(ahead).Select(Item).OfType<RadioItemViewModel>().Select(i => i.Song).ToList());
        var used = p.Referenced();
        foreach (var id in _items.Keys.Where(id => !used.Contains(id)).ToList()) Drop(id);
        if (!Busy && p.RadioRunningLow(Low) is { } contextId && !_exhausted.Contains(contextId) && p.RadioSeed() is { } seed)
            _ = Fetch(seed, contextId, false);
    }

    // The player waits for this song: it goes first (and is tried again if it had failed).
    public void Prepare(string id)
    {
        _fetcher.Retry(id);
        Refresh();
    }

    private void Drop(string id)
    {
        _items.Remove(id);
        _fetcher.Forget(id);
        _main.ForgetVm(id);
        try { File.Delete(AppPaths.TrackCover(id)); } catch { }
        LyricsStore.Delete(id);
    }

    private void OnFile(string id)
    {
        if (!_items.TryGetValue(id, out var item)) return;
        var st = _fetcher.StatusOf(id);
        item.T.Path = _fetcher.PathFor(id) ?? "";
        item.Refresh(st);
        var p = _main.Player;
        switch (st?.State)
        {
            case FileState.Ready:
                p.OnRadioReady(id);
                _ = Analyze(item);
                break;
            case FileState.Failed:
                p.OnRadioFailed(id);
                break;
            default:
                p.OnRadioProgress(id);
                break;
        }
    }

    // Waveform and loudness, as for the songs of the library (the volume is normalized the same way).
    private async Task Analyze(RadioItemViewModel item)
    {
        if (item.T.Wave != null || item.T.Path.Length == 0) return;
        try
        {
            var path = item.T.Path;
            var a = await Task.Run(() => AudioAnalysis.AnalyzeAsync(path, CancellationToken.None));
            item.T.Wave = a.Wave;
            item.T.Loudness = a.Loudness;
            item.T.Peak = a.Peak;
            if (a.Duration > 0) item.T.Duration = a.Duration;
            _main.Vm(item.T).Refresh();
            if (_main.Player.Current?.Id == item.Id) _main.Player.ApplyNormalization();
        }
        catch { }
    }

    // ------------------------------------------------------------------ covers

    // The site's picture, made square like the covers of the library.
    private async Task LoadCover(RadioItemViewModel item)
    {
        await CoverGate.WaitAsync();
        try
        {
            if (!_items.ContainsKey(item.Id)) return;
            var path = AppPaths.TrackCover(item.Id);
            var url = item.Song.Thumb!;
            bool ok = await Task.Run(async () =>
            {
                var tmp = Path.Combine(AppPaths.TempDir, item.Id + ".thumb");
                try
                {
                    await File.WriteAllBytesAsync(tmp, await Http.GetBytesAsync(url));
                    return await AudioAnalysis.MakeCoverFromImageAsync(tmp, path);
                }
                catch { return false; }
                finally
                {
                    try { File.Delete(tmp); } catch { }
                }
            });
            if (!ok) return;
            if (!_items.ContainsKey(item.Id))
            {
                try { File.Delete(path); } catch { }
                return;
            }
            item.T.HasCover = true;
            item.T.CoverVersion++;
            _main.Vm(item.T).Refresh();
            if (_main.Player.Current?.Id == item.Id) _main.Host.OnTrackChanged();
        }
        finally { CoverGate.Release(); }
    }

    // Covers of suggestions long gone (the app closed before it could forget them). Another profile's queue may still
    // have recent ones.
    private static void SweepCovers(HashSet<string> keep)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.CoversDir, RadioSong.Prefix + "*.jpg"))
                if (!keep.Contains(Path.GetFileNameWithoutExtension(f)) && DateTime.Now - File.GetLastWriteTime(f) > TimeSpan.FromDays(14)) File.Delete(f);
        }
        catch { }
    }

    // ------------------------------------------------------------------ keeping one

    // Into the library (and a playlist): the copy leaves the cache, and the queue goes on with the library song.
    public async Task<Track?> Save(RadioItemViewModel item, Playlist? playlist)
    {
        var from = _fetcher.PathFor(item.Id);
        if (from == null)
        {
            _main.Toast(L.T("Il brano non è ancora stato scaricato: aspetta che sia pronto."));
            return null;
        }
        var s = item.Song;
        try
        {
            var dir = _main.Host.Settings.MusicDir;
            Directory.CreateDirectory(dir);
            var name = Text.SafeFileName(string.IsNullOrWhiteSpace(s.Artist) ? s.Title : $"{s.Artist.Split(',')[0].Trim()} - {s.Title}");
            var target = Text.UniquePath(dir, name, Path.GetExtension(from));
            await Task.Run(() => File.Copy(from, target));
            var t = new Track
            {
                Title = s.Title, Artist = s.Artist, Album = s.Album, Duration = item.T.Duration, Path = target, SourceUrl = s.Url, Site = s.Service,
                Keys = s.Keys.ToList(), ArtUrl = item.T.ArtUrl, Wave = item.T.Wave, Loudness = item.T.Loudness, Peak = item.T.Peak,
            };
            var standInCover = AppPaths.TrackCover(item.Id);
            if (File.Exists(standInCover))
            {
                File.Copy(standInCover, AppPaths.TrackCover(t.Id), true);
                t.HasCover = true;
            }
            else t.HasCover = await AudioAnalysis.ExtractCoverAsync(target, AppPaths.TrackCover(t.Id), false, CancellationToken.None);
            LyricsStore.Copy(item.Id, t, item.T.Lyrics);
            if (t.Wave == null || t.Loudness == null || t.Duration <= 0)
            {
                try
                {
                    var a = await AudioAnalysis.AnalyzeAsync(target, CancellationToken.None);
                    t.Wave ??= a.Wave;
                    t.Loudness ??= a.Loudness;
                    t.Peak ??= a.Peak;
                    if (a.Duration > 0) t.Duration = a.Duration;
                }
                catch { }
            }
            _main.Library.Add(t);
            if (playlist != null) _main.Profile.AddTrack(playlist, t.Id);
            _fetcher.Forget(item.Id);
            _main.Player.ReplaceRadio(item.Id, t);
            // It plays on from the cache copy; that goes once it's no longer open.
            if (_main.Host.SongCache.Contains(from)) _main.Host.SongCache.Forget(from);
            _main.Toast(playlist != null ? L.F("«{0}» salvato in «{1}»", t.Title, PlaylistViewModel.DisplayName(playlist)) : L.F("«{0}» salvato nella libreria", t.Title));
            return t;
        }
        catch (Exception ex)
        {
            _main.Toast(L.T("Salvataggio non riuscito:") + " " + ex.Message);
            return null;
        }
    }
}
