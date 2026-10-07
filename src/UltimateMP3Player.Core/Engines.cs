using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace UltimateMP3Player.Core;

// Android: the programs come inside the app (Python with yt-dlp, ffmpeg, QuickJS), prepared by the app at its first start.
// Dir holds ffmpeg and ffprobe (links to them) and yt-dlp (a Python program, run by Python).
public sealed class EngineHost
{
    public required string Dir { get; init; }
    public required string Python { get; init; }
    // YouTube's JavaScript, for yt-dlp ("quickjs:<path>").
    public required string JsRuntime { get; init; }
    public required Func<Action<string, double?>, CancellationToken, Task> Install { get; init; }
    // Why a program could crash here (the app built for another processor), shown with the error; null when nothing's known.
    public Func<string?>? CrashHint { get; init; }
}

// External programs, kept in "engines" next to the exe (on Linux and macOS next to the data: the app's own folder,
// an AppImage or a .app, can't be written to).
public static class Engines
{
    // ".exe" on Windows, nothing elsewhere.
    private static readonly string Ext = OperatingSystem.IsWindows() ? ".exe" : "";

    private static string _dir = Locate();
    public static string Dir => Host?.Dir ?? _dir;

    // Set by the Android app before anything runs.
    public static EngineHost? Host { get; set; }

    public static string YtDlp => Path.Combine(Dir, "yt-dlp" + Ext);
    public static string GalleryDl => Path.Combine(Dir, "gallery-dl" + Ext);
    public static string Ffmpeg => Path.Combine(Dir, "ffmpeg" + Ext);
    public static string Ffprobe => Path.Combine(Dir, "ffprobe" + Ext);
    public static string Deno => Path.Combine(Dir, "deno" + Ext);

    // The JavaScript runtime yt-dlp needs for YouTube.
    public static string JsRuntime => Host?.JsRuntime ?? "deno:" + Deno;

