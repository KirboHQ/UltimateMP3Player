using System.Security.Cryptography;
using System.Text.Json;
using UIKit;
using UltimateMP3Player.Core;
using UltimateMP3Player.Platform;
using UltimateMP3Player.Views;

namespace UltimateMP3Player.Services;

public sealed record ReleaseInfo(Version Version, string Url, long Size, string Sha256);

// The new versions, from the latest GitHub release. With what the installer left (Renewal) the app signs and installs them over itself;
// without it, they come from the installer on a computer, or from SideStore (it reads the source attached to every release), and the
// check only says whether there's a newer one, so the settings can point there.
public sealed class Updater : Observable
{
    public const string AssetName = "UltimateMP3Player-ios.ipa";

    private Task<bool>? _running;
    private bool _installing;

    public static bool WrongPackage => false;
    // (whether LocalDevVPN is on changes at any moment: looked at when it's time to install)
    public static bool CanUpdate => Renewal.HasKeys;

    private static string UpdateDir => Path.Combine(Path.GetTempPath(), "update");

    // What an update leaves behind, since iOS closes the app in the middle of it: the package, and the library's copy of what's inside.
    public static void CleanUp() => Task.Run(() =>
    {
        foreach (var dir in new[] { UpdateDir, Path.Combine(Path.GetTempPath(), AssetName + "_extracted") })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    });

    private string? _status;
    public string? Status { get => _status; private set => Set(ref _status, value); }

    private ReleaseInfo? _ready;
    public ReleaseInfo? Ready { get => _ready; private set { if (Set(ref _ready, value)) OnChanged(nameof(IsReady), nameof(ReadyText)); } }
    public bool IsReady => Ready != null;
    public string ReadyText => Ready == null ? "" : L.F("Version {0} ready to install.", Ready.Version.ToString(3));
    public bool IsBusy => _running is { IsCompleted: false };

    public Task<bool> CheckAsync(bool manual) => IsBusy ? _running! : _running = Run(manual);

    private async Task<bool> Run(bool manual)
    {
        if (AppInfo.GitHubRepo.Length == 0) return false;
        try
        {
            if (manual) Status = L.T("Checking for updates…");
            var latest = await ReleaseAsync("latest");
            bool newer = latest != null && Norm(latest.Version) > Norm(AppInfo.Version);
            Ready = newer && CanUpdate ? latest : null;
            if (newer && !CanUpdate)
                Status = L.F("Version {0} is out: update it with the installer on your computer, or from SideStore (My Apps › Update).", latest!.Version.ToString(3));
            else Status = !newer && manual ? L.F("You already have the latest version ({0}).", AppInfo.VersionText) : null;
        }
        catch (Exception ex)
        {
            Status = manual ? L.T("Update failed:") + " " + ex.Message : null;
        }
        return IsReady;
    }

    // 3.5 = 3.5.0 = 3.5.0.0 (a tag against the assembly's version).
    private static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    // A release ("latest", "tags/v5.6.0"), when it has the iPhone's package.
    private static async Task<ReleaseInfo?> ReleaseAsync(string which)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{AppInfo.GitHubRepo}/releases/{which}");
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
            if (!(a.GetProperty("name").GetString() ?? "").Equals(AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            var sha = a.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:") ? digest[7..] : "";
            return new ReleaseInfo(version, a.GetProperty("browser_download_url").GetString()!, a.GetProperty("size").GetInt64(), sha);
        }
        return null;
    }

    // (signed with the user's own key and put in place of the app: never without the hash GitHub gives)
    private static bool Verify(string file, ReleaseInfo r)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length != r.Size) return false;
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).Equals(r.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    // "Install": asked first, since iOS closes the app while it's replaced.
    public bool Apply(bool relaunch, string? profileId, bool background)
    {
        if (Ready is { } release) _ = AskAndInstallAsync(release);
        return false;
    }

    private async Task AskAndInstallAsync(ReleaseInfo release)
    {
        try
        {
            if (!_installing && await RenewalNotice.AskUpdateAsync(release.Version.ToString(3))) await InstallShownAsync(L.F("Updating to {0}", release.Version.ToString(3)), release);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    // The same version again, when the profile alone can't be renewed.
    public async Task ReinstallAsync()
    {
        if (_installing) return;
        try
        {
            if (await ReleaseAsync("tags/v" + AppInfo.VersionText) is { } same) await InstallShownAsync(L.T("Installing it again"), same);
            else Dialogs.Alert(L.T("Update not installed."), L.F("Version {0} can't be downloaded from GitHub right now: try again later, or renew the app with the installer.", AppInfo.VersionText));
        }
        catch (Exception ex) { Dialogs.Alert(L.T("Update not installed."), ex.Message); }
    }

    private async Task InstallShownAsync(string title, ReleaseInfo release)
    {
        var close = RenewalNotice.ShowProgress(title);
        bool done = false;
        string? error = null;
        try { done = await InstallAsync(release, CancellationToken.None); }
        catch (Exception ex) { error = ex.Message; }
        close();
        if (error != null) Dialogs.Alert(L.T("Update not installed."), error);
        else if (!done && await RenewalNotice.ShowProblemAsync()) await InstallShownAsync(title, release);
    }

    // Settings' switch: in a background task, with nothing playing or downloading, on Wi-Fi.
    public async Task InstallAloneAsync(CancellationToken ct)
    {
        var host = App.Host;
        bool idle() => host.Session?.Player.IsPlaying != true && !host.Downloads.HasActive && UIApplication.SharedApplication.ApplicationState == UIApplicationState.Background;
        if (!host.Settings.InstallUpdatesAlone || _installing || NetworkCost.IsMetered || !idle()) return;
        await CheckAsync(false);
        if (Ready is { } release && idle()) await InstallAsync(release, ct);
    }

    private async Task<bool> InstallAsync(ReleaseInfo release, CancellationToken ct)
    {
        if (_installing) return false;
        _installing = true;
        var app = UIApplication.SharedApplication;
        bool slept = app.IdleTimerDisabled;
        try
        {
            app.IdleTimerDisabled = true;
            // (before downloading for nothing)
            if (!await Renewal.ReachAsync()) return false;
            Directory.CreateDirectory(UpdateDir);
            var ipa = Path.Combine(UpdateDir, AssetName);
            Renewal.Report("Downloading", 0);
            double shown = 0;
            await Http.DownloadFileAsync(release.Url, ipa, null, (done, total) =>
            {
                double all = RenewalRules.Overall("Downloading", total > 0 ? (double)done / total.Value : 0);
                if (all - shown < 0.003) return;
                shown = all;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Renewal.Report("Downloading", all));
            }, ct);
            if (!await Task.Run(() => Verify(ipa, release), ct)) throw new InvalidOperationException(L.T("The update didn't download correctly: try again later."));
            ct.ThrowIfCancellationRequested();
            // (a renewal that began meanwhile: seconds)
            while (Renewal.Busy) await Task.Delay(300, ct);
            App.Host.SaveAll();
            if (!await Renewal.InstallAsync(ipa)) return false;
            // Replaced and still running: what's on the screen is the version of before, so it goes, as iOS would have made it.
            App.Host.SaveAll();
            Environment.Exit(0);
            return true;
        }
        finally
        {
            _installing = false;
            app.IdleTimerDisabled = slept;
            CleanUp();
            Renewal.Report("", 0);
        }
    }
}
