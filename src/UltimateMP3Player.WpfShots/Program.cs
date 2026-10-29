using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltimateMP3Player;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;
using UltimateMP3Player.Views;

// The Windows app's window off the screens (never activated, not in the taskbar, its tray icon hidden), its pages drawn into
// pictures: what the Avalonia headless check does for the other desktops. A throwaway data folder; the online search off.
public static class Program
{
    private static readonly List<string> Failed = new();
    private static string _shots = "";

    [STAThread]
    public static int Main(string[] args)
    {
        // (the window's "app.ico" and the other relative resources are the app's, not this exe's)
        // (WPF has already taken this exe for it: set by hand)
        typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, typeof(App).Assembly);
        // ("online": also the sites' songs under yours, to look at; it reaches out)
        bool online = args.Contains("online");
        var root = Path.GetFullPath(args.FirstOrDefault(a => a != "online") ?? Path.Combine(Path.GetTempPath(), "ump-wpf"));
        if (Directory.Exists(root)) Directory.Delete(root, true);
        _shots = Directory.CreateDirectory(Path.Combine(root, "shots")).FullName;
        Environment.SetEnvironmentVariable("UMP_DATA", Path.Combine(root, "data"));

        var first = AppSettings.Load();
        first.Language = "it";
        first.TermsAccepted = Dialogs.TermsVersion;
        first.DiscordPresence = false;
        first.AutoUpdateApp = first.AutoUpdateEngines = first.ShowWhatsNew = false;
        first.OnlineSearch = false;
        first.MusicDir = Path.Combine(root, "music");
        first.Save();

        var app = new App();
        app.InitializeComponent();
        // (what Application.Run would give: the awaits come back here, as in the app, not on other threads)
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
        {
            Check(false, "no unhandled exception: " + e.Exception);
            e.Handled = true;
        };

        // What App.OnStartup does, without the single instance, the terms and the profile picker; the window placed by hand.
        var host = new AppHost();
        typeof(App).GetProperty(nameof(App.Host))!.SetValue(null, host);
        HideTray(host);
        host.OpenProfile(host.ChooseStartupProfile(null)!);
        var main = host.Session!;
        var win = new MainWindow(host)
        {
            DataContext = main, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000, Width = 1400, Height = 900,
            ShowActivated = false, ShowInTaskbar = false,
        };
        typeof(AppHost).GetProperty(nameof(AppHost.Window))!.SetValue(host, win);
        win.Show();
        main.Player.SetVisible(true);

        foreach (var (name, section, go) in new (string, string, Action)[]
        {
            ("home", "home", () => main.IsHome = true),
            ("library", "library", () => main.IsLibrary = true),
            ("unsorted", "unsorted", () => main.IsUnsorted = true),
            ("playlists", "playlists", () => main.LibraryHub.PlaylistsCommand.Execute(null)),
            ("artists", "artists", main.OpenArtists),
            ("radio", "radio", main.OpenStations),
            ("together", "together", () => main.IsTogether = true),
            ("downloads", "downloads", () => main.IsDownloads = true),
            ("stats", "settings", main.OpenStats),
            ("settings", "settings", main.GoSettings),
        })
        {
            go();
            Shot(win, name);
            Check(main.Section == section, $"page {name} opens");
        }
        // ---- Settings opened again: the window isn't held up (Windows listing its audio outputs took 0.2 s of it, every time)
        main.IsHome = true;
        Pump(400);
        var busy = System.Diagnostics.Stopwatch.StartNew();
        main.GoSettings();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Check(busy.ElapsedMilliseconds < 150, $"settings: opened again in {busy.ElapsedMilliseconds} ms");
        Pump(400);
        Check(main.Settings.OutputDevices.Count > UltimateMP3Player.Audio.AudioPlatform.OutputDevices().Count, "settings: the audio outputs listed meanwhile");
        // ---- the library's parts are tabs of one page; back and forward; the keyboard's pages
        main.OpenArtists();
        Pump(600);
        Check(main.Shown == main.LibraryHub && main.LibraryHub.IsArtists && main.IsLibraryNav, "library: Artists is a tab of the library");
        main.IsHome = true;
        main.BackCommand.Execute(null);
        Check(main.Page is ArtistsViewModel && main.ForwardCommand.CanExecute(null), "back: the page before, forward possible");
        main.ForwardCommand.Execute(null);
        Check(main.Page is HomeViewModel && !main.ForwardCommand.CanExecute(null), "forward: the page left with back");
        main.StepMenu(1);
        Check(main.IsSearch, "Ctrl+down: from Home to Search");
        main.GoMenu(2);
        main.StepTab(1);
        Check(main.IsLibraryNav && main.LibraryHub.IsRadio, "Ctrl+3 then Ctrl+right: the library's next tab");
        main.IsDownloads = true;
        Check(main.Shown == main.Search && main.IsSearch, "downloads: shown inside Search");

