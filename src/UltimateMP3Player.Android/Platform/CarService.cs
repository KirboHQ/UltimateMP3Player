using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media.Browse;
using Android.OS;
using Android.Runtime;
using Android.Service.Media;
using Java.Security;
using UltimateMP3Player.Services;

namespace UltimateMP3Player.Platform;

// What Android Auto (and the Assistant) browses: the session's token at once, the library when it asks for a list. Exported,
// because they bind to it from outside; only the clients CarClients knows get an answer.
[Service(Name = "com.kirbohq.ultimatemp3player.CarService", Exported = true, Label = "Ultimate MP3 Player")]
[IntentFilter(new[] { "android.media.browse.MediaBrowserService" })]
public sealed class CarService : MediaBrowserService
{
    public override void OnCreate()
    {
        base.OnCreate();
        SessionToken = MediaControls.Shared.Token;
        CarLibrary.Service = this;
    }

    public override void OnDestroy()
    {
        CarLibrary.Service = null;
        CarLibrary.SetActive(false);
        base.OnDestroy();
    }

    // Quick and without starting the app: Bluetooth and the system list every media app by asking this. The system's "resume the
    // last music" (EXTRA_RECENT) gets no answer, or it would start the app at every boot.
    public override BrowserRoot? OnGetRoot(string clientPackageName, int clientUid, Bundle? rootHints)
    {
        if (rootHints?.GetBoolean(BrowserRoot.ExtraRecent) == true || !CarClients.Allowed(this, clientPackageName, clientUid)) return null;
        if (clientUid != Process.MyUid()) CarLibrary.AddClient(clientPackageName, rootHints);
        return new BrowserRoot(CarLibrary.Root, CarLibrary.RootExtras());
    }

    // The answer may take a while (the first list starts the app): it comes later, from the main thread.
    public override void OnLoadChildren(string parentId, Result result)
    {
        result.Detach();
        new Handler(Looper.MainLooper!).Post(() => CarLibrary.Load(parentId, items => result.SendResult(new JavaList<MediaBrowser.MediaItem>(items))));
    }
}

// Who may read the library: this app, Android itself, the system's own apps, and Android Auto and Google's assistant checked by
// their signature (a name alone can be taken by any app). The test tool only in a debug build.
public static class CarClients
{
    private const string GoogleRelease = "19:75:b2:f1:71:77:bc:89:a5:df:f3:1f:9e:64:a6:ca:e2:81:a5:3d:c1:d1:d5:9b:1d:14:7f:e1:c8:2a:fa:00";

    private static readonly Dictionary<string, string[]> Signed = new()
    {
        ["com.google.android.projection.gearhead"] = new[]
        {
            GoogleRelease,
            "70:81:1a:3e:ac:fd:2e:83:e1:8d:a9:bf:ed:e5:2d:f1:6c:e9:1f:2e:69:a4:4d:21:f1:8a:b6:69:91:13:07:71",
            "fd:b0:0c:43:db:de:8b:51:cb:31:2a:a8:1d:3b:5f:a1:77:13:ad:b9:4b:28:f5:98:d7:7f:8e:b8:9d:ac:ee:df",
            "1c:a8:dc:c0:be:d3:cb:d8:72:d2:cb:79:12:00:c0:29:2c:a9:97:57:68:a8:2d:67:6b:8b:42:4f:b6:5b:52:95",
        },
        ["com.google.android.googlequicksearchbox"] = new[]
        {
            GoogleRelease,
            "f0:fd:6c:5b:41:0f:25:cb:25:c3:b5:33:46:c8:97:2f:ae:30:f8:ee:74:11:df:91:04:80:ad:6b:2d:60:db:83",
        },
        ["com.google.android.autosimulator"] = new[] { GoogleRelease },
    };

    public static bool Allowed(Context ctx, string package, int uid)
    {
        if (uid == Process.MyUid() || uid == 1000) return true;
#if DEBUG
        if (package == "com.example.android.mediacontroller") return true;
#endif
        try
        {
            var pm = ctx.PackageManager!;
            // (the name it gave must be its own)
            if (pm.GetPackagesForUid(uid)?.Contains(package) != true) return false;
            if (pm.GetApplicationInfo(package, 0).Flags.HasFlag(ApplicationInfoFlags.System)) return true;
            return Signed.TryGetValue(package, out var known) && Signers(pm, package).Any(known.Contains);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return false;
        }
    }

    private static IEnumerable<string> Signers(PackageManager pm, string package)
    {
        Android.Content.PM.Signature[]? list;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
        {
            var info = pm.GetPackageInfo(package, PackageInfoFlags.SigningCertificates)?.SigningInfo;
            list = info?.HasMultipleSigners == true ? info.GetApkContentsSigners() : info?.GetSigningCertificateHistory();
        }
        else
        {
#pragma warning disable CA1422, CS0618
            list = pm.GetPackageInfo(package, PackageInfoFlags.Signatures)?.Signatures?.ToArray();
#pragma warning restore CA1422, CS0618
        }
        foreach (var s in list ?? Array.Empty<Android.Content.PM.Signature>())
        {
            var digest = MessageDigest.GetInstance("SHA-256")!.Digest(s.ToByteArray()!)!;
            yield return string.Join(":", digest.Select(b => ((byte)b).ToString("x2")));
        }
    }
}
