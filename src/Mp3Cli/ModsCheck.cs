using System.IO.Compression;
using System.Text.Json;
using Realms;
using Realms.Schema;
using UltimateMP3Player.Core;
using UltimateMP3Player.Core.Mods;

// mp3cli mods: a made-up mod installed from a zip and run: its settings, store, events, menu entries, timers, a promise, the
// permissions it lacks, the sandbox; a mod that never ends its start is stopped; a zip with a file outside its folder, a wrong id,
// a newer api or an unknown permission are refused; switched off, removed; a developer's folder loaded again when it changes.
static class ModsCheck
{
    private sealed class Host : IModHost
    {
        public readonly List<string> Toasts = new();
        public string AppVersion => "9.9.9";
        public ModSong? Song { get; set; }
        public double Position => 0;
        public bool Playing => false;
        public void Control(string action) => Toasts.Add("control " + action);
        public IReadOnlyList<ModSong> Songs() => Array.Empty<ModSong>();
        public void Toast(ModRuntime mod, string text) { lock (Toasts) Toasts.Add(text); }
        public Task<bool> ConfirmAsync(ModRuntime mod, string title, string message, string ok) => Task.FromResult(true);
        public Task<int> ChooseAsync(ModRuntime mod, string title, string message, string[] options) => Task.FromResult(1);
        public Task<int> ListAsync(ModRuntime mod, string title, IReadOnlyList<(string Title, string? Hint)> items, int selected) => Task.FromResult(items.Count - 1);
        public void Changed(ModRuntime mod) { }
        public CueSheet? Cues;
        public void Cue(ModRuntime mod, CueSheet? sheet) => Cues = sheet;
        public ModImage? LoadImage(string file) => new ModImage(16, 8, file);
        public float MeasureText(string text, float size, bool bold) => text.Length * size / 2;
        public bool CanBlend => true;
        public readonly List<string> Chosen = new();
        public bool IsChosen(ModVisualizer v) => Chosen.Contains(v.Key);
        public void Choose(ModVisualizer v, bool on) { if (on) Chosen.Add(v.Key); else Chosen.Remove(v.Key); }
        public string MusicDir { get; set; } = "";
        public bool CanPickFolders => true;
        public readonly Queue<string?> Picks = new();
        public Task<string?> PickAsync(ModRuntime mod, bool folder, string title, IReadOnlyList<string> extensions, string? start) =>
            Task.FromResult(Picks.Count > 0 ? Picks.Dequeue() : null);
        public readonly List<ModNewSong> Added = new();
        public Task<string?> AddSongAsync(ModRuntime mod, ModNewSong song) { lock (Added) Added.Add(song); return Task.FromResult<string?>("s" + Added.Count); }
        public readonly List<ModPlaylist> Lists = new();
        public IReadOnlyList<ModPlaylist> Playlists() => Lists.ToList();
        public Task<string?> CreatePlaylistAsync(ModRuntime mod, string name)
        {
            Lists.Add(new ModPlaylist("p" + (Lists.Count + 1), name, new List<string>()));
            return Task.FromResult<string?>("p" + Lists.Count);
        }
        public Task<bool> AddToPlaylistAsync(ModRuntime mod, string playlistId, IReadOnlyList<string> songIds)
        {
            if (Lists.FirstOrDefault(l => l.Id == playlistId)?.Songs is not List<string> songs) return Task.FromResult(false);
            songs.AddRange(songIds);
            return Task.FromResult(true);
        }
    }

    // A small realm shaped like osu! lazer's client.realm (the classes and names the osu! mod reads).
    private static void MakeRealm(string path)
    {
        var schema = new RealmSchema.Builder
        {
            new ObjectSchema.Builder("BeatmapSet", ObjectSchema.ObjectType.RealmObject)
            {
                Property.Primitive("OnlineID", RealmValueType.Int), Property.Primitive("DeletePending", RealmValueType.Bool),
                Property.ObjectList("Beatmaps", "Beatmap"), Property.ObjectList("Files", "RealmNamedFileUsage"),
            },
            new ObjectSchema.Builder("Beatmap", ObjectSchema.ObjectType.RealmObject)
            {
                Property.Primitive("DifficultyName", RealmValueType.String, isNullable: true), Property.Object("Metadata", "BeatmapMetadata"),
                Property.Object("BeatmapSet", "BeatmapSet"),
            },
            new ObjectSchema.Builder("BeatmapMetadata", ObjectSchema.ObjectType.RealmObject) { Property.Primitive("Title", RealmValueType.String, isNullable: true) },
            new ObjectSchema.Builder("RealmNamedFileUsage", ObjectSchema.ObjectType.EmbeddedObject)
            {
                Property.Primitive("Filename", RealmValueType.String, isNullable: true), Property.Object("File", "File"),
            },
            new ObjectSchema.Builder("File", ObjectSchema.ObjectType.RealmObject) { Property.Primitive("Hash", RealmValueType.String, isPrimaryKey: true) },
            new ObjectSchema.Builder("BeatmapCollection", ObjectSchema.ObjectType.RealmObject)
            {
                Property.Primitive("Name", RealmValueType.String, isNullable: true), Property.PrimitiveList("BeatmapMD5Hashes", RealmValueType.String, true),
            },
        }.Build();
        using var realm = Realm.GetInstance(new RealmConfiguration(path) { Schema = schema, IsDynamic = true });
        realm.Write(() =>
        {
            var set = realm.DynamicApi.CreateObject("BeatmapSet");
            set.DynamicApi.Set("OnlineID", 123);
            var meta = realm.DynamicApi.CreateObject("BeatmapMetadata");
            meta.DynamicApi.Set("Title", "Song");
            var map = realm.DynamicApi.CreateObject("Beatmap");
            map.DynamicApi.Set("DifficultyName", "Insane");
            map.DynamicApi.Set("Metadata", RealmValue.Object(meta));
            map.DynamicApi.Set("BeatmapSet", RealmValue.Object(set));
            set.DynamicApi.GetList<IRealmObject>("Beatmaps").Add((IRealmObject)map);
            var file = realm.DynamicApi.CreateObject("File", "abc");
            var use = realm.DynamicApi.AddEmbeddedObjectToList(set.DynamicApi.GetList<IEmbeddedObject>("Files"));
            use.DynamicApi.Set("Filename", "audio.mp3");
            use.DynamicApi.Set("File", RealmValue.Object(file));
            var col = realm.DynamicApi.CreateObject("BeatmapCollection");
            col.DynamicApi.Set("Name", "Mine");
            col.DynamicApi.GetList<string?>("BeatmapMD5Hashes").Add("h1");
            col.DynamicApi.GetList<string?>("BeatmapMD5Hashes").Add("h2");
        });
    }