        main.IsHome = true;
        Shot(win, "home-empty");
        Check(Shown(win, L.T("Import from Spotify")) && Shown(win, L.T("Import from Last.fm")), "home: an empty library suggests Spotify and Last.fm");

        // ---- Search: an entry of the sidebar's menu, its box at the top of its page; one list under chips
        main.IsSearch = true;
        Shot(win, "search-empty");
        Check(main.Page == main.Search && Shown(win, L.T("Paste a link in the box above")) && Descendants(win).OfType<TextBox>().Any(t => t.Name == "SearchBox" && t.IsVisible),
            "search: the menu's Search opens its page, its box at the top, the downloads under it");
        // (a few seconds of silence saved on the device: it can play in the player bar)
        var wav = Path.Combine(root, "music", "duet.wav");
        WriteSilence(wav, 3);
        var duet = new Track { Id = "wpf-duet", Title = "Duet", Artist = "Headless Band feat. Someone", Duration = 3, SourceUrl = "https://example.com/duet", Path = wav };
        main.Library.Add(duet);
        Pump();
        main.SearchText = "headless";
        main.SubmitSearchCommand.Execute(null);
        Shot(win, "search");
        Check(main.Page == main.Search && main.Search.Items.OfType<TrackRow>().Any(r => r.Track.Title == "Duet") && Shown(win, "Duet"),
            "search: the page's box, your song under Songs");
        main.Search.Filter = SearchViewModel.ArtistsKey;
        Shot(win, "search-artists");
        Check(Shown(win, "Headless Band"), "search: the Artists chip, your artist's card");
        main.SearchText = "";
        Check(main.Page == main.Search && !main.Search.HasQuery, "search: the box emptied, the page stays");

        // ---- the artist's page (its site, if the network answers: Popular)
        main.OpenArtist("Headless Band", null);
        Shot(win, "artist", 1500);
        Check(main.Page is ArtistPageViewModel { HasSongs: true } && Shown(win, "Headless Band"), "artist: its page");
        main.BackCommand.Execute(null);

        // ---- a song in the player bar: up next popped out, the playback panel
        main.Player.PlayFrom(main.LibraryPage, main.AllVms().First(t => t.Id == "wpf-duet"));
        main.Player.PlayPauseCommand.Execute(null);
        main.IsHome = true;
        main.QueueOpen = true;
        Shot(win, "queue", 600);
        Check(Descendants(win).OfType<QueuePopup>().Any(q => q.IsVisible), "up next pops out over the page");
        main.QueueOpen = false;
        main.Player.Speed = 1.25;
        var panel = (System.Windows.Controls.Primitives.Popup)win.FindName("PlaybackPopup");
        panel.IsOpen = true;
        Pump(400);
        if (panel.Child is FrameworkElement body) ShotOf(body, "playback");
        Check(Shown(body: panel.Child, L.T("Playback speed")) && Shown(body: panel.Child, L.T("Equalizer")), "playback panel: speed and equalizer");
        panel.IsOpen = false;
        main.Player.Speed = 1;
        Shot(win, "playing");

