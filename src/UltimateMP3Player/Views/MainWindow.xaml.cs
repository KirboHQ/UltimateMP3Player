using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UltimateMP3Player.Core;
using UltimateMP3Player.Services;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class MainWindow : Window
{
    private readonly AppHost _host;
    private bool _silent;

    public MainWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        WindowFrame.Apply(this, Root, MinButton, MaxButton, CloseButton);
        SmoothScroll.Attach(this);
        // Two fingers sideways on the touchpad over a song's row: the row is pulled (into the queue).
        RowSwipe.Attach(this);
        PhoneLinkViewModel.ShowWindow = PhoneDialogs.Show;
        var s = host.Settings;
        Width = Math.Clamp(s.WindowWidth, MinWidth, SystemParameters.VirtualScreenWidth);
        Height = Math.Clamp(s.WindowHeight, MinHeight, SystemParameters.VirtualScreenHeight);
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
        Closing += OnClosing;
        StateChanged += (_, _) =>
        {
            Vm?.Player.SetVisible(WindowState != WindowState.Minimized);
            if (WindowState != WindowState.Minimized) s.WindowMaximized = WindowState == WindowState.Maximized;
        };
        SetBinding(BarShownProperty, nameof(MainViewModel.ShowPlayerBar));
        PreviewKeyDown += OnKeyDown;
        PreviewMouseUp += OnMouseSideButton;
        PreviewMouseDown += FoldQueue;
        PreviewTextInput += OnTextInput;
        PreviewMouseLeftButtonDown += (_, e) => SelectionBar.ClearOnOutsideClick(e.OriginalSource as DependencyObject);
        PreviewDragOver += OnDragOver;
        // Leaving one element for another is a leave followed by an over: only a real exit hides the pack overlay.
        PreviewDragLeave += (_, _) => _dropHide.Start();
        _dropHide.Tick += (_, _) => HidePackDrop();
        PreviewDrop += OnPackDrop;
        Drop += OnDrop;
        Loaded += (_, _) => _host.OnTrackChanged();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // Replaced by a new window (language change): no tray logic.
    public void CloseSilently()
    {
        _silent = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var s = _host.Settings;
        if (WindowState == WindowState.Normal)
        {
            s.WindowWidth = ActualWidth;
            s.WindowHeight = ActualHeight;
        }
        s.WindowMaximized = WindowState == WindowState.Maximized;
        s.Save();
        if (_silent) return;
        IsClosing = true;
        if (!_host.OnWindowClosing()) e.Cancel = true;
        IsClosing = !e.Cancel;
    }

    // Closing by itself: quitting the app from here mustn't close it a second time (WPF refuses it).
    public bool IsClosing { get; private set; }

    // ------------------------------------------------------------------ keyboard

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm == null) return;
        bool inText = Keyboard.FocusedElement is TextBox;
        // (Ctrl+↑/↓ go on from a one-line box, Search's: there they have nothing to move, and the box takes the keys as the page opens)
        bool inLines = Keyboard.FocusedElement is TextBox { AcceptsReturn: true };
        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0;
        // Alt+←/→: back and forward, like a browser (WPF hands Alt+key over as a system key).
        if (e.Key == Key.System && mods == ModifierKeys.Alt && e.SystemKey is Key.Left or Key.Right)
        {
            Step(vm, e.SystemKey == Key.Right);
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
            case Key.Escape when vm.QueueOpen:
                vm.QueueOpen = false;
                e.Handled = true;
                break;
            case Key.Space when !inText:
                vm.TogglePlay();
                e.Handled = true;
                break;
            // DJ "Tap BPM" tab: T taps along with the song playing there (or the deck tapped last).
            case Key.T when !inText && !e.IsRepeat && vm.Page is DjViewModel { TapMode: true } dj:
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
            case Key.Up when ctrl && !shift && !inLines:
            case Key.Down when ctrl && !shift && !inLines:
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

    // Up next popped out over the page: a click anywhere else folds it (its own button toggles it; menus opened from it are
    // windows of their own, not this one).
    private void FoldQueue(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { QueueOpen: true } vm || e.OriginalSource is not Visual v || PresentationSource.FromVisual(v)?.RootVisual != this) return;
        for (DependencyObject? d = v; d != null; d = VisualTreeHelper.GetParent(d))
            if (d == QueuePanel || d == QueueButton) return;
        vm.QueueOpen = false;
    }

    // ------------------------------------------------------------------ the player bar, away for the DJ

    private static readonly DependencyProperty BarShownProperty = DependencyProperty.Register("BarShown", typeof(bool), typeof(MainWindow),
        new PropertyMetadata(true, (d, e) => ((MainWindow)d).SlideBar((bool)e.NewValue)));

    private readonly TranslateTransform _barShift = new();
    private double _barHeight;

    // The page gets the bar's room at once (under its own slide: AnimatedContent), and the bar goes down out of the window over
    // it; coming back it rises over the page and only then takes its room again. The page is laid out once, not on every frame.
    private void SlideBar(bool show)
    {
        if (PlayerBar.Visibility == Visibility.Visible && PlayerBar.Margin.Bottom == 0) _barHeight = PlayerBar.ActualHeight;
        double h = _barHeight;
        // (how far down it is now: 0 in its place, h out of the window; halfway when it's turned back)
        double down = PlayerBar.Visibility != Visibility.Visible ? h : PlayerBar.Margin.Bottom < 0 ? h + _barShift.Y : 0;
        _barShift.BeginAnimation(TranslateTransform.YProperty, null);
        PlayerBar.RenderTransform = _barShift;
        PlayerBar.Visibility = Visibility.Visible;
        PlayerBar.IsHitTestVisible = show;
        void Done()
        {
            _barShift.BeginAnimation(TranslateTransform.YProperty, null);
            _barShift.Y = 0;
            PlayerBar.Margin = new Thickness(0);
            if (!show) PlayerBar.Visibility = Visibility.Collapsed;
        }
        if (!Ui.Animations || h <= 0 || !IsVisible)
        {
            Done();
            return;
        }
        // (laid out below the window, drawn back up where it is)
        PlayerBar.Margin = new Thickness(0, 0, 0, -h);
        double from = down - h, to = show ? -h : 0;
        var slide = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(AnimatedContent.Ms * Math.Abs(to - from) / h))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        slide.Completed += (_, _) => { if ((bool)GetValue(BarShownProperty) == show) Done(); };
        _barShift.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    // The mouse's side buttons: back and forward.
    private void OnMouseSideButton(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm || e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2)) return;
        Step(vm, e.ChangedButton == MouseButton.XButton2);
        e.Handled = true;
    }

    // "<" and ">" slow down and speed up like on YouTube (by the character, so whatever key types them here).
    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (Vm is not { } vm || Keyboard.FocusedElement is TextBox || vm.Page is DjViewModel || e.Text is not ("<" or ">")) return;
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

    private void Volume_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (Vm == null) return;
        Vm.Player.Volume += e.Delta > 0 ? 0.05 : -0.05;
        e.Handled = true;
    }

    // The wheel on the speed button or its slider: 0.05 a notch.
    private void Speed_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (Vm?.Player is not { CanSetSpeed: true } p) return;
        p.Speed += e.Delta > 0 ? 0.05 : -0.05;
        e.Handled = true;
    }

    // On a narrow window the volume slider gives up some width, or goes (the wheel on the speaker still works), so the right
    // side of the player bar fits its column.
    private void PlayerBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double column = PlayerGrid.ColumnDefinitions[2].ActualWidth;
        if (column <= 0) return;
        double others = 0;
        foreach (UIElement child in RightBlock.Children)
            if (child is FrameworkElement fe && fe != VolumeSlider && fe.Visibility == Visibility.Visible)
                others += fe.ActualWidth + fe.Margin.Left + fe.Margin.Right;
        double room = column - others - VolumeSlider.Margin.Left - 2;
        VolumeSlider.Visibility = room < 44 ? Visibility.Collapsed : Visibility.Visible;
        double width = Math.Clamp(room, 44, 110);
        if (Math.Abs(VolumeSlider.Width - width) > 0.5) VolumeSlider.Width = width;
    }

    // The playback panel's equalizer, like the one in Settings: the wheel moves a band by half a dB, a double click resets it.
    private void Eq_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Slider { IsEnabled: true } s || s.DataContext is not EqBandViewModel band) return;
        band.Gain += e.Delta > 0 ? 0.5 : -0.5;
        e.Handled = true;
    }

    private void Eq_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider { IsEnabled: true } s && s.DataContext is EqBandViewModel band) band.Gain = 0;
    }

    private void PlaybackSettings_Click(object sender, RoutedEventArgs e)
    {
        PlaybackPopup.IsOpen = false;
        Vm?.GoSettings();
    }

    private void AddCurrentToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (Vm?.Player.Current is not { } t) return;
        Menus.Open(Menus.AddToPlaylistMenu(t), (UIElement)sender, false);
    }

    // Right click on the song in the player bar: the usual song menu.
    private void Current_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm?.Player.Current is not { } t) return;
        Menus.Open(Menus.ForTrack(t, null), (UIElement)sender, false);
        e.Handled = true;
    }

    private void PlaylistList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlaylistList.SelectedItem is PlaylistViewModel p) Vm?.OpenPlaylist(p);
        PlaylistList.SelectedItem = null;
    }

    private void Playlist_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PlaylistViewModel p) return;
        Menus.Open(Menus.ForPlaylist(p), (UIElement)sender, false);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ drag & drop

    private void Playlist_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(TrackRow)) && sender is Border b)
        {
            e.Effects = DragDropEffects.Copy;
            b.BorderBrush = (Brush)FindResource("AccentBrush");
            b.BorderThickness = new Thickness(1);
            e.Handled = true;
        }
    }

    private void Playlist_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.BorderThickness = new Thickness(0);
    }

    private void Playlist_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.BorderThickness = new Thickness(0);
        if (e.Data.GetData(typeof(TrackRow)) is not TrackRow row || (sender as FrameworkElement)?.DataContext is not PlaylistViewModel p) return;
        row.Track.Main.AddToPlaylist(row.Track, p.P);
        e.Handled = true;
    }

    // A song dropped on a sidebar tag gets that tag.
    private void Tag_Drop(object sender, DragEventArgs e)
    {
        if (sender is Border b) b.BorderThickness = new Thickness(0);
        if (e.Data.GetData(typeof(TrackRow)) is not TrackRow row || (sender as FrameworkElement)?.DataContext is not TagViewModel t) return;
        row.Track.Main.SetTag(new[] { row.Track }, t, true);
        e.Handled = true;
    }

    private void Tag_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TagViewModel t) return;
        Menus.Open(Menus.ForTag(t), (UIElement)sender, false);
        e.Handled = true;
    }

    private static string? LinkFrom(IDataObject data)
    {
        foreach (var fmt in new[] { "UniformResourceLocatorW", DataFormats.UnicodeText, DataFormats.Text })
        {
            try
            {
                if (!data.GetDataPresent(fmt)) continue;
                string? s = data.GetData(fmt) switch
                {
                    string str => str,
                    MemoryStream ms => fmt == "UniformResourceLocatorW"
                        ? System.Text.Encoding.Unicode.GetString(ms.ToArray()).TrimEnd('\0')
                        : System.Text.Encoding.Default.GetString(ms.ToArray()).TrimEnd('\0'),
                    _ => null,
                };
                s = s?.Trim();
                if (!string.IsNullOrEmpty(s) && (s.StartsWith("http", StringComparison.OrdinalIgnoreCase) || s.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase)))
                    return s.Split('\n')[0].Trim();
            }
            catch { }
        }
        return null;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        if (e.Data.GetDataPresent(typeof(TrackRow)) || e.Data.GetDataPresent(typeof(QueueRow))) return;
        if (PacksIn(e.Data) is { Count: > 0 } packs) ShowPackDrop(packs);
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || LinkFrom(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // ------------------------------------------------------------------ .ump packs dropped on the window

    private readonly System.Windows.Threading.DispatcherTimer _dropHide = new() { Interval = TimeSpan.FromMilliseconds(90) };

    private static List<string>? PacksIn(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files
                ? files.Where(Pack.IsPack).ToList()
                : null;
        }
        catch { return null; }
    }

    private void ShowPackDrop(List<string> packs)
    {
        _dropHide.Stop();
        PackDropName.Text = packs.Count == 1 ? Path.GetFileNameWithoutExtension(packs[0]) : L.F("{0} packs", packs.Count);
        PackDrop.Visibility = Visibility.Visible;
    }

    private void HidePackDrop()
    {
        _dropHide.Stop();
        PackDrop.Visibility = Visibility.Collapsed;
    }

    // Before any part of the window: packs open the import, other files dropped along are added as usual.
    private void OnPackDrop(object sender, DragEventArgs e)
    {
        HidePackDrop();
        if (Vm == null || PacksIn(e.Data) is not { Count: > 0 } packs) return;
        e.Handled = true;
        var rest = ((string[])e.Data.GetData(DataFormats.FileDrop)).Where(f => !Pack.IsPack(f)).ToList();
        if (rest.Count > 0) _ = Vm.Import(rest, false);
        foreach (var p in packs) Vm.OpenPack(p);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Handled || Vm == null) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
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
    private void Artist_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PlayerViewModel p) return;
        if (Menus.ArtistsOfCurrent(p) is { } menu) Menus.Open(menu, (UIElement)sender, false);
        e.Handled = true;
    }
}
