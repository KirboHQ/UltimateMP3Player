using System.Collections.Concurrent;
using Jint;
using Jint.Native;
using Jint.Runtime;

namespace UltimateMP3Player.Core.Mods;

// What the app gives the mods; implemented on the UI side (ModsViewModel). Called from the mods' threads.
public interface IModHost
{
    string AppVersion { get; }
    ModSong? Song { get; }
    double Position { get; }
    bool Playing { get; }
    // "play", "pause", "next", "previous": done on the UI thread.
    void Control(string action);
    // The whole library: a copy made on the UI thread.
    IReadOnlyList<ModSong> Songs();
    // A mod's words: the host shows whose they are.
    void Toast(ModRuntime mod, string text);
    Task<bool> ConfirmAsync(ModRuntime mod, string title, string message, string ok);
    Task<int> ChooseAsync(ModRuntime mod, string title, string message, string[] options);
    // One of a list (a beatmap's difficulties): its index, -1 if none. selected: the one ticked.
    Task<int> ListAsync(ModRuntime mod, string title, IReadOnlyList<(string Title, string? Hint)> items, int selected);
    // A mod's state, settings or menu entries changed: what shows them refreshes (the host posts it to the UI).
    void Changed(ModRuntime mod);
    // Pictures and text for the visualizers (Core/Mods/ModCanvas.cs), done by the UI side's renderer; any thread.
    ModImage? LoadImage(string file);
    float MeasureText(string text, float size, bool bold);
    // Whether the renderer adds colours ("lighter"): not WPF's.
    bool CanBlend { get; }
    // Which visualizer shows (as the switch in the mod's page): whether this one is, and choosing it (the mod asked the user first).
    bool IsChosen(ModVisualizer v);
    void Choose(ModVisualizer v, bool on);
    // Where the app keeps its music (a zip a mod extracts goes under it, in a folder with the mod's name).
    string MusicDir { get; }
    // The user picks a folder (the computers only) or a file for a mod: its path, or null. start: where the picker opens.
    bool CanPickFolders { get; }
    Task<string?> PickAsync(ModRuntime mod, bool folder, string title, IReadOnlyList<string> extensions, string? start);
    // A song a mod adds to the library (ump.library.add, "library.write"): its id, or null if it can't be read.
    Task<string?> AddSongAsync(ModRuntime mod, ModNewSong song);
    IReadOnlyList<ModPlaylist> Playlists();
    Task<string?> CreatePlaylistAsync(ModRuntime mod, string name);
    Task<bool> AddToPlaylistAsync(ModRuntime mod, string playlistId, IReadOnlyList<string> songIds);
    // (what follows came later: a host without it still works)
    // A picture at most maxSide pixels a side, blurred by blur pixels of that size (images.load's options); any thread.
    ModImage? LoadImage(string file, int maxSide, float blur) => LoadImage(file);
    // A sound the app decodes itself (what isn't a plain WAV: Ogg, MP3): stereo samples and their rate, or null; any thread.
    (float[] Samples, int Rate)? DecodeSound(string file) => null;
    // Sounds played with a song at their times (ump.sounds.cue, a mod's hitsounds); null takes this mod's away.
    void Cue(ModRuntime mod, CueSheet? sheet) { }
    // A song like this in the library (the same title and artist, the closest length), for a mod to know it's there already.
    ModSong? FindSong(string title, string? artist, double? duration) => null;
    // One song takes another's place (library.replace): playlists, plays and likes move to it, the other leaves the library
    // (its file stays where it is).
    Task<bool> ReplaceSongAsync(ModRuntime mod, string oldId, string newId) => Task.FromResult(false);
    // The sign-ins made in the app (cookies.txt) when the downloads use them, for a mod's downloads from those sites; else null.
    string? CookieFile => null;
    // Settings › Downloads: the songs' videos come with them (a mod asking "with video?" puts that answer first).
    bool WantsVideos => false;
    // One of these chosen, then confirmed with the ok button (ui.pick): its index, or -1.
    Task<int> PickAsync(ModRuntime mod, string title, string message, IReadOnlyList<(string Title, string? Hint)> items, int selected, string ok) =>
        ListAsync(mod, title, items, selected);
}

