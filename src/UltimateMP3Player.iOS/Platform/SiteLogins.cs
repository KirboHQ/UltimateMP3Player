using System.Globalization;
using System.Text;
using CoreGraphics;
using Foundation;
using UIKit;
using WebKit;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Platform;

// A site the app can sign in to: its login page, its domains, the cookies it sets only once someone is signed in.
public sealed record LoginSite(string Name, string Color, string Url, string[] Domains, string[] Auth);

// The logins made inside the app, as on Android: the site's own page opens over the app (LoginPage, a WKWebView), the
// person signs in there as usual (the password goes only to the site), and once the page is closed the cookies of the
// app's web pages (WKHTTPCookieStore) are written to cookies.txt in the app's data, in the format yt-dlp reads
// (AppSettings.CookieFile): the downloads go as that account while "Usa i miei accessi" is on.
// The cookie store is the main thread's: everything here is called from the screen.
public static class SiteLogins
{
    private static readonly string YouTubeLogin = "https://accounts.google.com/ServiceLogin?service=youtube&passive=true&continue=" +
        Uri.EscapeDataString("https://www.youtube.com/signin?action_handle_signin=true&next=" + Uri.EscapeDataString("https://www.youtube.com/"));

    // (the same sites as on Android)
    public static readonly LoginSite[] Sites =
    {
        new("YouTube", "#FF0033", YouTubeLogin, new[] { "youtube.com", "google.com" }, new[] { "SAPISID", "__Secure-3PAPISID", "LOGIN_INFO" }),
        new("SoundCloud", "#FF5500", "https://soundcloud.com/signin", new[] { "soundcloud.com" }, new[] { "oauth_token" }),
        new("Instagram", "#E1306C", "https://www.instagram.com/accounts/login/", new[] { "instagram.com" }, new[] { "sessionid" }),
        new("TikTok", "#FE2C55", "https://www.tiktok.com/login", new[] { "tiktok.com" }, new[] { "sessionid", "sessionid_ss" }),
        new("X / Twitter", "#E7E9EA", "https://x.com/i/flow/login", new[] { "x.com", "twitter.com" }, new[] { "auth_token" }),
        new("Facebook", "#1877F2", "https://m.facebook.com/login/", new[] { "facebook.com" }, new[] { "c_user" }),
        new("Reddit", "#FF4500", "https://www.reddit.com/login/", new[] { "reddit.com" }, new[] { "reddit_session" }),
        new("Twitch", "#9146FF", "https://www.twitch.tv/login", new[] { "twitch.tv" }, new[] { "auth-token" }),
        new("Bandcamp", "#629AA9", "https://bandcamp.com/login", new[] { "bandcamp.com" }, new[] { "identity" }),
        new("Vimeo", "#1AB7EA", "https://vimeo.com/log_in", new[] { "vimeo.com" }, new[] { "vimeo" }),
    };

    public static string FilePath { get; private set; } = "";

    // The file changed (a login, a logout): the settings' row and the sheet show it.
    public static event Action? Changed;

    // At start (Startup), before anything downloads.
    public static void Configure(string dataDir)
    {
        FilePath = Path.Combine(dataDir, "cookies.txt");
        AppSettings.CookieFile = FilePath;
    }

    private static WKHttpCookieStore Store => WKWebsiteDataStore.DefaultDataStore.HttpCookieStore;

    // ------------------------------------------------------------------ what's in the file

    private sealed record Cookie(string Host, string Path, bool Secure, long Expires, string Name, string Value);

