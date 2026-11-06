using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace UltimateMP3Player.Core;

public sealed record BrowserCookie(string Host, string Path, bool Secure, long Expires, string Name, string Value);

// Signing in to a site on a computer, where the apps have no web view of their own: a window of a browser of this computer opens on the
// site's page, with a profile of its own, and the person signs in as usual (the password goes only to the site). Nothing drives that window,
// or Google refuses it ("this browser may not be secure"). Once it's closed the same browser opens the profile again without a window and is
// asked for its cookies, which is what Chrome no longer lets anyone else read; then the profile goes. With nothing left running them, YouTube's
// cookies aren't replaced behind the app's back, as they are in a browser kept open. Cookies that last only while the window is open are lost.
public sealed class BrowserSignIn
{
    private readonly string _exe, _profile;
    private readonly bool _firefox;
    private readonly Process _window;
    private Process? _reader;
    private int _ended;

    // The window was closed: by the person, or after Finish.
    public Task Closed { get; }

    // Firefox first, when there's one: Chrome and Edge may tie Google's sign-ins to the computer they were made on, useless to the engines.
    public static string? FindBrowser() => OverlayPreview.Browsers().OrderBy(b => OverlayPreview.IsFirefox(b) ? 0 : 1).FirstOrDefault();

    // unseen, for the tests: no window on the screens (Firefox: none at all, and its page may close it).
    public BrowserSignIn(string exe, string url, bool unseen = false)
    {
        _exe = exe;
        _firefox = OverlayPreview.IsFirefox(exe);
        _profile = OverlayPreview.NewProfile(exe, "ump-signin");
        var args = new List<string>();
        if (_firefox)
        {
            // (a new profile would open its welcome pages and questions first)
            File.WriteAllText(Path.Combine(_profile, "user.js"),
                "user_pref(\"browser.aboutwelcome.enabled\", false);\n" +
                "user_pref(\"browser.shell.checkDefaultBrowser\", false);\n" +
                "user_pref(\"browser.startup.homepage_override.mstone\", \"ignore\");\n" +
                "user_pref(\"datareporting.policy.dataSubmissionPolicyBypassNotification\", true);\n" +
                (unseen ? "user_pref(\"dom.allow_scripts_to_close_windows\", true);\n" : ""));
            args.AddRange(FirefoxArgs());
            if (unseen) args.Add("--headless");
        }
        else
        {
            args.AddRange(new[] { "--user-data-dir=" + _profile, "--no-first-run", "--no-default-browser-check", "--disable-sync" });
            if (unseen) args.Add("--window-position=-32000,-32000");
        }
        args.Add(url);
        _window = Start(args, null);
        Closed = _window.WaitForExitAsync();
    }

    // (--wait-for-browser: on Windows its first process would leave at once, the real one running on its own)
    private IEnumerable<string> FirefoxArgs()
    {
        yield return "--no-remote";
        yield return "--profile";
        yield return _profile;
        if (OperatingSystem.IsWindows()) yield return "--wait-for-browser";
    }

