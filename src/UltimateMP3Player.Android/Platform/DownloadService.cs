using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using UltimateMP3Player.Core;
using Application = Android.App.Application;

namespace UltimateMP3Player.Platform;

// Downloads going on with the app in the background or the screen off: a foreground service with a notification
// ("3 songs downloading"), only while there is something to download. Without it Android would stop the app halfway.
[Service(Name = "com.kirbohq.ultimatemp3player.DownloadService", Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class DownloadService : Service
{
    private const int NotificationId = 2;
    private const string Channel = "downloads";
    private static DownloadService? _current;
    // What the queue wants now (a service that starts after the downloads are over goes away at once), and its text.
    private static bool _wanted, _starting, _showPosted, _stopPosted;
    private static string? _summary, _shownSummary;
    private static double? _percent;
    private static int _shownPercent = -2;
    private static long _lastShown, _lastFailed;
    private PowerManager.WakeLock? _wake;
    private PendingIntent? _open, _cancel;
    private Android.Graphics.Drawables.Icon? _cancelIcon;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        _current = this;
        _starting = false;
        CreateChannel(this);
        try
        {
            _wake = ((PowerManager?)GetSystemService(PowerService))?.NewWakeLock(WakeLockFlags.Partial, "UltimateMP3Player:downloads");
            _wake?.Acquire(TimeSpan.FromHours(3).Ticks / TimeSpan.TicksPerMillisecond);
        }
        catch { }
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == "ump.cancel")
        {
            App.Host?.Downloads.CancelAll();
            return StartCommandResult.NotSticky;
        }
        // Started again while it was stopping: the same service carries on.
        _current = this;
        _starting = false;
        // In front at once, as Android requires of a service started this way, even if the downloads are already over.
        Show(true);
        if (!_wanted) StopNow();
        return StartCommandResult.NotSticky;
    }

    // Android 15 gives a data-sync service 6 hours a day: then it must stop (or the app is closed).
    public override void OnTimeout(int startId, ForegroundService fgsServiceType)
    {
        StopNow();
    }

    public override void OnDestroy()
    {
        if (_current == this) _current = null;
        try { if (_wake?.IsHeld == true) _wake.Release(); } catch { }
        _open?.Dispose();
        _cancel?.Dispose();
        _cancelIcon?.Dispose();
        base.OnDestroy();
    }

    // Called when the queue changes: started with the first download, the text follows (at most once a second: faster
    // ones Android drops anyway, and each is a new notification), stopped a moment after the last one (a playlist that
    // ends and the next one that starts don't take it down and up again).
    public static void Update(bool active, string? summary, double? percent)
    {
        _wanted = active;
        _summary = summary;
        _percent = percent;
        if (!active)
        {
            if (_current != null && !_stopPosted)
            {
                _stopPosted = true;
                Avalonia.Threading.DispatcherTimer.RunOnce(() =>
                {
                    _stopPosted = false;
                    if (!_wanted) _current?.StopNow();
                }, TimeSpan.FromSeconds(2));
            }
            return;
        }
        if (_current == null)
        {
            Start();
            return;
        }
        if (_showPosted) return;
        _showPosted = true;
        long wait = Math.Clamp(1000 - (SystemClock.ElapsedRealtime() - _lastShown), 0, 1000);
        Avalonia.Threading.DispatcherTimer.RunOnce(() =>
        {
            _showPosted = false;
            if (_wanted) _current?.Show(false);
        }, TimeSpan.FromMilliseconds(wait));
    }

    private static void Start()
    {
        // Android 12+ refuses it from the background: tried again in a while, not at every change of the queue.
        if (_starting || SystemClock.ElapsedRealtime() - _lastFailed < 30_000) return;
        _starting = true;
        var ctx = Application.Context;
        try
        {
            using var intent = new Intent(ctx, typeof(DownloadService));
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O) ctx.StartForegroundService(intent);
            else ctx.StartService(intent);
        }
        catch (Exception ex)
        {
            _starting = false;
            _lastFailed = SystemClock.ElapsedRealtime();
            App.Log(ex);
        }
    }

    private void Show(bool front)
    {
        int pct = _percent is double p ? (int)Math.Clamp(p, 0, 100) : -1;
        if (!front && _summary == _shownSummary && pct == _shownPercent) return;
        _shownSummary = _summary;
        _shownPercent = pct;
        _lastShown = SystemClock.ElapsedRealtime();
        // The Java objects of a notification let go at once (not when the garbage collector gets to them).
        using var n = MakeNotification(_summary, pct);
        try
        {
            if (front)
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.Q) StartForeground(NotificationId, n, ForegroundService.TypeDataSync);
                else StartForeground(NotificationId, n);
            }
            else
            {
                ((NotificationManager?)GetSystemService(NotificationService))?.Notify(NotificationId, n);
            }
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private void StopNow()
    {
        if (_current == this) _current = null;
        _shownSummary = null;
        _shownPercent = -2;
        try { StopForeground(StopForegroundFlags.Remove); } catch { }
        StopSelf();
    }

    private static void CreateChannel(Context ctx)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var m = (NotificationManager?)ctx.GetSystemService(NotificationService);
        if (m?.GetNotificationChannel(Channel) != null) return;
        var ch = new NotificationChannel(Channel, L.T("Download"), NotificationImportance.Low) { Description = L.T("I brani che si stanno scaricando") };
        ch.SetShowBadge(false);
        m?.CreateNotificationChannel(ch);
    }

    private Notification MakeNotification(string? summary, int percent)
    {
        if (_open == null)
        {
            using var open = new Intent(this, typeof(MainActivity)).SetAction(Intent.ActionMain)!.AddCategory(Intent.CategoryLauncher)!
                .PutExtra(MainActivity.ExtraOpenDownloads, true)!.AddFlags(ActivityFlags.SingleTop | ActivityFlags.NewTask)!;
            _open = PendingIntent.GetActivity(this, 20, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        }
        if (_cancel == null)
        {
            using var cancel = new Intent(this, typeof(DownloadService)).SetAction("ump.cancel")!;
            _cancel = PendingIntent.GetService(this, 21, cancel, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        }
        _cancelIcon ??= Android.Graphics.Drawables.Icon.CreateWithResource(this, Resource.Drawable.ic_close);
        using var b = Build.VERSION.SdkInt >= BuildVersionCodes.O ? new Notification.Builder(this, Channel) : new Notification.Builder(this);
        using var actionBuilder = new Notification.Action.Builder(_cancelIcon, L.T("Annulla tutti"), _cancel);
        using var action = actionBuilder.Build();
        b.SetSmallIcon(Resource.Drawable.ic_download)
            .SetContentTitle(L.T("Download in corso"))
            .SetContentText(summary ?? "")
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetShowWhen(false)
            .SetColor(unchecked((int)0xFF7C5CFF))
            .SetContentIntent(_open)
            .AddAction(action);
        if (percent >= 0) b.SetProgress(100, percent, false);
        else b.SetProgress(0, 0, true);
        return b.Build()!;
    }
}
