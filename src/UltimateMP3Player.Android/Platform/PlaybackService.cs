using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using UltimateMP3Player.Core;
using UltimateMP3Player.Services;
using Application = Android.App.Application;

namespace UltimateMP3Player.Platform;

// Keeps the music going with the screen off or the app closed: a foreground service with the media notification (the
// song, its cover, previous / play-pause / next and the heart; the system shows it on the lock screen too). While paused
// the notification stays and can be swiped away, which stops the service. It also holds the audio focus (another app
// that starts playing, a call: pause) and pauses when the headphones are unplugged.
[Service(Name = "com.kirbohq.ultimatemp3player.PlaybackService", Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class PlaybackService : Service
{
    private const int NotificationId = 1;
    private const string Channel = "playback";
    public const string ActionPlayPause = "ump.playpause", ActionNext = "ump.next", ActionPrevious = "ump.previous", ActionFavorite = "ump.favorite",
        ActionStop = "ump.stop", ActionSave = "ump.save";

    private static PlaybackService? _current;
    private bool _foreground;
    private NoisyReceiver? _noisy;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        _current = this;
        CreateChannel(this);
        _noisy = new NoisyReceiver();
        var filter = new IntentFilter(AudioManager.ActionAudioBecomingNoisy);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu) RegisterReceiver(_noisy, filter, ReceiverFlags.NotExported);
        else RegisterReceiver(_noisy, filter);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var c = App.Host?.Media;
        switch (intent?.Action)
        {
            case ActionPlayPause: c?.RaisePlayPause(); break;
            case ActionNext: c?.RaiseNext(); break;
            case ActionPrevious: c?.RaisePrevious(); break;
            case ActionFavorite: c?.RaiseFavorite(); break;
            case ActionSave: c?.RaiseSave(); break;
            case ActionStop:
                c?.RaiseStop();
                StopEverything();
                return StartCommandResult.NotSticky;
        }
        // Started to show the notification (StartForegroundService): it must be in front within a few seconds.
        Show(true);
        return StartCommandResult.NotSticky;
    }

    // Swiped away from the recent apps: the music goes on if it's playing (as in the other players), otherwise it's over.
    public override void OnTaskRemoved(Intent? rootIntent)
    {
        base.OnTaskRemoved(rootIntent);
        if (App.Host?.Media.IsPlaying != true) StopEverything();
    }

    public override void OnDestroy()
    {
        if (_current == this) _current = null;
        try { if (_noisy != null) UnregisterReceiver(_noisy); } catch { }
        Focus.Abandon();
        base.OnDestroy();
    }

    private void StopEverything()
    {
        _foreground = false;
        StopForeground(StopForegroundFlags.Remove);
        StopSelf();
    }

    // The state changed (song, play/pause, heart): the notification follows; playing again brings the service back.
    public static void Refresh()
    {
        var c = App.Host?.Media;
        if (c == null) return;
        if (c.IsPlaying) Focus.Request();
        if (_current is { } s)
        {
            s.Show(false);
            return;
        }
        if (!c.IsPlaying) return;
        var ctx = Application.Context;
        try
        {
            var intent = new Intent(ctx, typeof(PlaybackService));
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O) ctx.StartForegroundService(intent);
            else ctx.StartService(intent);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private void Show(bool starting)
    {
        var c = App.Host?.Media;
        if (c == null || !c.HasTrack)
        {
            if (starting)
            {
                // Asked to start with nothing to show: in front for a moment, as Android requires, then gone.
                Front(MakeNotification(c));
            }
            StopEverything();
            return;
        }
        var n = MakeNotification(c);
        if (c.IsPlaying || starting) Front(n);
        else
        {
            if (_foreground)
            {
                StopForeground(StopForegroundFlags.Detach);
                _foreground = false;
            }
            Manager?.Notify(NotificationId, n);
        }
    }

    private void Front(Notification n)
    {
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q) StartForeground(NotificationId, n, ForegroundService.TypeMediaPlayback);
            else StartForeground(NotificationId, n);
            _foreground = true;
        }
        catch (Exception ex)
        {
            // Android 12+ doesn't let a service come to the front from the background: the notification shows anyway.
            App.Log(ex);
            Manager?.Notify(NotificationId, n);
        }
    }

    private NotificationManager? Manager => (NotificationManager?)GetSystemService(NotificationService);

    public static void CreateChannel(Context ctx)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var m = (NotificationManager?)ctx.GetSystemService(NotificationService);
        if (m?.GetNotificationChannel(Channel) != null) return;
        var ch = new NotificationChannel(Channel, L.T("Riproduzione"), NotificationImportance.Low)
        {
            Description = L.T("Il brano in riproduzione, con i pulsanti per controllarlo"),
            LockscreenVisibility = NotificationVisibility.Public,
        };
        ch.SetShowBadge(false);
        m?.CreateNotificationChannel(ch);
    }

    private Notification MakeNotification(MediaControls? c)
    {
        var b = Build.VERSION.SdkInt >= BuildVersionCodes.O ? new Notification.Builder(this, Channel) : new Notification.Builder(this);
        b.SetSmallIcon(Resource.Drawable.ic_notification)
            .SetContentTitle(c?.Title ?? "Ultimate MP3 Player")
            .SetContentText(c?.Artist ?? "")
            .SetContentIntent(OpenAppIntent(this, true))
            .SetDeleteIntent(ActionIntent(this, ActionStop, 5))
            .SetVisibility(NotificationVisibility.Public)
            .SetShowWhen(false)
            .SetOnlyAlertOnce(true)
            .SetColor(unchecked((int)0xFF7C5CFF))
            .SetOngoing(c?.IsPlaying == true)
            .SetCategory(Notification.CategoryTransport);
        if (c?.Art != null) b.SetLargeIcon(c.Art);
        if (c?.HasTrack == true)
        {
            b.AddAction(Button(Resource.Drawable.ic_previous, L.T("Precedente"), ActionPrevious, 1));
            b.AddAction(c.IsPlaying ? Button(Resource.Drawable.ic_pause, L.T("Pausa"), ActionPlayPause, 2) : Button(Resource.Drawable.ic_play, L.T("Riproduci"), ActionPlayPause, 2));
            b.AddAction(Button(Resource.Drawable.ic_next, L.T("Successivo"), ActionNext, 3));
            b.AddAction(Button(c.IsFavorite ? Resource.Drawable.ic_heart : Resource.Drawable.ic_heart_outline,
                L.T(c.IsFavorite ? "Togli dai Preferiti" : "Aggiungi ai Preferiti"), ActionFavorite, 4));
            // Only in the cache: saved into the library (no playlist).
            if (c.IsTemporary) b.AddAction(Button(Resource.Drawable.ic_download, L.T("Salva nella libreria"), ActionSave, 6));
        }
        var style = new Notification.MediaStyle();
        if (c != null) style.SetMediaSession(c.Token);
        if (c?.HasTrack == true) style.SetShowActionsInCompactView(0, 1, 2);
        b.SetStyle(style);
        return b.Build()!;
    }

    private Notification.Action Button(int icon, string title, string action, int code)
        => new Notification.Action.Builder(Android.Graphics.Drawables.Icon.CreateWithResource(this, icon), title, ActionIntent(this, action, code)).Build()!;

    private static PendingIntent ActionIntent(Context ctx, string action, int code)
    {
        var intent = new Intent(ctx, typeof(PlaybackService)).SetAction(action);
        return PendingIntent.GetService(ctx, code, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    // A tap on the notification (or on the song in the lock screen): the app, on the song's page.
    public static PendingIntent OpenAppIntent(Context ctx, bool player)
    {
        var intent = new Intent(ctx, typeof(MainActivity)).SetAction(Intent.ActionMain).AddCategory(Intent.CategoryLauncher)
            .AddFlags(ActivityFlags.SingleTop | ActivityFlags.NewTask);
        if (player) intent.PutExtra(MainActivity.ExtraOpenPlayer, true);
        return PendingIntent.GetActivity(ctx, player ? 10 : 11, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
    }

    // Headphones unplugged (or Bluetooth gone): pause, like every player.
    private sealed class NoisyReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action == AudioManager.ActionAudioBecomingNoisy) Avalonia.Threading.Dispatcher.UIThread.Post(() => App.Host?.Media.RaisePause());
        }
    }
}

