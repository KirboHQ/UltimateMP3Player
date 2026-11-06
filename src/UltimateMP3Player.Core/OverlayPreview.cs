using System.Buffers.Text;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace UltimateMP3Player.Core;

// A stream overlay moving inside its editor. The computers' apps have no web view of their own, so a browser of this computer opens
// the page, without a window and with a throwaway profile, and sends pictures of it, a picture at a time: Edge, Chrome and Brave by the
// DevTools screencast, the next one only after this one is taken; Firefox hasn't got that, it's asked for each one (WebDriver BiDi).
// Only while the editor is open, and nothing is installed or added to the app.
public sealed class OverlayPreview : IDisposable
{
    // Pictures received since the start, and browsers running: for the tests.
    public static long Frames;
    public static int Running;
    // For the tests: this browser instead of the one found ("": none).
    public static string? Forced;

    private const int MinFrameMs = 8, MinFirefoxFrameMs = 16;
    private readonly CancellationTokenSource _stop = new();
    private Process? _browser;
    private string? _profile;
    private IntPtr _job;
    private int _closed;
    private readonly object _lastLock = new();
    private byte[]? _last;
    private int _lastSize;
    // (one array for all the pictures, nothing made per picture: they're big enough to wake the slowest of the garbage collections)
    private byte[] _picture = new byte[512 * 1024];

    // As many pixels as the screen shows, at most a quarter more than the page's: the picture stays sharp, and each frame costs far
    // less than at 1.5 (43 → 30 % of a core).
    private const double MostSharp = 1.25;

    // frame: the JPEG in the first bytes of an array kept for all of them, to be read before it returns. noPicture: it ended without
    // a single one (no browser, or it failed) and not because it was closed. Both off the UI thread.
    public OverlayPreview(string url, int width, int height, double screenScale, Action<byte[], int> frame, Action noPicture)
        => _ = Task.Run(() => Run(url, width, height, Math.Clamp(screenScale, 1, MostSharp), frame, noPicture));

    // The last picture received, as a file: what the settings' list shows of this source. False if none came.
    public bool SaveLast(string path)
    {
        lock (_lastLock)
        {
            if (_last == null) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var file = File.Create(path);
            file.Write(_last, 0, _lastSize);
            return true;
        }
    }

    // Firefox last: a picture costs it more than a screencast's.
    public static string? FindBrowser() => Browsers().FirstOrDefault();

    public static bool IsFirefox(string exe) => Path.GetFileName(exe).StartsWith("firefox", StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<string> Browsers()
    {
        if (Forced != null) return Forced.Length > 0 ? new[] { Forced } : Array.Empty<string>();
        IEnumerable<string> places;
        if (OperatingSystem.IsWindows())
        {
            var roots = new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData }.Select(Environment.GetFolderPath);
            places = new[] { @"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe", @"BraveSoftware\Brave-Browser\Application\brave.exe", @"Mozilla Firefox\firefox.exe" }
                .SelectMany(browser => roots.Select(root => Path.Combine(root, browser)));
        }
        else if (OperatingSystem.IsMacOS())
            places = new[] { "Google Chrome", "Microsoft Edge", "Chromium", "Brave Browser" }.Select(n => $"/Applications/{n}.app/Contents/MacOS/{n}").Append("/Applications/Firefox.app/Contents/MacOS/firefox");
        else
            places = new[] { "google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge", "brave-browser", "firefox", "firefox-esr" }
                .SelectMany(n => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Select(d => Path.Combine(d, n)));
        return places.Where(File.Exists);
    }