    private const string Phase3 = """
        export async function start(ump) {
          const dir = await ump.files.pickFolder({ title: "osu!", start: "~/nothing" });
          ump.log("picked " + (dir != null));
          ump.log("list " + ump.files.list(dir).map(f => f.name).sort().join(","));
          ump.log("text " + ump.files.readText(dir + "/a.osu").split("\n")[0]);
          ump.log("bytes " + ump.files.readBytes(dir + "/audio.mp3").length);
          ump.log("head " + ump.files.readText(dir + "/a.osu", 7) + " md5 " + ump.files.hash(dir + "/audio.mp3") + " " + ump.files.hash(dir + "/audio.mp3", "sha256").length);
          const pic = ump.images.load(dir + "/a.osu");
          ump.images.free(pic);
          ump.log("freed " + (ump.images.load(dir + "/a.osu") !== pic));
          try { ump.files.readText(SECRET); ump.log("read outside"); } catch (e) { ump.log("refused outside"); }
          const top = await ump.files.pickFolder();
          try { ump.files.readText(top + "/" + DATA + "/settings.json"); ump.log("read app data"); } catch (e) { ump.log("refused app data"); }
          ump.log("zip " + ump.zip.entries(dir + "/set.osz").map(e => e.name).sort().join(","));
          ump.log("zipped " + ump.zip.readText(dir + "/set.osz", "x.osu"));
          const out = await ump.zip.extract(dir + "/set.osz");
          ump.log("extracted " + ump.files.exists(out + "/x.osu"));
          const id = await ump.library.add({ path: dir + "/audio.mp3", title: "T", artist: "A", cover: dir + "/a.osu", keys: ["set:1"] });
          ump.log("added " + id);
          const pl = await ump.playlists.create("Mine");
          ump.log("playlist " + (await ump.playlists.add(pl, [id])) + " " + ump.playlists.list()[0].songs.join());
          ump.store.setSong(id, { diff: "Insane" });
          ump.log("song data " + ump.store.song(id).diff);
          ump.importers.add({ id: "osz", title: "A beatmap", extensions: [".osz", ".mp3"], run: () => ump.log("import run"), open: p => ump.log("opened " + p.endsWith("set.osz")) });
          if (ump.has("realm")) {
            const db = ump.realm.open(dir + "/sub/client.realm");
            const sets = db.all("BeatmapSet", ["Beatmaps.Metadata.Title", "Files.File.Hash"]);
            ump.log("realm pages " + db.count("BeatmapSet") + " " + db.all("BeatmapSet", [], { skip: 1 }).length + " " + db.all("BeatmapSet", [], { skip: 0, take: 1 }).length);
            const b = sets[0].Beatmaps[0];
            ump.log("realm " + sets.length + " " + sets[0].OnlineID + " " + b.DifficultyName + " " + b.Metadata.Title + " " + sets[0].Files[0].Filename + " " + sets[0].Files[0].File.Hash + " " + (b.BeatmapSet === undefined));
            const cols = db.all("BeatmapCollection", ["BeatmapMD5Hashes"]);
            ump.log("collection " + cols[0].Name + " " + cols[0].BeatmapMD5Hashes.join("+"));
            db.close();
          }
          ump.log("phase3 done");
        }
        """;

