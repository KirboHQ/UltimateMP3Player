using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using UltimateMP3Player.Core;
using UltimateMP3Player.Core.Together;
using UltimateMP3Player.ViewModels;
using UltimateMP3Player.Views;
#if TOUR_WPF
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using UiElement = System.Windows.FrameworkElement;
#else
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UiElement = Avalonia.Controls.Control;
#endif

namespace UltimateMP3Player;

// "tour" (Windows, linked into both test programs): what a person does, for real. The window on the screen, on top, a library of a
// thousand songs, the real mouse and keyboard, every page, menu and dialog opened one after the other while a silent song plays.
// For each: how long the window stood still, the frames while it moved, what it costs once at rest (a battery's worth), a picture of
// the screen. In <folder>/tour.txt and <folder>/shots. The music stays at volume 0.
public static class Tour
{
    private static MainViewModel M = null!;
    private static Window W = null!;
    private static string Shots = "";
    private static StreamWriter Log = null!;
    private static Func<Task>? _extra;

    public static void Run(MainViewModel main, Window win, string root, Func<Task>? extra = null)
    {
        M = main;
        W = win;
        _extra = extra;
        Shots = Directory.CreateDirectory(Path.Combine(root, "shots")).FullName;
        Log = new StreamWriter(Path.Combine(root, "tour.txt")) { AutoFlush = true };
        WatchMenusAndWindows();
        var work = Script();
#if TOUR_WPF
        var frame = new DispatcherFrame();
        work.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
        Dispatcher.PushFrame(frame);
#else
        using var done = new CancellationTokenSource();
        work.ContinueWith(_ => done.Cancel());
        Dispatcher.UIThread.MainLoop(done.Token);
#endif
        if (work.IsFaulted) Say("FAILED: " + work.Exception);
        Say("tour: done");
    }

    private static void Say(string line)
    {
        Console.WriteLine(line);
        Log.WriteLine(line);
    }

    private static async Task Script()
    {
        await Seed();
        Place();
        M.Player.Volume = 0;
        M.Player.AutoQueue = false;
        M.Player.PlayFrom(M.LibraryPage, M.AllVms().First());
        await Task.Delay(1500);
        Say($"window on screen, dpi {GetDpiForSystem()}, {Environment.ProcessorCount} threads");
        Say("scene | still ms | fps moving | p95 ms | worst ms | at rest: CPU %, desktop frames/s");

        // (TOUR_ONLY=song: only from one song to the next; light: in the light look)
        if (Environment.GetEnvironmentVariable("TOUR_ONLY") is "song" or "light")
        {
            if (Environment.GetEnvironmentVariable("TOUR_ONLY") == "light")
            {
                M.Profile.Data.Appearance = ThemeColors.Light;
                M.Host.ApplyTheme();
            }
            await Page("song page", () => M.GoNowPlayingCommand.Execute(null));
            await SongToSong();
            return;
        }
        // (room: only a room)
        if (Environment.GetEnvironmentVariable("TOUR_ONLY") == "room")
        {
            await Room(M.AllVms().Take(12).ToList());
            Tally();
            return;
        }
        // (rgb: what the theme RGB costs, against the theme from the song)
        if (Environment.GetEnvironmentVariable("TOUR_ONLY") == "rgb")
        {
            await RgbCost();
            return;
        }
        // (end: only the last part)
        if (Environment.GetEnvironmentVariable("TOUR_ONLY") == "end")
        {
            await Ending();
            return;
        }
        // (menus: only every menu, dropdown and dialog)
        if (Environment.GetEnvironmentVariable("TOUR_ONLY") == "menus")
        {
            await EveryMenu();
            await DjMenus();
            Tally();
            return;
        }

        await Page("home", () => M.IsHome = true);
        await Wheel("home, wheel", 12);
        await Page("library", () => M.IsLibrary = true);
        await Wheel("library, wheel down", 40);
        await Wheel("library, wheel up", -40);
        await HoverRows("library, mouse along the rows");
        await RowMenu("library, a song's menu");
        await Page("not in a playlist", () => M.IsUnsorted = true);
        await Page("playlists", () => M.LibraryHub.PlaylistsCommand.Execute(null));
        await Page("artists", M.OpenArtists);
        await Page("radio", M.OpenStations);
        if (M.Playlists.Count > 0) await Page("a playlist", () => M.OpenPlaylist(M.Playlists[0]));
        await Wheel("a playlist, wheel", 20);
        await Page("an artist", () => M.OpenArtist("Lumen Harbor", null));
        await Page("search", () => M.IsSearch = true);
        await Scene("search, typing", () => M.SearchText = "lu", 500, "search-typing");
        await Scene("search, results", () => M.SearchText = "lumen", 900, "search-results");
        M.SearchText = "";
        await Page("downloads", () => M.IsDownloads = true);
        await Page("together", () => M.IsTogether = true);
        await Page("stats", M.OpenStats);
        await Wheel("stats, wheel", 15);
        await Page("recap", () => M.OpenRecap(DateTime.Now.Year, 0));
        await Page("settings", M.GoSettings);
        await Wheel("settings, wheel down", 60);
        await Wheel("settings, wheel up", -60);

        await Page("song page", () => M.GoNowPlayingCommand.Execute(null));
        await Scene("song page, playing", () => { }, 2000, "song-playing", 2000);
        await Scene("up next folds", () => M.NowPlaying.QueueHidden = true, 500, "song-queue-folded");
        await Scene("up next back", () => M.NowPlaying.QueueHidden = false, 500, "song-queue-back");
        await Scene("full screen", () => M.FullScreen = true, 500, "song-fullscreen");
        await Scene("full screen, up next folds", () => M.NowPlaying.QueueHidden = true, 500, null);
        await Scene("full screen, up next back", () => M.NowPlaying.QueueHidden = false, 500, null);
        await Scene("full screen ends", () => M.FullScreen = false, 500, null);

        await Page("home again", () => M.IsHome = true);
        await Scene("up next pops out", () => M.QueueOpen = true, 450, "queue-popup");
        await Scene("up next goes", () => M.QueueOpen = false, 450, null);

        await Modal("new playlist", () => _ = M.NewPlaylist(null));
        await Modal("rename a playlist", () => { if (M.Playlists.Count > 0) _ = M.RenamePlaylist(M.Playlists[0]); });
        await Modal("export", () => M.ExportPack());
        await Modal("a question", () => _ = Dialogs.ConfirmAsync(L.T("Delete playlist"), "Sure?", L.T("Delete"), true));
        await Modal("edit a song", () => _ = Dialogs.EditTrackAsync(M.AllVms().First().T));

        await EveryMenu();

        if (_extra != null) await _extra();

        await Ending();
    }

