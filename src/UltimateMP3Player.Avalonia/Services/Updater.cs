using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Services;

public sealed record ReleaseInfo(Version Version, string Tag, string Url, string FileName, long Size, string? Sha256, bool IsSetup);

// Self-update from the latest GitHub release, like the Windows app: Linux gets UltimateMP3Player-linux-<arch>.tar.gz
// (the app's folder), macOS UltimateMP3Player-macos-<arch>.zip (the .app). The new files take the place of the running
// ones, which the system lets go on until the app quits.
public sealed class Updater : Observable
{
    public const string BundleName = "Ultimate MP3 Player.app";

    private Task<bool>? _running;
    private string? _extracted;

    private static string Exe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "UltimateMP3Player");
    private static string Arch => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
    public static string AssetName => OperatingSystem.IsMacOS() ? $"UltimateMP3Player-macos-{Arch}.zip" : $"UltimateMP3Player-linux-{Arch}.tar.gz";

    // macOS: the .app around the running program.
    private static string? Bundle
    {
        get
        {
            var dir = Path.GetDirectoryName(Exe); // …/X.app/Contents/MacOS
            var contents = dir == null ? null : Path.GetDirectoryName(dir);
            var bundle = contents == null ? null : Path.GetDirectoryName(contents);
            return bundle != null && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? bundle : null;
        }
    }

    // Where the update goes: the .app (macOS) or the app's folder (Linux), which must be writable.
    private static string? Target
    {
        get
        {
            var target = OperatingSystem.IsMacOS() ? Bundle : Path.GetDirectoryName(Exe);
            if (target == null) return null;
            var parent = OperatingSystem.IsMacOS() ? Path.GetDirectoryName(target) : target;
            return parent != null && Writable(parent) ? target : null;
        }
    }

    private static bool Writable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".ump-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    // Only the published app updates itself (not a build from the sources).
    public static bool CanUpdate => AppInfo.GitHubRepo.Length > 0 &&
                                    !Exe.Contains("/bin/", StringComparison.Ordinal) &&
                                    Path.GetFileName(Exe) == "UltimateMP3Player" &&
                                    (!OperatingSystem.IsMacOS() || Bundle != null);

    private string? _status;
    public string? Status { get => _status; private set => Set(ref _status, value); }

    private ReleaseInfo? _ready;
    public ReleaseInfo? Ready { get => _ready; private set { if (Set(ref _ready, value)) OnChanged(nameof(IsReady), nameof(ReadyText)); } }
    public bool IsReady => Ready != null;
    public string ReadyText => Ready == null ? "" : L.F("Versione {0} pronta: si installa alla chiusura, oppure riavvia ora.", Ready.Version.ToString(3));

    public bool IsBusy => _running is { IsCompleted: false };

    private static string UpdateDir => Path.Combine(AppPaths.TempDir, "update");

    // Leftovers of the previous update.
    public static void CleanUp()
    {
        try { File.Delete(Exe + ".old"); } catch { }
        try
        {
            if (Path.GetDirectoryName(Exe) is { } dir)
                foreach (var f in Directory.EnumerateFiles(dir, "*.old")) File.Delete(f);
        }
        catch { }
        try
        {
            if (Bundle is { } b && Path.GetDirectoryName(b) is { } parent)
                foreach (var d in Directory.EnumerateDirectories(parent, ".ump-old-*")) Directory.Delete(d, true);
        }
        catch { }
        try { if (Directory.Exists(UpdateDir)) Directory.Delete(UpdateDir, true); } catch { }
    }

    public Task<bool> CheckAsync(bool manual) => IsBusy ? _running! : _running = Run(manual);

    private async Task<bool> Run(bool manual)
    {
        if (Ready != null) return true;
        if (!CanUpdate || Target == null)
        {
            if (manual) Status = AppInfo.GitHubRepo.Length == 0 ? L.T("Aggiornamenti non configurati in questa versione.") : L.T("Aggiornamenti disponibili solo nella versione installata.");
            return false;
        }
        try
        {
            if (manual) Status = L.T("Controllo degli aggiornamenti…");
            var release = await LatestAsync();
            if (release == null || release.Version <= AppInfo.Version)
            {
                Status = manual ? L.F("Hai già l'ultima versione ({0}).", AppInfo.VersionText) : null;
                return false;
            }
            var archive = Path.Combine(AppPaths.TempDir, release.FileName);
            Status = L.F("Download della versione {0}…", release.Version.ToString(3));
            await Http.DownloadFileAsync(release.Url, archive, null, (done, total) =>
            {
                if (total > 0) Status = L.F("Download della versione {0}… {1:0}%", release.Version.ToString(3), done * 100.0 / total.Value);
            }, CancellationToken.None);
            if (!Verify(archive, release))
            {
                try { File.Delete(archive); } catch { }
                Status = L.T("Aggiornamento scaricato male: riprova più tardi.");
                return false;
            }
            _extracted = await Task.Run(() => Extract(archive));
            try { File.Delete(archive); } catch { }
            if (_extracted == null)
            {
                Status = L.T("Aggiornamento scaricato male: riprova più tardi.");
                return false;
            }
            Status = null;
            Ready = release;
            return true;
        }
        catch (Exception ex)
        {
            Status = manual ? L.T("Aggiornamento non riuscito:") + " " + ex.Message : null;
            return false;
        }
    }

    private static async Task<ReleaseInfo?> LatestAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{AppInfo.GitHubRepo}/releases/latest");
        req.Headers.TryAddWithoutValidation("User-Agent", "UltimateMP3Player/" + AppInfo.VersionText);
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var resp = await Http.Client.SendAsync(req, cts.Token);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (!name.Equals(AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            string? sha = a.TryGetProperty("digest", out var d) && d.GetString() is { } dg && dg.StartsWith("sha256:") ? dg[7..] : null;
            return new ReleaseInfo(version, tag, a.GetProperty("browser_download_url").GetString()!, name, a.GetProperty("size").GetInt64(), sha, false);
        }
        return null;
    }

    private static bool Verify(string file, ReleaseInfo r)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length != r.Size) return false;
        if (r.Sha256 == null) return true;
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).Equals(r.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    // The new app, ready to be moved: the .app (macOS) or the folder with the program (Linux). macOS: next to the
    // installed .app, so the swap is a rename on the same disk.
    private static string? Extract(string archive)
    {
        var target = Target;
        if (target == null) return null;
        string dir = OperatingSystem.IsMacOS()
            ? Path.Combine(Path.GetDirectoryName(target)!, ".ump-update")
            : UpdateDir;
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            ZipFile.ExtractToDirectory(archive, dir);
        else
        {
            using var file = File.OpenRead(archive);
            using var gz = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, dir, true);
        }
        if (OperatingSystem.IsMacOS())
        {
            var app = Directory.EnumerateDirectories(dir, "*.app").FirstOrDefault();
            var exe = app == null ? null : Path.Combine(app, "Contents", "MacOS", "UltimateMP3Player");
            if (exe == null || !File.Exists(exe)) return null;
            MakeExecutable(exe);
            // Downloaded by the app itself: no quarantine, but clear it anyway.
            Run("xattr", "-dr", "com.apple.quarantine", app!);
            return app;
        }
        var program = Directory.EnumerateFiles(dir, "UltimateMP3Player", SearchOption.AllDirectories).FirstOrDefault();
        if (program == null) return null;
        MakeExecutable(program);
        return Path.GetDirectoryName(program);
    }

    private static void MakeExecutable(string file)
    {
        try
        {
            File.SetUnixFileMode(file, File.GetUnixFileMode(file) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch { }
    }

    private static void Run(string program, params string[] args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(program, args) { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(10000);
        }
        catch { }
    }

    // Installs the downloaded version; with relaunch the app starts again afterwards.
    public bool Apply(bool relaunch, string? profileId, bool background)
    {
        if (_extracted == null || Ready == null || !Directory.Exists(_extracted)) return false;
        bool ok = OperatingSystem.IsMacOS() ? SwapBundle() : SwapFolder();
        if (!ok) return false;
        _extracted = null;
        if (relaunch)
        {
            var args = new List<string> { "--wait", Environment.ProcessId.ToString() };
            if (profileId != null) args.AddRange(new[] { "--profile", profileId });
            if (background) args.Add("--background");
            try
            {
                if (OperatingSystem.IsMacOS())
                    Process.Start(new ProcessStartInfo("open", new[] { "-n", Bundle!, "--args" }.Concat(args)) { UseShellExecute = false });
                else
                    Process.Start(new ProcessStartInfo(Exe, args) { UseShellExecute = false });
            }
            catch { }
        }
        return true;
    }

    // Linux: every file of the new folder replaces the old one (the running program is renamed, not overwritten).
    private bool SwapFolder()
    {
        var dir = Path.GetDirectoryName(Exe);
        if (dir == null) return false;
        try
        {
            foreach (var src in Directory.EnumerateFiles(_extracted!, "*", SearchOption.AllDirectories))
            {
                var dst = Path.Combine(dir, Path.GetRelativePath(_extracted!, src));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (File.Exists(dst))
                {
                    try { File.Delete(dst + ".old"); } catch { }
                    File.Move(dst, dst + ".old");
                }
                File.Move(src, dst);
            }
            return true;
        }
        catch { return false; }
    }

    // macOS: the new .app takes the old one's place (the running copy goes on from where it was moved).
    private bool SwapBundle()
    {
        var bundle = Bundle;
        if (bundle == null) return false;
        var parent = Path.GetDirectoryName(bundle)!;
        var old = Path.Combine(parent, ".ump-old-" + Environment.ProcessId);
        try
        {
            Directory.Move(bundle, old);
            try { Directory.Move(_extracted!, bundle); }
            catch
            {
                Directory.Move(old, bundle);
                return false;
            }
            try { Directory.Delete(Path.GetDirectoryName(_extracted!)!, true); } catch { }
            // Launch Services learns the new version (icon, file types).
            Run("/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister", "-f", bundle);
            return true;
        }
        catch { return false; }
    }
}
