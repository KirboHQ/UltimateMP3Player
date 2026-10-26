// Developer harness for the Core, without the UI (set UMP_DATA to use a throw-away library).
//   mp3cli download <url> [--items 1,3] [--video] [--original] [--out dir] [--cookies firefox]
//   mp3cli import <file-or-folder>
//   mp3cli list
//   mp3cli songlist [file]
//   mp3cli shuffle <n> <rounds>
using System.Diagnostics;
using UltimateMP3Player.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Length < 1) { Console.WriteLine("mp3cli download|import|list|shuffle …"); return 1; }

string? Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
bool Flag(string name) => args.Contains(name);

var lib = Library.Load();
var sw = Stopwatch.StartNew();

switch (args[0])
{
    case "list":
        foreach (var t in lib.Snapshot())
            Console.WriteLine($"{t.Id} | {t.Artist} - {t.Title} | {t.Album} | {Text.Duration(t.Duration)} | LUFS {t.Loudness:0.0} | cover={t.HasCover} wave={t.Wave?.Length} | video={t.VideoPath != null}\n    {t.Path}\n    keys: {string.Join(", ", t.Keys)}");
        return 0;

    case "import":
        foreach (var f in Importer.Expand(args.Skip(1)))
        {
            var t = await Importer.ImportAsync(lib, f, CancellationToken.None);
            Console.WriteLine($"[{sw.Elapsed.TotalSeconds:0.0}s] {t.Artist} - {t.Title} ({Text.Duration(t.Duration)}) LUFS {t.Loudness:0.0} cover={t.HasCover}");
        }
        lib.SaveNow();
        return 0;

    case "queue":
        {
            var q = new PlayQueue();
            string Show() => $"ora {q.Current} | coda [{string.Join(",", q.UpNext)}] | poi [{string.Join(",", q.Upcoming(99).Where(e => !e.Queued).Select(e => e.Id))}]";
            q.Play(new[] { "A", "B", "C", "D", "E", "F" }, 1, "pl", "Playlist");
            Console.WriteLine("play B:            " + Show());
            q.Enqueue("X"); q.Enqueue("Y");
            Console.WriteLine("+ coda X,Y:        " + Show());
            q.Move(4, 0);
            Console.WriteLine("sposta E in cima:  " + Show());
            q.RemoveAt(2);
            Console.WriteLine("togli il 3°:       " + Show());
            Console.WriteLine("next -> " + q.Next(false) + ":         " + Show());
            Console.WriteLine("jump 3° -> " + q.JumpTo(2) + ":     " + Show());
            Console.WriteLine("prev -> " + q.Previous() + ":         " + Show());
            Console.WriteLine("next -> " + q.Next(false) + ":         " + Show());
            q.Move(0, 1);
            Console.WriteLine("sposta 1° dopo 2°: " + Show());
            q.Forget("F");
            Console.WriteLine("elimina F:         " + Show());
            while (q.Next(true) is { } id) Console.Write(id + " ");
            Console.WriteLine("(fine, ripeti off)");
            // The song playing tapped in another list goes on: what follows is that list, it leaves the queue, one step back.
            var s = new PlayQueue();
            s.Play(new[] { "A", "B", "C" }, 0, "one", "One");
            s.Next(false);
            s.Enqueue("B");
            s.Play(new[] { "P", "B", "R" }, 1, "two", "Two");
            bool ok = s.Current == "B" && s.ContextId == "two" && s.UpNext.Count == 0 && s.PeekNext() == "R" && s.Previous() == "A";
            Console.WriteLine("stesso brano, altra lista: " + (ok ? "ok" : "SBAGLIATO"));
            return ok ? 0 : 1;
        }

    case "same":
        {
            // mp3cli same "<title A>" "<artist A>" "<title B>" "<artist B>"
            var a = SongMeta.Identity(args[1], args[2]);
            var b = SongMeta.Identity(args[3], args[4]);
            Console.WriteLine($"A: [{string.Join("|", a.Titles)}] by [{string.Join("|", a.Artists)}]");
            Console.WriteLine($"B: [{string.Join("|", b.Titles)}] by [{string.Join("|", b.Artists)}]");
            Console.WriteLine(SongMeta.SameSong(a, b) ? "same song" : "different");
            return 0;
        }

    case "similar":
        {
            // Library.FindSimilar's title index (set UMP_DATA: songs are added and removed): found, renamed, artist renamed, removed.
            var a = new Track { Title = "Heroes Tonight (Official Video)", Artist = "Janji", Duration = 208, SourceUrl = "https://example.com/a" };
            lib.FindSimilar("x", "y", 1);
            lib.Add(a);
            bool found = lib.FindSimilar("Heroes Tonight", "Janji feat. Johnning", 210) == a && lib.FindSimilar("Heroes Tonight", "Janji", 215) == null;
            a.Title = "Another Name";
            lib.Changed(a);
            bool renamed = lib.FindSimilar("Heroes Tonight", "Janji", 208) == null && lib.FindSimilar("Another Name", "Janji", 208) == a;
            lib.RenameArtist("Janji", "Janji Music");
            bool artist = lib.FindSimilar("Another Name", "Janji Music", 208) == a && lib.FindSimilar("Another Name", "Janji", 208) == null;
            lib.Remove(a);
            bool removed = lib.FindSimilar("Another Name", "Janji Music", 208) == null;
            bool ok = found && renamed && artist && removed;
            Console.WriteLine($"similar: {(ok ? "ok" : "SBAGLIATO")} (found={found} renamed={renamed} artist={artist} removed={removed})");
            return ok ? 0 : 1;
        }

    case "match":
        {
            // YouTube Music's results for "YOASOBI Racing Into The Night" (the English title of a Japanese song): the original,
            // first and written in Japanese, beats the English version and the piano cover; asked for, the English version
            // wins; the first result by someone else isn't taken.
            YtInfo Info(string artist, double d) => new() { Artist = artist, Duration = d };
            var results = new[] { ("夜に駆ける", Info("YOASOBI", 262)), ("夜に駆ける", Info("YOASOBI", 259)), ("Into The Night", Info("YOASOBI", 260)),
                ("Racing Into the Night [Yoru Ni Kakeru - 夜に駆ける] (Piano Version)", Info("DPSM", 263)), ("Run Into The Night", Info("Adgy & Éiru", 179)) };
            // (as MusicMatcher.FindAsync: the 3 best titles read, the best total taken)
            int Pick(string title, double d) => results.Select((r, i) => (i, r))
                .OrderByDescending(x => MusicMatcher.ResultScore(title, "YOASOBI", x.r.Item1, x.i)).Take(3)
                .OrderByDescending(x => MusicMatcher.Score(title, "YOASOBI", d, x.r.Item1, x.i, x.r.Item2).Total).First().i;
            bool ok = Pick("Racing Into The Night", 262) == 0 && Pick("Into The Night", 260) == 2 &&
                      MusicMatcher.Score("Racing Into The Night", "YOASOBI", 262, "夜に駆ける", 0, Info("Someone Else", 262)).Total <= 0.3;
            Console.WriteLine("match: " + (ok ? "ok" : "SBAGLIATO"));
            return ok ? 0 : 1;
        }

    case "notes":
        {
            // mp3cli notes [owner/repo]: the parser on a sample, then the real releases' notes as the "What's new" reads them.
            var sample = ReleaseNotes.Parse("## 📱 Title\n**Fixes**\n- **Bold**: a *b* [link](http://x)\n  wrapped\n> quote\n1. one\n\n**Full Changelog**: x");
            bool ok = sample.Count == 5 && sample[0] is { Kind: NoteKind.Heading } && sample[1].Runs[0].Text == "Fixes" &&
                      sample[2] is { Kind: NoteKind.Bullet } b && b.Runs[0] is { Text: "Bold", Bold: true } &&
                      string.Concat(b.Runs.Select(r => r.Text)) == "Bold: a b link wrapped" && b.Runs[2].Italic &&
                      sample[3].Kind == NoteKind.Quote && sample[4].Runs[0].Text == "1. one";
            Console.WriteLine("parser: " + (ok ? "ok" : "SBAGLIATO"));
            foreach (var n in (await ReleaseNotes.FetchAsync(args.Length > 1 ? args[1] : "KirboHQ/UltimateMP3Player")).Take(3))
            {
                Console.WriteLine($"===== {n.VersionText} | {n.Title}");
                foreach (var block in n.Blocks)
                    Console.WriteLine($"  {block.Kind,-8} " + string.Concat(block.Runs.Select(r => r.Bold ? $"[{r.Text}]" : r.Italic ? $"/{r.Text}/" : r.Text)));
            }
            return ok ? 0 : 1;
        }

    case "songlist":
        {
            // mp3cli songlist [file]: the parser on the exports' shapes, then (a file given) its songs read as the app reads them.
            var spotify = ListImport.Parse("https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC\r\nhttps://open.spotify.com/track/7ouMYWpwJ422jRcDASZB7P?si=x\r\n");
            var apple = ListImport.Parse("Nome\tArtista\tCompositore\tAlbum\tDurata\nCENERE\tLazza\t\tSirio\t208\n\"Heroes\"\tDavid Bowie\t\t\"Heroes\"\t371\n");
            var csv = ListImport.Parse("Track URI,Track Name,Artist URI(s),Artist Name(s),Album Name,Duration (ms)\n" +
                                       "spotify:track:4uLU6hMCjMI75M1A2tKUQC,\"Never Gonna Give You Up\",x,Rick Astley,\"Whenever You Need Somebody\",213573\n");
            var m3u = ListImport.Parse("#EXTM3U\n#EXTINF:208,Lazza - CENERE\nC:\\Music\\cenere.mp3\n/sdcard/Music/Bowie - Heroes.mp3\n");
            var plain = ListImport.Parse("1. Lazza - CENERE (3:28)\nDavid Bowie – Heroes\n\n# comment\nsolo un titolo\n");
            var xml = ListImport.Parse("<?xml version=\"1.0\"?><!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">" +
                "<plist version=\"1.0\"><dict><key>Tracks</key><dict><key>7</key><dict><key>Track ID</key><integer>7</integer><key>Name</key><string>CENERE</string>" +
                "<key>Artist</key><string>Lazza</string><key>Total Time</key><integer>208000</integer></dict><key>9</key><dict><key>Name</key><string>Heroes</string></dict></dict>" +
                "<key>Playlists</key><array><dict><key>Name</key><string>x</string><key>Playlist Items</key><array><dict><key>Track ID</key><integer>9</integer></dict>" +
                "<dict><key>Track ID</key><integer>7</integer></dict></array></dict></array></dict></plist>");
            bool ok = spotify.Count == 2 && spotify[1].Url!.EndsWith("?si=x") &&
                      apple is [{ Title: "CENERE", Artist: "Lazza", Album: "Sirio", Duration: 208 }, { Title: "\"Heroes\"" }] &&
                      csv is [{ Title: "Never Gonna Give You Up", Artist: "Rick Astley", SpotifyId: "4uLU6hMCjMI75M1A2tKUQC", Url: null }] && Math.Abs(csv[0].Duration!.Value - 213.573) < 0.01 &&
                      m3u is [{ Title: "CENERE", Artist: "Lazza", Duration: 208 }, { Title: "Heroes", Artist: "Bowie" }] &&
                      plain is [{ Title: "CENERE", Artist: "Lazza", Duration: 208 }, { Title: "Heroes", Artist: "David Bowie" }, { Title: "solo un titolo", Artist: null }] &&
                      xml is [{ Title: "Heroes" }, { Title: "CENERE", Duration: 208 }] &&
                      ListImport.IsList("a - b\nc - d") && !ListImport.IsList("https://youtu.be/x") && !ListImport.IsList("ciao\ncome stai") &&
                      ListImport.IsList("https://youtu.be/a\nhttps://youtu.be/b");
            Console.WriteLine("parser: " + (ok ? "ok" : "SBAGLIATO"));
            if (!ok)
                foreach (var e in spotify.Concat(apple).Concat(csv).Concat(m3u).Concat(plain).Concat(xml)) Console.WriteLine("  " + e);
            if (args.Length > 1)
            {
                var entries = ListImport.Parse(await File.ReadAllTextAsync(args[1]));
                Console.WriteLine($"{entries.Count} lines");
                var read = await ListImport.AnalyzeAsync(entries, Path.GetFileNameWithoutExtension(args[1]), Opt("--cookies"),
                    (d, n) => { if (d % 25 == 0 || d == n) Console.WriteLine($"  [{sw.Elapsed.TotalSeconds:0.0}s] {d}/{n}"); }, CancellationToken.None);
                foreach (var it in read.Items) Console.WriteLine($"  {it.SiteName,-13} {it.Artist} - {it.Title} ({Text.Duration(it.Duration)}) {it.Url}");
                foreach (var note in read.Notes) Console.WriteLine("  NOTE " + note);
                Console.WriteLine($"{read.Items.Count} songs in {sw.Elapsed.TotalSeconds:0.0}s");
            }
            return ok ? 0 : 1;
        }

    case "shuffle":
        {
            int n = int.Parse(args[1]), rounds = int.Parse(args[2]);
            var q = new PlayQueue { Repeat = RepeatMode.All };
            var ids = Enumerable.Range(1, n).Select(i => i.ToString()).ToList();
            q.PlayShuffled(ids, "x", "x");
            var seq = new List<string> { q.Current! };
            for (int i = 1; i < n * rounds; i++) seq.Add(q.Next(true)!);
            Console.WriteLine(string.Join(" ", seq));
            int minGap = int.MaxValue;
            var last = new Dictionary<string, int>();
            for (int i = 0; i < seq.Count; i++)
            {
                if (last.TryGetValue(seq[i], out var p)) minGap = Math.Min(minGap, i - p);
                last[seq[i]] = i;
            }
            Console.WriteLine($"distanza minima tra due ripetizioni: {minGap}");
            return 0;
        }
}