    // What the theme RGB costs against the theme from the song, over long enough to mean something.
    private static async Task RgbCost()
    {
        async Task Cpu(string what)
        {
            await Task.Delay(1500);
            var me = Process.GetCurrentProcess();
            me.Refresh();
            var cpu = me.TotalProcessorTime;
            var clock = Stopwatch.StartNew();
            long frames = DesktopFrames();
            uint seen = ScreenPixel();
            await Task.Delay(10000);
            me.Refresh();
            Say($"{what}: {(me.TotalProcessorTime - cpu).TotalMilliseconds / clock.Elapsed.TotalSeconds / 10:0.0}% of a core, {(DesktopFrames() - frames) / clock.Elapsed.TotalSeconds:0} desktop frames/s, on the screen {seen:X6} > {ScreenPixel():X6}");
        }
        M.Player.PlayPauseCommand.Execute(null);
        foreach (var theme in new[] { ThemeColors.DynamicId, ThemeColors.RgbId })
        {
            M.Settings.SelectThemeCommand.Execute(theme);
            M.IsHome = true;
            await Cpu($"home, paused, {theme}");
            M.IsLibrary = true;
            await Cpu($"library, paused, {theme}");
            M.GoSettings();
            await Cpu($"settings, paused, {theme}");
        }
        M.Settings.RgbSpeed = 10;
        M.IsHome = true;
        await Cpu("home, paused, rgb 10x");
        M.GoSettings();
        await Cpu("settings, paused, rgb 10x");
        M.Settings.RgbSpeed = 1;
        M.Player.PlayPauseCommand.Execute(null);
        foreach (var theme in new[] { ThemeColors.DynamicId, ThemeColors.RgbId })
        {
            M.Settings.SelectThemeCommand.Execute(theme);
            if (!M.IsNowPlaying) M.GoNowPlayingCommand.Execute(null);
            await Cpu($"song page, playing, {theme}");
        }
        M.Settings.RgbSpeed = 10;
        await Cpu("song page, playing, rgb 10x");
        M.Settings.RgbSpeed = 1;
        M.Settings.SelectThemeCommand.Execute(ThemeColors.DynamicId);
    }

    // The bars in every shape, one song to the next, the DJ.
    private static async Task Ending()
    {
        // The bars, every shape.
        M.GoNowPlayingCommand.Execute(null);
        await Task.Delay(800);
        foreach (var style in M.Settings.VisualizerStyles)
        {
            M.Settings.VisualizerStyleChoice = style;
            await Task.Delay(500);
            await Scene("bars: " + style.Value, () => { }, 2500, "bars-" + style.Value, 2400);
        }
        M.Settings.VisualizerStyleChoice = M.Settings.VisualizerStyles[0];

        await SongToSong();
        M.IsHome = true;
        await Task.Delay(900);
        M.Player.PlayPauseCommand.Execute(null);
        await Task.Delay(1500);
        await Scene("home, paused", () => { }, 300, null);
        await DjMenus();
        Tally();
    }

    // ------------------------------------------------------------------ every menu, every dropdown, every dialog

    private static ContextMenu? _opened;
    private static readonly List<Window> Dialogs_ = new();
    private static readonly List<string> NotOpened = new();
    private static int _menus, _dropdowns, _dialogs;

    private static void Tally()
    {
        Say($"opened: {_menus} menus, {_dropdowns} dropdowns, {_dialogs} dialogs; not opened as a person would: {NotOpened.Count}");
        foreach (var n in NotOpened) Say("  NOT OPENED: " + n);
    }

