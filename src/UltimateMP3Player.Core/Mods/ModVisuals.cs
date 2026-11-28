using System.Diagnostics;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;

namespace UltimateMP3Player.Core.Mods;

// A visualizer a mod added (ump.visualizers.add): on the song page (place "song", in place of the cover) or as the bars of the
// music (place "bars"). Which one shows is the user's choice, in the mod's page (AppSettings.SongScene / BarsScene).
public sealed class ModVisualizer
{
    internal ModVisualizer(ModRuntime mod, string id, string title, string place, JsValue canShow, JsValue open, JsValue frame, JsValue close)
    {
        Mod = mod;
        Id = id;
        Title = title;
        Place = place;
        CanShow = canShow;
        Open = open;
        Frame = frame;
        Close = close;
    }

    public ModRuntime Mod { get; }
    public string Id { get; }
    public string Title { get; }
    public string Place { get; }
    public string Key => Mod.Id + "/" + Id;
    internal JsValue CanShow { get; }
    internal JsValue Open { get; }
    internal JsValue Frame { get; }
    internal JsValue Close { get; }
    // fill: "page": over the whole song page, behind its words (f.stage is where the cover would be); press: taps on it.
    public bool Page { get; init; }
    internal JsValue Press { get; init; } = JsValue.Undefined;
    public bool CanPress => Press is Function;
    // A video under it (visualizers.video, a "page" one): the file and where the song is when it starts (ms), or null.
    public (string Path, double Offset)? Video { get; private set; }
    public event Action? VideoChanged;
    internal void SetVideo((string, double)? video)
    {
        if (Video == video) return;
        Video = video;
        VideoChanged?.Invoke();
    }
    // A button of the app's own over it, top left (visualizers.chip, a "page" one: osu!'s difficulty), its words or null; a
    // click calls the mod's chip().
    public string? Chip { get; private set; }
    public event Action? ChipChanged;
    internal JsValue OnChip { get; init; } = JsValue.Undefined;
    internal void SetChip(string? chip)
    {
        if (Chip == chip) return;
        Chip = chip;
        ChipChanged?.Invoke();
    }
    public void PressChip()
    {
        if (OnChip is Function) Mod.Post(() => Mod.CallChip(this));
    }
    public override string ToString() => Title;
}

// A visualizer open in a view: the view asks for frames (Request, never waiting), the mod draws them on its thread, the view
// draws the latest one (Latest). One frame late at most; a slow mod only shows fewer frames.
public sealed class ModScene : IDisposable
{
    private int _busy;
    private volatile DrawList? _latest;
    internal bool Opened, Closed;
    internal ModCanvasRecorder? Recorder;
    internal JsObject? Info;
    internal JsArray? Bands;
    internal string? SongId;
    internal long LastTicks;
    internal int Frames, Slow;
    internal JsObject? Stage;

    internal ModScene(ModVisualizer v) => Visualizer = v;

    public ModVisualizer Visualizer { get; }
    public DrawList? Latest => _latest;
    // A frame came: the view draws it (raised on the mod's thread).
    public event Action? Ready;

    public bool Request(ModFrameInfo info)
    {
        if (Closed || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;
        Visualizer.Mod.Post(() =>
        {
            try
            {
                if (Visualizer.Mod.RenderScene(this, info) is { } list)
                {
                    _latest = list;
                    Ready?.Invoke();
                }
            }
            finally { Volatile.Write(ref _busy, 0); }
        });
        return true;
    }

    // A tap or a click on it (only for one with press), in its units.
    public void Press(float x, float y)
    {
        if (!Closed && Visualizer.CanPress) Visualizer.Mod.Post(() => Visualizer.Mod.PressScene(this, x, y));
    }

    public void Dispose()
    {
        if (Closed) return;
        Closed = true;
        Visualizer.Mod.Post(() => Visualizer.Mod.CloseScene(this));
    }
}

// The time of the song for a frame: the engine's position moves in steps (a buffer at a time), so between them it's carried on
// by the clock, and pulled back gently towards the engine (a jump, a seek: at once).
public sealed class ModClock
{
    private double _base = -1, _pausedAt = double.NaN;
    private long _at;
    private bool _held;
    private int _far;

