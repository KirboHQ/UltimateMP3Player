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
    private CarReceiver? _car;

    // In front while music asked for from outside (the car, the voice) is still on its way (Hold).
    private static bool _holding;
    private static int _holdVersion;
    private const int HoldStep = 30_000;
    private static readonly Handler Main = new(Looper.MainLooper!);

    // Driving a computer (Connect to computer): this phone is silent, so no audio focus and no pauses for its headphones.
    private static bool Remote => App.Host?.Session?.Player.IsRemote == true;
    // That, or in a room: the service stays in front the whole time (paused too), or Android stops the app and the
    // connection with it.
    private static bool Connected => Remote || App.Host?.Session?.InRoom == true;

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
        // (Android Auto sends this one from its own app)
        _car = new CarReceiver();
        var car = new IntentFilter(CarReceiver.Updated);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu) RegisterReceiver(_car, car, ReceiverFlags.Exported);
        else RegisterReceiver(_car, car);
        _car.Read();
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
                if (Connected)
                {
                    // The notification swiped away: it comes back, the connection goes on (it ends from its own page).
                    Show(false);
                    return StartCommandResult.NotSticky;
                }
                c?.RaiseStop();
                StopEverything();
                return StartCommandResult.NotSticky;
        }
        // Started to show the notification (StartForegroundService): it must be in front within a few seconds.
        Show(true);
        return StartCommandResult.NotSticky;
    }

    // Swiped away from the recent apps (Settings: "When you close the app"): the music goes on if it's playing, as in the other players, or
    // pauses with the notification there to bring it back; not playing, or to close completely, it's over.
    public override void OnTaskRemoved(Intent? rootIntent)
    {
        base.OnTaskRemoved(rootIntent);
        if (App.Host is not { } host || !host.Media.IsPlaying)
        {
            StopEverything();
            return;
        }
        if (!host.Settings.CloseToTray)
        {
            host.Media.RaiseStop();
            StopEverything();
        }
        else if (host.Settings.PauseOnClose) host.Session?.SetPlaying(false);
    }

    public override void OnDestroy()
    {
        if (_current == this) _current = null;
        try { if (_noisy != null) UnregisterReceiver(_noisy); } catch { }
        try { if (_car != null) UnregisterReceiver(_car); } catch { }
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
        if (Remote) Focus.Abandon();
        // (muted: another app may play meanwhile)
        else if (c.IsPlaying && App.Host?.Session?.Player.Silenced != true) Focus.Request();
        if (c.IsPlaying) _holding = false;
        if (_current is { } s)
        {
            s.Show(false);
            return;
        }
        if (c.IsPlaying || Connected) Start();
    }

    // Music asked for from outside the app: a song in the cloud can take many seconds to start, and past the first ones Android 12+
    // no longer lets the service come to the front (ForegroundServiceStartNotAllowedException, and the music is killed with the
    // screen off). Now, inside the command, it does: in front from here until the music plays, or says why it doesn't (Release).
    public static void Hold()
    {
        if (Connected) return;
        _holding = true;
        int version = ++_holdVersion;
        if (_current is { } s) s.Show(false);
        else Start();
        Main.PostDelayed(() => HoldOver(version, 0), HoldStep);
    }

    // (a song still on its way keeps it, up to a few minutes: a slow connection, a long song being fetched)
    private static void HoldOver(int version, int steps)
    {
        if (version != _holdVersion || !_holding) return;
        if (steps < 10 && App.Host?.Session?.Player.IsWaiting == true) Main.PostDelayed(() => HoldOver(version, steps + 1), HoldStep);
        else Release();
    }

    public static void Release()
    {
        if (!_holding) return;
        _holding = false;
        _current?.Show(false);
    }

    private static void Start()
    {
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
        if (c == null || !c.HasTrack && !Connected)
        {
            if (starting || _holding)
            {
                // Asked to start with nothing to show: in front for a moment, as Android requires, then gone.
                Front(MakeNotification(c));
            }
            if (!_holding) StopEverything();
            return;
        }
        var n = MakeNotification(c);
        if (c.IsPlaying || starting || Connected || _holding) Front(n);
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
        var ch = new NotificationChannel(Channel, L.T("Playback"), NotificationImportance.Low)
        {
            Description = L.T("The song playing, with the buttons to control it"),
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
            b.AddAction(Button(Resource.Drawable.ic_previous, L.T("Previous"), ActionPrevious, 1));
            b.AddAction(c.IsPlaying ? Button(Resource.Drawable.ic_pause, L.T("Pause"), ActionPlayPause, 2) : Button(Resource.Drawable.ic_play, L.T("Play"), ActionPlayPause, 2));
            b.AddAction(Button(Resource.Drawable.ic_next, L.T("Next"), ActionNext, 3));
            b.AddAction(Button(c.IsFavorite ? Resource.Drawable.ic_heart : Resource.Drawable.ic_heart_outline,
                L.T(c.IsFavorite ? "Remove from Favorites" : "Add to Favorites"), ActionFavorite, 4));
            // Not saved on the phone: the library icon into the library (in the cloud), or the download arrow for one already in it.
            if (c.IsTemporary) b.AddAction(Button(c.SaveIcon, c.SaveLabel, ActionSave, 6));
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
            if (intent?.Action != AudioManager.ActionAudioBecomingNoisy || Remote || App.Host == null) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(GoQuiet);
        }
    }

    private static void GoQuiet()
    {
        // In a room the music goes on for the others: this phone goes quiet instead.
        if (App.Host?.Session?.Player is { InRoom: true } player) player.SilenceInRoom();
        else App.Host?.Media.RaisePause();
    }

    // The phone unplugged from the car, or Android Auto closed: the music stops with it, as when headphones come off (the sound was
    // going through Android Auto, so "becoming noisy" may never come). Android Auto says it through a broadcast and its provider;
    // the broadcast only says to look. Written without androidx.car.app: these names are what that library itself uses.
    private sealed class CarReceiver : BroadcastReceiver
    {
        public const string Updated = "androidx.car.app.connection.action.CAR_CONNECTION_UPDATED";
        private const string Authority = "content://androidx.car.app.connection", Column = "CarConnectionState";
        private int _state;

        public override void OnReceive(Context? context, Intent? intent) => Read();

        public void Read() => Task.Run(() =>
        {
            int now = Query();
            Main.Post(() =>
            {
                bool gone = _state != 0 && now == 0;
                _state = now;
                if (gone && !Remote && App.Host != null) GoQuiet();
            });
        });

        private static int Query()
        {
            try
            {
                using var cursor = Application.Context.ContentResolver!.Query(Android.Net.Uri.Parse(Authority)!, new[] { Column }, null, null, null);
                return cursor != null && cursor.MoveToNext() ? cursor.GetInt(cursor.GetColumnIndex(Column)) : 0;
            }
            catch { return 0; }
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
            // In a room the music goes on for the others: only this phone goes quiet (pausing would pause the room).
            var player = App.Host?.Session?.Player;
            bool room = player?.InRoom == true;
            switch (focusChange)
            {
                case AudioFocus.Loss:
                    // For good (another player): pause; pressing play takes the focus back.
                    _held = false;
                    _resumeAfter = false;
                    Audio.TrackOut.Duck = 1;
                    if (room) player!.SilenceInRoom();
                    else media?.RaisePause();
                    break;
                case AudioFocus.LossTransient:
                    // A call, a voice message: pause and come back after it.
                    if (room)
                    {
                        Audio.TrackOut.Duck = 0;
                        break;
                    }
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