    private static List<Cookie> ReadFile()
    {
        var list = new List<Cookie>();
        try
        {
            if (!File.Exists(FilePath)) return list;
            foreach (var raw in File.ReadLines(FilePath))
            {
                if (raw.Length == 0 || raw[0] == '#') continue;
                var p = raw.Split('\t');
                if (p.Length < 7) continue;
                long.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var exp);
                list.Add(new Cookie(p[0], p[2], p[3] == "TRUE", exp, p[5], p[6]));
            }
        }
        catch { }
        return list;
    }

    private static bool OfDomain(string host, string domain)
    {
        host = host.TrimStart('.');
        return host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
    }

    // The sites signed in, from the file (the web pages' engine isn't started just to look).
    public static HashSet<string> SignedIn()
    {
        var cookies = ReadFile();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var set = new HashSet<string>();
        foreach (var site in Sites)
            if (cookies.Any(c => c.Value.Length > 0 && (c.Expires == 0 || c.Expires > now) && site.Auth.Contains(c.Name) && site.Domains.Any(d => OfDomain(c.Host, d))))
                set.Add(site.Name);
        return set;
    }

    public static bool HasFile => File.Exists(FilePath);

    // "YouTube, SoundCloud" for the settings' row.
    public static string Summary()
    {
        var signed = SignedIn();
        if (signed.Count == 0) return HasFile ? L.T("Nessun sito della lista (forse un altro)") : L.T("Nessuno");
        var names = string.Join(", ", Sites.Where(s => signed.Contains(s.Name)).Select(s => s.Name));
        return App.Host?.Settings.UseCookies == false ? names + " · " + L.T("non usati (spento)") : names;
    }

    // ------------------------------------------------------------------ the page

    private static TaskCompletionSource? _open;

    // The site's page over the app; the task ends once it's closed and its cookies are saved.
    public static Task OpenAsync(string url, string? siteName)
    {
        if (Files.TopController() is not { } top) return Task.CompletedTask;
        _open?.TrySetResult();
        var open = _open = new TaskCompletionSource();
        try
        {
            var site = Sites.FirstOrDefault(s => s.Name == siteName);
            var accent = Ui.Find<Avalonia.Media.IBrush>("AccentBrush") is Avalonia.Media.ISolidColorBrush b
                ? UIColor.FromRGB(b.Color.R, b.Color.G, b.Color.B)
                : UIColor.FromRGB(0x7C, 0x5C, 0xFF);
            top.PresentViewController(new LoginPage(url, site, accent), true, null);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            open.TrySetResult();
        }
        return open.Task;
    }

    // (the login page closed)
    internal static async void Closed()
    {
        try
        {
            bool before = SignedIn().Count > 0;
            await ExportAsync();
            // The first login: used from now on (the switch can turn it off).
            if (!before && SignedIn().Count > 0 && App.Host is { } host && !host.Settings.UseCookies)
            {
                host.Settings.UseCookies = true;
                host.Settings.Save();
                Changed?.Invoke();
            }
        }
        catch (Exception ex) { App.Log(ex); }
        _open?.TrySetResult();
    }

    // Is this site signed in right now in the web pages (the login page's bar)?
    internal static async Task<bool> SignedInNowAsync(LoginSite site)
    {
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return (await Store.GetAllCookiesAsync()).Any(c => !string.IsNullOrEmpty(c.Value) && site.Auth.Contains(c.Name) &&
                                                             site.Domains.Any(d => OfDomain(c.Domain, d)) &&
                                                             (c.ExpiresDate == null || c.ExpiresDate.SecondsSince1970 > now));
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ the file from the web pages' cookies

    // Every cookie of the app's web pages into cookies.txt, each with its domain, path and expiry.
    public static async Task ExportAsync()
    {
        var list = new List<Cookie>();
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var c in await Store.GetAllCookiesAsync())
            {
                if (string.IsNullOrEmpty(c.Domain) || string.IsNullOrEmpty(c.Name) || string.IsNullOrEmpty(c.Value)) continue;
                long expires = c.ExpiresDate is { } date && !c.IsSessionOnly ? (long)date.SecondsSince1970 : 0;
                if (expires != 0 && expires < now) continue;
                list.Add(new Cookie(c.Domain, string.IsNullOrEmpty(c.Path) ? "/" : c.Path, c.IsSecure, expires, c.Name, c.Value));
            }
        }
        catch (Exception ex) { App.Log(ex); }
        await Task.Run(() => Write(list));
        Changed?.Invoke();
    }

    private static void Write(List<Cookie> cookies)
    {
        try
        {
            if (cookies.Count == 0)
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
                return;
            }
            var sb = new StringBuilder();
            sb.Append("# Netscape HTTP Cookie File\n");
            sb.Append("# Ultimate MP3 Player: the logins made inside the app (Settings > Downloads > Sign in to sites).\n\n");
            foreach (var c in cookies)
            {
                // (no tab or line break can be inside a cookie: a broken one is left out rather than breaking the file)
                if (c.Name.IndexOfAny(new[] { '\t', '\n', '\r' }) >= 0 || c.Value.IndexOfAny(new[] { '\t', '\n', '\r' }) >= 0) continue;
                sb.Append(c.Host).Append('\t')
                    .Append(c.Host.StartsWith('.') ? "TRUE" : "FALSE").Append('\t')
                    .Append(c.Path).Append('\t')
                    .Append(c.Secure ? "TRUE" : "FALSE").Append('\t')
                    .Append(c.Expires.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(c.Name).Append('\t')
                    .Append(c.Value).Append('\n');
            }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var part = FilePath + ".part";
            File.WriteAllText(part, sb.ToString());
            File.Move(part, FilePath, true);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    // ------------------------------------------------------------------ signing out

    // The site's cookies (of all its domains) go from the web pages too, then the file is written again without them.
    public static async Task SignOutAsync(LoginSite site)
    {
        try
        {
            foreach (var c in await Store.GetAllCookiesAsync())
                if (site.Domains.Any(d => OfDomain(c.Domain ?? "", d)))
                    await Store.DeleteCookieAsync(c);
        }
        catch (Exception ex) { App.Log(ex); }
        await ExportAsync();
    }

    // Every site: the web pages forget everything, the file goes.
    public static async Task SignOutAllAsync()
    {
        try { await WKWebsiteDataStore.DefaultDataStore.RemoveDataOfTypesAsync(WKWebsiteDataStore.AllWebsiteDataTypes, NSDate.DistantPast); }
        catch (Exception ex) { App.Log(ex); }
        try { File.Delete(FilePath); } catch { }
        Changed?.Invoke();
    }
}

// A site's own page over the app, to sign in to it: a bar on top (close, the page's title and whether the login is done,
// "Fatto"), a thin line while the page loads, the page. A screen of iOS's own (not drawn by Avalonia): the page is WebKit's.
internal sealed class LoginPage : UIViewController
{
    private static readonly UIColor Back = UIColor.FromRGB(0x0E, 0x10, 0x14), BarBack = UIColor.FromRGB(0x16, 0x19, 0x20),
        Muted = UIColor.FromRGB(0x9A, 0xA0, 0xAC), Green = UIColor.FromRGB(0x4A, 0xDE, 0x80), Idle = UIColor.FromRGB(0x2A, 0x2E, 0x38);

    private readonly string _url;
    private readonly LoginSite? _site;
    private readonly UIColor _accent;
    private WKWebView? _web;
    private UILabel? _title, _status;
    private UIButton? _done;
    private UIProgressView? _progress;
    private IDisposable? _progressWatch, _titleWatch;
    private bool _signedIn, _closed;

    public LoginPage(string url, LoginSite? site, UIColor accent)
    {
        _url = url;
        _site = site;
        _accent = accent;
        // (full screen: swiping it down would close it without saving the cookies)
        ModalPresentationStyle = UIModalPresentationStyle.FullScreen;
    }

    public override UIStatusBarStyle PreferredStatusBarStyle() => UIStatusBarStyle.LightContent;

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        var root = View!;
        root.BackgroundColor = Back;

        // ---- the bar
        var bar = new UIView { BackgroundColor = BarBack, TranslatesAutoresizingMaskIntoConstraints = false };
        root.AddSubview(bar);
        var close = new UIButton(UIButtonType.System) { TranslatesAutoresizingMaskIntoConstraints = false, TintColor = UIColor.FromRGB(0xC9, 0xCD, 0xD6) };
        close.SetImage(UIImage.GetSystemImage("xmark"), UIControlState.Normal);
        close.AccessibilityLabel = L.T("Chiudi");
        close.TouchUpInside += (_, _) => Close();
        bar.AddSubview(close);
        _title = new UILabel
        {
            Text = _site != null ? L.F("Accedi a {0}", _site.Name) : L.T("Accedi al sito"),
            TextColor = UIColor.White, Font = UIFont.BoldSystemFontOfSize(16)!, TranslatesAutoresizingMaskIntoConstraints = false,
        };
        _status = new UILabel
        {
            Text = L.T("Accedi come fai di solito, poi tocca Fatto"),
            TextColor = Muted, Font = UIFont.SystemFontOfSize(12.5f)!, TranslatesAutoresizingMaskIntoConstraints = false,
        };
        bar.AddSubview(_title);
        bar.AddSubview(_status);
        _done = new UIButton(UIButtonType.System) { TranslatesAutoresizingMaskIntoConstraints = false, BackgroundColor = Idle };
        _done.SetTitle(L.T("Fatto"), UIControlState.Normal);
        _done.SetTitleColor(UIColor.White, UIControlState.Normal);
        _done.TitleLabel!.Font = UIFont.BoldSystemFontOfSize(14.5f)!;
        _done.ContentEdgeInsets = new UIEdgeInsets(0, 18, 0, 18);
        _done.Layer.CornerRadius = 18;
        _done.TouchUpInside += (_, _) => Close();
        bar.AddSubview(_done);

        // ---- loading line and page
        _progress = new UIProgressView(UIProgressViewStyle.Bar) { ProgressTintColor = _accent, TrackTintColor = UIColor.Clear, TranslatesAutoresizingMaskIntoConstraints = false };
        root.AddSubview(_progress);
        var config = new WKWebViewConfiguration { WebsiteDataStore = WKWebsiteDataStore.DefaultDataStore };
        // Google won't let anyone sign in from a page that says it's an app's web view: it introduces itself as Safari
        // (it's Safari's engine anyway).
        var version = UIDevice.CurrentDevice.SystemVersion.Split('.');
        config.ApplicationNameForUserAgent = $"Version/{version[0]}.{(version.Length > 1 ? version[1] : "0")} Mobile/15E148 Safari/604.1";
        _web = new WKWebView(CGRect.Empty, config)
        {
            TranslatesAutoresizingMaskIntoConstraints = false, AllowsBackForwardNavigationGestures = true, BackgroundColor = Back, Opaque = false,
            NavigationDelegate = new Navigation(this), UIDelegate = new Windows(),
        };
        root.AddSubview(_web);

        var safe = root.SafeAreaLayoutGuide;
        NSLayoutConstraint.ActivateConstraints(new[]
        {
            bar.TopAnchor.ConstraintEqualTo(safe.TopAnchor),
            bar.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            bar.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            bar.HeightAnchor.ConstraintEqualTo(56),
            close.LeadingAnchor.ConstraintEqualTo(safe.LeadingAnchor, 4),
            close.CenterYAnchor.ConstraintEqualTo(bar.CenterYAnchor),
            close.WidthAnchor.ConstraintEqualTo(48),
            close.HeightAnchor.ConstraintEqualTo(48),
            _title.LeadingAnchor.ConstraintEqualTo(close.TrailingAnchor, 6),
            _title.TrailingAnchor.ConstraintLessThanOrEqualTo(_done.LeadingAnchor, -8),
            _title.BottomAnchor.ConstraintEqualTo(bar.CenterYAnchor, 1),
            _status.LeadingAnchor.ConstraintEqualTo(_title.LeadingAnchor),
            _status.TrailingAnchor.ConstraintLessThanOrEqualTo(_done.LeadingAnchor, -8),
            _status.TopAnchor.ConstraintEqualTo(bar.CenterYAnchor, 2),
            _done.TrailingAnchor.ConstraintEqualTo(safe.TrailingAnchor, -10),
            _done.CenterYAnchor.ConstraintEqualTo(bar.CenterYAnchor),
            _done.HeightAnchor.ConstraintEqualTo(36),
            _progress.TopAnchor.ConstraintEqualTo(bar.BottomAnchor),
            _progress.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            _progress.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            _progress.HeightAnchor.ConstraintEqualTo(3),
            _web.TopAnchor.ConstraintEqualTo(bar.BottomAnchor),
            _web.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            _web.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            _web.BottomAnchor.ConstraintEqualTo(root.BottomAnchor),
        });
        root.BringSubviewToFront(_progress);
        _title.SetContentCompressionResistancePriority((float)UILayoutPriority.DefaultLow, UILayoutConstraintAxis.Horizontal);
        _status.SetContentCompressionResistancePriority((float)UILayoutPriority.DefaultLow, UILayoutConstraintAxis.Horizontal);

        _progressWatch = _web.AddObserver("estimatedProgress", NSKeyValueObservingOptions.New, _ =>
        {
            if (_web == null || _progress == null) return;
            double p = _web.EstimatedProgress;
            _progress.SetProgress((float)p, true);
            _progress.Hidden = p >= 1;
        });
        _titleWatch = _web.AddObserver("title", NSKeyValueObservingOptions.New, _ =>
        {
            if (_site == null && !string.IsNullOrWhiteSpace(_web?.Title) && _title != null) _title.Text = _web.Title;
        });
        if (Uri.TryCreate(_url, UriKind.Absolute, out _)) _web.LoadRequest(new NSUrlRequest(new NSUrl(_url)));
    }

    // The site's login cookies are there: the bar says so and "Fatto" lights up.
    private async void CheckSignedIn()
    {
        if (_site == null || _done == null || _status == null) return;
        bool now = await SiteLogins.SignedInNowAsync(_site);
        if (now == _signedIn || _closed) return;
        _signedIn = now;
        _status.Text = now ? L.T("Accesso fatto: tocca Fatto per tornare all'app") : L.T("Accedi come fai di solito, poi tocca Fatto");
        _status.TextColor = now ? Green : Muted;
        _done.BackgroundColor = now ? _accent : Idle;
    }

    private void Close()
    {
        if (_closed) return;
        _closed = true;
        _progressWatch?.Dispose();
        _titleWatch?.Dispose();
        try { _web?.StopLoading(); } catch { }
        DismissViewController(true, () =>
        {
            _web?.RemoveFromSuperview();
            _web = null;
            SiteLogins.Closed();
        });
    }

    private sealed class Navigation : WKNavigationDelegate
    {
        private readonly LoginPage _page;
        public Navigation(LoginPage page) => _page = page;

        // Web pages stay here; links to apps (itms-apps:, intent-like ones) go nowhere.
        public override void DecidePolicy(WKWebView webView, WKNavigationAction navigationAction, Action<WKNavigationActionPolicy> decisionHandler)
        {
            var scheme = navigationAction.Request.Url?.Scheme?.ToLowerInvariant();
            decisionHandler(scheme is "http" or "https" or "about" or "blob" or "data" ? WKNavigationActionPolicy.Allow : WKNavigationActionPolicy.Cancel);
        }

        public override void DidFinishNavigation(WKWebView webView, WKNavigation navigation) => _page.CheckSignedIn();
    }

    // A page that opens another window (some logins do): in the same page instead.
    private sealed class Windows : WKUIDelegate
    {
        public override WKWebView? CreateWebView(WKWebView webView, WKWebViewConfiguration configuration, WKNavigationAction navigationAction, WKWindowFeatures windowFeatures)
        {
            if (navigationAction.TargetFrame == null) webView.LoadRequest(navigationAction.Request);
            return null;
        }
    }
}
