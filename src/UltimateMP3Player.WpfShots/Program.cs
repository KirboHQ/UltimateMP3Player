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
            ("downloads", "downloads", () => main.IsDownloads = true),
            ("stats", "settings", main.OpenStats),
            ("settings", "settings", main.GoSettings),
        })
        {
            go();
            Shot(win, name);
            Check(main.Section == section, $"page {name} opens");
        }
        main.IsHome = true;
        Shot(win, "home-empty");
        Check(Shown(win, L.T("Import from Spotify")) && Shown(win, L.T("Import from Last.fm")), "home: an empty library suggests Spotify and Last.fm");

        // ---- Search: an entry of the sidebar's menu, its box at the top of its page; one list under chips
        main.IsSearch = true;
        Shot(win, "search-empty");
        Check(main.Page == main.Search && Shown(win, L.T("Search your music and online")) && Descendants(win).OfType<TextBox>().Any(t => t.Name == "SearchBox" && t.IsVisible),
            "search: the menu's Search opens its page, its box at the top");
        var duet = new Track { Id = "wpf-duet", Title = "Duet", Artist = "Headless Band feat. Someone", Duration = 200, SourceUrl = "https://example.com/duet" };
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

    private static void Shot(Window w, string name, int ms = 250)
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

    private static bool Shown(Window w, string text) => Descendants(w).OfType<TextBlock>().Any(t => t.Text == text && t.IsVisible);

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
