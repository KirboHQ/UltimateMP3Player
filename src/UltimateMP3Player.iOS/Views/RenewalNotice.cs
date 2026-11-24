using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UltimateMP3Player.Core;
using UltimateMP3Player.Platform;

namespace UltimateMP3Player.Views;

// What can't go unnoticed about the app renewing itself: a sheet whose label, icon and colour say at a glance what it's about (like the
// installer's notices: something needed, something that isn't any more, a decision, news).
public static class RenewalNotice
{
    private const string Need = "\uE7BA", Gone = "\uE74D", Ask = "\uE946", News = "\uE896";

    // Stays: the button starts something elsewhere, and the sheet goes by itself once nothing's wrong any more. Risky: the button takes
    // something away, so it isn't the one that stands out. Away: a last button, under "Not now", for getting rid of it.
    internal sealed record Notice(string Label, string Glyph, string Colour, string Title, string Text, string? Yes = null, Action? Do = null, bool Stays = false, bool Risky = false,
        string? Away = null, Action? Discard = null);

    // ------------------------------------------------------------------ what's said

    // Why the app couldn't renew itself, and what to press (Settings › Renewal shows the same words).
    internal static Notice? For(RenewalProblem problem)
    {
        var until = Renewal.Expires is { } e && e > DateTime.UtcNow ? " " + L.F("The app stops opening after {0}.", Renewal.Day(e)) : "";
        Notice Needed(string title, string text, string? yes = null, Action? act = null, bool stays = false)
            => new(L.T("NEEDED"), Need, "Warning", title, text, yes, act, stays);
        switch (problem)
        {
            case RenewalProblem.None:
                return null;
            case RenewalProblem.Vpn:
                var vpn = Renewal.VpnInstalled ? L.T("Turn on LocalDevVPN") : L.T("Get LocalDevVPN");
                return Needed(vpn, (Renewal.VpnInstalled ? L.T("It's off: the app reaches the iPhone it's on only through it.")
                    : L.T("It isn't on the iPhone: it's a small free app of the App Store, and the app reaches the iPhone it's on only through it. Open it, tap Connect and allow the VPN.")) + until,
                    vpn, Renewal.OpenVpn, true);
            case RenewalProblem.Cable:
                return Needed(L.T("Plug the iPhone into the computer"),
                    L.T("The iPhone doesn't know this app's pairing any more (it happens after an iOS update or a reset). Open the installer on the computer and plug the iPhone in: it puts everything right.") + until);
            case RenewalProblem.SignIn:
                return Needed(L.T("Sign in to Apple again"),
                    L.T("Apple ended the sign-in the app renews itself with (after a change of password, for example). Open the installer on the computer, plug the iPhone in and sign in again.") + until);
            case RenewalProblem.Internet:
                return Needed(L.T("Check the connection"), L.T("Apple couldn't be reached. The app tries again by itself.") + until, L.T("Try again"), RenewNow);
            case RenewalProblem.Install:
                return Needed(L.T("Install it again"),
                    L.T("This time Apple's new permission doesn't fit the app as it is. It gets downloaded and put in again, which takes a few minutes: iOS closes it meanwhile, and your music stays.") + until,
                    L.T("Install it now"), () => _ = App.Host.Updates.ReinstallAsync());
            default:
                return new(L.T("NOT RENEWED"), Need, "Warning", L.T("Something didn't work"), L.T("Try again in a while. If it keeps happening, the installer on the computer does it with the cable.") + until
                    + (Renewal.ProblemText.Length > 0 ? "\n\n" + Renewal.ProblemText : ""), L.T("Try again"), RenewNow);
        }
    }

    // ------------------------------------------------------------------ when

    private static DateTime _shown;
    // (the sheets it was shown on: a screen built again, for another language, starts without it)
    private static SheetHost? _openOn;
    internal static bool Open => _openOn != null && _openOn == App.Host.View?.Sheets;

