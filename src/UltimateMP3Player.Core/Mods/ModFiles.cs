using System.IO.Compression;
using System.Text;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Json;
using Jint.Runtime;

namespace UltimateMP3Player.Core.Mods;

// A song a mod adds (ump.library.add): its file, the words it knows better than the file's tags, a picture for its cover, its
// keys (the mod's id before each, so no mod passes its songs off as another's or a site's).
public sealed record ModNewSong(string Path, string? Title, string? Artist, string? Album, string? Year, string? Cover, IReadOnlyList<string> Keys);

public sealed record ModPlaylist(string Id, string Name, IReadOnlyList<string> Songs);

// An importer a mod puts in Library's import menu (ump.importers.add), and the file types it opens when one is dropped on the
// window or opened with the app.
public sealed record ModImporter(ModRuntime Mod, string Id, string Title, IReadOnlyList<string> Extensions, JsValue Run, JsValue Open);

// What a mod may read and add (phase 3): the files and folders the user picked for it (remembered in its data, places.json),
// zips, songs and playlists into the library, data kept per song, importers. Every function runs on the mod's thread.
public sealed partial class ModRuntime
{
    private readonly List<string> _places = new();
    private readonly List<ModImporter> _importers = new();
    private const long MaxRead = 64L * 1024 * 1024;
    private static readonly StringComparison PathCase = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    // The app's own types and the songs' are the app's to open, whatever a mod declares.
    private static readonly HashSet<string> TakenExtensions = new(Importer.Extensions.Concat(new[] { ".umpmod", ".umplook", ".ump", ".json" }), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ModImporter> Importers { get { lock (_importers) return _importers.ToList(); } }
    public IReadOnlyList<string> Places { get { lock (_places) return _places.ToList(); } }

    private string PlacesFile => Path.Combine(DataDir, "places.json");

    private void LoadPlaces()
    {
        lock (_places)
        {
            _places.Clear();
            try { if (File.Exists(PlacesFile)) _places.AddRange(JsonStore.Load<List<string>>(PlacesFile)); }
            catch (Exception ex) { Write(ex.Message); }
        }
    }

    // A folder or a file the user gave this mod (picked, dropped, opened with the app): readable from then on, also next time.
    internal void Allow(string path)
    {
        path = Path.GetFullPath(path);
        lock (_places)
        {
            if (_places.Any(p => string.Equals(p, path, PathCase))) return;
            _places.Add(path);
            if (_places.Count > 300) _places.RemoveAt(0);
            SavePlaces();
        }
    }

    private void SavePlaces()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            JsonStore.Save(PlacesFile, _places.ToList());
        }
        catch (Exception ex) { Write(ex.Message); }
    }

    private static bool Under(string full, string root)
    {
        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(full, root, PathCase) || full.StartsWith(root + Path.DirectorySeparatorChar, PathCase);
    }

    // The full path of what a mod wants to read, if it may: its own folder and data, else what the user gave it (a folder with what's
    // inside), never the app's data (sign-ins, keys, other mods' data), whatever folder was picked. A relative path is in the mod.
    internal string Readable(string path)
    {
        if (path.Length == 0) throw new JavaScriptException("A path is needed.");
        string full;
        try { full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Folder, path)); }
        catch (Exception) { throw new JavaScriptException("Not a path: " + path); }
        if (Under(full, Path.GetFullPath(Folder)) || Under(full, Path.GetFullPath(DataDir))) return full;
        Need(ModPermissions.Files);
        bool given;
        lock (_places) given = _places.Any(p => Under(full, p)) || KnownRoots().Any(r => Under(full, r));
        if (!given) throw new JavaScriptException("A mod reads only what the user gave it (ump.files.pickFolder, pickFile): " + path);
        if (Under(full, Path.GetFullPath(AppPaths.DataDir))) throw new JavaScriptException("The app's own data isn't for mods: " + path);
        return full;
    }

    // The folders its mod.json names (places), on this system: readable without asking, like a folder the user picked.
    private List<string>? _known;
    private ModManifest? _knownOf;

    private List<string> KnownRoots()
    {
        if (_known != null && _knownOf == Manifest) return _known;
        _knownOf = Manifest;
        return _known = Manifest.Places.SelectMany(p => p.Paths).Select(ExpandPlace).OfType<string>().ToList();
    }

    // "~/…", "%LOCALAPPDATA%/…": the full path here, or null (another system's: a variable left, or not a full path).
    private static string? ExpandPlace(string path)
    {
        if (path.StartsWith('~')) path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        path = Environment.ExpandEnvironmentVariables(path);
        if (path.Contains('%') || !Path.IsPathRooted(path)) return null;
        try
        {
            var full = Path.GetFullPath(path);
            return Under(full, Path.GetFullPath(AppPaths.DataDir)) ? null : full;
        }
        catch (Exception) { return null; }
    }

    // "~/…" and "%APPDATA%/…" for where a picker opens (osu!'s folders differ on each system).
    private static string? Expand(string? start)
    {
        if (string.IsNullOrWhiteSpace(start)) return null;
        if (start.StartsWith('~')) start = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + start[1..];
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(start)); } catch (Exception) { return null; }
    }

    private JsValue Bytes(Engine e, byte[] data) =>
        e.Intrinsics.Uint8Array.Construct(new JsValue[] { e.Intrinsics.ArrayBuffer.Construct(data) }, e.Intrinsics.Uint8Array);

    private static byte[] ReadAll(Stream s, long length)
    {
        if (length > MaxRead) throw new JavaScriptException("Too big to read at once (64 MB).");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private void AddFiles(Engine e, JsObject ump)
    {
        // ---- files
        var files = new JsObject(e);
        (string Title, string? Start, List<string> Extensions) PickArgs(JsValue[] a)
        {
            if (Arg(a, 0) is JsObject o)
                return (Short(Nil(o.Get("title")) ? "" : TypeConverter.ToString(o.Get("title")), 100),
                    Nil(o.Get("start")) ? null : TypeConverter.ToString(o.Get("start")),
                    o.Get("extensions") is JsArray x ? x.Select(v => TypeConverter.ToString(v).ToLowerInvariant()).Where(s => s.StartsWith('.')).Take(20).ToList() : new());
            return (Short(Str(a, 0), 100), null, new());
        }
        Fn(e, files, "pickFolder", a =>
        {
            Need(ModPermissions.Files);
            var (title, start, _) = PickArgs(a);
            var picked = _host.CanPickFolders ? _host.PickAsync(this, true, title, Array.Empty<string>(), Expand(start)) : Task.FromResult<string?>(null);
            return Promise(e, picked, p => { if (p != null) Allow(p); return Of(p); });
        });
        Fn(e, files, "pickFile", a =>
        {
            Need(ModPermissions.Files);
            var (title, start, exts) = PickArgs(a);
            return Promise(e, _host.PickAsync(this, false, title, exts, Expand(start)), p => { if (p != null) Allow(p); return Of(p); });
        });
        Fn(e, files, "places", a => new JsArray(e, Places.Select(p => (JsValue)new JsString(p)).ToArray()));
        // known(id): the folder of a place its mod.json names that's there on this device (the first found), or null.
        Fn(e, files, "known", a =>
        {
            Need(ModPermissions.Files);
            var place = Manifest.Places.FirstOrDefault(p => p.Id == Str(a, 0));
            return place?.Paths.Select(ExpandPlace).FirstOrDefault(p => p != null && Directory.Exists(p)) is { } found ? found : JsValue.Null;
        });
        Fn(e, files, "forget", a =>
        {
            var gone = Str(a, 0);
            lock (_places) if (_places.RemoveAll(p => string.Equals(p, gone, PathCase)) > 0) SavePlaces();
            return JsValue.Undefined;
        });
        // remove(path): a file or folder of the mod's own data (a download done with), nothing else.
        Fn(e, files, "remove", a =>
        {
            var p = Str(a, 0);
            if (p.Length == 0) throw new JavaScriptException("A path is needed.");
            var full = Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(DataDir, p));
            var data = Path.GetFullPath(DataDir);
            if (!Under(full, data) || string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), data.TrimEnd(Path.DirectorySeparatorChar), PathCase))
                throw new JavaScriptException("files.remove: only what's in the mod's own data.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
            else if (File.Exists(full)) File.Delete(full);
            lock (_places) if (_places.RemoveAll(x => Under(x, full)) > 0) SavePlaces();
            return JsValue.Undefined;
        });
        Fn(e, files, "exists", a => { var p = Readable(Str(a, 0)); return File.Exists(p) || Directory.Exists(p); });
        Fn(e, files, "isFolder", a => Directory.Exists(Readable(Str(a, 0))));
        Fn(e, files, "stat", a =>
        {
            var p = Readable(Str(a, 0));
            FileSystemInfo info = Directory.Exists(p) ? new DirectoryInfo(p) : new FileInfo(p);
            if (!info.Exists) return JsValue.Null;
            var o = new JsObject(e);
            o.Set("folder", info is DirectoryInfo);
            o.Set("size", info is FileInfo f ? f.Length : 0);
            o.Set("modified", new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds());
            return o;
        });
        Fn(e, files, "list", a =>
        {
            var dir = new DirectoryInfo(Readable(Str(a, 0)));
            if (!dir.Exists) return JsValue.Null;
            var items = new List<JsValue>();
            foreach (var info in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true }))
            {
                if (items.Count >= 50_000) break;
                var o = new JsObject(e);
                o.Set("name", info.Name);
                o.Set("path", info.FullName);
                o.Set("folder", info is DirectoryInfo);
                o.Set("size", info is FileInfo f ? f.Length : 0);
                o.Set("modified", new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds());
                items.Add(o);
            }
            return new JsArray(e, items.ToArray());
        });
        // readText(path, maxBytes?): the whole file, or only its start (the headers of thousands of beatmaps, read fast).
        Fn(e, files, "readText", a =>
        {
            var p = Readable(Str(a, 0));
            if (Arg(a, 1).IsNumber() && TypeConverter.ToNumber(Arg(a, 1)) is var max && max > 0)
            {
                using var s = File.OpenRead(p);
                var buffer = new byte[(int)Math.Min(Math.Min(max, MaxRead), s.Length)];
                int read = 0;
                for (int n; read < buffer.Length && (n = s.Read(buffer, read, buffer.Length - read)) > 0;) read += n;
                return Encoding.UTF8.GetString(buffer, 0, read).TrimStart('\uFEFF');
            }
            if (new FileInfo(p).Length > MaxRead) throw new JavaScriptException("Too big to read at once (64 MB): " + p);
            return File.ReadAllText(p);
        });
        // A file's fingerprint (osu! stable's collections name beatmaps by their MD5): "md5" or "sha256", lower-case hex.
        Fn(e, files, "hash", a =>
        {
            var p = Readable(Str(a, 0));
            using var s = File.OpenRead(p);
            var hash = Str(a, 1).ToLowerInvariant() switch
            {
                "sha256" => System.Security.Cryptography.SHA256.HashData(s),
                "md5" or "" => System.Security.Cryptography.MD5.HashData(s),
                var other => throw new JavaScriptException("files.hash knows md5 and sha256, not " + other),
            };
            return Convert.ToHexString(hash).ToLowerInvariant();
        });
        Fn(e, files, "readBytes", a =>
        {
            var p = Readable(Str(a, 0));
            if (new FileInfo(p).Length > MaxRead) throw new JavaScriptException("Too big to read at once (64 MB): " + p);
            return Bytes(e, File.ReadAllBytes(p));
        });
        ump.Set("files", files);

        // ---- zips (an .osz, an .osk)
        var zip = new JsObject(e);
        Fn(e, zip, "entries", a =>
        {
            using var z = ZipFile.OpenRead(Readable(Str(a, 0)));
            return new JsArray(e, z.Entries.Take(50_000).Select(en =>
            {
                var o = new JsObject(e);
                o.Set("name", en.FullName);
                o.Set("size", en.Length);
                return (JsValue)o;
            }).ToArray());
        });
        ZipArchiveEntry Entry(ZipArchive z, string name) => z.GetEntry(name) ?? throw new JavaScriptException("No such file in the zip: " + name);
        Fn(e, zip, "readText", a =>
        {
            using var z = ZipFile.OpenRead(Readable(Str(a, 0)));
            var en = Entry(z, Str(a, 1));
            using var s = en.Open();
            return Encoding.UTF8.GetString(ReadAll(s, en.Length)).TrimStart('\uFEFF');
        });
        Fn(e, zip, "readBytes", a =>
        {
            using var z = ZipFile.OpenRead(Readable(Str(a, 0)));
            var en = Entry(z, Str(a, 1));
            using var s = en.Open();
            return Bytes(e, ReadAll(s, en.Length));
        });
        // The whole zip into the app's music folder (<music>/<mod's name>/<zip's name>), or, { data: true }, into the mod's own data
        // (an osu! skin: not music): the folder, readable by the mod from then on.
        Fn(e, zip, "extract", a =>
        {
            var file = Readable(Str(a, 0));
            var name = Safe(Str(a, 1) is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(file));
            bool data = Arg(a, 2) is JsObject options && TypeConverter.ToBoolean(options.Get("data"));
            var target = data ? Path.Combine(DataDir, "zips", name) : Path.Combine(_host.MusicDir, Safe(Manifest.Name), name);
            return Promise(e, Task.Run(() => Extract(file, target)), dir => { Allow(dir); return dir; });
        });
        ump.Set("zip", zip);

        // ---- songs and playlists into the library
        var library = (JsObject)ump.Get("library");
        Fn(e, library, "add", a =>
        {
            Need(ModPermissions.LibraryWrite);
            if (Arg(a, 0) is not JsObject o || Nil(o.Get("path"))) throw new JavaScriptException("library.add needs { path }.");
            string? Word(string name, int max) => Nil(o.Get(name)) ? null : Short(TypeConverter.ToString(o.Get(name)), max) is { Length: > 0 } s ? s : null;
            var path = Readable(TypeConverter.ToString(o.Get("path")));
            if (!File.Exists(path)) throw new JavaScriptException("No such file: " + path);
            var cover = Nil(o.Get("cover")) ? null : Readable(TypeConverter.ToString(o.Get("cover")));
            var keys = o.Get("keys") is JsArray k ? k.Select(x => Id + ":" + Short(TypeConverter.ToString(x), 200)).Take(20).ToList() : new List<string>();
            var song = new ModNewSong(path, Word("title", 300), Word("artist", 300), Word("album", 300), Word("year", 10), cover, keys);
            return Promise(e, _host.AddSongAsync(this, song), Of);
        });
        // find({ title, artist, duration }): a song like it already in the library (the same title and artist, the closest length), or null.
        Fn(e, library, "find", a =>
        {
            Need(ModPermissions.LibraryRead);
            if (Arg(a, 0) is not JsObject o || Nil(o.Get("title"))) throw new JavaScriptException("library.find needs { title, artist }.");
            double? duration = o.Get("duration") is { } d && d.IsNumber() && TypeConverter.ToNumber(d) > 0 ? TypeConverter.ToNumber(d) : null;
            var found = _host.FindSong(TypeConverter.ToString(o.Get("title")), Nil(o.Get("artist")) ? null : TypeConverter.ToString(o.Get("artist")), duration);
            return SongJs(e, found, true);
        });
        // replace(oldId, newId): a song this mod added takes another's place (the user said so): its playlists, plays and likes
        // move to it, the other leaves the library (its file stays where it is).
        Fn(e, library, "replace", a =>
        {
            Need(ModPermissions.LibraryWrite);
            var (old, now) = (Str(a, 0), Str(a, 1));
            if (old.Length == 0 || now.Length == 0 || old == now) throw new JavaScriptException("library.replace needs two songs' ids: the one going, the one taking its place.");
            return Promise(e, _host.ReplaceSongAsync(this, old, now), b => b);
        });
        var playlists = new JsObject(e);
        Fn(e, playlists, "list", a =>
        {
            Need(ModPermissions.LibraryRead);
            return new JsArray(e, _host.Playlists().Select(p =>
            {
                var o = new JsObject(e);
                o.Set("id", p.Id);
                o.Set("name", p.Name);
                o.Set("songs", new JsArray(e, p.Songs.Select(s => (JsValue)new JsString(s)).ToArray()));
                return (JsValue)o;
            }).ToArray());
        });
        Fn(e, playlists, "create", a =>
        {
            Need(ModPermissions.LibraryWrite);
            var name = Short(Str(a, 0).Trim(), 100);
            if (name.Length == 0) throw new JavaScriptException("playlists.create needs a name.");
            return Promise(e, _host.CreatePlaylistAsync(this, name), Of);
        });
        Fn(e, playlists, "add", a =>
        {
            Need(ModPermissions.LibraryWrite);
            var ids = Arg(a, 1) is JsArray arr ? arr.Select(v => TypeConverter.ToString(v)).Take(100_000).ToList() : new List<string>();
            return Promise(e, _host.AddToPlaylistAsync(this, Str(a, 0), ids), b => b);
        });
        ump.Set("playlists", playlists);

        // ---- data kept per song (dropped when the song leaves the library: ForgetSong)
        var store = (JsObject)ump.Get("store");
        Fn(e, store, "song", a => _store.TryGetValue("song:" + Str(a, 0), out var json) ? new JsonParser(e).Parse(json) : JsValue.Undefined);
        Fn(e, store, "setSong", a => { StoreSet(e, "song:" + Str(a, 0), Arg(a, 1)); return JsValue.Undefined; });

        // ---- importers
        var importers = new JsObject(e);
        Fn(e, importers, "add", a =>
        {
            if (Arg(a, 0) is not JsObject o || o.Get("run") is not Function && o.Get("open") is not Function)
                throw new JavaScriptException("importers.add needs { id, title, run } (open(path) for the file types in extensions).");
            var id = Nil(o.Get("id")) ? "" : TypeConverter.ToString(o.Get("id"));
            if (id.Length is 0 or > 40) throw new JavaScriptException("An importer needs an id (at most 40 characters).");
            var title = Short(Nil(o.Get("title")) ? id : TypeConverter.ToString(o.Get("title")), 60);
            var exts = o.Get("extensions") is JsArray x
                ? x.Select(v => TypeConverter.ToString(v).ToLowerInvariant()).Where(s => s.Length is > 1 and <= 12 && s[0] == '.' && !TakenExtensions.Contains(s)).Distinct().Take(10).ToList()
                : new List<string>();
            if (exts.Count > 0) Need(ModPermissions.Files);
            lock (_importers)
            {
                _importers.RemoveAll(i => i.Id == id);
                if (_importers.Count >= 10) throw new JavaScriptException("At most 10 importers per mod.");
                _importers.Add(new ModImporter(this, id, title, exts, o.Get("run"), o.Get("open")));
            }
            _host.Changed(this);
            return JsValue.Undefined;
        });
        Fn(e, importers, "remove", a => { lock (_importers) _importers.RemoveAll(i => i.Id == Str(a, 0)); _host.Changed(this); return JsValue.Undefined; });
        ump.Set("importers", importers);

        AddRealm(e, ump);
    }

    private static string Safe(string name)
    {
        var s = string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.');
        return s.Length == 0 ? "mod" : s.Length > 80 ? s[..80] : s;
    }

    // A zip's files into a folder, each checked to land inside it (as a mod's own package).
    private static string Extract(string file, string target)
    {
        using var z = ZipFile.OpenRead(file);
        if (z.Entries.Count > 20_000 || z.Entries.Sum(en => en.Length) > 4L * 1024 * 1024 * 1024) throw new ModException("The zip is too big.");
        var root = Path.GetFullPath(target);
        foreach (var en in z.Entries)
        {
            if (en.FullName.EndsWith('/') || en.FullName.EndsWith('\\')) continue;
            if (!ModPackage.IsSafePath(en.FullName)) throw new ModException("The zip has a file in a wrong place: " + en.FullName);
            var to = Path.GetFullPath(Path.Combine(root, en.FullName));
            if (!Under(to, root)) throw new ModException("The zip has a file in a wrong place: " + en.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            en.ExtractToFile(to, overwrite: true);
        }
        return root;
    }

    // ---- from the app
    // Library's import menu: an importer chosen.
    public void RunImporter(ModImporter imp) => Post(() => Call("importer " + imp.Id, imp.Run));

    // A file of a type the importer declared, dropped on the window or opened with the app: the mod may read it.
    public void OpenWithImporter(ModImporter imp, string path)
    {
        Allow(path);
        Post(() => Call("importer " + imp.Id, imp.Open, new JsString(Path.GetFullPath(path))));
    }

    // A song gone from the library: what the mod kept for it goes too.
    public void ForgetSong(string id)
    {
        if (_thread != null) { Post(() => _storeDirty |= _store.Remove("song:" + id)); return; }
        // (off: in its saved store)
        try
        {
            var file = Path.Combine(DataDir, "store.json");
            if (!File.Exists(file)) return;
            var saved = JsonStore.Load<Dictionary<string, string>>(file);
            if (saved.Remove("song:" + id)) JsonStore.Save(file, saved);
        }
        catch (Exception) { }
    }
}
