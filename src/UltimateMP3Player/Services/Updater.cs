using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Services;

// IsSetup: only the installer was attached to the release.
public sealed record ReleaseInfo(Version Version, string Tag, string Url, string FileName, long Size, string? Sha256, bool IsSetup);

// Self-update from the latest GitHub release: swaps the exe, or runs the setup silently.
public sealed class Updater : Observable
{
    public const string AssetName = "UltimateMP3Player.exe";
    private const string SetupPrefix = "UltimateMP3Player-Setup";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{3F6C2A71-9D4E-4B8A-A1C5-7E2D90B4F618}_is1";

    private Task<bool>? _running;
    private string? _downloaded;

    private static string Exe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, AssetName);
    private static string NewExe => Exe + ".new";

    // Only the published single-file exe updates itself.
    public static bool CanUpdate => AppInfo.GitHubRepo.Length > 0 &&
                                    !Exe.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) &&
                                    Path.GetFileName(Exe).Equals(AssetName, StringComparison.OrdinalIgnoreCase);

    private string? _status;
    public string? Status { get => _status; private set => Set(ref _status, value); }

    private ReleaseInfo? _ready;
    public ReleaseInfo? Ready { get => _ready; private set { if (Set(ref _ready, value)) OnChanged(nameof(IsReady), nameof(ReadyText)); } }
    public bool IsReady => Ready != null;
    public string ReadyText => Ready == null ? "" : L.F("Versione {0} pronta: si installa alla chiusura, oppure riavvia ora.", Ready.Version.ToString(3));

    public bool IsBusy => _running is { IsCompleted: false };

    // Leftovers of the previous update.
    public static void CleanUp()
    {
        try { File.Delete(Exe + ".old"); } catch { }
        try { File.Delete(NewExe); } catch { }
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.TempDir, SetupPrefix + "*.exe")) File.Delete(f);
        }
        catch { }
    }

    // Checks and downloads; true when a new version is ready.
    public Task<bool> CheckAsync(bool manual) => IsBusy ? _running! : _running = Run(manual);

    private async Task<bool> Run(bool manual)
    {
        if (Ready != null) return true;
        if (!CanUpdate)
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
            var target = release.IsSetup ? Path.Combine(AppPaths.TempDir, release.FileName) : NewExe;
            Status = L.F("Download della versione {0}…", release.Version.ToString(3));
            await Http.DownloadFileAsync(release.Url, target, null, (done, total) =>
            {
                if (total > 0) Status = L.F("Download della versione {0}… {1:0}%", release.Version.ToString(3), done * 100.0 / total.Value);
            }, CancellationToken.None);
            if (!Verify(target, release))
            {
                try { File.Delete(target); } catch { }
                Status = L.T("Aggiornamento scaricato male: riprova più tardi.");
                return false;
            }
            _downloaded = target;
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

    // The bare exe if attached (smaller download), otherwise the setup.
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
        ReleaseInfo? setup = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            bool exe = name.Equals(AssetName, StringComparison.OrdinalIgnoreCase);
            bool isSetup = name.StartsWith(SetupPrefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            if (!exe && !isSetup) continue;
            string? sha = a.TryGetProperty("digest", out var d) && d.GetString() is { } dg && dg.StartsWith("sha256:") ? dg[7..] : null;
            var info = new ReleaseInfo(version, tag, a.GetProperty("browser_download_url").GetString()!, name, a.GetProperty("size").GetInt64(), sha, isSetup);
            if (exe) return info;
            setup = info;
        }
        return setup;
    }

    private static bool Verify(string file, ReleaseInfo r)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length != r.Size) return false;
        if (r.Sha256 == null) return true;
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).Equals(r.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    // Installs the downloaded version; with relaunch the app starts again afterwards.
    public bool Apply(bool relaunch, string? profileId, bool background)
    {
        if (_downloaded == null || Ready == null || !File.Exists(_downloaded)) return false;
        bool ok = Ready.IsSetup ? RunSetup(relaunch, profileId, background) : Swap(relaunch, profileId, background);
        if (ok) _downloaded = null;
        return ok;
    }

    // The running exe can be renamed: the new one takes its place.
    private bool Swap(bool relaunch, string? profileId, bool background)
    {
        try
        {
            var old = Exe + ".old";
            try { File.Delete(old); } catch { }
            File.Move(Exe, old);
            File.Move(_downloaded!, Exe);
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(UninstallKey, true);
                key?.SetValue("DisplayVersion", Ready!.Version.ToString(3));
            }
            catch { }
        }
        catch { return false; }
        if (relaunch)
        {
            var args = $"--wait {Environment.ProcessId}";
            if (profileId != null) args += $" --profile {profileId}";
            if (background) args += " --background";
            try { Process.Start(new ProcessStartInfo(Exe, args) { UseShellExecute = false }); } catch { }
        }
        return true;
    }

    // Setup runs silently into the same folder once this process has exited.
    private bool RunSetup(bool relaunch, string? profileId, bool background)
    {
        var args = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR=\"{Path.GetDirectoryName(Exe)}\"";
        if (relaunch) args += " /relaunch=1" + (profileId != null ? $" /profile={profileId}" : "") + (background ? " /bg=1" : "");
        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
        var script = $"Wait-Process -Id {Environment.ProcessId} -Timeout 60 -ErrorAction SilentlyContinue; " +
                     $"Start-Process -FilePath {Quote(_downloaded!)} -ArgumentList {Quote(args)}";
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return true;
        }
        catch { return false; }
    }
}
