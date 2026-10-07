using Android.Content;
using Android.Graphics;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using Android.Views;
using UltimateMP3Player.Core;
using UltimateMP3Player.Platform;

namespace UltimateMP3Player.Services;

// The song on the lock screen, in the notification shade, on the watch and in the car, and the buttons of Bluetooth
// headphones: an Android MediaSession (what the media flyout is on Windows and MPRIS on Linux), shown by the notification
// of PlaybackService. The heart is a button there too (a custom action of the session).
public sealed class MediaControls : IDisposable
{
    public const string FavoriteAction = "ump.favorite", SaveAction = "ump.save";

    private readonly MediaSession _session;
    private TimeSpan _position, _duration;
    // When _position was read: the system moves the bar on from there (a heart tapped later doesn't take it back).
    private long _positionAt;
    private double _speed = 1;

    public MediaControls()
    {
        var ctx = Android.App.Application.Context;
        _session = new MediaSession(ctx, "UltimateMP3Player");
        _session.SetCallback(new Callback(this), new Handler(Looper.MainLooper!));
        _session.SetSessionActivity(PlaybackService.OpenAppIntent(ctx, true));
        Publish();
    }

    public event Action? PlayPressed;
    public event Action? PausePressed;
    public event Action? PlayPausePressed;
    public event Action? NextPressed;
    public event Action? PreviousPressed;
    public event Action? FavoritePressed;
    public event Action? SavePressed;
    public event Action? StopPressed;
    public event Action<TimeSpan>? SeekRequested;

    public MediaSession.Token Token => _session.SessionToken;
    public string? Title { get; private set; }
    public string? Artist { get; private set; }
    public string? Album { get; private set; }
    public Bitmap? Art { get; private set; }
    public bool HasTrack => Title != null;
    public bool IsPlaying { get; private set; }
    public bool IsFavorite { get; private set; }
    // Only in the cache, not in the library: the notification offers to save it.
    public bool IsTemporary { get; private set; }

    public void SetTrack(string? title, string? artist, string? album, string? coverPath, double duration)
    {
        Title = title;
        Artist = artist;
        Album = album;
        _duration = TimeSpan.FromSeconds(Math.Max(0, duration));
        var old = Art;
        Art = title != null && coverPath != null ? LoadArt(coverPath) : null;
        var meta = new MediaMetadata.Builder()
            .PutString(MediaMetadata.MetadataKeyTitle, title ?? "")!
            .PutString(MediaMetadata.MetadataKeyArtist, artist ?? "")!
            .PutString(MediaMetadata.MetadataKeyAlbum, album ?? "")!
            .PutLong(MediaMetadata.MetadataKeyDuration, (long)_duration.TotalMilliseconds)!;
        if (Art != null) meta.PutBitmap(MediaMetadata.MetadataKeyAlbumArt, Art);
        _session.SetMetadata(meta.Build());
        _session.Active = title != null;
        Publish();
        PlaybackService.Refresh();
        if (old != null && !ReferenceEquals(old, Art)) old.Recycle();
    }

    public void SetState(bool playing)
    {
        if (IsPlaying == playing) return;
        IsPlaying = playing;
        Publish();
        PlaybackService.Refresh();
    }

    public void SetTimeline(TimeSpan position, TimeSpan duration, double speed)
    {
        _position = position;
        _positionAt = SystemClock.ElapsedRealtime();
        if (duration > TimeSpan.Zero) _duration = duration;
        _speed = speed > 0 ? speed : 1;
        Publish();
    }

    public void SetFavorite(bool favorite)
    {
        if (IsFavorite == favorite) return;
        IsFavorite = favorite;
        Publish();
        PlaybackService.Refresh();
    }

    // The volume is the phone's own (its buttons).
    public void SetVolume(double volume) { }

