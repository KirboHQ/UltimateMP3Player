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
    private string? _id, _coverPath;
    // Said on the car's screen until the next command, or until the music starts.
    private string? _error;
    private bool _errorOpensApp, _waiting, _queued;

    // One for the process: the car's service hands its token out before the app has started, and the app takes this same one.
    private static MediaControls? _shared;
    public static MediaControls Shared => _shared ??= new MediaControls();

    private MediaControls()
    {
        var ctx = Android.App.Application.Context;
        _session = new MediaSession(ctx, "UltimateMP3Player");
        _session.SetCallback(new Callback(this), new Handler(Looper.MainLooper!));
        _session.SetSessionActivity(PlaybackService.OpenAppIntent(ctx, true));
        // (the car keeps the places of next and previous for them, whatever else the session offers)
        var extras = new Bundle();
        extras.PutBoolean("android.media.playback.ALWAYS_RESERVE_SPACE_FOR.ACTION_SKIP_TO_NEXT", true);
        extras.PutBoolean("android.media.playback.ALWAYS_RESERVE_SPACE_FOR.ACTION_SKIP_TO_PREVIOUS", true);
        _session.SetExtras(extras);
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
    // Not saved on the phone: the notification offers the library icon (into the library, in the cloud) for a song not in the
    // library, the download arrow (saved on the phone) for one of the library in the cloud.
    public bool IsTemporary { get; private set; }
    public bool InLibrary { get; private set; }
    public int SaveIcon => InLibrary ? Resource.Drawable.ic_download : Resource.Drawable.ic_library;
    public string SaveLabel => L.T(InLibrary ? "Save to device" : "Add to library");

    public void SetId(string? id) => _id = id;

    public void SetTrack(string? title, string? artist, string? album, string? coverPath, double duration)
    {
        Title = title;
        Artist = artist;
        Album = album;
        _duration = TimeSpan.FromSeconds(Math.Max(0, duration));
        _coverPath = title != null ? coverPath : null;
        var old = Art;
        Art = _coverPath != null ? LoadArt(_coverPath) : null;
        PublishTrack();
        Publish();
        PlaybackService.Refresh();
        if (old != null && !ReferenceEquals(old, Art)) old.Recycle();
    }

    // The song to the system: also again when a car comes, which reads its cover by address.
    public void PublishTrack()
    {
        var meta = new MediaMetadata.Builder()
            .PutString(MediaMetadata.MetadataKeyTitle, Title ?? "")!
            .PutString(MediaMetadata.MetadataKeyArtist, Artist ?? "")!
            .PutString(MediaMetadata.MetadataKeyAlbum, Album ?? "")!
            .PutString(MediaMetadata.MetadataKeyMediaId, _id ?? "")!
            .PutLong(MediaMetadata.MetadataKeyDuration, (long)_duration.TotalMilliseconds)!;
        if (Art != null) meta.PutBitmap(MediaMetadata.MetadataKeyAlbumArt, Art);
        if (CarLibrary.CoverUri(_coverPath)?.ToString() is { } uri)
        {
            meta.PutString(MediaMetadata.MetadataKeyAlbumArtUri, uri);
            meta.PutString(MediaMetadata.MetadataKeyDisplayIconUri, uri);
        }
        _session.SetMetadata(meta.Build());
        UpdateActive();
    }

    // (with a car on, always: its error and its queue are read from here, a song or not)
    private void UpdateActive() => _session.Active = HasTrack || _error != null || CarLibrary.Active;

    public void SetState(bool playing)
    {
        if (IsPlaying == playing && (!playing || _error == null)) return;
        IsPlaying = playing;
        if (playing) _error = null;
        Publish();
        PlaybackService.Refresh();
    }

    // Waiting for a song to come (in the cloud): the car shows it's on its way.
    public void SetWaiting(bool waiting)
    {
        if (_waiting == waiting) return;
        _waiting = waiting;
        Publish();
    }

    // The car's screen is all the driver has: what went wrong is said there, the app's toast is on a phone in a pocket.
    public void SetError(string text, bool openApp = false)
    {
        _error = text;
        _errorOpensApp = openApp;
        UpdateActive();
        Publish();
        PlaybackService.Release();
    }

    public void ClearError()
    {
        if (_error == null) return;
        _error = null;
        Publish();
    }

    public void SetQueue(IList<MediaSession.QueueItem> items, string? name)
    {
        _session.SetQueueTitle(name);
        _session.SetQueue(items);
        bool queued = items.Count > 0;
        if (_queued == queued) return;
        _queued = queued;
        Publish();
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
        CarLibrary.Changed();
    }

    // The volume is the phone's own (its buttons).
    public void SetVolume(double volume) { }

    private void Publish()
    {
        // (the car and the voice need the ones for a song or a search from the very start)
        long actions = PlaybackState.ActionPlay | PlaybackState.ActionPause | PlaybackState.ActionPlayPause | PlaybackState.ActionStop |
                       PlaybackState.ActionPlayFromMediaId | PlaybackState.ActionPlayFromSearch;
        if (HasTrack) actions |= PlaybackState.ActionSkipToNext | PlaybackState.ActionSkipToPrevious | PlaybackState.ActionSeekTo | PlaybackState.ActionSkipToQueueItem;
        // (with music playing it's only said, once: a voice request not understood doesn't stop what's on)
        var code = _error != null && !IsPlaying ? PlaybackStateCode.Error : IsPlaying ? PlaybackStateCode.Playing : _waiting ? PlaybackStateCode.Buffering
            : HasTrack ? PlaybackStateCode.Paused : PlaybackStateCode.None;
        var state = new PlaybackState.Builder()
            .SetActions(actions)!
            .SetState(code, (long)_position.TotalMilliseconds, IsPlaying ? (float)_speed : 0f, _positionAt > 0 ? _positionAt : SystemClock.ElapsedRealtime())!;
        if (_error != null)
        {
            state.SetErrorMessage(_error);
            if (_errorOpensApp)
            {
                var extras = new Bundle();
                extras.PutString("android.media.extras.ERROR_RESOLUTION_ACTION_LABEL", L.T("Open the app"));
                extras.PutParcelable("android.media.extras.ERROR_RESOLUTION_ACTION_INTENT", PlaybackService.OpenAppIntent(Android.App.Application.Context, false));
                state.SetExtras(extras);
            }
            if (IsPlaying) _error = null;
        }
        if (_queued) state.SetActiveQueueItemId(0);
        if (HasTrack)
            state.AddCustomAction(new PlaybackState.CustomAction.Builder(FavoriteAction, L.T(IsFavorite ? "Remove from Favorites" : "Add to Favorites"),
                IsFavorite ? Resource.Drawable.ic_heart : Resource.Drawable.ic_heart_outline).Build());
        // A song not saved on the phone: into the library, or saved on the phone, with a tap.
        if (HasTrack && IsTemporary)
            state.AddCustomAction(new PlaybackState.CustomAction.Builder(SaveAction, SaveLabel, SaveIcon).Build());
        _session.SetPlaybackState(state.Build());
    }

    public void SetTemporary(bool temporary, bool inLibrary)
    {
        if (IsTemporary == temporary && InLibrary == inLibrary) return;
        IsTemporary = temporary;
        InLibrary = inLibrary;
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

    // What the system asks (the lock screen, the notification, the headphones' buttons, a watch, the car). With music to start, the
    // service goes in front first, while the command still allows it; with the app closed, the app starts then (the service's own
    // start already on its way to this thread: starting the app takes seconds, the service has only a few to come to the front).
    private void Command(bool plays, Action what)
    {
        ClearError();
        if (plays) PlaybackService.Hold();
        if (App.Host != null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(what);
            return;
        }
        // (nothing to pause in an app that isn't running, and its dispatcher must not be touched yet)
        if (!plays) return;
        new Handler(Looper.MainLooper!).PostDelayed(() =>
        {
            if (PhoneBoot.Ensure()) Avalonia.Threading.Dispatcher.UIThread.Post(what);
            else SetError(L.T("Open Ultimate MP3 Player on your phone to start."), openApp: true);
        }, 100);
    }

    private void OpenThen(Action what)
    {
        if (CarLibrary.Open() != null) what();
    }

    private sealed class Callback : MediaSession.Callback
    {
        private readonly MediaControls _c;

        public Callback(MediaControls c) => _c = c;

        public override void OnPlay() => _c.Command(true, CarLibrary.PlayDefault);
        public override void OnPause()
        {
            PlaybackService.Release();
            _c.Command(false, _c.RaisePause);
        }

        public override void OnSkipToNext() => _c.Command(true, () => _c.OpenThen(_c.RaiseNext));
        public override void OnSkipToPrevious() => _c.Command(true, () => _c.OpenThen(_c.RaisePrevious));
        public override void OnStop()
        {
            PlaybackService.Release();
            _c.Command(false, _c.RaiseStop);
        }

        public override void OnSeekTo(long pos) => _c.Command(false, () => _c.SeekRequested?.Invoke(TimeSpan.FromMilliseconds(pos)));
        public override void OnPlayFromMediaId(string? mediaId, Bundle? extras) => _c.Command(true, () => CarLibrary.PlayId(mediaId));
        public override void OnPlayFromSearch(string? query, Bundle? extras) => _c.Command(true, () => CarLibrary.Search(query, extras));
        public override void OnSkipToQueueItem(long id) => _c.Command(true, () => CarLibrary.SkipTo(id));

        public override void OnCustomAction(string action, Bundle? extras)
        {
            if (action == FavoriteAction) _c.Command(false, _c.RaiseFavorite);
            else if (action == SaveAction) _c.Command(false, _c.RaiseSave);
        }

        // A single headset button: play/pause (the session's default does that too, this also works while paused).
        public override bool OnMediaButtonEvent(Intent mediaButtonIntent)
        {
            var key = (KeyEvent?)mediaButtonIntent.GetParcelableExtra(Intent.ExtraKeyEvent);
            if (key is { Action: KeyEventActions.Down, KeyCode: Keycode.Headsethook or Keycode.MediaPlayPause })
            {
                _c.Command(!_c.IsPlaying, () =>
                {
                    if (App.Host.Session != null) _c.RaisePlayPause();
                    else CarLibrary.PlayDefault();
                });
                return true;
            }
            return base.OnMediaButtonEvent(mediaButtonIntent);
        }
    }
}
