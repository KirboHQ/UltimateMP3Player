#if PHONE_TOUR
using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using SkiaSharp;
using UltimateMP3Player.Core;
using UltimateMP3Player.Core.Together;
using UltimateMP3Player.ViewModels;
using UltimateMP3Player.Views;

namespace UltimateMP3Player;

// The phone's "tour" (Tour.cs for the desktops), built only with -p:PhoneTour=true: every page, sheet and dialog opened one after the
// other. The touches are real: what it wants done goes to logcat (tag UMPTOUR: TAP, PRESS, SWIPE, BACK, SHOT) and a script on the
// computer does it with adb (tools/phone-tour.py), so what's measured is what a finger gets. The music stays at volume 0.
public static class PhoneTour
{
    private static MainViewModel M = null!;
    private static MainView V = null!;
    private static readonly List<string> NotOpened = new();
    private static int _sheets;

    private static void Say(string line) => Android.Util.Log.Info("UMPTOUR", line);

    public static async Task Run(MainViewModel main, MainView view)
    {
        M = main;
        V = view;
        try
        {
            await Task.Delay(4000);
            V.Sheets.CloseAll();
            await Seed();
            M.Player.Volume = 0;
            M.Player.AutoQueue = false;
            M.Player.PlayFrom(M.LibraryPage, M.AllVms().First());
            await Task.Delay(1500);
            Say("scene | fps moving | p95 ms | worst ms | at rest: ms CPU/s");
            if (MainActivity.Current?.Intent?.GetStringExtra("tour") == "themes") await ThemesTour();
            else if (MainActivity.Current?.Intent?.GetStringExtra("tour") == "flings") await Flings();
            else await Script();
            Say($"opened: {_sheets} sheets; not opened as a person would: {NotOpened.Count}");
            foreach (var n in NotOpened) Say("NOT OPENED: " + n);
        }
        catch (Exception e)
        {
            Say("FAILED: " + e);
        }
        Say("tour: done");
    }

