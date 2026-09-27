// Developer harness for the Core, without the UI (set UMP_DATA to use a throw-away library).
//   mp3cli download <url> [--items 1,3] [--video] [--original] [--out dir] [--cookies firefox]
//   mp3cli import <file-or-folder>
//   mp3cli list
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
            return 0;
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
