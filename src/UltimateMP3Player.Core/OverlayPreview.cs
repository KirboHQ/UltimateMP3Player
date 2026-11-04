using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace UltimateMP3Player.Core;

// A stream overlay moving inside its editor. The computers' apps have no web view of their own, so Edge or Chrome of this computer
// opens the page, without a window and with a throwaway profile, and sends pictures of it (the DevTools screencast), a picture at a
// time: the next one only after this one is taken. Only while the editor is open, and nothing is installed or added to the app.
public sealed class OverlayPreview : IDisposable
{
    // Pictures received since the start, and browsers running: for the tests.
    public static long Frames;
    public static int Running;

    private const int MinFrameMs = 33;
    private readonly CancellationTokenSource _stop = new();
    private Process? _browser;
    private string? _profile;
    private IntPtr _job;
    private int _closed;

    // A quarter more pixels than the page's: the picture stays sharp, and each frame costs far less than at 1.5 (43 → 30 % of a core).
    private const double Sharp = 1.25;

    public OverlayPreview(string url, int width, int height, Action<byte[]> frame) => _ = Task.Run(() => Run(url, width, height, frame));

    public static string? FindBrowser()
    {
        IEnumerable<string> places;
        if (OperatingSystem.IsWindows())
        {
            var roots = new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData }.Select(Environment.GetFolderPath);
            places = new[] { @"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe", @"BraveSoftware\Brave-Browser\Application\brave.exe" }
                .SelectMany(browser => roots.Select(root => Path.Combine(root, browser)));
        }
        else if (OperatingSystem.IsMacOS())
            places = new[] { "Google Chrome", "Microsoft Edge", "Chromium", "Brave Browser" }.Select(n => $"/Applications/{n}.app/Contents/MacOS/{n}");
        else
            places = new[] { "google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge", "brave-browser" }
                .SelectMany(n => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Select(d => Path.Combine(d, n)));
        return places.FirstOrDefault(File.Exists);
    }

    private async Task Run(string url, int width, int height, Action<byte[]> frame)
    {
        try
        {
            if (FindBrowser() is not { } exe) return;
            var ct = _stop.Token;
            // (an app that crashed or was killed left its browser's profile behind)
            foreach (var old in Directory.GetDirectories(Path.GetTempPath(), "ump-preview-*"))
                if (Directory.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-1))
                    try { Directory.Delete(old, true); } catch { }
            _profile = Path.Combine(Path.GetTempPath(), "ump-preview-" + Guid.NewGuid().ToString("N")[..10]);
            Directory.CreateDirectory(_profile);
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[]
                     {
                         "--headless=new", $"--window-size={width},{height}", $"--force-device-scale-factor={Sharp}", "--remote-debugging-port=0", "--user-data-dir=" + _profile, "--no-first-run",
                         "--no-default-browser-check", "--disable-extensions", "--disable-sync", "--disable-background-networking", "--disable-component-update",
                         "--mute-audio", "--hide-scrollbars", url,
                     })
                start.ArgumentList.Add(arg);
            _browser = Process.Start(start);
            if (_browser == null) return;
            _browser.OutputDataReceived += (_, _) => { };
            _browser.ErrorDataReceived += (_, _) => { };
            _browser.BeginOutputReadLine();
            _browser.BeginErrorReadLine();
            EndWithThisApp(_browser);
            Interlocked.Increment(ref Running);
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
            await Send("Page.startScreencast", $"{{\"format\":\"jpeg\",\"quality\":82,\"maxWidth\":{(int)(width * Sharp)},\"maxHeight\":{(int)(height * Sharp)},\"everyNthFrame\":1}}");
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
                using var said = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
                if (!said.RootElement.TryGetProperty("method", out var method) || method.GetString() != "Page.screencastFrame") continue;
                var details = said.RootElement.GetProperty("params");
                frame(Convert.FromBase64String(details.GetProperty("data").GetString()!));
                Interlocked.Increment(ref Frames);
                // About 30 a second are plenty for a preview; the browser waits for the answer before making the next one.
                int wait = MinFrameMs - (int)clock.ElapsedMilliseconds;
                if (wait > 0) await Task.Delay(wait, ct);
                clock.Restart();
                await Send("Page.screencastFrameAck", $"{{\"sessionId\":{details.GetProperty("sessionId").GetInt32()}}}");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is HttpRequestException or WebSocketException or JsonException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception or FormatException or NotSupportedException)
        {
            // (no preview: the editor goes on, with the picture of the look)
            AppPaths.AppendLog($"[{DateTime.Now:s}] Overlay preview: {e.Message}\n\n");
        }
        finally
        {
            Dispose();
        }
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
