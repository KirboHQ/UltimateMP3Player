using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using UltimateMP3Player.Core;
using UltimateMP3Player.Platform;

namespace UltimateMP3Player.Views;

// Settings › Renewal, only on the iPhone and only in a build that can renew itself: until when the app works, "Renew now", what it
// takes and whether it's there. The page's own kind of cards (SettingsPage.axaml), so its search and its groups find them too.
public sealed class RenewalCards
{
    private const string Group = "renewal";

    private readonly Border _badge = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _caption = new() { Classes = { "value" }, Margin = default };
    private readonly TextBlock _day = new() { FontSize = 21, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _left = new() { Classes = { "value" } };
    private readonly StackPanel _problem = new(), _needs = new();
    private readonly Button _renew = Button("TouchPrimary", L.T("Renew now"), RenewalNotice.RenewNow);
    private readonly TextBlock _stage = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
    private readonly ProgressBar _bar = new() { Maximum = 1 };
    private readonly StackPanel _working = new();
    private readonly TextBlock _hint = new() { Classes = { "setting-hint" } };
    private readonly TextBlock _rowValue = new() { Classes = { "value" } };
    private readonly Border _updates;

    // Into the page's cards and its list of groups, before About.
    public static void AddTo(Panel cards, Panel groups, EventHandler<RoutedEventArgs> open) => _ = new RenewalCards(cards, groups, open);

    private RenewalCards(Panel cards, Panel groups, EventHandler<RoutedEventArgs> open)
    {
        _working.Children.AddRange(new Control[] { _stage, _bar });
        var words = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { _caption, _day, _left } };
        Grid.SetColumn(words, 1);
        _renew.Margin = new Thickness(0, 14, 0, 0);
        _working.Margin = new Thickness(0, 14, 0, 0);
        _hint.Margin = new Thickness(0, 12, 0, 0);
        var status = Card(L.T("renewal renew expires expiry seven days sign signature localdevvpn vpn sidestore apple id"));
        status.Child = new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 10),
            Children = { new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { _badge, words } }, _problem, _renew, _working, _hint },
        };
        var needed = Card(L.T("localdevvpn vpn sidestore computer cable installer needed apple id sign in password"));
        needed.Child = _needs;
        _updates = Card(L.T("updates install by itself automatically version"));
        var alone = new CheckBox { Theme = Ui.Theme("Switch"), Classes = { "switch" }, IsChecked = App.Host.Settings.InstallUpdatesAlone };
        alone.Content = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = L.T("Install updates by itself when I'm not listening"), Classes = { "setting" } },
                new TextBlock { Text = L.T("In the background, when iOS lets it, on Wi-Fi and with LocalDevVPN on. Off: it says when there's a new version, and installs it when you tap Update."), Classes = { "setting-hint" } },
            },
        };
        alone.IsCheckedChanged += (_, _) =>
        {
            App.Host.Settings.InstallUpdatesAlone = alone.IsChecked == true;
            App.Host.Settings.Save();
        };
        _updates.Child = new StackPanel { Children = { alone } };

        int at = cards.Children.IndexOf(cards.Children.OfType<Border>().First(c => SettingsPage.GetGroup(c) == "about")) - 1;
        cards.Children.InsertRange(at, new Control[] { Title(L.T("RENEWAL")), status, Title(L.T("WHAT IT NEEDS")), needed, Title(L.T("UPDATES")), _updates });

        var row = new Button { Theme = Ui.Theme("SettingRow"), Tag = Group };
        var name = new StackPanel
        {
            Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = L.T("Renewal"), Classes = { "setting" } }, _rowValue },
        };
        Grid.SetColumn(name, 1);
        row.Content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { RenewalNotice.Badge("\uE72C", "Accent", 34), name } };
        row.Click += open;
        int about = groups.Children.IndexOf(groups.Children.OfType<Button>().First(b => b.Tag as string == "about"));
        groups.Children.InsertRange(about, new Control[] { row, new Border { Classes = { "line" } } });

        // (only while the page is in the window: a page let go of isn't kept alive by it)
        groups.AttachedToVisualTree += (_, _) =>
        {
            Renewal.Changed -= Refresh;
            Renewal.Changed += Refresh;
            Refresh();
        };
        groups.DetachedFromVisualTree += (_, _) => Renewal.Changed -= Refresh;
        // (the group opened: whether LocalDevVPN is on as of now)
        status.AttachedToVisualTree += async (_, _) =>
        {
            if (Renewal.HasKeys) await Renewal.ProbeAsync();
        };
        Refresh();
    }

    private static Border Card(string keywords)
    {
        var card = new Border { Classes = { "card" } };
        SettingsPage.SetGroup(card, Group);
        SettingsPage.SetKeywords(card, keywords);
        return card;
    }

    private static TextBlock Title(string text) => new() { Text = text, Classes = { "card-title" } };

    private static Button Button(string theme, string text, Action press)
    {
        var b = new Button { Theme = Ui.Theme(theme), Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        b.Click += (_, _) => press();
        return b;
    }

    private void Refresh()
    {
        bool keys = Renewal.HasKeys, busy = Renewal.Busy && !Renewal.SigningIn || Renewal.Stage.Length > 0;
        var now = DateTime.UtcNow;
        // (past and yet running: renewed some other way, by the cable, which doesn't tell the app)
        var expires = Renewal.Expires is { } e && e > now ? e : (DateTime?)null;
        bool warn = keys && Renewal.Problem != RenewalProblem.None || Renewal.Urgent;

        var colour = warn ? "Warning" : !keys ? "Accent" : "Success";
        var badge = RenewalNotice.Badge(warn ? "\uE7BA" : !keys ? "\uE72C" : "\uE73E", colour, 46);
        _badge.Child = badge;
        _caption.Text = expires != null ? L.T("The app works until") : L.T("Until when it works");
        _day.Text = expires is { } day ? Renewal.Day(day) : L.T("Not known");
        _left.Text = expires is { } at ? Left(at - now) + " · " + L.F("at {0}", at.ToLocalTime().ToString("t", L.Culture)) : "";
        _left.IsVisible = expires != null;
        _rowValue.Text = expires is { } until ? L.F("Until {0}", Renewal.Day(until)) : L.T("Not known");
        _rowValue.Foreground = warn ? RenewalNotice.Strong("Warning") : Ui.Res("SubTextBrush");

        _problem.Children.Clear();
        if (keys && !busy && RenewalNotice.For(Renewal.Problem) is { } n) _problem.Children.Add(Banner(n.Label, n.Title, n.Text, n.Do == RenewalNotice.RenewNow ? null : n.Yes, n.Do, n.Or, n.DoOr));
        else if (!keys)
            _problem.Children.Add(Banner(L.T("NEEDED"), L.T("The computer, once"),
                L.T("Apple keeps the app working for 7 days at a time. To renew itself here it needs what the installer puts in it: open the installer on the computer, plug the iPhone in and choose to let the app renew itself."),
                L.T("Copy the installer's address"), () =>
                {
                    Ui.CopyText(Installer);
                    App.Host.Session?.Toast(L.T("Address copied"));
                }));

        _renew.IsVisible = keys && !busy;
        _working.IsVisible = busy;
        _stage.Text = RenewalNotice.StageText(Renewal.Stage);
        _bar.Value = Renewal.Progress;
        _hint.IsVisible = keys;
        _hint.Text = (Renewal.LastRenewed is { } last ? L.F("It last renewed itself on {0}.", Renewal.Day(last)) + " " : "")
            + L.T("It does it by itself when 4 days are left, as soon as it finds LocalDevVPN on.");

        _needs.Children.Clear();
        var rows = new List<Control>();
        if (keys)
        {
            bool there = Renewal.VpnInstalled, on = Renewal.VpnOn == true;
            rows.Add(Need("LocalDevVPN", L.T("A free app of the App Store, kept connected: through it the app reaches the iPhone it's on. Nothing goes out of the iPhone that way."),
                on ? L.T("NEEDED, ON") : there ? L.T("NEEDED, OFF") : L.T("NEEDED, MISSING"), on ? "Success" : "Warning",
                on || Renewal.VpnOn == null || Renewal.Problem == RenewalProblem.Vpn ? null : there ? L.T("Turn it on") : L.T("Get it"), Renewal.OpenVpn));
        }
        if (keys)
        {
            bool ended = Renewal.Problem == RenewalProblem.SignIn;
            var signIn = Need(L.T("Sign-in to Apple"), L.T("It's what the app renews itself with. If Apple ends it, sign in again here or use the computer's sign-in."),
                ended ? L.T("NEEDED, ENDED") : L.T("NEEDED, DONE"), ended ? "Warning" : "Success", L.T("Sign in again"), RenewalSignIn.Open);
            // (one thing at a time with Apple)
            signIn.Children[^1].IsEnabled = !busy;
            rows.Add(signIn);
        }
        bool computer = !keys || Renewal.Problem == RenewalProblem.Cable;
        rows.Add(Need(L.T("The computer and the cable"), computer ? L.T("Once, with the installer: it puts in the app what it renews itself with.") : L.T("Only the way back, should the app ever not manage."),
            computer ? L.T("NEEDED, ONCE") : L.T("NOT NEEDED ANYMORE"), computer ? "Warning" : "Success"));
        if (Renewal.SideStoreInstalled && Renewal.LastRenewed != null)
            rows.Add(Need("SideStore", L.T("The app renews itself now. If no other app of yours needs it, delete it: hold its icon, then Remove App."), L.T("NOT NEEDED ANYMORE"), "Success"));
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) _needs.Children.Add(new Border { Classes = { "line" } });
            _needs.Children.Add(rows[i]);
        }
        _updates.IsEnabled = keys;
    }

    private const string Installer = "https://github.com/KirboHQ/UltimateMP3Player-Installer-Tool";

    private static string Left(TimeSpan left)
        => left.TotalDays >= 1 ? L.Count((int)left.TotalDays, "1 day left", "{0} days left") : L.Count(Math.Max(1, (int)left.TotalHours), "1 hour left", "{0} hours left");

    private static Border Banner(string label, string title, string text, string? yes, Action? act, string? or = null, Action? other = null)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = RenewalNotice.Strong("Warning") });
        body.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        body.Children.Add(new TextBlock { Text = text, Classes = { "setting-hint" } });
        if (yes != null && act != null)
        {
            var b = Button("TouchGhost", yes, act);
            b.Margin = new Thickness(0, 12, 0, 2);
            body.Children.Add(b);
        }
        if (or != null && other != null)
        {
            var b = Button("TouchGhost", or, other);
            b.Margin = new Thickness(0, 8, 0, 2);
            body.Children.Add(b);
        }
        return new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 12), Margin = new Thickness(0, 14, 0, 0), Background = RenewalNotice.Soft("Warning"), Child = body };
    }

    private static StackPanel Need(string name, string text, string state, string colour, string? yes = null, Action? act = null)
    {
        var pill = RenewalNotice.Pill(state, colour);
        pill.HorizontalAlignment = HorizontalAlignment.Left;
        var row = new StackPanel { Margin = new Thickness(0, 12) };
        row.Children.Add(pill);
        row.Children.Add(new TextBlock { Text = name, Classes = { "setting" }, Margin = new Thickness(0, 8, 0, 0) });
        row.Children.Add(new TextBlock { Text = text, Classes = { "setting-hint" } });
        if (yes != null && act != null)
        {
            var b = Button("TouchGhost", yes, act);
            b.Margin = new Thickness(0, 12, 0, 0);
            row.Children.Add(b);
        }
        return row;
    }
}