// A song as the mods see it (a copy: a mod never holds the library's objects).
public sealed record ModSong(string Id, string Title, string? Artist, string? Album, string? Year, double Duration, double? Bpm,
    string? Site, string? Url, string? Path, DateTime Added, bool HasCover)
{
    // (for the visualizers, not in the song's object)
    public double? BeatOffset { get; init; }
    public byte[]? Wave { get; init; }
    public int CoverVersion { get; init; }

    public static ModSong Of(Track t) => new(t.Id, t.Title, t.Artist, t.Album, t.Year, t.Duration, t.Bpm, t.Site, t.SourceUrl, t.Path, t.Added, t.HasCover)
    {
        BeatOffset = t.BeatOffset, Wave = t.Wave, CoverVersion = t.CoverVersion,
    };
}

public enum ModStatus { Off, Running, Failed }

// One mod running: its own JavaScript engine on its own thread (Jint is single-threaded), so a slow or broken mod never
// holds the UI. Everything the engine does happens on that thread, through Post.
public sealed partial class ModRuntime : IDisposable
{
    private readonly IModHost _host;
    private BlockingCollection<Action>? _work;
    private Thread? _thread;
    private Engine? _engine;
    private CancellationTokenSource? _stop;
    private readonly List<string> _log = new();
    private int _errorsInARow;

    public ModRuntime(string folder, bool isDev, IModHost host)
    {
        Folder = folder;
        IsDev = isDev;
        _host = host;
        try { Manifest = ModManifest.Read(folder); }
        catch (Exception ex)
        {
            // A broken mod stays listed (to be removed or fixed), never started.
            Manifest = new ModManifest { Id = Path.GetFileName(folder.TrimEnd('/', '\\')), Name = Path.GetFileName(folder.TrimEnd('/', '\\')), Version = "?" };
            Broken = ex is ModException or IOException ? ex.Message : ex.ToString();
            Status = ModStatus.Failed;
            Error = Broken;
        }
    }

    // How long one call into a mod may run (start, an event, a menu entry...) before it's cut short. mp3cli shortens it.
    public static TimeSpan CallLimit { get; set; } = TimeSpan.FromSeconds(10);

    public ModManifest Manifest { get; private set; }
    public string Folder { get; }
    public bool IsDev { get; }
    public string Id => Manifest.Id;
    public string? Broken { get; private set; }
    public ModStatus Status { get; private set; }
    public string? Error { get; private set; }
    public HashSet<string> Granted { get; set; } = new();
    public string DataDir => ModPackage.DataDirOf(Id);

    public IReadOnlyList<string> Log { get { lock (_log) return _log.ToList(); } }
    public event Action? LogChanged;

    public void Start()
    {
        if (_thread != null || Broken != null) return;
        Error = null;
        _errorsInARow = 0;
        _stop = new CancellationTokenSource();
        _work = new BlockingCollection<Action>();
        var work = _work;
        _thread = new Thread(() =>
        {
            foreach (var a in work.GetConsumingEnumerable())
            {
                try { a(); }
                catch (Exception ex) { Fail("", ex); }
            }
        }) { IsBackground = true, Name = "mod " + Id };
        _thread.Start();
        Status = ModStatus.Running;
        Post(Boot);
        _host.Changed(this);
    }

    public void Stop()
    {
        if (_thread == null) return;
        var done = new ManualResetEventSlim();
        Post(() =>
        {
            Call("stop", _stopFn);
            Emit("stop");
            CloseRealms();
            StopSounds();
            done.Set();
        });
        // A mod gets a moment to save its things, then whatever it's doing is cut short.
        done.Wait(1500);
        _stop?.Cancel();
        _work?.CompleteAdding();
        if (!_thread.Join(2000)) Write(L.T("The mod didn't stop in time."));
        _thread = null;
        lock (_timers) { foreach (var t in _timers.Values) t.Dispose(); _timers.Clear(); }
        FlushStore();
        _engine = null;
        _menus.Clear();
        _events.Clear();
        _settings.Clear();
        lock (_visualizers) _visualizers.Clear();
        lock (_importers) _importers.Clear();
        lock (_links) _links.Clear();
        lock (_searches) _searches.Clear();
        _imageCache.Clear();
        _imagePixels = 0;
        _coverKey = null;
        if (Status == ModStatus.Running) Status = ModStatus.Off;
        _host.Changed(this);
    }

