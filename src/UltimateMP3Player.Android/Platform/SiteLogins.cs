using System.Globalization;
using System.Text;
using Android.Content;
using Android.Database.Sqlite;
using Android.Webkit;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Platform;

// A site the app can sign in to: its login page, its domains, the cookies it sets only once someone is signed in.
public sealed record LoginSite(string Name, string Color, string Url, string[] Domains, string[] Auth);

// The logins made inside the app. On the computer the engines read the cookies of the browser where the person is already
// signed in; a phone's browsers keep theirs to themselves. So the site's own page opens inside the app (LoginActivity),
// the person signs in there as usual (the password goes only to the site), and once the page is closed the cookies of the
// app's web pages are written to cookies.txt in the app's data, in the format yt-dlp and gallery-dl read
// (AppSettings.CookieFile): the downloads go as that account while "Usa i miei accessi" is on.
public static class SiteLogins
{
    private static readonly string YouTubeLogin = "https://accounts.google.com/ServiceLogin?service=youtube&passive=true&continue=" +
        Uri.EscapeDataString("https://www.youtube.com/signin?action_handle_signin=true&next=" + Uri.EscapeDataString("https://www.youtube.com/"));

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

    // At start (MainApplication), before anything downloads.
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
        if (signed.Count == 0) return HasFile ? L.T("Nessun sito della lista (forse un altro)") : L.T("Nessuno");
        var names = string.Join(", ", Sites.Where(s => signed.Contains(s.Name)).Select(s => s.Name));
        return App.Host?.Settings.UseCookies == false ? names + " · " + L.T("non usati (spento)") : names;
    }

    // ------------------------------------------------------------------ the page

    private static readonly HashSet<string> Visited = new();

    // Hosts the login page went through (for the copy without the database, below).
    internal static void Visit(string? host)
    {
        if (string.IsNullOrEmpty(host)) return;
        lock (Visited) Visited.Add(host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host);
    }

    private static TaskCompletionSource? _open;

    // The site's page inside the app; the task ends once it's closed and its cookies are saved.
    public static Task OpenAsync(string url, string? siteName)
    {
        var activity = MainActivity.Current;
        if (activity == null) return Task.CompletedTask;
        _open?.TrySetResult();
        var open = _open = new TaskCompletionSource();
        try
        {
            var intent = new Intent(activity, typeof(LoginActivity));
            intent.PutExtra(LoginActivity.ExtraUrl, url);
            if (siteName != null) intent.PutExtra(LoginActivity.ExtraSite, siteName);
            if (Ui.Find<Avalonia.Media.IBrush>("AccentBrush") is Avalonia.Media.ISolidColorBrush accent)
                intent.PutExtra(LoginActivity.ExtraAccent, unchecked((int)accent.Color.ToUInt32()));
            activity.StartActivity(intent);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            open.TrySetResult();
        }
        return open.Task;
    }

    // (the login page closed, on the main thread)
    internal static async void Closed()
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
        _open?.TrySetResult();
    }

    // Is this site signed in right now in the web pages (the login page's bar)?
    internal static bool SignedInNow(LoginSite site)
    {
        try
        {
            var cm = CookieManager.Instance;
            if (cm == null) return false;
            foreach (var d in site.Domains)
                foreach (var url in new[] { "https://" + d + "/", "https://www." + d + "/" })
                    if (cm.GetCookie(url) is { } all && all.Split(';').Any(kv => site.Auth.Any(a => kv.TrimStart().StartsWith(a + "=", StringComparison.Ordinal) && kv.Trim().Length > a.Length + 1)))
                        return true;
        }
        catch { }
        return false;
    }

    // ------------------------------------------------------------------ the file from the web pages' cookies

    // Every cookie of the app's web pages into cookies.txt: read from the web view's own database (each one with its
    // domain, path and expiry), or, if that can't be read, asked to the web view for the known sites and the pages visited.
    public static async Task ExportAsync()
    {
        try { CookieManager.Instance?.Flush(); } catch (Exception ex) { App.Log(ex); }
        var dataDir = Android.App.Application.Context.ApplicationInfo?.DataDir;
        var cookies = dataDir != null ? await Task.Run(() => FromDatabase(dataDir)) : null;
        if (cookies == null || cookies.Count == 0) cookies = FromCookieManager();
        await Task.Run(() => Write(cookies));
        Changed?.Invoke();
    }

    private static List<Cookie>? FromDatabase(string dataDir)
    {
        var db = new[] { Path.Combine(dataDir, "app_webview", "Default", "Cookies"), Path.Combine(dataDir, "app_webview", "Cookies") }.FirstOrDefault(File.Exists);
        if (db == null) return null;
        // A copy: the web view keeps its database open (and locked).
        var dir = Path.Combine(Path.GetTempPath(), "ump-webcookies");
        var copy = Path.Combine(dir, "Cookies");
        string[] parts = { "", "-journal", "-wal", "-shm" };
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var s in parts)
            {
                if (File.Exists(db + s)) File.Copy(db + s, copy + s, true);
                else if (File.Exists(copy + s)) File.Delete(copy + s);
            }
            var list = new List<Cookie>();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (var sql = SQLiteDatabase.OpenDatabase(copy, null, DatabaseOpenFlags.OpenReadwrite | DatabaseOpenFlags.NoLocalizedCollators))
            using (var c = sql!.RawQuery("SELECT host_key, name, value, path, expires_utc, is_secure FROM cookies", null))
            {
                while (c != null && c.MoveToNext())
                {
                    string? host = c.GetString(0), name = c.GetString(1), value = c.GetString(2);
                    if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value)) continue;
                    // Chromium's time: microseconds since 1601.
                    long expires = c.GetLong(4);
                    long unix = expires > 0 ? expires / 1_000_000 - 11644473600L : 0;
                    if (unix < 0) unix = 0;
                    if (unix != 0 && unix < now) continue;
                    list.Add(new Cookie(host, string.IsNullOrEmpty(c.GetString(3)) ? "/" : c.GetString(3)!, c.GetInt(5) != 0, unix, name, value));
                }
            }
            return list;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
        finally
        {
            foreach (var s in parts)
                try { File.Delete(copy + s); } catch { }
        }
    }

    private static List<Cookie> FromCookieManager()
    {
        var list = new List<Cookie>();
        try
        {
            var cm = CookieManager.Instance;
            if (cm == null) return list;
            long expires = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
            List<string> domains;
            lock (Visited) domains = Sites.SelectMany(s => s.Domains).Concat(Visited).Distinct().ToList();
            foreach (var d in domains)
                foreach (var url in new[] { "https://" + d + "/", "https://www." + d + "/" })
                {
                    if (cm.GetCookie(url) is not { Length: > 0 } all) continue;
                    foreach (var part in all.Split(';'))
                    {
                        var kv = part.Trim();
                        int eq = kv.IndexOf('=');
                        if (eq <= 0 || eq == kv.Length - 1) continue;
                        list.Add(new Cookie("." + d, "/", true, expires, kv[..eq], kv[(eq + 1)..]));
                    }
                }
        }
        catch (Exception ex) { App.Log(ex); }
        return list.DistinctBy(c => (c.Host, c.Name)).ToList();
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

    // The site's cookies (of all its domains) expire in the web pages too, then the file is written again without them.
    public static async Task SignOutAsync(LoginSite site)
    {
        try
        {
            var cm = CookieManager.Instance;
            if (cm != null)
            {
                var names = new HashSet<(string Name, string Path)>();
                foreach (var c in ReadFile().Where(c => site.Domains.Any(d => OfDomain(c.Host, d)))) names.Add((c.Name, c.Path));
                foreach (var d in site.Domains)
                    foreach (var url in new[] { "https://" + d + "/", "https://www." + d + "/" })
                        if (cm.GetCookie(url) is { Length: > 0 } all)
                            foreach (var kv in all.Split(';'))
                                if (kv.Trim() is var t && t.IndexOf('=') is > 0 and var eq) names.Add((t[..eq], "/"));
                foreach (var d in site.Domains)
                    foreach (var (name, path) in names)
                        foreach (var host in new[] { d, "www." + d })
                        {
                            // As a cookie of the whole domain and as one of that host only: either may be the one there.
                            cm.SetCookie($"https://{host}{path}", $"{name}=; Max-Age=0; Path={path}; Domain=.{d}; Secure");
                            cm.SetCookie($"https://{host}{path}", $"{name}=; Max-Age=0; Path={path}; Secure");
                        }
            }
        }
        catch (Exception ex) { App.Log(ex); }
        await ExportAsync();
    }

    // Every site: the web pages forget everything, the file goes.
    public static async Task SignOutAllAsync()
    {
        try
        {
            var done = new TaskCompletionSource();
            var cm = CookieManager.Instance;
            if (cm != null)
            {
                cm.RemoveAllCookies(new Callback(() => done.TrySetResult()));
                await Task.WhenAny(done.Task, Task.Delay(3000));
                cm.Flush();
            }
            WebStorage.Instance?.DeleteAllData();
        }
        catch (Exception ex) { App.Log(ex); }
        try { File.Delete(FilePath); } catch { }
        Changed?.Invoke();
    }

    private sealed class Callback : Java.Lang.Object, IValueCallback
    {
        private readonly Action _done;
        public Callback(Action done) => _done = done;
        public void OnReceiveValue(Java.Lang.Object? value) => _done();
    }
}
