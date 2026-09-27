using System.Diagnostics;
using System.Net;

namespace UltimateMP3Player.Core;

public static class Http
{
    public const string BrowserUA =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

    // Link-preview bots get server-rendered Open Graph pages.
    public const string PreviewUA = "facebookexternalhit/1.1";

    public static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 10,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(20),
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    private static HttpRequestMessage Request(HttpMethod method, string url, string? ua, string? referer,
        IDictionary<string, string>? headers = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("User-Agent", ua ?? BrowserUA);
        req.Headers.TryAddWithoutValidation("Accept-Language", "it-IT,it;q=0.9,en-US;q=0.8,en;q=0.7");
        if (referer != null) req.Headers.TryAddWithoutValidation("Referer", referer);
        if (headers != null)
            foreach (var (k, v) in headers)
            {
                req.Headers.Remove(k);
                req.Headers.TryAddWithoutValidation(k, v);
            }
        return req;
    }

    public static async Task<string> GetStringAsync(string url, string? ua = null, string? referer = null,
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var req = Request(HttpMethod.Get, url, ua, referer);
        using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(cts.Token);
    }

    public static async Task<byte[]> GetBytesAsync(string url, string? referer = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var req = Request(HttpMethod.Get, url, null, referer);
        using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(cts.Token);
    }

    // Follows redirects (short links): final URL and content type.
    public static async Task<(string Url, string? ContentType, long? Length)> ProbeAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        using var req = Request(HttpMethod.Get, url, null, null);
        using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var final = resp.RequestMessage?.RequestUri?.ToString() ?? url;
        return (final, resp.Content.Headers.ContentType?.MediaType, resp.Content.Headers.ContentLength);
    }

    public static async Task DownloadFileAsync(string url, string path, IDictionary<string, string>? headers,
        Action<long, long?>? progress, CancellationToken ct, string? referer = null)
    {
        using var req = Request(HttpMethod.Get, url, null, referer, headers);
        using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new EngineException(resp.StatusCode switch
            {
                HttpStatusCode.Forbidden => L.T("Il sito ha negato l'accesso al file (errore 403)."),
                HttpStatusCode.NotFound => L.T("Il file non esiste più (errore 404)."),
                HttpStatusCode.Unauthorized => L.T("Il sito richiede l'accesso (login) per questo file."),
                _ => L.F("Download non riuscito (HTTP {0}).", (int)resp.StatusCode),
            }, url);
        long? total = resp.Content.Headers.ContentLength;
        var tmp = path + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
        {
            var buf = new byte[1 << 16];
            long done = 0;
            var sw = Stopwatch.StartNew();
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (sw.ElapsedMilliseconds > 250) { progress?.Invoke(done, total); sw.Restart(); }
            }
            progress?.Invoke(done, total ?? done);
        }
        File.Move(tmp, path, true);
    }
}