        // ---- a page left and opened again keeps its view (the DJ's took several frames to build again)
        main.IsDj = true;
        Shot(win, "dj", 600);
        var bar = (FrameworkElement)win.FindName("PlayerBar");
        Check(bar.Visibility == Visibility.Collapsed, "dj: the player bar went away");
        var djView = Descendants(win).OfType<DjView>().FirstOrDefault();
        main.IsTogether = true;
        Pump(600);
        main.IsDj = true;
        Pump(600);
        Check(djView != null && Descendants(win).OfType<DjView>().SingleOrDefault() == djView, "a page opened again keeps its view");
        main.IsHome = true;
        Shot(win, "bar-rising", 90);
        Shot(win, "bar-back", 600);
        Check(bar.Visibility == Visibility.Visible && bar.Margin.Bottom == 0 && bar.IsHitTestVisible, "dj left: the player bar back in its place");

        if (online)
        {
            host.Settings.OnlineSearch = true;
            main.Search.OnSettingsChanged();
            foreach (var title in new[] { "Bad Boys", "Bloodcode" })
                main.Library.Add(new Track { Id = "wpf-" + title, Title = title, Artist = "HOLY PRIEST", Album = "FXCK GENRES", Duration = 160, SourceUrl = "https://example.com/" + title });
            main.SearchText = "holy priest";
            main.SubmitSearchCommand.Execute(null);
            Shot(win, "search-online-busy", 120);
            Shot(win, "search-online", 15000);
            Console.WriteLine($"  online: busy {main.Search.OnlineBusy}, {main.Search.OnlineStatus}; " + string.Join(", ", main.Search.Items.GroupBy(i => i.GetType().Name).Select(g => g.Key + " " + g.Count())));
            main.Search.Filter = SearchViewModel.SongsKey;
            Shot(win, "search-online-songs");
            main.Search.Filter = SearchViewModel.AlbumsKey;
            Shot(win, "search-online-albums");
            Check(!Descendants(win).OfType<TextBlock>().Any(t => t.Text == L.T("Album") && t.IsVisible), "search: albums as cards, not the big card squeezed");
            main.Search.Filter = SearchViewModel.PlaylistsKey;
            Shot(win, "search-online-playlists");
            Console.WriteLine("  songs: " + string.Join(", ", main.Search.Items.GroupBy(i => i.GetType().Name).Select(g => g.Key + " " + g.Count())));
        }

        try { host.Exit(true); } catch { }
        Check(!File.Exists(AppPaths.LogFile) || new FileInfo(AppPaths.LogFile).Length == 0, "nothing in errors.log");
        Console.WriteLine(Failed.Count == 0 ? "wpf: ok" : $"wpf: {Failed.Count} FAILED");
        return Failed.Count == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what);
        if (!ok) Failed.Add(what);
    }

    // (the icon in the notification area would show while the check runs)
    private static void HideTray(AppHost host)
    {
        var icon = host.Tray.GetType().GetField("_icon", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(host.Tray);
        icon?.GetType().GetProperty("Visible")?.SetValue(icon, false);
    }

    // Lets the queued work, the layout and the bindings happen.
    private static void Pump(int ms = 250)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        do
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        } while (DateTime.UtcNow < until);
    }

    private static void Shot(Window w, string name, int ms = 600)
    {
        Pump(ms);
        // (off the screens WPF doesn't always lay the window out again by itself)
        w.UpdateLayout();
        if (w.Content is not FrameworkElement root || root.ActualWidth < 1) return;
        var rtb = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(rtb));
        using var f = File.Create(Path.Combine(_shots, name + ".png"));
        png.Save(f);
    }

    private static void WriteSilence(string file, int seconds)
    {
        const int rate = 44100;
        int bytes = rate * 4 * seconds;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var w = new BinaryWriter(File.Create(file));
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8); w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16); w.Write("data"u8); w.Write(bytes); w.Write(new byte[bytes]);
    }

    private static void ShotOf(FrameworkElement e, string name)
    {
        e.UpdateLayout();
        if (e.ActualWidth < 1) return;
        var rtb = new RenderTargetBitmap((int)e.ActualWidth, (int)e.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(e);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(rtb));
        using var f = File.Create(Path.Combine(_shots, name + ".png"));
        png.Save(f);
    }

    private static bool Shown(Window w, string text) => Shown(body: w, text);
    private static bool Shown(DependencyObject body, string text) => Descendants(body).OfType<TextBlock>().Any(t => t.Text == text && t.IsVisible);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject d)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            yield return c;
            foreach (var x in Descendants(c)) yield return x;
        }
    }
}
