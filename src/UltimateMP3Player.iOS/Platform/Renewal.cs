using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Threading;
using BackgroundTasks;
using Foundation;
using ObjCRuntime;
using UIKit;
using UltimateMP3Player.Core;
using UltimateMP3Player.Views;

namespace UltimateMP3Player.Platform;

// The app renewing (and updating) itself: what the installer left in Library/Renewal, LocalDevVPN, which lets an app reach the phone
// it's on, and umprenew, the library that talks to Apple and to the phone (the installer's; tools/ios-deps.sh). Without the library
// none of this shows. What's in that folder (a token, a key) is only ever handed to the library, never read, logged or copied here; but for
// one file, "expires", which is only a date.
public static class Renewal
{
    // Where the phone answers when LocalDevVPN is connected.
    private const string Phone = "10.7.0.1";
    // (Info.plist has the same names)
    private const string RenewTask = "com.kirbohq.ultimatemp3player.renew", InstallTask = "com.kirbohq.ultimatemp3player.install";

    private static readonly string Folder = Path.Combine(NSSearchPath.GetDirectories(NSSearchPathDirectory.LibraryDirectory, NSSearchPathDomain.User)[0], "Renewal");

    // Anything below that shows changed (on the screen's thread).
    public static event Action? Changed;

    // This build carries the library (its Info.plist says so to the installer too).
    public static bool Supported { get; } = File.Exists(Native.Library);

    // The installer put what it takes: until then everything's as without the library.
    public static bool HasKeys => Supported && Keys.All(File.Exists);

    private static readonly string[] Keys = { Path.Combine(Folder, "session.json"), Path.Combine(Folder, "pairing.plist") };

    // The profile the app was installed with, the last one it got by itself (iOS keeps it elsewhere) or the one the cable brought.
    public static DateTime? Expires => HasKeys ? RenewalRules.Latest(Embedded.Value, Saved("expires"), Placed()) : Embedded.Value;

    // (looked at each time: the installer may write it with the app open)
    private static DateTime? Placed()
    {
        try
        {
            var file = new FileInfo(Path.Combine(Folder, "expires"));
            return file is { Exists: true, Length: < 64 } ? RenewalRules.PlacedExpires(File.ReadAllText(file.FullName), DateTime.UtcNow) : null;
        }
        catch { return null; }
    }

    private static readonly Lazy<DateTime?> Embedded = new(() =>
    {
        try { return NSBundle.MainBundle.PathForResource("embedded", "mobileprovision") is { } path ? RenewalRules.ProfileExpires(File.ReadAllBytes(path)) : null; }
        catch { return null; }
    });

    public static DateTime? LastRenewed => HasKeys ? Saved("renewed") : null;

    public static bool Urgent => RenewalRules.Urgent(Expires, DateTime.UtcNow);

    // Null until it's been looked for.
    public static bool? VpnOn { get; private set; }
    public static bool VpnInstalled => CanOpen("localdevvpn://");
    public static bool SideStoreInstalled => CanOpen("sidestore://");

    public static bool Busy { get; private set; }
    // What it's doing, by the library's name for it ("Downloading" is the app's own), and how far the whole of it is.
    public static string Stage { get; private set; } = "";
    public static double Progress { get; private set; }

    // Why the last try didn't work; the text only for what has no words of its own.
    public static RenewalProblem Problem { get; private set; } = (RenewalProblem)(int)NSUserDefaults.StandardUserDefaults.IntForKey(Key("problem"));
    public static string ProblemText { get; private set; } = "";

    // ------------------------------------------------------------------ what's remembered

    // In the defaults, not in the settings: a backup brought from another phone mustn't say until when this one's app works.
    private static string Key(string name) => "ump.renewal." + name;

    private static DateTime? Saved(string name)
        => NSUserDefaults.StandardUserDefaults.DoubleForKey(Key(name)) is > 0 and var s ? DateTime.UnixEpoch.AddSeconds(s) : null;

    private static void Save(string name, DateTime? at)
    {
        if (at is { } t) NSUserDefaults.StandardUserDefaults.SetDouble((t - DateTime.UnixEpoch).TotalSeconds, Key(name));
        else NSUserDefaults.StandardUserDefaults.RemoveObject(Key(name));
    }

    // True the first time only.
    public static bool Once(string name)
    {
        if (Saved(name) != null) return false;
        Save(name, DateTime.UtcNow);
        return true;
    }

    private static bool CanOpen(string url)
    {
        try { return UIApplication.SharedApplication.CanOpenUrl(new NSUrl(url)); }
        catch { return false; }
    }

