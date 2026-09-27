using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace UltimateMP3Player.Core;

// External programs, kept in "engines" next to the exe.
public static class Engines
{
    public static string Dir { get; private set; } = Locate();

    public static string YtDlp => Path.Combine(Dir, "yt-dlp.exe");
    public static string GalleryDl => Path.Combine(Dir, "gallery-dl.exe");
    public static string Ffmpeg => Path.Combine(Dir, "ffmpeg.exe");
    public static string Ffprobe => Path.Combine(Dir, "ffprobe.exe");
    public static string Deno => Path.Combine(Dir, "deno.exe");

    private static string Locate()
    {
        var env = Environment.GetEnvironmentVariable("UMP_ENGINES");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "yt-dlp.exe"))) return env;
        // Portable: next to the exe; dev: first "engines" up the tree.
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "engines");
            if (File.Exists(Path.Combine(candidate, "yt-dlp.exe"))) return candidate;
        }
        return Path.Combine(AppContext.BaseDirectory, "engines");
    }

    public static IReadOnlyList<string> Missing()
        => new[] { YtDlp, GalleryDl, Ffmpeg, Ffprobe, Deno }.Where(p => !File.Exists(p)).Select(Path.GetFileName).ToList()!;

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
    private const string DenoZip = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";
    private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string GalleryDlUrl = "https://codeberg.org/mikf/gallery-dl/releases/download/latest/gallery-dl.exe";
    private const string GalleryDlApi = "https://codeberg.org/api/v1/repos/mikf/gallery-dl/releases/latest";

    // Downloads missing engines (e.g. only the exe was copied).
    public static async Task InstallMissingAsync(Action<string, double?> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        var tmp = Path.Combine(Path.GetTempPath(), "UltimateMP3Player", "engines-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            if (!File.Exists(YtDlp))
                await Http.DownloadFileAsync(YtDlpUrl, YtDlp, null, (d, t) => progress(L.T("Download di yt-dlp"), Pct(d, t)), ct);
            if (!File.Exists(GalleryDl))
            {
                var url = GalleryDlUrl;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(await Http.GetStringAsync(GalleryDlApi, ct: ct));
                    var asset = doc.RootElement.Arr("assets").FirstOrDefault(a => a.Str("name") == "gallery-dl.exe");
                    url = asset.ValueKind == System.Text.Json.JsonValueKind.Object ? asset.Str("browser_download_url") ?? url : url;
                }
                catch (OperationCanceledException) { throw; }
                catch { }
                await Http.DownloadFileAsync(url, GalleryDl, null, (d, t) => progress(L.T("Download di gallery-dl"), Pct(d, t)), ct);
            }
            if (!File.Exists(Ffmpeg) || !File.Exists(Ffprobe))
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
            if (!File.Exists(Deno))
            {
                var zip = Path.Combine(tmp, "deno.zip");
                await Http.DownloadFileAsync(DenoZip, zip, null, (d, t) => progress(L.T("Download di deno"), Pct(d, t)), ct);
                progress(L.T("Estrazione di deno"), null);
                using var za = ZipFile.OpenRead(zip);
                za.Entries.First(e => e.Name.Equals("deno.exe", StringComparison.OrdinalIgnoreCase)).ExtractToFile(Deno, true);
            }
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }

        static double? Pct(long done, long? total) => total > 0 ? done * 100.0 / total.Value : null;
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
}
