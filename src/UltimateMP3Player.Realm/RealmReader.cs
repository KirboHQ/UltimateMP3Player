using Realms;
using Realms.Schema;
using UltimateMP3Player.Core;
using UltimateMP3Player.Core.Mods;

namespace UltimateMP3Player.RealmReader;

// A realm for the mods (ModRuntime.Realm): a copy of the file, opened dynamic (no models, whatever its schema) and read-only,
// deleted when closed. Never the live file: lazer may be writing it.
public sealed class RealmReader : IModRealm
{
    public IModRealmFile Open(string path)
    {
        var dir = Path.Combine(AppPaths.TempDir, "realm", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var copy = Path.Combine(dir, "copy.realm");
        try
        {
            using (var from = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var to = File.Create(copy))
                from.CopyTo(to);
            Realm realm;
            try { realm = Realm.GetInstance(new RealmConfiguration(copy) { IsDynamic = true, IsReadOnly = true }); }
            // A file written by a newer Realm needs its format upgraded first: done on the copy, which is ours.
            catch (Exception) { realm = Realm.GetInstance(new RealmConfiguration(copy) { IsDynamic = true }); }
            return new RealmFile(realm, dir);
        }
        catch
        {
            Delete(dir);
            throw;
        }
    }

    private static void Delete(string dir)
    {
        try { Directory.Delete(dir, true); } catch (Exception) { }
    }

    // What the paths ask beyond an object's own values: "Beatmaps.Metadata.Title" → Beatmaps → Metadata → Title.
    private sealed class Paths : Dictionary<string, Paths>
    {
        public static Paths Of(IEnumerable<string> paths)
        {
            var root = new Paths();
            foreach (var path in paths)
            {
                var at = root;
                foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!at.TryGetValue(part, out var next)) at[part] = next = new Paths();
                    at = next;
                }
            }
            return root;
        }
    }

    private sealed class RealmFile(Realm realm, string dir) : IModRealmFile
    {
        public IReadOnlyList<ModRealmClass> Classes => realm.Schema
            .Select(c => new ModRealmClass(c.Name, c.Select(p => new ModRealmProperty(p.Name, TypeName(p.Type), p.ObjectType)).ToList()))
            .ToList();

        public IEnumerable<IReadOnlyDictionary<string, object?>> All(string className, IReadOnlyList<string> paths, int skip = 0, int take = int.MaxValue)
        {
            if (!realm.Schema.TryFindObjectSchema(className, out var schema) || schema == null) throw new ArgumentException("no class " + className);
            if (schema.BaseType == ObjectSchema.ObjectType.EmbeddedObject) throw new ArgumentException(className + " is inside other objects: reach it by a path");
            var wanted = Paths.Of(paths);
            // (results are a list: a page starts where it starts, the ones before aren't read)
            var all = realm.DynamicApi.All(className);
            if (all is IReadOnlyList<IRealmObject> list)
            {
                for (long i = skip, end = Math.Min(list.Count, (long)skip + take); i < end; i++)
                    yield return Read(list[(int)i], schema, wanted);
                yield break;
            }
            foreach (var o in all.AsEnumerable().Skip(skip).Take(take))
                yield return Read(o, schema, wanted);
        }

        public int Count(string className)
        {
            if (!realm.Schema.TryFindObjectSchema(className, out var schema) || schema == null) throw new ArgumentException("no class " + className);
            return realm.DynamicApi.All(className).Count();
        }

        private ObjectSchema SchemaOf(string name) =>
            realm.Schema.TryFindObjectSchema(name, out var s) && s != null ? s : throw new ArgumentException("no class " + name);

        private Dictionary<string, object?> Read(IRealmObjectBase o, ObjectSchema schema, Paths wanted)
        {
            var row = new Dictionary<string, object?>();
            foreach (var p in schema)
            {
                var kind = p.Type & ~PropertyType.Flags;
                if (p.Type.HasFlag(PropertyType.Dictionary) || kind == PropertyType.LinkingObjects) continue;
                bool many = p.Type.HasFlag(PropertyType.Array) || p.Type.HasFlag(PropertyType.Set);
                wanted.TryGetValue(p.Name, out var sub);
                try
                {
                    if (kind == PropertyType.Object)
                    {
                        // Objects only where a path goes (they link back to each other: a beatmap to its set, the set to its beatmaps).
                        if (sub == null || p.ObjectType == null) continue;
                        var target = SchemaOf(p.ObjectType);
                        if (many)
                        {
                            IEnumerable<IRealmObjectBase> items = target.BaseType == ObjectSchema.ObjectType.EmbeddedObject
                                ? o.DynamicApi.GetList<IEmbeddedObject>(p.Name)
                                : o.DynamicApi.GetList<IRealmObject>(p.Name);
                            row[p.Name] = items.Select(x => (object?)Read(x, target, sub)).ToList();
                        }
                        else
                        {
                            var v = o.DynamicApi.Get<RealmValue>(p.Name);
                            row[p.Name] = v.Type == RealmValueType.Null ? null : Read(v.AsIRealmObject(), target, sub);
                        }
                    }
                    else if (many)
                    {
                        // Lists of values (a collection's beatmap hashes) only when asked for: they can be long.
                        if (sub == null) continue;
                        row[p.Name] = Values(o, p, kind);
                    }
                    else row[p.Name] = Scalar(o.DynamicApi.Get<RealmValue>(p.Name));
                }
                catch (Exception) { row[p.Name] = null; }
            }
            return row;
        }

        // "String?", "Object", "String[]" (a [Flags] enum's own ToString gives "Bool, NullableDouble" for a link).
        private static string TypeName(PropertyType t) =>
            (t & ~PropertyType.Flags) + (t.HasFlag(PropertyType.Nullable) ? "?" : "")
            + (t.HasFlag(PropertyType.Array) ? "[]" : t.HasFlag(PropertyType.Set) ? " set" : t.HasFlag(PropertyType.Dictionary) ? " dictionary" : "");

        private static List<object?> Values(IRealmObjectBase o, Property p, PropertyType kind)
        {
            bool set = p.Type.HasFlag(PropertyType.Set);
            IEnumerable<object?> Of<T>() => set ? o.DynamicApi.GetSet<T>(p.Name).Cast<object?>() : o.DynamicApi.GetList<T>(p.Name).Cast<object?>();
            bool nullable = p.Type.HasFlag(PropertyType.Nullable);
            var items = kind switch
            {
                PropertyType.String => Of<string?>(),
                PropertyType.Int => nullable ? Of<long?>() : Of<long>(),
                PropertyType.Bool => nullable ? Of<bool?>() : Of<bool>(),
                PropertyType.Double => nullable ? Of<double?>() : Of<double>(),
                PropertyType.Float => (nullable ? Of<float?>() : Of<float>()).Select(f => f is float x ? (object?)(double)x : null),
                PropertyType.Date => Of<DateTimeOffset>().Select(d => (object?)((DateTimeOffset)d!).ToString("o")),
                PropertyType.Guid => Of<Guid>().Select(g => (object?)g!.ToString()),
                PropertyType.RealmValue => Of<RealmValue>().Select(v => Scalar((RealmValue)v!)),
                _ => Enumerable.Empty<object?>(),
            };
            return items.ToList();
        }

        private static object? Scalar(RealmValue v) => v.Type switch
        {
            RealmValueType.Int => v.AsInt64(),
            RealmValueType.Bool => v.AsBool(),
            RealmValueType.String => v.AsString(),
            RealmValueType.Float => (double)v.AsFloat(),
            RealmValueType.Double => v.AsDouble(),
            RealmValueType.Decimal128 => (double)v.AsDecimal(),
            RealmValueType.Date => v.AsDate().ToString("o"),
            RealmValueType.ObjectId => v.AsObjectId().ToString(),
            RealmValueType.Guid => v.AsGuid().ToString(),
            _ => null,
        };

        public void Dispose()
        {
            realm.Dispose();
            Delete(dir);
        }
    }
}
