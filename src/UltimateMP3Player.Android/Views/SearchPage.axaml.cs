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
// there goes back to the page before, here the box is the page).
public partial class SearchPage : UserControl, IPage
{
    private SearchViewModel? _vm;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(220) };

    public SearchPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
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
        Update();
    }

    private bool HasQuery => Text.Trim().Length > 0 && !IsLink(Text);

    private void Update()
    {
        if (_vm == null) return;
        bool query = HasQuery;
        Intro.IsVisible = !query;
        Tabs.IsVisible = query;
        LocalList.IsVisible = query && _vm.LocalMode;
        OnlinePane.IsVisible = query && _vm.OnlineMode;
        if (!LocalList.IsVisible) return;
        var items = new List<object>();
        items.AddRange(_vm.PlaylistResults);
        items.AddRange(_vm.Rows);
        if (items.Count == 0) items.Add(new ListNote(L.T("Nessun risultato"), _vm.LocalEmptyHint));
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
        if (string.IsNullOrEmpty(text)) return;
        Box.Text = text;
        if (IsLink(text)) Download_Click(null, new RoutedEventArgs());
    }

    private void Download_Click(object? sender, RoutedEventArgs? e)
    {
        var url = Text.Trim();
        if (url.Length == 0) return;
        Box.Text = "";
        App.Host.Session?.StartDownload(url);
    }

    public bool Back()
    {
        if (Text.Length == 0) return false;
        Box.Text = "";
        return true;
    }

    public void ScrollToTop() => Box.Focus();
}