    public double Now(double engineMs, bool playing, double speed)
    {
        long t = Stopwatch.GetTimestamp();
        if (!playing || _base < 0)
        {
            // Paused: the time heard last stays. The engine's position jumps on at a pause (what its output still held, ~120 ms,
            // isn't taken off any more), and back when it plays again; it moving while paused is a seek or another song.
            if (_base < 0 || (!double.IsNaN(_pausedAt) && Math.Abs(engineMs - _pausedAt) > 1)) _base = engineMs;
            (_pausedAt, _at, _held) = (engineMs, t, true);
            return _base;
        }
        // (playing again: on from where it stopped, not from the paused position)
        if (_held) (_held, _at, _pausedAt) = (false, t, double.NaN);
        double predicted = _base + (t - _at) * 1000.0 / Stopwatch.Frequency * speed;
        double off = engineMs - predicted;
        if (Math.Abs(off) <= 80)
        {
            _far = 0;
            _base = predicted + off * 0.05;
        }
        // Far from the engine once or twice: the engine catching up (the first frames after a pause). For longer: a seek jumps,
        // less is caught up in a few frames (what a pause's fade and output skipped).
        else if (++_far < 3) _base = predicted;
        else _base = Math.Abs(off) > 300 ? engineMs : predicted + off * 0.25;
        _at = t;
        return _base;
    }
}

public sealed partial class ModRuntime
{
    private readonly List<ModVisualizer> _visualizers = new();
    private readonly Dictionary<string, JsValue> _imageCache = new();
    private long _imagePixels;
    private string? _coverKey;
    private const long MaxImagePixels = 64L * 1024 * 1024;

    public IReadOnlyList<ModVisualizer> Visualizers { get { lock (_visualizers) return _visualizers.ToList(); } }

    // Path2D, as in browsers: a JavaScript class over the app's paths (ModPath).
    private const string Prelude = """
        globalThis.Path2D = class Path2D {
          constructor(from) { __pathNew(this, from); }
          moveTo(x, y) { __path(this, 0, x, y); }
          lineTo(x, y) { __path(this, 1, x, y); }
          quadraticCurveTo(a, b, c, d) { __path(this, 2, a, b, c, d); }
          bezierCurveTo(a, b, c, d, e, f) { __path(this, 3, a, b, c, d, e, f); }
          arc(x, y, r, a0, a1, ccw) { __path(this, 4, x, y, r, r, 0, a0, a1, ccw ? 1 : 0); }
          ellipse(x, y, rx, ry, rot, a0, a1, ccw) { __path(this, 4, x, y, rx, ry, rot, a0, a1, ccw ? 1 : 0); }
          rect(x, y, w, h) { __path(this, 5, x, y, w, h); }
          roundRect(x, y, w, h, r) { __path(this, 6, x, y, w, h, typeof r === "number" ? r : 0); }
          closePath() { __path(this, 7); }
        };
        """;

