using System.Globalization;
using System.Text;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Platform;

// A site the app can sign in to: its login page, its domains, the cookies it sets only once someone is signed in.
public sealed record LoginSite(string Name, string Color, string Url, string[] Domains, string[] Auth);

// The logins made inside the app. On the computer the engines read the cookies of the browser where the person is already
// signed in; a phone's browsers keep theirs to themselves. So the site's own page opens inside the app (Android:
// LoginActivity; iPhone: LoginPage, a WKWebView), the person signs in there as usual (the password goes only to the site),
// and once the page is closed the cookies of the app's web pages are written to cookies.txt in the app's data, in the
// format yt-dlp and gallery-dl read (AppSettings.CookieFile): the downloads go as that account while "Usa i miei accessi"
// is on. This part, the file, is the same on both (linked into the iPhone app); the page and the web pages' cookies are
// each one's own SiteLogins.cs.
public static partial class SiteLogins
{
    private static readonly string YouTubeLogin = "https://accounts.google.com/ServiceLogin?service=youtube&passive=true&continue=" +
        Uri.EscapeDataString("https://www.youtube.com/signin?action_handle_signin=true&next=" + Uri.EscapeDataString("https://www.youtube.com/"));

    public static readonly LoginSite[] Sites =
    {
        new("YouTube", "#FF0033", YouTubeLogin, new[] { "youtube.com", "google.com" }, new[] { "SAPISID", "__Secure-3PAPISID", "LOGIN_INFO" }),
        new("SoundCloud", "#FF5500", "https://soundcloud.com/signin", new[] { "soundcloud.com" }, new[] { "oauth_token" }),
        new("Instagram", "#E1306C", "https://www.instagram.com/accounts/login/", new[] { "instagram.com" }, new[] { "sessionid" }),
        new("TikTok", "#FE2C55", "https://www.tiktok.com/login", new[] { "tiktok.com" }, new[] { "sessionid", "sessionid_ss" }),
        new("X / Twitter", "#8B93A7", "https://x.com/i/flow/login", new[] { "x.com", "twitter.com" }, new[] { "auth_token" }),
        new("Facebook", "#1877F2", "https://m.facebook.com/login/", new[] { "facebook.com" }, new[] { "c_user" }),
        new("Reddit", "#FF4500", "https://www.reddit.com/login/", new[] { "reddit.com" }, new[] { "reddit_session" }),
        new("Twitch", "#9146FF", "https://www.twitch.tv/login", new[] { "twitch.tv" }, new[] { "auth-token" }),
        new("Bandcamp", "#629AA9", "https://bandcamp.com/login", new[] { "bandcamp.com" }, new[] { "identity" }),
        new("Vimeo", "#1AB7EA", "https://vimeo.com/log_in", new[] { "vimeo.com" }, new[] { "vimeo" }),
    };

    public static string FilePath { get; private set; } = "";

    // The file changed (a login, a logout): the settings' row and the sheet show it.
    public static event Action? Changed;

    // At start (Android: MainApplication, iPhone: Startup), before anything downloads.
    public static void Configure(string dataDir)
    {
        FilePath = Path.Combine(dataDir, "cookies.txt");
        AppSettings.CookieFile = FilePath;
    }

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

    // The sites signed in, from the file (the web pages' engine isn't started just to look: it's heavy).
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
        if (signed.Count == 0) return HasFile ? L.T("None of the listed sites (maybe another one)") : L.T("None");
        var names = string.Join(", ", Sites.Where(s => signed.Contains(s.Name)).Select(s => s.Name));
        return App.Host?.Settings.UseCookies == false ? names + " · " + L.T("not used (off)") : names;
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
}
