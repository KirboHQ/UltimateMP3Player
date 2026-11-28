using Jint;
using Jint.Native;
using Jint.Runtime;

namespace UltimateMP3Player.Core.Mods;

// Reading a Realm database (osu! lazer's client.realm) for a mod: only the computers have it (the UltimateMP3Player.Realm
// project, set into ModRuntime.Realm at startup); Core knows only this.
public interface IModRealm
{
    // A copy of the file, opened read-only whatever its schema (never the live file: lazer may be writing it).
    IModRealmFile Open(string path);
}

public interface IModRealmFile : IDisposable
{
    IReadOnlyList<ModRealmClass> Classes { get; }
    // Every object of a class as plain values: its own values (numbers, text, dates as text), and what the paths ask for beyond them
    // ("Beatmaps.Metadata.Title": the list of objects, the object, the value), lists as lists. Objects a path doesn't name aren't followed.
    // skip/take: a page of them (a library of thousands read a piece at a time, the mod's limits reset between pieces).
    IEnumerable<IReadOnlyDictionary<string, object?>> All(string className, IReadOnlyList<string> paths, int skip = 0, int take = int.MaxValue);
    int Count(string className);
}

public sealed record ModRealmClass(string Name, IReadOnlyList<ModRealmProperty> Properties);
public sealed record ModRealmProperty(string Name, string Type, string? Target);

public sealed partial class ModRuntime
{
    public static IModRealm? Realm { get; set; }
    private readonly List<IModRealmFile> _realms = new();

    private void AddRealm(Engine e, JsObject ump)
    {
        var realm = new JsObject(e);
        Fn(e, realm, "open", a =>
        {
            var reader = Realm ?? throw new JavaScriptException("Realm isn't on this system (ump.has(\"realm\")).");
            var path = Readable(Str(a, 0));
            if (!File.Exists(path)) throw new JavaScriptException("No such file: " + path);
            if (_realms.Count >= 4) throw new JavaScriptException("At most 4 realms open at once: close one.");
            IModRealmFile file;
            try { file = reader.Open(path); }
            catch (Exception ex) { throw new JavaScriptException("This realm can't be opened: " + ex.Message); }
            _realms.Add(file);
            var db = new JsObject(e);
            Fn(e, db, "classes", _ => new JsArray(e, file.Classes.Select(c =>
            {
                var o = new JsObject(e);
                o.Set("name", c.Name);
                o.Set("properties", new JsArray(e, c.Properties.Select(p =>
                {
                    var po = new JsObject(e);
                    po.Set("name", p.Name);
                    po.Set("type", p.Type);
                    po.Set("target", Of(p.Target));
                    return (JsValue)po;
                }).ToArray()));
                return (JsValue)o;
            }).ToArray()));
            Fn(e, db, "all", b =>
            {
                if (!_realms.Contains(file)) throw new JavaScriptException("This realm is closed.");
                var paths = Arg(b, 1) is JsArray arr ? arr.Select(v => TypeConverter.ToString(v)).Take(200).ToList() : new List<string>();
                // { skip, take }: a page of them
                int Page(string name, int fallback) => Arg(b, 2) is JsObject o && o.Get(name).IsNumber() ? (int)Math.Clamp(TypeConverter.ToNumber(o.Get(name)), 0, int.MaxValue) : fallback;
                var list = new List<JsValue>();
                try
                {
                    foreach (var row in file.All(Str(b, 0), paths, Page("skip", 0), Page("take", int.MaxValue)))
                    {
                        if (list.Count >= 500_000) break;
                        list.Add(ToJs(e, row));
                    }
                }
                catch (Exception ex) when (ex is not JavaScriptException) { throw new JavaScriptException("realm.all: " + ex.Message); }
                // (a whole library read natively: not counted against the mod's own time and memory)
                e.Constraints.Reset();
                return new JsArray(e, list.ToArray());
            });
            Fn(e, db, "count", b =>
            {
                if (!_realms.Contains(file)) throw new JavaScriptException("This realm is closed.");
                try { return file.Count(Str(b, 0)); }
                catch (Exception ex) when (ex is not JavaScriptException) { throw new JavaScriptException("realm.count: " + ex.Message); }
            });
            Fn(e, db, "close", _ =>
            {
                if (_realms.Remove(file)) file.Dispose();
                return JsValue.Undefined;
            });
            return db;
        });
        ump.Set("realm", realm);
    }

    private static JsValue ToJs(Engine e, IReadOnlyDictionary<string, object?> row)
    {
        var o = new JsObject(e);
        foreach (var (k, v) in row) o.Set(k, Value(e, v));
        return o;
    }

    private static JsValue Value(Engine e, object? v) => v switch
    {
        null => JsValue.Null,
        bool b => b,
        string s => s,
        int i => i,
        long l => l,
        float f => f,
        double d => d,
        IReadOnlyDictionary<string, object?> d => ToJs(e, d),
        IEnumerable<object?> list => new JsArray(e, list.Select(x => Value(e, x)).ToArray()),
        _ => new JsString(v.ToString() ?? ""),
    };

    // (on the mod's thread, as they were opened: a realm belongs to its thread)
    private void CloseRealms()
    {
        foreach (var r in _realms)
            try { r.Dispose(); } catch (Exception) { }
        _realms.Clear();
    }
}