    private void AddVisuals(Engine e, JsObject ump)
    {
        e.SetValue("__pathNew", new ClrFunction(e, "__pathNew", (_, a) =>
        {
            if (a.Length == 0 || a[0] is not ObjectInstance o) return JsValue.Undefined;
            var p = new ModPath();
            if (a.Length > 1 && a[1] is ObjectInstance from && ModCanvasRecorder.Paths.TryGetValue(from, out var src)) p.Segs.AddRange(src.Segs);
            ModCanvasRecorder.Paths.AddOrUpdate(o, p);
            return JsValue.Undefined;
        }));
        e.SetValue("__path", new ClrFunction(e, "__path", (_, a) =>
        {
            if (a.Length < 2 || a[0] is not ObjectInstance o || !ModCanvasRecorder.Paths.TryGetValue(o, out var p)) return JsValue.Undefined;
            float F(int i) => i < a.Length ? (float)TypeConverter.ToNumber(a[i]) : 0;
            switch ((int)F(1))
            {
                case 0: p.MoveTo(F(2), F(3)); break;
                case 1: p.LineTo(F(2), F(3)); break;
                case 2: p.QuadTo(F(2), F(3), F(4), F(5)); break;
                case 3: p.CubicTo(F(2), F(3), F(4), F(5), F(6), F(7)); break;
                case 4: p.Ellipse(F(2), F(3), F(4), F(5), F(6), F(7), F(8), F(9) != 0); break;
                case 5: p.Rect(F(2), F(3), F(4), F(5)); break;
                case 6: p.RoundRect(F(2), F(3), F(4), F(5), F(6)); break;
                case 7: p.Close(); break;
            }
            return JsValue.Undefined;
        }));
        e.Execute(Prelude);

        var visualizers = new JsObject(e);
        Fn(e, visualizers, "add", a =>
        {
            if (Arg(a, 0) is not JsObject o || o.Get("frame") is not Function)
                throw new JavaScriptException("visualizers.add needs { id, title, place, frame }.");
            var id = TypeConverter.ToString(o.Get("id"));
            var place = Nil(o.Get("place")) ? "song" : TypeConverter.ToString(o.Get("place"));
            if (id.Length is 0 or > 40) throw new JavaScriptException("A visualizer needs an id (at most 40 characters).");
            if (place is not ("song" or "bars")) throw new JavaScriptException("A visualizer's place is \"song\" or \"bars\".");
            var title = Short(Nil(o.Get("title")) ? id : TypeConverter.ToString(o.Get("title")), 60);
            var fill = Nil(o.Get("fill")) ? "" : TypeConverter.ToString(o.Get("fill"));
            if (fill is not ("" or "cover" or "page")) throw new JavaScriptException("A visualizer's fill is \"cover\" or \"page\".");
            var v = new ModVisualizer(this, id, title, place, o.Get("canShow"), o.Get("open"), o.Get("frame"), o.Get("close"))
            {
                Page = place == "song" && fill == "page",
                Press = o.Get("press"),
                OnChip = o.Get("chip"),
            };
            lock (_visualizers)
            {
                _visualizers.RemoveAll(x => x.Id == id);
                if (_visualizers.Count >= 20) throw new JavaScriptException("At most 20 visualizers per mod.");
                _visualizers.Add(v);
            }
            _host.Changed(this);
            return JsValue.Undefined;
        });
        // One of its visualizers chosen, as its switch in the mod's page would (after asking the user: ump.ui.confirm), and whether it is.
        ModVisualizer Own(JsValue[] a)
        {
            var id = Str(a, 0);
            lock (_visualizers) return _visualizers.FirstOrDefault(v => v.Id == id) ?? throw new JavaScriptException("No visualizer " + id + " (visualizers.add first).");
        }
        Fn(e, visualizers, "show", a => { _host.Choose(Own(a), true); return JsValue.Undefined; });
        Fn(e, visualizers, "shown", a => _host.IsChosen(Own(a)));
        // A video under a "page" visualizer (an osu! beatmap's): video(id, path, offsetMs), null for none. f.video says when it shows.
        Fn(e, visualizers, "video", a =>
        {
            var v = Own(a);
            if (!v.Page) throw new JavaScriptException("Only a visualizer with fill: \"page\" has a video under it.");
            if (Nil(Arg(a, 1))) { v.SetVideo(null); return JsValue.Undefined; }
            var path = Readable(Str(a, 1));
            double offset = Arg(a, 2).IsNumber() ? TypeConverter.ToNumber(Arg(a, 2)) : 0;
            v.SetVideo(File.Exists(path) ? (path, double.IsFinite(offset) ? offset : 0) : null);
            return JsValue.Undefined;
        });
        // chip(id, words): the app's own button over a "page" visualizer (top left, in the app's look), null for none; a click
        // calls its chip() given to add.
        Fn(e, visualizers, "chip", a =>
        {
            var v = Own(a);
            if (!v.Page) throw new JavaScriptException("Only a visualizer with fill: \"page\" has a chip.");
            v.SetChip(Nil(Arg(a, 1)) ? null : Short(Str(a, 1), 80) is { Length: > 0 } s ? s : null);
            return JsValue.Undefined;
        });
        ump.Set("visualizers", visualizers);

        var images = new JsObject(e);
        Fn(e, images, "load", a =>
        {
            var path = Str(a, 0);
            // load(path, { size, blur }): at most size pixels a side, blurred by blur pixels (of that size), made once (a map's background).
            int size = 0;
            float blur = 0;
            if (Arg(a, 1) is JsObject options)
            {
                if (options.Get("size") is { } s && s.IsNumber()) size = (int)Math.Clamp(TypeConverter.ToNumber(s), 0, 8192);
                if (options.Get("blur") is { } b && b.IsNumber()) blur = (float)Math.Clamp(TypeConverter.ToNumber(b), 0, 64);
            }
            var how = size > 0 || blur > 0 ? FormattableString.Invariant($":{size}:{blur:0.#}") : "";
            // A picture of what the user gave the mod (an osu! skin's folder, a map's background): by its full path.
            if (Path.IsPathRooted(path))
            {
                var full = Readable(path);
                if (!File.Exists(full)) return JsValue.Null;
                return Image(e, "file:" + full + ":" + File.GetLastWriteTimeUtc(full).Ticks + how, full, size, blur) ?? JsValue.Null;
            }
            path = path.Replace('\\', '/');
            if (!ModPackage.IsSafePath(path)) throw new JavaScriptException("images.load: a path inside the mod, like \"skin/circle.png\", or a full path the user gave it.");
            return Image(e, "mod:" + path + how, Path.Combine(Folder, path), size, blur) ?? throw new JavaScriptException("images.load: no picture at " + path);
        });
        // A picture no longer wanted (the last song's map background): out of the cache and the pictures' budget.
        Fn(e, images, "free", a =>
        {
            if (Arg(a, 0) is not JsObject o) return JsValue.Undefined;
            foreach (var (key, value) in _imageCache.Where(kv => ReferenceEquals(kv.Value, o)).ToList())
            {
                _imageCache.Remove(key);
                _imagePixels -= (long)(TypeConverter.ToNumber(o.Get("width")) * TypeConverter.ToNumber(o.Get("height")));
                if (key == _coverKey) _coverKey = null;
            }
            return JsValue.Undefined;
        });
        // The cover of the song playing (null without one).
        Fn(e, images, "cover", a =>
        {
            if (_host.Song is not { HasCover: true } s) return JsValue.Null;
            var key = "cover:" + s.Id + ":" + s.CoverVersion;
            // Only the playing song's cover is kept: the one before leaves the cache and the pictures' budget.
            if (_coverKey != key && _coverKey != null && _imageCache.Remove(_coverKey, out var old) && old is JsObject was)
                _imagePixels -= (long)(TypeConverter.ToNumber(was.Get("width")) * TypeConverter.ToNumber(was.Get("height")));
            _coverKey = key;
            return Image(e, key, AppPaths.TrackCover(s.Id)) ?? JsValue.Null;
        });
        ump.Set("images", images);

        Fn(e, (JsObject)ump.Get("player"), "wave", a =>
        {
            // The song's waveform (what the song bar draws): its loudness along it, 0..1.
            if (_host.Song?.Wave is not { Length: > 0 } wave) return JsValue.Null;
            return new JsArray(e, wave.Select(b => (JsValue)(b / 255.0)).ToArray());
        });
    }

