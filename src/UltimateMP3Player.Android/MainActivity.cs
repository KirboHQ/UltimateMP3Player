using System.Text.RegularExpressions;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Avalonia;
using Avalonia.Android;
using Avalonia.Threading;
using UltimateMP3Player.Core;
using UltimateMP3Player.Platform;

namespace UltimateMP3Player;

// Before any screen or service: where the data, the music and the engines are.
[Android.App.Application(Name = "com.kirbohq.ultimatemp3player.MainApplication", Label = "Ultimate MP3 Player", Icon = "@mipmap/ic_launcher", RoundIcon = "@mipmap/ic_launcher_round")]
public sealed class MainApplication : Android.App.Application
{
    public MainApplication(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public override void OnCreate()
    {
        base.OnCreate();
        // The library, the settings, the covers and the lyrics in the app's private data; the music in its folder of the
        // phone's storage (Android/data/…/files/Music, visible from a computer with the cable), both removed with the app
        // (Android asks whether to keep them).
        var files = FilesDir!.AbsolutePath;
        var data = Path.Combine(files, "data");
        MoveOldData(files, data);
        System.Environment.SetEnvironmentVariable("UMP_DATA", data);
        System.Environment.SetEnvironmentVariable("HOME", files);
        System.Environment.SetEnvironmentVariable("TMPDIR", CacheDir!.AbsolutePath);
        System.Environment.SetEnvironmentVariable("XDG_CACHE_HOME", CacheDir!.AbsolutePath);
        var music = GetExternalFilesDir(Android.OS.Environment.DirectoryMusic)?.AbsolutePath ?? Path.Combine(files, "Music");
        AppPaths.MusicDirOverride = music;
        // The logins made inside the app, for the downloads (there's no browser to read them from).
        SiteLogins.Configure(data);
        // The small copies of the covers, where Android may clean up when space runs low.
        Images.ThumbDir = Path.Combine(CacheDir!.AbsolutePath, "thumbs");
        AndroidEngines.Configure();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) App.Log(ex); };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            App.Log(e.Exception);
            e.SetObserved();
        };
    }

    // The first test builds of 3.5.0 could keep the data in files/Ultimate MP3 Player (the computer's default place):
    // moved once into files/data, where every build looks for it.
    private static void MoveOldData(string files, string data)
    {
        try
        {
            var old = Path.Combine(files, "Ultimate MP3 Player");
            if (!File.Exists(Path.Combine(old, "settings.json")) || File.Exists(Path.Combine(data, "settings.json"))) return;
            if (Directory.Exists(data)) Directory.Delete(data, true);
            Directory.Move(old, data);
        }
        catch { }
    }
}

// The app's only screen. Also where the links shared from other apps arrive (YouTube's or Spotify's "Share", a browser),
// the .ump packs opened from a file manager, a tap on the player's or the downloads' notification.
[Android.App.Activity(Name = "com.kirbohq.ultimatemp3player.MainActivity", Label = "Ultimate MP3 Player", MainLauncher = true, Exported = true, LaunchMode = LaunchMode.SingleTask,
    Theme = "@style/UmpTheme", Icon = "@mipmap/ic_launcher", RoundIcon = "@mipmap/ic_launcher_round",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout |
                           ConfigChanges.SmallestScreenSize | ConfigChanges.Density | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden |
                           ConfigChanges.Navigation | ConfigChanges.Locale | ConfigChanges.LayoutDirection,
    WindowSoftInputMode = SoftInput.AdjustResize)]
[Android.App.IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "text/plain",
    Label = "Scarica con Ultimate MP3 Player")]
[Android.App.IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataSchemes = new[] { "content", "file" }, DataHost = "*", DataMimeType = "*/*", DataPathPatterns = new[] { ".*\\.ump", ".*\\..*\\.ump", ".*\\..*\\..*\\.ump" })]
[Android.App.IntentFilter(new[] { Intent.ActionView }, Categories = new[] { Intent.CategoryDefault },
    DataSchemes = new[] { "content" }, DataMimeType = "application/x-ump")]
