using System.Text.Json;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Json;
using Jint.Runtime;
using Jint.Runtime.Interop;
using JsJson = Jint.Native.Json.JsonSerializer;

namespace UltimateMP3Player.Core.Mods;

// A setting a mod asked for (ump.settings.define): drawn by the app with its own controls, the value kept for the mod.
public sealed class ModSetting
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "switch"; // switch, slider, choice, text, button, info
    public string Title { get; init; } = "";
    public string? Hint { get; init; }
    public double Min { get; init; }
    public double Max { get; init; } = 1;
    public double Step { get; init; }
    public IReadOnlyList<(string Value, string Text)> Choices { get; init; } = Array.Empty<(string, string)>();
    public object? Default { get; init; }
}

// An entry a mod put in the songs' menus (under More).
public sealed record ModMenuEntry(int Id, string Title, JsValue When, JsValue Run);

// The `ump` object a mod's script sees (its errors for the mod's author stay in English, like the API): only what's here, nothing of .NET (no AllowClr). Every function runs on the mod's thread.
public sealed partial class ModRuntime
{
    public static readonly string[] Features = { "settings", "store", "events", "menus.song", "player", "library", "ui", "timers", "visualizers", "visualizers.show", "canvas", "images",
        "files", "zip", "library.add", "playlists", "importers", "store.song",
        // (sounds, the whole page, sites and links)
        "visualizers.page", "visualizers.press", "visualizers.video", "images.options", "sounds", "files.known", "library.find", "library.replace",
        "net", "links", "prefs", "canvas.origin", "visualizers.chip", "ui.pick", "search",
        // (beginLayer/endLayer, filter = "blur()", the other globalCompositeOperation names)
        "canvas.layers", "canvas.blur", "canvas.composite" };

    private JsValue? _ump;
    private readonly Dictionary<string, List<JsValue>> _events = new();
    private readonly List<ModMenuEntry> _menus = new();
    private readonly List<ModSetting> _settings = new();
    private Dictionary<string, object?> _values = new();
    private readonly List<JsValue> _settingsWatchers = new();
    private Dictionary<string, string> _store = new();
    private bool _storeDirty, _settingsDirty;
    private readonly Dictionary<int, Timer> _timers = new();
    private int _nextId;

    public IReadOnlyList<ModSetting> Settings { get { lock (_settings) return _settings.ToList(); } }
    public bool HasSongEntries { get { lock (_menus) return _menus.Count > 0; } }

    private static void Fn(Engine e, JsObject o, string name, Func<JsValue[], JsValue> f) =>
        o.Set(name, new ClrFunction(e, name, (_, a) => f(a)));

    private static string Str(JsValue[] a, int i) => i < a.Length && !Nil(a[i]) ? TypeConverter.ToString(a[i]) : "";
    private static JsValue Arg(JsValue[] a, int i) => i < a.Length ? a[i] : JsValue.Undefined;
    private static bool Nil(JsValue v) => v.Type is Types.Null or Types.Undefined;
    private static JsValue Of(string? s) => s == null ? JsValue.Null : new JsString(s);

    private void Need(string permission)
    {
        if (!Granted.Contains(permission)) throw new JavaScriptException($"This needs the permission \"{permission}\" in mod.json.");
    }