    private void Publish()
    {
        long actions = PlaybackState.ActionPlay | PlaybackState.ActionPause | PlaybackState.ActionPlayPause | PlaybackState.ActionStop;
        if (HasTrack) actions |= PlaybackState.ActionSkipToNext | PlaybackState.ActionSkipToPrevious | PlaybackState.ActionSeekTo;
        var state = new PlaybackState.Builder()
            .SetActions(actions)!
            .SetState(IsPlaying ? PlaybackStateCode.Playing : HasTrack ? PlaybackStateCode.Paused : PlaybackStateCode.None,
                (long)_position.TotalMilliseconds, IsPlaying ? (float)_speed : 0f, _positionAt > 0 ? _positionAt : SystemClock.ElapsedRealtime())!;
        if (HasTrack)
            state.AddCustomAction(new PlaybackState.CustomAction.Builder(FavoriteAction, L.T(IsFavorite ? "Togli dai Preferiti" : "Aggiungi ai Preferiti"),
                IsFavorite ? Resource.Drawable.ic_heart : Resource.Drawable.ic_heart_outline).Build());
        // A song only in the cache (heard without downloading it, or suggested): into the library with a tap.
        if (HasTrack && IsTemporary)
            state.AddCustomAction(new PlaybackState.CustomAction.Builder(SaveAction, L.T("Salva nella libreria"), Resource.Drawable.ic_download).Build());
        _session.SetPlaybackState(state.Build());
    }

    public void SetTemporary(bool temporary)
    {
        if (IsTemporary == temporary) return;
        IsTemporary = temporary;
        Publish();
        PlaybackService.Refresh();
    }

    // The cover for the lock screen: at most 720 px (Android keeps it in memory).
    private static Bitmap? LoadArt(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bounds = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeFile(path, bounds);
            int sample = 1;
            while (bounds.OutWidth / (sample * 2) >= 720) sample *= 2;
            return BitmapFactory.DecodeFile(path, new BitmapFactory.Options { InSampleSize = sample });
        }
        catch { return null; }
    }

    internal void RaisePlay() => PlayPressed?.Invoke();
    internal void RaisePause() => PausePressed?.Invoke();
    internal void RaisePlayPause() => PlayPausePressed?.Invoke();
    internal void RaiseNext() => NextPressed?.Invoke();
    internal void RaisePrevious() => PreviousPressed?.Invoke();
    internal void RaiseFavorite() => FavoritePressed?.Invoke();
    internal void RaiseSave() => SavePressed?.Invoke();
    internal void RaiseStop() => StopPressed?.Invoke();

    public void Dispose()
    {
        _session.Active = false;
        _session.Release();
    }

    // What the system asks (the lock screen, the notification, the headphones' buttons, a watch).
    private sealed class Callback : MediaSession.Callback
    {
        private readonly MediaControls _c;

        public Callback(MediaControls c) => _c = c;

        public override void OnPlay() => Avalonia.Threading.Dispatcher.UIThread.Post(_c.RaisePlay);
        public override void OnPause() => Avalonia.Threading.Dispatcher.UIThread.Post(_c.RaisePause);
        public override void OnSkipToNext() => Avalonia.Threading.Dispatcher.UIThread.Post(_c.RaiseNext);
        public override void OnSkipToPrevious() => Avalonia.Threading.Dispatcher.UIThread.Post(_c.RaisePrevious);
        public override void OnStop() => Avalonia.Threading.Dispatcher.UIThread.Post(_c.RaiseStop);
        public override void OnSeekTo(long pos) => Avalonia.Threading.Dispatcher.UIThread.Post(() => _c.SeekRequested?.Invoke(TimeSpan.FromMilliseconds(pos)));

        public override void OnCustomAction(string action, Bundle? extras)
        {
            if (action == FavoriteAction) Avalonia.Threading.Dispatcher.UIThread.Post(_c.RaiseFavorite);
            else if (action == SaveAction) Avalonia.Threading.Dispatcher.UIThread.Post(_c.RaiseSave);
        }

        // A single headset button: play/pause (the session's default does that too, this also works while paused).
        public override bool OnMediaButtonEvent(Intent mediaButtonIntent)
        {
            var key = (KeyEvent?)mediaButtonIntent.GetParcelableExtra(Intent.ExtraKeyEvent);
            if (key is { Action: KeyEventActions.Down, KeyCode: Keycode.Headsethook or Keycode.MediaPlayPause })
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(_c.RaisePlayPause);
                return true;
            }
            return base.OnMediaButtonEvent(mediaButtonIntent);
        }
    }
}