    private JsValue? Image(Engine e, string key, string file, int size = 0, float blur = 0)
    {
        if (_imageCache.TryGetValue(key, out var known)) return known;
        if (!File.Exists(file)) return null;
        var img = (size > 0 || blur > 0 ? _host.LoadImage(file, size, blur) : _host.LoadImage(file))
            ?? throw new JavaScriptException("Not a picture this app can read: " + Path.GetFileName(file));
        long pixels = (long)img.Width * img.Height;
        if (_imagePixels + pixels > MaxImagePixels)
            throw new JavaScriptException("The mod's pictures are too big all together (64 megapixels).");
        _imagePixels += pixels;
        var o = new JsObject(e);
        o.Set("width", img.Width);
        o.Set("height", img.Height);
        ModCanvasRecorder.Images.AddOrUpdate(o, img);
        if (_imageCache.Count > 2000) { _imageCache.Clear(); _imagePixels = 0; _coverKey = null; }
        _imageCache[key] = o;
        return o;
    }

    internal float MeasureText(string text, float size, bool bold) => _host.MeasureText(text, size, bold);

    // ---- scenes (from the views)
    public ModScene OpenScene(ModVisualizer v) => new(v);

    // Can it show this song (canShow, on the mod's thread)? The answer comes on that thread.
    public void AskCanShow(ModVisualizer v, ModSong? song, Action<bool> answer) => Post(() =>
    {
        if (_engine == null) { answer(false); return; }
        answer(v.CanShow is not Function || TypeConverter.ToBoolean(Call("canShow", v.CanShow, SongJs(_engine, song, Granted.Contains(ModPermissions.LibraryRead)))));
    });