    private JsObject MakeApi(Engine e)
    {
        var ump = new JsObject(e);
        ump.Set("api", ModManifest.ApiVersion);
        ump.Set("version", _host.AppVersion);
        ump.Set("language", L.English ? "en" : "it");
        ump.Set("platform", OperatingSystem.IsAndroid() ? "android" : OperatingSystem.IsIOS() ? "ios" : OperatingSystem.IsMacOS() ? "macos"
            : OperatingSystem.IsWindows() ? "windows" : "linux");
        ump.Set("id", Id);
        Fn(e, ump, "log", a => { Write(string.Join(" ", a.Select(Show))); return JsValue.Undefined; });
        Fn(e, ump, "t", a => Manifest.Translate(Str(a, 0)));
        Fn(e, ump, "has", a => Str(a, 0) switch
        {
            "canvas.blend" => _host.CanBlend,
            "files.folders" => _host.CanPickFolders,
            "realm" => Realm != null,
            var f => Features.Contains(f),
        });

        var settings = new JsObject(e);
        Fn(e, settings, "define", a => { DefineSettings(e, Arg(a, 0)); return JsValue.Undefined; });
        Fn(e, settings, "get", a => ToJs(e, SettingValue(Str(a, 0))));
        Fn(e, settings, "set", a => { SetSettingValue(Str(a, 0), FromJs(Arg(a, 1)), fromMod: true); return JsValue.Undefined; });
        Fn(e, settings, "onChange", a => { if (Arg(a, 0) is Function f) _settingsWatchers.Add(f); return JsValue.Undefined; });
        ump.Set("settings", settings);

        var store = new JsObject(e);
        Fn(e, store, "get", a => _store.TryGetValue(Str(a, 0), out var json) ? new JsonParser(e).Parse(json) : JsValue.Undefined);
        Fn(e, store, "set", a => { StoreSet(e, Str(a, 0), Arg(a, 1)); return JsValue.Undefined; });
        Fn(e, store, "remove", a => { _storeDirty |= _store.Remove(Str(a, 0)); return JsValue.Undefined; });
        Fn(e, store, "keys", a => new JsArray(e, _store.Keys.Select(k => (JsValue)new JsString(k)).ToArray()));
        ump.Set("store", store);

        var events = new JsObject(e);
        Fn(e, events, "on", a =>
        {
            var name = Str(a, 0);
            if (Arg(a, 1) is not Function f) return JsValue.Undefined;
            if (name is "songAdded" or "songRemoved") Need(ModPermissions.LibraryRead);
            lock (_events) { if (!_events.TryGetValue(name, out var list)) _events[name] = list = new(); list.Add(f); }
            return JsValue.Undefined;
        });
        ump.Set("events", events);

        var menus = new JsObject(e);
        var song = new JsObject(e);
        Fn(e, song, "add", a =>
        {
            if (Arg(a, 0) is not JsObject o) throw new JavaScriptException("menus.song.add needs { title, run }.");
            var title = TypeConverter.ToString(o.Get("title"));
            if (title.Length == 0 || o.Get("run") is not Function) throw new JavaScriptException("menus.song.add needs { title, run }.");
            var entry = new ModMenuEntry(++_nextId, title.Length > 60 ? title[..60] : title, o.Get("when"), o.Get("run"));
            lock (_menus) _menus.Add(entry);
            _host.Changed(this);
            return entry.Id;
        });
        Fn(e, song, "remove", a => { lock (_menus) _menus.RemoveAll(m => m.Id == (int)TypeConverter.ToNumber(Arg(a, 0))); return JsValue.Undefined; });
        menus.Set("song", song);
        ump.Set("menus", menus);

        var player = new JsObject(e);
        Fn(e, player, "song", a => SongJs(e, _host.Song, full: Granted.Contains(ModPermissions.LibraryRead)));
        Fn(e, player, "position", a => _host.Position);
        Fn(e, player, "playing", a => _host.Playing);
        foreach (var action in new[] { "play", "pause", "next", "previous" })
            Fn(e, player, action, a => { Need(ModPermissions.PlayerControl); _host.Control(action); return JsValue.Undefined; });
        ump.Set("player", player);

        var library = new JsObject(e);
        Fn(e, library, "songs", a =>
        {
            Need(ModPermissions.LibraryRead);
            return new JsArray(e, _host.Songs().Select(s => SongJs(e, s, true)).ToArray());
        });
        Fn(e, library, "get", a =>
        {
            Need(ModPermissions.LibraryRead);
            var id = Str(a, 0);
            return SongJs(e, _host.Songs().FirstOrDefault(s => s.Id == id), true);
        });
        ump.Set("library", library);

        var ui = new JsObject(e);
        Fn(e, ui, "toast", a => { _host.Toast(this, Short(Str(a, 0), 200)); return JsValue.Undefined; });
        Fn(e, ui, "confirm", a => Promise(e, _host.ConfirmAsync(this, Short(Str(a, 0), 80), Short(Str(a, 1), 1000), Short(Str(a, 2), 30) is { Length: > 0 } ok ? ok : L.T("OK")), b => b));
        Fn(e, ui, "choose", a =>
        {
            var options = Arg(a, 2) is JsArray arr ? arr.Select(v => Short(TypeConverter.ToString(v), 80)).Take(20).ToArray() : Array.Empty<string>();
            if (options.Length == 0) throw new JavaScriptException("ui.choose needs a list of answers.");
            return Promise(e, _host.ChooseAsync(this, Short(Str(a, 0), 80), Short(Str(a, 1), 1000), options), i => i);
        });
        // A list to choose one from (strings, or { title, hint }), the one at `selected` ticked.
        Fn(e, ui, "list", a =>
        {
            var items = Arg(a, 1) is JsArray arr
                ? arr.Take(500).Select(v => v is JsObject o
                    ? (Short(TypeConverter.ToString(o.Get("title")), 120), Nil(o.Get("hint")) ? null : Short(TypeConverter.ToString(o.Get("hint")), 200))
                    : (Short(TypeConverter.ToString(v), 120), (string?)null)).ToList()
                : new List<(string, string?)>();
            if (items.Count == 0) throw new JavaScriptException("ui.list needs a list.");
            int selected = Arg(a, 2).IsNumber() ? (int)TypeConverter.ToNumber(Arg(a, 2)) : -1;
            return Promise(e, _host.ListAsync(this, Short(Str(a, 0), 80), items, selected), i => i);
        });
        ump.Set("ui", ui);
        _ump = ump;
        return ump;
    }