    private static async Task EveryMenu()
    {
        var songs = M.AllVms().Take(12).ToList();
        var three = songs.Take(3).ToList();
        // What the menus list: tags on the songs, a followed artist, a radio, songs waiting in the queue.
        M.Profile.CreateTag("Workout", "#E5484D");
        M.Profile.CreateTag("Road trip", "#2EB67D");
        M.Profile.CreateTag("Late night", "#7C5CFF");
        M.Profile.Follow(new FollowedArtist { Name = "Lumen Harbor" });
        M.Profile.SaveStation(new Station { Id = "tour-fm", Name = "Tour FM", Url = "http://127.0.0.1:9/stream", Tags = "rock,indie", Country = "Italia", Codec = "MP3" });
        await Task.Delay(500);
        M.SetTag(songs.Take(6).ToList(), M.Tags[0], true);
        foreach (var s in songs.Skip(1).Take(6)) M.Player.Enqueue(s, true);
        var track = songs[2];
        var list = M.Playlists.First(p => !p.IsFavorites);
        // (on: its presets' dropdown opens)
        M.Settings.Equalizer.Enabled = true;

        // The library's rows and filters.
        await Page("library again", () => M.IsLibrary = true);
        await Dropdowns("library");
        await Context("a song", d => d is TrackRow, () => Menus.ForTrack(track, M.LibraryPage));
        await Menu("several songs", () => Menus.ForSelection(three, M.LibraryPage));
        await Menu("several songs: add to a playlist", () => Menus.AddManyToPlaylist(three));
        await Menu("several songs: tags", () => Menus.TagMenu(three));
        await Menu("library: filter by tag", () => Menus.TagFilterMenu(M.LibraryPage.TagFilter, M));
        await Menu("library: filter by where", () => Menus.StorageFilterMenu(M.LibraryPage.Storage));
        await Menu("player bar: add to a playlist", () => Menus.AddToPlaylistMenu(M.Player.Current ?? track));

        // Playlists and tags, in the sidebar and on their pages.
        await Context("a playlist", d => d is PlaylistViewModel, () => Menus.ForPlaylist(list));
        await Menu("several playlists", () => Menus.ForPlaylists(M.Playlists.Take(3).ToList()));
        await Menu("several playlists: tags", () => Menus.PlaylistsTagMenu(M.Playlists.Take(3).ToList()));
        await Page("a playlist again", () => M.OpenPlaylist(list));
        await Dropdowns("a playlist");
        await Menu("a playlist's tags", () => Menus.PlaylistTagMenu(list));
        await Context("a tag", d => d is TagViewModel, () => Menus.ForTag(M.Tags[0]));
        await Page("a tag's songs", () => M.OpenTag(M.Tags[0]));

        // Artists, radios, suggestions, songs found online.
        await Page("artists again", M.OpenArtists);
        await Context("an artist", d => d is ArtistCardViewModel, () => Menus.ForArtistCard(new ArtistCardViewModel(M, "Lumen Harbor", null, null)));
        await Page("an artist again", () => M.OpenArtist("Lumen Harbor", null));
        await Menu("an artist's page", () => M.Page is ArtistPageViewModel a ? Menus.ForArtist(a) : null);
        await Page("radio again", M.OpenStations);
        await Menu("a radio", () => Menus.ForTrack(M.Vm(new Track { Id = Station.Prefix + "tour-fm", Title = "Tour FM", Artist = "Italia" }), null));
        var suggested = new Track { Id = RadioSong.Prefix + "tour", Title = "Suggested Song", Artist = "Neon Tide", SourceUrl = "https://example.com/suggested" };
        await Menu("a suggested song", () => Menus.ForRadioItem(new RadioItemViewModel(new RadioSong { Id = suggested.Id, Title = suggested.Title, Artist = suggested.Artist, Url = "https://example.com/suggested" }, suggested), M, null));
        var hit = new OnlineHitViewModel(new SearchHit { Service = OnlineSearchServices.Deezer, Title = "Found Online", Artist = "Blue Atlas", Url = "https://example.com/hit" }, M);
        await Menu("a song found online", () => Menus.ForHit(hit));

        // Up next, the playback panel.
        await Page("home with a queue", () => M.IsHome = true);
        await Scene("up next pops out with songs", () => M.QueueOpen = true, 450, "queue-popup-songs");
        await Context("a song in up next", d => d is QueueRow, () => Menus.ForQueue(M.Player.UpNext[0]));
        await Menu("several songs in up next", () => Menus.ForQueueRows(M.Player.UpNext.Take(3).ToList()));
        M.QueueOpen = false;
        await Task.Delay(400);
        await PlaybackPanel();

        // The download card, a song being downloaded.
        await M.Downloads.ImportListAsync("Track URI,Track Name,Artist Name(s),Duration (ms)\nspotify:track:4uLU6hMCjMI75M1A2tKUQC,Never Gonna Give You Up,Rick Astley,213573\n" +
                                          "spotify:track:7ouMYWpwJ422jRcDASZB7P,Heroes,David Bowie,371000\n", "Tour list");
        M.Host.Downloads.Groups.FirstOrDefault()?.OpenCommand.Execute(null);
        await Page("a download card", () => M.IsDownloads = true);
        await Dropdowns("a download card");
        if (M.Downloads.Link is { } link)
        {
            await Menu("download card: tags", () => Menus.TagPicker(link.TagIds, M, link.OnTagsChosen));
            await Menu("download card: playlists", () => Menus.PlaylistPicker(link));
        }
        else NotOpened.Add("the download card");
        var job = new DownloadJobViewModel(new MediaItem { Title = "Glass Avenue" }, new DownloadBatch(M.Profile, null, 1), 1, false, M.Host.Downloads);
        await Menu("a song being downloaded", () => Menus.ForJobs(new[] { job }));

        // Stats, recap, settings: their dropdowns one by one.
        await Page("stats again", M.OpenStats);
        await Dropdowns("stats");
        await Page("recap again", () => M.OpenRecap(DateTime.Now.Year, 0));
        await Dropdowns("recap");
        await Page("settings again", M.GoSettings);
        await Dropdowns("settings");

        await Lyrics();
        await SearchOnline();
        await Room(songs);
        await EveryDialog(songs);
    }

    // Lyrics with times on the song playing, lighting up line by line.
    private static async Task Lyrics()
    {
        var now = M.Player.Current!.T;
        Directory.CreateDirectory(AppPaths.LyricsDir);
        File.WriteAllLines(LyricsStore.PathFor(now.Id, true), Enumerable.Range(0, 60).Select(i => $"[{i / 60:00}:{i % 60 * 3 % 60:00}.00] A line of the song, number {i}, long enough to read"));
        now.Lyrics = LyricsKind.Synced;
        await Page("song page, lyrics", () =>
        {
            M.GoNowPlayingCommand.Execute(null);
            M.ShowLyrics();
        });
        await Scene("song page, lyrics moving", () => { }, 2500, "song-lyrics-moving", 2400);
        M.IsHome = true;
        await Task.Delay(500);
    }

    // The search with the sites' songs under yours, as anyone sees it: Deezer and the others really asked.
    private static async Task SearchOnline()
    {
        M.Host.Settings.OnlineSearch = true;
        M.Search.OnSettingsChanged();
        await Page("search online", () => M.IsSearch = true);
        M.SearchText = "queen";
        await Task.Delay(5000);
        await Scene("search online, results", () => { }, 400, "search-online");
        await Context("a song found online, for real", d => d is OnlineHitViewModel { IsSong: true }, () => null);
        await Wheel("search online, wheel", 12);
        foreach (var filter in M.Search.Filters.ToList())
        {
            await Scene("search online: " + filter.Key, () => M.Search.Filter = filter.Key, 900, "search-online-" + Slug(filter.Key));
            await Wheel("search online: " + filter.Key + ", wheel", 8);
        }
        M.SearchText = "";
        M.Host.Settings.OnlineSearch = false;
        M.Search.OnSettingsChanged();
    }

