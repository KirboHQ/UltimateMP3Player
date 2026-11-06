#if AVALONIA
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using DialogKind = UltimateMP3Player.DialogWindow;
using ElementKind = Avalonia.Controls.Control;
#else
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DialogKind = System.Windows.Window;
using ElementKind = System.Windows.FrameworkElement;
#endif
using UltimateMP3Player.Core;
using UltimateMP3Player.Platform;

namespace UltimateMP3Player;

// Settings › Download › "Sign in to sites" on the computers (the phones' LoginsSheet): each site with its state and "Sign in" / "Sign out",
// another site by its address, "sign out of all". Linked into the Avalonia app.
public static partial class Dialogs
{
    public static void ShowLogins() => LoginsWindow().ShowDialog();

    public static DialogKind LoginsWindow()
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = L.T("Sign in to each site on its own page, in a separate window of your browser: downloads will use your account, for private, members-only or age-restricted content. Only the site sees your password, and that window leaves nothing behind."),
            TextWrapping = TextWrapping.Wrap, Foreground = OverlayBrush("SubTextBrush"), LineHeight = 20,
        });
        var rows = new StackPanel();
        body.Children.Add(new ScrollViewer
        {
            Content = rows, MaxHeight = Math.Min(440, OverlayRoom().Height - 340), Margin = new Thickness(0, 10, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        var more = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        body.Children.Add(more);
        body.Children.Add(OverlayHint("For YouTube, a secondary account is better: Google may restrict accounts used for downloading."));

        void Fill()
        {
            var signed = SiteLogins.SignedIn();
            rows.Children.Clear();
            foreach (var site in SiteLogins.Sites) rows.Children.Add(LoginRow(site, signed.Contains(site.Name)));
            more.Children.Clear();
            var other = Button(L.T("Another site…"), "GhostButton");
            other.Click += (_, _) => SignInElsewhere();
            more.Children.Add(other);
            if (!SiteLogins.HasFile) return;
            var all = Button(L.T("Sign out of all sites"), "GhostButton");
            all.Margin = new Thickness(8, 0, 0, 0);
            all.Click += (_, _) =>
            {
                if (Confirm(L.T("Sign out of all sites?"), L.T("The app forgets the sign-ins made on the sites' pages; downloads will go without an account."), L.T("Sign out"), true))
                    SiteLogins.SignOutAll();
            };
            more.Children.Add(all);
        }

        Fill();
        SiteLogins.Changed += Fill;
        var win = Frame(L.T("Sign in to sites"), body, 520, Button(L.T("Done"), "PrimaryButton", isDefault: true, isCancel: true));
        win.Closed += (_, _) => SiteLogins.Changed -= Fill;
        return win;
    }

    private static ElementKind LoginRow(LoginSite site, bool signedIn)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 12, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
#if AVALONIA
        var color = Color.Parse(site.Color);
#else
        var color = (Color)ColorConverter.ConvertFromString(site.Color);
#endif
        grid.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { Margin = new Thickness(14, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = site.Name, FontSize = 14.5, FontWeight = FontWeights_SemiBold });
        text.Children.Add(new TextBlock
        {
            Text = signedIn ? L.T("Signed in") : L.T("Not signed in"), FontSize = 12.5, Margin = new Thickness(0, 2, 0, 0),
            Foreground = OverlayBrush(signedIn ? "SuccessBrush" : "MutedBrush"),
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var action = Button(signedIn ? L.T("Sign out") : L.T("Sign in"), "GhostButton");
        action.VerticalAlignment = VerticalAlignment.Center;
        action.Click += (_, _) =>
        {
            if (!signedIn) SignIn(site.Url, site);
            else if (Confirm(L.F("Sign out of {0}?", site.Name), L.T("Downloads from this site will go without your account."), L.T("Sign out"), true)) SiteLogins.SignOut(site);
        };
        Grid.SetColumn(action, 2);
        grid.Children.Add(action);
        return grid;
    }

    private static void SignInElsewhere()
    {
        var address = Prompt(L.T("Another site"), L.T("Address of the sign-in page"), "https://")?.Trim();
        if (string.IsNullOrWhiteSpace(address)) return;
        if (!address.Contains("://", StringComparison.Ordinal)) address = "https://" + address;
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Contains('.')) SignIn(uri.ToString(), null);
        else Alert(L.T("Another site"), L.T("Not a valid address."));
    }

    // The window on the site's page, this dialog while it's open: it goes by itself once the window is closed and its cookies are saved.
    private static void SignIn(string url, LoginSite? site)
    {
        string title = site?.Name ?? L.T("Another site");
        BrowserSignIn window;
        try
        {
            if (BrowserSignIn.FindBrowser() is not { } browser) throw new InvalidOperationException("no browser");
            window = new BrowserSignIn(browser, url);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            AppPaths.AppendLog($"[{DateTime.Now:s}] Sign-in: {e.Message}\n\n");
            Alert(title, L.T("Signing in needs Firefox, Chrome, Edge or Brave on this computer."));
            return;
        }
        var text = new TextBlock { Text = L.T("Sign in in the window that has just opened, then close it or press Done."), TextWrapping = TextWrapping.Wrap, Foreground = OverlayBrush("SubTextBrush"), LineHeight = 20 };
        var done = Button(L.T("Done"), "PrimaryButton", isDefault: true);
        var cancel = Button(L.T("Cancel"), "GhostButton", isCancel: true);
        var win = Frame(title, text, 400, cancel, done);
        done.Click += (_, _) =>
        {
            done.IsEnabled = false;
            window.Finish();
        };
        bool? kept = null;
        var stop = new CancellationTokenSource();
        async void Wait()
        {
            // (the dialog comes up first: it can't be closed before that)
            await Task.Yield();
            await window.Closed;
            if (stop.IsCancellationRequested) return;
            text.Text = L.T("Saving your sign-in…");
            done.IsEnabled = cancel.IsEnabled = false;
            var cookies = await window.CookiesAsync(stop.Token);
            if (stop.IsCancellationRequested) return;
            kept = SiteLogins.Keep(cookies, site);
            win.DialogResult = true;
        }
        Wait();
        win.ShowDialog();
        stop.Cancel();
        _ = window.EndAsync();
        if (kept == false) Alert(title, L.T("No sign-in found: the window was closed before signing in, or the site keeps it only while the window is open."));
    }
}