    private static string Short(string s, int max) => s.Length > max ? s[..max] : s;

    private void StoreSet(Engine e, string key, JsValue value)
    {
        var json = new JsJson(e).Serialize(value).ToString();
        if (json == "undefined") { _store.Remove(key); }
        else
        {
            if (_store.Sum(p => p.Key.Length + p.Value.Length) + key.Length + json.Length > 20_000_000)
                throw new JavaScriptException("The mod's store is full (20 MB).");
            _store[key] = json;
        }
        _storeDirty = true;
    }

    // A promise the mod can await, settled on its own thread when the app answers. What follows the await runs then, with
    // its own time limit (counted from there, not from the call that made the promise); a failed task rejects it.
    private JsValue Promise<T>(Engine e, Task<T> task, Func<T, JsValue> result)
    {
        var (promise, resolve, reject) = e.Advanced.RegisterPromise();
        task.ContinueWith(t => Post(() =>
        {
            if (_engine != e) return;
            try
            {
                e.Constraints.Reset();
                if (t.IsCompletedSuccessfully) resolve(result(t.Result));
                else reject(new JsString(t.Exception?.InnerException is { } ex ? ex.Message : "cancelled"));
                e.Advanced.ProcessTasks();
            }
            catch (Exception ex) { Fail("await", ex); }
        }));
        return promise;
    }

    private JsObject MakeConsole(Engine e)
    {
        var console = new JsObject(e);
        foreach (var name in new[] { "log", "info", "warn", "error", "debug" })
            Fn(e, console, name, a => { Write(string.Join(" ", a.Select(Show))); return JsValue.Undefined; });
        return console;
    }

    private string Show(JsValue v)
    {
        if (v.IsString() || v.IsNumber() || v.IsBoolean() || Nil(v)) return TypeConverter.ToString(v);
        try { return new JsJson(_engine!).Serialize(v).ToString(); } catch { return TypeConverter.ToString(v); }
    }

    // setTimeout and friends: a timer posts the call to the mod's thread.
    private void AddTimers(Engine e)
    {
        JsValue Add(JsValue[] a, bool repeat)
        {
            if (Arg(a, 0) is not Function f) return JsValue.Undefined;
            var ms = Math.Max(repeat ? 16 : 0, (int)Math.Min(int.MaxValue, TypeConverter.ToNumber(Arg(a, 1)) is var n && double.IsFinite(n) ? n : 0));
            var extra = a.Skip(2).ToArray();
            lock (_timers)
            {
                if (_timers.Count >= 200) throw new JavaScriptException("Too many timers.");
                var id = ++_nextId;
                _timers[id] = new Timer(_ => Post(() =>
                {
                    if (!repeat) lock (_timers) { if (_timers.Remove(id, out var t)) t.Dispose(); else return; }
                    else lock (_timers) { if (!_timers.ContainsKey(id)) return; }
                    Call(repeat ? "setInterval" : "setTimeout", f, extra);
                }), null, ms, repeat ? ms : Timeout.Infinite);
                return id;
            }
        }
        JsValue Clear(JsValue[] a)
        {
            lock (_timers) if (_timers.Remove((int)TypeConverter.ToNumber(Arg(a, 0)), out var t)) t.Dispose();
            return JsValue.Undefined;
        }
        e.SetValue("setTimeout", new ClrFunction(e, "setTimeout", (_, a) => Add(a, false)));
        e.SetValue("setInterval", new ClrFunction(e, "setInterval", (_, a) => Add(a, true)));
        e.SetValue("clearTimeout", new ClrFunction(e, "clearTimeout", (_, a) => Clear(a)));
        e.SetValue("clearInterval", new ClrFunction(e, "clearInterval", (_, a) => Clear(a)));
    }

