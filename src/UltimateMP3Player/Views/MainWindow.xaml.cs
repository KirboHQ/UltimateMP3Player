using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        PreviewKeyDown += OnKeyDown;
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
        if (!_host.OnWindowClosing()) e.Cancel = true;
    }

    // ------------------------------------------------------------------ keyboard

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm == null) return;
        bool inText = Keyboard.FocusedElement is TextBox;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.Space when !inText:
                vm.TogglePlay();
                e.Handled = true;
                break;
            // DJ "Tap BPM" tab: T taps along with the song playing there (or the deck tapped last).
            case Key.T when !inText && !e.IsRepeat && vm.Page is DjViewModel { TapMode: true } dj:
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
    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (Vm is not { } vm || Keyboard.FocusedElement is TextBox || vm.Page is DjViewModel || e.Text is not ("<" or ">")) return;
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

    private void Search_KeyDown(object sender, KeyEventArgs e)
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
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void Search_Focus(object sender, KeyboardFocusChangedEventArgs e)
    {
        bool focused = SearchBox.IsKeyboardFocused;
        SearchPill.BorderBrush = (Brush)FindResource(focused ? "AccentBrush" : "BorderBrush");
        SearchIcon.Foreground = (Brush)FindResource(focused ? "TextBrush" : "SubTextBrush");
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

    // On a narrow window the volume slider gives up some width, so the right side of the player bar fits its column.
    private void PlayerBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double column = PlayerGrid.ColumnDefinitions[2].ActualWidth;
        if (column <= 0) return;
        double others = 0;
        foreach (UIElement child in RightBlock.Children)
            if (child is FrameworkElement fe && fe != VolumeSlider && fe.Visibility == Visibility.Visible)
                others += fe.ActualWidth + fe.Margin.Left + fe.Margin.Right;
        double width = Math.Clamp(column - others - VolumeSlider.Margin.Left - 2, 56, 110);
        if (Math.Abs(VolumeSlider.Width - width) > 0.5) VolumeSlider.Width = width;
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
        PackDropName.Text = packs.Count == 1 ? Path.GetFileNameWithoutExtension(packs[0]) : L.F("{0} pacchetti", packs.Count);
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
}