    // Only the lists flung, again and again (am start --es tour flings): what stops them.
    // What the theme RGB costs, Settings' themes, the song's bar in every shape, the library's playlist.
    private static async Task ThemesTour()
    {
        await Task.Delay(2500);
        await Page("home", () => M.IsHome = true);
        await CpuFor("from the song, playing", 10);
        M.Settings.SelectThemeCommand.Execute(ThemeColors.RgbId);
        await CpuFor("rgb, playing", 10);
        M.Player.PlayPauseCommand.Execute(null);
        await CpuFor("rgb, paused", 10);
        M.Settings.RgbSpeed = 10;
        await CpuFor("rgb 10x, paused", 10);
        M.Player.PlayPauseCommand.Execute(null);
        await CpuFor("rgb 10x, playing", 10);
        M.Player.PlayPauseCommand.Execute(null);
        M.Settings.RgbSpeed = 1;
        M.Settings.SelectThemeCommand.Execute(ThemeColors.DynamicId);
        await CpuFor("from the song, paused", 10);
        M.Player.PlayPauseCommand.Execute(null);
        M.Settings.SelectThemeCommand.Execute(ThemeColors.RgbId);
        await Page("settings themes", M.GoSettings);
        V.GetVisualDescendants().OfType<SettingsPage>().First().OpenGroup("appearance");
        M.Settings.RgbSpeed = 4;
        await Scene("settings rgb speed", null, 500);
        M.Settings.RgbSpeed = 1;
        // The song bar's row with a finger: scrolled to, touched, its list open.
        if (V.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Tag as string == "SeekStyleChoice") is not { } row) NotOpened.Add("the song bar's row");
        else
        {
            row.BringIntoView();
            await Scene("settings song bar row", null, 900);
            var at = row.PointToScreen(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2));
            await Scene("sheet: shape of the song bar", () => Say($"TAP {at.X} {at.Y}"), 900);
            if (V.Sheets.IsOpen) _sheets++;
            else NotOpened.Add("the song bar's row: the touch opened nothing");
            await Close();
        }
        foreach (var shape in Enum.GetValues<VisualizerStyle>())
        {
            M.Settings.SeekStyleChoice = M.Settings.VisualizerStyles.First(c => Equals(c.Value, shape));
            await Page("settings bar " + shape, M.GoSettings);
            await Page("song bar " + shape, () => M.GoNowPlayingCommand.Execute(null));
        }
        M.Settings.SeekStyleChoice = M.Settings.VisualizerStyles.First(c => Equals(c.Value, VisualizerStyle.Bars));
        M.Settings.SelectThemeCommand.Execute(ThemeColors.DynamicId);
    }

    private static async Task Flings()
    {
        await Task.Delay(2500);
        await Page("an artist", () => M.OpenArtist("Lumen Harbor", null));
        await Task.Delay(1500);
        for (int i = 0; i < 4; i++) await Fling("an artist");
        await Page("stats", M.OpenStats);
        for (int i = 0; i < 4; i++) await Fling("stats");
        await Page("library", () => M.IsLibrary = true);
        for (int i = 0; i < 3; i++) await Fling("library");
        await Page("radio", M.OpenStations);
        await CpuFor("radio", 8);
        await Fling("radio");
        await Scene("radio, the list again", () => M.Stations.SelectChipCommand.Execute(M.Stations.Chips[1]), 1500);
        Say($"memory of the app: {Process.GetCurrentProcess().WorkingSet64 >> 20} MB, collections: {GC.CollectionCount(0)}");
    }

    private static async Task Script()
    {
        var songs = M.AllVms().Take(12).ToList();
        var three = songs.Take(3).ToList();
        var track = songs[2];
        M.Profile.CreateTag("Workout", "#E5484D");
        M.Profile.CreateTag("Road trip", "#2EB67D");
        M.Profile.Follow(new FollowedArtist { Name = "Lumen Harbor" });
        M.Profile.SaveStation(new Station { Id = "tour-fm", Name = "Tour FM", Url = "http://127.0.0.1:9/stream", Tags = "rock,indie", Country = "Italia", Codec = "MP3" });
        await Task.Delay(500);
        M.SetTag(songs.Take(6).ToList(), M.Tags[0], true);
        foreach (var s in songs.Skip(1).Take(6)) M.Player.Enqueue(s, true);
        var list = M.Playlists.First(p => !p.IsFavorites);

        // Pages, flung by a finger.
        await Page("home", () => M.IsHome = true);
        await Fling("home");
        await Page("library", () => M.IsLibrary = true);
        await Fling("library");
        await Page("not in a playlist", () => M.IsUnsorted = true);
        await Page("playlists", () =>
        {
            M.OpenArtists();
            V.Hub!.Tab = 1;
        });
        await Page("artists", () =>
        {
            M.OpenArtists();
            V.Hub!.Tab = 3;
        });
        await Page("radio", M.OpenStations);
        await CpuFor("radio", 12);
        await Page("a playlist", () => M.OpenPlaylist(list));
        for (int i = 0; i < 3; i++) await Fling("a playlist");
        await Page("a tag's songs", () => M.OpenTag(M.Tags[0]));
        await Page("an artist", () => M.OpenArtist("Lumen Harbor", null));
        await Page("search", () => M.IsSearch = true);
        await Scene("search, typing", () => SearchBox().Text = "lumen", 900);
        SearchBox().Text = "";
        await SearchOnline();
        await Page("downloads", () => M.IsDownloads = true);
        await Page("stats", M.OpenStats);
        await Fling("stats");
        await Page("recap", () => M.OpenRecap(DateTime.Now.Year, 0));
        await Page("settings", M.GoSettings);
        var settingsPage = V.GetVisualDescendants().OfType<SettingsPage>().First();
        foreach (var group in new[] { "appearance", "playback", "download", "search", "library", "backup", "connect", "integrations", "about" })
        {
            await Scene("settings, " + group, () => settingsPage.OpenGroup(group), 300);
            Say("SWIPE 720 1700 720 300 260");
            await Task.Delay(700);
            await Scene("settings, " + group + ", further", null, 120);
        }
        settingsPage.OpenGroup(null);

        // The song's page and its sheets.
        await Page("song page", () => M.GoNowPlayingCommand.Execute(null));
        await Sheet("up next", () => QueueSheet.Show(M.Player));
        await Sheet("speed", () => SpeedSheet.Show(M.Player));
        await Sheet("the song's menu", () => Menus.Open(Menus.ForTrack(M.Player.Current ?? track, null)));
        await Sheet("lyrics menu", () => Menus.Open(Menus.LyricsMenu(track)));
        var now = M.Player.Current!.T;
        File.WriteAllLines(LyricsStore.PathFor(now.Id, true), Enumerable.Range(0, 60).Select(i => $"[{i / 60:00}:{i % 60 * 3 % 60:00}.00] A line of the song, number {i}, long enough to read"));
        now.Lyrics = LyricsKind.Synced;
        await Scene("song page, lyrics", M.ShowLyrics, 2500);
        Say("BACK");
        await Task.Delay(600);

        // The menus, by a long press where there's something to press.
        await Page("library again", () => M.IsLibrary = true);
        await Press("a song", d => d is SongItem or TrackRow, () => Menus.ForTrack(track, M.LibraryPage));
        await Sheet("add to a playlist", () => Menus.Open(Menus.AddToPlaylistMenu(track)));
        await Sheet("several songs: add to a playlist", () => Menus.Open(Menus.AddManyToPlaylist(three)));
        await Sheet("several songs: tags", () => Menus.Open(Menus.TagMenu(three)));
        await Sheet("filter by tag", () => Menus.Open(Menus.TagFilterMenu(M.LibraryPage.TagFilter, M)));
        await Sheet("filter by where", () => Menus.Open(Menus.StorageFilterMenu(M.LibraryPage.Storage)));
        await Selecting();
        await Page("playlists again", () =>
        {
            M.OpenArtists();
            V.Hub!.Tab = 1;
        });
        await Press("a playlist", d => d is PlaylistViewModel, () => Menus.ForPlaylist(list));
        await Sheet("a playlist's tags", () => Menus.Open(Menus.PlaylistTagMenu(list)));
        await Sheet("several playlists: tags", () => Menus.Open(Menus.PlaylistsTagMenu(M.Playlists.Take(3).ToList())));
        await Sheet("a tag", () => Menus.Open(Menus.ForTag(M.Tags[0])));
        await Page("artists again", () =>
        {
            M.OpenArtists();
            V.Hub!.Tab = 3;
        });
        await Press("an artist", d => d is ArtistCardViewModel, () => Menus.ForArtistCard(new ArtistCardViewModel(M, "Lumen Harbor", null, null)));
        await Page("an artist again", () => M.OpenArtist("Lumen Harbor", null));
        await Sheet("an artist's page", () => { if (M.Page is ArtistPageViewModel a) Menus.Open(Menus.ForArtist(a)); });
        await Sheet("a radio", () => Menus.Open(Menus.ForTrack(M.Vm(new Track { Id = Station.Prefix + "tour-fm", Title = "Tour FM", Artist = "Italia" }), null)));
        var suggested = new Track { Id = RadioSong.Prefix + "tour", Title = "Suggested Song", Artist = "Neon Tide", SourceUrl = "https://example.com/suggested" };
        await Sheet("a suggested song", () => Menus.Open(Menus.ForRadioItem(new RadioItemViewModel(new RadioSong { Id = suggested.Id, Title = suggested.Title, Artist = suggested.Artist, Url = "https://example.com/suggested" }, suggested), M, null)));
        var hit = new OnlineHitViewModel(new SearchHit { Service = OnlineSearchServices.Deezer, Title = "Found Online", Artist = "Blue Atlas", Url = "https://example.com/hit" }, M);
        await Sheet("a song found online", () => { if (Menus.ForHit(hit) is { } m) Menus.Open(m); });
        await Sheet("a song in up next", () => Menus.Open(Menus.ForQueue(M.Player.UpNext[0])));
        var job = new DownloadJobViewModel(new MediaItem { Title = "Glass Avenue" }, new DownloadBatch(M.Profile, null, 1), 1, false, M.Host.Downloads);
        await Sheet("a song being downloaded", () => { if (Menus.ForJob(job) is { } m) Menus.Open(m); });

        // The download card.
        await M.Downloads.ImportListAsync("Track URI,Track Name,Artist Name(s),Duration (ms)\nspotify:track:4uLU6hMCjMI75M1A2tKUQC,Never Gonna Give You Up,Rick Astley,213573\n" +
                                          "spotify:track:7ouMYWpwJ422jRcDASZB7P,Heroes,David Bowie,371000\n", "Tour list");
        M.Host.Downloads.Groups.FirstOrDefault()?.OpenCommand.Execute(null);
        await Page("a download card", () => M.IsDownloads = true);
        if (M.Downloads.Link is { } link)
        {
            await Sheet("download card: tags", () => Menus.Open(Menus.TagPicker(link.TagIds, M, link.OnTagsChosen)));
            await Sheet("download card: playlists", () => Menus.Open(Menus.PlaylistPicker(link)));
        }
        else NotOpened.Add("the download card");

        await Room(songs);

        // Dialogs.
        var settings = M.Host.Settings;
        await Sheet("a message", () => Dialogs.Alert("A message", "Something a person has to read before going on, in a couple of lines of text at most."));
        await Sheet("a question", () => _ = Dialogs.ConfirmAsync("Delete playlist", "Sure?", "Delete", true));
        await Sheet("three answers", () => _ = Dialogs.ChooseAsync("What do you want to do?", "Pick one of these.", "The first", "The second", "The third"));
        await Sheet("ticks", () => _ = Dialogs.ChecksAsync("What to import", "Choose what comes in.", new[] { ("Liked songs", true), ("Playlists", true), ("Plays", false) }, "Import"));
        await Sheet("a name", () => _ = Dialogs.PromptAsync("A name", "Give it a name", "Something"));
        await Sheet("a form", () => _ = Dialogs.FormAsync("Last.fm", "Your own key.", ("Get a key", "https://example.com"), ("Key", ""), ("Secret", "")));
        await Sheet("edit a song", () => _ = Dialogs.EditTrackAsync(track.T));
        await Sheet("terms", () => _ = Dialogs.TermsAsync(false));
        await Sheet("what's new", () => _ = Dialogs.WhatsNewAsync(new[] { new ReleaseNote(new Version(9, 9, 9), "A title", "## What changed\n- One thing\n- Another thing\n"), new ReleaseNote(new Version(9, 9, 8), "An older one", "- Something else\n") }, settings));
        var words = new LyricsResult { Kind = LyricsKind.Plain, Plain = "A line\nAnother line" };
        await Sheet("choose lyrics", () => _ = Dialogs.ChooseLyricsAsync("Glass Avenue", 200, new[] { new LyricsChoice("Lumen Harbor", "Glass Avenue", 200, true, "A line\nAnother line", words), new LyricsChoice("Somebody", "Glass Avenue", 260, false, "A line\nAnother line", words) }));
        await Sheet("choose the artist", () => _ = Dialogs.ChooseArtistAsync("Lumen Harbor", new[] { new ArtistChoice(new ArtistSite { Id = "1", Name = "Lumen Harbor", Fans = 12345, Songs = 3 }, 3, true), new ArtistChoice(new ArtistSite { Id = "2", Name = "Lumen Harbor", Fans = 12 }, 3, false) }));
        await Sheet("delete songs", () => _ = Dialogs.RemoveAsync(new RemovePlan(three)));
        await Sheet("quality", () => _ = Dialogs.QualityAsync(new QualityPlan("Glass Avenue", "Lumen Harbor", "mp3", null, true, false, true)));
        await Sheet("choose the video", () => _ = Dialogs.PickVideoAsync(M, songs[0]));
        await Sheet("new tag", () => _ = TagDialogs.EditAsync(null, "#7C5CFF"));
        await Sheet("a tag's songs: choose", () => _ = TagDialogs.ChooseSongsAsync(M.Tags[0]));
        await Sheet("tags on a playlist's songs", () => _ = TagDialogs.ApplyToSongsAsync(list, songs));
        await Sheet("export", () => PackDialogs.Export(M, list));
        var manifest = new PackManifest { Music = false, Author = "Tour" };
        for (int i = 0; i < 12; i++) manifest.Tracks.Add(new PackTrack { Id = "p" + i, Title = "A song " + i, Artist = "An artist", Duration = 200, SourceUrl = "https://example.com/p" + i });
        await Sheet("import a pack", () => _ = PackDialogs.Import(M, PackFile.Remote("Test pack", manifest, 4096, Array.Empty<string>(), (_, _, _, _) => Task.FromResult(false), null)));
        await Sheet("connect to computer", LinkSheet.Show);
        await Sheet("logins", LoginsSheet.Show);
        await Scene("profiles", () => V.ShowProfiles(false), 700);
        Say("BACK");
        await Task.Delay(800);
    }

    // The search with the sites' songs under yours, as anyone sees it: Deezer and the others really asked.
    private static async Task SearchOnline()
    {
        M.Host.Settings.OnlineSearch = true;
        M.Search.OnSettingsChanged();
        M.IsSearch = true;
        await Task.Delay(400);
        SearchBox().Text = "queen";
        await Task.Delay(5000);
        await Scene("search online, results", null, 400);
        await Press("a song found online, for real", d => d is OnlineHitViewModel { IsSong: true }, () => null);
        await Fling("search online");
        foreach (var filter in M.Search.Filters.ToList())
        {
            await Scene("search online: " + filter.Key, () => M.Search.Filter = filter.Key, 900);
            await Fling("search online: " + filter.Key);
        }
        SearchBox().Text = "";
    }

    private static TextBox SearchBox() => V.GetVisualDescendants().OfType<SearchPage>().First().FindControl<TextBox>("Box")!;

    private static async Task Selecting()
    {
        if (Find(d => d is SongItem or TrackRow) is not { } row || Views.Selection.Of(row) is not { } sel)
        {
            NotOpened.Add("choosing several songs: no list on the screen");
            return;
        }
        await Scene("choosing several songs", () => sel.Start(row.DataContext), 500);
        if (Menus.ForSelection(sel) is { } menu) await Sheet("several songs", () => Menus.Open(menu));
        else NotOpened.Add("several songs: no menu");
        sel.Stop();
        await Task.Delay(400);
    }

    // A room for real: this phone is the host, three people come in over the loopback.
    private static async Task Room(List<TrackViewModel> songs)
    {
        var room = M.Together;
        await Page("together", () => M.IsTogether = true);
        room.CreateCommand.Execute(null);
        for (int i = 0; i < 60 && !M.InRoom; i++) await Task.Delay(50);
        if (!M.InRoom)
        {
            NotOpened.Add("a room");
            return;
        }
        var host = (TogetherSession)typeof(TogetherViewModel).GetField("_s", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(room)!;
        var guests = new List<TogetherSession>();
        foreach (var (name, color) in new[] { ("Marta", "#E5484D"), ("Kilo", "#2EB67D"), ("Sera", "#3E8BFF") })
        {
            var guest = new TogetherSession(new Member { Id = Ids.New(), Name = name, Color = color }, a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
            await guest.JoinAsync("127.0.0.1", host.ListenPort, null);
            guests.Add(guest);
        }
        room.Add(songs.Skip(3).Take(8).ToList());
        await Task.Delay(1200);
        await Page("a room", () => M.IsTogether = true);
        await Fling("a room");
        await Press("a song in the room", d => d is RoomItemViewModel, () => Menus.ForRoomItem(room.Queue[Math.Min(1, room.Queue.Count - 1)], room));
        V.GetVisualDescendants().OfType<TogetherPage>().First().FindControl<RadioButton>("TabPeople")!.IsChecked = true;
        await Task.Delay(400);
        await Press("a person in the room", d => d is MemberViewModel m && !m.IsMe, () => Menus.ForMembers(room, room.Members.First(m => !m.IsMe)) ?? new SheetMenu());
        await Sheet("in a room: a song of yours", () => Menus.Open(Menus.ForTrack(songs[0], M.LibraryPage)));
        foreach (var g in guests) g.Dispose();
        room.Leave();
        await Task.Delay(600);
    }

    // How long a page's first load goes on.
    private static async Task CpuFor(string name, int seconds)
    {
        var me = Process.GetCurrentProcess();
        var each = new List<string>();
        for (int i = 0; i < seconds; i++)
        {
            me.Refresh();
            var cpu = me.TotalProcessorTime;
            await Task.Delay(1000);
            me.Refresh();
            each.Add((me.TotalProcessorTime - cpu).TotalMilliseconds.ToString("0"));
        }
        Say($"{name}, ms of CPU each second: {string.Join(" ", each)}");
    }

    private static Task Page(string name, Action go) => Scene(name, go, 500);

    private static async Task Fling(string name)
    {
        await Scene(name + ", flung", () => Say("SWIPE 720 1700 720 300 120"), 1500);
        if (V.Sheets.IsOpen)
        {
            NotOpened.Add(name + ": flinging opened a menu");
            await Close();
        }
        await Scene(name + ", flung back", () => Say("SWIPE 720 500 720 1900 120"), 1500);
        if (V.Sheets.IsOpen)
        {
            NotOpened.Add(name + ": flinging back opened a menu");
            await Close();
        }
    }

    private static async Task Press(string name, Func<object?, bool> wanted, Func<SheetMenu?> otherwise)
    {
        var at = Find(wanted);
        if (at == null)
        {
            NotOpened.Add(name + ": nothing on the screen to press");
            await Sheet(name, () => { if (otherwise() is { } m) Menus.Open(m); });
            return;
        }
        var p = at.PointToScreen(new Point(at.Bounds.Width / 2, at.Bounds.Height / 2));
        await Scene("sheet: " + name, () => Say($"PRESS {p.X} {p.Y}"), 1500);
        if (!V.Sheets.IsOpen)
        {
            NotOpened.Add(name + ": the long press opened nothing");
            await Sheet(name, () => { if (otherwise() is { } m) Menus.Open(m); });
            return;
        }
        _sheets++;
        await Close();
    }

    private static async Task Sheet(string name, Action open)
    {
        await Scene("sheet: " + name, open, 650);
        if (V.Sheets.IsOpen) _sheets++;
        else NotOpened.Add(name);
        await Close();
    }

    private static async Task Close()
    {
        // (nothing open: Back would leave the page)
        if (V.Sheets.IsOpen)
        {
            Say("BACK");
            await Task.Delay(550);
            if (V.Sheets.IsOpen) V.Sheets.CloseAll();
        }
        await Task.Delay(120);
    }

    private static Control? Find(Func<object?, bool> wanted)
    {
        double height = V.Bounds.Height;
        foreach (var c in V.GetVisualDescendants().OfType<Control>())
        {
            if (!c.IsEffectivelyVisible || c.Bounds.Height < 24 || !wanted(c.DataContext) || Touch.GetMenu(c) == null) continue;
            var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), V);
            // (what a finger there would touch: a row scrolled under the page's header isn't)
            if (p is { } at && at.Y > 80 && at.Y < height - 200 && V.InputHitTest(at) is Visual hit && (hit == c || c.IsVisualAncestorOf(hit))) return c;
        }
        return null;
    }

    private static async Task Scene(string name, Action? go, int ms)
    {
        var times = new List<double>();
        var at = new List<double>();
        var collections = new List<int>();
        var headers = new List<bool>();
        var list = V.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.IsEffectivelyVisible && s.Bounds.Height > 300 && s.Extent.Height > s.Viewport.Height);
        var songs = list?.FindAncestorOfType<SongList>();
        var clock = Stopwatch.StartNew();
        bool ticking = true;
        var top = TopLevel.GetTopLevel(V)!;
        void Tick(TimeSpan _)
        {
            times.Add(clock.Elapsed.TotalMilliseconds);
            at.Add(list?.Offset.Y ?? 0);
            collections.Add(GC.CollectionCount(0));
            headers.Add(songs?.ContainerFromIndex(0) is { IsVisible: true });
            if (ticking) top.RequestAnimationFrame(Tick);
        }
        top.RequestAnimationFrame(Tick);
        go?.Invoke();
        await Task.Delay(ms);
        ticking = false;
        var gaps = times.Zip(times.Skip(1), (a, b) => b - a).OrderBy(g => g).ToList();
        double p95 = gaps.Count > 0 ? gaps[(int)(gaps.Count * 0.95)] : 0, worst = gaps.Count > 0 ? gaps[^1] : 0;
        double fps = times.Count > 1 ? (times.Count - 1) * 1000 / (times[^1] - times[0]) : 0;
        await Task.Delay(300);
        var me = Process.GetCurrentProcess();
        var cpu = me.TotalProcessorTime;
        var rest = Stopwatch.StartNew();
        await Task.Delay(1000);
        me.Refresh();
        double restCpu = (me.TotalProcessorTime - cpu).TotalMilliseconds / rest.Elapsed.TotalSeconds;
        // (when the long stops came, and where the list was: its header leaving, the finger coming down...)
        var stops = Enumerable.Range(1, Math.Max(0, times.Count - 1)).Where(i => times[i] - times[i - 1] > 40).Select(i => $"+{times[i - 1]:0} {times[i] - times[i - 1]:0}ms y{at[i - 1]:0}>{at[i]:0} gc+{collections[i] - collections[i - 1]} header {headers[i - 1]}>{headers[i]}");
        Say($"{name} | {fps:0} | {p95:0.0} | {worst:0} | {restCpu:0} | {string.Join("; ", stops)}");
        Say("SHOT " + System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-'));
        await Task.Delay(1100);
    }

    // ------------------------------------------------------------------ a library to look at

    private static readonly (uint A, uint B)[] Moods =
    {
        (0xFFE5173F, 0xFF7A0E22), (0xFF1DB954, 0xFF0A4F2A), (0xFF1E6BFF, 0xFF0B1F66), (0xFFFF8A1A, 0xFF8A2E05), (0xFF8A3DFF, 0xFF2B0F6B), (0xFF12C4B0, 0xFF06433D),
        (0xFFFFD21A, 0xFF8A6A05), (0xFFFF4FA3, 0xFF6B0F46), (0xFFC8C8C8, 0xFF202020), (0xFF7CC8FF, 0xFF2467A8), (0xFF8E1F4F, 0xFF26060F), (0xFFA6E22E, 0xFF3C5A08),
    };

    private static readonly string[] Artists = { "Lumen Harbor", "Neon Tide", "The Quiet Engines", "Marta Vale", "Kilo & the Foxes", "Aurora Fields", "Sette Venti", "Blue Atlas" };
    private static readonly string[] Words = { "Midnight", "Glass", "Summer", "Static", "Paper", "Electric", "Silver", "Hollow", "Golden", "Velvet" };
    private static readonly string[] Things = { "Avenue", "Hearts", "Signal", "Rain", "Horizon", "Machine", "Letters", "Ocean", "Lights", "Garden", "Motel" };

    private static async Task Seed()
    {
        if (M.Library.Get("tour0") != null) return;
        var folder = Path.GetDirectoryName(AppPaths.TrackCover("x"))!;
        var wav = Path.Combine(folder, "tour-song.wav");
        await Task.Run(() => WriteMusic(wav, 90));
        await Task.Run(() =>
        {
            for (int i = 0; i < 400; i++)
            {
                var (a, b) = Moods[i % Moods.Length];
                using var surface = SKSurface.Create(new SKImageInfo(320, 320));
                using var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(320, 320), new[] { new SKColor(a), new SKColor(b) }, SKShaderTileMode.Clamp) };
                surface.Canvas.DrawRect(0, 0, 320, 320, paint);
                using var data = surface.Snapshot().Encode(SKEncodedImageFormat.Jpeg, 85);
                File.WriteAllBytes(AppPaths.TrackCover("tour" + i), data.ToArray());
            }
        });
        for (int i = 0; i < 400; i++)
            M.Library.Add(new Track
            {
                Id = "tour" + i,
                Title = $"{Words[i % Words.Length]} {Things[i * 7 % Things.Length]}",
                Artist = Artists[i * 5 % Artists.Length],
                Album = $"{Words[i / 12 % Words.Length]} Sessions",
                Duration = 90,
                SourceUrl = "https://example.com/tour" + i,
                Path = wav,
                HasCover = true,
            });
        for (int p = 0; p < 8; p++)
        {
            var list = M.Profile.CreatePlaylist($"{Words[p]} mix");
            for (int i = 0; i < 30; i++) M.Profile.AddTrack(list, "tour" + (p * 40 + i));
        }
        await Task.Delay(1500);
    }

    private static void WriteMusic(string file, int seconds)
    {
        const int rate = 44100;
        int frames = rate * seconds;
        using var w = new BinaryWriter(new BufferedStream(File.Create(file), 1 << 20));
        w.Write("RIFF"u8); w.Write(36 + frames * 4); w.Write("WAVE"u8); w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16); w.Write("data"u8); w.Write(frames * 4);
        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / rate, beat = t * 2 % 1;
            short s = (short)(Math.Clamp(0.5 * Math.Exp(-beat * 9) * Math.Sin(2 * Math.PI * 62 * t) + 0.2 * Math.Sin(2 * Math.PI * 330 * t), -1, 1) * 30000);
            w.Write(s); w.Write(s);
        }
    }
}
#endif
