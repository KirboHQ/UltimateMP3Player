// Developer harness for the Core, without the UI (set UMP_DATA to use a throw-away library).
//   mp3cli download <url> [--analyze] [--items 1,3] [--video] [--original] [--out dir] [--cookies firefox]
//   mp3cli import <file-or-folder>
//   mp3cli list
//   mp3cli songlist [file]
//   mp3cli shuffle <n> <rounds>
//   mp3cli saver
//   mp3cli renewal
//   mp3cli fromcomputer
//   mp3cli backup
//   mp3cli room
//   mp3cli albums [artist]
//   mp3cli artist <name> [song titles…]
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using UltimateMP3Player.Core;
using UltimateMP3Player.Core.Together;

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
            // By name (Spotify's export has no lengths, a video runs longer): any length, other versions not.
            bool byName = lib.FindByName("Heroes Tonight", "Janji", null) == a && lib.FindByName("Heroes Tonight", "Janji", 260) == a &&
                          lib.FindByName("Heroes Tonight (Live)", "Janji", null) == null && lib.FindByName("Heroes Tonight", "Other", null) == null &&
                          lib.FindItem(new MediaItem { Title = "Heroes Tonight", Artist = "Janji", Source = SourceKind.Search }) == a &&
                          lib.FindItem(new MediaItem { Title = "Heroes Tonight", Artist = "Janji", Source = SourceKind.YtDlp, Url = "https://example.com/b" }) == null;
            found &= byName;
            // "Remove duplicates": the copy added later goes, the first stays; a live version isn't one.
            var b = new Track { Title = "Heroes Tonight", Artist = "Janji", Duration = 240, SourceUrl = "https://example.com/b" };
            var live = new Track { Title = "Heroes Tonight (Live)", Artist = "Janji", Duration = 300, SourceUrl = "https://example.com/c" };
            lib.Add(b);
            lib.Add(live);
            var dups = lib.Duplicates();
            bool dup = dups.Count == 1 && dups[0].Keep == a && dups[0].Copy == b;
            lib.Remove(b);
            lib.Remove(live);
            found &= dup;
            a.Title = "Another Name";
            lib.Changed(a);
            bool renamed = lib.FindSimilar("Heroes Tonight", "Janji", 208) == null && lib.FindSimilar("Another Name", "Janji", 208) == a;
            lib.RenameArtist("Janji", "Janji Music");
            bool artist = lib.FindSimilar("Another Name", "Janji Music", 208) == a && lib.FindSimilar("Another Name", "Janji", 208) == null;
            lib.Remove(a);
            bool removed = lib.FindSimilar("Another Name", "Janji Music", 208) == null;
            bool ok = found && renamed && artist && removed;
            Console.WriteLine($"similar: {(ok ? "ok" : "SBAGLIATO")} (found={found} byName={byName} duplicates={dup} renamed={renamed} artist={artist} removed={removed})");
            return ok ? 0 : 1;
        }

    case "missing":
        {
            // (online) Settings › Library › "Find missing covers": a song by title and artist with no length (Spotify's export), a
            // video's "Artist - Title (Official Video)" with no artist, and nonsense, on Deezer.
            var known = await ListImport.FindOnDeezerAsync("Blinding Lights", "The Weeknd", null, CancellationToken.None);
            var video = await ListImport.FindOnDeezerAsync("The Weeknd - Blinding Lights (Official Video)", null, null, CancellationToken.None);
            var none = await ListImport.FindOnDeezerAsync("Qwzx Plorbt Vimmle", "Nobody Atall", null, CancellationToken.None);
            Console.WriteLine($"  known: {known?.Title} by {known?.Artist}, {known?.Duration} s, {known?.Thumb}");
            Console.WriteLine($"  video: {video?.Title} by {video?.Artist}");
            bool ok = known is { Duration: > 0, Thumb: not null } && video != null && none == null;
            Console.WriteLine($"missing: {(ok ? "ok" : "SBAGLIATO")}");
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
            // A song gone from Spotify ("Heaven" by angel): another "Heaven" of the same length isn't it, nor a re-upload under someone
            // else's name, nor a video of that length without the title; a video naming both is.
            bool ok = Pick("Racing Into The Night", 262) == 0 && Pick("Into The Night", 260) == 2 &&
                      MusicMatcher.Score("Racing Into The Night", "YOASOBI", 262, "夜に駆ける", 0, Info("Someone Else", 262)).Total <= 0.3 &&
                      MusicMatcher.Score("Heaven", "angel", 209, "Heaven", 0, Info("Julia Michaels", 209)).Total <= 0.3 &&
                      MusicMatcher.Score("Heaven", "angel", 209, "Heaven", 0, Info("ANGEL!", 394)).Total <= 0.3 &&
                      // (the artist's name in the title, not as its artist)
                      MusicMatcher.Score("Heaven", "angel", 209, "Heaven Must Be Missing An Angel", 0,
                          new YtInfo { Artist = "Tavares", Uploader = "Tavares - Topic", Title = "Heaven Must Be Missing An Angel", Duration = 209 }).Total <= 0.3 &&
                      !MusicMatcher.Fits("Heaven", "angel", "THE DRIVER ERA - Heaven Angel (audio)", "The Driver Era") &&
                      MusicMatcher.Score("Heaven", "angel", 209, "Heaven (feat. angel)", 0, new YtInfo { Artist = "Someone", Title = "Heaven (feat. angel)", Duration = 209 }).Sure &&
                      MusicMatcher.Score("Heaven", "angel", 209, "Heaven", 0, Info("angel", 209)).Sure &&
                      !MusicMatcher.Fits("Heaven", "angel", "Heaven (slowed)", "someone's uploads") &&
                      !MusicMatcher.Fits("Heaven", "angel", "angel - my new song", "angel") &&
                      MusicMatcher.Fits("Heaven", "angel", "angel - Heaven (Official Audio)", "Some Label");
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

    case "renewal":
        {
            // The iPhone's app renewing itself, the part that doesn't need an iPhone (Core/Renewal.cs): until when a profile says, what's due
            // when, what the library's lines mean, and that nothing secret is ever shown.
            bool fine = true;
            void Is(bool ok, string what)
            {
                if (!ok) Console.WriteLine("WRONG " + what);
                fine &= ok;
            }
            var now = new DateTime(2026, 12, 1, 12, 0, 0, DateTimeKind.Utc);
            var profile = new byte[] { 0x30, 0x82, 0xFF }.Concat(System.Text.Encoding.ASCII.GetBytes("<?xml?><plist><dict>\n\t<key>CreationDate</key>\n\t<date>2026-12-01T10:00:00Z</date>\n\t<key>ExpirationDate</key>\n\t<date>2026-12-08T10:00:00Z</date></dict></plist>")).Append((byte)0xFE).ToArray();
            Is(RenewalRules.ProfileExpires(profile) is { Kind: DateTimeKind.Utc } e && e == new DateTime(2026, 12, 8, 10, 0, 0), "a profile's end");
            Is(RenewalRules.ProfileExpires(new byte[] { 1, 2, 3 }) == null, "junk has no end");
            Is(RenewalRules.PlacedExpires("1796724000", now) is { Kind: DateTimeKind.Utc } p && p == new DateTime(2026, 12, 8, 10, 0, 0) && RenewalRules.PlacedExpires(" 1796724000\r\n", now) == p, "what the installer left");
            Is(new[] { "", " ", "0", "-1796724000", "+1796724000", "1796724000.5", "1,796,724,000", "1796724000 1", "soon", "1796724000000", "99999999999999999999", "253402300800" }
                .All(t => RenewalRules.PlacedExpires(t, now) == null), "anything else left there is nothing");
            Is(RenewalRules.Latest(null, now, now.AddDays(1)) == now.AddDays(1) && RenewalRules.Latest(null, null) == null, "the latest date");
            Is(!RenewalRules.Due(now.AddDays(5), now) && RenewalRules.Due(now.AddDays(3), now) && RenewalRules.Due(null, now) && RenewalRules.Due(now.AddDays(-1), now), "due under four days");
            Is(RenewalRules.From(now.AddDays(6)) == now.AddDays(2) && RenewalRules.From(null) == null, "due from");
            Is(!RenewalRules.Urgent(now.AddDays(3), now) && RenewalRules.Urgent(now.AddHours(40), now) && !RenewalRules.Urgent(now.AddHours(-1), now) && !RenewalRules.Urgent(null, now), "urgent in the last two days");
            Is(RenewalRules.CanTry(null, RenewalProblem.None, now) && !RenewalRules.CanTry(now.AddMinutes(-59), RenewalProblem.Other, now) && RenewalRules.CanTry(now.AddMinutes(-61), RenewalProblem.Other, now)
                && !RenewalRules.CanTry(now.AddHours(-2), RenewalProblem.Install, now) && RenewalRules.CanTry(now.AddHours(2), RenewalProblem.Other, now), "one try an hour");
            Is(RenewalRules.Reminders(now.AddDays(7), now).SequenceEqual(new[] { now.AddDays(5), now.AddDays(6) }) && RenewalRules.Reminders(now.AddHours(30), now).SequenceEqual(new[] { now.AddHours(6) })
                && !RenewalRules.Reminders(null, now).Any(), "the fifth and the sixth day");
            // (as the library writes them: helper/src/phone.rs and lib.rs of the installer)
            Is(RenewalRules.Problem("The iPhone can't be reached at 10.7.0.1:49152: no answer") == RenewalProblem.Vpn
                && RenewalRules.Problem("Not paired: plug the iPhone in with the installer once") == RenewalProblem.Cable
                && RenewalRules.Problem("This app was installed with another Apple ID") == RenewalProblem.Cable
                && RenewalRules.Problem("Not signed in") == RenewalProblem.SignIn && RenewalRules.Problem("Your session has expired. Please log in.") == RenewalProblem.SignIn
                && RenewalRules.Problem("error sending request for url (https://developerservices2.apple.com/)") == RenewalProblem.Internet
                && RenewalRules.Problem("whatever") == RenewalProblem.Other, "what the errors mean");
            var shown = RenewalRules.Shown("\n ● Failed\n ├ src\\phone.rs:136\n ╰ someone@example.com eyJhbGciOiJIUzI1NiJ9abcdefgh/+== at 00008110-000A1B2C3D4E5F60\n ╰ src/ffi.rs:38\n");
            Is(shown == "Failed · … … at …" && !shown.Contains('@') && !shown.Contains("eyJ") && !shown.Contains("00008110") && RenewalRules.Shown(new string('a', 10) + string.Concat(Enumerable.Repeat(" b", 400))).Length <= 241, "nothing secret shown");
            Is(RenewalRules.Parse("{\"event\":\"stage\",\"stage\":\"Signing\",\"progress\":0.42}") is { Name: "stage", Stage: "Signing", Progress: 0.42 }
                && RenewalRules.Parse("{\"event\":\"done\",\"renewed\":true,\"expires\":1796724000}") is { Renewed: true, Expires: { } until } && until == new DateTime(2026, 12, 8, 10, 0, 0)
                && RenewalRules.Parse("{\"event\":\"done\",\"renewed\":false,\"why\":\"x\"}") is { Renewed: false, Expires: null } && RenewalRules.Parse("{\"event\":\"done\"}") is { Renewed: null }
                && RenewalRules.Parse("{\"event\":\"ask\",\"what\":\"revoke\",\"name\":null}") is { Name: "ask", What: "revoke" }
                && RenewalRules.Parse("{\"event\":\"error\",\"message\":\"m\"}") is { Message: "m" }
                && RenewalRules.Parse("{\"event\":\"done\",\"expires\":-5,\"progress\":\"x\"}") is { Expires: null }
                && RenewalRules.Parse("not json") == null && RenewalRules.Parse("[1]") == null && RenewalRules.Parse("{\"stage\":1}") == null, "the library's lines");
            double before = 0;
            foreach (var stage in new[] { "Downloading", "Preparing", "Signing", "Installing" })
            {
                Is(Math.Abs(RenewalRules.Overall(stage, 0) - before) < 1e-9 || stage == "Downloading", "the progress goes on from stage to stage");
                before = RenewalRules.Overall(stage, 1);
            }
            Is(before == 1 && RenewalRules.Overall("Renewing", 0.5) == 0.5, "the progress ends whole");
            Console.WriteLine("renewal: " + (fine ? "ok" : "WRONG"));
            return fine ? 0 : 1;
        }

    case "fromcomputer":
        {
            // What the installer brings to the iPhone from the computer's app (Core/FromComputer.cs, set UMP_DATA), joined to what's there.
            bool fine = true;
            void Is(bool ok, string what)
            {
                if (!ok) Console.WriteLine("WRONG " + what);
                fine &= ok;
            }
            var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ump-fromcomputer-" + Ids.New())).FullName;
            string inbox = Path.Combine(root, "FromComputer"), musicTo = Path.Combine(root, "Music");
            byte[] Json(object o) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(o, o.GetType(), JsonStore.Options);
            // The computer's data, as the installer makes it (ComputerLibrary.WriteData) and puts it.
            void Send(string id, List<Track> songs, (ProfileInfo Info, ProfileData Data, SealedStats? Stats, YearStats? Year)[] profiles, AppSettings? settings, string[] take,
                Dictionary<string, string>? more = null, params string[] files)
            {
                foreach (var f in files)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(inbox, f))!);
                    File.WriteAllText(Path.Combine(inbox, f), "sound of " + f);
                }
                Directory.CreateDirectory(inbox);
                using (var zip = new ZipArchive(File.Create(Path.Combine(inbox, "data.zip")), ZipArchiveMode.Create))
                {
                    void Entry(string name, byte[] data)
                    {
                        using var s = zip.CreateEntry(name).Open();
                        s.Write(data);
                    }
                    Entry("backup.json", Json(new BackupInfo { Songs = songs.Count }));
                    Entry("library.json", Json(new LibraryFile { Tracks = songs }));
                    if (settings != null) Entry("settings.json", Json(settings));
                    Entry("profiles.json", Json(new ProfileList { Profiles = profiles.Select(p => p.Info).ToList() }));
                    foreach (var (info, data, stats, year) in profiles)
                    {
                        Entry($"profiles/{info.Id}/data.json", Json(data));
                        if (stats != null) Entry($"profiles/{info.Id}/stats.dat", Sealed.Protect(Json(stats)));
                        if (year != null) Entry($"profiles/{info.Id}/stats/2026.dat", Sealed.Protect(Json(year)));
                    }
                    foreach (var (name, text) in more ?? new()) Entry(name, System.Text.Encoding.UTF8.GetBytes(text));
                }
                File.WriteAllText(Path.Combine(inbox, "ready.json"), $"{{\"version\":1,\"id\":\"{id}\",\"made\":\"2026-11-24T18:00:00Z\",\"computer\":\"PC\",\"app\":\"5.5.3\",\"songs\":{songs.Count},\"playlists\":1,\"files\":0,\"bytes\":52000000,\"take\":[{string.Join(",", take.Select(t => $"\"{t}\""))}]}}");
            }
            string[] all = { "songs", "stats", "look", "settings" };
            bool Playable(string f) => Path.GetExtension(f) is not (".ogg" or ".webm");
            var list = new ProfileList();
            ProfileInfo NewProfile(string name)
            {
                var p = new ProfileInfo { Name = name, Color = "#111111" };
                list.Profiles.Add(p);
                return p;
            }
            // (a song that comes into the library as nobody's is taken by the profile that's open: MainViewModel.DoRefresh)
            bool nobodys = false, coming = false;
            lib.TrackAdded += t => nobodys |= coming && !Profile.AnyHas(list, t.Id);
            ComputerResult Run(ProfileInfo open)
            {
                coming = true;
                try { return FromComputer.RunAsync(inbox, FromComputer.Look(inbox).Ready!, lib, list, open, musicTo, Playable, Playable).GetAwaiter().GetResult(); }
                finally { coming = false; }
            }

            // The computer: one profile, its songs of every kind. (Objects made anew each time: the import fills them in.)
            var pc = new ProfileInfo { Name = "Alex", Color = "#1DB954", HasAvatar = true };
            string[] ids = Enumerable.Range(0, 9).Select(_ => Ids.New()).ToArray();
            string heard = RadioSong.Prefix + Ids.New(), rock = Ids.New(), road = Ids.New();
            List<Track> Songs() => new()
            {
                new() { Id = ids[0], Title = "Link Song", Artist = "Tester", Duration = 100, SourceUrl = "https://example.com/fc0", Site = "YouTube", Keys = { "youtube:fc000000000" }, HasCover = true, Lyrics = LyricsKind.Plain, Added = new DateTime(2025, 1, 2) },
                new() { Id = ids[1], Title = "File Song", Artist = "Tester, Other", Duration = 110, SourceUrl = "https://example.com/fc1", Site = "SoundCloud", Path = @"C:\Music\x.mp3", VideoPath = @"C:\Music\x.mp4", Keys = { "soundcloud:fc1" } },
                new() { Id = ids[2], Title = "Nowhere Song", Artist = "Tester", Duration = 120, Path = @"C:\Music\gone.mp3", Site = "File locale", Keys = { @"file:c:\music\gone.mp3" } },
                new() { Id = ids[3], Title = "Odd Format", Artist = "Tester", Duration = 130, Path = @"C:\Music\odd.ogg", Site = "File locale" },
                new() { Id = ids[4], Title = "Odd Format With Link", Artist = "Tester", Duration = 140, Path = @"C:\Music\odd2.ogg", SourceUrl = "https://example.com/fc4", Site = "YouTube" },
                new() { Id = ids[5], Title = "recording 1", Duration = 12.34, Path = @"C:\Music\recording.wav", Site = "File locale", Keys = { @"file:c:\music\recording.wav" } },
                // (two there are two here, whatever their names)
                new() { Id = ids[6], Title = "Twice", Artist = "Tester", Duration = 60, SourceUrl = "https://example.com/fc6", Site = "YouTube" },
                new() { Id = ids[7], Title = "Twice", Artist = "Tester", Duration = 90, SourceUrl = "https://example.com/fc7", Site = "YouTube" },
                // (no profile's: the first one's)
                new() { Id = ids[8], Title = "Nobody's", Artist = "Tester", Duration = 70, SourceUrl = "https://example.com/fc8", Site = "YouTube" },
            };
            ProfileData Data() => new()
            {
                Songs = ids[..8].ToHashSet(), Theme = "pink", Speed = 1.5, StatsSince = new DateTime(2025, 1, 1), Tags = { new Tag { Id = rock, Name = "Rock" } }, NoVideo = { "x|y" },
                TrackTags = { [ids[0]] = new() { rock }, [ids[2]] = new() { rock } }, Artists = { new FollowedArtist { Name = "Tester" } }, Unsuggested = { "Slop" },
                Playlists = { new Playlist { Id = "favorites", Name = "Favorites", Tracks = { ids[0] } }, new Playlist { Id = road, Name = "Road", HasCover = true, Tags = { rock }, Tracks = { ids[1], ids[2], ids[0] } } },
                History = { new HistoryEntry { TrackId = ids[0], At = new DateTime(2026, 11, 20, 10, 0, 0) }, new HistoryEntry { TrackId = heard, At = new DateTime(2026, 11, 21, 10, 0, 0) }, new HistoryEntry { TrackId = "gone", At = new DateTime(2026, 11, 22) } },
                Radio = { new RadioSong { Id = heard, Title = "Heard", Artist = "Someone", Url = "https://example.com/heard", Keys = { "youtube:heard000000" }, Phone = "x" } },
                Looks = { new LookPreset { Id = "look1", Name = "Other look", Theme = "blue" } },
            };
            SealedStats Stats(int plays) => new() { Stats = { [ids[0]] = new TrackStats { Plays = plays, Seconds = plays * 100, Last = new DateTime(2026, 11, 20) }, [heard] = new TrackStats { Plays = 3, Seconds = 300 }, ["gone"] = new TrackStats { Plays = 9 } } };
            YearStats Year(int plays)
            {
                var m = new MonthStats { Songs = { [ids[0]] = new MonthSong { Plays = plays, Seconds = plays * 100, Title = "Link Song", Artist = "Tester" }, ["gone"] = new MonthSong { Plays = 1, Seconds = 50, Title = "Gone", Artist = "Them" } } };
                m.Hours[10] = m.Days[19] = plays * 100 + 50;
                return new YearStats { Months = { [11] = m } };
            }
            var pcSettings = new AppSettings { MaxParallel = 5, MusicDir = @"C:\Music", DiscordPresence = false, Language = "en", Visualizer = false };
            var more = new Dictionary<string, string>
            {
                [$"covers/{ids[0]}.jpg"] = "cover", [$"covers/{heard}.jpg"] = "cover", [$"lyrics/{ids[0]}.txt"] = "la la", [$"profiles/{pc.Id}/avatar.jpg"] = "face", [$"profiles/{pc.Id}/pl-{road}.jpg"] = "road",
                [$"profiles/{pc.Id}/looks/look1.jpg"] = "picture", ["covers/artists/123.jpg"] = "artist", ["../../evil.txt"] = "evil", ["artists.json"] = "{\"tester fc\":{\"Service\":\"Deezer\",\"Id\":\"123\",\"Name\":\"Tester FC\"}}",
            };
            string[] files = { $"music/{ids[1]}.mp3", $"videos/{ids[1]}.mp4", $"music/{ids[3]}.ogg", $"music/{ids[4]}.ogg", $"music/{ids[5]}.wav", "music/unknown.mp3" };

            // An empty library: all of it, the profile's name and face too.
            var me = NewProfile("Me");
            var phone = Profile.Get(me);
            int before = lib.Count;
            Send("one", Songs(), new[] { (pc, Data(), (SealedStats?)Stats(10), (YearStats?)Year(10)) }, pcSettings, all, more, files);
            var came = Run(me);
            Is(came is { Added: 7, Present: 0, Saved: 2, Linked: 1, BadFormat: 1, NoFile: 1, NewPlaylists: 1, NewTags: 1, Plays: 13 } && came.NewProfiles.Count == 0 && lib.Count == before + 7, "all of it, once: " + string.Join(" | ", came.Report()));
            Is(me is { Name: "Alex", Color: "#1DB954", HasAvatar: true } && File.ReadAllText(me.AvatarPath) == "face" && list.Profiles.Count == 1, "the empty profile takes the name, the colour and the picture");
            Is(lib.Get(ids[0]) is { Path: "", HasCover: true, Lyrics: LyricsKind.Plain, Site: "YouTube" } a && a.Added == new DateTime(2025, 1, 2) && File.Exists(AppPaths.TrackCover(ids[0])) && File.ReadAllText(LyricsStore.PathFor(ids[0], false)) == "la la", "a song with only its link");
            Is(lib.Get(ids[1]) is { HasVideo: true } b && b.Path == Path.Combine(musicTo, "Tester - File Song.mp3") && File.ReadAllText(b.Path) == $"sound of music/{ids[1]}.mp3" && File.Exists(b.VideoPath) && !File.Exists(Path.Combine(inbox, "music", ids[1] + ".mp3")), "a song with its file and its video, moved");
            Is(lib.Get(ids[2]) == null && lib.Get(ids[3]) == null && lib.Get(ids[4]) is { Path: "" } && lib.Get(ids[5]) is { Site: "Pacchetto", Keys.Count: 0, HasSavedFile: true }, "no file, odd formats, a file of one's own");
            Is(phone.Has(ids[6]) && phone.Has(ids[7]) && phone.Has(ids[8]) && !phone.Has(ids[2]) && phone.Data.Songs!.Count == 7, "the profile's songs");
            Is(phone.Favorites.Tracks.SequenceEqual(new[] { ids[0] }) && phone.PlaylistsSnapshot().Single(p => p.Name == "Road") is { HasCover: true, Tags.Count: 1 } mine && mine.Tracks.SequenceEqual(new[] { ids[1], ids[0] })
               && File.ReadAllText(phone.PlaylistCover(mine)) == "road" && phone.TagsOf(ids[0]).Count == 1 && phone.IsNoVideo("x|y") && phone.FollowedArtist("Tester") != null, "playlists, tags, artists");
            Is(phone.StatsOf(ids[0]) is { Plays: 10, Seconds: 1000 } && phone.StatsOf(heard)?.Plays == 3 && phone.StatsOf("gone") == null && phone.Data.StatsSince == new DateTime(2025, 1, 1), "the statistics");
            Is(phone.Data.Radio.Single() is { Phone: null } rs && rs.Id == heard && phone.HistorySnapshot(9).Select(h => h.TrackId).SequenceEqual(new[] { ids[0], heard }), "the songs heard, the recently played");
            Is(phone.Log.Month(2026, 11) is { Plays: 11, Seconds: 1050 } m1 && m1.Hours[10] == 1050 && m1.Days[19] == 1050, "the months");
            Is(ArtistSites.Get("Tester FC")?.Id == "123" && File.Exists(AppPaths.ArtistPicture("123")), "the artists and their pictures");
            Is(!File.Exists(Path.Combine(inbox, "evil.txt")) && !File.Exists(Path.Combine(root, "evil.txt")), "nothing written outside");
            // Its look and settings; the device's own things stay, what was here isn't lost.
            var here = new AppSettings { MusicDir = musicTo, Visualizer = true };
            phone.Data.Unsuggested.Add("Mine");
            FromComputer.Apply(came, phone, here);
            Is(phone.Data.Theme == "pink" && phone.Data.Speed == 1.5 && here is { MaxParallel: 5, Language: "en", DiscordPresence: true, Visualizer: false } && here.MusicDir == musicTo && phone.Data.Unsuggested.Count == 2, "look and settings");
            Is(phone.Data.Looks.Select(l => l.Name).SequenceEqual(new[] { L.T("My look"), "Other look", "Alex" }) && File.Exists(LookPreset.PictureOf(me.Id, phone.Data.Looks[1].Id)), "the look from before among the looks");

            // The same computer again, with two more plays: only those.
            Send("two", Songs(), new[] { (pc, Data(), (SealedStats?)Stats(12), (YearStats?)Year(12)) }, pcSettings, all, more, files);
            came = Run(me);
            Is(came is { Added: 0, Present: 7, Saved: 0, NewPlaylists: 0, NewTags: 0, Plays: 2 } && lib.Count == before + 7 && Directory.GetFiles(musicTo).Length == 3, "again: " + string.Join(" | ", came.Report()));
            Is(phone.StatsOf(ids[0]) is { Plays: 12, Seconds: 1200 } && phone.Log.Month(2026, 11) is { Plays: 13, Seconds: 1250 } && phone.HistorySnapshot(9).Count == 2 && phone.Data.Radio.Count == 1 && phone.PlaylistsSnapshot().Count == 2, "the plays aren't doubled");
            FromComputer.Apply(came, phone, here);
            Is(phone.Data.Looks.Count == 3, "nor the looks");
            // (as they were saved)
            phone.Flush();
            Is(Sealed.Load<SealedStats>(Path.Combine(AppPaths.ProfileDir(me.Id), "stats.dat"))?.Stats[ids[0]].Plays == 12, "saved");

            // Closed halfway: in the library, its file not moved yet; then a file that comes later for a song with only its link.
            var planned = Path.Combine(musicTo, "Tester - File Song.mp3");
            File.Delete(planned);
            Send("three", Songs(), new[] { (pc, Data(), (SealedStats?)null, (YearStats?)null) }, null, new[] { "songs" }, null, $"music/{ids[1]}.mp3", $"music/{ids[0]}.mp3");
            came = Run(me);
            Is(came is { Added: 0, Saved: 2 } && File.Exists(planned) && lib.Get(ids[1])!.Path == planned && lib.Get(ids[0])!.HasSavedFile && Directory.GetFiles(musicTo).Length == 4, "taken up from where it was");
            Is(FromComputer.Look(inbox).Ready is { WithSongs: true, WithStats: false, WithLook: false, WithSettings: false } && came.Looks.Count == 0 && came.Plays == 0, "only the songs");

            // A library with songs of its own: one in common by name, an id of another song's.
            var own = new Track { Title = "Link Song Two", Artist = "Tester", Duration = 101, SourceUrl = "https://example.com/own2", HasCover = false };
            var taken = new Track { Title = "Something Else", Artist = "Nobody", Duration = 33, SourceUrl = "https://example.com/else" };
            lib.Add(own);
            lib.Add(taken);
            phone.Own(new[] { own.Id, taken.Id });
            string theirs = Ids.New();
            List<Track> Common() => new()
            {
                new() { Id = theirs, Title = "Link Song Two (Official Video)", Artist = "Tester", Duration = 103, SourceUrl = "https://example.com/theirs", HasCover = true },
                new() { Id = taken.Id, Title = "Quite Another", Artist = "Tester", Duration = 34, SourceUrl = "https://example.com/another" },
            };
            for (int round = 0; round < 2; round++)
            {
                int count = lib.Count;
                Send("four" + round, Common(), new[] { (pc, new ProfileData { Songs = new() { theirs, taken.Id }, Playlists = { new Playlist { Name = " road", Tracks = { theirs, taken.Id } } } }, (SealedStats?)new SealedStats { Stats = { [theirs] = new TrackStats { Plays = 4, Seconds = 40 } } }, (YearStats?)null) },
                    null, new[] { "songs", "stats" }, new() { [$"covers/{theirs}.jpg"] = "theirs" });
                came = Run(me);
                Is(came.Added == 1 - round && came.Present == 1 + round && lib.Count == count + 1 - round && came.NewPlaylists == 0 && phone.StatsOf(own.Id)?.Plays == 4 && own.HasCover && lib.Get(theirs) == null && taken.Title == "Something Else", "in common, round " + round);
                var added = lib.Snapshot().Single(t => t.Title == "Quite Another");
                Is(added.Id != taken.Id && phone.PlaylistsSnapshot().Single(p => p.Name == "Road").Tracks.TakeLast(2).SequenceEqual(new[] { own.Id, added.Id }), "an id that's taken, round " + round);
            }

            // Several profiles: by name, or a new one; only their looks (no songs come, no settings).
            var second = new ProfileInfo { Name = "Guest" };
            int songsBefore = lib.Count;
            Send("five", Songs(), new[] { (new ProfileInfo { Name = "alex" }, new ProfileData { Songs = new(), Theme = "green" }, (SealedStats?)null, (YearStats?)null), (second, new ProfileData { Songs = new() { ids[0] }, Theme = "blue", Speed = 2 }, null, null) },
                pcSettings, new[] { "look" });
            came = Run(me);
            var app = new AppSettings();
            FromComputer.Apply(came, phone, app);
            var guest = list.Profiles.Single(p => p.Name == "Guest");
            Is(came.NewProfiles.SequenceEqual(new[] { "Guest" }) && list.Profiles.Count == 2 && phone.Data.Theme == "green" && phone.Data.Speed == 1.5 && Profile.Get(guest).Data is { Theme: "blue", Speed: 1 } && Profile.Get(guest).Data.Songs!.Count == 0
               && lib.Count == songsBefore && app.MaxParallel == 3 && Profile.Get(guest).Data.Looks.Count == 1, "several profiles, only the look");

            // Each its own songs.
            var guests = new Track { Title = "Guest's Own", Artist = "Visitor", Duration = 10, SourceUrl = "https://example.com/guest" };
            Send("five and a half", new() { guests }, new[] { (new ProfileInfo { Name = "Alex" }, new ProfileData { Songs = new() }, (SealedStats?)null, (YearStats?)null), (second, new ProfileData { Songs = new() { guests.Id } }, null, null) }, null, new[] { "songs" });
            came = Run(me);
            Is(came.Added == 1 && Profile.Get(guest).Has(guests.Id) && !phone.Has(guests.Id) && !nobodys, "each profile its own songs");

            // A profile brought here before with a backup of that one: its plays aren't counted twice.
            var twin = Profile.Get(second);
            list.Profiles.Remove(guest);
            list.Profiles.Add(second);
            twin.Own(new[] { ids[0] });
            twin.AddListening(ids[0], 100, true);
            Send("six", Songs(), new[] { (second, new ProfileData { Songs = new() { ids[0] } }, (SealedStats?)new SealedStats { Stats = { [ids[0]] = new TrackStats { Plays = 1, Seconds = 100 } } }, (YearStats?)null) }, null, new[] { "songs", "stats" });
            came = Run(second);
            Is(came.Plays == 0 && twin.StatsOf(ids[0])?.Plays == 1, "the same profile, restored here");

            // What can't be read, or isn't all there yet, changes nothing.
            int untouched = lib.Count;
            bool Refused(Action spoil)
            {
                Send("bad", Songs(), new[] { (pc, Data(), (SealedStats?)null, (YearStats?)null) }, pcSettings, all, new() { ["x"] = new string('x', 100_000) });
                spoil();
                try { Run(me); }
                catch (InvalidDataException) { return lib.Count == untouched; }
                return false;
            }
            var zipFile = Path.Combine(inbox, "data.zip");
            Is(Refused(() => File.WriteAllText(zipFile, "not a zip")) && Refused(() => File.Delete(zipFile)) && Refused(() => File.WriteAllBytes(zipFile, File.ReadAllBytes(zipFile)[..^40])), "a damaged zip");
            Is(Refused(() =>
            {
                using var zip = ZipFile.Open(zipFile, ZipArchiveMode.Update);
                zip.GetEntry($"profiles/{pc.Id}/data.json")!.Delete();
                using var s = new StreamWriter(zip.CreateEntry($"profiles/{pc.Id}/data.json").Open());
                s.Write("{\"Playlists\":[");
            }), "a damaged file inside");
            var readyFile = Path.Combine(inbox, "ready.json");
            (bool, ComputerReady?) Says(string text)
            {
                File.WriteAllText(readyFile, text);
                return FromComputer.Look(inbox);
            }
            Is(Says("") == (false, null) && Says("{") == (true, null) && Says("null") == (true, null) && Says("{\"take\":null}") == (true, null) && Says("{\"version\":2,\"id\":\"z\",\"take\":[]}").Item2 is { Newer: true }, "what it says of itself");
            FromComputer.Delete(inbox, "another");
            Is(Directory.Exists(inbox), "another one's there by now");
            FromComputer.Delete(inbox, "z");
            Is(!Directory.Exists(inbox) && FromComputer.Look(inbox) == (false, null), "gone once it's in");
            Profile.FlushAll();
            foreach (var p in list.Profiles.Append(guest)) Profile.Delete(p.Id);
            try { Directory.Delete(root, true); } catch { }
            Console.WriteLine("fromcomputer: " + (fine ? "ok" : "WRONG"));
            return fine ? 0 : 1;
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

    case "overlay":
        {
            // mp3cli overlay [seconds] [cover] [video]: the stream overlays' server with a song and bars made up, every look at /s/<skin>?preview. Its
            // addresses are checked first: what isn't one of them, or doesn't come from this computer's name, gets nothing.
            var server = OverlayServer.Shared;
            var sources = new[] { "compact", "boxy", "gallery", "minimal", "macos", "shell", "discord", "pill", "neon", "cassette", "polaroid", "broadcast" }
                .Select(s => new OverlaySource { Id = s, Name = s, Skin = s, NothingTitle = "Nothing playing", NothingArtist = "Get the music started" }).ToList();
            foreach (var skin in sources.Select(s => s.Skin).ToList())
            {
                sources.Add(new OverlaySource { Id = "canvas-" + skin, Name = skin, Skin = skin, Cover = "canvas" });
                sources.Add(new OverlaySource { Id = "square-" + skin, Name = skin, Skin = skin, Cover = "square" });
                sources.Add(new OverlaySource { Id = "nobars-" + skin, Name = skin, Skin = skin, Bars = false });
                sources.Add(new OverlaySource { Id = "rgb-" + skin, Name = skin, Skin = skin, Rgb = true, RgbSpeed = 10 });
            }
            sources.Add(new OverlaySource { Id = "none", Name = "none", Skin = "compact", Cover = "none" });
            sources.Add(new OverlaySource { Id = "photo", Name = "photo", Skin = "polaroid", Cover = "square" });
            sources.Add(new OverlaySource { Id = "vinyl-compact", Name = "vinyl", Skin = "compact", Cover = "vinyl" });
            sources.Add(new OverlaySource { Id = "light", Name = "light", Skin = "compact", Cover = "square", Light = true });
            sources.Add(new OverlaySource { Id = "tint", Name = "tint", Skin = "boxy", Cover = "canvas", MagicColors = false, Tint = "#1DB954" });
            sources.Add(new OverlaySource { Id = "behind", Name = "behind", Skin = "gallery", Cover = "square", BarsBehind = true, Flow = VisualizerFlow.MirrorSides });
            sources.Add(new OverlaySource { Id = "line", Name = "line", Skin = "compact", Cover = "none", BarsBehind = true, Style = VisualizerStyle.Line, Flow = VisualizerFlow.LeftToRight });
            foreach (VisualizerStyle style in Enum.GetValues<VisualizerStyle>())
                sources.Add(new OverlaySource { Id = "style-" + (int)style, Name = "style", Skin = "gallery", Cover = "none", BarsBehind = true, Style = style, Flow = VisualizerFlow.LeftToRight });
            foreach (VisualizerPlace place in Enum.GetValues<VisualizerPlace>())
                foreach (VisualizerStyle style in new[] { VisualizerStyle.Bars, VisualizerStyle.Line, VisualizerStyle.Wave })
                    sources.Add(new OverlaySource { Id = $"place-{(int)place}-{(int)style}", Name = "place", Skin = "compact", Cover = "square", BarsBehind = true, BarsPlace = place, Style = style });
            sources.Add(new OverlaySource { Id = "noarm", Name = "noarm", Skin = "compact", Cover = "vinyl", Arm = false });
            foreach (var inside in new[] { "bars", "line", "both", "none" })
                sources.Add(new OverlaySource { Id = "inside-" + inside, Name = inside, Skin = "compact", Cover = "square", Bars = inside is "bars" or "both", ShowBar = inside is "line" or "both" });
            sources.Add(new OverlaySource { Id = "small", Name = "small", Size = 0.5, AnchorX = 0, AnchorY = 1, Pulse = true });
            sources.Add(new OverlaySource { Id = "round", Name = "round", Skin = "boxy", Cover = "square", Corners = 2 });
            sources.Add(new OverlaySource { Id = "sharp", Name = "sharp", Skin = "boxy", Cover = "square", Corners = 0, Glow = false });
            sources.Add(new OverlaySource { Id = "glass", Name = "glass", Skin = "macos", Cover = "square", Solid = 0.5 });
            sources.Add(new OverlaySource { Id = "bare", Name = "bare", Skin = "compact", Bars = false, ShowArtist = false, ShowTime = false, ShowBar = false });
            sources.Add(new OverlaySource { Id = "change", Name = "change", Show = "change", ShowFor = 2 });
            // Every look with every cover, what's inside the card and the big bars: m-<look>-<cover>-<inside>-<where, or x>. Something switched off: t-<look>-<what>.
            foreach (var look in sources.Select(s => s.Skin).Distinct().ToList())
            {
                foreach (var kind in new[] { "square", "canvas", "vinyl", "none" })
                    foreach (var inside in new[] { "bars", "line", "both", "none" })
                        foreach (var behind in new[] { -1, 0, 1, 2, 3 })
                            sources.Add(new OverlaySource { Id = $"m-{look}-{kind}-{inside}-{(behind < 0 ? "x" : behind.ToString())}", Name = look, Skin = look, Cover = kind, Bars = inside is "bars" or "both", ShowBar = inside is "line" or "both", BarsBehind = behind >= 0, BarsPlace = behind < 0 ? VisualizerPlace.Bottom : (VisualizerPlace)behind });
                // Every cover with the three corners of the editor (OverlaySourceRow.CornersOf): c-<look>-<cover>-<0|1|2>.
                foreach (var kind in new[] { "square", "canvas", "vinyl", "none" })
                    for (int corner = 0; corner < 3; corner++)
                        sources.Add(new OverlaySource { Id = $"c-{look}-{kind}-{corner}", Name = look, Skin = look, Cover = kind, Corners = look == "pill" ? corner / 2.0 : corner });
                sources.Add(new OverlaySource { Id = $"t-{look}-noartist", Name = look, Skin = look, Cover = "square", Bars = false, ShowBar = true, ShowArtist = false });
                sources.Add(new OverlaySource { Id = $"t-{look}-notime", Name = look, Skin = look, Cover = "square", Bars = false, ShowBar = true, ShowTime = false });
                sources.Add(new OverlaySource { Id = $"t-{look}-bare", Name = look, Skin = look, Cover = "square", Bars = false, ShowBar = false, ShowTime = false });
                sources.Add(new OverlaySource { Id = $"t-{look}-light", Name = look, Skin = look, Cover = "square", Bars = false, ShowBar = true, Light = true });
                // The words in the middle or at the end, with a cover beside them and alone; half see-through, over the page's dots.
                foreach (var align in new[] { "center", "right" })
                {
                    sources.Add(new OverlaySource { Id = $"a-{look}-{align}", Name = look, Skin = look, Cover = "square", Bars = false, ShowBar = false, TextAlign = align });
                    sources.Add(new OverlaySource { Id = $"a-{look}-{align}-canvas", Name = look, Skin = look, Cover = "canvas", Bars = false, ShowBar = false, ShowTime = false, TextAlign = align });
                }
                sources.Add(new OverlaySource { Id = $"o-{look}", Name = look, Skin = look, Cover = "square", Solid = 0.3, BarsBehind = true, BarsPlace = VisualizerPlace.Top });
                sources.Add(new OverlaySource { Id = $"s-{look}", Name = look, Skin = look, Cover = "square", BarsBehind = true, BarsPlace = VisualizerPlace.Top, Style = VisualizerStyle.Line, InsideStyle = VisualizerStyle.Bars });
                sources.Add(new OverlaySource { Id = $"o-{look}-canvas", Name = look, Skin = look, Cover = "canvas", Solid = 0.3 });
            }
            int port = server.Apply(sources, 0);
            async Task<int> Status(string path, string? host = null)
            {
                using var http = new HttpClient();
                using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}{path}");
                if (host != null) request.Headers.Host = host;
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                return (int)response.StatusCode;
            }
            var cover = args.Length > 2 ? args[2] : Path.GetTempFileName();
            if (args.Length <= 2) File.WriteAllBytes(cover, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
            var nowPlaying = new OverlayState(Environment.GetEnvironmentVariable("UMP_OVERLAY_TITLE") ?? "A song with a rather long title (feat. Someone)", Environment.GetEnvironmentVariable("UMP_OVERLAY_ARTIST") ?? "An artist", "c1", cover, args.Length > 3 ? "v1" : null, args.Length > 3 ? args[3] : null, 62, 206, 1, Environment.GetEnvironmentVariable("UMP_OVERLAY_PAUSED") == null, false);
            server.Push(nowPlaying);
            bool ok = await Status("/s/compact") == 200 && await Status("/s/nope") == 404 && await Status("/s/compact", "evil.example") == 403
                      && await Status("/../../etc/passwd") == 404 && await Status("/cover/other") == 404 && await Status("/cover/c1") == 200
                      && await Status("/video/c1") == 404 && await Status("/") == 404;
            Console.WriteLine("overlay: " + (ok ? "ok" : "SBAGLIATO") + $" (http://127.0.0.1:{port}/s/compact?preview)");
            int seconds = args.Length > 1 && int.TryParse(args[1], out var n) ? n : 0;
            // UMP_PREVIEW=<browser>: the editor's preview of /s/compact through it, for those seconds; its last picture in the temp folder.
            OverlayPreview? preview = null;
            if (Environment.GetEnvironmentVariable("UMP_PREVIEW") is { } browser)
            {
                OverlayPreview.Forced = browser;
                preview = new OverlayPreview($"http://127.0.0.1:{port}/s/compact?preview", 700, 700, 1.25, (_, _) => { }, () => Console.WriteLine("preview: no picture"));
            }
            long said = 0;
            for (var t = Stopwatch.StartNew(); t.Elapsed.TotalSeconds < seconds;)
            {
                // (as the app does, for the clock)
                if (t.ElapsedMilliseconds / 3000 > said) { said = t.ElapsedMilliseconds / 3000; server.Push(nowPlaying); }
                var block = new float[9600];
                // (something like music: tones over the whole range, each coming and going)
                double now = t.Elapsed.TotalSeconds;
                for (int k = 0; k < 28; k++)
                {
                    double hz = 50 * Math.Pow(2, k / 3.2), level = 0.05 * (1 + Math.Sin(now * (1.3 + k * 0.17) + k * 0.9)) * (1 - k / 40.0);
                    for (int i = 0; i < block.Length; i += 2) block[i] = block[i + 1] += (float)(level * Math.Sin(2 * Math.PI * hz * (i / 2) / 48000));
                }
                Spectrum.Shared.Feed(block, 0, block.Length);
                await Task.Delay(50);
            }
            if (preview != null)
            {
                var lastPicture = Path.Combine(Path.GetTempPath(), "ump-preview.jpg");
                bool moved = OverlayPreview.Frames >= seconds && preview.SaveLast(lastPicture);
                long pictures = OverlayPreview.Frames;
                preview.Dispose();
                for (int i = 0; i < 100 && OverlayPreview.Running > 0; i++) await Task.Delay(100);
                ok &= moved && OverlayPreview.Running == 0;
                Console.WriteLine($"preview: {(moved ? "ok" : "SBAGLIATO")}, {pictures / (double)Math.Max(1, seconds):0.0} pictures a second, browser {(OverlayPreview.Running == 0 ? "gone" : "STILL THERE")}, {lastPicture}");
            }
            server.Stop();
            if (args.Length <= 2) File.Delete(cover);
            return ok ? 0 : 1;
        }

    case "signin":
        {
            // mp3cli signin <browser> [finish] [window]: a sign-in with that browser on a small site of this computer, nothing on the screens. The cookie it
            // sets comes back once the window is closed (by the page, or as the Done button does: "finish"), and its profile goes. "window": a real window,
            // as a person gets (with "finish", best on a desktop of its own).
            bool finish = args.Contains("finish"), realWindow = args.Contains("window");
            var site = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            site.Start();
            string page = finish ? "signed in" : "<script>setTimeout(() => window.close(), 1500)</script>";
            var seen = new TaskCompletionSource();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    using var client = await site.AcceptTcpClientAsync();
                    using var stream = client.GetStream();
                    await stream.ReadAsync(new byte[4096]);
                    await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nSet-Cookie: kept=yes; Max-Age=86400; Path=/\r\nConnection: close\r\nContent-Length: {page.Length}\r\n\r\n{page}"));
                    seen.TrySetResult();
                }
            });
            var signIn = new BrowserSignIn(args[1], $"http://127.0.0.1:{((System.Net.IPEndPoint)site.LocalEndpoint).Port}/", unseen: !realWindow);
            await Task.WhenAny(seen.Task, Task.Delay(30000));
            if (finish)
            {
                await Task.Delay(2500);
                signIn.Finish();
            }
            await Task.WhenAny(signIn.Closed, Task.Delay(25000));
            bool closed = signIn.Closed.IsCompleted;
            using var limit = new CancellationTokenSource(40000);
            var got = await signIn.CookiesAsync(limit.Token);
            bool found = got.Any(c => c.Name == "kept" && c.Value == "yes" && c.Path == "/" && c.Expires > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await signIn.EndAsync();
            site.Stop();
            int left = Directory.GetDirectories(Path.GetTempPath(), "ump-signin-*").Count(d => Directory.GetLastWriteTimeUtc(d) > DateTime.UtcNow.AddMinutes(-2));
            bool fine = seen.Task.IsCompleted && closed && found && left == 0;
            Console.WriteLine($"signin: {(fine ? "ok" : "SBAGLIATO")} (page seen {seen.Task.IsCompleted}, closed {closed}, {got.Count} cookies, ours {found}, {left} profiles left)");
            return fine ? 0 : 1;
        }

    case "rgbspeed":
        {
            // The theme RGB: ten times as fast at 10x, and another speed goes on from the same colour.
            double Moved(double speed, int ms)
            {
                ThemeColors.RgbSpeed = speed;
                ThemeColors.Rgb();
                double from = ThemeColors.RgbHue;
                Thread.Sleep(ms);
                ThemeColors.Rgb();
                return (ThemeColors.RgbHue - from + 360) % 360;
            }
            double slow = Moved(1, 1000), fast = Moved(10, 1000);
            ThemeColors.Rgb();
            double before = ThemeColors.RgbHue;
            ThemeColors.RgbSpeed = 1;
            ThemeColors.Rgb();
            double jump = Math.Abs(ThemeColors.RgbHue - before);
            bool fine = Math.Abs(slow - 8) < 1 && Math.Abs(fast - 80) < 8 && jump < 2 && ThemeColors.RgbStepMs == 250;
            // A colour of your own: only a whole colour counts; any colour comes back from the picker's square and bar as it was (greys,
            // black and white too), and even black or white leaves the accent's text readable on both looks.
            var hsv = ThemeColors.ToHsv("#12AB9F");
            // (the plain look's panes: the glass's are see-through, Contrast reads no alpha)
            bool glassWas = Glass.Look.On;
            Glass.Look.On = false;
            fine &= ThemeColors.ColorFrom("12ab9f") == "#12AB9F" && ThemeColors.ColorFrom("#12AB9") == null && ThemeColors.ColorFrom("#12AB9G") == null
                && Math.Abs(ThemeColors.ToHsv(ThemeColors.FromHue(200)).H - 200) < 2 && ThemeColors.Custom("#12AB9F").A == "#12AB9F"
                && new[] { "#12AB9F", "#000000", "#FFFFFF", "#808080", "#FF0000", "#FF00FF" }.All(x => { var (h, s, v) = ThemeColors.ToHsv(x); return ThemeColors.FromHsv(h, s, v) == x; })
                && ThemeColors.FromHsv(hsv.H, hsv.S, hsv.V) == "#12AB9F" && ThemeColors.FromHsv(360, 1, 1) == "#FF0000"
                && new[] { "#000000", "#FFFFFF" }.All(x => new[] { false, true }.All(light =>
                {
                    var built = ThemeColors.Build(ThemeColors.Custom(x), light);
                    return ThemeColors.Contrast(built["AccentText"], built["Surface"]) >= (light ? 4.5 : 3);
                }));
            Glass.Look.On = glassWas;
            // What's written on the buttons: one colour all the way round, readable on every colour.
            foreach (var light in new[] { false, true })
            {
                var inks = new HashSet<uint>();
                double worst = 99;
                for (int h = 0; h < 360; h++)
                {
                    var c = ThemeColors.Build(ThemeColors.RgbAt(h), light);
                    inks.Add(c["OnAccent"]);
                    worst = Math.Min(worst, Math.Min(ThemeColors.Contrast(c["OnAccent"], c["Accent"]), ThemeColors.Contrast(c["OnAccent"], c["Accent2"])));
                }
                Console.WriteLine($"{(light ? "light" : "dark")} look: {inks.Count} ink, worst contrast {worst:0.0}");
                fine &= inks.Count == 1 && worst >= 4;
            }
            ThemeColors.RgbSpeed = 10;
            fine &= Math.Abs(ThemeColors.RgbStepMs - 1000.0 / 30) < 0.01;
            Console.WriteLine($"1x {slow:0.0} degrees a second, 10x {fast:0.0}, on changing speed {jump:0.00}: " + (fine ? "rgbspeed: ok" : "rgbspeed: WRONG"));
            return fine ? 0 : 1;
        }

    case "seekshapes":
        {
            // The song's bar: every shape, mirrored or not, with a song or without, stays inside its box; "Bars" is the bar it always was, a bar
            // every 4 points; mirrored is the same above and below the middle.
            var loud = Enumerable.Range(0, 300).Select(i => (byte)(i * 37 % 256)).ToArray();
            bool fine = true;
            foreach (var shape in Enum.GetValues<VisualizerStyle>())
                foreach (var peaks in new[] { loud, null, Array.Empty<byte>() })
                    foreach (var (w, h) in new[] { (900.0, 30.0), (340.0, 46.0), (4000.0, 30.0), (5.0, 8.0) })
                        foreach (var mirrored in new[] { true, false })
                        {
                            var marks = SeekShapes.Build(shape, peaks, w, h, 2.5, mirrored);
                            bool even = !mirrored || Math.Abs(marks.Sum(m => m.Y + m.H / 2 - h / 2)) < marks.Length * 0.001 + 0.01;
                            if (even && marks.Length > 0 && marks.Length < 20000 && marks.All(m => m.W > 0 && m.H > 0 && m.X >= 0 && m.X + m.W <= w + 0.5 && m.Y >= -0.01 && m.Y + m.H <= h + 0.01)) continue;
                            fine = false;
                            Console.WriteLine($"WRONG: {shape}, mirrored {mirrored}, {peaks?.Length} peaks, {w}x{h}: {marks.Length} marks");
                        }
            int bars = SeekShapes.Build(VisualizerStyle.Bars, loud, 900, 30, 2.5).Length;
            if (bars != 225) { fine = false; Console.WriteLine($"WRONG: {bars} bars in 900 points"); }
            Console.WriteLine(fine ? "seekshapes: ok" : "seekshapes: WRONG");
            return fine ? 0 : 1;
        }

    case "spectrum":
        {
            // Spectrum: a sine lands in the band of its frequency, silence stays at rest, and the flows put the bass where they say.
            var sp = new Spectrum();
            sp.Acquire();
            int Loudest(double hz)
            {
                var block = new float[9600];
                for (int i = 0; i < block.Length; i += 2) block[i] = block[i + 1] = (float)(0.5 * Math.Sin(2 * Math.PI * hz * (i / 2) / 48000));
                // (the sound before it has had time to fall)
                float[] b = Array.Empty<float>();
                for (int round = 0; round < 12; round++)
                {
                    sp.Feed(block, 0, block.Length);
                    Thread.Sleep(35);
                    b = sp.Read();
                }
                return Array.IndexOf(b, b.Max());
            }
            int low = Loudest(100), mid = Loudest(1000), high = Loudest(8000);
            bool ok = low < mid && mid < high && low < 20 && high > 70;
            var quiet = new float[9600];
            sp.Feed(quiet, 0, quiet.Length);
            float[] bands = Array.Empty<float>();
            for (int i = 0; i < 40; i++)
            {
                sp.Feed(quiet, 0, quiet.Length);
                Thread.Sleep(35);
                bands = sp.Read();
            }
            ok &= bands.Max() < 0.05f;
            ok &= Spectrum.PlaceOf(0, VisualizerFlow.LeftToRight) == 0 && Spectrum.PlaceOf(1, VisualizerFlow.RightToLeft) == 0
                  && Spectrum.PlaceOf(0.5, VisualizerFlow.MirrorCenter) == 0 && Spectrum.PlaceOf(0, VisualizerFlow.MirrorSides) == 0;
            sp.Release();
            ok &= !sp.Wanted;
            ok &= new AppSettings().VisualizerFlow == VisualizerFlow.MirrorSides && new OverlaySource().Flow == VisualizerFlow.MirrorSides;
            // Thin, normal and wide look different at any size, a 2560 px window (columns at their cap) too.
            foreach (double width in new[] { 400.0, 1280, 2560 })
            {
                double Bar(int thickness) => SpectrumColumns.BarOf(width / SpectrumColumns.CountFor(width, thickness, VisualizerStyle.Bars), thickness, VisualizerStyle.Bars);
                ok &= Bar(1) > Bar(0) * 1.5 && Bar(2) > Bar(1) * 1.5;
            }
            // The two lanes of "in the center" start from the same line and go opposite ways, sharing the height; solid where they start.
            var (up, wayUp) = SpectrumColumns.Lane(VisualizerPlace.Center, 0, 800, 300);
            var (down, wayDown) = SpectrumColumns.Lane(VisualizerPlace.Center, 1, 800, 300);
            ok &= up == 300 && down == 300 && wayUp == -1 && wayDown == 1 && SpectrumColumns.ReachOf(VisualizerPlace.Center, 800, 1) == 400;
            ok &= SpectrumColumns.Lane(VisualizerPlace.Top, 0, 800, 300) == (0, 1) && SpectrumColumns.Lane(VisualizerPlace.Bottom, 0, 800, 300) == (800, -1);
            var solid = SpectrumColumns.FadeOf(VisualizerPlace.Center, 800, 400, 300);
            ok &= solid.All(s => s.At is >= 0 and <= 1) && solid.MaxBy(s => s.Alpha).At == 300.0 / 800;
            // They fade in and out by the clock, and only when they've gone are the frames over.
            var columns = new SpectrumColumns();
            float first = -1;
            for (int i = 0; i < 6; i++)
            {
                columns.Update(16, VisualizerFlow.MirrorSides, true, true);
                if (i == 1) first = columns.Presence;
                Thread.Sleep(30);
            }
            bool fadedIn = first is > 0 and < 0.5f && columns.Presence > first;
            for (int i = 0; i < 40; i++)
            {
                columns.Update(16, VisualizerFlow.MirrorSides, true, true);
                Thread.Sleep(15);
            }
            fadedIn &= columns.Presence == 1;
            int frames = 0;
            while (columns.Update(16, VisualizerFlow.MirrorSides, false, false) && frames < 200)
            {
                frames++;
                Thread.Sleep(15);
            }
            ok &= fadedIn && frames is > 5 and < 100 && columns.Presence == 0;
            Console.WriteLine($"spectrum: {(ok ? "ok" : "SBAGLIATO")} (bands 100 Hz {low}, 1 kHz {mid}, 8 kHz {high}, silence {bands.Max():0.000}, fade {frames} frames)");
            return ok ? 0 : 1;
        }

    case "glide":
        {
            // Glide (read every ~16 ms, like frames): on its way halfway, sent back: no jump at the turn, it keeps going a little
            // before it turns round, it arrives at rest on time; the same target again doesn't start it over; a long wait
            // between two frames counts as one frame.
            var g = new Glide();
            void Frames(int ms)
            {
                for (var t = Stopwatch.StartNew(); t.ElapsedMilliseconds < ms;)
                {
                    Thread.Sleep(5);
                    _ = g.Value;
                }
            }
            g.To(1, 200);
            Frames(100);
            double before = g.Value;
            g.To(0, 200);
            double after = g.Value;
            Frames(15);
            bool ok = before is > 0.2 and < 0.8 && Math.Abs(after - before) < 0.05 && g.Value > after - 0.02 && g.Moving;
            Frames(60);
            g.To(0, 200);
            Frames(160);
            ok &= !g.Moving && g.Value == 0;
            g.To(1, 200);
            Thread.Sleep(300);
            double late = g.Value;
            ok &= g.Moving && late < 0.2;
            Console.WriteLine($"glide: {(ok ? "ok" : "SBAGLIATO")} (halfway {before:0.00}, after the turn {after:0.00}, after a 300 ms wait {late:0.00})");
            return ok ? 0 : 1;
        }

    case "voice":
        {
            // What spoken words ask for (VoiceWords), in the languages it knows: the verb and the app's name left out, the words of
            // their own (music, shuffle, favorites) and the kinds ("the album…", "songs by…") read, an article dropped for a second try.
            var cases = new (string Said, VoiceMeaning Meaning, VoiceFocus Focus, string Name)[]
            {
                ("play Bohemian Rhapsody on UMP", VoiceMeaning.Name, VoiceFocus.Any, "Bohemian Rhapsody"),
                ("", VoiceMeaning.AnyMusic, VoiceFocus.Any, ""),
                ("metti un po' di musica su Ultimate MP3 Player", VoiceMeaning.AnyMusic, VoiceFocus.Any, ""),
                ("riproduci i miei preferiti", VoiceMeaning.Favorites, VoiceFocus.Playlist, "i miei preferiti"),
                ("spiele Zufallswiedergabe", VoiceMeaning.Shuffle, VoiceFocus.Any, ""),
                ("lecture aléatoire", VoiceMeaning.Shuffle, VoiceFocus.Any, ""),
                ("metti musica a caso", VoiceMeaning.Shuffle, VoiceFocus.Any, ""),
                ("play my music on shuffle", VoiceMeaning.Shuffle, VoiceFocus.Any, ""),
                ("riproduci l'album Thriller", VoiceMeaning.Name, VoiceFocus.Album, "Thriller"),
                ("canzoni dei Queen", VoiceMeaning.Name, VoiceFocus.Artist, "Queen"),
                ("écoute la playlist Été 2024", VoiceMeaning.Name, VoiceFocus.Playlist, "Été 2024"),
                ("pon canciones de Shakira", VoiceMeaning.Name, VoiceFocus.Artist, "Shakira"),
                ("Musik von Rammstein", VoiceMeaning.Name, VoiceFocus.Artist, "Rammstein"),
                ("toque músicas de Anitta no UMP", VoiceMeaning.Name, VoiceFocus.Artist, "Anitta"),
                ("la radio Deejay", VoiceMeaning.Name, VoiceFocus.Station, "Deejay"),
                ("the playlist", VoiceMeaning.Name, VoiceFocus.Any, "the playlist"),
                ("Radio Ga Ga", VoiceMeaning.Name, VoiceFocus.Station, "Ga Ga"),
            };
            bool ok = true;
            foreach (var (said, meaning, focus, name) in cases)
            {
                var got = VoiceWords.Read(said);
                bool right = got.Meaning == meaning && got.Focus == focus && got.Name == name;
                ok &= right;
                if (!right) Console.WriteLine($"  “{said}” → {got.Meaning} {got.Focus} “{got.Name}”, expected {meaning} {focus} “{name}”");
            }
            var names = VoiceWords.Names("i Queen").ToList();
            ok &= names.SequenceEqual(new[] { "i Queen", "Queen" }) && VoiceWords.Names("Queen").Count() == 1;
            Console.WriteLine($"voice: {(ok ? "ok" : "SBAGLIATO")} ({cases.Length} phrases)");
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

    case "backup":
        {
            // A restore that can't go through leaves today's data as it was and the app saving; one that can puts back the backup's
            // and only that. It writes over the data: only with UMP_DATA.
            if (Environment.GetEnvironmentVariable("UMP_DATA") is not { Length: > 0 })
            {
                Console.WriteLine("backup: set UMP_DATA");
                return 1;
            }
            var data = AppPaths.DataDir;
            string At(string name) => Path.Combine(data, name);
            void Put(string name, string text)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(At(name))!);
                File.WriteAllText(At(name), text);
            }
            string Read(string name) => File.Exists(At(name)) ? File.ReadAllText(At(name)) : "";
            // Where the data is, as the phones say it: also after something asked, and with the data of a version that looked elsewhere.
            AppPaths.Use(At("phone"));
            bool place = AppPaths.DataDir == At("phone") && AppPaths.LibraryFile == Path.Combine(At("phone"), "library.json");
            AppPaths.Use(data);
            Put("stray/settings.json", "stray");
            Put("stray/library.json", "stray");
            Put("stray/covers/a.jpg", "stray");
            Put("kept/engines/x", "kept");
            AppPaths.MoveData(At("stray"), At("kept"));
            place &= Read("kept/settings.json") == "stray" && Read("kept/covers/a.jpg") == "stray" && Read("kept/engines/x") == "kept" && !File.Exists(At("stray/library.json"));
            Put("stray/settings.json", "again");
            Put("stray/library.json", "again");
            AppPaths.MoveData(At("stray"), At("kept"));
            place &= Read("kept/library.json") == "stray" && Read("stray/library.json") == "again" && AppPaths.DataDir == data;
            if (!place) Console.WriteLine("SBAGLIATO: where the data is");
            Put("library.json", "backup");
            Put("covers/a.jpg", "a");
            Put("profiles/p/data.json", "backup");
            Put("settings.json", "{\"MusicDir\":\"elsewhere\"}");
            var good = At("good.zip");
            using (var fs = File.Create(good)) Backup.Write(fs, "device", "1.0", 1, 1);
            // (a cover that should be a folder: the last entry can't be taken out)
            var bad = At("bad.zip");
            File.Copy(good, bad, true);
            using (var zip = ZipFile.Open(bad, ZipArchiveMode.Update)) zip.CreateEntry("covers/a.jpg/x.txt").Open().Dispose();
            Put("library.json", "today");
            Put("library.json.bak", "today");
            Put("profiles/p/data.json", "today");
            Put("covers/b.jpg", "b");
            File.Delete(At("covers/a.jpg"));
            var here = new AppSettings { MusicDir = "here" };
            bool failed = false;
            try { Backup.Restore(bad, here); }
            catch { failed = true; }
            bool ok = place && failed && !JsonStore.Frozen && Read("library.json") == "today" && Read("profiles/p/data.json") == "today" && Read("covers/b.jpg") == "b"
                      && !File.Exists(At("covers/a.jpg")) && !Directory.Exists(At("restoring"));
            Backup.Restore(good, here);
            ok &= JsonStore.Frozen && Read("library.json") == "backup" && Read("profiles/p/data.json") == "backup" && Read("covers/a.jpg") == "a"
                  && !File.Exists(At("covers/b.jpg")) && !File.Exists(At("library.json.bak")) && Read("settings.json").Contains("\"here\"")
                  && !Directory.Exists(At("restoring"));
            Console.WriteLine("backup: " + (ok ? "ok" : "SBAGLIATO"));
            return ok ? 0 : 1;
        }

    case "room":
        {
            // A room on this computer only: strangers (no hello yet) send no files, the host's id and a guest's don't open the door
            // without the password, a member's files stay inside their folder and their size, a whole list added goes out once.
            TogetherSession.ListenOn = IPAddress.Loopback;
            var todo = new BlockingCollection<Action>();
            new Thread(() => { foreach (var a in todo.GetConsumingEnumerable()) a(); }) { IsBackground = true }.Start();
            T On<T>(Func<T> f)
            {
                var t = new TaskCompletionSource<T>();
                todo.Add(() => t.SetResult(f()));
                return t.Task.Result;
            }
            var dir = Path.Combine(Path.GetTempPath(), "ump-room-" + Ids.New());
            Directory.CreateDirectory(Path.Combine(dir, "in"));
            var received = new List<string>();
            var host = new TogetherSession(new Member { Id = "host", Name = "Host" }, todo.Add)
            {
                TempFile = (id, ext) => Path.Combine(dir, "in", id + ext),
            };
            host.FileReceived += (id, _) => received.Add(id);
            On(() => { host.Create(new RoomOptions { Name = "r", Password = "pw", DefaultPerms = Perm.All }); return 0; });
            async Task<(Connection C, List<Msg> Got)> Knock(Member? me, string? password)
            {
                var c = await Connection.ConnectAsync("127.0.0.1", host.ListenPort, TimeSpan.FromSeconds(4));
                var got = new List<Msg>();
                c.Message += (_, m) => { lock (got) got.Add(m); };
                c.Start();
                if (me != null) c.Send(new Msg { T = "hello", Proto = TogetherSession.Proto, Me = me, Password = password });
                await Task.Delay(400);
                return (c, got);
            }
            async Task SendFile(Connection c, string id, long size, byte[] bytes)
            {
                await c.SendBulkAsync(new Msg { T = "fileStart", Id = id, Size = size, Text = ".mp3" });
                await c.SendChunkAsync(id, bytes, bytes.Length);
                await c.SendBulkAsync(new Msg { T = "fileEnd", Id = id, Flag = true });
                await Task.Delay(400);
            }
            int Files() => Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;

            var (stranger, _) = await Knock(null, null);
            await SendFile(stranger, "abc", 3, new byte[3]);
            bool ok = received.Count == 0 && Files() == 0;
            Console.WriteLine("  a stranger's file: " + (ok ? "ok" : "SBAGLIATO"));

            var (fake, _) = await Knock(new Member { Id = "host", Name = "Fake" }, null);
            bool door = fake.IsClosed && On(() => host.Members.Count) == 1;
            var (guest, _) = await Knock(new Member { Id = "guest", Name = "Guest" }, "pw");
            var (thief, refused) = await Knock(new Member { Id = "guest", Name = "Thief" }, null);
            door &= On(() => host.Members.Count) == 2 && On(() => host.Members[1].Name) == "Guest" && !guest.IsClosed
                    && refused.Any(m => m.T == "deny") && refused.All(m => m.T != "welcome");
            // (the guest back after losing the connection)
            var (back, seen) = await Knock(new Member { Id = "guest", Name = "Guest" }, "pw");
            door &= seen.Any(m => m.T == "welcome");
            Console.WriteLine("  the password: " + (door ? "ok" : "SBAGLIATO"));

            await SendFile(back, "../out", 3, new byte[3]);
            await SendFile(back, "big", 3, new byte[10]);
            bool inside = received.Count == 0 && Files() == 0;
            await SendFile(back, "fine", 3, new byte[3]);
            inside &= received.SequenceEqual(new[] { "fine" }) && Files() == 1;
            Console.WriteLine("  a member's files: " + (inside ? "ok" : "SBAGLIATO"));

            lock (seen) seen.Clear();
            On(() =>
            {
                for (int i = 0; i < 100; i++) host.Add(new RoomTrack { Title = "Song " + i }, i % 2 == 0 ? new byte[200] : null, false);
                return 0;
            });
            await Task.Delay(800);
            List<Msg> queues;
            lock (seen) queues = seen.Where(m => m.T == "queue").ToList();
            bool once = queues.Count is >= 1 and <= 3 && queues[^1].Queue!.Count == 99 && queues[^1].Current?.Title == "Song 0";
            Console.WriteLine($"  a list added, {queues.Count} messages: " + (once ? "ok" : "SBAGLIATO"));

            host.Dispose();
            try { Directory.Delete(dir, true); } catch { }
            ok &= door && inside && once;
            Console.WriteLine("room: " + (ok ? "ok" : "SBAGLIATO"));
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
            // "Remove duplicates" (Profile.ReplaceTracks): the copy's plays and listening time join the kept song's, all-time and in the months.
            var before = profile.Log.Totals("x");
            profile.AddListening("y", 30, true);
            profile.ReplaceTracks(new Dictionary<string, string> { ["y"] = "x" });
            var month = profile.Log.Month(DateTime.Now.Year, DateTime.Now.Month);
            ok &= profile.StatsOf("y") == null && profile.StatsOf("x") is { Plays: 6, Seconds: 140 } && profile.Log.Totals("x").Plays == before.Plays + 1
                && month != null && !month.Songs.ContainsKey("y");
            // Artists never suggested: "A feat. B" left out with B, the name kept once (any case), and back in.
            profile.SetUnsuggested("Slop Maker", true);
            profile.SetUnsuggested("slop maker", true);
            ok &= profile.UnsuggestedSnapshot().Count == 1 && !profile.Suggestible("Someone feat. Slop Maker") && profile.Suggestible("Slop")
                && profile.Suggestible(null);
            profile.SetUnsuggested("SLOP MAKER", false);
            ok &= profile.Suggestible("Someone feat. Slop Maker") && !profile.IsUnsuggested("Slop Maker");
            Profile.Delete(info.Id);
            Console.WriteLine("seal: " + (ok ? "ok" : "SBAGLIATO"));
            return ok ? 0 : 1;
        }

    case "ownsongs":
        {
            // Each profile's own library (5.5, set UMP_DATA): a profile from before takes the whole library once, a new one starts
            // empty; a song deleted by one profile stays the other's (its file too) and keeps its plays; deleted by the last one,
            // it goes with its file (DropOrphans). Statistics cleared for one profile only.
            var dir = Path.Combine(Path.GetTempPath(), "ump-ownsongs-" + Ids.New());
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "song.mp3");
            File.WriteAllText(file, "x");
            var song = new Track { Title = "Own Song", Artist = "Tester", Duration = 100, Path = file, SourceUrl = "https://example.com/own" };
            lib.Add(song);
            var old = new ProfileInfo { Name = "own-old" };
            var fresh = new ProfileInfo { Name = "own-new" };
            Directory.CreateDirectory(AppPaths.ProfileDir(old.Id));
            File.WriteAllText(Path.Combine(AppPaths.ProfileDir(old.Id), "data.json"), "{}");
            var list = new ProfileList { Profiles = { old, fresh } };
            var a = Profile.Get(old);
            var b = Profile.Get(fresh);
            Profile.TakeLibrary(list, lib);
            bool ok = a.Has(song.Id) && !b.Has(song.Id);
            b.Own(new[] { song.Id });
            a.AddListening(song.Id, 100, true);
            b.AddListening(song.Id, 100, true);
            a.Disown(new[] { song.Id });
            a.ForgetTracks(new[] { song.Id });
            ok &= !a.Has(song.Id) && a.StatsOf(song.Id)?.Plays == 1 && Profile.AnyHas(list, song.Id) && Profile.DropOrphans(list, lib) == 0
                  && File.Exists(file);
            b.ClearStats();
            ok &= b.StatsOf(song.Id) == null && a.StatsOf(song.Id)?.Plays == 1 && b.Has(song.Id);
            b.Disown(new[] { song.Id });
            ok &= !Profile.AnyHas(list, song.Id) && Profile.DropOrphans(list, lib) == 1 && lib.Get(song.Id) == null && !File.Exists(file);
            Profile.Delete(old.Id);
            Profile.Delete(fresh.Id);
            try { Directory.Delete(dir, true); } catch { }
            Console.WriteLine("ownsongs: " + (ok ? "ok" : "SBAGLIATO"));
            return ok ? 0 : 1;
        }

    case "videocache":
        {
            // The music videos' cache (5.5): the last VideoCache.Max files stay (never the ones kept: the song playing, the next),
            // the links found and "none found" stay by the songs' keys; a video leaves for beside its song's audio and comes back.
            var dir = Path.Combine(Path.GetTempPath(), "ump-videos-" + Ids.New());
            var cache = new VideoCache(Path.Combine(dir, "cache"));
            string Temp(int i)
            {
                var f = Path.Combine(cache.TempDir, $"v{i}.mp4");
                File.WriteAllText(f, "v" + i);
                return f;
            }
            var keep = cache.Adopt(new[] { "k0" }, "Song 0", "A", Temp(0));
            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { keep };
            for (int i = 1; i <= VideoCache.Max + 3; i++) cache.Adopt(new[] { "k" + i }, "Song " + i, "A", Temp(i), kept);
            bool ok = cache.Stats().Count == VideoCache.Max && cache.FileFor(new[] { "k0" }) == keep && cache.FileFor(new[] { "k1" }) == null
                      && cache.FileFor(new[] { "k" + (VideoCache.Max + 3) }) != null;
            cache.SetUrl(new[] { "k1" }, "https://example.com/v1", chosen: true);
            cache.SetNone(new[] { "k99" });
            ok &= cache.Known(new[] { "k1" }) == ("https://example.com/v1", true, false) && cache.Known(new[] { "k99" }).None;
            var beside = Path.Combine(dir, "beside.mp4");
            ok &= cache.MoveOut(new[] { "k0" }, beside) == beside && File.Exists(beside) && cache.FileFor(new[] { "k0" }) == null;
            var back = cache.Adopt(new[] { "k0" }, "Song 0", "A", beside);
            ok &= !File.Exists(beside) && cache.FileFor(new[] { "k0" }) == back && cache.Stats().Count == VideoCache.Max;
            cache.Forget(new[] { "k0" });
            ok &= cache.FileFor(new[] { "k0" }) == null && !File.Exists(back);
            cache.Flush();
            var again = new VideoCache(Path.Combine(dir, "cache"));
            ok &= again.Known(new[] { "k1" }).Url == "https://example.com/v1" && again.Stats().Count == VideoCache.Max - 1;
            try { Directory.Delete(dir, true); } catch { }
            Console.WriteLine("videocache: " + (ok ? "ok" : "SBAGLIATO"));
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

    case "suggested":
        {
            // The suggested queue online (it reaches out): two asks for the same song give 25 songs each, without the song
            // itself or doubles, and not the same queue.
            var seed = new RadioSeed("Heroes Tonight", "Janji", Array.Empty<string>());
            var a = await OnlineSearchServices.SimilarAsync(seed, RadioSources.Auto, 25, CancellationToken.None);
            var b = await OnlineSearchServices.SimilarAsync(seed, RadioSources.Auto, 25, CancellationToken.None);
            string Key(SearchHit h) => SongMeta.Key(h.Title, h.Artist);
            bool clean(List<SearchHit> l) => l.Count == 25 && l.Select(Key).Distinct().Count() == 25 && !l.Any(h => Key(h) == SongMeta.Key(seed.Title, seed.Artist));
            bool differ = !a.Select(Key).SequenceEqual(b.Select(Key));
            Console.WriteLine($"{a.Count} and {b.Count} songs, {a.Select(Key).Intersect(b.Select(Key)).Count()} in both");
            foreach (var h in a.Take(5)) Console.WriteLine($"  {h.Artist} - {h.Title} ({h.Service})");
            // How many each site gives to pick from (asked for 100).
            foreach (var site in RadioSources.Sites)
                Console.WriteLine($"  {site}: {(await OnlineSearchServices.SimilarAsync(seed, site, 100, CancellationToken.None)).Count} to pick from");
            bool ok = clean(a) && clean(b) && differ;
            Console.WriteLine(ok ? "ok" : "WRONG");
            return ok ? 0 : 1;
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
// --analyze: what the card would add (title and artist as the library takes them, the cover), nothing downloaded.
if (Flag("--analyze"))
{
    foreach (var item in r.Items)
    {
        var meta = SongMeta.From(item);
        Console.WriteLine($"  {meta.Artist ?? "?"} - {meta.Title} | {item.Thumbnail ?? "no cover"}");
    }
    return 0;
}
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
