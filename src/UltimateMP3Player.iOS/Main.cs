using System.Text.RegularExpressions;
using Avalonia.iOS;
using Foundation;
using UIKit;
using UltimateMP3Player.Core;
using UltimateMP3Player.Platform;

namespace UltimateMP3Player;

public static class Program
{
    private static void Main(string[] args)
    {
        Startup.Configure();
        UIApplication.Main(args, null, typeof(AppDelegate));
    }
}

// Avalonia's: the window, the screen's MainView (App.OnFrameworkInitializationCompleted), files and links opened with the
// app, coming to the front and going away (Lifecycle).
[Register("AppDelegate")]
public sealed partial class AppDelegate : AvaloniaAppDelegate<App>
{
}

// Before any screen: where the data and the music are, what replaces ffmpeg, the engines (Python inside the app).
public static class Startup
{
    public static void Configure()
    {
        // The library, the settings, the covers and the lyrics in Library/Application Support (out of sight, in the
        // iCloud backup); the music in Documents/Music and errors.log in Documents, both in the Files app (On My iPhone ›
        // Ultimate MP3 Player). Removing the app removes them.
        var support = Folder(NSSearchPathDirectory.ApplicationSupportDirectory);
        var documents = Folder(NSSearchPathDirectory.DocumentDirectory);
        var caches = Folder(NSSearchPathDirectory.CachesDirectory);
        var data = Path.Combine(support, "data");
        Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable("UMP_DATA", data);
        AppPaths.MusicDirOverride = Path.Combine(documents, "Music");
        App.LogFile = Path.Combine(documents, "errors.log");
        AppPaths.Rename(Path.Combine(documents, "errori.log"), App.LogFile);
        // The app's folder takes another name with some updates: the songs' paths saved before follow it.
        _home = Path.GetDirectoryName(documents.TrimEnd('/')) ?? "";
        AppPaths.MovedPath = MovedPath;
        // The small copies of the covers, where iOS may clean up when space runs low.
        Images.ThumbDir = Path.Combine(caches, "thumbs");
        // The songs heard without saving them (suggested, in the cloud) stay out of the iCloud backup: they come back by
        // themselves.
        ExcludeFromBackup(AppPaths.CacheDir);
        // Dark clock and battery over the light look (Info.plist's style is the dark look's; the app sets it, not each screen).
#pragma warning disable CA1422, CS0618
        Themes.Changed += () => UIApplication.SharedApplication.SetStatusBarStyle(Themes.IsLight ? UIStatusBarStyle.DarkContent : UIStatusBarStyle.LightContent, false);
#pragma warning restore CA1422, CS0618
        AudioAnalysis.Native = new MediaTools();
        SiteLogins.Configure(data);
        IosEngines.Configure(data);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) App.Log(ex); };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            App.Log(e.Exception);
            e.SetObserved();
        };
    }

    private static string Folder(NSSearchPathDirectory which)
    {
        var path = NSFileManager.DefaultManager.GetUrls(which, NSSearchPathDomain.User)[0].Path!;
        Directory.CreateDirectory(path);
        return path;
    }

    private static string _home = "";
    private static readonly Regex Container = new(@"^(/private)?/var/mobile/Containers/Data/Application/[0-9A-Fa-f-]{36}(?=/)");

    private static string MovedPath(string path)
    {
        var m = Container.Match(path);
        return m.Success && _home.Length > 0 ? _home + path[m.Length..] : path;
    }

    private static void ExcludeFromBackup(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            NSUrl.FromFilename(dir).SetResource(NSUrl.IsExcludedFromBackupKey, NSNumber.FromBoolean(true), out _);
        }
        catch { }
    }
}