    // A room for real: this window is the host, three people come in from the same program over the loopback.
    private static async Task Room(List<TrackViewModel> songs)
    {
        var room = M.Together;
        TogetherSession.ListenOn = System.Net.IPAddress.Loopback;
        await Page("together again", () => M.IsTogether = true);
        await Dropdowns("together, the lobby");
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
            var guest = new TogetherSession(new Member { Id = Ids.New(), Name = name, Color = color }, OnUi);
            await guest.JoinAsync("127.0.0.1", host.ListenPort, null);
            guests.Add(guest);
        }
        room.Add(songs.Skip(3).Take(8).ToList());
        await Task.Delay(1200);
        await Page("a room", () => M.IsTogether = true);
        await Wheel("a room, wheel", 6);
        await Dropdowns("a room");
        await Context("a song in the room", d => d is RoomItemViewModel, () => Menus.ForRoomItem(room.Queue[Math.Min(1, room.Queue.Count - 1)], room));
        await Context("a person in the room", d => d is MemberViewModel m && !m.IsMe, () => Menus.ForMembers(room, room.Members.First(m => !m.IsMe)));
        await Menu("in a room: a song of yours", () => Menus.ForTrack(songs[0], M.LibraryPage));
        room.ToggleRandomCommand.Execute(null);
        await Scene("a room: random songs", () => { }, 400, "room-random");
        await Dropdowns("a room, random songs");
        room.ToggleRandomCommand.Execute(null);
        foreach (var g in guests) g.Dispose();
        room.Leave();
        await Task.Delay(600);
        M.Player.PlayFrom(M.LibraryPage, M.AllVms().First());
    }

    private static async Task PlaybackPanel()
    {
#if TOUR_WPF
        var button = W.FindName("PlaybackButton") as UiElement;
        var popup = W.FindName("PlaybackPopup") as System.Windows.Controls.Primitives.Popup;
#else
        var button = W.FindControl<Control>("PlaybackButton");
        var popup = W.FindControl<Avalonia.Controls.Primitives.Popup>("PlaybackPopup");
#endif
        if (button == null || popup == null)
        {
            NotOpened.Add("the playback panel");
            return;
        }
        var (x, y) = CenterOf(button);
        Screen.MoveTo(x, y);
        await Task.Delay(120);
        await Scene("the playback panel", () => Screen.Click(), 450, "playback-panel");
        if (!popup.IsOpen) NotOpened.Add("the playback panel, by a click");
        if (popup.Child is { } inside) await Dropdowns("the playback panel", inside);
        popup.IsOpen = false;
        await Task.Delay(300);
    }

    private static async Task EveryDialog(List<TrackViewModel> songs)
    {
        var settings = M.Host.Settings;
        var list = M.Playlists.First(p => !p.IsFavorites);
        await Modal("a message", () => Dialogs.Alert("A message", "Something a person has to read before going on, in a couple of lines of text at most."));
        await Modal("three answers", () => _ = Dialogs.ChooseAsync("What do you want to do?", "Pick one of these.", "The first", "The second", "The third"));
        await Modal("ticks", () => _ = Dialogs.ChecksAsync("What to import", "Choose what comes in.", new[] { ("Liked songs", true), ("Playlists", true), ("Plays", false) }, "Import"));
        await Modal("a name", () => _ = Dialogs.PromptAsync("A name", "Give it a name", "Something"));
        await Modal("a form", () => _ = Dialogs.FormAsync("Last.fm", "Your own key.", ("Get a key", "https://example.com"), ("Key", ""), ("Secret", "")));
        await Modal("terms, first start", () => Dialogs.Terms(true));
        await Modal("terms", () => Dialogs.Terms(false));
        await Modal("what's new", () => _ = Dialogs.WhatsNewAsync(new[] { new ReleaseNote(new Version(9, 9, 9), "A title", "## What changed\n- One thing\n- Another thing\n"), new ReleaseNote(new Version(9, 9, 8), "An older one", "- Something else\n") }, settings));
        var words = new LyricsResult { Kind = LyricsKind.Plain, Plain = "A line\nAnother line" };
        await Modal("choose lyrics", () => _ = Dialogs.ChooseLyricsAsync("Glass Avenue", 200, new[] { new LyricsChoice("Lumen Harbor", "Glass Avenue", 200, true, "A line\nAnother line", words), new LyricsChoice("Somebody", "Glass Avenue", 260, false, "A line\nAnother line", words) }));
        await Modal("choose the artist", () => _ = Dialogs.ChooseArtistAsync("Lumen Harbor", new[] { new ArtistChoice(new ArtistSite { Id = "1", Name = "Lumen Harbor", Fans = 12345, Songs = 3 }, 3, true), new ArtistChoice(new ArtistSite { Id = "2", Name = "Lumen Harbor", Fans = 12 }, 3, false) }));
        await Modal("delete songs", () => _ = Dialogs.RemoveAsync(new RemovePlan(songs.Take(3).ToList())));
        await Modal("quality", () => _ = Dialogs.QualityAsync(new QualityPlan("Glass Avenue", "Lumen Harbor", "mp3", null, true, false, true)));
        await Modal("choose the video", () => _ = Dialogs.PickVideoAsync(M, songs[0]));
        await Modal("new tag", () => _ = TagDialogs.EditAsync(null, "#7C5CFF"));
        await Modal("edit a tag", () => _ = TagDialogs.EditAsync("Workout", "#E5484D"));
        await Modal("tags on a playlist's songs", () => _ = TagDialogs.ApplyToSongsAsync(list, songs));
        await Modal("export a playlist", () => PackDialogs.Export(M, list));
        var manifest = new PackManifest { Music = false, Author = "Tour" };
        for (int i = 0; i < 12; i++) manifest.Tracks.Add(new PackTrack { Id = "p" + i, Title = "A song " + i, Artist = "An artist", Duration = 200, SourceUrl = "https://example.com/p" + i });
        var pack = PackFile.Remote("Test pack", manifest, 4096, Array.Empty<string>(), (_, _, _, _) => Task.FromResult(false), null);
        await Modal("import a pack", () => ShowOwned(PackDialogs.BuildImport(pack, PackImporter.Analyze(pack, M.Library), M.Library, M.Profile, settings.MusicDir, () => Task.CompletedTask, _ => { }, settings)));
        await Modal("connect phone", () => PhoneDialogs.Show(M));
        await Modal("overlay editor", () =>
        {
            var overlay = OverlaySourcesViewModel.Shared.Add();
            var editor = Dialogs.Frame(L.T("Stream overlay"), Dialogs.OverlayEditor(overlay), Dialogs.OverlayEditorWidth(), Dialogs.Button(L.T("Done"), "PrimaryButton", isCancel: true));
            ShowOwned(editor);
        }, 3000, OverlayTabs);
        await Modal("profiles", () => ProfilePickerWindow.Pick(M.Host, W, false));
    }

    private static async Task DjMenus()
    {
        await Page("dj", () => M.IsDj = true);
        await Dropdowns("dj");
        await Modal("dj: choose a song", () => DjSongPicker.Show(M.Dj, M.Dj.A));
        await Modal("dj: save the recording", () => DjDialogs.SaveRecording(M.Dj, M.Dj.Decks));
    }

    // The overlay's editor: its four tabs clicked, what's in them.
    private static async Task OverlayTabs(Window dialog)
    {
        foreach (var tab in Inside(dialog).OfType<RadioButton>().Where(r => r.GroupName == "overlay-tabs").ToList())
        {
            string name = "overlay editor: " + tab.Content;
            var (x, y) = CenterOf(tab);
            Screen.MoveTo(x, y);
            await Task.Delay(100);
            await Scene(name, () => Screen.Click(), 500, "dialog-" + Slug(name));
            await Dropdowns(name, dialog);
        }
    }

    // A menu by a real right click on something on the screen; only if there's nothing to click on, or the click opens nothing, opened by hand.
    private static async Task Context(string name, Func<object?, bool> wanted, Func<ContextMenu?> otherwise)
    {
        if (Spot(wanted) is not { } spot)
        {
            NotOpened.Add(name + ": nothing on the screen to click on");
            await Menu(name, otherwise);
            return;
        }
        Screen.MoveTo(spot.X, spot.Y);
        await Task.Delay(120);
        await Before("menu-" + Slug(name));
        _opened = null;
        await Scene("menu: " + name, () => Screen.Click(right: true), 420, "menu-" + Slug(name));
        if (_opened == null)
        {
            NotOpened.Add(name + $": the right click at {spot.X},{spot.Y} opened nothing");
            Picture("nothing-" + Slug(name));
            await Menu(name, otherwise);
            return;
        }
        await Walk(name, _opened);
    }

    private static async Task Menu(string name, Func<ContextMenu?> build)
    {
        var (x, y) = Middle();
        Screen.MoveTo(x - 200, y - 200);
        await Task.Delay(100);
        await Before("menu-" + Slug(name));
        ContextMenu? menu = null;
        await Scene("menu: " + name, () =>
        {
            menu = build();
            if (menu != null) Menus.Open(menu, W, false);
        }, 420, "menu-" + Slug(name));
        if (menu == null)
        {
            NotOpened.Add(name + ": no menu");
            return;
        }
        await Walk(name, menu);
    }

    // The mouse along every entry; on one with a submenu it stays until the submenu opens, and goes along that too.
    private static async Task Walk(string name, ContextMenu menu)
    {
        _menus++;
        var entries = menu.Items.OfType<MenuItem>().ToList();
        int subs = entries.Count(e => e.IsEnabled && e.Items.Count > 0);
        if (entries.Count == 0) NotOpened.Add(name + ": an empty menu");
        int ms = entries.Count * 70 + subs * 1150;
        await Scene("menu: " + name + ", the mouse along it", () => _ = Along(name, entries), ms, null, ms);
        CloseMenu(menu);
        Screen.Escape();
        await Task.Delay(260);
        Screen.Escape();
        await Task.Delay(120);
    }

    private static async Task Along(string name, List<MenuItem> entries)
    {
        foreach (var entry in entries)
        {
            if (!IsShown(entry)) continue;
            var (x, y) = CenterOf(entry);
            Screen.MoveTo(x, y);
            await Task.Delay(70);
            if (!entry.IsEnabled || entry.Items.Count == 0) continue;
            await Task.Delay(650);
            if (!SubOpen(entry))
            {
                NotOpened.Add(name + ": a submenu that doesn't open, " + entry.Header + $" at {x},{y}, items {entry.Items.Count}");
                Picture("nothing-" + Slug(name) + "-sub");
                continue;
            }
            _menus++;
            var sub = "menu-" + Slug(name) + "-" + Slug(entry.Header as string ?? "sub");
            _ = Task.Run(() => Picture(sub));
            foreach (var inner in entry.Items.OfType<MenuItem>().Where(IsShown).Take(8))
            {
                var (ix, iy) = CenterOf(inner);
                Screen.MoveTo(ix, iy);
                await Task.Delay(50);
            }
            Screen.MoveTo(x, y);
            await Task.Delay(40);
        }
    }

    // Every dropdown shown in there: scrolled to, clicked for real, the mouse down its entries, Esc.
    private static async Task Dropdowns(string where, object? root = null)
    {
        int i = 0;
        foreach (var combo in Inside(root ?? W).OfType<ComboBox>().Where(c => IsShown(c) && CanUse(c)).ToList())
        {
            i++;
            combo.BringIntoView();
            await Task.Delay(280);
            var (x, y) = CenterOf(combo);
            Screen.MoveTo(x, y);
            await Task.Delay(60);
            await Before("drop-" + Slug(where) + "-" + i);
            string name = $"dropdown: {where} {i} ({combo.SelectedItem})";
            await Scene(name, () =>
            {
                Screen.Click();
                _ = Down(x, y);
            }, 300, "drop-" + Slug(where) + "-" + i);
            if (combo.IsDropDownOpen) _dropdowns++;
            else NotOpened.Add(name);
            Screen.Escape();
            await Task.Delay(220);
            if (combo.IsDropDownOpen) combo.IsDropDownOpen = false;
        }
    }

    private static async Task Down(int x, int y)
    {
        for (int step = 1; step <= 6; step++)
        {
            await Task.Delay(45);
            Screen.MoveTo(x, y + step * 34);
        }
    }

    private static string Slug(string name) => System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    // The screen just before something opens: what opened is what differs.
    private static Task Before(string shot) => Task.Run(() => Picture("before-" + shot));

    // Song to song: the colours of the whole app.
    private static async Task SongToSong()
    {
        for (int i = 1; i <= 4; i++)
        {
            int next = i * 37;
            await Scene("next song " + i, () => M.Player.PlayFrom(M.LibraryPage, M.AllVms().ElementAt(next)), 1400, "next-" + i, 1300);
#if TOUR_WPF
            var brush = (SolidColorBrush)Application.Current.Resources["AccentBrush"];
            Say($"  theme {M.Profile.Data.Theme}, song colours {M.Profile.Data.SongColors}, accent {Themes.Get("Accent")}, the brush {brush.Color} frozen {brush.IsFrozen}");
#else
            Say($"  theme {M.Profile.Data.Theme}, song colours {M.Profile.Data.SongColors}, accent {Themes.Get("Accent")}");
#endif
        }
    }

    // ------------------------------------------------------------------ one thing opened, measured, seen

    private static Task Page(string name, Action go) => Scene(name, go, 500, name.Replace(' ', '-').Replace(",", ""));

    private static async Task Scene(string name, Action go, int ms, string? shot, int moving = 260)
    {
        var times = new List<double>();
        var clock = Stopwatch.StartNew();
        StartTicks(times, clock);
        go();
        double still = clock.Elapsed.TotalMilliseconds;
        long frames = DesktopFrames(), drawn = WaveVisualizer.Drawn;
        await Task.Delay(ms);
        double bars = (WaveVisualizer.Drawn - drawn) / (clock.Elapsed.TotalSeconds - still / 1000);
        double shown = (DesktopFrames() - frames) / (clock.Elapsed.TotalSeconds - still / 1000);
        StopTicks();
        times.RemoveAll(at => at > still + moving);
        var gaps = times.Zip(times.Skip(1), (a, b) => b - a).OrderBy(g => g).ToList();
        double p95 = gaps.Count > 0 ? gaps[(int)(gaps.Count * 0.95)] : 0, worst = gaps.Count > 0 ? gaps[^1] : 0;
        double fps = times.Count > 1 ? (times.Count - 1) * 1000 / (times[^1] - times[0]) : 0;
        await Task.Delay(350);
        var me = Process.GetCurrentProcess();
        var cpu = me.TotalProcessorTime;
        long restFrom = DesktopFrames();
        var rest = Stopwatch.StartNew();
        await Task.Delay(1000);
        me.Refresh();
        double restCpu = (me.TotalProcessorTime - cpu).TotalSeconds / rest.Elapsed.TotalSeconds * 100, restFrames = (DesktopFrames() - restFrom) / rest.Elapsed.TotalSeconds;
        if (shot != null) Picture(shot);
        Say($"{name}{(M.Player.IsPlaying ? "" : " (paused)")} | {still:0} | {fps:0} (desktop {shown:0}, bars {bars:0}) | {p95:0.0} | {worst:0} | {restCpu:0.0}, {restFrames:0}");
    }

    private static async Task Wheel(string name, int notches)
    {
        var over = Middle();
        Screen.MoveTo(over.X, over.Y);
        await Task.Delay(120);
        await Scene(name, () => _ = Spin(notches), Math.Abs(notches) * 32 + 400, null, Math.Abs(notches) * 32 + 200);
    }

    private static async Task Spin(int notches)
    {
        for (int i = 0; i < Math.Abs(notches); i++)
        {
            Screen.Wheel(notches > 0 ? -120 : 120);
            await Task.Delay(28);
        }
    }

    private static async Task HoverRows(string name)
    {
        var rows = Rows();
        if (rows.Count == 0) return;
        await Scene(name, () => _ = Slide(rows), 1200, null, 1100);
    }

    private static async Task Slide(List<(int X, int Y)> rows)
    {
        for (int k = 0; k < 3; k++)
            foreach (var (x, y) in rows)
            {
                Screen.MoveTo(x, y);
                await Task.Delay(30);
            }
    }

    private static async Task RowMenu(string name)
    {
        var rows = Rows();
        if (rows.Count < 3) return;
        var (x, y) = rows[2];
        Screen.MoveTo(x, y);
        await Task.Delay(150);
        await Scene(name, () => Screen.Click(right: true), 500, "song-menu");
        for (int i = 1; i <= 5; i++)
        {
            Screen.MoveTo(x + 40, y + 12 + i * 34);
            await Task.Delay(120);
        }
        Picture("song-menu-hover");
        Screen.Escape();
        await Task.Delay(400);
        Screen.Escape();
        await Task.Delay(200);
    }

    private static async Task Modal(string name, Action open, int ms = 600, Func<Window, Task>? inside = null)
    {
        // (on the next turn of the loop: a modal window stops whoever opens it until it closes)
        Later(open);
        await Scene(name, () => { }, ms, "dialog-" + Slug(name));
        if (Others().LastOrDefault() is { } dialog)
        {
            _dialogs++;
            await Dropdowns(name, dialog);
            if (inside != null) await inside(dialog);
        }
        else NotOpened.Add("dialog: " + name);
        Screen.Escape();
        await Task.Delay(500);
        CloseOthers();
        await Task.Delay(300);
    }

    // ------------------------------------------------------------------ a library to look at

    private static readonly (string Name, uint A, uint B)[] Moods =
    {
        ("red", 0xFFE5173F, 0xFF7A0E22), ("green", 0xFF1DB954, 0xFF0A4F2A), ("blue", 0xFF1E6BFF, 0xFF0B1F66), ("orange", 0xFFFF8A1A, 0xFF8A2E05),
        ("purple", 0xFF8A3DFF, 0xFF2B0F6B), ("teal", 0xFF12C4B0, 0xFF06433D), ("yellow", 0xFFFFD21A, 0xFF8A6A05), ("pink", 0xFFFF4FA3, 0xFF6B0F46),
        ("grey", 0xFFC8C8C8, 0xFF202020), ("sky", 0xFF7CC8FF, 0xFF2467A8), ("wine", 0xFF8E1F4F, 0xFF26060F), ("lime", 0xFFA6E22E, 0xFF3C5A08),
    };

    private static readonly string[] Artists = { "Lumen Harbor", "Neon Tide", "The Quiet Engines", "Marta Vale", "Kilo & the Foxes", "Aurora Fields", "Sette Venti", "Blue Atlas", "Ostara", "Paper Moons", "Giulia Sera", "Delta Nine" };
    private static readonly string[] Words = { "Midnight", "Glass", "Summer", "Static", "Paper", "Electric", "Silver", "Hollow", "Golden", "Velvet", "Distant", "Falling", "Quiet", "Wild", "Northern", "Last" };
    private static readonly string[] Things = { "Avenue", "Hearts", "Signal", "Rain", "Horizon", "Machine", "Letters", "Ocean", "Lights", "Garden", "Motel", "Echoes", "Runner", "Skyline", "Tides", "Parade" };

    private static async Task Seed()
    {
        var music = Path.Combine(Path.GetDirectoryName(AppPaths.TrackCover("x"))!, "..", "tour-music");
        Directory.CreateDirectory(music);
        var wav = Path.Combine(music, "song.wav");
        WriteMusic(wav, 240);
        var covers = Moods.Select((m, i) =>
        {
            var file = Path.Combine(music, $"cover{i}.png");
            WriteCover(file, m.A, m.B);
            return file;
        }).ToArray();
        var clock = Stopwatch.StartNew();
        const int Songs = 1500;
        for (int i = 0; i < Songs; i++)
        {
            string id = "tour" + i;
            File.Copy(covers[i % covers.Length], AppPaths.TrackCover(id), true);
            M.Library.Add(new Track
            {
                Id = id,
                Title = $"{Words[i % Words.Length]} {Things[i * 7 % Things.Length]}",
                Artist = Artists[i * 5 % Artists.Length],
                Album = $"{Words[i / 12 % Words.Length]} Sessions",
                Duration = 150 + i % 150,
                SourceUrl = "https://example.com/" + id,
                Path = wav,
                HasCover = true,
            });
            if (i % 200 == 0) await Task.Delay(1);
        }
        for (int p = 0; p < 12; p++)
        {
            var list = M.Profile.CreatePlaylist($"{Words[p]} mix");
            for (int i = 0; i < 40; i++) M.Profile.AddTrack(list, "tour" + (p * 90 + i));
        }
        foreach (var name in Artists.Take(6)) M.Profile.Log.Add("tour" + Array.IndexOf(Artists, name), 240, true, DateTime.Now);
        await Task.Delay(1500);
        Say($"{Songs} songs in the library in {clock.ElapsedMilliseconds} ms, {M.Playlists.Count} playlists");
    }

    private static void WriteMusic(string file, int seconds)
    {
        const int rate = 44100;
        int frames = rate * seconds;
        using var w = new BinaryWriter(new BufferedStream(File.Create(file), 1 << 20));
        w.Write("RIFF"u8); w.Write(36 + frames * 4); w.Write("WAVE"u8); w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16); w.Write("data"u8); w.Write(frames * 4);
        var random = new Random(7);
        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / rate, beat = t * 2 % 1, chord = Math.Floor(t / 2) % 4;
            double kick = Math.Exp(-beat * 9) * Math.Sin(2 * Math.PI * 62 * t);
            double pad = 0;
            foreach (var note in new[] { 220 + chord * 30, 330 + chord * 45, 660 + chord * 90, 1760 + chord * 120 }) pad += 0.1 * Math.Sin(2 * Math.PI * note * t);
            double hiss = Math.Exp(-((beat + 0.5) % 1) * 14) * (random.NextDouble() - 0.5);
            short s = (short)(Math.Clamp(0.5 * kick + 0.5 * pad + 0.3 * hiss, -1, 1) * 30000);
            w.Write(s); w.Write(s);
        }
    }

    // A diagonal between two colours, a bright circle: enough of a picture to have colours of its own.
    private static void WriteCover(string file, uint a, uint b)
    {
        const int size = 320;
        var px = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double t = (x + y) / (2.0 * size), dx = x - size * 0.62, dy = y - size * 0.38, glow = Math.Clamp(1 - Math.Sqrt(dx * dx + dy * dy) / (size * 0.42), 0, 1) * 0.35;
                int at = (y * size + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    int shift = c * 8;
                    double v = ((a >> shift) & 255) * (1 - t) + ((b >> shift) & 255) * t;
                    px[at + c] = (byte)Math.Clamp(v + (255 - v) * glow, 0, 255);
                }
                px[at + 3] = 255;
            }
        Screen.SavePng(file, px, size, size);
    }

    // ------------------------------------------------------------------ a picture of the screen, real input

    private static void Picture(string name)
    {
        var (w, h) = Screen.Size();
        Screen.Save(Path.Combine(Shots, name + ".png"), 0, 0, w, h);
    }

    private static class Screen
    {
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, int data, nint extra);
        [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nint extra);
        [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
        [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int w, int h);
        [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dest, int x, int y, int w, int h, nint src, int sx, int sy, uint op);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte[] bits, byte[] info, uint usage);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);

        public static (int W, int H) Size() => (GetSystemMetrics(0), GetSystemMetrics(1));
        public static void MoveTo(int x, int y) => SetCursorPos(x, y);
        public static void Wheel(int delta) => mouse_event(0x0800, 0, 0, delta, 0);

        public static void Click(bool right = false)
        {
            mouse_event(right ? 0x0008u : 0x0002u, 0, 0, 0, 0);
            mouse_event(right ? 0x0010u : 0x0004u, 0, 0, 0, 0);
        }

        public static void Escape()
        {
            keybd_event(0x1B, 0, 0, 0);
            keybd_event(0x1B, 0, 2, 0);
        }

        public static void Save(string file, int x, int y, int w, int h)
        {
            nint screen = GetDC(0), dc = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, w, h), old = SelectObject(dc, bitmap);
            BitBlt(dc, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000);
            var info = new byte[40];
            BitConverter.TryWriteBytes(info.AsSpan(0), 40);
            BitConverter.TryWriteBytes(info.AsSpan(4), w);
            BitConverter.TryWriteBytes(info.AsSpan(8), -h);
            BitConverter.TryWriteBytes(info.AsSpan(12), (short)1);
            BitConverter.TryWriteBytes(info.AsSpan(14), (short)32);
            var px = new byte[w * h * 4];
            GetDIBits(dc, bitmap, 0, (uint)h, px, info, 0);
            SelectObject(dc, old);
            DeleteObject(bitmap);
            DeleteDC(dc);
            ReleaseDC(0, screen);
            SavePng(file, px, w, h);
        }

        // BGRA, top row first.
        public static void SavePng(string file, byte[] bgra, int w, int h)
        {
            var raw = new byte[(w * 3 + 1) * h];
            for (int y = 0, o = 0; y < h; y++)
            {
                raw[o++] = 0;
                for (int x = 0, p = y * w * 4; x < w; x++, p += 4)
                {
                    raw[o++] = bgra[p + 2];
                    raw[o++] = bgra[p + 1];
                    raw[o++] = bgra[p];
                }
            }
            using var packed = new MemoryStream();
            using (var z = new ZLibStream(packed, CompressionLevel.Fastest, true)) z.Write(raw);
            using var f = File.Create(file);
            f.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            var head = new byte[13];
            head[0] = (byte)(w >> 24); head[1] = (byte)(w >> 16); head[2] = (byte)(w >> 8); head[3] = (byte)w;
            head[4] = (byte)(h >> 24); head[5] = (byte)(h >> 16); head[6] = (byte)(h >> 8); head[7] = (byte)h;
            head[8] = 8; head[9] = 2;
            Chunk(f, "IHDR", head);
            Chunk(f, "IDAT", packed.ToArray());
            Chunk(f, "IEND", Array.Empty<byte>());
        }

        private static void Chunk(Stream f, string type, byte[] data)
        {
            var name = System.Text.Encoding.ASCII.GetBytes(type);
            void Big(uint v) => f.Write(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
            Big((uint)data.Length);
            f.Write(name);
            f.Write(data);
            uint crc = 0xFFFFFFFF;
            foreach (var part in new[] { name, data })
                foreach (var b in part)
                {
                    crc ^= b;
                    for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
                }
            Big(~crc);
        }
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("dwmapi.dll")] private static extern int DwmGetCompositionTimingInfo(nint window, byte[] info);

    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);

    // A corner of Favorites' tile in the sidebar, as the screen really shows it.
    private static uint ScreenPixel()
    {
        var p = W.PointToScreen(new Point(26, 296));
        nint dc = GetDC(0);
        uint color = GetPixel(dc, (int)p.X, (int)p.Y);
        ReleaseDC(0, dc);
        return color;
    }

    // The frames the desktop's compositor really put together (DWM_TIMING_INFO: packed, 292 bytes, cFrame at 56).
    private static long DesktopFrames()
    {
        var info = new byte[292];
        BitConverter.TryWriteBytes(info, info.Length);
        return DwmGetCompositionTimingInfo(0, info) == 0 ? BitConverter.ToInt64(info, 56) : 0;
    }

    // ------------------------------------------------------------------ the two windows' differences