    // What came with 6.0's sounds, page visualizers and sites: a folder named in mod.json, a sound cued with a song, sites and links
    // only from its hosts, the app's choice for videos, a page visualizer drawing where the cover would be, a tap.
    private const string More = """
        export async function start(ump) {
          const k = ump.files.known("here");
          ump.log("known " + (k != null) + " " + ump.files.exists(k + "/a.osu") + " " + ump.files.known("nope"));
          const w = new Float32Array(3200);
          for (let i = 0; i < w.length; i++) w[i] = Math.sin(i / 5) * 0.5;
          const s = ump.sounds.make(w, 32000);
          ump.log("cue " + ump.sounds.cue("s9", [[1000, s, 1, 0], [500, s, 0.5, -1], [700, {}, 1, 0]]) + " " + ump.sounds.cue("nobody", [[1, s]]));
          try { await ump.net.text("https://other.org/x"); ump.log("fetched other"); } catch (e) { ump.log("refused other"); }
          try { ump.links.add({ id: "x", match: ["other.org/maps/"], run: u => 0 }); ump.log("link other"); } catch (e) { ump.log("refused link"); }
          ump.links.add({ id: "maps", title: "Maps", match: ["example.com/maps/"], run: u => ump.log("link " + u) });
          ump.log("videos " + ump.prefs.get("videos"));
          ump.visualizers.add({
            id: "page", title: "Page", fill: "page", press: (x, y) => ump.log("press " + x + "," + y), chip: () => ump.log("chip pressed"),
            frame: (ctx, f) => {
              ctx.setOrigin(f.stage.x, f.stage.y);
              ctx.fillRect(0, 0, f.stage.width, 10);
              ctx.setTransform(1, 0, 0, 1, 5, 5);
              ctx.fillRect(0, 0, 1, 1);
              ctx.setOrigin(0, 0);
              ctx.fillRect(0, 0, f.width, f.height);
            },
          });
          try { ump.visualizers.add({ id: "bad", place: "bars", fill: "page", frame: () => 0 }); ump.visualizers.video("bad", "x.mp4", 0); ump.log("bars video"); } catch (e) { ump.log("refused bars video"); }
          ump.visualizers.chip("page", "Insane · 5.2★");
          try { ump.visualizers.chip("bad", "x"); ump.log("bars chip"); } catch (e) { ump.log("refused bars chip"); }
          ump.log("pick " + await ump.ui.pick("T", "M", ["a", { title: "b", hint: "h" }], 0, "Go"));
          ump.search.add({ id: "maps", title: "Maps", run: async w => [
            { title: "One " + w, artist: "A", hint: "h", url: "https://example.com/maps/1", picture: "https://example.com/c.jpg", duration: 90 },
            { title: "Another site", url: "https://other.org/maps/2" },
            { title: "Not one of its links", url: "https://example.com/else/3" },
            { title: "Picture from elsewhere", url: "https://www.example.com/maps/4", picture: "https://other.org/p.jpg" },
          ] });
          ump.log("more done");
        }
        """;

    private const string Main = """
        import { double } from "./lib/util.js";
        export function start(ump) {
          ump.log("hello", double(21));
          ump.settings.define([
            { id: "on", type: "switch", title: "On", default: true },
            { id: "speed", type: "slider", title: "Speed", min: 1, max: 5 },
            { id: "mode", type: "choice", title: "Mode", choices: [{ value: "a", title: "A" }, { value: "b", title: "B" }] },
          ]);
          ump.store.set("runs", (ump.store.get("runs") || 0) + 1);
          ump.events.on("song", s => ump.log("song " + s.title + " path " + s.path));
          ump.menus.song.add({ title: "Only long", when: s => s.duration > 100, run: s => ump.ui.toast("long " + s.title) });
          ump.menus.song.add({ title: "Always", run: s => ump.ui.toast("always " + s.title) });
          ump.settings.onChange((id, v) => ump.log("changed " + id + "=" + v));
          setTimeout(() => ump.log("timer"), 10);
          try { ump.player.play(); } catch (e) { ump.log("refused player"); }
          try { ump.library.songs(); } catch (e) { ump.log("refused library"); }
          ump.log("System is " + typeof System + ", importNamespace is " + typeof importNamespace);
          ump.ui.confirm("Title", "Sure?").then(ok => ump.log("confirm " + ok));
        }
        export function stop() { ump.log("bye"); }
        """;

