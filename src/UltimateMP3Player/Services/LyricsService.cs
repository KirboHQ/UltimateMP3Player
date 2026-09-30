using System.IO;
using System.Net.Http;
using System.Windows.Input;
using System.Windows.Threading;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Services;

// Songs asked together (a selection, the whole library): the toast at the end counts them.
public sealed class LyricsBatch
{
    public int Total { get; init; }
    public int Done, Synced, Plain, Missing, Failed;
    // Several network errors in a row: the rest is dropped.
    public int FailedInRow;
    public bool Library { get; init; }
    public bool Canceled { get; set; }
    public int Found => Synced + Plain;
}

// Lyrics searched online one song at a time (LyricsFinder): the song playing and new songs by themselves
// (Settings > Lyrics), a song or a selection from its menu, the missing ones of the whole library from Settings.
// Everything runs on the UI thread; only the network waits.
public sealed class LyricsService : Observable
{
    // A pause between songs, not to flood the archive (it answers 503 when asked too fast).
    private static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(350);

    private readonly AppHost _host;
    private readonly LinkedList<(Track Track, LyricsBatch? Batch, bool Manual)> _queue = new();
    private readonly HashSet<string> _queued = new();
    private bool _running;

    public LyricsService(AppHost host)
    {
        _host = host;
        LyricsFinder.UserAgent = $"UltimateMP3Player/{AppInfo.VersionText} (" + (AppInfo.RepoUrl ?? "https://github.com/KirboHQ/UltimateMP3Player") + ")";
        var ui = System.Windows.Application.Current.Dispatcher;
        host.Library.TrackAdded += t => ui.BeginInvoke(() => Auto(t, false));
        // Files of songs that are gone (older than a day: a room's songs live only while it's open).
        _ = Task.Run(() => LyricsStore.Sweep(id => host.Library.Get(id) != null));
        StopScanCommand = new RelayCommand(StopScan, () => Scan != null);
    }

    // A song is done: what was found (null = failed), asked by hand, and the songs asked with it (null = alone).
    public event Action<Track, LyricsKind?, bool, LyricsBatch?>? TrackDone;
    // A batch asked by hand (a selection) is over: the toast.
    public event Action<LyricsBatch>? BatchDone;

    public ICommand StopScanCommand { get; }

    public bool IsSearching(Track t) => _queued.Contains(t.Id) || _busy == t.Id;
    private string? _busy;

    // The song is playing or was just added: searched once, if it never was (and the setting is on).
    public void Auto(Track t, bool playing)
    {
        if (!_host.Settings.AutoLyrics || t.Lyrics != null || !Worth(t)) return;
        if (_queued.Contains(t.Id))
        {
            // The song playing goes first.
            if (playing) MoveToFront(t.Id);
            return;
        }
        if (_busy == t.Id) return;
        Enqueue(t, null, false, playing);
    }

    // Sound effects, DJ mixes and very short clips aren't songs with words.
    private static bool Worth(Track t) => t.Duration is 0 or >= 30 && t.Site != "DJ";

