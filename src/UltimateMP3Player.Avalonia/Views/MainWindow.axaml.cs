using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class MainWindow : Window
{
    private readonly AppHost _host = null!;
    private bool _silent;
    private readonly HashSet<Key> _down = new();

    public MainWindow() => InitializeComponent();

    public MainWindow(AppHost host) : this()
    {
        _host = host;
        Icon = App.WindowIcon;
        PhoneLinkViewModel.ShowWindow = PhoneDialogs.Show;
        WindowFrame.Apply(this, Root, TitleBar, TitleText, WindowButtons, MinButton, MaxButton, CloseButton, ResizeEdges);
        var s = host.Settings;
        var screen = Screens.Primary?.WorkingArea.Size.ToSize(Screens.Primary.Scaling);
        Width = Math.Clamp(s.WindowWidth, MinWidth, Math.Max(MinWidth, screen?.Width ?? s.WindowWidth));
        Height = Math.Clamp(s.WindowHeight, MinHeight, Math.Max(MinHeight, screen?.Height ?? s.WindowHeight));
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
        Closing += OnClosing;
        this.Bind(BarShownProperty, new Avalonia.Data.Binding(nameof(MainViewModel.ShowPlayerBar)));
        PropertyChanged += (_, e) =>
        {
            if (e.Property == BarShownProperty) SlideBar(e.GetNewValue<bool>());
            if (e.Property == FullScreenProperty) ApplyFullScreen(e.GetNewValue<bool>());
            if (e.Property != WindowStateProperty) return;
            Ui.Hidden = WindowState == WindowState.Minimized || !IsVisible;
            Vm?.Player.SetVisible(WindowState != WindowState.Minimized);
            if (WindowState is WindowState.Normal or WindowState.Maximized) s.WindowMaximized = WindowState == WindowState.Maximized;
        };
        this.Bind(FullScreenProperty, new Avalonia.Data.Binding(nameof(MainViewModel.FullScreen)));
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) => _down.Remove(e.Key), RoutingStrategies.Tunnel, true);
        Deactivated += (_, _) => _down.Clear();
        AddHandler(TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, (_, e) => SelectionBar.ClearOnOutsideClick(e.Source as Visual), RoutingStrategies.Tunnel, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver, RoutingStrategies.Tunnel);
        // Leaving one element for another is a leave followed by an over: only a real exit hides the pack overlay.
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => _dropHide.Start(), RoutingStrategies.Tunnel);
        _dropHide.Tick += (_, _) => HidePackDrop();
        AddHandler(DragDrop.DropEvent, OnPackDrop, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DropEvent, OnDrop, RoutingStrategies.Bubble);
        // Two fingers sideways on the touchpad over a song's row: the row is pulled (into the queue).
        AddHandler(PointerWheelChangedEvent, Rows.OnWheel, RoutingStrategies.Tunnel);
        Opened += (_, _) => _host.OnTrackChanged();

        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        VolumeSlider.AddHandler(PointerWheelChangedEvent, Volume_Wheel, RoutingStrategies.Tunnel);
        PlaybackButton.AddHandler(PointerWheelChangedEvent, Speed_Wheel, RoutingStrategies.Tunnel);
        SpeedSlider.AddHandler(PointerWheelChangedEvent, Speed_Wheel, RoutingStrategies.Tunnel);
        PlayerGrid.SizeChanged += (_, _) => FitRightBlock();
        PlaybackButton.SizeChanged += (_, _) => FitRightBlock();
        // The playback panel's equalizer, like the one in Settings: the wheel moves a band by half a dB, a double click resets it.
        if (PlaybackPopup.Child is { } panel)
        {
            panel.AddHandler(PointerWheelChangedEvent, (_, e) =>
            {
                if (Band(e.Source) is not { } band || e.Delta.Y == 0) return;
                band.Gain += e.Delta.Y > 0 ? 0.5 : -0.5;
                e.Handled = true;
            }, RoutingStrategies.Tunnel);
            panel.AddHandler(DoubleTappedEvent, (_, e) => { if (Band(e.Source) is { } band) band.Gain = 0; }, RoutingStrategies.Bubble, true);
        }
        PlaybackSettingsButton.Click += (_, _) =>
        {
            PlaybackPopup.IsOpen = false;
            Vm?.GoSettings();
        };
        AddToPlaylistButton.Click += (_, _) =>
        {
            if (Vm?.Player.Current is { } t) Menus.Open(Menus.AddToPlaylistMenu(t), AddToPlaylistButton, false);
        };
        PlaylistList.SelectionChanged += (_, _) =>
        {
            if (PlaylistList.SelectedItem is PlaylistViewModel p)
            {
                PlaylistList.SelectedItem = null;
                Vm?.OpenPlaylist(p);
            }
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // Replaced by a new window (language change): no tray logic.
    public void CloseSilently()
    {
        _silent = true;
        Close();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // (the size kept is the window's own, not the screen's)
        if (Vm is { FullScreen: true } vm) vm.FullScreen = false;
        var s = _host.Settings;
        if (WindowState == WindowState.Normal)
        {
            s.WindowWidth = Bounds.Width;
            s.WindowHeight = Bounds.Height;
        }
        s.WindowMaximized = WindowState == WindowState.Maximized;
        s.Save();
        if (_silent) return;
        IsClosing = true;
        // Into the tray the window goes away too (a new one opens from the icon); the app and the music stay.
        if (!_host.OnWindowClosing()) e.Cancel = true;
        IsClosing = !e.Cancel;
    }

    // Closing by itself: quitting the app from here mustn't close it a second time.
    public bool IsClosing { get; private set; }

    // ------------------------------------------------------------------ keyboard

    // Ctrl on Windows and Linux, ⌘ on macOS.
    private static readonly KeyModifiers Command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private bool InText => FocusManager?.GetFocusedElement() is TextBox;
    // (Ctrl+↑/↓ go on from a one-line box, Search's: there they have nothing to move, and the box takes the keys as the page opens)
    private bool InLines => FocusManager?.GetFocusedElement() is TextBox { AcceptsReturn: true };

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool repeat = !_down.Add(e.Key);
        var vm = Vm;
        if (vm == null) return;
        bool inText = InText;
        var mods = e.KeyModifiers;
        bool ctrl = (mods & Command) != 0, shift = (mods & KeyModifiers.Shift) != 0;
        // Alt+←/→ (and ⌘[ ⌘] on macOS): back and forward, like a browser.
        if (mods == KeyModifiers.Alt && e.Key is Key.Left or Key.Right
            || OperatingSystem.IsMacOS() && mods == KeyModifiers.Meta && e.Key is Key.OemOpenBrackets or Key.OemCloseBrackets)
        {
            Step(vm, e.Key is Key.Right or Key.OemCloseBrackets);
            e.Handled = true;
            return;
        }
        // Ctrl+1…5: the sidebar's pages, like a browser's tabs.
        if (ctrl && !shift && e.Key is >= Key.D1 and <= Key.D5 or >= Key.NumPad1 and <= Key.NumPad5)
        {
            vm.GoMenu(e.Key >= Key.NumPad1 ? e.Key - Key.NumPad1 : e.Key - Key.D1);
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Escape when vm.FullScreen:
                vm.FullScreen = false;
                e.Handled = true;
                break;
            case Key.Escape when vm.QueueOpen:
                vm.QueueOpen = false;
                e.Handled = true;
                break;
            case Key.Space when !inText:
                vm.TogglePlay();
                e.Handled = true;
                break;
            // DJ "Tap BPM" tab: T taps along with the song playing there (or the deck tapped last).
            case Key.T when !inText && !repeat && vm.Page is DjViewModel { TapMode: true } dj:
                dj.TapKey();
                e.Handled = true;
                break;
            // Ctrl+Shift+arrows: the songs and the volume; Ctrl+arrows: the pages (up/down the sidebar, sideways the tabs).
            case Key.Right when ctrl && shift && !inText:
                vm.Player.NextCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Left when ctrl && shift && !inText:
                vm.Player.PreviousCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up when ctrl && shift && !inText:
                vm.Player.Volume += 0.05;
                e.Handled = true;
                break;
            case Key.Down when ctrl && shift && !inText:
                vm.Player.Volume -= 0.05;
                e.Handled = true;
                break;
            case Key.Right when ctrl && !inText:
            case Key.Left when ctrl && !inText:
                vm.StepTab(e.Key == Key.Right ? 1 : -1);
                e.Handled = true;
                break;
            case Key.Up when ctrl && !shift && !InLines:
            case Key.Down when ctrl && !shift && !InLines:
                vm.StepMenu(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                break;
            case Key.Right when !inText:
                vm.Player.Seek(vm.Player.Position + 5);
                e.Handled = true;
                break;
            case Key.Left when !inText:
                vm.Player.Seek(vm.Player.Position - 5);
                e.Handled = true;
                break;
            case Key.F when ctrl:
            case Key.L when ctrl:
                vm.OpenSearch();
                SearchView.FocusShown();
                e.Handled = true;
                break;
            case Key.OemComma when ctrl:
                vm.GoSettings();
                e.Handled = true;
                break;
            case Key.V when ctrl && !inText:
                vm.PasteLinkCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Back when !inText:
            case Key.BrowserBack:
                Step(vm, false);
                e.Handled = true;
                break;
            case Key.BrowserForward:
                Step(vm, true);
                e.Handled = true;
                break;
        }
    }

    private static void Step(MainViewModel vm, bool forward)
    {
        var command = forward ? vm.ForwardCommand : vm.BackCommand;
        if (command.CanExecute(null)) command.Execute(null);
    }

    // The mouse's side buttons: back and forward. Up next popped out over the page: a press anywhere else folds it (its own
    // button toggles it; menus opened from it are windows of their own, not this one).
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (kind is PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed)
        {
            Step(vm, kind == PointerUpdateKind.XButton2Pressed);
            e.Handled = true;
            return;
        }
        if (vm.QueueOpen && e.Source is Visual v && TopLevel.GetTopLevel(v) == this
            && !QueuePanel.IsVisualAncestorOf(v) && !QueueButton.IsVisualAncestorOf(v) && v != QueueButton)
            vm.QueueOpen = false;
    }

    // ------------------------------------------------------------------ the song's page over the whole screen

    private static readonly StyledProperty<bool> FullScreenProperty = AvaloniaProperty.Register<MainWindow, bool>("FullScreen");
    private WindowState _windowedState;

    // The system's full screen, without title bar and sidebar; the page reaches under the player bar, see-through
    // (NowPlayingView.PlaceFullScreen). Then back to how the window was.
    private void ApplyFullScreen(bool on)
    {
        TitleBar.IsVisible = Sidebar.IsVisible = !on;
        Body.ColumnDefinitions[0].Width = new GridLength(on ? 0 : 248);
        Grid.SetRow(Body, on ? 0 : 1);
        Grid.SetRowSpan(Body, on ? 3 : 1);
        PlayerBar.Background = on ? Brushes.Transparent : Ui.Find<IBrush>("SidebarBrush");
        double bar = on ? PlayerBar.Bounds.Height : 0;
        ToastBox.Margin = new Thickness(0, 0, 0, 18 + bar);
        DownloadPanel.Margin = new Thickness(0, 0, 20, 16 + bar);
        if (on && WindowState != WindowState.FullScreen)
        {
            _windowedState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
            WindowState = WindowState.FullScreen;
        }
        else if (!on && WindowState == WindowState.FullScreen) WindowState = _windowedState;
    }

    // ------------------------------------------------------------------ the player bar, away for the DJ

    private static readonly StyledProperty<bool> BarShownProperty = AvaloniaProperty.Register<MainWindow, bool>("BarShown", true);
    private readonly TranslateTransform _barShift = new();
    private double _barHeight;
    private int _barRun;

    // The page gets the bar's room at once (under its own slide: AnimatedContent), and the bar goes down out of the window over
    // it; coming back it rises over the page and only then takes its room again. The page is laid out once, not on every frame.
    private void SlideBar(bool show)
    {
        if (PlayerBar.IsVisible && PlayerBar.Margin.Bottom == 0) _barHeight = PlayerBar.Bounds.Height;
        double h = _barHeight;
        // (how far down it is now: 0 in its place, h out of the window; halfway when it's turned back)
        double down = !PlayerBar.IsVisible ? h : PlayerBar.Margin.Bottom < 0 ? h + _barShift.Y : 0;
        int run = ++_barRun;
        PlayerBar.RenderTransform = _barShift;
        PlayerBar.IsVisible = true;
        PlayerBar.IsHitTestVisible = show;
        void Done()
        {
            _barShift.Y = 0;
            PlayerBar.Margin = default;
            PlayerBar.IsVisible = show;
        }
        if (!Ui.Animations || h <= 0 || !IsVisible)
        {
            Done();
            return;
        }
        // (laid out below the window, drawn back up where it is)
        PlayerBar.Margin = new Thickness(0, 0, 0, -h);
        double from = down - h, to = show ? -h : 0;
        Ui.Tween(PlayerBar, AnimatedContent.Ms * Math.Abs(to - from) / h, Ui.CubicOut, t =>
        {
            if (run != _barRun) return;
            _barShift.Y = from + (to - from) * t;
            if (t >= 1) Done();
        });
    }

    // "<" and ">" slow down and speed up like on YouTube (by the character, so whatever key types them here).
    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (Vm is not { } vm || InText || vm.Page is DjViewModel || e.Text is not ("<" or ">")) return;
        e.Handled = true;
        var p = vm.Player;
        if (!p.CanSetSpeed)
        {
            vm.Toast(L.T("Only people with permission can change the speed (the host decides)"));
            return;
        }
        p.Speed += e.Text == ">" ? 0.25 : -0.25;
        vm.Toast(L.F("Speed: {0}", p.SpeedText));
    }

    private void Volume_Wheel(object? sender, PointerWheelEventArgs e)
    {
        if (Vm == null || e.Delta.Y == 0) return;
        Vm.Player.Volume += e.Delta.Y > 0 ? 0.05 : -0.05;
        e.Handled = true;
    }

    // The wheel on the speed button or its slider: 0.05 a notch.
    private void Speed_Wheel(object? sender, PointerWheelEventArgs e)
    {
        if (Vm?.Player is not { CanSetSpeed: true } p || e.Delta.Y == 0) return;
        p.Speed += e.Delta.Y > 0 ? 0.05 : -0.05;
        e.Handled = true;
    }

    private static EqBandViewModel? Band(object? source)
        => Ui.FindAncestor<Slider>(source as Visual) is { IsEnabled: true, Classes: var c } s && c.Contains("eqband") ? s.DataContext as EqBandViewModel : null;

    // On a narrow window the volume slider gives up some width, or goes (the wheel on the speaker still works), so the right
    // side of the player bar fits its column.
    private void FitRightBlock()
    {
        double column = PlayerGrid.ColumnDefinitions[2].ActualWidth;
        if (column <= 0) return;
        double others = 0;
        foreach (var child in RightBlock.Children)
            if (child != VolumeSlider && child.IsVisible)
                others += child.Bounds.Width + child.Margin.Left + child.Margin.Right;
        double room = column - others - VolumeSlider.Margin.Left - 2;
        VolumeSlider.IsVisible = room >= 44;
        double width = Math.Clamp(room, 44, 110);
        if (Math.Abs(VolumeSlider.Width - width) > 0.5) VolumeSlider.Width = width;
    }

    // ------------------------------------------------------------------ drag & drop

    private static List<string> FilesIn(IDataObject data)
    {
        try
        {
            return data.GetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? new List<string>();
        }
        catch { return new List<string>(); }
    }

    private static string? LinkFrom(IDataObject data)
    {
        var candidates = new List<string?>();
        try { candidates.Add(data.GetText()); } catch { }
        foreach (var fmt in new[] { "text/uri-list", "text/x-moz-url", "UniformResourceLocatorW", "public.url" })
        {
            try
            {
                if (!data.Contains(fmt)) continue;
                candidates.Add(data.Get(fmt) switch
                {
                    string s => s,
                    byte[] b => fmt == "UniformResourceLocatorW" || fmt == "text/x-moz-url" ? System.Text.Encoding.Unicode.GetString(b) : System.Text.Encoding.UTF8.GetString(b),
                    _ => null,
                });
            }
            catch { }
        }
        foreach (var c in candidates)
        {
            var s = c?.Trim('\0', ' ', '\r', '\n');
            if (string.IsNullOrEmpty(s)) continue;
            s = s.Split('\n')[0].Trim('\0', ' ', '\r');
            if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase) || s.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase)) return s;
        }
        return null;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.Handled) return;
        if (e.Data.Contains(Rows.TrackRowFormat) || e.Data.Contains(Rows.QueueRowFormat)) return;
        var files = FilesIn(e.Data);
        var packs = files.Where(Pack.IsPack).ToList();
        if (packs.Count > 0) ShowPackDrop(packs);
        e.DragEffects = files.Count > 0 || LinkFrom(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // ------------------------------------------------------------------ .ump packs dropped on the window

    private readonly DispatcherTimer _dropHide = new() { Interval = TimeSpan.FromMilliseconds(90) };

    private void ShowPackDrop(List<string> packs)
    {
        _dropHide.Stop();
        PackDropName.Text = packs.Count == 1 ? Path.GetFileNameWithoutExtension(packs[0]) : L.F("{0} packs", packs.Count);
        PackDrop.IsVisible = true;
    }

    private void HidePackDrop()
    {
        _dropHide.Stop();
        PackDrop.IsVisible = false;
    }

    // Before any part of the window: packs open the import, other files dropped along are added as usual.
    private void OnPackDrop(object? sender, DragEventArgs e)
    {
        HidePackDrop();
        if (Vm == null) return;
        var files = FilesIn(e.Data);
        var packs = files.Where(Pack.IsPack).ToList();
        if (packs.Count == 0) return;
        e.Handled = true;
        var rest = files.Where(f => !Pack.IsPack(f)).ToList();
        if (rest.Count > 0) _ = Vm.Import(rest, false);
        foreach (var p in packs) Vm.OpenPack(p);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.Handled || Vm == null) return;
        if (e.Data.Contains(Rows.TrackRowFormat) || e.Data.Contains(Rows.QueueRowFormat)) return;
        var files = FilesIn(e.Data);
        if (files.Count > 0)
        {
            _ = Vm.Import(files, Vm.Player.Current == null);
            e.Handled = true;
            return;
        }
        if (LinkFrom(e.Data) is { } link)
        {
            Vm.StartDownload(link);
            e.Handled = true;
        }
    }

    // The artist's name in the player bar: its page (which one, for a song of several).
    private void Artist_Click(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || sender is not Control { DataContext: PlayerViewModel p } c) return;
        if (Menus.ArtistsOfCurrent(p) is { } menu) Menus.Open(menu, c, false);
        e.Handled = true;
    }
}