    // The app opened or came back: in the last two days without having managed (every six hours at most); SideStore, once.
    public static async Task ShowPendingAsync()
    {
        if (Open || !Renewal.HasKeys || App.Host.View is not { IsReady: true }) return;
        if (Renewal.Urgent && Renewal.Problem != RenewalProblem.None)
        {
            if (DateTime.UtcNow - _shown < TimeSpan.FromHours(6)) return;
            _shown = DateTime.UtcNow;
            await ShowProblemAsync();
        }
        else if (Renewal.LastRenewed != null && Renewal.SideStoreInstalled && Renewal.Once("sidestore"))
            await ShowAsync(new Notice(L.T("NOT NEEDED ANYMORE"), Gone, "Success", "SideStore",
                L.T("The app renews itself now, so SideStore has nothing left to do for it. If no other app of yours needs it, delete it from the iPhone: hold its icon, then Remove App. LocalDevVPN stays: the app uses it.")));
    }

    // True: it was put right meanwhile (LocalDevVPN turned on), so what it was needed for can go on.
    public static async Task<bool> ShowProblemAsync() => For(Renewal.Problem) is { } notice && await ShowAsync(notice) && notice.Stays && Renewal.Problem == RenewalProblem.None;

    // "Renew now": what came of it is always said.
    public static async void RenewNow()
    {
        if (Renewal.Busy) return;
        try
        {
            if (await Renewal.RenewAsync()) App.Host.Session?.Toast(L.F("Renewed: it works until {0}", Renewal.Day(Renewal.Expires ?? DateTime.UtcNow)));
            else if (await ShowProblemAsync()) RenewNow();
        }
        catch (Exception ex) { App.Log(ex); }
    }

    public static Task<bool> AskUpdateAsync(string version) => ShowAsync(new Notice(L.T("NEW VERSION"), News, "Accent", L.F("Update to {0}", version),
        L.T("It's downloaded, signed and installed right here, in a few minutes. Towards the end iOS closes the app to replace it: the music stops, and everything's found again where it was."), L.T("Update")));

    public static Task<bool> AskRevokeAsync() => ShowAsync(new Notice(L.T("YOUR DECISION"), Ask, "Accent", L.T("Replace a certificate?"),
        L.T("A free Apple ID holds only a couple of signing certificates, and they're all in use. If you replace one, other apps signed with it (with SideStore or AltStore, say) will stop opening until you renew them."),
        L.T("Replace it"), Risky: true));

    // ------------------------------------------------------------------ the sheet

    internal static IBrush Strong(string colour) => Ui.Res(colour == "Accent" ? "AccentTextBrush" : colour + "Brush");
    internal static IBrush Soft(string colour) => Ui.Res(colour + "SoftBrush");

