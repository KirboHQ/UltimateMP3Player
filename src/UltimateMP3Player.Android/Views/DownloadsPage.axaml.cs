using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// Not a page of its own: the lower part of the search page (SearchPage), shown while its box is empty or a link is open.
public partial class DownloadsPage : UserControl
{
    private DownloadQueue? _queue;

    // Choosing several downloads.
    public Selection Picks { get; } = new();

    public DownloadsPage()
    {
        InitializeComponent();
        Selection.SetOwner(JobList, Picks);
        DataContextChanged += (_, _) => Attach();
    }

    private DownloadsPageViewModel? Vm => DataContext as DownloadsPageViewModel;

    private void Attach()
    {
        if (_queue != null) _queue.Jobs.CollectionChanged -= OnJobs;
        _queue = Vm?.Queue;
        if (_queue != null) _queue.Jobs.CollectionChanged += OnJobs;
        OnJobs(null, null);
    }

    private void OnJobs(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs? e) =>
        Picks.SetItems(_queue?.Jobs ?? (IEnumerable<object>)Array.Empty<object>());

    private void Playlists_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Link is { } link) Menus.Open(Menus.PlaylistPicker(link));
    }

    // The audio or video quality of the songs of the card.
    private void Quality_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Link is not { } link || sender is not Control { Tag: string what }) return;
        bool audio = what == "audio";
        var menu = new SheetMenu { Title = audio ? L.T("Audio quality") : L.T("Maximum video quality"), Subtitle = link.Title };
        foreach (var c in audio ? link.AudioChoices : link.VideoChoices)
        {
            var choice = c;
            var text = string.IsNullOrEmpty(c.Hint) ? c.Label : $"{c.Label}  ·  {c.Hint}";
            menu.Add(new SheetEntry(text, c == (audio ? link.Audio : link.Video) ? "" : "", () =>
            {
                if (audio) link.Audio = choice;
                else link.Video = choice;
            }));
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
        if (await SignIn(url) && url.Length > 0) await vm.AnalyzeAsync(url);
    }

    // A download that failed for the same reason: the same, then it's tried again.
    private async void JobLogin_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: DownloadJobViewModel job }) return;
        if (await SignIn(job.LoginUrl) && job.CanRetry) job.RetryCommand.Execute(null);
    }

    // The page of the link's site to sign in, or the list of the sites when it isn't one of them. True once signed in.
    private static async Task<bool> SignIn(string url)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : "";
        var site = Platform.SiteLogins.Sites.FirstOrDefault(s => s.Domains.Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal)));
        if (site == null)
        {
            LoginsSheet.Show();
            return false;
        }
        await Platform.SiteLogins.OpenAsync(site.Url, site.Name);
        return Platform.SiteLogins.SignedIn().Contains(site.Name) && App.Host.Settings.CookiesBrowserOrNull != null;
    }

    public bool Back()
    {
        if (!Picks.IsActive) return false;
        Picks.Stop();
        return true;
    }

    public void ScrollToTop() => Scroller.Offset = default;
}