// The audio focus: another app that starts playing pauses this one (and the music comes back after a short
// interruption, like a notification sound or a navigation voice, which only lowers it for a moment).
public static class Focus
{
    private static AudioFocusRequestClass? _request;
    private static bool _held, _resumeAfter;
    private static readonly Listener Callback = new();

    private static AudioManager? Manager => (AudioManager?)Application.Context.GetSystemService(Context.AudioService);

    public static void Request()
    {
        if (_held || Manager is not { } am) return;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            _request ??= new AudioFocusRequestClass.Builder(AudioFocus.Gain)
                .SetAudioAttributes(new AudioAttributes.Builder()!.SetUsage(AudioUsageKind.Media)!.SetContentType(AudioContentType.Music)!.Build()!)!
                .SetOnAudioFocusChangeListener(Callback)!
                .SetWillPauseWhenDucked(false)!
                .Build();
            _held = am.RequestAudioFocus(_request!) == AudioFocusRequest.Granted;
        }
        else _held = am.RequestAudioFocus(Callback, Android.Media.Stream.Music, AudioFocus.Gain) == AudioFocusRequest.Granted;
    }

    public static void Abandon()
    {
        if (!_held || Manager is not { } am) return;
        _held = false;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O && _request != null) am.AbandonAudioFocusRequest(_request);
        else am.AbandonAudioFocus(Callback);
    }

    private sealed class Listener : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
    {
        public void OnAudioFocusChange(AudioFocus focusChange) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var media = App.Host?.Media;
            switch (focusChange)
            {
                case AudioFocus.Loss:
                    // For good (another player): pause; pressing play takes the focus back.
                    _held = false;
                    _resumeAfter = false;
                    Audio.TrackOut.Duck = 1;
                    media?.RaisePause();
                    break;
                case AudioFocus.LossTransient:
                    // A call, a voice message: pause and come back after it.
                    _resumeAfter = media?.IsPlaying == true;
                    media?.RaisePause();
                    break;
                case AudioFocus.LossTransientCanDuck:
                    Audio.TrackOut.Duck = 0.25f;
                    break;
                case AudioFocus.Gain:
                    Audio.TrackOut.Duck = 1;
                    if (_resumeAfter) media?.RaisePlay();
                    _resumeAfter = false;
                    break;
            }
        });
    }
}
