using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class DownloadsPage : UserControl, IPage
{
    public DownloadsPage()
    {
        InitializeComponent();
        LinkBox.TextChanged += (_, _) => GoText.Text = (LinkBox.Text ?? "").Trim().Length > 0 ? L.T("Leggi") : L.T("Incolla");
        LinkBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Go_Click(null, new RoutedEventArgs());
        };
    }

    private DownloadsPageViewModel? Vm => DataContext as DownloadsPageViewModel;

    // Empty: what's in the clipboard; written: that link.
    private async void Go_Click(object? sender, RoutedEventArgs? e)
    {
        var text = (LinkBox.Text ?? "").Trim();
        if (text.Length == 0) text = (await Ui.ClipboardTextAsync())?.Trim() ?? "";
        if (text.Length == 0)
        {
            App.Host.Session?.Toast(L.T("Negli appunti non c'è nessun link: copialo dall'app o dal sito e riprova."));
            return;
        }
        LinkBox.Text = "";
        App.Host.Session?.StartDownload(text);
        Scroller.Offset = default;
    }

    private void Playlist_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Link is not { } link) return;
        var menu = new SheetMenu { Title = L.T("Salva nella playlist"), Subtitle = link.Title };
        foreach (var c in link.PlaylistChoices)
        {
            var choice = c;
            menu.Add(new SheetEntry(c.Label, c == link.Playlist ? "" : c.Hint == "♥" ? "" : "", () => link.Playlist = choice));
        }
        Menus.Open(menu);
    }

    private void Tags_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Link is not { } link || App.Host.Session is not { } main) return;
        Menus.Open(Menus.TagPicker(link.TagIds, main, link.OnTagsChosen));
    }

    // A tap on a song of the playlist ticks it (the whole row, not only the box).
    private void Item_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: LinkItemViewModel item } && Ui.FindAncestor<CheckBox>(e.Source as Avalonia.Visual) == null)
            item.IsSelected = !item.IsSelected;
    }

    // The link needs a login: the page of its site to sign in (then the link is read again), or the list of the sites.
    private async void Logins_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var url = vm.LastUrl;
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : "";
        var site = Platform.SiteLogins.Sites.FirstOrDefault(s => s.Domains.Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal)));
        if (site == null)
        {
            LoginsSheet.Show();
            return;
        }
        await Platform.SiteLogins.OpenAsync(site.Url, site.Name);
        if (Platform.SiteLogins.SignedIn().Contains(site.Name) && App.Host.Settings.CookiesBrowserOrNull != null && url.Length > 0)
            await vm.AnalyzeAsync(url);
    }

    public bool Back() => false;

    public void ScrollToTop() => Scroller.Offset = default;
}