    // From the menu of a song or a selection: searched again even if it has lyrics.
    public void Search(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0) return;
        var batch = tracks.Count > 1 ? new LyricsBatch { Total = tracks.Count } : null;
        foreach (var t in tracks.Reverse())
        {
            Remove(t.Id);
            if (_busy == t.Id) continue;
            Enqueue(t, batch, true, true);
        }
    }

    // ------------------------------------------------------------------ the whole library (Settings)

    private LyricsBatch? _scan;
    public LyricsBatch? Scan { get => _scan; private set { if (Set(ref _scan, value)) OnChanged(nameof(IsScanning)); } }
    public bool IsScanning => Scan != null;
    public double ScanProgress => Scan is { Total: > 0 } s ? (double)s.Done / s.Total : 0;
    public string ScanText => Scan is { } s ? L.F("Cerco {0} di {1}… · trovati {2}", Math.Min(s.Done + 1, s.Total), s.Total, s.Found) : "";

    // Songs never searched, and the ones not found last time (the archive grows).
    public List<Track> Missing() => _host.Library.Snapshot().Where(t => Worth(t) && t.Lyrics is null or LyricsKind.None).ToList();

    public void ScanLibrary()
    {
        if (Scan != null) return;
        var todo = Missing().Where(t => !_queued.Contains(t.Id) && _busy != t.Id).ToList();
        if (todo.Count == 0)
        {
            _host.Session?.Toast(L.T("Non ci sono testi da cercare."));
            return;
        }
        var batch = new LyricsBatch { Total = todo.Count, Library = true };
        Scan = batch;
        foreach (var t in todo) Enqueue(t, batch, true, false);
        OnScanChanged();
    }

    private void StopScan()
    {
        if (Scan is not { } s) return;
        s.Canceled = true;
        foreach (var node in _queue.Where(j => j.Batch == s).ToList())
        {
            _queue.Remove(node);
            _queued.Remove(node.Track.Id);
        }
        FinishScan(s);
    }

    private void FinishScan(LyricsBatch s)
    {
        if (Scan != s) return;
        Scan = null;
        OnScanChanged();
        _host.Session?.Toast(s.Canceled && s.FailedInRow >= 3
            ? L.T("LRCLIB non risponde: controlla la connessione e riprova più tardi.")
            : L.F("Testi: {0} trovati, {1} senza testo", s.Found, s.Missing));
        CommandManager.InvalidateRequerySuggested();
    }

    private void OnScanChanged() => OnChanged(nameof(ScanProgress), nameof(ScanText), nameof(IsScanning));

    // ------------------------------------------------------------------ queue

    private void Enqueue(Track t, LyricsBatch? batch, bool manual, bool front)
    {
        _queued.Add(t.Id);
        if (front) _queue.AddFirst((t, batch, manual));
        else _queue.AddLast((t, batch, manual));
        Pump();
    }

    private void MoveToFront(string id)
    {
        if (_queue.FirstOrDefault(j => j.Track.Id == id) is not { Track: not null } job) return;
        _queue.Remove(job);
        _queue.AddFirst(job);
    }

    private void Remove(string id)
    {
        foreach (var job in _queue.Where(j => j.Track.Id == id).ToList()) _queue.Remove(job);
        _queued.Remove(id);
    }

    private async void Pump()
    {
        if (_running) return;
        _running = true;
        try
        {
            while (_queue.First is { } node)
            {
                _queue.RemoveFirst();
                var (t, batch, manual) = node.Value;
                _queued.Remove(t.Id);
                if (batch?.Canceled == true) continue;
                _busy = t.Id;
                LyricsKind? kind = null;
                bool failed = false;
                try
                {
                    var had = t.Lyrics;
                    var r = await LyricsFinder.FindAsync(t.Title, t.Artist, t.Duration, CancellationToken.None);
                    // Searched again by hand without finding anything: the lyrics it had stay.
                    if (!(r.Kind is LyricsKind.None && had is LyricsKind.Synced or LyricsKind.Plain))
                        await Task.Run(() => LyricsStore.Save(t, r));
                    kind = r.Kind;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or System.Text.Json.JsonException)
                {
                    failed = true;
                }
                catch (Exception ex)
                {
                    failed = true;
                    try { File.AppendAllText(Path.Combine(AppPaths.DataDir, "errori.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] lyrics: {ex}\n\n"); } catch { }
                }
                _busy = null;
                if (!failed && _host.Library.Get(t.Id) == t) _host.Library.Changed(t);
                Count(batch, kind, failed);
                TrackDone?.Invoke(t, failed ? null : kind, manual, batch);
                if (batch != null && batch.Done >= batch.Total)
                {
                    if (batch.Library) FinishScan(batch);
                    else if (!batch.Canceled) BatchDone?.Invoke(batch);
                }
                if (_queue.Count > 0) await Task.Delay(Gap);
            }
        }
        finally
        {
            _running = false;
            _busy = null;
        }
    }

    private void Count(LyricsBatch? b, LyricsKind? kind, bool failed)
    {
        if (b == null) return;
        b.Done++;
        if (failed) { b.Failed++; b.FailedInRow++; }
        else
        {
            b.FailedInRow = 0;
            if (kind == LyricsKind.Synced) b.Synced++;
            else if (kind == LyricsKind.Plain) b.Plain++;
            else b.Missing++;
        }
        // Offline: the rest of the library waits for another time.
        if (b.Library && b.FailedInRow >= 3 && !b.Canceled)
        {
            b.Canceled = true;
            foreach (var node in _queue.Where(j => j.Batch == b).ToList())
            {
                _queue.Remove(node);
                _queued.Remove(node.Track.Id);
            }
            FinishScan(b);
            return;
        }
        if (b == Scan) OnScanChanged();
    }

    // Removed by hand from a song's menu.
    public void Delete(Track t)
    {
        LyricsStore.Delete(t.Id);
        t.Lyrics = LyricsKind.None;
        if (_host.Library.Get(t.Id) == t) _host.Library.Changed(t);
        TrackDone?.Invoke(t, LyricsKind.None, false, null);
    }
}
