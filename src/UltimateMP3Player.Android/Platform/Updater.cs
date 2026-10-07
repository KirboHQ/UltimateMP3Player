using System.Security.Cryptography;
using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using UltimateMP3Player.Core;
using Application = Android.App.Application;

namespace UltimateMP3Player.Services;

public sealed record ReleaseInfo(Version Version, string Tag, string Url, string FileName, long Size, string? Sha256);

// Updates from the latest GitHub release, like the other apps: the package for this phone's processor
// (UltimateMP3Player-android-<abi>.apk) is downloaded in the background (by itself only on Wi-Fi, it's big) and Android
// installs it after asking (the first time it asks to allow installing apps from Ultimate MP3 Player).
public sealed class Updater : Observable
{
    private Task<bool>? _running;
    private string? _apk;

    private static Context Ctx => Application.Context;

    // The processor this phone runs the app on: the package of the same kind.
    public static string Abi
    {
        get
        {
            foreach (var abi in Build.SupportedAbis ?? Array.Empty<string>())
                if (abi is "arm64-v8a" or "armeabi-v7a" or "x86_64") return abi;
            return "arm64-v8a";
        }
    }

    public static string AssetName => $"UltimateMP3Player-android-{Abi}.apk";

    // A build from the sources (debuggable) doesn't update itself.
    public static bool CanUpdate => AppInfo.GitHubRepo.Length > 0 && (Ctx.ApplicationInfo!.Flags & ApplicationInfoFlags.Debuggable) == 0;

    private string? _status;
    public string? Status { get => _status; private set => Set(ref _status, value); }

    private ReleaseInfo? _ready;
    public ReleaseInfo? Ready { get => _ready; private set { if (Set(ref _ready, value)) OnChanged(nameof(IsReady), nameof(ReadyText)); } }
    public bool IsReady => Ready != null;
    public string ReadyText => Ready == null ? "" : L.F("Versione {0} pronta da installare.", Ready.Version.ToString(3));

    public bool IsBusy => _running is { IsCompleted: false };

    private static string UpdateDir => Path.Combine(Ctx.CacheDir!.AbsolutePath, "update");

    public static void CleanUp()
    {
        try { if (Directory.Exists(UpdateDir)) Directory.Delete(UpdateDir, true); } catch { }
    }

    // Phone data (not Wi-Fi): the automatic check doesn't download 60 MB on its own.
    private static bool Metered
    {
        get
        {
            try { return ((ConnectivityManager?)Ctx.GetSystemService(Context.ConnectivityService))?.IsActiveNetworkMetered ?? false; }
            catch { return false; }
        }
    }

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
            if (!manual && Metered)
            {
                Status = L.F("È uscita la versione {0}: si scarica da sola con il Wi-Fi, oppure controlla gli aggiornamenti.", release.Version.ToString(3));
                return false;
            }
            Directory.CreateDirectory(UpdateDir);
            var apk = Path.Combine(UpdateDir, release.FileName);
            Status = L.F("Download della versione {0}…", release.Version.ToString(3));
            await Http.DownloadFileAsync(release.Url, apk, null, (done, total) =>
            {
                if (total > 0) Status = L.F("Download della versione {0}… {1:0}%", release.Version.ToString(3), done * 100.0 / total.Value);
            }, CancellationToken.None);
            if (!Verify(apk, release))
            {
                try { File.Delete(apk); } catch { }
                Status = L.T("Aggiornamento scaricato male: riprova più tardi.");
                return false;
            }
            _apk = apk;
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
            return new ReleaseInfo(version, tag, a.GetProperty("browser_download_url").GetString()!, name, a.GetProperty("size").GetInt64(), sha);
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

    // Hands the package to Android, which asks before installing it (the app closes while it's installed).
    // False: the permission to install apps is missing; its page of the settings has been opened.
    public bool Apply(bool relaunch, string? profileId, bool background)
    {
        if (_apk == null || !File.Exists(_apk)) return false;
        var pm = Ctx.PackageManager!;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O && !pm.CanRequestPackageInstalls())
        {
            Status = L.T("Permetti a Ultimate MP3 Player di installare app, poi torna qui e premi di nuovo Installa.");
            try
            {
                var intent = new Intent(Android.Provider.Settings.ActionManageUnknownAppSources, Android.Net.Uri.Parse("package:" + Ctx.PackageName))
                    .AddFlags(ActivityFlags.NewTask);
                Ctx.StartActivity(intent);
            }
            catch { }
            return false;
        }
        try
        {
            var installer = pm.PackageInstaller;
            var p = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
            p.SetAppPackageName(Ctx.PackageName);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.S) p.SetRequireUserAction((int)PackageInstallUserAction.NotRequired);
            int id = installer.CreateSession(p);
            using var session = installer.OpenSession(id);
            using (var input = File.OpenRead(_apk))
            using (var output = session.OpenWrite("UltimateMP3Player.apk", 0, input.Length))
            {
                input.CopyTo(output);
                session.Fsync(output);
            }
            var done = new Intent(Ctx, typeof(InstallReceiver)).SetAction(InstallReceiver.Action);
            var flags = PendingIntentFlags.UpdateCurrent | (Build.VERSION.SdkInt >= BuildVersionCodes.S ? PendingIntentFlags.Mutable : 0);
            var pi = PendingIntent.GetBroadcast(Ctx, 30, done, flags)!;
            session.Commit(pi.IntentSender);
            Status = L.T("Installazione dell'aggiornamento…");
            return true;
        }
        catch (Exception ex)
        {
            Status = L.T("Aggiornamento non riuscito:") + " " + ex.Message;
            return false;
        }
    }
}

// The installer's answers: Android's confirmation to show, or the outcome.
[BroadcastReceiver(Name = "com.kirbohq.ultimatemp3player.InstallReceiver", Exported = false)]
public sealed class InstallReceiver : BroadcastReceiver
{
    public const string Action = "ump.install";

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context == null || intent == null) return;
        var status = (PackageInstallStatus)intent.GetIntExtra(PackageInstaller.ExtraStatus, -999);
        if (status == PackageInstallStatus.PendingUserAction)
        {
            if (intent.GetParcelableExtra(Intent.ExtraIntent) is Intent confirm)
            {
                confirm.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(confirm);
            }
            return;
        }
        if (status != PackageInstallStatus.Success)
        {
            var msg = intent.GetStringExtra(PackageInstaller.ExtraStatusMessage);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => App.Host?.Session?.Toast(L.T("Aggiornamento non installato.") + (msg != null ? " " + msg : "")));
        }
    }
}