    private Process Start(IEnumerable<string> args, Action<string>? said)
    {
        var start = new ProcessStartInfo(_exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new InvalidOperationException(_exe);
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) said?.Invoke(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    // As if the person closed the window, so the browser saves its cookies on the way out. A window of this process: closed, 100 ms (taskkill
    // would write to its hidden ones and it would stay). None: Firefox on Windows, its window is its second process's, reached by taskkill.
    // Elsewhere told to quit (a macOS browser stays open without windows).
    public void Finish()
    {
        if (_window.HasExited) return;
        try
        {
            _window.Refresh();
            if (!OperatingSystem.IsWindows() || !_window.CloseMainWindow())
            {
                var (tool, args) = OperatingSystem.IsWindows() ? ("taskkill", $"/PID {_window.Id} /T") : ("kill", $"-TERM {_window.Id}");
                using var ask = Process.Start(new ProcessStartInfo(tool, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true });
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppPaths.AppendLog($"[{DateTime.Now:s}] Sign-in: {e.Message}\n\n");
        }
        // (a window asking something back, or that didn't hear: it won't hold the app up)
        _ = Task.Delay(10000).ContinueWith(_ => Stop(_window), TaskScheduler.Default);
    }

    // Once it's closed. Empty if they can't be read.
    public async Task<List<BrowserCookie>> CookiesAsync(CancellationToken ct)
    {
        try
        {
            using var ws = new ClientWebSocket();
            if (_firefox)
            {
                const string Listening = "WebDriver BiDi listening on ";
                var listening = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _reader = Start(FirefoxArgs().Concat(new[] { "--headless", "--remote-debugging-port=0", "about:blank" }), line =>
                {
                    if (line.StartsWith(Listening, StringComparison.Ordinal)) listening.TrySetResult(line[Listening.Length..]);
                });
                if (await Task.WhenAny(listening.Task, _reader.WaitForExitAsync(ct), Task.Delay(30000, ct)) != listening.Task) return new();
                await ws.ConnectAsync(new Uri(await listening.Task + "/session"), ct);
                using (await Ask(ws, 1, "session.new", "{\"capabilities\":{}}", ct)) { }
                using var answer = await Ask(ws, 2, "storage.getCookies", "{}", ct);
                return Read(answer, firefox: true);
            }
            _reader = Start(new[] { "--headless=new", "--disable-gpu", "--remote-debugging-port=0", "--user-data-dir=" + _profile, "--no-first-run", "--no-default-browser-check", "--disable-sync", "about:blank" }, null);
            // (it writes down the port it was given)
            var portFile = Path.Combine(_profile, "DevToolsActivePort");
            string? port = null;
            for (int i = 0; i < 400 && port == null; i++)
            {
                await Task.Delay(50, ct);
                try { port = File.Exists(portFile) ? File.ReadLines(portFile).FirstOrDefault(l => l.Length > 0) : null; }
                catch (IOException) { }
            }
            if (port == null) return new();
            using var http = new HttpClient();
            using (var version = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/version", ct)))
                await ws.ConnectAsync(new Uri(version.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!), ct);
            using var cookies = await Ask(ws, 1, "Storage.getCookies", "{}", ct);
            return Read(cookies, firefox: false);
        }
        catch (OperationCanceledException) { return new(); }
        catch (Exception e) when (e is HttpRequestException or WebSocketException or JsonException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception or FormatException or KeyNotFoundException)
        {
            AppPaths.AppendLog($"[{DateTime.Now:s}] Sign-in: {e.Message}\n\n");
            return new();
        }
    }

    // Told to report nothing, so what comes after a question is its answer.
    private static async Task<JsonDocument> Ask(ClientWebSocket ws, int id, string method, string args, CancellationToken ct)
    {
        await ws.SendAsync(Encoding.UTF8.GetBytes($"{{\"id\":{id},\"method\":\"{method}\",\"params\":{args}}}"), WebSocketMessageType.Text, true, ct);
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        WebSocketReceiveResult got;
        do
        {
            got = await ws.ReceiveAsync(buffer, ct);
            message.Write(buffer, 0, got.Count);
        } while (!got.EndOfMessage);
        return JsonDocument.Parse(message.ToArray());
    }

    // {"result":{"cookies":[{"name":"a","domain":".x.com","path":"/","secure":true, ...}]}}: Firefox's value is {"type":"string","value":"v"} and its
    // end "expiry", absent if none; the others' a plain "value" and "expires", -1 if none.
    private static List<BrowserCookie> Read(JsonDocument answer, bool firefox)
    {
        var list = new List<BrowserCookie>();
        foreach (var c in answer.RootElement.GetProperty("result").GetProperty("cookies").EnumerateArray())
        {
            var value = c.GetProperty("value");
            if (firefox && value.GetProperty("type").GetString() != "string") continue;
            string? text = firefox ? value.GetProperty("value").GetString() : value.GetString();
            double expires = c.TryGetProperty(firefox ? "expiry" : "expires", out var end) ? end.GetDouble() : 0;
            list.Add(new BrowserCookie(c.GetProperty("domain").GetString() ?? "", c.GetProperty("path").GetString() ?? "/", c.GetProperty("secure").GetBoolean(),
                expires > 0 ? (long)expires : 0, c.GetProperty("name").GetString() ?? "", text ?? ""));
        }
        return list;
    }

    private static void Stop(Process? process)
    {
        try
        {
            if (process is { HasExited: false }) process.Kill(true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    // Its browser and its profile go, whatever happened.
    public async Task EndAsync()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        await Task.Run(async () =>
        {
            foreach (var process in new[] { _window, _reader })
            {
                if (process == null) continue;
                Stop(process);
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { }
                process.Dispose();
            }
            for (int i = 0; i < 10 && Directory.Exists(_profile); i++)
            {
                try { Directory.Delete(_profile, true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await Task.Delay(300); }
            }
        });
    }
}
