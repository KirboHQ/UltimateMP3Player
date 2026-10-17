using System.ComponentModel;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// The search box drives the search view model itself (the computer apps' box is in the window's top bar: emptying it
// there goes back to the page before, here the box is the page). The download page is shown here too (its part under the
// box): MainView shows this page for it.
public partial class SearchPage : UserControl, IPage
{
    private SearchViewModel? _vm;
    private MainViewModel? _main;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(220) };

    // Choosing several of the songs (or playlists) found.
    public Selection Picks { get; } = new();

    public SearchPage()
    {
        InitializeComponent();
        Selection.SetOwner(LocalList, Picks);
        DataContextChanged += (_, _) => Attach();
        AttachedToVisualTree += (_, _) => Watch(App.Host.Session);
        DetachedFromVisualTree += (_, _) => Watch(null);
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Run(false);
        };
        Box.TextChanged += (_, _) =>
        {
            var text = Text;
            ClearButton.IsVisible = text.Length > 0;
            DownloadLink.IsVisible = IsLink(text);
            _timer.Stop();
            if (text.Trim().Length == 0) Run(false);
            else _timer.Start();
        };
        Box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (IsLink(Text)) Download_Click(null, new RoutedEventArgs());
            else Run(true);
        };
    }

    private string Text => Box.Text ?? "";

    private void Attach()
    {
        if (_vm != null) _vm.PropertyChanged -= OnVm;
        _vm = DataContext as SearchViewModel;
        if (_vm != null) _vm.PropertyChanged += OnVm;
        Update();
    }

    private void Watch(MainViewModel? main)
    {
        if (_main != null) _main.PropertyChanged -= OnMain;
        _main = main;
        if (_main != null) _main.PropertyChanged += OnMain;
        Update();
    }

    private void OnMain(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.Page)) return;
        // (a song found online tapped while the words were still waiting to be searched)
        if (OnDownloads) _timer.Stop();
        Update();
    }

    // The download page open: a link, or a song found online (the words searched stay in the box, Back goes to the results).
    private bool OnDownloads => _main?.Page is DownloadsPageViewModel;

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SearchViewModel.Rows) or nameof(SearchViewModel.PlaylistResults) or nameof(SearchViewModel.OnlineMode)
            or nameof(SearchViewModel.LocalMode) or nameof(SearchViewModel.OnlineEnabled))
            Update();
    }

    private static bool IsLink(string s)
    {
        s = s.Trim();
        return Regex.IsMatch(s, @"^(https?://|spotify:|www\.)\S+$", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(s, @"^[\w-]+(\.[\w-]+)*\.(com|it|be|tv|net|org|app|co|link|fm|me|ly|gl|to|cc)/\S*$", RegexOptions.IgnoreCase);
    }

    private void Run(bool now)
    {
        if (_vm == null) return;
        var text = Text.Trim();
        if (text.Length == 0 || IsLink(text))
        {
            _vm.Reset();
            Update();
            return;
        }
        _vm.Run(text);
        if (now) _vm.RunOnlineNow();
        // Written while a link is open: its results, like the computer's box.
        if (OnDownloads) _main!.Navigate(_vm);
        Update();
    }

    // Results on the page (not the download part).
    public bool HasQuery => Text.Trim().Length > 0 && !IsLink(Text) && !OnDownloads;

    private void Update()
    {
        if (_vm == null) return;
        bool query = HasQuery;
        DownloadsPart.IsVisible = !query;
        if (query) DownloadsPart.Picks.Stop();
        Tabs.IsVisible = query;
        LocalList.IsVisible = query && _vm.LocalMode;
        OnlinePane.IsVisible = query && _vm.OnlineMode;
        if (!LocalList.IsVisible)
        {
            Picks.Stop();
            return;
        }
        var items = new List<object>();
        items.AddRange(_vm.PlaylistResults);
        items.AddRange(_vm.Rows);
        Picks.SetItems(items, _vm);
        if (items.Count == 0) items.Add(new ListNote(L.T("No results"), _vm.LocalEmptyHint));
        LocalList.ItemsSource = items;
    }

    private void Clear_Click(object? sender, RoutedEventArgs e)
    {
        Box.Text = "";
        Box.Focus();
    }

    private async void Paste_Click(object? sender, RoutedEventArgs e)
    {
        var text = (await Ui.ClipboardTextAsync())?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            _main?.Toast(L.T("There's no link in the clipboard: copy it from the app or the site and try again."));
            return;
        }
        Box.Text = text;
        if (IsLink(text)) Download_Click(null, new RoutedEventArgs());
    }

    private void Download_Click(object? sender, RoutedEventArgs? e)
    {
        var url = Text.Trim();
        if (url.Length == 0) return;
        Box.Text = "";
        App.Host.Session?.StartDownload(url);
        DownloadsPart.ScrollToTop();
    }

    public bool Back()
    {
        if (Picks.IsActive)
        {
            Picks.Stop();
            return true;
        }
        if (DownloadsPart.IsVisible && DownloadsPart.Back()) return true;
        // (back to the results, or to the page before)
        if (OnDownloads || Text.Length == 0) return false;
        Box.Text = "";
        return true;
    }

    // A tap on the tab already open: back to the top. The keyboard only opens with a tap on the box.
    public void ScrollToTop()
    {
        if (DownloadsPart.IsVisible) DownloadsPart.ScrollToTop();
        else if (LocalList.ItemCount > 0) LocalList.ScrollIntoView(0);
    }
}