#if TOUR_WPF
    private static EventHandler? _tick;

    private static void StartTicks(List<double> times, Stopwatch clock)
    {
        TimeSpan last = default;
        _tick = (_, e) =>
        {
            var at = ((RenderingEventArgs)e).RenderingTime;
            if (at == last) return;
            last = at;
            times.Add(clock.Elapsed.TotalMilliseconds);
        };
        CompositionTarget.Rendering += _tick;
    }

    private static void StopTicks() => CompositionTarget.Rendering -= _tick;

    private static void Place()
    {
        W.Left = SystemParameters.WorkArea.Left + 10;
        W.Top = SystemParameters.WorkArea.Top + 10;
        W.Topmost = true;
        W.ShowActivated = true;
        W.Activate();
    }

    private static void Later(Action go) => Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, go);

    private static void ShowOwned(Window dialog)
    {
        dialog.Owner = W;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        dialog.Topmost = true;
        dialog.Show();
    }

    private static void WatchMenusAndWindows() =>
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) => _opened = (ContextMenu)s));

    private static List<Window> Others() => Application.Current.Windows.OfType<Window>().Where(w => w != W && w.IsVisible).ToList();

    private static void CloseOthers()
    {
        foreach (var w in Others()) w.Close();
    }

    private static void CloseMenu(ContextMenu menu) => menu.IsOpen = false;

    private static bool SubOpen(MenuItem m) => m.IsSubmenuOpen;

    private static bool CanUse(UiElement e) => e.IsEnabled;

    private static void OnUi(Action go) => W.Dispatcher.BeginInvoke(go);

    private static bool IsShown(UiElement e) => e.IsVisible && e.ActualWidth > 0 && e.ActualHeight > 0;

    private static (int X, int Y) CenterOf(UiElement e)
    {
        var p = e.PointToScreen(new Point(e.ActualWidth / 2, e.ActualHeight / 2));
        return ((int)p.X, (int)p.Y);
    }

    private static IEnumerable<object> Inside(object root) => root is DependencyObject d ? Below(d) : Array.Empty<object>();

    // Where the first thing of that kind is, inside the window.
    private static (int X, int Y)? Spot(Func<object?, bool> wanted)
    {
        var box = new Rect(W.PointToScreen(new Point(0, 0)), new Size(W.ActualWidth * VisualTreeHelper.GetDpi(W).DpiScaleX, W.ActualHeight * VisualTreeHelper.GetDpi(W).DpiScaleY));
        foreach (var e in Below(W).OfType<UiElement>())
            if (IsShown(e) && e.ActualHeight >= 20 && wanted(e.DataContext) && CenterOf(e) is var at && box.Contains(at.X, at.Y)) return at;
        return null;
    }

    private static IEnumerable<DependencyObject> Below(DependencyObject d)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            yield return c;
            foreach (var x in Below(c)) yield return x;
        }
    }

    private static (int X, int Y) Middle()
    {
        var p = W.PointToScreen(new Point(W.ActualWidth * 0.6, W.ActualHeight * 0.5));
        return ((int)p.X, (int)p.Y);
    }

    private static List<(int X, int Y)> Rows() => Below(W).OfType<ListBoxItem>().Where(r => r.IsVisible && r.ActualHeight > 20).Take(14)
        .Select(r => r.PointToScreen(new Point(r.ActualWidth * 0.4, r.ActualHeight / 2))).Select(p => ((int)p.X, (int)p.Y)).ToList();