    private static JsValue SongJs(Engine e, ModSong? s, bool full)
    {
        if (s == null) return JsValue.Null;
        var o = new JsObject(e);
        o.Set("id", s.Id);
        o.Set("title", s.Title);
        o.Set("artist", Of(s.Artist));
        o.Set("album", Of(s.Album));
        o.Set("year", Of(s.Year));
        o.Set("duration", s.Duration);
        o.Set("bpm", s.Bpm is { } b ? b : JsValue.Null);
        o.Set("site", Of(s.Site));
        o.Set("url", Of(s.Url));
        o.Set("added", s.Added.ToString("o"));
        o.Set("hasCover", s.HasCover);
        // Where the file is on this device only with the library permission.
        if (full) o.Set("path", Of(s.Path));
        return o;
    }

    // ---- events, from the app (any thread)
    public void Emit(string name, object? arg = null)
    {
        List<JsValue>? list;
        lock (_events) list = _events.TryGetValue(name, out var l) ? l.ToList() : null;
        if (list == null) return;
        void Send()
        {
            var e = _engine;
            if (e == null) return;
            JsValue value = arg switch
            {
                null => JsValue.Null,
                bool b => b,
                ModSong s => SongJs(e, s, Granted.Contains(ModPermissions.LibraryRead)),
                _ => new JsString(arg.ToString()!),
            };
            foreach (var f in list) Call("event " + name, f, value);
        }
        if (_thread != null && Thread.CurrentThread == _thread) Send(); else Post(Send);
    }

    // ---- song menu entries: which of this mod's entries a song gets (asked by the UI, at most waitMs, or none)
    public IReadOnlyList<ModMenuEntry> SongEntries(ModSong song, int waitMs)
    {
        List<ModMenuEntry> all;
        lock (_menus) all = _menus.ToList();
        if (all.Count == 0) return all;
        var shown = new List<ModMenuEntry>();
        if (all.All(m => m.When is not Function)) return all;
        Run(() =>
        {
            var js = SongJs(_engine!, song, Granted.Contains(ModPermissions.LibraryRead));
            foreach (var m in all)
                if (m.When is not Function || TypeConverter.ToBoolean(Call("menu when", m.When, js))) lock (shown) shown.Add(m);
        }, waitMs);
        lock (shown) return shown.ToList();
    }

    public void RunEntry(ModMenuEntry entry, ModSong song) =>
        Post(() => Call("menu " + entry.Title, entry.Run, SongJs(_engine!, song, Granted.Contains(ModPermissions.LibraryRead))));

    // ---- settings
    private void DefineSettings(Engine e, JsValue list)
    {
        if (list is not JsArray arr) throw new JavaScriptException("settings.define needs a list.");
        var defs = new List<ModSetting>();
        foreach (var v in arr)
        {
            if (v is not JsObject o) continue;
            var type = TypeConverter.ToString(o.Get("type"));
            // (heading: a title over the settings after it, in a card of their own)
            if (type is not ("switch" or "slider" or "choice" or "text" or "button" or "info" or "heading"))
                throw new JavaScriptException($"Unknown setting type: {type}");
            var id = Nil(o.Get("id")) ? "" : TypeConverter.ToString(o.Get("id"));
            if (id.Length == 0 && type is not ("info" or "heading")) throw new JavaScriptException("Every setting needs an id.");
            var choices = o.Get("choices") is JsArray c
                ? c.Select(x => x is JsObject co ? (TypeConverter.ToString(co.Get("value")), TypeConverter.ToString(co.Get("title")))
                    : (TypeConverter.ToString(x), TypeConverter.ToString(x))).Take(400).Select(c => (c.Item1, Short(c.Item2, 80))).ToList()
                : new List<(string, string)>();
            double Num(string name, double fallback) => o.Get(name) is { } n && n.IsNumber() ? TypeConverter.ToNumber(n) : fallback;
            defs.Add(new ModSetting
            {
                Id = id,
                Type = type,
                Title = Short(TypeConverter.ToString(o.Get("title")), 80),
                Hint = Nil(o.Get("hint")) ? null : Short(TypeConverter.ToString(o.Get("hint")), 400),
                Min = Num("min", 0),
                Max = Num("max", 1),
                Step = Num("step", 0),
                Choices = choices,
                Default = FromJs(o.Get("default")) ?? type switch
                {
                    "switch" => false,
                    "slider" => Num("min", 0),
                    "choice" => choices.Count > 0 ? choices[0].Item1 : null,
                    "text" => "",
                    _ => null,
                },
            });
        }
        lock (_settings) { _settings.Clear(); _settings.AddRange(defs.Take(60)); }
        _host.Changed(this);
    }

