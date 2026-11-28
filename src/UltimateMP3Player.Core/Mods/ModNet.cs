using System.Globalization;
using System.Net;
using System.Text;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Runtime;

namespace UltimateMP3Player.Core.Mods;

// Links of a site a mod takes (ump.links.add): pasted in the app, they go to the mod instead of the downloads (an osu! beatmap's
// page → its .osz). Only sites the mod names in mod.json (hosts), so no mod takes another site's links.
public sealed record ModLink(ModRuntime Mod, string Id, string Title, IReadOnlyList<string> Match, JsValue Run)
{
    public bool Matches(string url)
    {
        var bare = Bare(url);
        return Match.Any(m => bare.StartsWith(m, StringComparison.OrdinalIgnoreCase));
    }

    // "https://www.osu.ppy.sh/beatmapsets/1" → "osu.ppy.sh/beatmapsets/1".
    public static string Bare(string url)
    {
        var s = url.Trim();
        int scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        return s.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? s[4..] : s;
    }
}

// The internet for a mod (permission "internet"): only the sites its mod.json names, text or a file into its own data. A site the
// user signed in to in the app (cookies.txt, Settings › Downloads) gets that sign-in; the mod never sees the cookies.
public sealed partial class ModRuntime
{
    private readonly List<ModLink> _links = new();
    private const long MaxText = 8L * 1024 * 1024, MaxDownload = 2L * 1024 * 1024 * 1024;
    // (redirects by hand: a sign-in goes only to its own site, never to where the site sends the request next)
    private static readonly HttpClient Net = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public IReadOnlyList<ModLink> Links { get { lock (_links) return _links.ToList(); } }

