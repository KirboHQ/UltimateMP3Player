using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using UltimateMP3Player.Core;
using UltimateMP3Player.Platform;

namespace UltimateMP3Player.Views;

// Settings › Download › "Accessi ai siti": the sites the downloads can sign in to (SiteLogins), each with its state and
// "Accedi" / "Esci", another site by its address, the switch that uses them or not, "esci da tutti".
public static class LoginsSheet
{
    public static void Show()
    {
        if (App.Host.View?.Sheets is not { } sheets) return;
        var settings = App.Host.Settings;
        var body = new StackPanel { Margin = new Thickness(20, 4, 20, 18) };
        body.Children.Add(new TextBlock { Text = L.T("Accessi ai siti"), FontSize = 20, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 8) });
        body.Children.Add(Ui.Text(L.T("Accedi a un sito nella sua pagina, dentro l'app, come fai di solito: i download useranno il tuo account, per i contenuti privati, per abbonati o con limite d'età. La password la vede solo il sito."),
            "hint", new Thickness(0, 0, 0, 10)));

        var list = new StackPanel();
        var use = new CheckBox { Theme = Ui.Theme("Switch"), IsChecked = settings.UseCookies, Margin = new Thickness(0, 4, 0, 10) };
        var useText = new StackPanel();
        useText.Children.Add(new TextBlock { Text = L.T("Usa i miei accessi per scaricare"), Classes = { "setting" } });
        useText.Children.Add(new TextBlock { Text = L.T("Spento, i download vanno senza account anche se hai fatto l'accesso"), Classes = { "setting-hint" } });
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
        body.Children.Add(Ui.Text(L.T("Per YouTube meglio un account secondario: Google può limitare gli account usati per scaricare."), "hint", new Thickness(0, 12, 0, 0)));

        void Fill()
        {
            list.Children.Clear();
            var signed = SiteLogins.SignedIn();
            foreach (var site in SiteLogins.Sites) list.Children.Add(Row(site, signed.Contains(site.Name)));
            var other = new Button { Theme = Ui.Theme("SheetItem"), Content = L.T("Un altro sito…"), Margin = new Thickness(-14, 4, -14, 0) };
            Ui.SetGlyph(other, "");
            other.Click += async (_, _) =>
            {
                var address = await Dialogs.PromptAsync(L.T("Un altro sito"), L.T("Indirizzo della pagina in cui accedere"), "https://");
                if (string.IsNullOrWhiteSpace(address)) return;
                address = address.Trim();
                if (!address.Contains("://", StringComparison.Ordinal)) address = "https://" + address;
                if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                {
                    App.Host.Session?.Toast(L.T("Indirizzo non valido."));
                    return;
                }
                await SiteLogins.OpenAsync(uri.ToString(), null);
            };
            list.Children.Add(other);
            if (SiteLogins.HasFile)
            {
                var all = new Button { Theme = Ui.Theme("SheetItem"), Content = L.T("Esci da tutti i siti"), Margin = new Thickness(-14, 0, -14, 0) };
                Ui.SetGlyph(all, "");
                all.Classes.Add("danger");
                all.Click += async (_, _) =>
                {
                    if (!await Dialogs.ConfirmAsync(L.T("Esci da tutti i siti?"), L.T("L'app dimentica gli accessi fatti nelle pagine dei siti; i download andranno senza account."), L.T("Disconnetti"), true))
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
            Text = signedIn ? L.T("Accesso fatto") : L.T("Accesso non fatto"), FontSize = 13,
            Foreground = signedIn ? Ui.Res("SuccessBrush") : Ui.Res("MutedBrush"), Margin = new Thickness(0, 2, 0, 0),
        };
        text.Children.Add(state);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var action = new Button { Theme = Ui.Theme("SmallGhost"), Content = signedIn ? L.T("Disconnetti") : L.T("Accedi"), VerticalAlignment = VerticalAlignment.Center };
        action.Click += async (_, _) =>
        {
            if (!signedIn)
            {
                await SiteLogins.OpenAsync(site.Url, site.Name);
                return;
            }
            if (!await Dialogs.ConfirmAsync(L.F("Esci da {0}?", site.Name), L.T("I download da questo sito andranno senza il tuo account."), L.T("Disconnetti"), true)) return;
            await SiteLogins.SignOutAsync(site);
        };
        Grid.SetColumn(action, 2);
        grid.Children.Add(action);
        return grid;
    }
}
