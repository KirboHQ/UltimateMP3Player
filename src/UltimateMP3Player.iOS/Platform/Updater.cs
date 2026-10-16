using System.Text.Json;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Services;

// An iPhone app can't install itself: SideStore installs the new versions (it reads the source attached to every GitHub
// release). Here the check only says whether there's a newer one, so the settings can point to SideStore.
public sealed class Updater : Observable
{
    public const string AssetName = "UltimateMP3Player-ios.ipa";

    private Task<bool>? _running;

    public static bool WrongPackage => false;
    public static bool CanUpdate => false;
    public static void CleanUp() { }

    private string? _status;
    public string? Status { get => _status; private set => Set(ref _status, value); }

    public bool IsReady => false;
    public string ReadyText => "";
    public bool IsBusy => _running is { IsCompleted: false };

    public Task<bool> CheckAsync(bool manual) => IsBusy ? _running! : _running = Run(manual);

    private async Task<bool> Run(bool manual)
    {
        if (AppInfo.GitHubRepo.Length == 0) return false;
        try
        {
            if (manual) Status = L.T("Checking for updates…");
            var latest = await LatestAsync();
            if (latest != null && Norm(latest) > Norm(AppInfo.Version))
                Status = L.F("Version {0} is out: update it from SideStore (My Apps › Update).", latest.ToString(3));
            else Status = manual ? L.F("You already have the latest version ({0}).", AppInfo.VersionText) : null;
        }
        catch (Exception ex)
        {
            Status = manual ? L.T("Update failed:") + " " + ex.Message : null;
        }
        return false;
    }

    // 3.5 = 3.5.0 = 3.5.0.0 (a tag against the assembly's version).
    private static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    // The latest release's version, when it has the iPhone's package.
    private static async Task<Version?> LatestAsync()
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
            if ((a.GetProperty("name").GetString() ?? "").Equals(AssetName, StringComparison.OrdinalIgnoreCase)) return version;
        return null;
    }

    public bool Apply(bool relaunch, string? profileId, bool background) => false;
}