    private static string Locate()
    {
        var env = Environment.GetEnvironmentVariable("UMP_ENGINES");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "yt-dlp" + Ext))) return env;
        // Portable: next to the exe; dev: first "engines" up the tree.
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "engines");
            if (File.Exists(Path.Combine(candidate, "yt-dlp" + Ext))) return candidate;
        }
        return OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, "engines") : Path.Combine(AppPaths.DataDir, "engines");
    }

    // gallery-dl (picture galleries) has no build for macOS and Android: there it's used only if installed by hand.
    private static bool GalleryDlNeeded => !OperatingSystem.IsMacOS() && Host == null;

    public static IReadOnlyList<string> Missing()
        => new[] { YtDlp, GalleryDlNeeded ? GalleryDl : null, Ffmpeg, Ffprobe, Host == null ? Deno : null }.OfType<string>()
            .Where(p => !File.Exists(p)).Select(Path.GetFileName).ToList()!;

    public static async Task<string> VersionAsync(string exe, CancellationToken ct = default)
    {
        if (!File.Exists(exe)) return L.T("mancante");
        try
        {
            var r = await ProcRunner.RunAsync(exe, new[] { "--version" }, ct: ct, timeout: TimeSpan.FromSeconds(20));
            return r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "?";
        }
        catch (OperationCanceledException) { throw; }
        catch { return "?"; }
    }

    // Updates yt-dlp and gallery-dl, which break when sites change.
    public static async Task<string> UpdateAsync(Action<string>? log, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        foreach (var (name, exe) in new[] { ("yt-dlp", YtDlp), ("gallery-dl", GalleryDl) })
        {
            if (!File.Exists(exe) && !GalleryDlNeeded) continue;
            log?.Invoke(L.F("Aggiornamento di {0}…", name));
            try
            {
                var r = await ProcRunner.RunAsync(exe, new[] { "-U" }, ct: ct, timeout: TimeSpan.FromMinutes(5));
                var text = (r.StdOut + "\n" + r.StdErr).Trim();
                var last = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
                sb.AppendLine($"{name}: {(r.ExitCode == 0 ? "ok" : L.T("errore"))} – {last}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { sb.AppendLine(L.F("{0}: errore – {1}", name, ex.Message)); }
        }
        return sb.ToString().Trim();
    }

    private const string FfmpegZip = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-shared.zip";
    private const string GalleryDlApi = "https://codeberg.org/api/v1/repos/mikf/gallery-dl/releases/latest";

    private static bool Arm => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;

    // Where each engine comes from on this system.
    private static string YtDlpUrl => "https://github.com/yt-dlp/yt-dlp/releases/latest/download/" +
        (OperatingSystem.IsWindows() ? "yt-dlp.exe" : OperatingSystem.IsMacOS() ? "yt-dlp_macos" : Arm ? "yt-dlp_linux_aarch64" : "yt-dlp_linux");
    private static string GalleryDlAsset => OperatingSystem.IsWindows() ? "gallery-dl.exe" : "gallery-dl.bin";
    private static string GalleryDlUrl => "https://codeberg.org/mikf/gallery-dl/releases/download/latest/" + GalleryDlAsset;
    private static string DenoZip => "https://github.com/denoland/deno/releases/latest/download/deno-" +
        (OperatingSystem.IsWindows() ? "x86_64-pc-windows-msvc" : (Arm ? "aarch64" : "x86_64") + (OperatingSystem.IsMacOS() ? "-apple-darwin" : "-unknown-linux-gnu")) + ".zip";
    // Linux and macOS: static builds, ffmpeg and ffprobe in a zip each (macOS has only the nightly ones there).
    private static string MartinRiedl(string tool) => "https://ffmpeg.martin-riedl.de/redirect/latest/" +
        (OperatingSystem.IsMacOS() ? "macos" : "linux") + "/" + (Arm ? "arm64" : "amd64") + "/" + (OperatingSystem.IsMacOS() ? "snapshot" : "release") + "/" + tool + ".zip";

    // Downloads missing engines (e.g. only the exe was copied).
    public static async Task InstallMissingAsync(Action<string, double?> progress, CancellationToken ct)
    {
        if (Host != null)
        {
            await Host.Install(progress, ct);
            return;
        }
        Directory.CreateDirectory(Dir);
        var tmp = Path.Combine(Path.GetTempPath(), "UltimateMP3Player", "engines-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            if (!File.Exists(YtDlp))
            {
                await Http.DownloadFileAsync(YtDlpUrl, YtDlp, null, (d, t) => progress(L.T("Download di yt-dlp"), Pct(d, t)), ct);
                MakeRunnable(YtDlp);
            }
            if (!File.Exists(GalleryDl) && GalleryDlNeeded)
            {
                var url = GalleryDlUrl;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(await Http.GetStringAsync(GalleryDlApi, ct: ct));
                    var asset = doc.RootElement.Arr("assets").FirstOrDefault(a => a.Str("name") == GalleryDlAsset);
                    url = asset.ValueKind == System.Text.Json.JsonValueKind.Object ? asset.Str("browser_download_url") ?? url : url;
                }
                catch (OperationCanceledException) { throw; }
                catch { }
                await Http.DownloadFileAsync(url, GalleryDl, null, (d, t) => progress(L.T("Download di gallery-dl"), Pct(d, t)), ct);
                MakeRunnable(GalleryDl);
            }
            if (!File.Exists(Ffmpeg) || !File.Exists(Ffprobe))
            {
                if (OperatingSystem.IsWindows())
                {
                    var zip = Path.Combine(tmp, "ffmpeg.zip");
                    await Http.DownloadFileAsync(FfmpegZip, zip, null, (d, t) => progress(L.T("Download di ffmpeg"), Pct(d, t)), ct);
                    progress(L.T("Estrazione di ffmpeg"), null);
                    using var za = ZipFile.OpenRead(zip);
                    foreach (var entry in za.Entries)
                    {
                        var parent = Path.GetFileName(Path.GetDirectoryName(entry.FullName.Replace('/', '\\')) ?? "");
                        if (!string.Equals(parent, "bin", StringComparison.OrdinalIgnoreCase) || entry.Name.Length == 0) continue;
                        if (entry.Name.Equals("ffplay.exe", StringComparison.OrdinalIgnoreCase)) continue;
                        entry.ExtractToFile(Path.Combine(Dir, entry.Name), true);
                    }
                }
                else
                    foreach (var (tool, target) in new[] { ("ffmpeg", Ffmpeg), ("ffprobe", Ffprobe) })
                    {
                        if (File.Exists(target)) continue;
                        var zip = Path.Combine(tmp, tool + ".zip");
                        await Http.DownloadFileAsync(MartinRiedl(tool), zip, null, (d, t) => progress(L.F("Download di {0}", tool), Pct(d, t)), ct);
                        progress(L.F("Estrazione di {0}", tool), null);
                        using var za = ZipFile.OpenRead(zip);
                        za.Entries.First(e => e.Name == tool).ExtractToFile(target, true);
                        MakeRunnable(target);
                    }
            }
            if (!File.Exists(Deno))
            {
                var zip = Path.Combine(tmp, "deno.zip");
                await Http.DownloadFileAsync(DenoZip, zip, null, (d, t) => progress(L.T("Download di deno"), Pct(d, t)), ct);
                progress(L.T("Estrazione di deno"), null);
                using var za = ZipFile.OpenRead(zip);
                za.Entries.First(e => e.Name.Equals(Path.GetFileName(Deno), StringComparison.OrdinalIgnoreCase)).ExtractToFile(Deno, true);
                MakeRunnable(Deno);
            }
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }

        static double? Pct(long done, long? total) => total > 0 ? done * 100.0 / total.Value : null;
    }

    // Linux and macOS: a downloaded program needs the "executable" permission.
    private static void MakeRunnable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                       UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch { }
    }
}

public sealed record ProcResult(int ExitCode, string StdOut, string StdErr);

