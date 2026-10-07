using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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
        WindowFrame.Apply(this, Root, TitleBar, TitleText, WindowButtons, MinButton, MaxButton, CloseButton, ResizeEdges);
        var s = host.Settings;
        var screen = Screens.Primary?.WorkingArea.Size.ToSize(Screens.Primary.Scaling);
        Width = Math.Clamp(s.WindowWidth, MinWidth, Math.Max(MinWidth, screen?.Width ?? s.WindowWidth));
        Height = Math.Clamp(s.WindowHeight, MinHeight, Math.Max(MinHeight, screen?.Height ?? s.WindowHeight));
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
        Closing += OnClosing;
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty) return;
            Ui.Hidden = WindowState == WindowState.Minimized || !IsVisible;
            Vm?.Player.SetVisible(WindowState != WindowState.Minimized);
            if (WindowState != WindowState.Minimized) s.WindowMaximized = WindowState == WindowState.Maximized;
        };
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
        Opened += (_, _) => _host.OnTrackChanged();

        SearchBox.GotFocus += (_, _) => SearchFocus();
        SearchBox.LostFocus += (_, _) => SearchFocus();
        SearchBox.AddHandler(KeyDownEvent, Search_KeyDown, RoutingStrategies.Tunnel);
        VolumeSlider.AddHandler(PointerWheelChangedEvent, Volume_Wheel, RoutingStrategies.Tunnel);
        SpeedButton.AddHandler(PointerWheelChangedEvent, Speed_Wheel, RoutingStrategies.Tunnel);
        SpeedSlider.AddHandler(PointerWheelChangedEvent, Speed_Wheel, RoutingStrategies.Tunnel);
        PlayerGrid.SizeChanged += (_, _) => FitRightBlock();
        SpeedButton.SizeChanged += (_, _) => FitRightBlock();
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

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        bool repeat = !_down.Add(e.Key);
        var vm = Vm;
        if (vm == null) return;
        bool inText = InText;
        bool ctrl = (e.KeyModifiers & Command) != 0;
        switch (e.Key)
        {
            case Key.Space when !inText:
                vm.TogglePlay();
                e.Handled = true;
                break;
            // DJ "Tap BPM" tab: T taps along with the song playing there (or the deck tapped last).
            case Key.T when !inText && !repeat && vm.Page is DjViewModel { TapMode: true } dj:
                dj.TapKey();
                e.Handled = true;
                break;
            case Key.Right when ctrl:
                vm.Player.NextCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Left when ctrl:
                vm.Player.PreviousCommand.Execute(null);
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
            case Key.Up when ctrl:
                vm.Player.Volume += 0.05;
                e.Handled = true;
                break;
            case Key.Down when ctrl:
                vm.Player.Volume -= 0.05;
                e.Handled = true;
                break;
            case Key.F when ctrl:
            case Key.L when ctrl:
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
                break;
            case Key.V when ctrl && !inText:
                vm.PasteLinkCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Back when !inText:
            case Key.BrowserBack:
                if (vm.BackCommand.CanExecute(null)) vm.BackCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    // "<" and ">" slow down and speed up like on YouTube (by the character, so whatever key types them here).
    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (Vm is not { } vm || InText || vm.Page is DjViewModel || e.Text is not ("<" or ">")) return;
        e.Handled = true;
        var p = vm.Player;
        if (!p.CanSetSpeed)
        {
            vm.Toast(L.T("Solo chi ha il permesso può cambiare la velocità (lo decide l'host)"));
            return;
        }
        p.Speed += e.Text == ">" ? 0.25 : -0.25;
        vm.Toast(L.F("Velocità: {0}", p.SpeedText));
    }

    private void Search_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm == null) return;
        if (e.Key == Key.Enter)
        {
            Vm.SubmitSearchCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Vm.SearchText = "";
            FocusManager?.ClearFocus();
            e.Handled = true;
        }
    }

    private void SearchFocus()
    {
        bool focused = SearchBox.IsFocused;
        SearchPill.BorderBrush = Ui.Res(focused ? "AccentBrush" : "BorderBrush");
        SearchIcon.Foreground = Ui.Res(focused ? "TextBrush" : "SubTextBrush");
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

    // On a narrow window the volume slider gives up some width, so the right side of the player bar fits its column.
    private void FitRightBlock()
    {
        double column = PlayerGrid.ColumnDefinitions[2].ActualWidth;
        if (column <= 0) return;
        double others = 0;
        foreach (var child in RightBlock.Children)
            if (child != VolumeSlider && child.IsVisible)
                others += child.Bounds.Width + child.Margin.Left + child.Margin.Right;
        double width = Math.Clamp(column - others - VolumeSlider.Margin.Left - 2, 56, 110);
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
        PackDropName.Text = packs.Count == 1 ? Path.GetFileNameWithoutExtension(packs[0]) : L.F("{0} pacchetti", packs.Count);
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
}