    public void Dispose() => Stop();

    // Runs on the mod's thread, later.
    public void Post(Action a)
    {
        try { _work?.Add(a); } catch (InvalidOperationException) { } // stopping
    }

    // Runs on the mod's thread and waits for it, at most waitMs: false if it didn't finish (the mod is busy).
    public bool Run(Action a, int waitMs)
    {
        if (_work == null || Status != ModStatus.Running) return false;
        var done = new ManualResetEventSlim();
        Post(() => { try { a(); } finally { done.Set(); } });
        return done.Wait(waitMs);
    }

    private void Boot()
    {
        _engine = new Engine(o =>
        {
            o.LimitMemory(512_000_000);
            o.TimeoutInterval(CallLimit);
            o.LimitRecursion(1000);
            o.CancellationToken(_stop!.Token);
            o.EnableModules(Folder, restrictToBasePath: true);
        });
        LoadStore();
        LoadSettingValues();
        LoadPlaces();
        var ump = MakeApi(_engine);
        AddVisuals(_engine, ump);
        AddFiles(_engine, ump);
        AddSounds(_engine, ump);
        AddNet(_engine, ump);
        AddSearch(_engine, ump);
        _engine.SetValue("ump", ump);
        _engine.SetValue("console", MakeConsole(_engine));
        AddTimers(_engine);
        try
        {
            _engine.Constraints.Reset();
            var exports = _engine.Modules.Import("./" + Manifest.Main.Replace('\\', '/'));
            _engine.Advanced.ProcessTasks();
            _stopFn = exports.Get("stop");
            var start = exports.Get("start");
            // Not through Call: an error in start stops the mod.
            if (start is Jint.Native.Function.Function)
            {
                _engine.Constraints.Reset();
                _engine.Invoke(start, _ump!);
                _engine.Advanced.ProcessTasks();
            }
            if (Status == ModStatus.Running) Write(L.T("Started."));
        }
        catch (Exception ex) { Fail("start", ex, stop: true); }
        _host.Changed(this);
    }

    private JsValue _stopFn = JsValue.Undefined;

    // Calls a function of the mod, with its limits; an error goes to its log, never further.
    private JsValue Call(string where, JsValue fn, params JsValue[] args)
    {
        if (_engine == null || fn is not Jint.Native.Function.Function) return JsValue.Undefined;
        try
        {
            _engine.Constraints.Reset();
            var r = _engine.Invoke(fn, args.Cast<object>().ToArray());
            _engine.Advanced.ProcessTasks();
            _errorsInARow = 0;
            return r;
        }
        catch (Exception ex) { Fail(where, ex); return JsValue.Undefined; }
    }

    private void Fail(string where, Exception ex, bool stop = false)
    {
        if (ex is OperationCanceledException || ex is ExecutionCanceledException) return;
        var text = ex switch
        {
            JavaScriptException js => $"{js.Message}{(js.Location.Start.Line > 0 ? $" ({js.Location.SourceFile} {js.Location.Start.Line}:{js.Location.Start.Column})" : "")}",
            TimeoutException => L.F("it took more than {0} seconds", CallLimit.TotalSeconds),
            MemoryLimitExceededException => L.T("it used too much memory"),
            RecursionDepthOverflowException => L.T("it called itself too deep"),
            ModException m => m.Message,
            _ => ex.Message,
        };
        Write(where.Length > 0 ? $"{L.T("Error")} ({where}): {text}" : $"{L.T("Error")}: {text}");
        AppPaths.AppendLog($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] mod {Id} {where}: {ex}\n\n");
        // Something wrong at every call: the mod is stopped rather than filling its log forever.
        if (stop || ++_errorsInARow >= 20)
        {
            Status = ModStatus.Failed;
            Error = text;
            _host.Changed(this);
            if (_thread != null && Thread.CurrentThread == _thread) _ = Task.Run(Stop);
        }
    }

    private void Write(string line)
    {
        lock (_log)
        {
            _log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
            if (_log.Count > 300) _log.RemoveRange(0, _log.Count - 300);
        }
        LogChanged?.Invoke();
    }
}
