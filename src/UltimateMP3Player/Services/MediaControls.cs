using System.IO;
using System.Windows.Interop;
using System.Windows.Threading;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;

namespace UltimateMP3Player.Services;

// Media keys, volume flyout and lock screen controls.
public sealed class MediaControls : IDisposable
{
    private readonly HwndSource _host;
    private readonly SystemMediaTransportControls? _smtc;
    private string? _coverPath;

    public event Action? PlayPressed;
    public event Action? PausePressed;
    public event Action? NextPressed;
    public event Action? PreviousPressed;
    public event Action<TimeSpan>? SeekRequested;

    public MediaControls(Dispatcher ui)
    {
        // Own hidden window: survives closing to the tray.
        _host = new HwndSource(new HwndSourceParameters("UltimateMP3Player.Media") { Width = 0, Height = 0, WindowStyle = 0 });
        // Its id gives Windows the app's name and icon (otherwise "Unknown app").
        AppIdentity.ApplyTo(_host.Handle);
        try
        {
            _smtc = SystemMediaTransportControlsInterop.GetForWindow(_host.Handle);
            _smtc.IsEnabled = true;
            _smtc.IsPlayEnabled = _smtc.IsPauseEnabled = _smtc.IsNextEnabled = _smtc.IsPreviousEnabled = true;
            _smtc.IsStopEnabled = false;
            _smtc.ButtonPressed += (_, e) => ui.BeginInvoke(() =>
            {
                switch (e.Button)
                {
                    case SystemMediaTransportControlsButton.Play: PlayPressed?.Invoke(); break;
                    case SystemMediaTransportControlsButton.Pause: PausePressed?.Invoke(); break;
                    case SystemMediaTransportControlsButton.Next: NextPressed?.Invoke(); break;
                    case SystemMediaTransportControlsButton.Previous: PreviousPressed?.Invoke(); break;
                }
            });
            _smtc.PlaybackPositionChangeRequested += (_, e) => ui.BeginInvoke(() => SeekRequested?.Invoke(e.RequestedPlaybackPosition));
        }
        catch
        {
            _smtc = null;
        }
    }

    public async void SetTrack(string? title, string? artist, string? album, string? coverPath)
    {
        if (_smtc == null) return;
        try
        {
            var du = _smtc.DisplayUpdater;
            if (title == null)
            {
                du.ClearAll();
                du.Update();
                _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
                return;
            }
            du.Type = MediaPlaybackType.Music;
            du.MusicProperties.Title = title;
            du.MusicProperties.Artist = artist ?? "";
            du.MusicProperties.AlbumTitle = album ?? "";
            if (coverPath != _coverPath)
            {
                _coverPath = coverPath;
                du.Thumbnail = coverPath != null && File.Exists(coverPath)
                    ? RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(coverPath))
                    : null;
            }
            du.Update();
        }
        catch { }
    }

    public void SetState(bool playing)
    {
        if (_smtc == null) return;
        try { _smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused; } catch { }
    }

    public void SetTimeline(TimeSpan position, TimeSpan duration)
    {
        if (_smtc == null || duration <= TimeSpan.Zero) return;
        try
        {
            _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                EndTime = duration,
                MinSeekTime = TimeSpan.Zero,
                MaxSeekTime = duration,
                Position = position,
            });
        }
        catch { }
    }

    public void Dispose()
    {
        try { if (_smtc != null) _smtc.IsEnabled = false; } catch { }
        _host.Dispose();
    }
}