    // A throwaway profile for this browser.
    internal static string NewProfile(string exe, string name)
    {
        // (Ubuntu's Firefox is a snap: it doesn't see this /tmp, only its own corner of the home folder)
        var profiles = IsFirefox(exe) && File.Exists("/snap/bin/firefox") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "snap", "firefox", "common") : Path.GetTempPath();
        var profile = Path.Combine(profiles, name + "-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(profile);
        // (an app that crashed or was killed left its browser's profile behind)
        foreach (var old in Directory.GetDirectories(profiles, name + "-*"))
            if (Directory.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-1))
                try { Directory.Delete(old, true); } catch { }
        return profile;
    }

    private async Task Run(string url, int width, int height, double sharp, Action<byte[], int> frame, Action noPicture)
    {
        try
        {
            if (FindBrowser() is not { } exe) return;
            var ct = _stop.Token;
            bool firefox = IsFirefox(exe);
            _profile = NewProfile(exe, "ump-preview");
            // (it would draw the page as often as the screen refreshes: at 199 Hz, a whole core; at 60, with 36 pictures a second, 56 %)
            if (firefox) File.WriteAllText(Path.Combine(_profile, "user.js"), "user_pref(\"layout.frame_rate\", 60);\n");
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            // (--wait-for-browser: on Windows its first process would leave at once, the real one running on its own)
            foreach (var arg in firefox
                         ? new[] { "--headless", "--no-remote", "--profile", _profile, "--remote-debugging-port=0" }.Concat(OperatingSystem.IsWindows() ? new[] { "--wait-for-browser" } : Array.Empty<string>()).Append("about:blank")
                         : new[]
                     {
                         // (--disable-gpu: drawn on the processor costs less here than its stand-in for a GPU, 71 → 57 % of a core, and leaves the real one to OBS and the games)
                         "--headless=new", "--disable-gpu", $"--window-size={width},{height}", "--force-device-scale-factor=" + sharp.ToString(System.Globalization.CultureInfo.InvariantCulture),
                         "--remote-debugging-port=0", "--user-data-dir=" + _profile, "--no-first-run",
                         "--no-default-browser-check", "--disable-extensions", "--disable-sync", "--disable-background-networking", "--disable-component-update",
                         "--mute-audio", "--hide-scrollbars", url,
                     })
                start.ArgumentList.Add(arg);
            _browser = Process.Start(start);
            if (_browser == null) return;
            _browser.OutputDataReceived += (_, _) => { };
            // (Firefox says where it's listening here: a snap's profile isn't where this app can read it)
            const string Listening = "WebDriver BiDi listening on ";
            var listening = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _browser.ErrorDataReceived += (_, e) =>
            {
                if (e.Data?.StartsWith(Listening, StringComparison.Ordinal) == true) listening.TrySetResult(e.Data[Listening.Length..]);
            };
            _browser.BeginOutputReadLine();
            _browser.BeginErrorReadLine();
            // (a preview gives way: the music and the windows never wait for it)
            // shortcut: Firefox's first process starts the others at normal priority; a job limit if it ever shows.
            try { _browser.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            EndWithThisApp(_browser);
            Interlocked.Increment(ref Running);
            if (firefox)
            {
                await FromFirefox(listening.Task, url, width, height, sharp, frame, ct);
                return;
            }
            // (it writes down the port it was given)
            var portFile = Path.Combine(_profile, "DevToolsActivePort");
            string? port = null;
            for (int i = 0; i < 200 && port == null; i++)
            {
                await Task.Delay(50, ct);
                try { port = File.Exists(portFile) ? File.ReadLines(portFile).FirstOrDefault(l => l.Length > 0) : null; }
                catch (IOException) { }
            }
            if (port == null) return;
            using var http = new HttpClient();
            string? socket = null;
            for (int i = 0; i < 40 && socket == null; i++)
            {
                using var list = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct));
                socket = list.RootElement.EnumerateArray().Where(t => t.GetProperty("type").GetString() == "page").Select(t => t.GetProperty("webSocketDebuggerUrl").GetString()).FirstOrDefault();
                if (socket == null) await Task.Delay(100, ct);
            }
            if (socket == null) return;
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(socket), ct);
            int id = 0;
            Task Send(string method, string args) => ws.SendAsync(Encoding.UTF8.GetBytes($"{{\"id\":{++id},\"method\":\"{method}\",\"params\":{args}}}"), WebSocketMessageType.Text, true, ct);
            await Send("Page.enable", "{}");
            await Send("Page.startScreencast", $"{{\"format\":\"jpeg\",\"quality\":82,\"maxWidth\":{(int)(width * sharp)},\"maxHeight\":{(int)(height * sharp)},\"everyNthFrame\":1}}");
            var buffer = new byte[256 * 1024];
            using var message = new MemoryStream();
            var clock = Stopwatch.StartNew();
            while (ws.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult got;
                do
                {
                    got = await ws.ReceiveAsync(buffer, ct);
                    message.Write(buffer, 0, got.Count);
                } while (!got.EndOfMessage);
                if (got.MessageType == WebSocketMessageType.Close) return;
                if (Show(message, frame) is not { } session) continue;
                // As many as the page is drawn, 60 a second without a window: it's drawn anyway, so they cost little more than 20 did (54 → 62 %
                // of a core). Past that, a page drawn with no limit: 280 %. The browser waits for the answer before making the next one.
                int wait = MinFrameMs - (int)clock.ElapsedMilliseconds;
                if (wait > 0) await Task.Delay(wait, ct);
                clock.Restart();
                await Send("Page.screencastFrameAck", $"{{\"sessionId\":{session}}}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is HttpRequestException or WebSocketException or JsonException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception or FormatException or NotSupportedException or KeyNotFoundException or IndexOutOfRangeException)
        {
            // (no preview: the editor goes on, with the picture of the look)
            AppPaths.AppendLog($"[{DateTime.Now:s}] Overlay preview: {e.Message}\n\n");
        }
        finally
        {
            if (_lastSize == 0 && !_stop.IsCancellationRequested) noPicture();
            Dispose();
        }
    }

    // Asked for one picture at a time: the answer comes when it's made, the next is asked for after it. Told to report nothing, so what
    // comes after a question is its answer.
    private async Task FromFirefox(Task<string> listening, string url, int width, int height, double sharp, Action<byte[], int> frame, CancellationToken ct)
    {
        if (await Task.WhenAny(listening, _browser!.WaitForExitAsync(ct), Task.Delay(30000, ct)) != listening) return;
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(await listening + "/session"), ct);
        var buffer = new byte[256 * 1024];
        using var message = new MemoryStream();
        int id = 0;
        async Task Ask(string method, string args)
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes($"{{\"id\":{++id},\"method\":\"{method}\",\"params\":{args}}}"), WebSocketMessageType.Text, true, ct);
            message.SetLength(0);
            WebSocketReceiveResult got;
            do
            {
                got = await ws.ReceiveAsync(buffer, ct);
                message.Write(buffer, 0, got.Count);
            } while (!got.EndOfMessage);
        }
        await Ask("session.new", "{\"capabilities\":{}}");
        await Ask("browsingContext.getTree", "{\"maxDepth\":0}");
        string? tab;
        using (var tree = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length)))
            tab = tree.RootElement.GetProperty("result").GetProperty("contexts")[0].GetProperty("context").GetString();
        await Ask("browsingContext.setViewport", $"{{\"context\":\"{tab}\",\"viewport\":{{\"width\":{width},\"height\":{height}}},\"devicePixelRatio\":{sharp.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");
        await Ask("browsingContext.navigate", $"{{\"context\":\"{tab}\",\"url\":{JsonSerializer.Serialize(url)},\"wait\":\"interactive\"}}");
        string picture = $"{{\"context\":\"{tab}\",\"format\":{{\"type\":\"image/jpeg\",\"quality\":0.82}}}}";
        var clock = Stopwatch.StartNew();
        // (no picture for a moment can happen; for ten seconds, it won't come)
        for (int none = 0; ws.State == WebSocketState.Open && none < 300;)
        {
            await Ask("browsingContext.captureScreenshot", picture);
            none = Show(message, frame) != null ? 0 : none + 1;
            int wait = MinFirefoxFrameMs - (int)clock.ElapsedMilliseconds;
            if (wait > 0) await Task.Delay(wait, ct);
            clock.Restart();
        }
    }

    // The picture in this message, if it has one: kept as the last and shown. Its screencast session, if any.
    private int? Show(MemoryStream message, Action<byte[], int> frame)
    {
        (int Size, int Session) said;
        lock (_lastLock)
        {
            if (ReadFrame(message.GetBuffer().AsSpan(0, (int)message.Length), ref _picture) is not { } read) return null;
            said = read;
            _last = _picture;
            _lastSize = read.Size;
        }
        frame(_picture, said.Size);
        Interlocked.Increment(ref Frames);
        return said.Session;
    }

    // The picture decoded straight from the message into the array, null for a message without one. Screencast:
    // {"method":"Page.screencastFrame","params":{"data":"<the picture in base64>","metadata":{...},"sessionId":1}}; Firefox: {"id":9,"result":{"data":"<the same>"},"type":"success"}.
    private static (int Size, int Session)? ReadFrame(ReadOnlySpan<byte> json, ref byte[] picture)
    {
        var reader = new Utf8JsonReader(json);
        int size = 0, session = 0;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            if (reader.CurrentDepth == 1 && reader.ValueTextEquals("method"))
            {
                reader.Read();
                if (!reader.ValueTextEquals("Page.screencastFrame")) return null;
            }
            else if (reader.CurrentDepth == 2 && reader.ValueTextEquals("data"))
            {
                reader.Read();
                var text = reader.ValueSpan;
                int most = Base64.GetMaxDecodedFromUtf8Length(text.Length);
                if (picture.Length < most) picture = new byte[most * 5 / 4];
                if (Base64.DecodeFromUtf8(text, picture, out _, out size) != System.Buffers.OperationStatus.Done) return null;
            }
            else if (reader.CurrentDepth == 2 && reader.ValueTextEquals("sessionId"))
            {
                reader.Read();
                session = reader.GetInt32();
            }
        }
        return size > 0 ? (size, session) : null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _stop.Cancel();
        var browser = _browser;
        var profile = _profile;
        var job = _job;
        // (off the UI thread: ending a browser and deleting its folder take a moment)
        _ = Task.Run(async () =>
        {
            if (browser != null)
            {
                Interlocked.Decrement(ref Running);
                try
                {
                    if (!browser.HasExited) browser.Kill(true);
                    await browser.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or TimeoutException) { }
                browser.Dispose();
            }
            if (job != IntPtr.Zero) CloseHandle(job);
            for (int i = 0; i < 10 && profile != null && Directory.Exists(profile); i++)
            {
                try { Directory.Delete(profile, true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await Task.Delay(300); }
            }
        });
    }

    // On Windows it goes when this app goes, even if the app dies: a job that ends what's in it with its last handle.
    // shortcut: on Linux and macOS a crash with the editor open leaves the browser running; a watchdog if that ever shows up.
    private void EndWithThisApp(Process browser)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return;
            // (JOBOBJECT_EXTENDED_LIMIT_INFORMATION, its LimitFlags: JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE)
            var limits = new byte[IntPtr.Size == 8 ? 144 : 112];
            BitConverter.TryWriteBytes(limits.AsSpan(IntPtr.Size == 8 ? 16 : 8), 0x2000);
            if (SetInformationJobObject(job, 9, limits, limits.Length) && AssignProcessToJobObject(job, browser.Handle)) _job = job;
            else CloseHandle(job);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, byte[] info, int size);
    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
