using CoreGraphics;
using MediaPlayer;
using UIKit;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Services;

// The song on the lock screen, in Control Center, on the Apple Watch and in the car, and the buttons of the headphones:
// iOS's Now Playing (what the Android app's MediaSession is). The heart is the "like" command (shown where iOS offers it).
public sealed class MediaControls : IDisposable
{
    private readonly List<(MPRemoteCommand Command, Foundation.NSObject Target)> _targets = new();
    private TimeSpan _position, _duration;
    private double _speed = 1;
    private MPMediaItemArtwork? _art;

    public MediaControls()
    {
        var c = MPRemoteCommandCenter.Shared;
        On(c.PlayCommand, RaisePlay);
        On(c.PauseCommand, RaisePause);
        On(c.TogglePlayPauseCommand, RaisePlayPause);
        On(c.NextTrackCommand, RaiseNext);
        On(c.PreviousTrackCommand, RaisePrevious);
        On(c.StopCommand, RaiseStop);
        On(c.LikeCommand, RaiseFavorite);
        _targets.Add((c.ChangePlaybackPositionCommand, c.ChangePlaybackPositionCommand.AddTarget(e =>
        {
            if (e is MPChangePlaybackPositionCommandEvent p) Post(() => SeekRequested?.Invoke(TimeSpan.FromSeconds(p.PositionTime)));
            return MPRemoteCommandHandlerStatus.Success;
        })));
        c.LikeCommand.LocalizedTitle = L.T("Aggiungi ai Preferiti");
        c.LikeCommand.LocalizedShortTitle = L.T("Preferiti");
        Publish();
    }

    private void On(MPRemoteCommand command, Action raise)
    {
        command.Enabled = true;
        _targets.Add((command, command.AddTarget(_ =>
        {
            Post(raise);
            return MPRemoteCommandHandlerStatus.Success;
        })));
    }

    private static void Post(Action a) => Avalonia.Threading.Dispatcher.UIThread.Post(a);

    public event Action? PlayPressed;
    public event Action? PausePressed;
    public event Action? PlayPausePressed;
    public event Action? NextPressed;
    public event Action? PreviousPressed;
    public event Action? FavoritePressed;
    public event Action? SavePressed;
    public event Action? StopPressed;
    public event Action<TimeSpan>? SeekRequested;

    public string? Title { get; private set; }
    public string? Artist { get; private set; }
    public string? Album { get; private set; }
    public bool HasTrack => Title != null;
    public bool IsPlaying { get; private set; }
    public bool IsFavorite { get; private set; }
    public bool IsTemporary { get; private set; }
    public bool InLibrary { get; private set; }

    public void SetTrack(string? title, string? artist, string? album, string? coverPath, double duration)
    {
        Title = title;
        Artist = artist;
        Album = album;
        _duration = TimeSpan.FromSeconds(Math.Max(0, duration));
        _position = TimeSpan.Zero;
        _art?.Dispose();
        _art = title != null && coverPath != null ? LoadArt(coverPath) : null;
        Publish();
    }

    public void SetState(bool playing)
    {
        if (IsPlaying == playing) return;
        IsPlaying = playing;
        Publish();
    }

    public void SetTimeline(TimeSpan position, TimeSpan duration, double speed)
    {
        _position = position;
        if (duration > TimeSpan.Zero) _duration = duration;
        _speed = speed > 0 ? speed : 1;
        Publish();
    }

    public void SetFavorite(bool favorite)
    {
        if (IsFavorite == favorite) return;
        IsFavorite = favorite;
        Publish();
    }

    public void SetTemporary(bool temporary, bool inLibrary)
    {
        IsTemporary = temporary;
        InLibrary = inLibrary;
    }

    // The volume is the phone's own (its buttons).
    public void SetVolume(double volume) { }

    // iOS moves the bar on by itself from the position and the speed given here.
    private void Publish()
    {
        var c = MPRemoteCommandCenter.Shared;
        c.NextTrackCommand.Enabled = HasTrack;
        c.PreviousTrackCommand.Enabled = HasTrack;
        c.ChangePlaybackPositionCommand.Enabled = HasTrack;
        c.LikeCommand.Enabled = HasTrack;
        c.LikeCommand.Active = IsFavorite;
        var center = MPNowPlayingInfoCenter.DefaultCenter;
        if (!HasTrack)
        {
            center.NowPlaying = new MPNowPlayingInfo();
            return;
        }
        center.NowPlaying = new MPNowPlayingInfo
        {
            Title = Title,
            Artist = Artist,
            AlbumTitle = Album,
            Artwork = _art,
            PlaybackDuration = _duration.TotalSeconds,
            ElapsedPlaybackTime = _position.TotalSeconds,
            PlaybackRate = IsPlaying ? _speed : 0,
            DefaultPlaybackRate = 1,
            MediaType = MPNowPlayingInfoMediaType.Audio,
        };
    }

    private static MPMediaItemArtwork? LoadArt(string path)
    {
        try
        {
            if (!File.Exists(path) || UIImage.FromFile(path) is not { } image) return null;
            return new MPMediaItemArtwork(image.Size, (CGSize _) => image);
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
        foreach (var (command, target) in _targets) command.RemoveTarget(target);
        _targets.Clear();
        MPNowPlayingInfoCenter.DefaultCenter.NowPlaying = new MPNowPlayingInfo();
    }
}
