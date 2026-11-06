using System.IO;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Platform;

// The computers' part of the sign-ins made in the app; the file is the same as on the phones (SiteLogins.Shared.cs). The site's page opens in
// a window of a browser of this computer (BrowserSignIn, Dialogs.ShowLogins). The file is used only when the choice in Settings is
// "Sign in to each site": a browser's own cookies are the other choice (AppSettings.CookiesBrowserOrNull).
public static partial class SiteLogins
{
    public static void Prepare(string dataDir)
    {
        FilePath = Path.Combine(dataDir, "cookies.txt");
        AppSettings.SitesFile = FilePath;
    }

    // What a sign-in left. A known site: only its own cookies, and only if it's really signed in (a window closed before that changes
    // nothing). Another site: all of them, there's no telling. True if something was kept.
    public static bool Keep(IEnumerable<BrowserCookie> got, LoginSite? site)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fresh = got.Where(c => c.Value.Length > 0 && (site == null || site.Domains.Any(d => OfDomain(c.Host, d))))
            .Select(c => new Cookie(c.Host, c.Path, c.Secure, c.Expires, c.Name, c.Value)).ToList();
        if (fresh.Count == 0 || site != null && !fresh.Any(c => site.Auth.Contains(c.Name) && (c.Expires == 0 || c.Expires > now))) return false;
        var all = ReadFile();
        all.RemoveAll(c => site != null ? site.Domains.Any(d => OfDomain(c.Host, d)) : fresh.Any(n => n.Host == c.Host && n.Name == c.Name && n.Path == c.Path));
        all.AddRange(fresh);
        Write(all);
        Changed?.Invoke();
        return true;
    }

    public static void SignOut(LoginSite site)
    {
        Write(ReadFile().Where(c => !site.Domains.Any(d => OfDomain(c.Host, d))).ToList());
        Changed?.Invoke();
    }

    public static void SignOutAll()
    {
        Write(new List<Cookie>());
        Changed?.Invoke();
    }
}