#else
    private static bool _ticking;

    private static void StartTicks(List<double> times, Stopwatch clock)
    {
        _ticking = true;
        void Tick(TimeSpan _)
        {
            times.Add(clock.Elapsed.TotalMilliseconds);
            if (_ticking) W.RequestAnimationFrame(Tick);
        }
        W.RequestAnimationFrame(Tick);
    }

    private static void StopTicks() => _ticking = false;

    private static void Place()
    {
        W.Position = new PixelPoint(10, 10);
        W.Topmost = true;
        W.Activate();
    }

    private static void Later(Action go) => Dispatcher.UIThread.Post(go, DispatcherPriority.Background);

    private static void ShowOwned(Window dialog)
    {
        dialog.Topmost = true;
        dialog.Show(W);
    }

    private static void WatchMenusAndWindows()
    {
        ContextMenu.PlacementTargetProperty.Changed.AddClassHandler<ContextMenu, Control?>((m, _) => _opened = m);
        // (without a lifetime the windows are listed nowhere)
        Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) => { if (w != W) Dialogs_.Add(w); });
        Window.WindowClosedEvent.AddClassHandler<Window>((w, _) => Dialogs_.Remove(w));
    }

    private static List<Window> Others() => Dialogs_.ToList();

    private static void CloseOthers()
    {
        foreach (var w in Others()) w.Close();
    }

    private static void CloseMenu(ContextMenu menu) => menu.Close();

    private static bool SubOpen(MenuItem m) => m.IsSubMenuOpen;

    private static bool CanUse(UiElement e) => e.IsEffectivelyEnabled;

    private static void OnUi(Action go) => Dispatcher.UIThread.Post(go);

    private static bool IsShown(UiElement e) => e.IsEffectivelyVisible && e.Bounds.Width > 0 && e.Bounds.Height > 0;

    private static (int X, int Y) CenterOf(UiElement e)
    {
        var p = e.PointToScreen(new Point(e.Bounds.Width / 2, e.Bounds.Height / 2));
        return (p.X, p.Y);
    }

    private static IEnumerable<object> Inside(object root) => root is Visual v ? v.GetVisualDescendants() : Array.Empty<object>();

    // Where the first thing of that kind is, inside the window.
    private static (int X, int Y)? Spot(Func<object?, bool> wanted)
    {
        foreach (var e in W.GetVisualDescendants().OfType<UiElement>())
        {
            if (!IsShown(e) || e.Bounds.Height < 20 || !wanted(e.DataContext)) continue;
            var at = CenterOf(e);
            var corner = W.PointToScreen(new Point(0, 0));
            if (at.X >= corner.X && at.Y >= corner.Y && at.X < corner.X + W.Bounds.Width * W.RenderScaling && at.Y < corner.Y + W.Bounds.Height * W.RenderScaling) return at;
        }
        return null;
    }

    private static (int X, int Y) Middle()
    {
        var p = W.PointToScreen(new Point(W.Bounds.Width * 0.6, W.Bounds.Height * 0.5));
        return (p.X, p.Y);
    }

    private static List<(int X, int Y)> Rows() => W.GetVisualDescendants().OfType<ListBoxItem>().Where(r => r.IsEffectivelyVisible && r.Bounds.Height > 20).Take(14)
        .Select(r => r.PointToScreen(new Point(r.Bounds.Width * 0.4, r.Bounds.Height / 2))).Select(p => (p.X, p.Y)).ToList();
#endif
}
