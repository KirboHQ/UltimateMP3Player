// Developer harness for the Core, without the UI (set UMP_DATA to use a throw-away library).
//   mp3cli download <url> [--items 1,3] [--video] [--original] [--out dir] [--cookies firefox]
//   mp3cli import <file-or-folder>
//   mp3cli list
//   mp3cli songlist [file]
//   mp3cli shuffle <n> <rounds>
//   mp3cli saver
//   mp3cli albums [artist]
//   mp3cli artist <name> [song titles…]
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

    case "artist":
        {
            // Online: who a name is, proved by some of its songs' titles (mp3cli artist Envy "HOLD ONTO ME" "SELF AWARE"): every
            // artist it may be, the likeliest first, and the first one's popular songs.
            var name = args[1];
            var songs = args.Skip(2).Select(t => new Track { Id = Ids.New(), Title = t, Artist = name }).ToList();
            var found = await ArtistSites.CandidatesAsync(name, songs, CancellationToken.None);
            foreach (var s in found) Console.WriteLine($"{s.Service} | {s.Id} | {s.Name} | fans {s.Fans} | songs {s.Songs} | {s.Url} | {s.Picture}");
            if (found.FirstOrDefault() is { } best)
            {
                foreach (var h in await ArtistSites.TopAsync(best, 5, 0, CancellationToken.None)) Console.WriteLine($"  top: {h.Title} · {h.Artist}");
                foreach (var rel in await ArtistSites.ReleasesAsync(best, best.Name, 5, CancellationToken.None)) Console.WriteLine($"  release: {rel.Date:d} {rel.Kind} {rel.Title}");
                foreach (var a in (await ArtistSites.AlbumsAsync(best, CancellationToken.None)).Take(5)) Console.WriteLine($"  album: {a.Title} ({a.Year})");
            }
            return found.Count > 0 ? 0 : 1;
        }

    case "albums":
        {
            // Offline: an album on two sites kept once, the first site's (also spaced otherwise, or with "EP" in its title), "÷"
            // and "×" both kept, the EPs after the albums, each kind the newest first. With an artist's name, then, every site's
            // albums of them (online, in the search's order).
            SearchHit A(string site, string title, string year, string kind = "album") => new() { Service = site, Title = title, Year = year, Collection = kind };
            var merged = OnlineSearchServices.MergeAlbums(new[]
            {
                new List<SearchHit> { A("YouTube Music", "Parachutes", "2000"), A("YouTube Music", "÷", "2017"), A("YouTube Music", "LeftRightLeftRightLeft (Live)", "2009") },
                new List<SearchHit> { A("Apple Music", "Kaleidoscope", "2017", "ep") },
                new List<SearchHit> { A("Deezer", "parachutes", "2000"), A("Deezer", "×", "2014"), A("Deezer", "Kaleidoscope EP", "2017", "ep"), A("Deezer", "Moon Music", "2024"), A("Deezer", "Left Right Left Right Left (Live)", "2009") },
            });
            var order = string.Join(" | ", merged.Select(h => $"{h.Title}/{h.Service}"));
            bool ok = order == "Moon Music/Deezer | ÷/YouTube Music | ×/Deezer | LeftRightLeftRightLeft (Live)/YouTube Music | Parachutes/YouTube Music | Kaleidoscope/Apple Music";
            Console.WriteLine($"albums merge: {(ok ? "ok" : "SBAGLIATO")} ({order})");
            if (args.Length < 2) return ok ? 0 : 1;
            var name = string.Join(' ', args.Skip(1));
            var found = new List<List<SearchHit>>();
            foreach (var site in new[] { OnlineSearchServices.YouTubeMusic, OnlineSearchServices.Deezer, OnlineSearchServices.Spotify, OnlineSearchServices.AppleMusic, OnlineSearchServices.SoundCloud })
            {
                try
                {
                    var siteAlbums = await OnlineSearchServices.ArtistAlbumsAsync(site, name, CancellationToken.None);
                    Console.WriteLine($"[{sw.Elapsed.TotalSeconds:0.0}s] {site}: " + (siteAlbums is { } sa
                        ? $"{sa.Name}, {sa.Hits.Count}: " + string.Join(" | ", sa.Hits.Take(8).Select(h => $"{h.Title} ({h.Collection} {h.Year} {h.Songs})")) : "-"));
                    if (siteAlbums is { } kept) found.Add(kept.Hits);
                }
                catch (Exception ex) { Console.WriteLine($"{site}: {ex.GetType().Name} {ex.Message}"); }
            }
            var all = OnlineSearchServices.MergeAlbums(found);
            Console.WriteLine($"together {all.Count}: " + string.Join(" | ", all.Select(h => $"{h.Title} {h.Year} [{h.Service}]")));
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

    case "saver":
        {
            // DebouncedSaver: nothing asked, nothing written; asked, one write; a failed one is tried again at the next flush.
            // Then errors.log past 1 MB keeps its newer half, from the start of an entry.
            int writes = 0;
            bool full = true;
            var saver = new DebouncedSaver(() => { writes++; if (full) throw new IOException("disk full"); }, 60_000);
            saver.Flush();
            bool ok = writes == 0;
            saver.Request();
            saver.Flush();
            full = false;
            saver.Flush();
            saver.Flush();
            ok &= writes == 2;
            var log = Path.Combine(Path.GetTempPath(), "ump-saver-test.log");
            File.WriteAllText(log, new string('x', 1 << 20) + "\n\n[old]\n\n");
            AppPaths.AppendLog("[new]\n\n", log);
            ok &= File.ReadAllText(log) == "[old]\n\n[new]\n\n";
            File.Delete(log);
            Console.WriteLine("saver: " + (ok ? "ok" : "SBAGLIATO"));
            return ok ? 0 : 1;
        }

    case "spotifyhistory":
        {
            // mp3cli spotifyhistory [files]: both shapes of Spotify's history (podcasts and too-short plays aside), the list made for
            // the songs to add; then (files given) what they hold.
            var extended = SpotifyHistory.Parse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                "[{\"ts\":\"2024-03-05T18:21:33Z\",\"ms_played\":200000,\"master_metadata_track_name\":\"CENERE\",\"master_metadata_album_artist_name\":\"Lazza\",\"spotify_track_uri\":\"spotify:track:4uLU6hMCjMI75M1A2tKUQC\"}," +
                "{\"ts\":\"2024-03-05T18:30:00Z\",\"ms_played\":9000,\"master_metadata_track_name\":null,\"episode_name\":\"A podcast\"}]")));
            var year = SpotifyHistory.Parse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                "[{\"endTime\":\"2024-03-06 10:00\",\"artistName\":\"David Bowie\",\"trackName\":\"Heroes\",\"msPlayed\":12000}]")));
            var list = ListImport.Parse(SpotifyHistory.AsList(new[] { ("CENERE", "Lazza", (string?)"https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC", 3) }));
            bool ok = extended is [{ Title: "CENERE", Artist: "Lazza", SpotifyId: "4uLU6hMCjMI75M1A2tKUQC", Seconds: 200 }]
                      && extended[0].At == new DateTime(2024, 3, 5, 18, 21, 33, DateTimeKind.Utc).ToLocalTime().AddSeconds(-200)
                      && year is [{ Title: "Heroes", Artist: "David Bowie", SpotifyId: null, Seconds: 12 }]
                      && list is [{ Title: "CENERE", Artist: "Lazza", SpotifyId: "4uLU6hMCjMI75M1A2tKUQC" }]
                      && SpotifyHistory.IsHistory("Spotify Extended Streaming History/Streaming_History_Audio_2019-2021_0.json")
                      && !SpotifyHistory.IsHistory("Streaming_History_Video_2020.json") && !SpotifyHistory.IsHistory("StreamingHistory_podcast_0.json");
            // The same plays imported again add nothing; a longer export adds only what's past the earlier counts, on top of the
            // plays heard here.
            var info = new ProfileInfo { Name = "spotify-test" };
            Directory.CreateDirectory(AppPaths.ProfileDir(info.Id));
            var profile = Profile.Get(info);
            profile.AddListening("t1", 100, true, save: false);
            ImportedPlay P(int day, string id, string title, bool inLibrary) => new(new DateTime(2024, 3, day, 12, 0, 0), id, title, "Lazza", null, 200, true, inLibrary);
            var first = new[] { P(1, "t1", "CENERE", true), P(2, "t1", "CENERE", true), P(3, "sp:x", "Noi", false) };
            var longer = first.Append(P(20, "t1", "CENERE", true)).Append(P(21, "t1", "CENERE", true)).ToArray();
            int a = profile.AddImported(SpotifyHistory.Source, first).Count, b = profile.AddImported(SpotifyHistory.Source, first).Count;
            var c = profile.AddImported(SpotifyHistory.Source, longer);
            var march = profile.Log.Month(2024, 3);
            ok &= a == 3 && b == 0 && c.Count == 2 && c.All(x => x.At.Day >= 20) && profile.StatsSnapshot()["t1"].Plays == 5
                  && march!.Songs["t1"].Plays == 4 && march.Songs["sp:x"].Plays == 1;
            Profile.Delete(info.Id);
            Console.WriteLine("spotifyhistory: " + (ok ? "ok" : "SBAGLIATO"));
            if (args.Length > 1)
            {
                var plays = SpotifyHistory.Read(args.Skip(1));
                Console.WriteLine($"{plays.Count} plays, {plays.Count(p => p.Seconds >= SpotifyHistory.PlaySeconds)} counted, " +
                                  $"{plays.Select(p => p.SpotifyId ?? p.Artist + p.Title).Distinct().Count()} songs, {plays.Sum(p => p.Seconds) / 3600:0} h" +
                                  (plays.Count > 0 ? $", {plays[0].At:d} – {plays[^1].At:d}" : ""));
                foreach (var g in plays.GroupBy(p => p.Artist + " - " + p.Title).OrderByDescending(g => g.Count()).Take(10))
                    Console.WriteLine($"  {g.Count(),5}  {g.Key}");
            }
            return ok ? 0 : 1;
        }

    case "recap":
        {
            // mp3cli recap: a year and a month made from a log kept in memory (the names saved with the plays, a duet counted
            // for both artists, the year before compared).
            ListeningLog.Names = id => id switch { "a" => ("CENERE", "Lazza"), "b" => ("Noi", "Lazza"), _ => (null, null) };
            var log = new ListeningLog(null);
            log.Add("a", 200, true, new DateTime(2025, 3, 2, 22, 10, 0));
            log.Add("a", 200, true, new DateTime(2025, 3, 9, 22, 40, 0));
            log.Add("b", 180, true, new DateTime(2025, 7, 1, 8, 0, 0));
            log.Import(new[] { (new DateTime(2025, 3, 9, 23, 0, 0), "sp:x", (string?)"Heroes", (string?)"David Bowie, Brian Eno", (string?)null, 300.0, true) });
            log.Add("a", 100, true, new DateTime(2024, 5, 1, 12, 0, 0));
            var year = Recap.Make(log, 2025, 0);
            var march = Recap.Make(log, 2025, 3);
            bool ok = year is { Plays: 4, SongCount: 3, ArtistCount: 3, PreviousSeconds: 100, TopHour: 22, BusiestPart: 3, Parts.Length: 12 }
                      && year.TopSongs[0].Id == "a" && year.TopArtists[0] is { Name: "Lazza", Plays: 3 } && year.TopArtists.Count(a => a.Plays == 1) == 2
                      && march is { Plays: 3, Parts.Length: 31, BusiestPart: 9, PreviousSeconds: 0 }
                      && Recap.Make(log, 2025, 5) == null && Recap.Periods(log).Take(4).SequenceEqual(new[] { (2025, 0), (2025, 7), (2025, 3), (2024, 0) });
            Console.WriteLine("recap: " + (ok ? "ok" : "SBAGLIATO"));
            return ok ? 0 : 1;
        }

    case "seal":
        {
            // mp3cli seal: the statistics' files read back as written, a changed byte refused (the version before is read), the
            // counts never plain in memory; a profile saved by 4.2 takes its plain counts once, into stats.dat.
            var data = System.Text.Encoding.UTF8.GetBytes("{\"a\":1}");
            var box = Sealed.Protect(data);
            var bad = (byte[])box.Clone();
            bad[^40] ^= 1;
            var t = new TrackStats { Plays = 7, Seconds = 12.5 };
            var raw = typeof(TrackStats).GetField("_plays", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(t);
            bool ok = Sealed.Unprotect(box)!.SequenceEqual(data) && Sealed.Unprotect(bad) == null && t is { Plays: 7, Seconds: 12.5 } && (int)raw! != 7;
            var info = new ProfileInfo { Name = "seal-test" };
            var dir = AppPaths.ProfileDir(info.Id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "data.json"), "{\"Stats\":{\"x\":{\"Plays\":3,\"Seconds\":90}},\"StatsSince\":\"2024-01-01T00:00:00\"}");
            var profile = Profile.Get(info);
            ok &= profile.StatsOf("x") is { Plays: 3, Seconds: 90 };
            profile.AddListening("x", 10, true);
            profile.Flush();
            profile.AddListening("x", 10, true);
            profile.Flush();
            var file = Path.Combine(dir, "stats.dat");
            ok &= !File.ReadAllText(Path.Combine(dir, "data.json")).Contains("\"Stats\"") && Sealed.Load<SealedStats>(file)?.Stats["x"].Plays == 5;
            var edited = File.ReadAllBytes(file);
            edited[^1] ^= 1;
            File.WriteAllBytes(file, edited);
            ok &= Sealed.Load<SealedStats>(file)?.Stats["x"].Plays == 4 && File.Exists(file + ".unreadable");
            Profile.Delete(info.Id);
            Console.WriteLine("seal: " + (ok ? "ok" : "SBAGLIATO"));
            return ok ? 0 : 1;
        }

    case "radio":
        {
            // mp3cli radio [name]: radio-browser.info answers (by name, or the most listened to in Italy), the stations' fields read.
            var found = await RadioBrowser.SearchAsync(args.Length > 1 ? args[1] : null, args.Length > 1 ? null : "IT", null, false, CancellationToken.None);
            foreach (var s in found.Take(5)) Console.WriteLine($"  {s.Name} | {s.Subtitle} | {s.Quality} | {s.Url}");
            bool ok = found.Count > 0 && found.All(s => s.Url.StartsWith("http") && s.Id.Length > 0);
            Console.WriteLine("radio: " + (ok ? "ok" : "SBAGLIATO"));
            return ok ? 0 : 1;
        }

    case "scrobble":
        {
            // mp3cli scrobble: Last.fm's signature (the parameters sorted by name, then the secret) and a play's fields.
            var sig = LastFm.Sign(new Dictionary<string, string> { ["token"] = "t", ["method"] = "auth.getSession", ["api_key"] = "k" }, "s");
            var expected = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("api_keykmethodauth.getSessiontokents"))).ToLowerInvariant();
            var play = Scrobble.Of(new Track { Title = " CENERE ", Artist = "Lazza ", Album = "", Duration = 208.4 }, 1700000000);
            bool ok = sig == expected && play is { Title: "CENERE", Artist: "Lazza", Album: null, Duration: 208, At: 1700000000 };
            Console.WriteLine("scrobble: " + (ok ? "ok" : "SBAGLIATO"));
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
                    (d, n, wait) =>
                    {
                        if (d % 25 == 0 || d == n) Console.WriteLine($"  [{sw.Elapsed.TotalSeconds:0.0}s] {d}/{n}");
                        if (wait is { } w) Console.WriteLine($"  [{sw.Elapsed.TotalSeconds:0.0}s] {d}/{n} too many requests: waiting {(w - DateTime.UtcNow).TotalSeconds:0} s");
                    }, CancellationToken.None);
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
