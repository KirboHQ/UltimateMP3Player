using UltimateMP3Player.Audio;

namespace UltimateMP3Player.ViewModels;

// Keeps this device's player on another device's song and clock: a room's (TogetherViewModel), the computer this phone
// drives while the music is heard here too (ComputerLinkViewModel). Every few hundred ms: the song opened, started at the
// right point, put back on time when it drifts.
internal sealed class SyncFollower
{
    // Output buffer of the player: what you hear is this much behind what it has read (AudioEngine.Position).
    private static readonly double Lead = AudioEngine.LatencyMs / 1000.0;

    private readonly PlayerViewModel _p;
    private readonly bool _count;
    private bool _loading, _justSeeked;
    private long _lastSeek, _failedUntil;
    private string? _failedItem;
    // Extra delay measured after a jump on this song (slow decoders): seeks aim this much ahead.
    private double _lag;

    // count: the songs heard count here (a room's; not the computer's, which counts them itself).
    public SyncFollower(PlayerViewModel p, bool count)
    {
        _p = p;
        _count = count;
    }

    // A song opening: nothing is played, paused or moved until then.
    public bool Loading => _loading;

    public void Reset() => _loading = false;

    // itemId: the song there (what's loaded is known by it); target: where it is there now (seconds of song).
    public void Follow(string itemId, TrackViewModel vm, string path, double target, bool playing, double speed, Action again)
    {
        if (_loading) return;
        long now = Environment.TickCount64;
        if (_p.RoomLoadedId != itemId)
        {
            // A file that didn't open is tried again after a while, not every 200 ms.
            if (_failedItem == itemId && now < _failedUntil) return;
            _loading = true;
            // The song before stops here (it doesn't go on under the new one); the new one starts at the right point once open.
            _p.RoomPlay(false);
            _ = Load(itemId, vm, path, again);
            return;
        }
        var audio = _p.Audio;
        double lead = (Lead + AudioEngine.DeviceLatencyMs / 1000.0) * speed;
        if (playing)
        {
            if (!audio.IsPlaying)
            {
                audio.Seek(TimeSpan.FromSeconds(target + lead + _lag));
                _p.RoomPlay(true);
                _lastSeek = now;
                _justSeeked = true;
            }
            else if (now - _lastSeek > 1500)
            {
                double behind = target - audio.Position.TotalSeconds;
                if (Math.Abs(behind) > 0.2 * Math.Max(1, speed))
                {
                    // Drifted (a hiccup, a seek by someone): back on time. If the last jump itself landed late (files decoded
                    // through ffmpeg take a moment to restart), the next ones aim that much further.
                    if (_justSeeked) _lag = Math.Clamp(_lag + behind, 0, 1.5);
                    audio.Seek(TimeSpan.FromSeconds(target + lead + _lag));
                    _lastSeek = now;
                    _justSeeked = true;
                }
                else _justSeeked = false;
            }
        }
        else
        {
            if (audio.IsPlaying) _p.RoomPlay(false);
            if (now - _lastSeek > 400 && Math.Abs(audio.Position.TotalSeconds - target) > 0.25)
            {
                audio.Seek(TimeSpan.FromSeconds(target));
                _lastSeek = now;
            }
        }
    }

    private async Task Load(string itemId, TrackViewModel vm, string path, Action again)
    {
        bool ok = false;
        try { ok = await _p.RoomLoad(itemId, vm, path, _count); }
        finally { _loading = false; }
        if (!ok)
        {
            _failedItem = itemId;
            _failedUntil = Environment.TickCount64 + 5000;
        }
        _lastSeek = 0;
        _lag = 0;
        _justSeeked = false;
        again();
    }
}
