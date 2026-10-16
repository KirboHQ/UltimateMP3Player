using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using UltimateMP3Player.Core;
using UltimateMP3Player.Platform;

namespace UltimateMP3Player.Views;

// Settings › Download › "Sign in to sites": the sites the downloads can sign in to (SiteLogins), each with its state and
// "Sign in" / "Quit", another site by its address, the switch that uses them or not, "esci da tutti".
public static class LoginsSheet
{
    public static void Show()
    {
        if (App.Host.View?.Sheets is not { } sheets) return;
        var settings = App.Host.Settings;
        var body = new StackPanel { Margin = new Thickness(20, 4, 20, 18) };
        body.Children.Add(new TextBlock { Text = L.T("Sign in to sites"), FontSize = 20, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 8) });
        body.Children.Add(Ui.Text(L.T("Sign in to a site on its own page, inside the app, as you always do: downloads will use your account, for private, members-only or age-restricted content. Only the site sees your password."),
            "hint", new Thickness(0, 0, 0, 10)));

        var list = new StackPanel();
        var use = new CheckBox { Theme = Ui.Theme("Switch"), IsChecked = settings.UseCookies, Margin = new Thickness(0, 4, 0, 10) };
        var useText = new StackPanel();
        useText.Children.Add(new TextBlock { Text = L.T("Use my sign-ins for downloads"), Classes = { "setting" } });
        useText.Children.Add(new TextBlock { Text = L.T("Off, downloads go without an account even if you're signed in"), Classes = { "setting-hint" } });
        use.Content = useText;
        use.IsCheckedChanged += (_, _) =>
        {
            if (settings.UseCookies == (use.IsChecked == true)) return;
            settings.UseCookies = use.IsChecked == true;
            settings.Save();
            SiteLoginsChanged();
        };
        body.Children.Add(use);
        body.Children.Add(list);
        body.Children.Add(Ui.Text(L.T("For YouTube, a secondary account is better: Google may restrict accounts used for downloading."), "hint", new Thickness(0, 12, 0, 0)));

        void Fill()
        {
            list.Children.Clear();
            var signed = SiteLogins.SignedIn();
            foreach (var site in SiteLogins.Sites) list.Children.Add(Row(site, signed.Contains(site.Name)));
            var other = new Button { Theme = Ui.Theme("SheetItem"), Content = L.T("Another site…"), Margin = new Thickness(-14, 4, -14, 0) };
            Ui.SetGlyph(other, "");
            other.Click += async (_, _) =>
            {
                var address = await Dialogs.PromptAsync(L.T("Another site"), L.T("Address of the sign-in page"), "https://");
                if (string.IsNullOrWhiteSpace(address)) return;
                address = address.Trim();
                if (!address.Contains("://", StringComparison.Ordinal)) address = "https://" + address;
                if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                {
                    App.Host.Session?.Toast(L.T("Not a valid address."));
                    return;
                }
                await SiteLogins.OpenAsync(uri.ToString(), null);
            };
            list.Children.Add(other);
            if (SiteLogins.HasFile)
            {
                var all = new Button { Theme = Ui.Theme("SheetItem"), Content = L.T("Sign out of all sites"), Margin = new Thickness(-14, 0, -14, 0) };
                Ui.SetGlyph(all, "");
                all.Classes.Add("danger");
                all.Click += async (_, _) =>
                {
                    if (!await Dialogs.ConfirmAsync(L.T("Sign out of all sites?"), L.T("The app forgets the sign-ins made on the sites' pages; downloads will go without an account."), L.T("Sign out"), true))
                        return;
                    await SiteLogins.SignOutAllAsync();
                };
                list.Children.Add(all);
            }
        }

        void SiteLoginsChanged() => Fill();
        Fill();
        SiteLogins.Changed += SiteLoginsChanged;
        _ = sheets.Show(new ScrollViewer { Content = body }, maxHeightShare: 0.92).ContinueWith(_ => SiteLogins.Changed -= SiteLoginsChanged,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static Control Row(LoginSite site, bool signedIn)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 6) };
        grid.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(Color.Parse(site.Color)), VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { Margin = new Thickness(14, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = site.Name, FontSize = 15.5, FontWeight = FontWeight.SemiBold });
        var state = new TextBlock
        {
            Text = signedIn ? L.T("Signed in") : L.T("Not signed in"), FontSize = 13,
            Foreground = signedIn ? Ui.Res("SuccessBrush") : Ui.Res("MutedBrush"), Margin = new Thickness(0, 2, 0, 0),
        };
        text.Children.Add(state);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var action = new Button { Theme = Ui.Theme("SmallGhost"), Content = signedIn ? L.T("Sign out") : L.T("Sign in"), VerticalAlignment = VerticalAlignment.Center };
        action.Click += async (_, _) =>
        {
            if (!signedIn)
            {
                await SiteLogins.OpenAsync(site.Url, site.Name);
                return;
            }
            if (!await Dialogs.ConfirmAsync(L.F("Sign out of {0}?", site.Name), L.T("Downloads from this site will go without your account."), L.T("Sign out"), true)) return;
            await SiteLogins.SignOutAsync(site);
        };
        Grid.SetColumn(action, 2);
        grid.Children.Add(action);
        return grid;
    }
}
