using System.ComponentModel;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UltimateMP3Player.Core;
using Avalonia.Media;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class MainView : UserControl
{
    private readonly AppHost _host;
    private MainViewModel? _session;
    private bool _started;
    // The pages already built, most recent last (coming back to one finds it as it was: scroll, filter).
    private readonly List<(object Page, Control View)> _pages = new();
    private Control? _shown;
    private object? _shownPage;
    private bool _syncingTabs;

    public MainView() : this(App.Host) { }

    public MainView(AppHost host)
    {
        _host = host;
        InitializeComponent();
        foreach (var (tab, name) in new[] { (TabHome, "home"), (TabSearch, "search"), (TabLibrary, "library"), (TabDownloads, "downloads"), (TabSettings, "settings") })
        {
            var t = name;
            tab.IsCheckedChanged += (_, _) => { if (tab.IsChecked == true && !_syncingTabs) GoTab(t); };
            // Again on the open tab: back to its start, or up to the top.
            tab.AddHandler(PointerReleasedEvent, (_, _) => { if (tab.IsChecked == true && TabOf(_session?.Page) == t) Dispatcher.UIThread.Post(() => GoTab(t)); },
                RoutingStrategies.Bubble, true);
        }
        Mini.OpenRequested += () => _session?.Navigate(_session.NowPlaying);
        FullPlayer.CloseRequested += () =>
        {
            if (_session?.Page is NowPlayingViewModel) _session.BackCommand.Execute(null);
        };
        AttachedToVisualTree += (_, _) => OnAttached();
        DetachedFromVisualTree += (_, _) => OnDetached();
        // Turned (landscape): the system's buttons may move to a side, the tabs to the left.
        SizeChanged += (_, _) =>
        {
            RefreshInsets();
            Arrange();
        };
    }

    // ------------------------------------------------------------------ turned, or a tablet

    // Wide (a phone turned, a tablet): the tabs in a column on the left, like the computer's side bar, so the pages keep
    // the whole height; narrow: the bar at the bottom.
    private const double RailWidth = 92;
    private bool? _rail;
    public bool IsWide => _rail == true;

    private void Arrange()
    {
        double w = Bounds.Width - Math.Max(0, _insets.Left) - Math.Max(0, _insets.Right), h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        bool rail = w >= 600 && (w > h || w >= 840);
        if (rail == _rail) return;
        _rail = rail;
        Shell.ColumnDefinitions = rail ? new ColumnDefinitions("Auto,*") : new ColumnDefinitions("*");
        foreach (var c in new Control[] { Banners, PageHost, ToastBox, Mini }) Grid.SetColumn(c, rail ? 1 : 0);
        Grid.SetColumn(NavBar, 0);
        Grid.SetRow(NavBar, rail ? 0 : 3);
        Grid.SetRowSpan(NavBar, rail ? 4 : 1);
        NavBar.BorderThickness = rail ? new Thickness(0, 0, 1, 0) : new Thickness(0, 1, 0, 0);
        Tabs.Rows = rail ? 0 : 1;
        Tabs.Columns = rail ? 1 : 0;
        Tabs.VerticalAlignment = rail ? Avalonia.Layout.VerticalAlignment.Center : Avalonia.Layout.VerticalAlignment.Stretch;
        Tabs.Margin = rail ? new Thickness(0, 6, 0, 6) : new Thickness(4, 2, 4, 0);
        if (_insets.Top >= 0) ApplyInsets(_insets, true);
        else NavBar.Width = rail ? RailWidth : double.NaN;
    }

    public SheetHost Sheets => SheetLayer;

    public bool IsReady { get; private set; }
    public double TopInset { get; private set; }
    public double BottomInset { get; private set; }

    public LibraryHub? Hub { get; private set; }

    // ------------------------------------------------------------------ start

    private TopLevel? _top;

    private void OnAttached()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        _top = top;
        if (top.InsetsManager is { } insets)
        {
            insets.DisplayEdgeToEdge = true;
            insets.SafeAreaChanged -= OnSafeArea;
            insets.SafeAreaChanged += OnSafeArea;
        }
        RefreshInsets();
        // Some phones tell the room of their bars only a moment after the start: asked again.
        DispatcherTimer.RunOnce(RefreshInsets, TimeSpan.FromMilliseconds(400));
        DispatcherTimer.RunOnce(RefreshInsets, TimeSpan.FromMilliseconds(1500));
        if (top.InputPane is { } pane)
        {
            pane.StateChanged -= OnKeyboard;
            pane.StateChanged += OnKeyboard;
        }
        top.BackRequested -= OnBack;
        top.BackRequested += OnBack;
#if DEBUG
        // Test builds: frames per second and drawing times on screen, with a file named "fps" in the app's data.
        if (File.Exists(Path.Combine(AppPaths.DataDir, "fps")))
            top.RendererDiagnostics.DebugOverlays = Avalonia.Rendering.RendererDebugOverlays.Fps | Avalonia.Rendering.RendererDebugOverlays.RenderTimeGraph |
                                                   Avalonia.Rendering.RendererDebugOverlays.LayoutTimeGraph;
#endif
        if (_started) return;
        _started = true;
        Dispatcher.UIThread.Post(Start, DispatcherPriority.Background);
    }

    // Replaced by a new screen (the language changed): the window's back button, bars and keyboard are the new one's.
    private void OnDetached()
    {
        if (_top == null) return;
        if (_top.InsetsManager is { } insets) insets.SafeAreaChanged -= OnSafeArea;
        if (_top.InputPane is { } pane) pane.StateChanged -= OnKeyboard;
        _top.BackRequested -= OnBack;
        _top = null;
    }

    private void OnSafeArea(object? sender, Avalonia.Controls.Platform.SafeAreaChangedArgs e) => RefreshInsets();

    // The room of the status bar, of the system's buttons or gesture line and of a notch: what Avalonia says, or what
    // Android says if it's more (on some phones the first doesn't follow every change).
    public void RefreshInsets()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var a = top.InsetsManager?.SafeAreaPadding ?? default;
        var s = SystemInsets(top.RenderScaling);
        ApplyInsets(new Thickness(Math.Max(a.Left, s.Left), Math.Max(a.Top, s.Top), Math.Max(a.Right, s.Right), Math.Max(a.Bottom, s.Bottom)));
    }

    // The system bars and the notch (never the keyboard), in the app's units.
    private static Thickness SystemInsets(double scaling)
    {
        try
        {
            var wi = MainActivity.Current?.Window?.DecorView?.RootWindowInsets;
            if (wi == null || scaling <= 0) return default;
            int l, t, r, b;
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var i = wi.GetInsets(Android.Views.WindowInsets.Type.SystemBars() | Android.Views.WindowInsets.Type.DisplayCutout());
                (l, t, r, b) = (i.Left, i.Top, i.Right, i.Bottom);
            }
            else
            {
                // The stable ones: the bars even while hidden, without the keyboard.
                (l, t, r, b) = (wi.StableInsetLeft, wi.StableInsetTop, wi.StableInsetRight, wi.StableInsetBottom);
                if (OperatingSystem.IsAndroidVersionAtLeast(28) && wi.DisplayCutout is { } c)
                    (l, t, r, b) = (Math.Max(l, c.SafeInsetLeft), Math.Max(t, c.SafeInsetTop), Math.Max(r, c.SafeInsetRight), Math.Max(b, c.SafeInsetBottom));
            }
            return new Thickness(l / scaling, t / scaling, r / scaling, b / scaling);
        }
        catch { return default; }
    }

    private Thickness _insets = new(-1);

    private void ApplyInsets(Thickness t, bool force = false)
    {
        if (t == _insets && !force) return;
        _insets = t;
        // Drawn under the status bar and the system's buttons (edge to edge, AutoSafeAreaPadding off): the room they take is
        // given here once, so the Home's colour can go up behind the clock.
        TopInset = t.Top;
        BottomInset = t.Bottom;
        Backdrop.Height = 280 + t.Top;
        if (_rail == true)
        {
            // The column of tabs on the left, from the top of the screen (behind the clock) to the bottom, its tabs in the
            // middle of the room left; the song playing keeps clear of the gesture line itself.
            Shell.Margin = t;
            NavBar.Margin = new Thickness(-t.Left, -t.Top, 0, -t.Bottom);
            NavBar.Padding = new Thickness(t.Left, t.Top, 0, t.Bottom);
            NavBar.Width = RailWidth + t.Left;
            Backdrop.Margin = new Thickness(t.Left + RailWidth, 0, 0, 0);
        }
        else
        {
            Shell.Margin = new Thickness(t.Left, t.Top, t.Right, 0);
            // A little room under the labels, above the system's gesture line or buttons (or the screen's edge).
            NavBar.Margin = default;
            NavBar.Padding = new Thickness(0, 0, 0, t.Bottom + (t.Bottom > 0 ? 2 : 6));
            NavBar.Width = double.NaN;
            Backdrop.Margin = default;
        }
        FullPlayer.SetInsets(t);
        Gate.Padding = t;
        SheetLayer.SetInsets(t);
    }

    private void OnKeyboard(object? sender, Avalonia.Controls.Platform.InputPaneStateEventArgs e)
    {
        double h = e.NewState == Avalonia.Controls.Platform.InputPaneState.Open ? e.EndRect.Height : 0;
        SheetLayer.SetKeyboard(h);
    }

    private async void Start()
    {
        try
        {
            if (_host.Session != null)
            {
                Attach(_host.Session);
                Ready();
                return;
            }
            if (!_host.TermsAccepted)
            {
                if (!await Dialogs.TermsAsync(true))
                {
                    MainActivity.Current?.FinishAndRemoveTask();
                    return;
                }
                _host.AcceptTerms();
                await AskName();
            }
            if (_host.StartupProfile() is { } profile)
            {
                _host.OpenProfile(profile);
                Ready();
            }
            else ShowProfiles(true);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Dialogs.Alert("Ultimate MP3 Player", L.T("Ultimate MP3 Player non è riuscito ad avviarsi:") + "\n\n" + ex.Message);
        }
    }

    // The first time: the phone doesn't say who owns it (the computer apps take the account's name), so the Home's
    // "Good evening" asks. Skipped, the profile stays "Me".
    private async Task AskName()
    {
        if (_host.Profiles.Profiles is not [{ } only] || only.Name != L.T("Io")) return;
        var name = (await Dialogs.PromptAsync(L.T("Come ti chiami?"), L.T("Il tuo nome"), ""))?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        only.Name = name.Length > 40 ? name[..40] : name;
        _host.Profiles.Save();
    }

    private void Ready()
    {
        if (IsReady) return;
        IsReady = true;
        Shell.IsVisible = true;
        App.DeliverPending();
        _ = _host.EnsureEnginesAsync();
        _ = AskNotifications();
        _ = OfferRightPackage();
    }

    // The phones' package (ARM) on an x86 device, an emulator: everything runs through a translator and downloads fail.
    // Said at every start until the right one is installed (the updater fetches it: same version, this device's package).
    private async Task OfferRightPackage()
    {
        if (!Services.Updater.WrongPackage) return;
        await Task.Delay(1200);
        if (await Dialogs.ConfirmAsync(L.T("Versione per un altro processore"),
                L.T("Hai installato la versione per telefoni (ARM), ma questo dispositivo è x86_64 (un emulatore o un Chromebook): l'app va molto più lenta e i download non funzionano. Scarica e installa quella giusta (UltimateMP3Player-android-x86_64.apk): musica e impostazioni restano."),
                L.T("Installa quella giusta")))
        {
            _session?.Toast(L.T("Download della versione giusta per questo dispositivo…"));
            await _host.CheckAppUpdateNow();
            if (!_host.Updates.IsReady && _host.Updates.Status is { } status) _session?.Toast(status);
        }
    }

    // Android 13+: the notifications are allowed by the user (the player's one is shown anyway, the downloads' isn't).
    private static async Task AskNotifications()
    {
        if (Android.OS.Build.VERSION.SdkInt < Android.OS.BuildVersionCodes.Tiramisu || MainActivity.Current is not { } activity) return;
        var prefs = activity.GetSharedPreferences("ump", Android.Content.FileCreationMode.Private);
        if (prefs == null || prefs.GetBoolean("asked-notifications", false)) return;
        prefs.Edit()?.PutBoolean("asked-notifications", true)?.Apply();
        await Task.Delay(1500);
        await activity.RequestPermission(Android.Manifest.Permission.PostNotifications);
    }

    // "Who's listening?" (at start with several profiles, or from the avatar): the chosen one opens.
    public void ShowProfiles(bool startup)
    {
        var page = new ProfilesPage(_host, startup);
        page.Done += info =>
        {
            Gate.IsVisible = false;
            Gate.Content = null;
            if (info != null && info.Id != _host.Session?.Profile.Info.Id) _host.OpenProfile(info);
            else if (_session != null)
            {
                // The same profile, maybe renamed or with another picture.
                DataContext = null;
                DataContext = _session;
                _host.ApplyTheme();
            }
            if (startup) Ready();
        };
        Gate.Content = page;
        Gate.IsVisible = true;
    }

    // ------------------------------------------------------------------ the profile's screen

    public void Attach(MainViewModel session)
    {
        if (_session != null) _session.PropertyChanged -= OnSession;
        _session = session;
        Hub = new LibraryHub(session);
        foreach (var (_, v) in _pages) PageHost.Children.Remove(v);
        _pages.Clear();
        _trail.Clear();
        _shown = null;
        _shownPage = null;
        DataContext = session;
        FullPlayer.DataContext = session.NowPlaying;
        session.PropertyChanged += OnSession;
        ShowPage(session.Page, false);
        WarmTabs(session);
    }

    // The tabs' pages built ahead, one at a time while nothing else is going on: the first tap on a tab then only
    // has to show it (building one takes a noticeable moment).
    private void WarmTabs(MainViewModel session)
    {
        var roots = new object[] { session.Home, session.Search, Hub!, session.Downloads, session.Settings };
        void Next(int i)
        {
            if (i >= roots.Length || _session != session) return;
            if (_pages.All(p => p.Page != roots[i])) ViewFor(roots[i]);
            Dispatcher.UIThread.Post(() => Next(i + 1), DispatcherPriority.ApplicationIdle);
        }
        Dispatcher.UIThread.Post(() => Next(0), DispatcherPriority.ApplicationIdle);
    }

    // The language changed: everything again with the new texts (the music goes on).
    public void Rebuild(MainViewModel session)
    {
        var fresh = new MainView(_host) { _started = true };
        fresh.IsReady = true;
        fresh.Shell.IsVisible = true;
        _host.View = fresh;
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime single) single.MainView = fresh;
        fresh.Attach(session);
        if (_session != null) _session.PropertyChanged -= OnSession;
    }

    private void OnSession(object? sender, PropertyChangedEventArgs e)
    {
        if (sender != _session) return;
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.Page):
                ShowPage(_session!.Page, true);
                break;
            case nameof(MainViewModel.ToastText):
                ShowToast(_session!.ToastText);
                break;
        }
    }

    // ------------------------------------------------------------------ pages

    private void ShowPage(object page, bool animate)
    {
        if (page is NowPlayingViewModel)
        {
            FullPlayer.Open(animate);
            SyncTabs();
            return;
        }
        if (FullPlayer.IsOpen) FullPlayer.Close(animate);
        var view = ViewFor(page);
        if (view != _shown)
        {
            var old = _shown;
            var oldPage = _shownPage;
            _shown = view;
            _shownPage = page;
            bool forward = Forward(oldPage, page);
            // A movement still going (tabs tapped quickly) gives way: every other page out of sight and in its place.
            _slide?.Cancel();
            _slide = null;
            foreach (var (_, v) in _pages)
                if (v != view && v != old)
                {
                    v.IsVisible = false;
                    v.RenderTransform = null;
                }
            view.RenderTransform = null;
            if (old != null) old.RenderTransform = null;
            bool motion = animate && Ui.Animations && old != null;
            (view as IPage)?.Shown();
            if (motion) _ = Slide(old!, view, forward, _slide = new CancellationTokenSource());
            else
            {
                view.IsVisible = true;
                if (old != null) old.IsVisible = false;
            }
            ShowBackdrop(view is HomePage, motion);
        }
        SyncTabs();
    }

    // ------------------------------------------------------------------ the movement between pages

    // Side by side, like turning a page: the old one goes out on one side while the new one comes in from the other,
    // never one over the other. Between tabs in the bar's order; within a tab, into a list from the right, back from the left.
    private static readonly SplineEasing PageEase = new(0.2, 0, 0, 1);
    private CancellationTokenSource? _slide;
    // The pages seen, to tell "into" from "back".
    private readonly List<object> _trail = new();

    private async Task Slide(Control from, Control to, bool forward, CancellationTokenSource cts)
    {
        double w = PageHost.Bounds.Width;
        var fromShift = new TranslateTransform();
        var toShift = new TranslateTransform(forward ? w : -w, 0);
        from.RenderTransform = fromShift;
        to.RenderTransform = toShift;
        to.IsVisible = true;
        // The new page measured and drawn once off screen (built now if it's new): the movement then doesn't stutter.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (cts.IsCancellationRequested) return;
        Ui.Tween(this, 300, t => PageEase.Ease(t), t =>
        {
            if (cts.IsCancellationRequested) return;
            fromShift.X = (forward ? -w : w) * t;
            toShift.X = (forward ? w : -w) * (1 - t);
            if (t < 1) return;
            // Done, in the same frame: the old page out of sight and both back in their place.
            from.IsVisible = from == _shown;
            from.RenderTransform = null;
            to.RenderTransform = null;
            if (_slide == cts) _slide = null;
        });
    }

    private static int TabOrder(string tab) => tab switch { "home" => 0, "search" => 1, "library" => 2, "downloads" => 3, _ => 4 };

    private bool Forward(object? from, object to)
    {
        bool back = _trail.Count >= 2 && _trail[^2] == to;
        if (back) _trail.RemoveAt(_trail.Count - 1);
        else
        {
            _trail.Add(to);
            if (_trail.Count > 30) _trail.RemoveAt(0);
        }
        if (from == null) return true;
        int a = TabOrder(TabOf(from)), b = TabOrder(TabOf(to));
        return a != b ? b > a : !back;
    }

    private int _backdropRun;

    private void ShowBackdrop(bool show, bool animate)
    {
        double from = Backdrop.Opacity, to = show ? 0.6 : 0;
        int run = ++_backdropRun;
        if (Math.Abs(from - to) < 0.01) return;
        if (!animate || !Ui.Animations) { Backdrop.Opacity = to; return; }
        // A newer change (tabs tapped quickly) takes over from this one.
        Ui.Tween(Backdrop, 200, Ui.CubicOut, t => { if (run == _backdropRun) Backdrop.Opacity = from + (to - from) * t; });
    }

    private Control ViewFor(object page)
    {
        int i = _pages.FindIndex(p => p.Page == page);
        if (i >= 0)
        {
            var hit = _pages[i];
            _pages.RemoveAt(i);
            _pages.Add(hit);
            return hit.View;
        }
        Control view = page switch
        {
            HomeViewModel => new HomePage(),
            SearchViewModel => new SearchPage(),
            LibraryHub => new LibraryPage(),
            LibraryViewModel or PlaylistPageViewModel => new SongsPage(),
            DownloadsPageViewModel => new DownloadsPage(),
            SettingsViewModel => new SettingsPage(),
            StatsViewModel => new StatsPage(),
            _ => new TextBlock { Text = page.ToString() },
        };
        view.DataContext = page;
        view.IsVisible = false;
        PageHost.Children.Add(view);
        _pages.Add((page, view));
        // Old pages are let go (the tabs' own pages stay).
        while (_pages.Count > 10)
        {
            int victim = _pages.FindIndex(p => p.View != _shown && !IsRoot(p.Page));
            if (victim < 0) break;
            PageHost.Children.Remove(_pages[victim].View);
            _pages.RemoveAt(victim);
        }
        return view;
    }

    private bool IsRoot(object page) => _session != null &&
        (page == _session.Home || page == _session.Search || page == Hub || page == _session.Downloads || page is SettingsViewModel);

    // ------------------------------------------------------------------ the bottom bar

    private string TabOf(object? page) => page switch
    {
        SearchViewModel => "search",
        LibraryHub or LibraryViewModel or PlaylistPageViewModel => "library",
        DownloadsPageViewModel => "downloads",
        SettingsViewModel => "settings",
        NowPlayingViewModel => TabOf(_shownPage),
        _ => "home",
    };

    private void SyncTabs()
    {
        var tab = TabOf(_session?.Page);
        _syncingTabs = true;
        TabHome.IsChecked = tab == "home";
        TabSearch.IsChecked = tab == "search";
        TabLibrary.IsChecked = tab == "library";
        TabDownloads.IsChecked = tab == "downloads";
        TabSettings.IsChecked = tab == "settings";
        _syncingTabs = false;
    }

    private void GoTab(string tab)
    {
        if (_session == null) return;
        object root = tab switch
        {
            "search" => _session.Search,
            "library" => Hub!,
            "downloads" => _session.Downloads,
            "settings" => _session.Settings,
            _ => _session.Home,
        };
        Sheets.CloseAll();
        if (_session.Page == root)
        {
            (_shown as IPage)?.ScrollToTop();
            return;
        }
        if (tab == "settings") _session.GoSettings();
        else _session.Navigate(root);
    }

    // ------------------------------------------------------------------ back

    private void OnBack(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Sheets.CloseTop()) return;
        if (Gate.IsVisible)
        {
            if (Gate.Content is ProfilesPage p && p.Back()) return;
            if (_session != null) { Gate.IsVisible = false; Gate.Content = null; return; }
            MainActivity.Current?.MoveTaskToBack(true);
            return;
        }
        if (_session == null) return;
        if (_session.Page is NowPlayingViewModel)
        {
            if (FullPlayer.Back()) return;
            _session.BackCommand.Execute(null);
            return;
        }
        if (_shown is IPage page && page.Back()) return;
        if (_session.BackCommand.CanExecute(null))
        {
            _session.BackCommand.Execute(null);
            return;
        }
        if (_session.Page != _session.Home)
        {
            _session.Navigate(_session.Home);
            return;
        }
        // At the start: the app goes in the background (the music goes on), like the other players.
        MainActivity.Current?.MoveTaskToBack(true);
    }

    // ------------------------------------------------------------------ toast

    private string? _toast;

    private void ShowToast(string? text)
    {
        if (text != null)
        {
            _toast = text;
            ToastText.Text = text;
            ToastBox.IsVisible = true;
            if (!Ui.Animations) { ToastBox.Opacity = 1; return; }
            double from = ToastBox.Opacity;
            Ui.Tween(this, 160, Ui.CubicOut, t => ToastBox.Opacity = from + (1 - from) * t);
            return;
        }
        _toast = null;
        double start = ToastBox.Opacity;
        Ui.Tween(this, 220, Ui.Linear, t =>
        {
            if (_toast != null) return;
            ToastBox.Opacity = start * (1 - t);
        });
    }
}

// A page of the phone: back (a filter, a choice of songs to undo first) and up to the top; shown again (what changed
// while it was hidden is brought up to date only now).
public interface IPage
{
    bool Back();
    void ScrollToTop();
    void Shown() { }
}

// The "Libreria" tab: songs, playlists, tags.
public sealed class LibraryHub : Observable
{
    public LibraryHub(MainViewModel main) => Main = main;

    public MainViewModel Main { get; }

    private int _tab = 1;
    // 0 songs, 1 playlists, 2 tags.
    public int Tab { get => _tab; set => Set(ref _tab, value); }
}
