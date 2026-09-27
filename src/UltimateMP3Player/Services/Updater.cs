using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Services;

public sealed record ReleaseInfo(Version Version, string Tag, string ExeUrl, long Size, string? Sha256, string PageUrl);

// Self-update from the latest GitHub release: the exe is swapped in place.
public sealed class Updater : Observable
{
    public const string AssetName = "UltimateMP3Player.exe";
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

    // Leftovers of the previous swap.
    public static void CleanUp()
    {
        try { File.Delete(Exe + ".old"); } catch { }
        try { File.Delete(NewExe); } catch { }
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
            Status = L.F("Download della versione {0}…", release.Version.ToString(3));
            await Http.DownloadFileAsync(release.ExeUrl, NewExe, null, (done, total) =>
            {
                if (total > 0) Status = L.F("Download della versione {0}… {1:0}%", release.Version.ToString(3), done * 100.0 / total.Value);
            }, CancellationToken.None);
            if (!Verify(NewExe, release))
            {
                try { File.Delete(NewExe); } catch { }
                Status = L.T("Aggiornamento scaricato male: riprova più tardi.");
                return false;
            }
            _downloaded = NewExe;
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
            if (!string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            string? sha = a.TryGetProperty("digest", out var d) && d.GetString() is { } dg && dg.StartsWith("sha256:") ? dg[7..] : null;
            return new ReleaseInfo(version, tag, a.GetProperty("browser_download_url").GetString()!, a.GetProperty("size").GetInt64(), sha,
                root.GetProperty("html_url").GetString() ?? "");
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

    // Puts the new exe in place; the running one becomes .old.
    public bool Swap()
    {
        if (_downloaded == null || Ready == null || !File.Exists(_downloaded)) return false;
        try
        {
            var old = Exe + ".old";
            try { File.Delete(old); } catch { }
            File.Move(Exe, old);
            File.Move(_downloaded, Exe);
            _downloaded = null;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(UninstallKey, true);
                key?.SetValue("DisplayVersion", Ready.Version.ToString(3));
            }
            catch { }
            return true;
        }
        catch { return false; }
    }

    // Starts the updated exe once this process has exited.
    public static void Relaunch(string? profileId, bool background)
    {
        var args = $"--wait {Environment.ProcessId}";
        if (profileId != null) args += $" --profile {profileId}";
        if (background) args += " --background";
        try { Process.Start(new ProcessStartInfo(Exe, args) { UseShellExecute = false }); } catch { }
    }
}
