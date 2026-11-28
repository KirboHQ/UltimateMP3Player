using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;

namespace UltimateMP3Player.Core.Mods;

// What a mod finds for the search box's words (ump.search.add: osu! beatmaps), shown in the app's Search under its title. Each
// result is a link the mod takes itself (links.add): opened, it goes back to the mod (a beatmap downloaded and brought in).
public sealed record ModSearch(ModRuntime Mod, string Id, string Title, JsValue Run);

public sealed record ModSearchHit(string Title, string? Artist, string? Hint, string Url, string? Picture, double? Duration);

public sealed partial class ModRuntime
{
    private readonly List<ModSearch> _searches = new();
    public IReadOnlyList<ModSearch> Searches { get { lock (_searches) return _searches.ToList(); } }

    private void AddSearch(Engine e, JsObject ump)
    {
        var search = new JsObject(e);
        // add({ id, title, run(words) → [{ title, artist, hint, url, picture, duration (s) }] or a promise of it }): words, or a link
        // the mod takes, typed in the search box.
        Fn(e, search, "add", a =>
        {
            if (Arg(a, 0) is not JsObject o || o.Get("run") is not Function) throw new JavaScriptException("search.add needs { id, title, run(words) }.");
            Need(ModPermissions.Internet);
            var id = Nil(o.Get("id")) ? "" : TypeConverter.ToString(o.Get("id"));
            if (id.Length is 0 or > 40) throw new JavaScriptException("A search needs an id (at most 40 characters).");
            var title = Short(Nil(o.Get("title")) ? Manifest.Name : TypeConverter.ToString(o.Get("title")), 40);
            lock (_searches)
            {
                _searches.RemoveAll(s => s.Id == id);
                if (_searches.Count >= 3) throw new JavaScriptException("At most 3 searches per mod.");
                _searches.Add(new ModSearch(this, id, title, o.Get("run")));
            }
            return JsValue.Undefined;
        });
        Fn(e, search, "remove", a => { lock (_searches) _searches.RemoveAll(s => s.Id == Str(a, 0)); return JsValue.Undefined; });
        ump.Set("search", search);

        // pick(title, message, [text | { title, hint }], selected, ok): one of them chosen, then confirmed with the ok button (as
        // the app's own "Delete…" asks) → its index, or -1.
        Fn(e, (JsObject)ump.Get("ui"), "pick", a =>
        {
            var items = Arg(a, 2) is JsArray arr
                ? arr.Take(50).Select(v => v is JsObject o
                    ? (Short(TypeConverter.ToString(o.Get("title")), 120), Nil(o.Get("hint")) ? null : Short(TypeConverter.ToString(o.Get("hint")), 300))
                    : (Short(TypeConverter.ToString(v), 120), (string?)null)).ToList()
                : new List<(string, string?)>();
            if (items.Count == 0) throw new JavaScriptException("ui.pick needs a list.");
            int selected = Arg(a, 3).IsNumber() ? Math.Clamp((int)TypeConverter.ToNumber(Arg(a, 3)), 0, items.Count - 1) : 0;
            var ok = Short(Str(a, 4), 30) is { Length: > 0 } s ? s : L.T("OK");
            return Promise(e, _host.PickAsync(this, Short(Str(a, 0), 80), Short(Str(a, 1), 1000), items, selected, ok), i => i);
        });
    }

    // The mod's results for these words (at most 20), on its thread; nothing if it fails or takes too long.
    public Task<List<ModSearchHit>> SearchAsync(ModSearch s, string words, CancellationToken ct)
    {
        var done = new TaskCompletionSource<List<ModSearchHit>>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => done.TrySetResult(new()));
        Post(() =>
        {
            var e = _engine;
            if (e == null || Status != ModStatus.Running) { done.TrySetResult(new()); return; }
            var ok = new ClrFunction(e, "found", (_, a) => { done.TrySetResult(Hits(a.Length > 0 ? a[0] : JsValue.Undefined)); return JsValue.Undefined; });
            var fail = new ClrFunction(e, "failed", (_, a) => { if (a.Length > 0) Write("search: " + Show(a[0])); done.TrySetResult(new()); return JsValue.Undefined; });
            var r = Call("search " + s.Id, s.Run, Short(words, 300));
            if (r is ObjectInstance o && o.Get("then") is Function then)
            {
                try
                {
                    e.Constraints.Reset();
                    e.Call(then, r, new JsValue[] { ok, fail });
                    e.Advanced.ProcessTasks();
                }
                catch (Exception ex) { Fail("search " + s.Id, ex); done.TrySetResult(new()); }
            }
            else done.TrySetResult(Hits(r));
        });
        return done.Task;
    }

    // What a mod found, checked: its links are ones it takes, its pictures from its own sites (the app loads them).
    private List<ModSearchHit> Hits(JsValue v)
    {
        var list = new List<ModSearchHit>();
        if (v is not JsArray arr) return list;
        foreach (var item in arr.Take(20))
        {
            if (item is not JsObject o || Nil(o.Get("title")) || Nil(o.Get("url"))) continue;
            var url = TypeConverter.ToString(o.Get("url"));
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !HostAllowed(u.Host)) continue;
            lock (_links) if (!_links.Any(l => l.Matches(url))) continue;
            string? Word(string name, int max) => Nil(o.Get(name)) ? null : Short(TypeConverter.ToString(o.Get(name)), max) is { Length: > 0 } s ? s : null;
            var picture = Word("picture", 1000);
            if (picture != null && !(Uri.TryCreate(picture, UriKind.Absolute, out var p) && p.Scheme == Uri.UriSchemeHttps && HostAllowed(p.Host))) picture = null;
            double? duration = o.Get("duration") is { } d && d.IsNumber() && TypeConverter.ToNumber(d) is var sec && sec > 0 && sec < 86400 ? sec : null;
            list.Add(new ModSearchHit(Short(TypeConverter.ToString(o.Get("title")), 200), Word("artist", 200), Word("hint", 200), url, picture, duration));
        }
        return list;
    }
}
