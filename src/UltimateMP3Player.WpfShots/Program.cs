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
        // ("perf": the real window on the screen, on top for half a minute, frames counted while a silent song plays)
        bool perf = args.Contains("perf");
        // ("editor": the overlay's editor on the screen while a silent song plays, as measured for 4.5.2)
        bool editorPerf = args.Contains("editor");
        // ("queue": the song's page on the screen, "Up next" folding away and back while a silent song plays)
        bool queuePerf = args.Contains("queue");
        // ("tour": the real window, the real mouse, every page and menu: Tour.cs in the other test program)
        bool tour = args.Contains("tour");
        var root = Path.GetFullPath(args.FirstOrDefault(a => a != "online" && a != "perf" && a != "editor" && a != "queue" && a != "tour") ?? Path.Combine(Path.GetTempPath(), "ump-wpf"));
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
        // The App's constructor posts its OnStartup, which ran at the first pump: a second host, its window on the screens, the
        // profile picker with more profiles, this folder taken as a file to open. Skipped, as WPF does when Shutdown came before Run.
        var shuttingDown = typeof(Application).GetField("_isShuttingDown", BindingFlags.NonPublic | BindingFlags.Static)!;
        shuttingDown.SetValue(null, true);
        Pump(50);
        shuttingDown.SetValue(null, false);
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
        if (tour)
        {
            Tour.Run(main, win, root);
            try { host.Exit(true); } catch { }
            return 0;
        }
        if (queuePerf)
        {
            QueuePerf(main, win, root);
            try { host.Exit(true); } catch { }
            return 0;
        }
        if (editorPerf)
        {
            EditorPerf(main, win, root);
            try { host.Exit(true); } catch { }
            return 0;
        }
        if (perf)
        {
            Perf(host, main, win, root);
            try { host.Exit(true); } catch { }
            return 0;
        }

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
        // (a window's loading or something drawn with a frozen pen would freeze it: the app would stay in one colour, with no error to see)
        Check(!((Brush)Application.Current.Resources["AccentBrush"]).IsFrozen, "theme from the song: the accent's brush can still move");
        Themes.FadeTo(ThemeColors.FromSong("200;0.7;230;0.7"));
        Themes.FadeTo(ThemeColors.FromSong(null));
        Pump(400);
        // ---- The sidebar's playlists and artists: ways into the library, not pages of their own
        main.OpenPlaylist(main.Playlists[0]);
        Pump(350);
        Check(main.Shown == main.LibraryHub && main.IsLibraryNav && main.LibraryHub.IsPlaylists, "sidebar: a playlist opens inside the library");
        Shot(win, "playlist-in-library");
        main.OpenArtist("Lumen Harbor", null);
        Pump(350);
        Check(main.Shown == main.LibraryHub && main.IsLibraryNav && main.LibraryHub.IsArtists, "sidebar: an artist opens inside the library");
        Shot(win, "artist-in-library");
        // ---- The theme RGB goes round on the brushes in place, only while the window can be seen
        // (this window is off the screens: it would count as covered)
        Color AccentNow() => ((SolidColorBrush)Application.Current.Resources["AccentBrush"]).Color;
        main.Settings.SelectThemeCommand.Execute(ThemeColors.RgbId);
        main.GoSettings();
        Ui.Hidden = false;
        Pump(400);
        Shot(win, "theme-rgb");
        Ui.Hidden = false;
        var rgbFrom = AccentNow();
        Pump(900);
        Check(AccentNow() != rgbFrom, "theme RGB: the accent goes round");
        main.Settings.RgbSpeed = 10;
        Check(ThemeColors.RgbSpeed == 10 && main.Settings.RgbSpeedText == "10x", "theme RGB: the slider reaches 10x");
        Ui.Hidden = false;
        rgbFrom = AccentNow();
        Pump(150);
        Check(AccentNow() != rgbFrom, "theme RGB: at 10x the accent moves within 150 ms");
        Shot(win, "theme-rgb-10x");
        main.Settings.RgbSpeed = 1;
        Ui.Hidden = false;
        Ui.Hidden = true;
        rgbFrom = AccentNow();
        Pump(600);
        Check(AccentNow() == rgbFrom, "theme RGB: stands still while the window can't be seen");
        Ui.Hidden = false;
        main.Settings.SelectThemeCommand.Execute(ThemeColors.CustomId);
        var customFrom = AccentNow();
        main.Settings.CustomColorText = "#12AB9F";
        Pump(150);
        Check(AccentNow() != customFrom && main.Settings.CustomHue > 170 && main.Settings.CustomHue < 180, "custom colour: typed, the accent takes it at once");
        customFrom = AccentNow();
        main.Settings.CustomHue = 30;
        Pump(150);
        Check(AccentNow() != customFrom && main.Settings.CustomColorText != "#12AB9F", "custom colour: the slider moves it, the box follows");
        Shot(win, "theme-custom");
        main.Settings.SelectThemeCommand.Execute(ThemeColors.DynamicId);
        Pump(900);
        rgbFrom = AccentNow();
        Pump(500);
        Check(AccentNow() == rgbFrom, "theme RGB: over when another theme is chosen");
        // ---- The song's bar, every shape (the one in Settings: a made-up song)
        foreach (var shape in Enum.GetValues<VisualizerStyle>())
        {
            main.Settings.SeekStyleChoice = main.Settings.VisualizerStyles.First(c => Equals(c.Value, shape));
            Shot(win, "seek-" + shape.ToString().ToLowerInvariant());
            Check(main.Player.SeekShape == shape, $"song's bar: {shape}");
            main.Settings.SeekMirrored = false;
            Shot(win, "seek-flat-" + shape.ToString().ToLowerInvariant());
            Check(!main.Player.SeekMirrored, $"song's bar: {shape}, not mirrored");
            main.Settings.SeekMirrored = true;
        }
        main.Settings.SeekStyleChoice = main.Settings.VisualizerStyles.First(c => Equals(c.Value, VisualizerStyle.Bars));
        main.OpenSettingsGroup("playback");
        Pump(400);
        Check(main.Settings.OutputDevices.Count > UltimateMP3Player.Audio.AudioPlatform.OutputDevices().Count, "settings: the audio outputs listed meanwhile");
        var output = Descendants(win).OfType<ComboBox>().FirstOrDefault(c => System.Windows.Automation.AutomationProperties.GetName(c) == L.T("Audio output"));
        Check(output?.SelectedIndex >= 0 && output.SelectionBoxItem is Choice, $"settings opened again: the audio output still shown ({output?.SelectionBoxItem})");
        main.OpenSettingsGroup("profiles");
        Pump(400);
        var startup = Descendants(win).OfType<ComboBox>().FirstOrDefault(c => System.Windows.Automation.AutomationProperties.GetName(c) == L.T("At startup"));
        Check(startup?.SelectedIndex >= 0 && startup.SelectionBoxItem is Choice, $"settings opened again: the profile at startup still shown ({startup?.SelectionBoxItem})");
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

        // The song above the play button, halfway up the stage, with as much on its left as on its right.
        var windowRoot = (FrameworkElement)win.Content;
        Point Middle(FrameworkElement e) => e.TransformToAncestor(windowRoot).Transform(new Point(e.ActualWidth / 2, e.ActualHeight / 2));
        void SongInTheMiddle(string when)
        {
            win.UpdateLayout();
            Pump(100);
            var songHit = Descendants(win).OfType<FrameworkElement>().First(e => e.Name == "CoverHit");
            var songBars = Descendants(win).OfType<WaveVisualizer>().First();
            var songStage = Descendants(win).OfType<FrameworkElement>().First(e => e.Name == "Stage");
            double barsFrom = songBars.TransformToAncestor(songStage).Transform(new Point()).X;
            Check(barsFrom <= 0 && barsFrom + songBars.ActualWidth >= songStage.ActualWidth, $"song's page, {when}: the bars reach both sides of the stage ({barsFrom:0}, {barsFrom + songBars.ActualWidth:0} of {songStage.ActualWidth:0})");
            var songBack = Descendants(win).OfType<FrameworkElement>().First(e => e.Name == "StageBack");
            double MiddleOf(FrameworkElement e) => e.TransformToAncestor(songStage).Transform(new Point(e.ActualWidth / 2, 0)).X;
            Check(Math.Abs(MiddleOf(songBack) - MiddleOf(songHit)) <= 1.5, $"song's page, {when}: the backdrop in the middle of the song ({MiddleOf(songBack):0} and {MiddleOf(songHit):0})");
            var playerGrid = (FrameworkElement)win.FindName("PlayerGrid");
            var songAt = Middle(songHit);
            Check(Math.Abs(songAt.X - Middle(playerGrid).X) < 1 && Math.Abs(songAt.Y - Middle(Descendants(win).OfType<FrameworkElement>().First(e => e.Name == "Stage")).Y) < 1, $"song's page, {when}: the cover above play, halfway up the stage ({songAt.X:0},{songAt.Y:0} of {windowRoot.ActualWidth:0}x{windowRoot.ActualHeight:0})");
        }

        // ---- the song's page over the whole screen (the window hidden meanwhile: full screen puts it on a real one)
        main.GoNowPlayingCommand.Execute(null);
        win.Hide();
        main.FullScreen = true;
        Sound();
        Shot(win, "fullscreen");
        var titleBar = (FrameworkElement)win.FindName("TitleBar");
        var playerBar = (Border)win.FindName("PlayerBar");
        Check(main.NowPlaying.FullScreen && titleBar.Visibility == Visibility.Collapsed && playerBar.Background is LinearGradientBrush && win.Left > -30000,
              "full screen: the window over its screen, no title bar, the player bar see-through");
        main.IsHome = true;
        Pump(300);
        Check(!main.FullScreen && titleBar.Visibility == Visibility.Visible && win.Left == -32000 && win.Width == 1400, "full screen: another page ends it, the window back");
        win.Show();

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

        // ---- 4.5: the speed of the animations; the interface's size; the sidebar's sections; the bars of the music; the stream overlays
        main.Settings.AnimationSpeed = 2;
        Check(Glide.Speed == 2 && Ui.Time(100).TotalMilliseconds == 50, "animations: 2x reaches the glide and the transitions");
        main.Settings.AnimationSpeed = 1;
        main.SetZoom(0.9, false);
        Shot(win, "zoom-90");
        Check(Math.Abs(Ui.ZoomTransform.ScaleX - 0.9) < 0.01 && Math.Abs(host.Settings.UiZoom - 0.9) < 0.01, "zoom: the whole window, saved");
        main.SetZoom(1.3, false);
        Shot(win, "zoom-130");
        main.QueueOpen = true;
        Shot(win, "zoom-130-queue", 600);
        main.QueueOpen = false;
        main.SetZoom(1, false);
        foreach (var side in new[] { main.PlaylistsSection, main.ArtistsSection, main.RadiosSection }) side.ToggleCommand.Execute(null);
        Shot(win, "sidebar-folded");
        Check(host.Settings.PlaylistsFolded && host.Settings.ArtistsFolded && host.Settings.RadiosFolded && !main.PlaylistsSection.Open, "sidebar: playlists, artists and radios fold, and stay so");
        foreach (var side in new[] { main.PlaylistsSection, main.ArtistsSection, main.RadiosSection }) side.ToggleCommand.Execute(null);
        Shot(win, "sidebar-open");

        main.GoNowPlayingCommand.Execute(null);
        Shot(win, "song-page");
        SongInTheMiddle("with up next");
        var queuePane = Descendants(win).OfType<FrameworkElement>().First(e => e.Name == "QueuePane");
        var leftBar = (FrameworkElement)win.FindName("Sidebar");
        var stageView = Descendants(win).OfType<FrameworkElement>().First(e => e.Name == "Stage");
        Check(Math.Abs(queuePane.ActualWidth - stageView.TransformToAncestor(windowRoot).Transform(new Point()).X) < 1, $"song's page: as much room on each side of the stage ({leftBar.ActualWidth:0} and {queuePane.ActualWidth:0})");
        var queueCard = Descendants(win).OfType<FrameworkElement>().First(e => e.Name == "QueueCard");
        var barEntries = (FrameworkElement)((Border)leftBar).Child;
        double FromLeft(FrameworkElement e) => e.TransformToAncestor(windowRoot).Transform(new Point()).X;
        double[] sideGaps = { FromLeft(barEntries), FromLeft(stageView) - FromLeft(barEntries) - barEntries.ActualWidth, FromLeft(queueCard) - FromLeft(stageView) - stageView.ActualWidth, windowRoot.ActualWidth - FromLeft(queueCard) - queueCard.ActualWidth };
        Check(sideGaps.All(g => Math.Abs(g - 14) < 1) && Math.Abs(barEntries.ActualWidth - queueCard.ActualWidth) < 1, $"song's page: the same gaps, the sidebar's entries as wide as up next ({string.Join(", ", sideGaps.Select(g => g.ToString("0")))}; {barEntries.ActualWidth:0} and {queueCard.ActualWidth:0})");
        main.NowPlaying.ToggleQueueCommand.Execute(null);
        Pump(Ui.Animations ? 700 : 100);
        SongInTheMiddle("with up next folded away");
        main.NowPlaying.ToggleQueueCommand.Execute(null);
        Pump(Ui.Animations ? 700 : 100);
        var bars = Descendants(win).OfType<WaveVisualizer>().FirstOrDefault(v => v.IsVisible);
        Check(bars != null && main.NowPlaying.ShowVisualizer, "bars of the music: behind the song's title");
        if (bars != null)
        {
            bars.IsPlaying = true;
            long drawn = WaveVisualizer.Drawn;
            int baked = TextShadow.Baked;
            Sound();
            Shot(win, "bars-center", 50);
            Check(WaveVisualizer.Drawn > drawn + 10, "bars of the music: drawn at the frames of the screen");
            Check(baked > 0 && TextShadow.Baked == baked, "song's title: its shadow is ready, and not made again while the bars move");
            // A long title scrolls under the mouse: meanwhile it's out of the shadow's picture (it would smear beside the letters going by).
            var block = Descendants(win).OfType<StackPanel>().First(s => s.Name == "TitleBlock" && s.IsVisible);
            var title = block.Children.OfType<TextBlock>().First();
            title.SetCurrentValue(TextBlock.TextProperty, string.Concat(Enumerable.Repeat("A very long title ", 12)));
            Pump(300);
            int still = TextShadow.Baked;
            block.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
            Pump(300);
            Check(Marquee.IsMoving(title) && TextShadow.Baked > still && title.Opacity == 1, "song's title: scrolling, it leaves the shadow's picture");
            Shot(win, "title-moving", 800);
            still = TextShadow.Baked;
            block.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = System.Windows.Input.Mouse.MouseLeaveEvent });
            Pump(300);
            Check(!Marquee.IsMoving(title) && TextShadow.Baked > still, "song's title: back in the picture when it stops");
            title.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
            main.Settings.VisualizerFlowChoice = main.Settings.VisualizerFlows[0];
            foreach (var shape in main.Settings.VisualizerStyles)
            {
                main.Settings.VisualizerStyleChoice = shape;
                Sound();
                Shot(win, "bars-" + shape.Value, 50);
            }
            Check(bars.BarStyle == VisualizerStyle.Wave && bars.Flow == VisualizerFlow.LeftToRight, "bars of the music: their direction and shape follow the settings");
            Check(!main.Settings.VisualizerHasThickness, "bars of the music: no thickness to choose for a filled wave");
            main.Settings.VisualizerStyleChoice = main.Settings.VisualizerStyles[0];
            Check(main.Settings.VisualizerHasThickness, "bars of the music: a thickness for the bars");
            main.Settings.VisualizerFlowChoice = main.Settings.VisualizerFlows[3];
            foreach (var place in main.Settings.VisualizerPlaces)
            {
                main.Settings.VisualizerPlaceChoice = place;
                foreach (var shape in new[] { 0, 4, 5 })
                {
                    main.Settings.VisualizerStyleChoice = main.Settings.VisualizerStyles[shape];
                    Sound();
                    Shot(win, $"place-{place.Value}-{main.Settings.VisualizerStyleChoice.Value}", 50);
                }
            }
            Check(bars.Place == VisualizerPlace.Center && bars.Middle > 0 && bars.Middle < bars.ActualHeight * 0.6, $"bars of the music: in the center, on the middle of the cover ({bars.Middle:0})");
            main.Settings.VisualizerPlaceChoice = main.Settings.VisualizerPlaces[0];
            main.Settings.VisualizerStyleChoice = main.Settings.VisualizerStyles[0];
            foreach (var thickness in main.Settings.VisualizerThicknesses)
            {
                main.Settings.VisualizerThicknessChoice = thickness;
                Sound();
                Shot(win, "thickness-" + thickness.Value, 50);
            }
            main.Settings.VisualizerThicknessChoice = main.Settings.VisualizerThicknesses[1];
            main.Settings.VisualizerHeight = 1;
            Check(bars.HeightShare == 1, "bars of the music: as high as the whole page");
            main.Settings.VisualizerHeight = 0.38;
            // They don't stop at once: they fade, and only then do the frames end.
            Sound();
            long playing = WaveVisualizer.Drawn;
            bars.IsPlaying = false;
            Pump(100);
            long fading = WaveVisualizer.Drawn;
            Pump(900);
            long ended = WaveVisualizer.Drawn;
            Pump(300);
            Check(fading > playing && ended > fading && WaveVisualizer.Drawn == ended, "bars of the music: they fade when the music stops, then nothing is drawn");
        }

        var overlay = OverlaySourcesViewModel.Shared.Add();
        Check(OverlayServer.Shared.Running && host.Settings.OverlayPort == OverlayServer.Shared.Port, "overlay: the first source starts its server");
        using (var http = new System.Net.Http.HttpClient())
            Check(http.GetAsync(OverlayServer.Shared.LinkOf(overlay.Source)).GetAwaiter().GetResult().IsSuccessStatusCode, "overlay: its link answers");
        // (what the app says to a page: the song playing)
        using (var http = new System.Net.Http.HttpClient())
        {
            host.OnTrackChanged();
            var link = OverlayServer.Shared.LinkOf(overlay.Source).Replace("/s/", "/events/");
            using var events = new StreamReader(http.GetStreamAsync(link).GetAwaiter().GetResult());
            var said = "";
            for (int i = 0; i < 8 && !said.Contains("event: state"); i++) said += events.ReadLine() + "\n";
            said += events.ReadLine();
            Check(said.Contains("\"Duet\"") && said.Contains("\"dur\":3"), "overlay: a page is told the song playing");
        }
        main.OpenSettingsGroup("overlay");
        Pump(600);
        Check(Shown(win, L.T("Stream overlay")) && Shown(win, overlay.Name), "overlay: its card in the settings, its row");
        // (5.1: the groups on the left, one open on the right; the search looks in all of them)
        var settingsCards = Descendants(win).OfType<UltimateMP3Player.Views.MasonryPanel>().First(p => p.Name == "Cards");
        Check(settingsCards.Children.OfType<FrameworkElement>().All(c => UltimateMP3Player.Views.SettingsView.GetGroup(c).Length > 0), "settings: every card is in a group");
        foreach (var groupEntry in Descendants(win).OfType<RadioButton>().Where(r => r.GroupName == "SettingsGroups").ToList())
        {
            groupEntry.IsChecked = true;
            Shot(win, "settings-" + groupEntry.Tag, 300);
            var inGroup = settingsCards.Children.OfType<FrameworkElement>().Where(c => c.Visibility == Visibility.Visible).ToList();
            Check(inGroup.Count > 0 && inGroup.All(c => UltimateMP3Player.Views.SettingsView.GetGroup(c) == groupEntry.Tag as string), $"settings group {groupEntry.Tag}: {inGroup.Count} cards");
        }
        var settingsBox = Descendants(win).OfType<TextBox>().First(t => t.Name == "SearchBox" && t.IsVisible);
        settingsBox.Text = L.T("Bars of the music");
        Shot(win, "settings-bars", 500);
        Check(!Descendants(win).OfType<RadioButton>().Any(r => r.GroupName == "SettingsGroups" && r.IsChecked == true), "settings search: no group lit, it looks in all of them");
        var clearX = (Button)settingsBox.Template.FindName("ClearX", settingsBox);
        Check(clearX.IsVisible, "settings search: an X with text in the box");
        clearX.Command.Execute(clearX.CommandParameter);
        Pump(50);
        Check(settingsBox.Text == "" && !clearX.IsVisible, "settings search: the X empties the box and goes away");
        Check(Descendants(win).OfType<RadioButton>().Single(r => r.GroupName == "SettingsGroups" && r.IsChecked == true).Tag as string == "about", "settings search: emptied, back to the group open");
        OverlayPreview.Forced = Environment.GetEnvironmentVariable("UMP_PREVIEW");
        var editor = Dialogs.Frame(L.T("Stream overlay"), Dialogs.OverlayEditor(overlay), Dialogs.OverlayEditorWidth(), Dialogs.Button(L.T("Done"), "PrimaryButton", isCancel: true));
        editor.WindowStartupLocation = WindowStartupLocation.Manual;
        editor.Left = editor.Top = -32000;
        editor.ShowActivated = false;
        editor.ShowInTaskbar = false;
        editor.Show();
        Pump(500);
        Shot(editor, "overlay-editor", 100);
        var pictures = Descendants(editor).OfType<Image>().ToList();
        Check(pictures.Count(i => i.Source is System.Windows.Media.Imaging.BitmapSource { PixelWidth: 240 }) >= 16, "overlay: the looks and covers are real pictures");
        // The overlay itself, moving, over the options.
        if (OverlayPreview.FindBrowser() != null)
        {
            for (int i = 0; i < 60 && OverlayPreview.Frames < 8; i++) Pump(250);
            long frames = OverlayPreview.Frames;
            var appTime = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
            Pump(1500);
            double appShare = (System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - appTime).TotalSeconds / 1.5 * 100;
            Check(frames >= 8 && OverlayPreview.Frames > frames && OverlayPreview.Running == 1, $"overlay: the preview moves inside the editor ({(OverlayPreview.Frames - frames) / 1.5:0} pictures a second, the app {appShare:0} % of a core)");
            Shot(editor, "overlay-live", 100);
            Check(!Descendants(editor).OfType<TextBlock>().Any(t => t.Text.Contains("Firefox")), "overlay: with a browser, no note");
        }
        // Only what makes sense for the look and the cover.
        RadioButton Tile(string text) => Descendants(editor).OfType<RadioButton>().First(r => Shown(body: r, text));
        // (one page at a time: what's on the others isn't drawn yet)
        void Page(string key)
        {
            Descendants(editor).OfType<RadioButton>().First(r => r.GroupName == "overlay-tabs" && (string)r.Content == L.T(key)).IsChecked = true;
            Pump(50);
            Check(Descendants(editor).OfType<ScrollViewer>().Any(v => v.VerticalOffset == 0), $"overlay: the page {key} opens at its top");
        }
        Tile(L.T("Vinyl")).IsChecked = true;
        Pump(50);
        Check(Shown(editor, L.T("The needle on the record")) && Shown(editor, L.T("Light around the cover")), "overlay: the needle with the vinyl");
        Tile(L.T("Square")).IsChecked = true;
        Pump(50);
        Check(!Shown(editor, L.T("The needle on the record")) && Shown(editor, L.T("Light around the cover")), "overlay: no needle without it");
        Tile(L.T("Neon")).IsChecked = true;
        Pump(50);
        Check(!Shown(editor, L.T("Card opacity")) && Shown(editor, L.T("Card|overlay")), "overlay: a look without cards has no opacity, its cover has corners");
        Tile(L.T("Vinyl")).IsChecked = true;
        Pump(50);
        Check(!Shown(editor, L.T("Card|overlay")), "overlay: and a record on it has none");
        Tile(L.T("Pill")).IsChecked = true;
        Tile(L.T("Square")).IsChecked = true;
        Pump(50);
        Check(overlay.Source.Corners == 1 && Shown(editor, L.T("Canvas")), "overlay: a capsule as drawn, and it has a canvas too");
        RadioButton Corner(int i) => Descendants(editor).OfType<RadioButton>().Where(r => r.GroupName == "overlay-corners").ElementAt(i);
        Corner(1).IsChecked = true;
        Check(overlay.Source.Corners == 0.5, "overlay: a capsule's corners in the middle are half of it");
        Corner(2).IsChecked = true;
        Tile(L.T("Boxy")).IsChecked = true;
        Pump(50);
        Check(overlay.Source.Corners == 1 && Corner(1).IsChecked == true, "overlay: from a capsule to another look, as drawn");
        Tile(L.T("Cassette")).IsChecked = true;
        Pump(50);
        Check(Descendants(editor).OfType<RadioButton>().Any(r => r.IsVisible && Shown(body: r, L.T("Canvas"))) && !Shown(editor, L.T("Light around the cover")), "overlay: the cassette has its cover on its label: a canvas fills it, no light");
        Page("Bars");
        Tile(L.T("Small bars")).IsChecked = true;
        Pump(50);
        Check(overlay.Source is { Bars: true, ShowBar: false } && Shown(editor, L.T("Shape of the small bars")), "overlay: small bars alone, in place of the line");
        Tile(L.T("Progress line")).IsChecked = true;
        Pump(50);
        Check(overlay.Source is { Bars: false, ShowBar: true } && !Shown(editor, L.T("Shape of the small bars")), "overlay: the line alone, and no shape of bars to choose");
        Tile(L.T("Small bars")).IsChecked = true;
        Check(!Shown(editor, L.T("Above and below")), "overlay: no place for the big bars while they're off");
        Descendants(editor).OfType<CheckBox>().First(c => Shown(body: c, L.T("Big bars behind the card"))).IsChecked = true;
        Pump(50);
        Check(Shown(editor, L.T("Above and below")), "overlay: their places when they're on");
        Tile(L.T("Through the middle")).IsChecked = true;
        Check(overlay.Source.BarsPlace == VisualizerPlace.Center, "overlay: the big bars through the middle");
        // Each kind of bars with its own shape.
        RadioButton Shape(string group, VisualizerStyle style) => Descendants(editor).OfType<RadioButton>().Where(r => r.GroupName == "overlay-" + group).ElementAt((int)style);
        Shape("shape", VisualizerStyle.Line).IsChecked = true;
        Check(overlay.Source is { Style: VisualizerStyle.Line, InsideStyle: VisualizerStyle.Bars }, "overlay: the big bars change shape, the small ones don't");
        Shape("shape-inside", VisualizerStyle.Dots).IsChecked = true;
        Check(overlay.Source is { Style: VisualizerStyle.Line, InsideStyle: VisualizerStyle.Dots }, "overlay: the small bars change shape, the big ones don't");
        Shape("shape", VisualizerStyle.Bars).IsChecked = true;
        Shape("shape-inside", VisualizerStyle.Bars).IsChecked = true;
        Descendants(editor).OfType<CheckBox>().First(c => Shown(body: c, L.T("Big bars behind the card"))).IsChecked = false;
        Page("Appearance");
        Tile(L.T("Compact")).IsChecked = true;
        Pump(50);
        // The colors: from the cover, going round, or one by a bar of hues.
        Slider HueBar() => Descendants(editor).OfType<Slider>().First(v => System.Windows.Automation.AutomationProperties.GetName(v) == L.T("Hue"));
        Tile(L.T("RGB")).IsChecked = true;
        Pump(50);
        Check(overlay.Source.Rgb && Shown(editor, L.T("RGB speed")) && !HueBar().IsVisible, "overlay: RGB, its speed, no color to pick");
        Tile(L.T("RGB")).BringIntoView();
        Shot(editor, "overlay-rgb", 100);
        Tile(L.T("One color")).IsChecked = true;
        Pump(50);
        HueBar().Value = 200;
        Check(overlay.Source is { Rgb: false, MagicColors: false } && overlay.Source.Tint == ThemeColors.FromHue(200) && !Shown(editor, L.T("RGB speed")), "overlay: one color, from the bar of hues");
        HueBar().BringIntoView(); Shot(editor, "overlay-hue", 100);
        Tile(L.T("From the cover")).IsChecked = true;
        Pump(50);
        Check(overlay.Source.MagicColors && Shown(editor, L.T("For songs without a cover")), "overlay: from the cover, a color for songs without one");
        // Each font in its own face, and only the ones in view made. (The list stays see-through: this window is off the screens, a list isn't.)
        Page("Text");
        var fontCombo = Descendants(editor).OfType<ComboBox>().First(c => c.IsVisible);
        var fontPopup = (System.Windows.Controls.Primitives.Popup)fontCombo.Template.FindName("PART_Popup", fontCombo);
        var fontList = (FrameworkElement)fontPopup.Child;
        fontList.Opacity = 0;
        var opening = System.Diagnostics.Stopwatch.StartNew();
        fontCombo.IsDropDownOpen = true;
        fontCombo.UpdateLayout();
        fontList.UpdateLayout();
        long openMs = opening.ElapsedMilliseconds;
        Pump(300);
        var madeFonts = Enumerable.Range(0, fontCombo.Items.Count).Select(i => fontCombo.ItemContainerGenerator.ContainerFromIndex(i)).OfType<ComboBoxItem>().ToList();
        var inOwnFace = madeFonts.Skip(1).Select(i => Descendants(i).OfType<TextBlock>().FirstOrDefault()).Where(t => t != null).ToList();
        Check(fontCombo.Items.Count > 5 && madeFonts.Count < 40 && inOwnFace.Count > 0 && inOwnFace.All(t => t!.FontFamily.Source == t.Text), $"overlay: {fontCombo.Items.Count} fonts, {madeFonts.Count} made, each in its face (opened in {openMs} ms)");
        fontList.Opacity = 1;
        var fontShot = new RenderTargetBitmap((int)fontList.ActualWidth, (int)fontList.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        fontShot.Render(fontList);
        fontList.Opacity = 0;
        var fontPng = new PngBitmapEncoder();
        fontPng.Frames.Add(BitmapFrame.Create(fontShot));
        using (var fontFile = File.Create(Path.Combine(_shots, "overlay-fonts.png"))) fontPng.Save(fontFile);
        fontCombo.IsDropDownOpen = false;
        fontList.Opacity = 1;
        // Size, place and when: they reach the source, and only the line that counts stays.
        Tile(L.T("Center|place")).IsChecked = true;
        Check(overlay.Source.TextAlign == "center", "overlay: the text in the center");
        Tile(L.T("Left|place")).IsChecked = true;
        Page("On screen");
        Descendants(editor).OfType<Slider>().First(v => v.IsVisible).Value = 0.6;
        // (the buttons of the ways in and out move a little card, and end with it in its place)
        var moveTile = Descendants(editor).OfType<RadioButton>().First(r => r.GroupName == "overlay-reveal" && Shown(body: r, L.T("Slide: left")));
        moveTile.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
        Pump(100);
        var movingCard = Descendants(moveTile).OfType<Border>().First(b => b.RenderTransform is TransformGroup);
        double movingX = ((TranslateTransform)((TransformGroup)movingCard.RenderTransform).Children[2]).X;
        Pump(900);
        Check(movingX < -0.5 && ((TranslateTransform)((TransformGroup)movingCard.RenderTransform).Children[2]).X == 0 && movingCard.Opacity == 1, $"overlay: a way in shown on its button ({movingX:0.0})");
        Descendants(editor).OfType<RadioButton>().Where(r => r.GroupName == "overlay-place").ElementAt(6).IsChecked = true;
        Descendants(editor).OfType<RadioButton>().First(r => r.GroupName == "overlay-when" && Shown(body: r, L.T("At every new song"))).IsChecked = true;
        // (a moment later: not at each step of a slider)
        Pump(900);
        Check(overlay.Source is { Size: 0.6, AnchorX: 0, AnchorY: 1, Show: "change" } && File.ReadAllText(AppPaths.SettingsFile).Contains("\"change\""), "overlay: size, place and when are saved");
        Check(Shown(editor, L.T("It stays for")) && !Shown(editor, L.T("With the music paused, hide it")), "overlay: only the line of the choice shows");
        Descendants(editor).OfType<RadioButton>().First(r => r.GroupName == "overlay-when" && Shown(body: r, L.T("With the music"))).IsChecked = true;
        Pump(50);
        Descendants(editor).OfType<Slider>().First(v => System.Windows.Automation.AutomationProperties.GetName(v) == L.T("With the music paused, hide it")).Value = 1.5;
        Check(overlay.Source.HideAfter == 1.5, "overlay: hidden after less than 3 seconds");
        overlay.Source.Show = "playing";
        overlay.Source.Size = 1;
        Page("Appearance");
        var minimalTile = Descendants(editor).OfType<RadioButton>().First(r => Shown(body: r, L.T("Minimal")));
        minimalTile.IsChecked = true;
        Pump(900);
        Check(overlay.Source.Skin == "minimal" && overlay.Summary.StartsWith(L.T("Minimal")) && File.ReadAllText(AppPaths.SettingsFile).Contains("\"minimal\""), "overlay: a look picked is saved");
        foreach (var (key, part) in new[] { ("Appearance", 1), ("Text", 2), ("Bars", 3), ("On screen", 4) })
        {
            Page(key);
            Shot(editor, "overlay-editor-" + part, 200);
        }
        editor.Close();
        for (int i = 0; i < 20 && OverlayPreview.Running > 0; i++) Pump(150);
        Check(OverlayPreview.Running == 0, "overlay: its browser goes with the editor");
        Check(File.Exists(overlay.PictureFile) && overlay.Picture != null, "overlay: the settings' list shows it as it was left");
        Descendants(win).OfType<TextBlock>().FirstOrDefault(t => t.Text == overlay.Name)?.BringIntoView(new Rect(0, -60, 10, 160));
        Shot(win, "overlay-row", 400);
        // ---- 5.1: signing in to each site (the sites' pages need a person; the browser's part: mp3cli signin)
        var eachSite = main.Settings.BrowserChoices.First();
        Check(Equals(eachSite.Value, AppSettings.EachSite), "sign-ins: each site is the first choice");
        var browserBefore = main.Settings.Browser;
        main.Settings.UseCookies = true;
        main.Settings.Browser = eachSite;
        Check(main.Settings.UsesEachSite && host.Settings.CookiesBrowserOrNull == null, "sign-ins: each site chosen, nothing to use yet");
        var youTube = UltimateMP3Player.Platform.SiteLogins.Sites[0];
        Check(!UltimateMP3Player.Platform.SiteLogins.Keep(new[] { new BrowserCookie(".youtube.com", "/", true, 0, "PREF", "x") }, youTube) && !UltimateMP3Player.Platform.SiteLogins.HasFile, "sign-ins: a window closed before signing in keeps nothing");
        Check(UltimateMP3Player.Platform.SiteLogins.Keep(new[] { new BrowserCookie(".youtube.com", "/", true, DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds(), "SAPISID", "abc"), new BrowserCookie(".ads.example", "/", false, 0, "ad", "1") }, youTube)
            && UltimateMP3Player.Platform.SiteLogins.SignedIn().SetEquals(new[] { "YouTube" }) && !File.ReadAllText(UltimateMP3Player.Platform.SiteLogins.FilePath).Contains("ads.example")
            && host.Settings.CookiesBrowserOrNull == "file:" + UltimateMP3Player.Platform.SiteLogins.FilePath, "sign-ins: only the site's cookies kept, and the downloads get them");
        main.Settings.LoginsChanged();
        main.OpenSettingsGroup("download");
        Pump(400);
        var loginsButton = Descendants(win).OfType<Button>().First(b => b.IsVisible && Equals(b.Content, L.T("Sign in to sites")));
        loginsButton.BringIntoView();
        Shot(win, "settings-signins", 400);
        Check(Descendants(win).OfType<TextBlock>().Any(t => t.IsVisible && t.Text == "YouTube"), "sign-ins: Settings says which sites");
        var logins = Dialogs.LoginsWindow();
        logins.WindowStartupLocation = WindowStartupLocation.Manual;
        logins.Left = logins.Top = -32000;
        logins.ShowActivated = false;
        logins.ShowInTaskbar = false;
        logins.Show();
        Pump(300);
        Shot(logins, "logins", 100);
        Check(Descendants(logins).OfType<TextBlock>().Count(t => t.Text == L.T("Signed in")) == 1 && Descendants(logins).OfType<TextBlock>().Count(t => t.Text == L.T("Not signed in")) == UltimateMP3Player.Platform.SiteLogins.Sites.Length - 1, "sign-ins: one site signed in, the others not");
        UltimateMP3Player.Platform.SiteLogins.SignOut(youTube);
        Pump(100);
        Check(!UltimateMP3Player.Platform.SiteLogins.HasFile && Descendants(logins).OfType<TextBlock>().All(t => t.Text != L.T("Signed in")), "sign-ins: signed out, the list follows");
        logins.Close();
        main.Settings.Browser = browserBefore;
        main.Settings.UseCookies = false;
        // No browser to draw the preview: the editor says so.
        OverlayPreview.Forced = "";
        var stillEditor = Dialogs.Frame(L.T("Stream overlay"), Dialogs.OverlayEditor(overlay), Dialogs.OverlayEditorWidth(), Dialogs.Button(L.T("Done"), "PrimaryButton", isCancel: true));
        stillEditor.WindowStartupLocation = WindowStartupLocation.Manual;
        stillEditor.Left = stillEditor.Top = -32000;
        stillEditor.ShowActivated = false;
        stillEditor.ShowInTaskbar = false;
        stillEditor.Show();
        Pump(500);
        Shot(stillEditor, "overlay-no-browser", 100);
        Check(Descendants(stillEditor).OfType<TextBlock>().Any(t => t.Text.Contains("Firefox")), "overlay: without a browser, a note says why the preview stands still");
        stillEditor.Close();
        OverlayPreview.Forced = null;
        main.IsHome = true;

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

        // ---- 5.1: a long library makes its rows as they're reached; Home keeps to one row of cards
        for (int i = 0; i < 450; i++) main.Library.Add(new Track { Id = "long-" + i, Title = "Long song " + i, Artist = "Long Artist", Duration = 180, SourceUrl = "https://example.com/long" + i });
        main.IsHome = true;
        Shot(win, "home-long", 500);
        var addedShelf = Descendants(win).OfType<UltimateMP3Player.Views.Shelf>().First(s => s.Title == L.T("Recently added"));
        Check(addedShelf.ActualHeight is > 100 and < 420, $"home: Recently added is one row ({addedShelf.ActualHeight:0} high)");
        Descendants(addedShelf).OfType<Button>().First(b => b.Name == "PART_More").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Shot(win, "library-long", 500);
        Check(main.Section == "library", "home: its Show all goes to the library");
        var longList = (ItemsControl)Descendants(win).OfType<UltimateMP3Player.Views.LibraryView>().First().FindName("List");
        var longRows = (PagedRows)longList.ItemsSource;
        Check(longRows.Count == PagedRows.Piece && longRows.HasMore, $"library: {longRows.Count} rows made of {main.Library.Snapshot().Count}");
        Descendants(longList).OfType<ScrollViewer>().First().ScrollToEnd();
        Shot(win, "library-long-end", 500);
        Check(longRows.Count > PagedRows.Piece, $"library: scrolled to the end, {longRows.Count} rows");
        UltimateMP3Player.Views.RowPaging.LoadAll(longList);
        Check(longRows.Count == main.Library.Snapshot().Count && !longRows.HasMore, $"library: Select all makes them all ({longRows.Count})");
        // (one row that scrolls sideways: arrows and a bar from the start, its cards made as they near)
        main.IsHome = true;
        Shot(win, "home-again-long", 500);
        addedShelf = Descendants(win).OfType<UltimateMP3Player.Views.Shelf>().First(s => s.Title == L.T("Recently added"));
        double oneRow = addedShelf.ActualHeight;
        Button ShelfPart(string part) => Descendants(addedShelf).OfType<Button>().First(b => b.Name == part);
        var shelfScroller = Descendants(addedShelf).OfType<ScrollViewer>().First(v => v.Name == "PART_Scroller");
        Check(ShelfPart("PART_Next").IsVisible && ShelfPart("PART_Next").IsEnabled && ShelfPart("PART_Back").IsVisible && !ShelfPart("PART_Back").IsEnabled
            && shelfScroller.ComputedHorizontalScrollBarVisibility == Visibility.Visible, "home: arrows and a bar from the start, none to go back to");
        Check(shelfScroller.ExtentWidth > shelfScroller.ViewportWidth && Math.Abs(shelfScroller.ExtentWidth - (addedShelf.Total * (addedShelf.ItemWidth + UltimateMP3Player.Views.Shelf.Gap) - UltimateMP3Player.Views.Shelf.Gap)) < 1, $"home: the row is as long as its {addedShelf.Total} cards");
        ShelfPart("PART_Next").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        addedShelf.BringIntoView();
        Shot(win, "home-shelf-next", 500);
        Check(shelfScroller.HorizontalOffset > 100 && ShelfPart("PART_Back").IsEnabled, $"home: its arrow moves the row ({shelfScroller.HorizontalOffset:0})");
        shelfScroller.ScrollToHorizontalOffset(shelfScroller.ScrollableWidth);
        Shot(win, "home-shelf-end", 500);
        Check(((ItemsControl)addedShelf.Content).Items.Count == addedShelf.Total && !ShelfPart("PART_Next").IsEnabled && addedShelf.ActualHeight == oneRow, "home: the bar at its end, all the cards made, one row");

        try { host.Exit(true); } catch { }
        Check(!File.Exists(AppPaths.LogFile) || new FileInfo(AppPaths.LogFile).Length == 0, "nothing in errors.log");
        Console.WriteLine(Failed.Count == 0 ? "wpf: ok" : $"wpf: {Failed.Count} FAILED");
        return Failed.Count == 0 ? 0 : 1;
    }

    private static void QueuePerf(MainViewModel main, MainWindow win, string root)
    {
        var wav = Path.Combine(root, "music", "perf.wav");
        WriteMusic(wav, 300);
        var track = new Track { Id = "wpf-perf", Title = "A song with a title", Artist = "An artist", Duration = 300, SourceUrl = "https://example.com/perf", Path = wav, HasCover = true };
        WriteCover(AppPaths.TrackCover(track.Id));
        main.Library.Add(track);
        Pump();
        main.Player.Volume = 0;
        win.Left = SystemParameters.WorkArea.Left + 20;
        win.Top = SystemParameters.WorkArea.Top + 20;
        win.Topmost = true;
        main.Player.PlayFrom(main.LibraryPage, main.AllVms().First(t => t.Id == track.Id));
        main.GoNowPlayingCommand.Execute(null);
        Pump(1500);
        Measure("song page, still", 3000);
        var flip = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        flip.Tick += (_, _) => main.NowPlaying.QueueHidden = !main.NowPlaying.QueueHidden;
        flip.Start();
        Measure("Up next folding and back", 7000);
        flip.Stop();
        Pump(500);
    }

    // Editing an overlay while a song plays: what the app, its garbage collector and the browser of the preview cost, and what's left
    // for the frames of the windows; then the slider dragged, a step every 50 ms.
    private static void EditorPerf(MainViewModel main, MainWindow win, string root)
    {
        var wav = Path.Combine(root, "music", "perf.wav");
        WriteMusic(wav, 300);
        var track = new Track { Id = "wpf-perf", Title = "A song with a title", Artist = "An artist", Duration = 300, SourceUrl = "https://example.com/perf", Path = wav, HasCover = true };
        WriteCover(AppPaths.TrackCover(track.Id));
        main.Library.Add(track);
        Pump();
        main.Player.Volume = 0;
        win.Left = SystemParameters.WorkArea.Left + 20;
        win.Top = SystemParameters.WorkArea.Top + 20;
        win.Topmost = true;
        main.Player.PlayFrom(main.LibraryPage, main.AllVms().First(t => t.Id == track.Id));
        // (not Settings: its preview of the bars, at the top of the page, would be measured with it; scrolled to the overlays it rests)
        main.IsHome = true;
        Pump(1500);
        Console.WriteLine($"  tier {RenderCapability.Tier >> 16}, playing {main.Player.IsPlaying}");
        Measure("no editor", 4000);
        var since = DateTime.Now;
        var overlay = OverlaySourcesViewModel.Shared.Add();
        var built = System.Diagnostics.Stopwatch.StartNew();
        var editor = Dialogs.Frame(L.T("Stream overlay"), Dialogs.OverlayEditor(overlay), Dialogs.OverlayEditorWidth(), Dialogs.Button(L.T("Done"), "PrimaryButton", isCancel: true));
        editor.Owner = win;
        editor.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        editor.ShowActivated = false;
        editor.ShowInTaskbar = false;
        editor.Topmost = true;
        editor.Show();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Console.WriteLine($"  editor opened in {built.ElapsedMilliseconds} ms");
        for (int i = 0; i < 60 && OverlayPreview.Frames < 8; i++) Pump(250);
        Pump(1000);
        void Costs(string what, int ms, Action? meanwhile = null)
        {
            static double Browser(DateTime from) => new[] { "msedge", "chrome", "brave" }.SelectMany(System.Diagnostics.Process.GetProcessesByName).Sum(p =>
            {
                try { return p.StartTime >= from ? p.TotalProcessorTime.TotalSeconds : 0; }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return 0; }
            });
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            long made = GC.GetTotalAllocatedBytes(), pictures = OverlayPreview.Frames;
            double browser = Browser(since);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var late = new List<double>();
            var beat = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
            double last = 0;
            beat.Tick += (_, _) =>
            {
                double now = clock.Elapsed.TotalMilliseconds;
                if (last > 0) late.Add(now - last - 50);
                last = now;
                meanwhile?.Invoke();
            };
            beat.Start();
            // (without counting WPF's ticks: asking for them makes it draw at every frame of the screen, which is what's being measured)
            Measure(what, ms, false);
            beat.Stop();
            double seconds = clock.Elapsed.TotalSeconds;
            late.Sort();
            Console.WriteLine($"    UI late p95 {late[(int)(late.Count * 0.95)]:0} ms, worst {late[^1]:0} ms; GC gen0 {GC.CollectionCount(0) - g0}, gen1 {GC.CollectionCount(1) - g1}, gen2 {GC.CollectionCount(2) - g2}; allocated {(GC.GetTotalAllocatedBytes() - made) / seconds / 1048576:0.0} MB/s; pictures {(OverlayPreview.Frames - pictures) / seconds:0.0}/s; browser {(Browser(since) - browser) / seconds * 100:0}% of a core");
        }
        Costs("editor open", 7000);
        double step = 0.5;
        Costs("slider dragged", 5000, () =>
        {
            step = step >= 1 ? 0.5 : step + 0.05;
            overlay.Source.Solid = step;
            overlay.Changed();
        });
        editor.Close();
        Pump(500);
    }

    // Frames the window really shows, with the bars and without: WPF holds back its Rendering ticks while the render thread is behind.
    private static void Perf(AppHost host, MainViewModel main, MainWindow win, string root)
    {
        var wav = Path.Combine(root, "music", "perf.wav");
        WriteMusic(wav, 300);
        var track = new Track { Id = "wpf-perf", Title = "A song with a title", Artist = "An artist", Duration = 300, SourceUrl = "https://example.com/perf", Path = wav, HasCover = true };
        WriteCover(AppPaths.TrackCover(track.Id));
        main.Library.Add(track);
        Pump();
        main.Player.Volume = 0;
        win.WindowState = WindowState.Normal;
        win.Left = SystemParameters.WorkArea.Left + 20;
        win.Top = SystemParameters.WorkArea.Top + 20;
        win.Topmost = true;
        main.Player.PlayFrom(main.LibraryPage, main.AllVms().First(t => t.Id == track.Id));
        main.GoNowPlayingCommand.Execute(null);
        Pump(1500);
        Console.WriteLine($"  tier {RenderCapability.Tier >> 16}, screen {SystemParameters.PrimaryScreenWidth}x{SystemParameters.PrimaryScreenHeight}, dpi x{VisualTreeHelper.GetDpi(win).DpiScaleX}, window {win.ActualWidth}x{win.ActualHeight}, playing {main.Player.IsPlaying}, animate {main.Player.Animate}");
        Measure("bars on, window");
        // ("quick": only the song's page out of sight and paused)
        if (!Environment.GetCommandLineArgs().Contains("quick"))
        {
        main.Settings.VisualizerThicknessChoice = main.Settings.VisualizerThicknesses[0];
        Pump(600);
        Measure("thin, window", 4000);
        main.Settings.VisualizerThicknessChoice = main.Settings.VisualizerThicknesses[1];
        Pump(600);
        main.Settings.Visualizer = false;
        Pump(800);
        Measure("bars off, window");
        main.Settings.Visualizer = true;
        win.WindowState = WindowState.Maximized;
        Pump(1500);
        Console.WriteLine($"  maximized {win.ActualWidth}x{win.ActualHeight}");
        Measure("bars on, maximized");
        main.Settings.Visualizer = false;
        Pump(800);
        Measure("bars off, maximized");
        main.Settings.Visualizer = true;
        Pump(600);
        // The tallest bars behind everything: what the buttons have to be read over.
        var usualHeight = main.Settings.VisualizerHeight;
        main.Settings.VisualizerHeight = 1;
        main.FullScreen = true;
        Shot(win, "fullscreen-bars", 1500);
        CoverInTheMiddle(win);
        Measure("full screen, the tallest bars");
        main.FullScreen = false;
        main.Settings.VisualizerHeight = usualHeight;
        Pump(800);
        foreach (var place in main.Settings.VisualizerPlaces)
        {
            main.Settings.VisualizerPlaceChoice = place;
            Pump(600);
            Measure(place.Label + ", maximized", 4000);
        }
        main.Settings.VisualizerPlaceChoice = main.Settings.VisualizerPlaces[0];
        foreach (var thickness in main.Settings.VisualizerThicknesses)
        {
            main.Settings.VisualizerThicknessChoice = thickness;
            Pump(600);
            Measure(thickness.Label + ", maximized", 4000);
        }
        foreach (var style in main.Settings.VisualizerStyles)
        {
            main.Settings.VisualizerStyleChoice = style;
            Pump(600);
            Measure(style.Label + ", maximized", 4000);
        }
        main.Settings.VisualizerStyleChoice = main.Settings.VisualizerStyles[0];
        // Thin and tall: the most there is to draw.
        main.Settings.VisualizerThicknessChoice = main.Settings.VisualizerThicknesses[0];
        main.Settings.VisualizerHeight = 0.8;
        Pump(600);
        Measure("thin and tall, maximized");
        main.GoSettings();
        Pump(900);
        var box = Descendants(win).OfType<TextBox>().First(t => t.Name == "SearchBox" && t.IsVisible);
        box.Text = L.T("Bars of the music");
        Pump(900);
        Measure("settings, the preview in sight");
        box.Text = "";
        Pump(500);
        foreach (var scroller in Descendants(win).OfType<ScrollViewer>().Where(s => s.IsVisible && s.ScrollableHeight > 500)) scroller.ScrollToEnd();
        Pump(900);
        Measure("settings, the preview scrolled away");
        main.GoNowPlayingCommand.Execute(null);
        }
        else
        {
            win.WindowState = WindowState.Maximized;
            Pump(1000);
            var usual = main.Settings.VisualizerHeight;
            main.Settings.VisualizerHeight = 1;
            main.FullScreen = true;
            Shot(win, "fullscreen-bars", 1500);
            CoverInTheMiddle(win);
            main.FullScreen = false;
            main.Settings.VisualizerHeight = usual;
        }
        Pump(1500);
        Measure("song's page, before going out of sight");
        // Out of sight: another window over it, then minimized; nothing asks for frames (no tick of the measure either).
        win.Topmost = false;
        var cover = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = SystemParameters.WorkArea.Left, Top = SystemParameters.WorkArea.Top, Width = SystemParameters.WorkArea.Width, Height = SystemParameters.WorkArea.Height };
        cover.Show();
        Pump(2500);
        Check(!main.Player.Animate && main.Player.IsPlaying && Ui.Hidden, "out of sight: covered by another window, the bars rest and the music plays on");
        Measure("covered by another window", 6000, false);
        cover.Close();
        Pump(2500);
        Check(main.Player.Animate && !Ui.Hidden, "out of sight: uncovered, they're back");
        win.WindowState = WindowState.Minimized;
        Pump(1500);
        Measure("minimized, playing", 6000, false);
        main.Player.PlayPauseCommand.Execute(null);
        Pump(1500);
        Measure("minimized, paused", 6000, false);
        win.WindowState = WindowState.Maximized;
        Pump(1500);
        // Nothing moves, so nothing is drawn: an animation left running (even out of sight) means every frame of the screen.
        long framesBefore = DesktopFrames();
        Measure("on screen, paused", 6000, false);
        Check((DesktopFrames() - framesBefore) / 6.0 < 5, "paused, on screen: the window draws nothing");
    }

    private static void CoverInTheMiddle(Window win)
    {
        var hit = Descendants(win).OfType<FrameworkElement>().First(e => e.Name == "CoverHit");
        var root = (FrameworkElement)win.Content;
        var at = hit.TransformToAncestor(root).Transform(new Point(hit.ActualWidth / 2, hit.ActualHeight / 2));
        Check(Math.Abs(at.X - root.ActualWidth / 2) < 1 && Math.Abs(at.Y - root.ActualHeight / 2) < 1, $"full screen, on the screen: the cover in the middle ({at.X:0},{at.Y:0} of {root.ActualWidth:0}x{root.ActualHeight:0})");
    }

    private static void Measure(string what, int ms = 6000, bool ticks = true)
    {
        var times = new List<TimeSpan>();
        EventHandler tick = (_, e) =>
        {
            var at = ((RenderingEventArgs)e).RenderingTime;
            if (times.Count == 0 || times[^1] != at) times.Add(at);
        };
        var me = System.Diagnostics.Process.GetCurrentProcess();
        var cpu = me.TotalProcessorTime;
        var threads = me.Threads.Cast<System.Diagnostics.ProcessThread>().ToDictionary(t => t.Id, t => t.TotalProcessorTime);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long drawn = WaveVisualizer.Drawn, shown = DesktopFrames();
        if (ticks) CompositionTarget.Rendering += tick;
        var frame = new DispatcherFrame();
        var stop = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        stop.Tick += (_, _) => { stop.Stop(); frame.Continue = false; };
        stop.Start();
        Dispatcher.PushFrame(frame);
        if (ticks) CompositionTarget.Rendering -= tick;
        me.Refresh();
        double seconds = clock.Elapsed.TotalSeconds;
        var gaps = times.Zip(times.Skip(1), (a, b) => (b - a).TotalMilliseconds).OrderBy(g => g).ToList();
        double p95 = gaps.Count > 0 ? gaps[(int)(gaps.Count * 0.95)] : 0, worst = gaps.Count > 0 ? gaps[^1] : 0;
        Console.WriteLine($"  {what}: bars drawn {(WaveVisualizer.Drawn - drawn) / seconds:0.0}/s, desktop frames {(DesktopFrames() - shown) / seconds:0.0}/s, ticks {times.Count / seconds:0.0}/s, p95 {p95:0.0} ms, worst {worst:0} ms, CPU {(me.TotalProcessorTime - cpu).TotalSeconds / seconds * 100:0}% of a core");
        // Which threads it was: the UI's is the first (WPF's render thread and the audio's are others).
        var ui = Environment.CurrentManagedThreadId;
        var top = me.Threads.Cast<System.Diagnostics.ProcessThread>()
            .Select(t => (t.Id, Used: (t.TotalProcessorTime - threads.GetValueOrDefault(t.Id)).TotalSeconds / seconds * 100, Start: t.StartAddress))
            .OrderByDescending(t => t.Used).Take(4);
        Console.WriteLine("    threads: " + string.Join(", ", top.Select(t => $"{t.Used:0.0}%{(t.Id == GetCurrentThreadId() ? " UI" : "")}")));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    // The frames the desktop's compositor really put together: the proof of what reaches the screen (Rendering ticks come
    // even when nothing changes). DWM_TIMING_INFO is packed, 292 bytes; its cFrame is at 56.
    private static long DesktopFrames()
    {
        var info = new byte[292];
        BitConverter.TryWriteBytes(info, info.Length);
        return DwmGetCompositionTimingInfo(IntPtr.Zero, info) == 0 ? BitConverter.ToInt64(info, 56) : 0;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmGetCompositionTimingInfo(IntPtr window, byte[] info);

    // Something for the bars to follow: chords, a beat and a hiss that come and go.
    private static void WriteMusic(string file, int seconds)
    {
        const int rate = 44100;
        int frames = rate * seconds;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
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

    private static void WriteCover(string file)
    {
        const int size = 1000;
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x2B, 0x10, 0x70), Color.FromRgb(0xF0, 0x40, 0x80), 45), null, new Rect(0, 0, size, size));
            dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(220, 0xFF, 0xC0, 0x50), Color.FromArgb(0, 0xFF, 0xC0, 0x50)), null, new Point(620, 380), 420, 420);
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(v);
        var jpeg = new JpegBitmapEncoder { QualityLevel = 90 };
        jpeg.Frames.Add(BitmapFrame.Create(rtb));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var f = File.Create(file);
        jpeg.Save(f);
    }

    // (a made-up sound for the bars: nothing reaches the speakers)
    private static void Sound()
    {
        var block = new float[9600];
        for (int t = 0; t < 30; t++)
        {
            for (int i = 0; i < block.Length; i += 2)
            {
                double at = (t * 4800 + i / 2) / 48000.0;
                block[i] = block[i + 1] = (float)(0.3 * Math.Sin(2 * Math.PI * 70 * at) + 0.15 * Math.Sin(2 * Math.PI * 440 * at) + 0.1 * Math.Sin(2 * Math.PI * 3000 * at));
            }
            Spectrum.Shared.Feed(block, 0, block.Length);
            Pump(30);
        }
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