if (args[0] != "download" || args.Length < 2) return 1;
var cookies = Opt("--cookies");
AnalysisResult r;
try
{
    r = await Analyzer.AnalyzeAsync(new AnalyzeRequest(args[1], cookies), CancellationToken.None);
}
catch (EngineException ex)
{
    Console.WriteLine($"ERRORE: {ex.Message} (login={ex.NeedsLogin})");
    return 2;
}
Console.WriteLine($"[{sw.Elapsed.TotalSeconds:0.0}s] {r.Site} | {r.Title} | {r.Uploader} | collection={r.IsCollection} items={r.Items.Count}");
var picks = Opt("--items")?.Split(',').Select(int.Parse).ToList() ?? new List<int> { 1 };
var musicDir = Opt("--out") ?? Path.Combine(Path.GetTempPath(), "ump-test-music");
foreach (var n in picks)
{
    var item = r.Items[n - 1];
    Console.WriteLine($"  #{n} {item.Title} | {item.Artist} | {item.Kind}/{item.Source} | keys: {string.Join(", ", SourceKeys.ForItem(item))}");
    sw.Restart();
    string last = "";
    try
    {
        var res = await TrackDownloader.RunAsync(lib,
            new TrackRequest(item, Flag("--video"), 720, Flag("--original") ? "original" : "mp3", musicDir, cookies),
            p =>
            {
                var line = $"{p.Phase} {p.Text}";
                if (line[..Math.Min(20, line.Length)] != last[..Math.Min(20, last.Length)]) Console.WriteLine("     " + line);
                last = line;
            }, CancellationToken.None);
        var t = res.Track;
        Console.WriteLine($"  {res.Outcome} [{sw.Elapsed.TotalSeconds:0.0}s] {t.Artist} - {t.Title} | {t.Album} {t.Year} | {Text.Duration(t.Duration)} | LUFS {t.Loudness:0.0} | cover={t.HasCover} | {res.Note}");
        Console.WriteLine($"     {t.Path}{(t.VideoPath != null ? "\n     " + t.VideoPath : "")}");
        Console.WriteLine($"     keys: {string.Join(", ", t.Keys)}");
        if (t.Wave != null) Console.WriteLine("     " + new string(t.Wave.Where((_, i) => i % 5 == 0).Select(b => " ▁▂▃▄▅▆▇█"[b * 8 / 256]).ToArray()));
    }
    catch (EngineException ex)
    {
        Console.WriteLine($"  ERRORE: {ex.Message}");
    }
}
lib.SaveNow();
return 0;