    internal DrawList? RenderScene(ModScene scene, ModFrameInfo f)
    {
        var e = _engine;
        if (e == null || scene.Closed || Status != ModStatus.Running) return null;
        var v = scene.Visualizer;
        if (!scene.Opened)
        {
            scene.Opened = true;
            scene.Recorder = new ModCanvasRecorder(e, this);
            scene.Info = new JsObject(e);
            Call("open", v.Open);
        }
        var info = scene.Info!;
        long now = Stopwatch.GetTimestamp();
        info.Set("time", f.Time);
        info.Set("duration", f.Duration);
        info.Set("playing", f.Playing);
        info.Set("speed", f.Speed);
        info.Set("width", f.Width);
        info.Set("height", f.Height);
        info.Set("scale", f.Scale);
        info.Set("place", f.Place);
        info.Set("accent", CssColor.Of(f.Accent));
        info.Set("text", CssColor.Of(f.Text));
        info.Set("frame", scene.Frames);
        // (a "page" one: where the cover would be, for what belongs there; the video under it showing)
        scene.Stage ??= new JsObject(e);
        bool stage = f.StageWidth > 0 && f.StageHeight > 0;
        scene.Stage.Set("x", stage ? f.StageX : 0);
        scene.Stage.Set("y", stage ? f.StageY : 0);
        scene.Stage.Set("width", stage ? f.StageWidth : f.Width);
        scene.Stage.Set("height", stage ? f.StageHeight : f.Height);
        info.Set("stage", scene.Stage);
        info.Set("video", f.Video);
        info.Set("below", f.Below);
        info.Set("dt", scene.LastTicks == 0 ? 0 : (now - scene.LastTicks) * 1000.0 / Stopwatch.Frequency);
        scene.LastTicks = now;
        if (f.Bands is { } bands)
        {
            if (scene.Bands == null || scene.Bands.Length != bands.Length)
            {
                scene.Bands = new JsArray(e, bands.Select(b => (JsValue)b).ToArray());
                info.Set("bands", scene.Bands);
            }
            else for (uint i = 0; i < bands.Length; i++) scene.Bands[i] = bands[i];
        }
        var song = _host.Song;
        if (song?.Id != scene.SongId)
        {
            scene.SongId = song?.Id;
            info.Set("song", SongJs(e, song, Granted.Contains(ModPermissions.LibraryRead)));
            info.Set("bpm", song?.Bpm is { } bpm ? bpm : JsValue.Null);
            info.Set("beatOffset", song?.BeatOffset is { } off ? off : JsValue.Null);
        }
        var rec = scene.Recorder!;
        rec.Begin(f.Width, f.Height, scene.Frames++);
        long start = Stopwatch.GetTimestamp();
        Call("frame", v.Frame, rec.Ctx, info);
        var ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        // Said once when it can't keep up (the UI doesn't wait: it only gets fewer frames).
        if (ms > 33 && ++scene.Slow == 60) Write(L.F("“{0}” takes {1:0} ms a frame: it can't keep up with the screen.", v.Title, ms));
        return rec.End();
    }

    internal void CloseScene(ModScene scene)
    {
        if (scene.Opened && _engine != null) Call("close", scene.Visualizer.Close);
    }

    internal void CallChip(ModVisualizer v)
    {
        if (_engine != null && Status == ModStatus.Running) Call("chip", v.OnChip);
    }

    internal void PressScene(ModScene scene, float x, float y)
    {
        if (_engine != null && !scene.Closed && Status == ModStatus.Running) Call("press", scene.Visualizer.Press, x, y);
    }
}