    private static string Zip(string dir, string name, Dictionary<string, string> files)
    {
        var path = Path.Combine(dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, text) in files)
            using (var w = new StreamWriter(zip.CreateEntry(entry).Open())) w.Write(text);
        return path;
    }

    private static string Manifest(string id, string extra = "") =>
        $$"""{ "id": "{{id}}", "name": "Test", "version": "1.0.0", "main": "main.js"{{extra}} }""";

    private static bool Wait(Func<bool> done, int ms = 4000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!done()) { if (DateTime.UtcNow > until) return false; Thread.Sleep(20); }
        return true;
    }

    public static int Run()
    {
        var fails = new List<string>();
        void Check(bool ok, string what) { if (!ok) fails.Add(what); }
        var work = Path.Combine(Path.GetTempPath(), "ump-mods-check");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        ModRuntime.CallLimit = TimeSpan.FromSeconds(1);
        var host = new Host();
        var settings = new AppSettings();
        using var mods = new ModManager(settings, host);

        // Zipped as a folder (one level down), with a module in a subfolder.
        var file = Zip(work, "good.umpmod", new()
        {
            ["test-mod/mod.json"] = Manifest("check.test"),
            ["test-mod/main.js"] = Main,
            ["test-mod/lib/util.js"] = "export const double = x => x * 2;",
        });
        Check(ModPackage.Peek(file).Id == "check.test", "peek");
        var mod = mods.Install(file);
        bool Logged(ModRuntime m, string text) => m.Log.Any(l => l.Contains(text));
        Check(File.Exists(Path.Combine(ModPackage.ModsDir, "check.test", "lib", "util.js")), "installed files");
        Check(Wait(() => Logged(mod, "hello 42") && Logged(mod, "timer") && Logged(mod, "confirm true")), "start, module, timer, promise");
        Check(Logged(mod, "refused player") && Logged(mod, "refused library"), "permissions not granted are refused");
        Check(Logged(mod, "System is undefined, importNamespace is undefined"), "no .NET from a script");
        Check(mod.Settings.Count == 3 && Equals(mod.SettingValue("on"), true) && Equals(mod.SettingValue("mode"), "a"), "settings and their defaults");
        mod.SetSettingValue("speed", 3.0);
        Check(Wait(() => Logged(mod, "changed speed=3")), "a setting changed by the app reaches the mod");
        var song = new ModSong("t1", "Long one", "Someone", null, null, 200, null, null, null, "C:\\music\\a.mp3", DateTime.Now, false);
        mods.Emit("song", song);
        Check(Wait(() => Logged(mod, "song Long one path undefined")), "events, and no file path without library.read");
        Check(mods.SongEntries(song).Count() == 2 && mods.SongEntries(song with { Duration = 50 }).Count() == 1, "menu entries and their when");
        mod.RunEntry(mods.SongEntries(song).First(e => e.Entry.Title == "Always").Entry, song);
        Check(Wait(() => { lock (host.Toasts) return host.Toasts.Contains("always Long one"); }), "a menu entry runs");

        // Stopped (its stop runs, its store is saved), started again: the store kept its value.
        mods.SetEnabled(mod, false);
        Check(mod.Status == ModStatus.Off && Logged(mod, "bye") && settings.ModsOff.Contains("check.test"), "switched off");
        Check(File.ReadAllText(Path.Combine(ModPackage.DataDirOf("check.test"), "store.json")).Contains("runs"), "store saved");
        mods.SetEnabled(mod, true);
        Check(Wait(() => Logged(mod, "hello 42")) && mods.Mods[0].Status == ModStatus.Running, "switched on again");
        mod = mods.Reload(mod);
        Check(Wait(() => Logged(mod, "hello 42")), "reloaded");

        // A start that never ends: cut short and the mod marked as failed.
        var loop = mods.Install(Zip(work, "loop.umpmod", new() { ["mod.json"] = Manifest("check.loop"), ["main.js"] = "export function start() { while (true) {} }" }));
        Check(Wait(() => loop.Status == ModStatus.Failed, 5000), "an endless start is stopped");

        // Refused files.
        void Refused(string name, Dictionary<string, string> files, string what)
        {
            try { mods.Install(Zip(work, name, files)); fails.Add(what); }
            catch (ModException) { }
        }
        Refused("slip.umpmod", new() { ["mod.json"] = Manifest("check.slip"), ["main.js"] = "", ["../evil.js"] = "" }, "a file outside the mod's folder");
        Check(!File.Exists(Path.Combine(ModPackage.ModsDir, "evil.js")) && !Directory.Exists(Path.Combine(ModPackage.ModsDir, "check.slip")), "nothing left by a refused zip");
        Refused("id.umpmod", new() { ["mod.json"] = Manifest("Bad Id"), ["main.js"] = "" }, "a wrong id");
        Refused("api.umpmod", new() { ["mod.json"] = Manifest("check.api", ", \"api\": 99"), ["main.js"] = "" }, "a newer api");
        Refused("perm.umpmod", new() { ["mod.json"] = Manifest("check.perm", ", \"permissions\": [\"everything\"]"), ["main.js"] = "" }, "an unknown permission");
        Refused("nomain.umpmod", new() { ["mod.json"] = Manifest("check.nomain") }, "a missing main script");
        Refused("notzip.umpmod", new(), "a zip without mod.json");

        // A developer's folder: loaded again when a file changes.
        var dev = Path.Combine(work, "dev");
        Directory.CreateDirectory(dev);
        File.WriteAllText(Path.Combine(dev, "mod.json"), Manifest("check.dev"));
        File.WriteAllText(Path.Combine(dev, "main.js"), "ump.log('v1');");
        var d = mods.AddFolder(dev);
        Check(Wait(() => Logged(d, "v1")), "a folder loaded");
        File.WriteAllText(Path.Combine(dev, "main.js"), "ump.log('v2');");
        Check(Wait(() => mods.Mods.FirstOrDefault(m => m.Id == "check.dev") is { } now && Logged(now, "v2")), "a folder loaded again when it changes");

        // A visualizer: what its frame draws, as the renderers get it.
        const string Vis = """
            const ring = new Path2D();
            ring.arc(0, 0, 10, 0, Math.PI * 2);
            let opened = 0;
            export function start(ump) {
              ump.visualizers.add({
                id: "test", title: "Test visual", place: "song",
                canShow: s => s.duration > 100,
                open: () => { opened++; },
                frame: (ctx, f) => {
                  ctx.fillStyle = "#ff0000";
                  ctx.fillRect(0, 0, f.width, f.height);
                  ctx.save();
                  ctx.translate(50, 20);
                  ctx.rotate(Math.PI / 2);
                  ctx.beginPath(); ctx.rect(0, 0, 10, 10); ctx.clip();
                  ctx.globalAlpha = 0.5;
                  ctx.globalCompositeOperation = "lighter";
                  ctx.strokeStyle = "rgba(0, 255, 0, 0.5)"; ctx.lineWidth = 3; ctx.lineCap = "round";
                  ctx.stroke(ring);
                  ctx.restore();
                  const g = ctx.createLinearGradient(0, 0, 100, 0);
                  g.addColorStop(0, "white"); g.addColorStop(1, "#0000ff80");
                  ctx.fillStyle = g;
                  ctx.beginPath(); ctx.arc(30, 30, 5, 0, Math.PI); ctx.fill();
                  ctx.imageTint = "#ff8000";
                  ctx.drawImage(ump.images.load("dot.png"), 1, 2, 3, 4, 5, 6, 7, 8);
                  ctx.globalAlpha = 0.25; ctx.filter = "blur(3px)";
                  ctx.beginLayer();
                  ctx.restore();
                  ctx.globalCompositeOperation = "destination-out";
                  ctx.fillRect(0, 0, 4, 4);
                  ctx.endLayer();
                  ctx.log = ctx.globalAlpha + " " + ctx.filter;
                  ctx.globalAlpha = 1; ctx.filter = "none";
                  ctx.font = "bold 20px sans-serif"; ctx.textAlign = "center";
                  ctx.fillText(f.song.title + " " + f.bands.length + " " + opened + " " + Math.round(f.time) + " " + ctx.log, 10, 10);
                },
              });
              const before = ump.visualizers.shown("test");
              ump.visualizers.show("test");
              ump.log("shown " + before + " " + ump.visualizers.shown("test"));
            }
            """;
        var vm = mods.Install(Zip(work, "vis.umpmod", new() { ["mod.json"] = Manifest("check.vis"), ["main.js"] = Vis, ["dot.png"] = "not really a png" }));
        Check(Wait(() => vm.Visualizers.Count == 1), "a visualizer added");
        Check(Wait(() => Logged(vm, "shown false true")) && host.Chosen.Contains("check.vis/test"), "a mod's visualizer chosen by the mod");
        if (vm.Visualizers.FirstOrDefault() is { } visual)
        {
            bool? can = null, cannot = null;
            vm.AskCanShow(visual, song, b => can = b);
            vm.AskCanShow(visual, song with { Duration = 50 }, b => cannot = b);
            Check(Wait(() => can != null && cannot != null) && can == true && cannot == false, "canShow");
            host.Song = song;
            using var scene = vm.OpenScene(visual);
            var bands = Enumerable.Range(0, Spectrum.BandCount).Select(i => i / 96f).ToArray();
            Check(scene.Request(new ModFrameInfo { Time = 1234, Width = 200, Height = 100, Bands = bands, Playing = true }), "a frame asked");
            Check(Wait(() => scene.Latest != null), "a frame drawn");
            var c = scene.Latest?.Cmds ?? new List<DrawCmd>();
            var ops = string.Join(",", c.Select(x => x.Op));
            Check(ops == "Rect,ClipPush,Path,ClipPop,Path,Image,LayerPush,Rect,LayerPop,Text", "the frame's commands: " + ops);
            if (c.Count == 10)
            {
                Check(c[0] is { Color: 0xFFFF0000, W: 200, H: 100, Stroke: false }, "fillRect with the frame's size and its colour");
                var p = System.Numerics.Vector2.Transform(new(10, 0), c[2].M);
                Check(Math.Abs(p.X - 50) < 0.01 && Math.Abs(p.Y - 30) < 0.01, $"translate then rotate (10,0 -> {p})");
                Check(c[2] is { Stroke: true, Additive: true, Alpha: 0.5f, Color: 0x8000FF00, LineWidth: 3, Cap: LineCap.Round } && c[2].Path!.Segs.Count(s => s.Kind == SegKind.Cubic) == 4,
                    "stroke of a Path2D circle: alpha, lighter, rgba, width, cap, four curves");
                Check(c[4].Gradient is { Stops.Count: 2 } gr && gr.Stops[1].Color == 0x800000FF && !c[4].Additive && c[4].Alpha == 1, "a gradient, and restore() puts alpha and blending back");
                Check(c[5] is { Tint: 0xFFFF8000, SX: 1, SY: 2, SW: 3, SH: 4, X: 5, Y: 6, W: 7, H: 8 } && c[5].Image is { Width: 16, Height: 8 }, "drawImage with a part of a tinted picture");
                Check(c[6] is { Alpha: 0.25f, Blur: 3, Blend: ModBlend.SourceOver } && c[7] is { Alpha: 1, Blur: 0, Blend: ModBlend.DestinationOut } && c[8].Op == DrawOp.LayerPop,
                    "a layer keeps alpha and blur for when it's put down, starts from the defaults inside, restore() doesn't leave it");
                Check(c[9] is { Bold: true, FontSize: 20, Align: TextAlign.Center, Blur: 0, Text: "Long one 96 1 1234 0.25 blur(3px)" },
                    "text: font, align, the song, bands, open once, time; endLayer() puts the state back");
            }
            // A second frame: open isn't called again, the list is new.
            var first = scene.Latest;
            scene.Request(new ModFrameInfo { Time = 2000, Width = 200, Height = 100, Bands = bands });
            Check(Wait(() => scene.Latest != first) && scene.Latest!.Cmds[^1].Text == "Long one 96 1 2000 0.25 blur(3px)", "the next frame");
        }
        Check(CssColor.Parse("#abc") == 0xFFAABBCC && CssColor.Parse("#11223344") == 0x44112233 && CssColor.Parse("rgb(255, 0, 0)") == 0xFFFF0000
              && CssColor.Parse("rgba(0,0,255,0.5)") == 0x800000FF && CssColor.Parse("nonsense") == null, "CSS colours");

        // Phase 3: what the user gives a mod (a folder, its files, a zip), songs and playlists into the library, data per song,
        // importers and the files they open, a realm read like osu! lazer's.
        var given = Path.Combine(work, "given");
        Directory.CreateDirectory(Path.Combine(given, "sub"));
        File.WriteAllText(Path.Combine(given, "a.osu"), "osu file format v14\n[General]\nAudioFilename: audio.mp3\n");
        File.WriteAllBytes(Path.Combine(given, "audio.mp3"), new byte[] { 1, 2, 3 });
        var secret = Path.Combine(work, "secret.txt");
        File.WriteAllText(secret, "no");
        var osz = Zip(given, "set.osz", new() { ["x.osu"] = "hi", ["audio.mp3"] = "mp3" });
        MakeRealm(Path.Combine(given, "sub", "client.realm"));
        ModRuntime.Realm = new UltimateMP3Player.RealmReader.RealmReader();
        host.MusicDir = Path.Combine(work, "music");
        host.Picks.Enqueue(given);
        host.Picks.Enqueue(Path.GetDirectoryName(Path.GetFullPath(AppPaths.DataDir).TrimEnd(Path.DirectorySeparatorChar)));
        ModRuntime.CallLimit = TimeSpan.FromSeconds(5);
        var dataName = Path.GetFileName(Path.GetFullPath(AppPaths.DataDir).TrimEnd(Path.DirectorySeparatorChar));
        var files = mods.Install(Zip(work, "files.umpmod", new()
        {
            ["mod.json"] = Manifest("check.files", ", \"permissions\": [\"files\", \"library.read\", \"library.write\"]"),
            ["main.js"] = Phase3.Replace("SECRET", JsonSerializer.Serialize(secret)).Replace("DATA", JsonSerializer.Serialize(dataName)),
        }));
        Check(Wait(() => Logged(files, "phase3 done") || files.Status != ModStatus.Running, 15000) && Logged(files, "phase3 done"),
            "phase 3 mod ran to its end" + (files.Log.LastOrDefault() is { } last ? " (" + last + ")" : ""));
        Check(Logged(files, "picked true") && Logged(files, "list a.osu,audio.mp3,set.osz,sub") && Logged(files, "text osu file format v14") && Logged(files, "bytes 3"),
            "a folder picked: listed, a text and bytes read");
        Check(Logged(files, "refused outside") && Logged(files, "refused app data"), "nothing outside what was picked, never the app's data");
        Check(Logged(files, "head osu fil md5 5289df737df57326fcdd22597afb1fac 64"), "a file's start read, its md5 and sha256");
        Check(Logged(files, "freed true"), "a picture freed (loaded again as a new one)");
        Check(Logged(files, "zip audio.mp3,x.osu") && Logged(files, "zipped hi") && Logged(files, "extracted true")
              && File.Exists(Path.Combine(host.MusicDir, "Test", "set", "x.osu")), "a zip read and extracted into the music folder");
        Check(Logged(files, "added s1") && host.Added.FirstOrDefault() is { Title: "T", Artist: "A" } added && added.Keys.SequenceEqual(new[] { "check.files:set:1" })
              && added.Cover?.EndsWith("a.osu") == true, "a song added, with its words, cover and the mod's keys");
        Check(Logged(files, "playlist true s1"), "a playlist made, the song in it");
        Check(Logged(files, "song data Insane"), "data kept for a song");
        Check(Logged(files, "realm 1 123 Insane Song audio.mp3 abc true") && Logged(files, "collection Mine h1+h2"),
            "a realm read: values, paths through lists, embedded objects, links; not where no path goes");
        Check(Logged(files, "realm pages 1 0 1"), "a realm read by pages: count, skip, take");
        Check(mods.Importers.Any(i => i.Title == "A beatmap") && mods.OpenerOf(Path.Combine(work, "x.mp3")) == null, "an importer, never for the songs' own types");
        if (mods.OpenerOf(osz) is { } opener) opener.Mod.OpenWithImporter(opener, osz);
        if (mods.Importers.FirstOrDefault(i => i.Title == "A beatmap") is { } imp) files.RunImporter(imp);
        Check(Wait(() => Logged(files, "opened true") && Logged(files, "import run")), "a dropped .osz opened by the mod, its importer run");
        mods.ForgetSong("s1");
        mods.SetEnabled(files, false);
        var kept = File.ReadAllText(Path.Combine(files.DataDir, "store.json"));
        Check(!kept.Contains("song:s1") && File.ReadAllText(Path.Combine(files.DataDir, "places.json")).Contains("given"), "a song's data gone with it, the places picked remembered");

        // 6.0: sounds, known folders, sites and links, the app's choices, page visualizers, the catalogue, WAV files.
        host.Song = new ModSong("s9", "Cued", null, null, null, 200, null, null, null, Path.Combine(given, "audio.mp3"), DateTime.Now, false);
        var more = mods.Install(Zip(work, "more.umpmod", new()
        {
            ["mod.json"] = Manifest("check.more", ", \"permissions\": [\"files\", \"library.read\", \"internet\"], \"hosts\": [\"example.com\"], \"places\": [{ \"id\": \"here\", \"title\": \"Here\", \"paths\": [" + JsonSerializer.Serialize(given) + ", \"%NO_SUCH_VARIABLE%/x\"] }]"),
            ["main.js"] = More,
        }));
        Check(Wait(() => Logged(more, "more done") || more.Status != ModStatus.Running, 15000) && Logged(more, "more done"),
            "6.0 mod ran to its end" + (more.Log.LastOrDefault() is { } lastMore ? " (" + lastMore + ")" : ""));
        Check(Logged(more, "known true true null"), "a folder named in mod.json read without asking; another not there");
        Check(Logged(more, "cue true false") && host.Cues is { Times.Length: 2 } cues && cues.Times[0] == 0.5 && cues.Times[1] == 1
              && cues.Path == Path.Combine(given, "audio.mp3") && Math.Abs(cues.Left[0] - 0.7071f) < 0.001 && cues.Right[0] < 0.0001,
            "sounds cued with the song's file: sorted, in seconds, panned; a song that isn't here refused");
        Check(Logged(more, "refused other") && Logged(more, "refused link"), "a site and links only from mod.json's hosts");
        Check(Logged(more, "videos false"), "the app's choice for videos");
        Check(Logged(more, "refused bars video"), "a video only under a page visualizer");
        Check(mods.LinkOf("https://www.example.com/maps/12?x=1") is { Id: "maps" } && mods.LinkOf("https://example.com/other/1") == null, "a pasted link to the mod that takes it");
        if (mods.LinkOf("https://example.com/maps/12") is { } link) more.RunLink(link, "https://example.com/maps/12 ");
        Check(Wait(() => Logged(more, "link https://example.com/maps/12")), "the link run by the mod");
        if (more.Visualizers.FirstOrDefault(v => v.Id == "page") is { } pageVis)
        {
            using var scene = more.OpenScene(pageVis);
            Check(pageVis.Page && pageVis.CanPress, "a page visualizer that takes taps");
            scene.Request(new ModFrameInfo { Time = 0, Width = 400, Height = 300, StageX = 100, StageY = 50, StageWidth = 200, StageHeight = 150 });
            Check(Wait(() => scene.Latest != null), "a page frame drawn");
            var pc = scene.Latest?.Cmds ?? new List<DrawCmd>();
            Check(pc.Count == 3 && pc[0] is { W: 200, H: 10 } && pc[0].M.Translation == new System.Numerics.Vector2(100, 50)
                  && pc[1].M.Translation == new System.Numerics.Vector2(105, 55) && pc[2] is { W: 400, H: 300 } && pc[2].M.Translation == System.Numerics.Vector2.Zero,
                "the stage where the cover is, setOrigin under setTransform, back to the corner");
            scene.Press(12, 34);
            Check(Wait(() => Logged(more, "press 12,34")), "a tap on the page");
            Check(pageVis.Chip == "Insane · 5.2★" && Logged(more, "refused bars chip"), "a page visualizer's chip; none on bars");
            pageVis.PressChip();
            Check(Wait(() => Logged(more, "chip pressed")), "the chip pressed: to the mod");
        }
        Check(Logged(more, "pick 1"), "a choice then its button (ui.pick)");
        if (mods.Searches.FirstOrDefault() is { Title: "Maps" } search)
        {
            var hits = search.Mod.SearchAsync(search, "x", CancellationToken.None);
            Check(hits.Wait(5000) && hits.Result.Count == 2 && hits.Result[0] is { Title: "One x", Artist: "A", Hint: "h", Url: "https://example.com/maps/1", Picture: "https://example.com/c.jpg", Duration: 90 }
                  && hits.Result[1] is { Url: "https://www.example.com/maps/4", Picture: null },
                "a mod's search: only its own links, pictures only from its sites");
        }
        else Check(false, "a mod's search added");
        // An MP3's lead: ID3 tag, then a frame with an Info (constant bitrate) header and LAME's tag, its encoder delay 576.
        byte[] Mp3(string tag)
        {
            var b = new byte[30 + 400];
            "ID3"u8.ToArray().CopyTo(b, 0);
            b[3] = 3; b[9] = 20;
            int f = 30;
            b[f] = 0xFF; b[f + 1] = 0xFB; b[f + 2] = 0x90;
            System.Text.Encoding.ASCII.GetBytes(tag).CopyTo(b, f + 36);
            b[f + 36 + 7] = 0x0F;
            int lame = f + 36 + 8 + 4 + 4 + 100 + 4;
            "LAME3.100"u8.ToArray().CopyTo(b, lame);
            b[lame + 21] = 0x24;
            return b;
        }
        var info = Path.Combine(work, "info.mp3");
        var xing = Path.Combine(work, "xing.mp3");
        File.WriteAllBytes(info, Mp3("Info"));
        File.WriteAllBytes(xing, Mp3("Xing"));
        Check(Mp3Gapless.Read(info) is { Tag: "Info", Lame: true, EncoderDelay: 576, FrameSamples: 1152, Rate: 44100 }
              && Math.Abs(Mp3Gapless.MediaFoundationLead(info).TotalSeconds - 1728 / 44100.0) < 1e-6
              && Math.Abs(Mp3Gapless.MediaFoundationLead(xing).TotalSeconds - 576 / 44100.0) < 1e-6
              && Math.Abs(Mp3Gapless.NLayerLead(info).TotalSeconds - 1105 / 44100.0) < 1e-6,
            "an MP3's lead: Info frame and encoder delay (Media Foundation), delays (NLayer)");
        // After a jump Media Foundation is at the start of the frame the time falls in (measured on real files: 30 s in, the
        // music 1080 samples early, within a sample); a VBR file is left as it is.
        var jump = TimeSpan.FromSeconds(30) + Mp3Gapless.MediaFoundationLead(info);
        Check(Math.Abs((jump - Mp3Gapless.MediaFoundationSeek(Mp3Gapless.Read(info), jump)).TotalSeconds - 1080 / 44100.0) < 2.0 / 44100
              && Mp3Gapless.MediaFoundationSeek(Mp3Gapless.Read(xing), jump) == jump && Mp3Gapless.MediaFoundationSeek(null, jump) == jump,
            "an MP3 after a jump: Media Foundation at its frame's start");
        var catalogue = ModCatalogue.Parse("""
            { "mods": [
              { "id": "kirbo.osu", "name": "osu!", "version": "1.1.0", "url": "https://example.com/a.umpmod", "sha256": "HASH", "platforms": [ ], "minApp": "6.0.0", "official": true, "descriptions": { "it": "Nell\u0027app" } },
              { "id": "too.new", "name": "New", "version": "1", "url": "https://example.com/b", "sha256": "HASH", "minApp": "99.0" },
              { "id": "not.https", "name": "Http", "version": "1", "url": "http://example.com/c", "sha256": "HASH" },
              { "id": "else.where", "name": "Else", "version": "1", "url": "https://example.com/d", "sha256": "HASH", "platforms": ["nowhere"] },
              { "id": "bad.hash", "name": "Bad", "version": "1", "url": "https://example.com/e", "sha256": "xyz" },
              { "id": "Bad Id", "name": "Id", "version": "1", "url": "https://example.com/f", "sha256": "HASH" }
            ] }
            """.Replace("HASH", new string('a', 64)), new Version(6, 0, 0));
        Check(catalogue.Count == 1 && catalogue[0] is { Id: "kirbo.osu", Official: true, MinApp: "6.0.0" } && catalogue[0].Descriptions["it"] == "Nell'app",
            "the catalogue: only well-formed https entries for this device and version");
        Check(ModCatalogue.IsNewer("1.2.0", "1.1.9") && !ModCatalogue.IsNewer("1.0", "1.0") && ModCatalogue.IsNewer("beta", "1.0"), "a newer version in the catalogue");
        static ModManifest Wants(string perms, string hosts, string paths) => ModManifest.Parse(
            $$"""{ "id": "a.b", "name": "B", "version": "1", "permissions": [{{perms}}], "hosts": [{{hosts}}], "places": [{ "id": "osu", "title": "osu!", "paths": [{{paths}}] }] }""");
        var was = Wants("\"files\", \"internet\"", "\"osu.ppy.sh\"", "\"%APPDATA%/osu\"");
        Check(!Wants("\"files\", \"internet\"", "\"osu.ppy.sh\"", "\"%APPDATA%/osu\"").AsksMoreThan(was, was.Permissions)
              && !Wants("\"internet\"", "\"osu.ppy.sh\"", "").AsksMoreThan(was, was.Permissions)
              && Wants("\"files\", \"internet\"", "\"osu.ppy.sh\", \"catboy.best\"", "\"%APPDATA%/osu\"").AsksMoreThan(was, was.Permissions)
              && Wants("\"files\", \"internet\", \"library.write\"", "\"osu.ppy.sh\"", "\"%APPDATA%/osu\"").AsksMoreThan(was, was.Permissions)
              && Wants("\"files\", \"internet\"", "\"osu.ppy.sh\"", "\"%APPDATA%/osu\", \"~/.local/share/osu\"").AsksMoreThan(was, was.Permissions),
            "an update asking for nothing new (installed by itself), or for a permission, a site, a folder (asked first)");
        // A WAV of 4 samples, 16 bits, one channel at 8000 Hz: read as stereo floats.
        var wav = new List<byte>();
        void Add32(int v) => wav.AddRange(BitConverter.GetBytes(v));
        void Add16(short v) => wav.AddRange(BitConverter.GetBytes(v));
        wav.AddRange("RIFF"u8.ToArray()); Add32(36 + 8); wav.AddRange("WAVE"u8.ToArray());
        wav.AddRange("fmt "u8.ToArray()); Add32(16); Add16(1); Add16(1); Add32(8000); Add32(16000); Add16(2); Add16(16);
        wav.AddRange("data"u8.ToArray()); Add32(8); Add16(0); Add16(16384); Add16(-32768); Add16(32767);
        Check(Wav.Read(wav.ToArray()) is { Rate: 8000 } w && w.Samples.Length == 8 && Math.Abs(w.Samples[2] - 0.5f) < 0.001 && w.Samples[3] == w.Samples[2] && w.Samples[4] <= -0.99f
              && Wav.Read(new byte[] { 1, 2, 3 }) == null, "a WAV read (16 bits, mono to stereo); not a WAV refused");
        mods.SetEnabled(more, false);
        Check(host.Cues == null, "a mod switched off: its sounds gone");

        // Removed with its data.
        mods.Remove(mods.Mods.First(m => m.Id == "check.test"), data: true);
        Check(!Directory.Exists(Path.Combine(ModPackage.ModsDir, "check.test")) && !Directory.Exists(ModPackage.DataDirOf("check.test")) && !settings.ModGranted.ContainsKey("check.test"), "removed with its data");

        foreach (var f in fails) Console.WriteLine("  WRONG: " + f);
        Console.WriteLine("mods: " + (fails.Count == 0 ? "ok" : "SBAGLIATO"));
        return fails.Count == 0 ? 0 : 1;
    }
}