    private bool HostAllowed(string host) =>
        Manifest.Hosts.Any(h => host.Equals(h, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    private Uri Allowed(string url)
    {
        Need(ModPermissions.Internet);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
            throw new JavaScriptException("net: not a web address: " + url);
        if (!HostAllowed(u.Host)) throw new JavaScriptException($"net: \"{u.Host}\" isn't one of the sites in mod.json (hosts).");
        return u;
    }

    private void AddNet(Engine e, JsObject ump)
    {
        var net = new JsObject(e);
        (bool Cookies, string? Referer) Options(JsValue[] a, int i) => Arg(a, i) is JsObject o
            ? (TypeConverter.ToBoolean(o.Get("signIn")), Nil(o.Get("referer")) ? null : TypeConverter.ToString(o.Get("referer")))
            : (false, null);
        // text(url, { signIn, referer }): a page or an API's answer, as text (≤ 8 MB).
        Fn(e, net, "text", a =>
        {
            var uri = Allowed(Str(a, 0));
            var (cookies, referer) = Options(a, 1);
            var ct = _stop!.Token;
            return Promise(e, Task.Run(async () =>
            {
                using var resp = await SendAsync(uri, cookies, referer, ct);
                if (!resp.IsSuccessStatusCode) throw new ModException($"{uri.Host} answered {(int)resp.StatusCode}.");
                if (resp.Content.Headers.ContentLength > MaxText) throw new ModException("Too big to read as text (8 MB).");
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length > MaxText) throw new ModException("Too big to read as text (8 MB).");
                return Encoding.UTF8.GetString(bytes);
            }, ct), s => s);
        });
        // download(url, { name, signIn, referer, progress(done, total) }): a file into the mod's data (downloads/<name>), its path.
        Fn(e, net, "download", a =>
        {
            var uri = Allowed(Str(a, 0));
            var (cookies, referer) = Options(a, 1);
            var o = Arg(a, 1) as JsObject;
            var name = Safe(o != null && !Nil(o.Get("name")) ? TypeConverter.ToString(o.Get("name")) : Path.GetFileName(uri.LocalPath) is { Length: > 0 } n ? n : "download");
            var progress = o?.Get("progress") as Function;
            var target = Path.Combine(DataDir, "downloads", name);
            var ct = _stop!.Token;
            long said = 0;
            void Progress(long done, long? total)
            {
                // (four times a second at most)
                long now = Environment.TickCount64;
                if (progress == null || now - Interlocked.Read(ref said) < 250) return;
                Interlocked.Exchange(ref said, now);
                Post(() => Call("net.download progress", progress, done, total is { } t ? t : JsValue.Null));
            }
            return Promise(e, Task.Run(() => DownloadAsync(uri, cookies, referer, target, Progress, ct), ct), p => p);
        });
        ump.Set("net", net);

        var links = new JsObject(e);
        Fn(e, links, "add", a =>
        {
            if (Arg(a, 0) is not JsObject o || o.Get("run") is not Function) throw new JavaScriptException("links.add needs { id, match, run(url) }.");
            var id = Nil(o.Get("id")) ? "" : TypeConverter.ToString(o.Get("id"));
            if (id.Length is 0 or > 40) throw new JavaScriptException("A link handler needs an id (at most 40 characters).");
            var match = o.Get("match") is JsArray m ? m.Select(v => ModLink.Bare(TypeConverter.ToString(v))).Where(s => s.Contains('.') && s.Length >= 4).Take(20).ToList() : new List<string>();
            if (match.Count == 0) throw new JavaScriptException("links.add needs match: the links' start, like \"osu.ppy.sh/beatmapsets/\".");
            Need(ModPermissions.Internet);
            if (match.FirstOrDefault(s => !HostAllowed(s.Split('/')[0])) is { } other)
                throw new JavaScriptException($"links.add: \"{other}\" isn't one of the sites in mod.json (hosts).");
            var title = Short(Nil(o.Get("title")) ? id : TypeConverter.ToString(o.Get("title")), 60);
            lock (_links)
            {
                _links.RemoveAll(l => l.Id == id);
                if (_links.Count >= 10) throw new JavaScriptException("At most 10 link handlers per mod.");
                _links.Add(new ModLink(this, id, title, match, o.Get("run")));
            }
            return JsValue.Undefined;
        });
        Fn(e, links, "remove", a => { lock (_links) _links.RemoveAll(l => l.Id == Str(a, 0)); return JsValue.Undefined; });
        ump.Set("links", links);

        // The app's own choices a mod may follow: "videos" (Settings › Downloads: videos with the songs).
        var prefs = new JsObject(e);
        Fn(e, prefs, "get", a => Str(a, 0) switch
        {
            "videos" => _host.WantsVideos,
            _ => JsValue.Undefined,
        });
        ump.Set("prefs", prefs);
    }

    // A link the user pasted, for this mod's handler.
    public void RunLink(ModLink link, string url) => Post(() => Call("link " + link.Id, link.Run, url.Trim()));

    private async Task<HttpResponseMessage> SendAsync(Uri uri, bool cookies, string? referer, CancellationToken ct)
    {
        var jar = cookies ? _host.CookieFile : null;
        for (int hop = 0; hop < 8; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.UserAgent.ParseAdd("UltimateMP3Player/" + _host.AppVersion);
            if (referer != null && Uri.TryCreate(referer, UriKind.Absolute, out var r)) req.Headers.Referrer = r;
            if (jar != null && CookieHeader(jar, uri) is { } c) req.Headers.TryAddWithoutValidation("Cookie", c);
            var resp = await Net.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } to)
            {
                resp.Dispose();
                uri = to.IsAbsoluteUri ? to : new Uri(uri, to);
                if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) throw new ModException("Sent to something that isn't a web page.");
                continue;
            }
            return resp;
        }
        throw new ModException("Too many redirects.");
    }

    private async Task<string> DownloadAsync(Uri uri, bool cookies, string? referer, string target, Action<long, long?> progress, CancellationToken ct)
    {
        using var resp = await SendAsync(uri, cookies, referer, ct);
        if (!resp.IsSuccessStatusCode) throw new ModException($"{uri.Host} answered {(int)resp.StatusCode}.");
        long? total = resp.Content.Headers.ContentLength;
        if (total > MaxDownload) throw new ModException("Too big to download (2 GB).");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var part = target + ".part";
        try
        {
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
                for (int n; ;)
                {
                    // (a site that stops sending for a minute: given up)
                    quiet.CancelAfter(TimeSpan.FromSeconds(60));
                    if ((n = await src.ReadAsync(buffer, quiet.Token)) <= 0) break;
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if (done > MaxDownload) throw new ModException("Too big to download (2 GB).");
                    progress(done, total);
                }
            }
            File.Move(part, target, true);
            return target;
        }
        catch
        {
            try { File.Delete(part); } catch (Exception) { }
            throw;
        }
    }

    // The "Cookie" header of a cookies.txt (Netscape's format, as yt-dlp reads it) for an address: its site's, still valid.
    public static string? CookieHeader(string file, Uri uri)
    {
        try
        {
            if (!File.Exists(file)) return null;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var parts = new List<string>();
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.StartsWith("#HttpOnly_", StringComparison.Ordinal) ? raw[10..] : raw;
                if (line.Length == 0 || line[0] == '#') continue;
                var p = line.Split('\t');
                if (p.Length < 7) continue;
                var domain = p[0].TrimStart('.');
                bool site = uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
                if (!site || !uri.AbsolutePath.StartsWith(p[2], StringComparison.Ordinal)) continue;
                if (p[3] == "TRUE" && uri.Scheme != Uri.UriSchemeHttps) continue;
                if (long.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expires) && expires != 0 && expires < now) continue;
                parts.Add(p[5] + "=" + p[6]);
            }
            return parts.Count > 0 ? string.Join("; ", parts) : null;
        }
        catch (Exception) { return null; }
    }
}