    // ------------------------------------------------------------------ the life of the app

    // While the app is still starting (iOS takes no background task after that).
    public static void Hook()
    {
        if (!Supported) return;
        // (set just before being replaced by an update: the app's open again)
        Notices.Cancel("updated");
        KeepOutOfBackups(Folder);
        try
        {
            foreach (var id in new[] { RenewTask, InstallTask }) BGTaskScheduler.Shared.Register(id, null, OnTask);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    // iCloud's and the Finder's backups would carry the token and the key around (or what's only passing through: FromComputerSheet).
    internal static void KeepOutOfBackups(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) NSUrl.FromFilename(folder).SetResource(NSUrl.IsExcludedFromBackupKey, NSNumber.FromBoolean(true), out _);
        }
        catch { }
    }

    private static bool _askedVpn;

    // The app's in front: the VPN is looked for, a renewal that's due is done, and what can't go unnoticed is shown.
    public static async void Shown()
    {
        if (!Supported) return;
        try
        {
            KeepOutOfBackups(Folder);
            if (HasKeys)
            {
                // (the installer has been here since, with new keys or a renewal: what was wrong before doesn't count)
                if (Saved("since") is { } since && Keys.Any(f => File.GetLastWriteTimeUtc(f) > since))
                {
                    Save("failed", null);
                    SetProblem(RenewalProblem.None);
                }
                // (back from turning the VPN on: it takes a moment to connect)
                int tries = _askedVpn ? 5 : 1;
                _askedVpn = false;
                while (!await ProbeAsync() && --tries > 0) await Task.Delay(700);
                await AutoAsync();
            }
            Remind();
            Changed?.Invoke();
            await RenewalNotice.ShowPendingAsync();
        }
        catch (Exception ex) { App.Log(ex); }
    }