    public object? SettingValue(string id)
    {
        lock (_settings)
        {
            var def = _settings.FirstOrDefault(s => s.Id == id);
            return _values.TryGetValue(id, out var v) && v != null ? v : def?.Default;
        }
    }

    // From the app (a control moved) or from the mod (ump.settings.set). Buttons only tell the mod they were pressed.
    public void SetSettingValue(string id, object? value, bool fromMod = false)
    {
        ModSetting? def;
        lock (_settings) def = _settings.FirstOrDefault(s => s.Id == id);
        if (def == null) return;
        if (def.Type != "button")
        {
            lock (_settings) { _values[id] = value; _settingsDirty = true; }
            // A slider moves many times a second: saved with the store's timer while the mod runs, now when it's off.
            if (_thread == null) FlushSettings();
        }
        if (fromMod) { _host.Changed(this); return; }
        Post(() =>
        {
            var e = _engine;
            if (e == null) return;
            foreach (var f in _settingsWatchers.ToList()) Call("settings.onChange", f, id, ToJs(e, value));
        });
    }

    private void LoadSettingValues()
    {
        _values = new();
        try
        {
            var file = Path.Combine(DataDir, "settings.json");
            if (!File.Exists(file)) return;
            foreach (var (k, v) in JsonStore.Load<Dictionary<string, JsonElement>>(file))
                _values[k] = v.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Number => v.GetDouble(),
                    JsonValueKind.String => v.GetString(),
                    _ => null,
                };
        }
        catch (Exception ex) { Write(ex.Message); }
    }

    private static object? FromJs(JsValue v) =>
        v.IsBoolean() ? TypeConverter.ToBoolean(v) : v.IsNumber() ? TypeConverter.ToNumber(v) : v.IsString() ? TypeConverter.ToString(v) : null;

    private static JsValue ToJs(Engine e, object? v) => v switch
    {
        bool b => b,
        double d => d,
        string s => s,
        _ => JsValue.Undefined,
    };

    // ---- store: the mod's own keys and values (JSON), in mods-data/<id>/store.json
    private void LoadStore()
    {
        try
        {
            var file = Path.Combine(DataDir, "store.json");
            _store = File.Exists(file) ? JsonStore.Load<Dictionary<string, string>>(file) : new();
        }
        catch (Exception ex) { _store = new(); Write(ex.Message); }
        _storeDirty = false;
        // Saved a second after it changes (at most every 5 s while it keeps changing: an import writes for minutes), and when the mod stops.
        lock (_timers) _timers[-1] = new Timer(_ => Post(() => FlushStore(force: false)), null, 1000, 1000);
    }

    private void FlushSettings()
    {
        Dictionary<string, object?> copy;
        lock (_settings)
        {
            if (!_settingsDirty) return;
            copy = new(_values);
            _settingsDirty = false;
        }
        try
        {
            Directory.CreateDirectory(DataDir);
            JsonStore.Save(Path.Combine(DataDir, "settings.json"), copy);
        }
        catch (Exception ex) { Write(ex.Message); }
    }

    private long _storeSavedAt;

    private void FlushStore(bool force = true)
    {
        FlushSettings();
        if (!_storeDirty || !force && Environment.TickCount64 - _storeSavedAt < 5000) return;
        try
        {
            Directory.CreateDirectory(DataDir);
            JsonStore.Save(Path.Combine(DataDir, "store.json"), _store);
            _storeDirty = false;
            _storeSavedAt = Environment.TickCount64;
        }
        catch (Exception ex) { Write(ex.Message); }
    }
}