public sealed class MainActivity : AvaloniaMainActivity<App>
{
    public const string ExtraOpenPlayer = "ump.open_player", ExtraOpenDownloads = "ump.open_downloads";

    public static MainActivity? Current { get; private set; }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) => base.CustomizeAppBuilder(builder);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Current = this;
        base.OnCreate(savedInstanceState);
        PreferFastestMode();
        Take(Intent);
    }

    // The screen's fastest refresh rate (90, 120, 144 Hz...) at its resolution: without asking, many phones keep an app
    // at 60. Only while the app is on screen (it's this window's wish); a still screen draws nothing anyway.
    private void PreferFastestMode()
    {
        try
        {
            var display = Build.VERSION.SdkInt >= BuildVersionCodes.R ? Display : WindowManager?.DefaultDisplay;
            if (display?.GetMode() is not { } now || Window?.Attributes is not { } attrs) return;
            var best = display.GetSupportedModes()?
                .Where(m => m.PhysicalWidth == now.PhysicalWidth && m.PhysicalHeight == now.PhysicalHeight)
                .OrderByDescending(m => m.RefreshRate)
                .FirstOrDefault();
            // A 60 Hz phone: nothing to ask.
            if (best == null || display.GetSupportedModes()!.Length < 2) return;
            attrs.PreferredDisplayModeId = best.ModeId;
            Window.Attributes = attrs;
        }
        catch (Exception ex) { App.Log(ex); }
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        Take(intent);
    }

    protected override void OnResume()
    {
        base.OnResume();
        Current = this;
        App.Host?.SetVisible(true);
    }

    protected override void OnPause()
    {
        App.Host?.SetVisible(false);
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        if (Current == this) Current = null;
        base.OnDestroy();
    }

    // What came with the intent, handed to the app once it's ready (it may be starting).
    private void Take(Intent? intent)
    {
        if (intent == null) return;
        Incoming? what = null;
        try
        {
            if (intent.Action == Intent.ActionSend && intent.GetStringExtra(Intent.ExtraText) is { } text && FirstLink(text) is { } url)
                what = new Incoming(IncomingKind.Link, url);
            else if (intent.Action == Intent.ActionView && intent.Data is { } data)
            {
                if (data.Scheme is "http" or "https") what = new Incoming(IncomingKind.Link, data.ToString());
                else if (Files.CopyIn(this, data, ".ump") is { } pack) what = new Incoming(IncomingKind.Pack, pack);
            }
            else if (intent.GetBooleanExtra(ExtraOpenPlayer, false)) what = new Incoming(IncomingKind.Player);
            else if (intent.GetBooleanExtra(ExtraOpenDownloads, false)) what = new Incoming(IncomingKind.Downloads);
        }
        catch (Exception ex) { App.Log(ex); }
        // Handled once (a rotation or coming back must not do it again).
        intent.RemoveExtra(ExtraOpenPlayer);
        intent.RemoveExtra(ExtraOpenDownloads);
        if (what != null) Dispatcher.UIThread.Post(() => App.Deliver(what));
    }

    // "Guarda questo video https://youtu.be/xyz": the link in the text.
    private static string? FirstLink(string text)
    {
        var m = Regex.Match(text, @"(https?://|spotify:)\S+", RegexOptions.IgnoreCase);
        return m.Success ? m.Value.TrimEnd('.', ',', ')', ']', '»', '"', '\'') : null;
    }

    // ------------------------------------------------------------------ permissions

    private TaskCompletionSource<bool>? _permission;

    public Task<bool> RequestPermission(string permission)
    {
        if (CheckSelfPermission(permission) == Permission.Granted) return Task.FromResult(true);
        _permission?.TrySetResult(false);
        _permission = new TaskCompletionSource<bool>();
        RequestPermissions(new[] { permission }, 7);
        return _permission.Task;
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == 7) _permission?.TrySetResult(grantResults.Length > 0 && grantResults[0] == Permission.Granted);
    }
}