    // Away: from here on iOS wakes the app when it thinks best, music or not.
    public static void Hidden()
    {
        if (!HasKeys) return;
        try
        {
            var soon = DateTime.UtcNow.AddHours(1);
            var due = RenewalRules.From(Expires) is { } from && from > soon ? from : soon;
            BGTaskScheduler.Shared.Submit(new BGAppRefreshTaskRequest(RenewTask) { EarliestBeginDate = (NSDate)due }, out _);
            BGTaskScheduler.Shared.Submit(new BGProcessingTaskRequest(InstallTask)
            {
                RequiresNetworkConnectivity = true, EarliestBeginDate = (NSDate)(App.Host.Settings.InstallUpdatesAlone ? soon : due),
            }, out _);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    // (on a thread of iOS's own)
    private static void OnTask(BGTask task)
    {
        var stop = new CancellationTokenSource();
        task.ExpirationHandler = () =>
        {
            stop.Cancel();
            Cancel();
        };
        Dispatcher.UIThread.Post(async () =>
        {
            bool fine = true;
            try
            {
                await AutoAsync();
                // (renewed over the cable meanwhile: the reminders were for the old date)
                Remind();
                // (the other kind of task has half a minute: enough for a renewal, not for an update)
                if (task is BGProcessingTask) await App.Host.Updates.InstallAloneAsync(stop.Token);
            }
            catch (Exception ex)
            {
                App.Log(ex);
                fine = false;
            }
            Hidden();
            task.SetTaskCompleted(fine);
        });
    }

    // The fifth and the sixth day after a renewal: each one that works moves them on.
    private static void Remind()
    {
        var ids = new[] { "renewal-1", "renewal-2" };
        int n = 0;
        bool computer = Problem is RenewalProblem.Cable or RenewalProblem.SignIn;
        if (HasKeys && Expires is { } e)
            foreach (var at in RenewalRules.Reminders(e, DateTime.UtcNow))
                Notices.Schedule(ids[n++], L.T(computer ? "NEEDED: the computer, once" : "NEEDED: turn on LocalDevVPN"),
                    L.F(computer ? "Ultimate MP3 Player stops opening after {0}. Plug the iPhone into the computer and open the installer: it puts everything right."
                        : "Ultimate MP3 Player stops opening after {0}. Turn on LocalDevVPN and open the app: it renews itself.", Day(e)), at);
        Notices.Cancel(ids[n..]);
    }

    // "Friday 28 November"
    public static string Day(DateTime utc) => utc.ToLocalTime().ToString("dddd d MMMM", L.Culture);

    // ------------------------------------------------------------------ LocalDevVPN

    // Whether the phone answers itself: quick, and no more than a knock.
    public static async Task<bool> ProbeAsync()
    {
        bool on = false;
        try
        {
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var socket = new TcpClient(AddressFamily.InterNetwork);
            await socket.ConnectAsync(IPAddress.Parse(Phone), UIDevice.CurrentDevice.CheckSystemVersion(17, 4) ? 49152 : 62078, ct.Token);
            on = true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { }
        VpnOn = on;
        if (on && Problem == RenewalProblem.Vpn) SetProblem(RenewalProblem.None);
        Changed?.Invoke();
        return on;
    }

    // The same, when something can't be done without.
    public static async Task<bool> ReachAsync()
    {
        if (await ProbeAsync()) return true;
        SetProblem(RenewalProblem.Vpn);
        Changed?.Invoke();
        return false;
    }

    // Turned on and back here by itself ("ump://" opens nothing else), or its page of the App Store.
    public static void OpenVpn()
    {
        _askedVpn = true;
        Files.OpenUrl(VpnInstalled ? "localdevvpn://enable?scheme=ump" : "https://apps.apple.com/app/id6755608044");
    }

    // ------------------------------------------------------------------ renewing

    private static void SetProblem(RenewalProblem problem, string text = "")
    {
        Problem = problem;
        ProblemText = text;
        NSUserDefaults.StandardUserDefaults.SetInt((nint)(int)problem, Key("problem"));
        Save("since", problem == RenewalProblem.None ? null : DateTime.UtcNow);
    }

    // A renewal that's due, if nothing says to wait: when the app opens or comes back, while the music plays, from a background task.
    public static async Task AutoAsync()
    {
        if (!HasKeys || Busy || !RenewalRules.Due(Expires, DateTime.UtcNow) || !RenewalRules.CanTry(Saved("failed"), Problem, DateTime.UtcNow)) return;
        if (await RenewAsync()) App.Host.Session?.Toast(L.F("Renewed: it works until {0}", Day(Expires ?? DateTime.UtcNow)));
    }

    // Seconds: a new profile for the app that's there. False: Problem says why.
    public static Task<bool> RenewAsync() => WorkAsync("renew-here", Array.Empty<string>(), done =>
    {
        if (done?.Renewed != true) return RenewalProblem.Install;
        Save("expires", done.Expires);
        Save("renewed", DateTime.UtcNow);
        return RenewalProblem.None;
    });

    // Signs an .ipa and installs it over this app, which iOS closes towards the end: what comes back from here is mostly why it didn't.
    public static Task<bool> InstallAsync(string ipa) => WorkAsync("update-here", new[] { ipa }, done => done == null ? RenewalProblem.Other : RenewalProblem.None);

    // The app's own part of an update (the download), shown like the library's.
    public static void Report(string stage, double progress)
    {
        Stage = stage;
        Progress = progress;
        Changed?.Invoke();
    }

    private static async Task<bool> WorkAsync(string command, string[] more, Func<RenewalEvent?, RenewalProblem> outcome)
    {
        if (!HasKeys || Busy) return false;
        Busy = true;
        // (the app left halfway: iOS gives it half a minute more)
        var app = UIApplication.SharedApplication;
        nint extra = UIApplication.BackgroundTaskInvalid;
        void End()
        {
            if (extra != UIApplication.BackgroundTaskInvalid) app.EndBackgroundTask(extra);
            extra = UIApplication.BackgroundTaskInvalid;
        }
        extra = app.BeginBackgroundTask("renewal", () =>
        {
            Cancel();
            End();
        });
        bool done = false;
        Report(command == "renew-here" ? "Renewing" : "Preparing", Progress);
        try
        {
            // (off, nothing was tried: no hour to wait)
            if (!await ReachAsync()) return false;
            var (answer, error) = await RunAsync(command, more);
            var problem = error != null ? RenewalRules.Problem(error) : outcome(answer);
            SetProblem(problem, problem == RenewalProblem.Other ? RenewalRules.Shown(error ?? "") : "");
            Save("failed", problem is RenewalProblem.None or RenewalProblem.Vpn ? null : DateTime.UtcNow);
            return done = problem == RenewalProblem.None;
        }
        finally
        {
            Busy = false;
            if (!done) Notices.Cancel("updated");
            End();
            Remind();
            Report("", 0);
        }
    }

    // The library's whole conversation, on a thread of its own (it blocks, and signing takes a deep stack): its "done", or its error.
    private static async Task<(RenewalEvent? Done, string? Error)> RunAsync(string command, string[] more)
    {
        var args = JsonSerializer.Serialize(new[] { "--data", Folder, command, NSBundle.MainBundle.BundleIdentifier, Phone }.Concat(more).ToArray());
        RenewalEvent? done = null;
        string? error = null;
        Native.Hear = line =>
        {
            if (RenewalRules.Parse(line) is not { } e) return;
            if (e.Name == "done") done = e;
            else if (e.Name == "error") error = e.Message;
            else if (e.Name == "stage") Dispatcher.UIThread.Post(() => OnStage(e));
            else if (e.Name == "ask" && e.What == "revoke") Dispatcher.UIThread.Post(() => _ = AnswerRevokeAsync());
            // (a password or a code: these commands don't ask for them, and the app has nowhere to type them)
            else if (e.Name == "ask") Native.Cancel();
        };
        var over = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            try { over.SetResult(Native.Run(args)); }
            catch (Exception ex) { over.SetException(ex); }
        }, 8 << 20) { IsBackground = true, Name = "renewal" }.Start();
        try
        {
            if (await over.Task != 0) error ??= "It ended without saying why";
        }
        catch (Exception ex)
        {
            // (the library doesn't load: its own path, nothing of the folder's)
            App.Log(ex);
            error = ex.Message;
        }
        Native.Hear = null;
        return (done, error);
    }

    // With nobody at the screen a certificate other apps may be using is never replaced.
    private static async Task AnswerRevokeAsync()
    {
        bool yes = false;
        try { yes = UIApplication.SharedApplication.ApplicationState == UIApplicationState.Active && await RenewalNotice.AskRevokeAsync(); }
        catch (Exception ex) { App.Log(ex); }
        Native.Answer(yes ? "yes" : "no");
    }

    private static void OnStage(RenewalEvent e)
    {
        if (!Busy) return;
        if (e.Stage == "Installing")
        {
            // From here iOS may close the app at any moment: everything's saved, and what says it's done is set to come out by itself a
            // moment after the last time this was reached.
            if (Stage != "Installing") App.Host.SaveAll();
            Notices.Schedule("updated", L.T("Updated"), L.T("Tap to open Ultimate MP3 Player again."), DateTime.UtcNow.AddSeconds(15));
        }
        Report(e.Stage, RenewalRules.Overall(e.Stage, e.Progress));
    }

    private static void Cancel()
    {
        if (Busy) Native.Cancel();
    }

    // The library's C functions (the installer's helper/src/ffi.rs), found like LAME's.
    private static unsafe class Native
    {
        public static string Library => Path.Combine(NSBundle.MainBundle.PrivateFrameworksPath ?? Path.Combine(NSBundle.MainBundle.BundlePath, "Frameworks"),
            "umprenew.framework", "umprenew");

        // Each line it says, on whatever thread it says it.
        public static Action<string>? Hear;

        private static readonly object Gate = new();
        private static delegate* unmanaged[Cdecl]<byte*, delegate* unmanaged[Cdecl]<byte*, void>, int> _run;
        private static delegate* unmanaged[Cdecl]<byte*, void> _answer;
        private static delegate* unmanaged[Cdecl]<void> _cancel;

        private static void Load()
        {
            lock (Gate)
            {
                if (_run != null) return;
                var lib = Dlfcn.dlopen(Library, 0x2);
                if (lib == IntPtr.Zero) throw new InvalidOperationException(Dlfcn.dlerror() ?? Library);
                IntPtr Symbol(string name) => Dlfcn.dlsym(lib, name) is var p && p != IntPtr.Zero ? p : throw new InvalidOperationException("symbol missing: " + name);
                _answer = (delegate* unmanaged[Cdecl]<byte*, void>)Symbol("ump_answer");
                _cancel = (delegate* unmanaged[Cdecl]<void>)Symbol("ump_cancel");
                _run = (delegate* unmanaged[Cdecl]<byte*, delegate* unmanaged[Cdecl]<byte*, void>, int>)Symbol("ump_run");
            }
        }

        // The text is only there during the call.
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static void Say(byte* line)
        {
            try
            {
                if (Marshal.PtrToStringUTF8((IntPtr)line) is { } text) Hear?.Invoke(text);
            }
            catch { }
        }

        public static int Run(string args)
        {
            Load();
            var text = Marshal.StringToCoTaskMemUTF8(args);
            try { return _run((byte*)text, &Say); }
            finally { Marshal.FreeCoTaskMem(text); }
        }

        public static void Answer(string line)
        {
            if (_answer == null) return;
            var text = Marshal.StringToCoTaskMemUTF8(line);
            _answer((byte*)text);
            Marshal.FreeCoTaskMem(text);
        }

        public static void Cancel()
        {
            if (_cancel != null) _cancel();
        }
    }
}