public static class ProcRunner
{
    // Runs a hidden console program; kills its tree on cancel.
    public static async Task<ProcResult> RunAsync(string exe, IEnumerable<string> args,
        Action<string>? onOut = null, Action<string>? onErr = null, CancellationToken ct = default,
        bool captureOut = true, TimeSpan? timeout = null, string? workDir = null)
    {
        // Android: yt-dlp is a Python program, Python runs it.
        string name = exe;
        if (Engines.Host is { } host && exe == Engines.YtDlp)
        {
            args = args.Prepend(exe).ToList();
            exe = host.Python;
        }
        // gallery-dl isn't there: picture galleries are left out, the link goes on to the next way of reading it.
        if (Engines.Host != null && !File.Exists(exe))
            throw name == Engines.GalleryDl ? new EngineException(L.T("Questo link non è supportato.")) { Unsupported = true }
                : new EngineException(L.F("Impossibile avviare {0}.", Path.GetFileName(name)));
        if (ChildProcess.Starter != null) return await RunChildAsync(exe, args, onOut, onErr, ct, captureOut, timeout, workDir).ConfigureAwait(false);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workDir ?? Path.GetTempPath(),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["NO_COLOR"] = "1";

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var so = new StringBuilder();
        var se = new StringBuilder();
        var outDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) { outDone.TrySetResult(); return; }
            if (captureOut) lock (so) so.AppendLine(e.Data);
            try { onOut?.Invoke(e.Data); } catch { }
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) { errDone.TrySetResult(); return; }
            lock (se)
            {
                se.AppendLine(e.Data);
                if (se.Length > 200_000) se.Remove(0, se.Length - 100_000);
            }
            try { onErr?.Invoke(e.Data); } catch { }
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) cts.CancelAfter(t);

        if (!p.Start()) throw new EngineException(L.F("Impossibile avviare {0}.", Path.GetFileName(exe)));
        try { p.StandardInput.Close(); } catch { }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using (cts.Token.Register(() => { try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { } }))
        {
            await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAny(Task.WhenAll(outDone.Task, errDone.Task), Task.Delay(5000)).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        if (cts.IsCancellationRequested)
            throw new EngineException(L.F("{0} non ha risposto in tempo.", Path.GetFileNameWithoutExtension(exe)));
        return new ProcResult(p.ExitCode, so.ToString(), se.ToString());
    }

    // The same through ChildProcess (the Android app's way of starting programs), in the background of the phone.
    private static async Task<ProcResult> RunChildAsync(string exe, IEnumerable<string> args, Action<string>? onOut, Action<string>? onErr,
        CancellationToken ct, bool captureOut, TimeSpan? timeout, string? workDir)
    {
        var env = new Dictionary<string, string> { ["PYTHONIOENCODING"] = "utf-8", ["PYTHONUTF8"] = "1", ["NO_COLOR"] = "1" };
        ChildProcess p;
        try { p = ChildProcess.Start(exe, args, workDir ?? Path.GetTempPath(), env); }
        catch (EngineException) { throw; }
        catch (Exception ex) { throw new EngineException(L.F("Impossibile avviare {0}.", Path.GetFileName(exe)), ex.Message); }
        using (p)
        {
            p.LowerPriority();
            var so = new StringBuilder();
            var se = new StringBuilder();
            var outDone = Lines(p.StandardOutput, line =>
            {
                if (captureOut) lock (so) so.AppendLine(line);
                try { onOut?.Invoke(line); } catch { }
            });
            var errDone = Lines(p.StandardError, line =>
            {
                lock (se)
                {
                    se.AppendLine(line);
                    if (se.Length > 200_000) se.Remove(0, se.Length - 100_000);
                }
                try { onErr?.Invoke(line); } catch { }
            });
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeout is { } t) cts.CancelAfter(t);
            using (cts.Token.Register(p.Kill))
            {
                await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAny(Task.WhenAll(outDone, errDone), Task.Delay(5000)).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            if (cts.IsCancellationRequested)
                throw new EngineException(L.F("{0} non ha risposto in tempo.", Path.GetFileNameWithoutExtension(exe)));
            lock (so) lock (se) return new ProcResult(p.ExitCode, so.ToString(), se.ToString());
        }
    }

    // The lines a program writes, as it writes them (a "\r" ends one too: yt-dlp's progress), on a thread of their own (the
    // read waits for the program).
    private static Task Lines(Stream stream, Action<string> line) => Task.Factory.StartNew(() =>
    {
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192);
            var buf = new char[4096];
            var sb = new StringBuilder();
            bool afterCr = false;
            int n;
            while ((n = reader.Read(buf, 0, buf.Length)) > 0)
                for (int i = 0; i < n; i++)
                {
                    char c = buf[i];
                    if (c == '\n' && afterCr)
                    {
                        afterCr = false;
                        continue;
                    }
                    afterCr = c == '\r';
                    if (c is '\r' or '\n')
                    {
                        line(sb.ToString());
                        sb.Clear();
                    }
                    else sb.Append(c);
                }
            if (sb.Length > 0) line(sb.ToString());
        }
        catch { }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