    // Drawn, not written: a character sits where its font's line puts it, up to a point off the middle of the circle.
    internal static Border Badge(string glyph, string colour, double size)
    {
        var face = new Typeface(Icons.FontFor(glyph)).GlyphTypeface;
        var text = Icons.Map(glyph);
        var drawn = new GlyphRun(face, size / 2, text.AsMemory(), new[] { face.GetGlyph(text[0]) }).BuildGeometry();
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = Soft(colour),
            Child = new Avalonia.Controls.Shapes.Path
            {
                Data = drawn, Fill = Strong(colour), Stretch = Stretch.Uniform, Width = drawn.Bounds.Width, Height = drawn.Bounds.Height,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, UseLayoutRounding = false,
            },
        };
    }

    internal static Border Pill(string label, string colour) => new()
    {
        CornerRadius = new CornerRadius(11), Height = 22, Padding = new Thickness(10, 0), Background = Soft(colour), VerticalAlignment = VerticalAlignment.Top,
        Child = new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Strong(colour), VerticalAlignment = VerticalAlignment.Center },
    };

    private static StackPanel Body(Notice n)
    {
        var body = new StackPanel { Margin = new Thickness(24, 10, 24, 18) };
        var badge = Badge(n.Glyph, n.Colour, 64);
        badge.HorizontalAlignment = HorizontalAlignment.Center;
        body.Children.Add(badge);
        var pill = Pill(n.Label, n.Colour);
        pill.HorizontalAlignment = HorizontalAlignment.Center;
        pill.Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(pill);
        var title = new TextBlock { Text = n.Title, FontSize = 23, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 8) };
        AutomationProperties.SetLiveSetting(title, AutomationLiveSetting.Assertive);
        body.Children.Add(title);
        if (n.Text.Length > 0) body.Children.Add(new TextBlock { Text = n.Text, Classes = { "hint" }, FontSize = 15, LineHeight = 22, TextAlignment = TextAlignment.Center });
        return body;
    }

    private static Button Wide(string text, string theme, double top) => new()
    {
        Content = text, Theme = Ui.Theme(theme), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, top, 0, 0),
    };

    // True: its main button.
    internal static async Task<bool> ShowAsync(Notice n)
    {
        if (App.Host.View?.Sheets is not { } sheets) return false;
        var body = Body(n);
        bool yes = false, away = false;
        if (n.Yes != null)
        {
            var go = Wide(n.Yes, n.Risky ? "TouchGhost" : "TouchPrimary", 22);
            go.Click += (_, _) =>
            {
                yes = true;
                if (n.Stays) n.Do?.Invoke();
                else sheets.Close(body);
            };
            body.Children.Add(go);
        }
        var later = Wide(n.Yes != null ? L.T("Not now") : "OK", n.Yes != null && !n.Risky ? "TouchGhost" : "TouchPrimary", n.Yes != null ? 10 : 22);
        later.Click += (_, _) => sheets.Close(body);
        body.Children.Add(later);
        if (n.Away != null)
        {
            var discard = Wide(n.Away, "TouchDanger", 10);
            discard.Click += (_, _) =>
            {
                away = true;
                sheets.Close(body);
            };
            body.Children.Add(discard);
        }
        void Solved()
        {
            if (n.Stays && !Renewal.Busy && Renewal.Problem == RenewalProblem.None) sheets.Close(body);
        }
        _openOn = sheets;
        Renewal.Changed += Solved;
        await sheets.Show(new ScrollViewer { Content = body });
        Renewal.Changed -= Solved;
        _openOn = null;
        if (yes && !n.Stays) n.Do?.Invoke();
        if (away) n.Discard?.Invoke();
        return yes;
    }

    // An update going on: where it is, until iOS closes the app. What closes the sheet, if it doesn't get there.
    public static Action ShowProgress(string title)
    {
        var (show, close) = ShowWorking(new Notice(L.T("DON'T CLOSE THE APP"), News, "Accent", title,
            L.T("Towards the end iOS closes it by itself, and a notification says when it can be opened again.")));
        void Refresh() => show(Renewal.Stage.Length == 0 ? L.T("Preparing…") : StageText(Renewal.Stage), Renewal.Progress);
        Refresh();
        Renewal.Changed += Refresh;
        return () =>
        {
            Renewal.Changed -= Refresh;
            close();
        };
    }

    // Something going on that's best not interrupted: what shows where it is (what it's doing, how far), and what closes the sheet.
    internal static (Action<string, double> Show, Action Close) ShowWorking(Notice n)
    {
        if (App.Host.View?.Sheets is not { } sheets) return ((_, _) => { }, () => { });
        var body = Body(n);
        var stage = new TextBlock { FontSize = 15, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 10) };
        var bar = new ProgressBar { Maximum = 1, Margin = new Thickness(0, 0, 0, 14) };
        body.Children.Add(stage);
        body.Children.Add(bar);
        _openOn = sheets;
        _ = sheets.Show(new ScrollViewer { Content = body }, dismissable: false);
        return ((what, part) =>
        {
            stage.Text = what + $" {part * 100:0}%";
            bar.Value = part;
        }, () =>
        {
            _openOn = null;
            sheets.Close(body);
        });
    }

    internal static string StageText(string stage) => stage switch
    {
        "Downloading" => L.T("Downloading…"),
        "Preparing" => L.T("Preparing…"),
        "Signing" => L.T("Signing…"),
        "Installing" => L.T("Installing…"),
        _ => L.T("Renewing…"),
    };
}
