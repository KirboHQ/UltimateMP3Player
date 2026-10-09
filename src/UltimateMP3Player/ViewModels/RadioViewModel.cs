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
    // Its stand-in for the rest of the app (its file is the cache's once it's here: T.CachePath).
    public Track T { get; }
    public string Id => Song.Id;

    public FileState State { get; private set; }
    public double Pct { get; private set; }
    public bool IsReady => State == FileState.Ready;
    public bool IsBusy => State == FileState.Downloading;
    public bool IsFailed => State == FileState.Failed;
    public string PctText => IsBusy ? $"{Pct:0}%" : "";
    public string FromText => Song.FromLink ? L.F("Da {0}, non salvato", Song.Service) : L.F("Consigliato da {0}", Song.Service);
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
        OnChanged(nameof(State), nameof(Pct), nameof(IsReady), nameof(IsBusy), nameof(IsFailed), nameof(PctText), nameof(StateTip), nameof(FromText));
    }
}

// The songs heard through the temporary cache instead of a file of the library: the songs suggested online after a song
// ("Play similar songs", or a song played outside a list), the ones of a link heard without saving them, and the songs of
// the library in the cloud (not saved on the device). They get downloaded into the cache of "Listen together" a few at a
// time as their turn comes (the same "songs ready ahead" as the rooms). A suggested song can be kept in the library (in
// the cloud) or saved on the device. Heard, it stays among the recently played (Home) until newer songs push it out, and
// in the statistics once a play of it counted.
public sealed class RadioViewModel : Observable
{
    // Songs asked for at a time, and how many still waiting before asking again.
    private const int Batch = 25, Low = 8;
    private static readonly SemaphoreSlim CoverGate = new(3);

    private readonly MainViewModel _main;
    private readonly Dictionary<string, RadioItemViewModel> _items = new();
    // Library songs in the cloud the cache is getting (or has): what it needs to know of them, by their library id.
    private readonly Dictionary<string, RadioSong> _streams = new();
    private readonly RadioFetcher _fetcher;
    // Lists that found nothing more: not asked again.
    private readonly HashSet<string> _exhausted = new();
    // Songs of the library that were suggestions saved in this session (library id → the suggestion).
    private readonly Dictionary<string, RadioSong> _saved = new();
    private CancellationTokenSource? _cts;

    public RadioViewModel(MainViewModel main)
    {
        _main = main;
        var host = main.Host;
        var ui = Application.Current.Dispatcher;
        _fetcher = new RadioFetcher(host.SongCache,
            () => (host.Settings.AudioFormat, host.Settings.CookiesBrowserOrNull, Math.Clamp(host.Settings.TogetherCacheSize, 1, 200)), a => ui.BeginInvoke(a));
        _fetcher.Changed += OnFile;
        _fetcher.Replaced += OnReplaced;
        // The ones of the last session: their files may still be in the cache (the player may even be playing one).
        foreach (var s in main.Profile.Data.Radio.Where(s => RadioSong.IsRadio(s.Id))) Add(s).T.CachePath = host.SongCache.Find(s.Keys);
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
    // Gone from its site, and not found anywhere else.
    public bool IsGone(string id) => _fetcher.IsGone(id);
    // Couldn't be fetched because there's no connection.
    public bool IsOffline(string id) => _fetcher.IsOffline(id);
    public FileStatus? StatusOf(string id) => _fetcher.StatusOf(id);

    // A song of the library heard from its link: not saved on the device (or its file is gone), with a link to get it.
    public bool IsCloud(Track t) => _main.Library.Get(t.Id) == t && !t.HasSavedFile && t.HasLink;

    // Played from the cache, fetched first if needed: a suggested song, or a library song in the cloud.
    public bool Streams(Track t) => _items.ContainsKey(t.Id) || IsCloud(t);

    // Heard enough to stay in the statistics without being in the library (a play of it counted).
    private bool Heard(string id) => _main.Profile.StatsOf(id) is { Plays: > 0 };

    // The suggested (and heard) songs that stay in the statistics.
    public IEnumerable<RadioItemViewModel> HeardItems() => _items.Values.Where(i => Heard(i.Id));

    public void Detach()
    {
        _cts?.Cancel();
        _fetcher.Changed -= OnFile;
        _fetcher.Replaced -= OnReplaced;
        _fetcher.Dispose();
    }

    public void Save() => _main.Profile.Data.Radio = _items.Values.Select(i => i.Song).ToList();

    public static RadioSeed SeedOf(Track t) => new(t.Title, t.Artist, t.Keys.ToList());

    // pictures: where its cover may be, in order (otherwise its Thumb).
    private RadioItemViewModel Add(RadioSong s, IReadOnlyList<string>? pictures = null)
    {
        var t = new Track
        {
            Id = s.Id, Title = s.Title, Artist = s.Artist, Album = s.Album, Duration = s.Duration, SourceUrl = s.Url, Site = s.Service, Keys = s.Keys.ToList(),
            ArtUrl = s.Thumb is { Length: <= 250 } th && th.StartsWith("https://") && !th.Contains("webp") ? th : null,
            HasCover = File.Exists(AppPaths.TrackCover(s.Id)),
        };
        var item = new RadioItemViewModel(s, t);
        _items[s.Id] = item;
        if (!t.HasCover && s.Thumb != null) _ = LoadCover(item, pictures ?? new[] { s.Thumb });
        return item;
    }

    // A song of the library already known by these keys (one of the library heard from the cloud counts too).
    private Track? Known(RadioSong song, bool similar)
    {
        var lib = _main.Library;
        var have = lib.FindByKeys(song.Keys) ?? (similar ? lib.FindSimilar(song.Title, song.Artist, song.Duration) : null);
        return have != null && (have.HasSavedFile || have.HasLink) ? have : null;
    }

    // ------------------------------------------------------------------ a link heard without saving it

    // The songs of a link to hear without saving them ("Listen without saving"): in the queue like the suggested ones,
    // each downloaded into the cache just before its turn. One you already have (saved or in the cloud) plays as the
    // library song, one already in the queue keeps its id. The ids to play, in order.
    public List<string> AddFromLink(IEnumerable<MediaItem> items)
    {
        var ids = new List<string>();
        foreach (var it in items)
        {
            var song = RadioSong.FromItem(it);
            if (Known(song, it.Source == SourceKind.Search) is { } have) ids.Add(have.Id);
            else if (_items.Values.FirstOrDefault(i => i.Song.Keys.Intersect(song.Keys, StringComparer.OrdinalIgnoreCase).Any()) is { } same) ids.Add(same.Id);
            else
            {
                // (a file already in the cache, heard in a room or as a suggestion, is found when its turn comes)
                Add(song, RadioSong.Pictures(it));
                ids.Add(song.Id);
            }
        }
        return ids;
    }

    // ------------------------------------------------------------------ heard in a room

    // A song of a room that isn't in the library, just heard: kept like the songs of a link, so it's among the recently
    // played and plays again from there (from the cache, or its page). Its id; null for one that can't be found again
    // (a file of the host's own, with no page).
    public string? KeepHeard(Track t)
    {
        if (t.SourceUrl is not { } url || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || t.Keys.Count == 0) return null;
        if (_items.Values.FirstOrDefault(i => i.Song.Keys.Intersect(t.Keys, StringComparer.OrdinalIgnoreCase).Any()) is { } same) return same.Id;
        var s = new RadioSong
        {
            Id = RadioSong.Prefix + Ids.New(), Title = t.Title, Artist = t.Artist, Album = t.Album, Duration = t.Duration, Url = url,
            Service = string.IsNullOrEmpty(t.Site) ? Sites.NameFor(url) : t.Site, Thumb = t.ArtUrl, Keys = t.Keys.ToList(), FromLink = true,
        };
        try
        {
            if (t.HasCover) File.Copy(AppPaths.TrackCover(t.Id), AppPaths.TrackCover(s.Id), true);
        }
        catch { }
        var copy = Add(s).T;
        copy.CachePath = _main.Host.SongCache.Find(s.Keys);
        copy.Wave = t.Wave;
        copy.Loudness = t.Loudness;
        copy.Peak = t.Peak;
        copy.Bpm = t.Bpm;
        LyricsStore.Copy(t.Id, copy, t.Lyrics);
        Save();
        return s.Id;
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
        var known = _items.Values.Select(i => i.Song).ToList();
        List<(string Id, RadioSong? New)> picks = new();
        try
        {
            picks = await Task.Run(async () =>
            {
                var hits = await OnlineSearchServices.SimilarAsync(seed, source, Batch, cts.Token);
                // A song you already have plays from the library (saved or in the cloud); one suggested before keeps its id.
                var list = new List<(string Id, RadioSong? New)>();
                foreach (var song in hits.Select(RadioSong.From))
                {
                    if (Known(song, true) is { } have) list.Add((have.Id, null));
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

    // Which songs to get into the cache (the one playing, then the next ones as far as the settings say), more
    // suggestions to ask for when few are left, and the suggested ones no longer in the queue forgotten (unless they're
    // among the recently played or in the statistics).
    public void Refresh()
    {
        var p = _main.Player;
        if (p.InRoom)
        {
            _fetcher.Want(Array.Empty<RadioSong>());
            return;
        }
        int ahead = Math.Clamp(_main.Host.Settings.RadioAhead, 1, TogetherSession.MaxAhead);
        var ids = p.Window(ahead);
        var wanted = new List<RadioSong>();
        var lib = _main.Library;
        foreach (var id in ids)
        {
            if (_items.TryGetValue(id, out var item)) wanted.Add(item.Song);
            else if (lib.Get(id) is { } t && IsCloud(t))
            {
                if (!_streams.TryGetValue(id, out var s)) _streams[id] = s = CloudSongs.StreamOf(t);
                wanted.Add(s);
            }
        }
        // Library songs out of the window: their files stay in the cache (found again by their keys).
        foreach (var gone in _streams.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            _streams.Remove(gone);
            _fetcher.Forget(gone);
        }
        _fetcher.Want(wanted);
        // Waveform and loudness of the ones in the window that don't have them yet (the suggestions of the last session,
        // the library songs in the cloud heard for the first time), only for these few.
        foreach (var id in ids)
            if (TrackFor(id) is { Wave: null } t && t.AudioPath is { } path) _ = Analyze(t, path);
        var used = p.Referenced();
        var recent = Recent();
        foreach (var id in _items.Keys.Where(id => !used.Contains(id)).ToList())
        {
            if (recent.Contains(id) || Heard(id)) Rest(id);
            else Drop(id);
        }
        if (!Busy && p.RadioRunningLow(Low) is { } contextId && !_exhausted.Contains(contextId) && p.RadioSeed() is { } seed)
            _ = Fetch(seed, contextId, false);
    }

    private Track? TrackFor(string id) => _items.TryGetValue(id, out var i) ? i.T : _streams.ContainsKey(id) ? _main.Library.Get(id) : null;

    // The player waits for this song: it goes first (and is tried again if it had failed).
    public void Prepare(string id)
    {
        _fetcher.Retry(id);
        Refresh();
    }

    // The ones among the recently played (as many as Home shows).
    private HashSet<string> Recent() =>
        _main.Profile.RecentTracks(HomeViewModel.RecentCount, id => _items.ContainsKey(id) || _main.Library.Get(id) != null).Where(_items.ContainsKey).ToHashSet();

    // Out of the queue but still among the recently played or in the statistics: kept, with its cover, to play again from
    // there; its file in the cache is free to go (it's found again there, or downloaded, when it plays).
    private void Rest(string id)
    {
        if (_fetcher.StatusOf(id) == null) return;
        _fetcher.Forget(id);
        _items[id].Refresh(null);
    }

    private void Drop(string id)
    {
        _items.Remove(id);
        _fetcher.Forget(id);
        _main.ForgetVm(id);
        try { File.Delete(AppPaths.TrackCover(id)); } catch { }
        LyricsStore.Delete(id);
        // Time heard without a play that counted: it doesn't stay in the statistics.
        if (_main.Profile.StatsOf(id) != null) _main.Profile.ForgetTracks(new[] { id });
    }

    // "Remove completely" on a suggested song: out of the queue, the recently played and the statistics.
    public async Task Forget(IReadOnlyCollection<string> ids)
    {
        await _main.Player.RemoveTracks(ids);
        _main.Profile.ForgetTracks(ids);
        foreach (var id in ids)
            if (_items.ContainsKey(id)) Drop(id);
        Save();
    }

    private void OnFile(string id)
    {
        var st = _fetcher.StatusOf(id);
        var p = _main.Player;
        if (_items.TryGetValue(id, out var item))
        {
            item.T.CachePath = _fetcher.PathFor(id) ?? item.T.CachePath;
            item.Refresh(st);
        }
        else if (_streams.ContainsKey(id) && _main.Library.Get(id) is { } t)
        {
            t.CachePath = _fetcher.PathFor(id) ?? t.CachePath;
            // Gone from its site and found nowhere else: shown as such, skipped when its turn comes.
            if (st?.State == FileState.Failed && _fetcher.IsGone(id) && !t.Unavailable)
            {
                t.Unavailable = true;
                _main.Library.Changed(t);
            }
            else if (st?.State == FileState.Ready && t.Unavailable)
            {
                t.Unavailable = false;
                _main.Library.Changed(t);
            }
            _main.Vm(t).RefreshCloud();
        }
        else return;
        switch (st?.State)
        {
            case FileState.Ready:
                p.OnRadioReady(id);
                if (TrackFor(id) is { Wave: null } a && a.AudioPath is { } path) _ = Analyze(a, path);
                break;
            case FileState.Failed:
                p.OnRadioFailed(id);
                break;
            default:
                p.OnRadioProgress(id);
                break;
        }
    }

    // Its link didn't work, the same song was found on YouTube: heard (and saved) from there from now on.
    private void OnReplaced(string id, string url, List<string> keys)
    {
        string? title = null, from = null;
        if (_items.TryGetValue(id, out var item))
        {
            from = item.T.Site;
            title = item.T.Title;
            item.T.SourceUrl = url;
            item.T.Site = item.Song.Service;
            item.T.Keys = item.Song.Keys.ToList();
            item.Refresh(_fetcher.StatusOf(id));
            Save();
        }
        else if (_main.Library.Get(id) is { } t)
        {
            from = t.Site;
            title = t.Title;
            t.SourceUrl = url;
            t.Site = Sites.NameFor(url);
            t.Unavailable = false;
            _main.Library.AddKeys(t, keys);
            _main.Library.Changed(t);
        }
        if (title != null)
            _main.Toast(L.F("«{0}» non è più disponibile su {1}: lo trovi su {2}, e da ora si ascolta da lì", title, string.IsNullOrEmpty(from) ? L.T("il suo sito") : from, Sites.NameFor(url)));
    }

    // Waveform and loudness, as for the songs saved (the volume is normalized the same way). Once per song and session (a
    // file that can't be read isn't tried again at every change of the queue). A library song keeps them.
    private readonly HashSet<string> _analyzing = new();

    private async Task Analyze(Track t, string path)
    {
        if (t.Wave != null || !_analyzing.Add(t.Id)) return;
        try
        {
            var a = await Task.Run(() => AudioAnalysis.AnalyzeAsync(path, CancellationToken.None));
            if (t.Wave != null) return;
            t.Wave = a.Wave;
            t.Loudness = a.Loudness;
            t.Peak = a.Peak;
            if (a.Duration > 0) t.Duration = a.Duration;
            if (_main.Library.Get(t.Id) == t) _main.Library.Changed(t);
            else _main.Vm(t).Refresh();
            if (_main.Player.Current?.Id == t.Id) _main.Player.ApplyNormalization();
        }
        catch { }
    }

    // ------------------------------------------------------------------ covers

    // The site's picture, made square like the covers of the library: the first of these that can be had.
    private async Task LoadCover(RadioItemViewModel item, IReadOnlyList<string> pictures)
    {
        await CoverGate.WaitAsync();
        try
        {
            if (!_items.ContainsKey(item.Id)) return;
            var got = await CloudSongs.MakeCoverAsync(item.Id, pictures);
            if (got == null) return;
            // The one that worked, for the next time.
            item.Song.Thumb = got;
            if (!_items.ContainsKey(item.Id))
            {
                try { File.Delete(AppPaths.TrackCover(item.Id)); } catch { }
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

    // Into the library without its audio (in the cloud), and into a playlist if given: the queue, the recently played and
    // the statistics go on with the library song. One the library already has (the same link) is that one.
    public Track Keep(RadioItemViewModel item, Playlist? playlist)
    {
        var s = item.Song;
        var lib = _main.Library;
        var t = lib.FindByKeys(s.Keys);
        if (t == null)
        {
            t = CloudSongs.FromRadio(s, item.T.Duration);
            t.ArtUrl = item.T.ArtUrl;
            t.Wave = item.T.Wave;
            t.Loudness = item.T.Loudness;
            t.Peak = item.T.Peak;
            t.Bpm = item.T.Bpm;
            try
            {
                if (File.Exists(AppPaths.TrackCover(item.Id)))
                {
                    File.Copy(AppPaths.TrackCover(item.Id), AppPaths.TrackCover(t.Id), true);
                    t.HasCover = true;
                }
            }
            catch { }
            LyricsStore.Copy(item.Id, t, item.T.Lyrics);
            t.CachePath = _fetcher.PathFor(item.Id) ?? item.T.CachePath;
            lib.Add(t);
        }
        if (playlist != null) _main.Profile.AddTrack(playlist, t.Id);
        _saved[t.Id] = s;
        _fetcher.Forget(item.Id);
        // Among the recently played and in the statistics as the library song now (before the queue changes: then the
        // suggestion goes).
        _main.Profile.RenameHistory(item.Id, t.Id);
        _main.Player.ReplaceRadio(item.Id, t);
        return t;
    }

    // Kept and saved on the device: from the cache's copy at once when it's there, otherwise downloaded.
    public async Task<Track?> Save(RadioItemViewModel item, Playlist? playlist)
    {
        var t = Keep(item, playlist);
        await _main.SaveToDevice(new[] { _main.Vm(t) }, quiet: playlist != null);
        if (playlist != null) _main.Toast(L.F("«{0}» salvato in «{1}»", t.Title, PlaylistViewModel.DisplayName(playlist)));
        return t;
    }

    // ------------------------------------------------------------------ deleted while it plays

    // A suggestion saved to the library in this session.
    public bool WasSaved(string libraryId) => _saved.ContainsKey(libraryId);

    // A saved suggestion deleted from the library while it plays: it goes back to being a suggestion, with its file in
    // the cache (the copy it was playing from, still there, or a copy of the library file), so this listen goes on.
    // Afterwards the cache keeps or drops it like any other. playing: the file the player has open.
    public RadioItemViewModel? Unsave(Track saved, string? playing)
    {
        if (!_saved.Remove(saved.Id, out var song)) return null;
        var cache = _main.Host.SongCache;
        var s = new RadioSong
        {
            Id = RadioSong.Prefix + Ids.New(), Title = saved.Title, Artist = saved.Artist, Album = saved.Album, Duration = saved.Duration,
            Url = saved.SourceUrl ?? song.Url, Service = saved.Site ?? song.Service, Thumb = song.Thumb, Keys = saved.Keys.ToList(),
        };
        string? path = null;
        try
        {
            if (playing != null && File.Exists(playing) && cache.IsInside(playing)) path = cache.Keep(s.Keys, s.Title, s.Artist, playing);
            else if (File.Exists(saved.Path))
            {
                var tmp = Path.Combine(cache.TempDir, Ids.New() + Path.GetExtension(saved.Path));
                File.Copy(saved.Path, tmp);
                path = cache.Adopt(s.Keys, s.Title, s.Artist, tmp);
            }
        }
        catch { }
        if (path == null) return null;
        try
        {
            if (saved.HasCover) File.Copy(AppPaths.TrackCover(saved.Id), AppPaths.TrackCover(s.Id), true);
        }
        catch { }
        var item = Add(s);
        var t = item.T;
        t.CachePath = path;
        t.HasCover = File.Exists(AppPaths.TrackCover(s.Id));
        t.ArtUrl = saved.ArtUrl ?? t.ArtUrl;
        t.Wave = saved.Wave;
        t.Loudness = saved.Loudness;
        t.Peak = saved.Peak;
        t.Bpm = saved.Bpm;
        LyricsStore.Copy(saved.Id, t, saved.Lyrics);
        _fetcher.Have(s, path);
        item.Refresh(_fetcher.StatusOf(s.Id));
        return item;
    }
}
