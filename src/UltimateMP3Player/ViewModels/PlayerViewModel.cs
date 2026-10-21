using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using UltimateMP3Player.Audio;
using UltimateMP3Player.Core;
using UltimateMP3Player.Core.Link;

namespace UltimateMP3Player.ViewModels;

// A row of "next up": queued by hand or from the list; Radio = a suggested song (not in the library).
public sealed class QueueRow
{
    public QueueRow(int index, TrackViewModel track, bool queued, string? header, PlayerViewModel player, RadioItemViewModel? radio = null)
    {
        Index = index;
        Track = track;
        Queued = queued;
        Header = header;
        Player = player;
        Radio = radio;
    }

    public int Index { get; }
    public TrackViewModel Track { get; }
    public bool Queued { get; }
    public string? Header { get; }
    public PlayerViewModel Player { get; }
    public RadioItemViewModel? Radio { get; }
    public bool IsRadio => Radio != null;
    // A song of the library in the cloud (a suggested one shows its own state instead).
    public bool ShowCloud => Radio == null && Track.IsCloud;

    public ICommand PlayCommand => new RelayCommand(() => _ = Player.JumpTo(Index));
    public ICommand RemoveCommand => new RelayCommand(() => Player.RemoveUpcoming(Index));

    public override string ToString() => $"{Track.Title} – {Track.Artist}";
}

// A button of the speed panel (0,5 · 0,75 · 1 · 1,25 · 1,5 · 2); the one playing is lit.
public sealed class SpeedPreset : Observable
{
    public SpeedPreset(double value, PlayerViewModel player)
    {
        Value = value;
        Label = value.ToString("0.##", L.Culture);
        Command = new RelayCommand(() => player.Speed = value, () => player.CanSetSpeed);
    }

    public double Value { get; }
    public string Label { get; }
    public ICommand Command { get; }

    private bool _isCurrent;
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }
}

public sealed class PlayerViewModel : Observable
{
    private const double TargetLufs = -14;

    private readonly MainViewModel _main;
    private readonly AudioEngine _audio;
    private readonly PlayQueue _queue = new();
    private readonly DispatcherTimer _timer;
    private bool _opened;
    private bool _visible = true;
    private int _loadVersion;
    // Where to start the restored song on first play.
    private double _resumeAt;

    public PlayerViewModel(MainViewModel main, AudioEngine audio, bool adopt = false)
    {
        _main = main;
        _audio = audio;
        var d = main.Profile.Data;
        _queue.Shuffle = d.Shuffle;
        _queue.Repeat = d.Repeat;
        _queue.AutoFill = d.AutoQueue;
        // A song alone in its list (e.g. played from a card): the queue comes from the whole library.
        _queue.Fallback = () => (_main.Library.Snapshot().OrderByDescending(t => t.Added).Select(t => t.Id).ToList(), "library", "All songs");
        _volume = d.Volume;
        _muted = d.Muted;
        ApplyVolume();
        ApplyEqualizer();
        ApplyCrossfade();
        ApplySpeed();
        _speedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _speedTimer.Tick += (_, _) => SendRoomSpeed();

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick();
        _listenTimer.Tick += (_, _) => CountListening();
        _audio.Ended += OnEnded;
        _audio.NearEnd += OnNearEnd;
        _audio.Failed += OnFailed;

        // In a room of "Listen together" these buttons need the host's permission: without it they are greyed out.
        PlayPauseCommand = new RelayCommand(PlayPause, () => _room == null || _room.CanPause);
        NextCommand = new RelayCommand(() => _ = Next(false), () => _room == null || _room.CanSkip);
        PreviousCommand = new RelayCommand(() => _ = Previous(), () => _room == null || _room.CanPause);
        // In a room: loop of the song playing, for everyone (with the pause permission).
        CycleRepeatCommand = new RelayCommand(() => { if (_room != null) _room.ToggleLoop(); else CycleRepeat(); }, () => _room == null || _room.CanPause);
        ToggleMuteCommand = new RelayCommand(() => Muted = !Muted);
        // (a song of the connected computer isn't one of this phone's: no heart for it)
        FavoriteCommand = new RelayCommand(() => { if (Current != null) _main.ToggleFavorite(Current); }, () => Current != null);
        SeekCommand = new RelayCommand(p => { if (p is double f) SeekFraction(f); }, _ => _room == null || _room.CanPause);
        SpeedStepCommand = new RelayCommand(p => { if (p is string s && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) Speed += d; },
            _ => CanSetSpeed);
        SetSpeedCommand = new RelayCommand(p => { if (p is string s && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) Speed = d; },
            _ => CanSetSpeed);
        ClearQueueCommand = new RelayCommand(ClearQueue, () => _remote != null ? _remote.State.Upcoming > 0 : _room == null && _queue.UpcomingCount > 0);
        GenerateQueueCommand = new RelayCommand(GenerateQueue, () => _room == null && Current != null);
        SaveCurrentCommand = new RelayCommand(() => SaveCurrent(true), () => IsTemporary);

        Restore(adopt);
        _queue.Changed += () =>
        {
            if (UpNextVisible) RefreshUpNext();
            SaveState();
            _main.Radio.Refresh();
            ApplyBend();
        };
    }

    // A song of the library, or one not in it still in the queue or among the recently played.
    // (connected to a phone: also one of the phone's songs, SharedLibrary)
    private Track? TrackOf(string id) => _main.Library.Get(id) ?? _main.Radio.Track(id) ?? _main.Shared?.Get(id);
    private bool Exists(string id) => TrackOf(id) != null;

    // Among the recently played: a song of the library, and one heard without saving it (suggested, from a link, or a
    // room's own, kept from now on like those). Connected to a phone, a song of the phone's (or one both have) counts
    // there too, or only there when only the phone's music is shown.
    // The id it's remembered by (null: one that can't be found again).
    private string? AddHistory(Track t)
    {
        if (_main.Shared is { } shared)
        {
            var (here, there) = shared.Counters(t.Id);
            if (there != null) shared.Heard(there, 0, false, true);
            if (!here) return t.Id;
        }
        var id = _main.Library.Get(t.Id) != null || _main.Radio.Has(t.Id) ? t.Id : _main.Radio.KeepHeard(t);
        if (id == null) return null;
        _main.Profile.AddHistory(id);
        _main.Shared?.OwnHistory();
        return id;
    }

    // Brings back the song, the list and "next up" of the last session.
    private void Restore(bool adopt)
    {
        var d = _main.Profile.Data;
        var lib = _main.Library;
        if (d.Queue is { } q && q.Current != null && Exists(q.Current))
            _queue.Restore(q, Exists);
        else if (d.LastTrack != null && lib.Get(d.LastTrack) is { } last)
        {
            // No saved queue yet: continue with the library.
            var ids = lib.Snapshot().OrderByDescending(t => t.Added).Select(t => t.Id).ToList();
            _queue.Play(ids, ids.IndexOf(last.Id), "library", "All songs");
        }
        if (_queue.Current == null || TrackOf(_queue.Current) is not { } track) return;
        Current = _main.Vm(track);
        Duration = track.Duration;
        if (adopt && _audio.HasSource && _audio.SourcePath is { } open && (open == track.Path || open == track.CachePath || open == _main.Radio.PathFor(track.Id)))
        {
            // The window was rebuilt: the engine is still playing this song.
            _opened = true;
            Duration = _audio.Duration.TotalSeconds > 0 ? _audio.Duration.TotalSeconds : track.Duration;
            Position = _audio.Position.TotalSeconds;
            IsPlaying = _audio.IsPlaying;
            return;
        }
        _resumeAt = d.Queue?.Current == track.Id ? Math.Clamp(d.Queue.Position, 0, Math.Max(0, track.Duration - 5)) : 0;
        Position = _resumeAt;
    }

    public void SaveState()
    {
        // In a room (or driving a computer) the saved song is still your own one, from before.
        if (_room != null || _remote != null) return;
        double pos = 0;
        if (Current != null && Current.Id == _queue.Current) pos = _opened ? _audio.Position.TotalSeconds : _resumeAt;
        _main.Profile.Data.Queue = _queue.Save(pos);
        _main.Radio.Save();
        SaveListening();
        _main.Profile.Save();
    }

    public void Detach()
    {
        _timer.Stop();
        _speedTimer.Stop();
        CountListening();
        _listenTimer.Stop();
        SaveListening();
        _audio.Ended -= OnEnded;
        _audio.NearEnd -= OnNearEnd;
        _audio.Failed -= OnFailed;
    }

    private void OnFailed(string msg) => _main.Toast(msg);

    public ICommand PlayPauseCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand CycleRepeatCommand { get; }
    public ICommand ToggleMuteCommand { get; }
    public ICommand FavoriteCommand { get; }
    public ICommand SeekCommand { get; }
    public ICommand ClearQueueCommand { get; }
    public ICommand GenerateQueueCommand { get; }
    public ICommand SpeedStepCommand { get; }
    public ICommand SetSpeedCommand { get; }
    // The song playing, only in the cache, into the library (no playlist).
    public ICommand SaveCurrentCommand { get; }

    // ------------------------------------------------------------------ speed

    // Playback speed like YouTube's: 0.5-2 in steps of 0.05, the key kept or (SpeedPitch) following it like a record.
    // Your own is saved in the profile; in a room the room's one plays (changed by who has the permission) and yours
    // comes back when you leave.
    private readonly DispatcherTimer _speedTimer;
    private double? _roomSpeedDraft;
    private bool? _roomPitchDraft;
    private long _roomSpeedSentAt;

    public static string SpeedLabel(double s) => (Math.Abs(s - 1) < 0.001 ? "1" : s.ToString("0.##", L.Culture)) + "×";

    public double Speed
    {
        get => _remote != null ? _remote.State.Speed : _room != null ? _roomSpeedDraft ?? _room.RoomSpeed : _main.Profile.Data.Speed;
        set
        {
            value = Math.Round(Math.Clamp(value, 0.5, 2) * 20) / 20;
            if (Math.Abs(value - Speed) < 0.001) return;
            if (_remote != null) _remote.Do("speed", pos: value, flag: SpeedPitch);
            else if (_room != null)
            {
                if (!_room.CanSpeed) return;
                _roomSpeedDraft = value;
                _speedTimer.Start();
            }
            else
            {
                _main.Profile.Data.Speed = value;
                _main.Profile.Save();
                ApplySpeed();
            }
            OnSpeedChanged();
        }
    }

    public bool SpeedPitch
    {
        get => _remote != null ? _remote.State.Pitch : _room != null ? _roomPitchDraft ?? _room.RoomPitch : _main.Profile.Data.SpeedPitch;
        set
        {
            if (value == SpeedPitch) return;
            if (_remote != null) _remote.Do("speed", pos: Speed, flag: value);
            else if (_room != null)
            {
                if (!_room.CanSpeed) return;
                _roomPitchDraft = value;
                _speedTimer.Start();
            }
            else
            {
                _main.Profile.Data.SpeedPitch = value;
                _main.Profile.Save();
                ApplySpeed();
            }
            OnSpeedChanged();
        }
    }

    public bool CanSetSpeed => _room == null || _room.CanSpeed;
    public bool IsSpeedChanged => Math.Abs(Speed - 1) > 0.001;
    public string SpeedText => SpeedLabel(Speed);
    // Big number in the speed panel ("1,25×").
    public string SpeedValueText => Speed.ToString("0.00", L.Culture) + "×";
    public string SpeedTip => _room == null ? L.T("Playback speed (keys < and >)")
        : _room.CanSpeed ? L.T("Playback speed for everyone in the room (keys < and >)")
        : L.T("Only people with permission can change the speed (the host decides)");
    public string SpeedHint => _room == null ? L.T("It applies to every song until you change it.")
        : _room.CanSpeed ? L.T("Changes the speed for everyone in the room.")
        : L.T("Only people with permission can change the speed (the host decides).");

    private List<SpeedPreset>? _presets;
    public List<SpeedPreset> SpeedPresets
    {
        get
        {
            if (_presets == null)
            {
                _presets = new[] { 0.5, 0.75, 1, 1.25, 1.5, 2 }.Select(v => new SpeedPreset(v, this)).ToList();
                MarkPreset();
            }
            return _presets;
        }
    }

    private void MarkPreset()
    {
        if (_presets == null) return;
        double s = Speed;
        foreach (var p in _presets) p.IsCurrent = Math.Abs(p.Value - s) < 0.001;
    }

    private void OnSpeedChanged()
    {
        MarkPreset();
        OnChanged(nameof(Speed), nameof(SpeedPitch), nameof(IsSpeedChanged), nameof(SpeedText), nameof(SpeedValueText));
    }

    private void ApplySpeed()
    {
        if (_room != null) return;
        _audio.SetSpeed(_main.Profile.Data.Speed, _main.Profile.Data.SpeedPitch);
        _main.Host.OnSeek();
    }

    // Changes in a room go out a few times a second while the slider is dragged; the last one always goes.
    private void SendRoomSpeed()
    {
        _speedTimer.Stop();
        if (_room == null || (_roomSpeedDraft == null && _roomPitchDraft == null)) return;
        _roomSpeedSentAt = Environment.TickCount64;
        _room.SetSpeed(_roomSpeedDraft ?? _room.RoomSpeed, _roomPitchDraft ?? _room.RoomPitch);
    }

    // The room's speed, from TogetherViewModel.Follow.
    internal void RoomSpeed(double speed, bool pitch)
    {
        if (Math.Abs(_audio.Speed - speed) > 0.001 || _roomPitchApplied != pitch)
        {
            _roomPitchApplied = pitch;
            _audio.SetSpeed(speed, pitch);
            _main.Host.OnSeek();
        }
    }

    private bool _roomPitchApplied;

    // The room's state changed (loop, speed): the buttons and the panel follow; an answer to our own change ends its draft.
    public void OnRoomPlayback()
    {
        if (_room != null && !_speedTimer.IsEnabled && Environment.TickCount64 - _roomSpeedSentAt > 400)
        {
            _roomSpeedDraft = null;
            _roomPitchDraft = null;
        }
        OnSpeedChanged();
        OnChanged(nameof(RepeatOn), nameof(RepeatGlyph), nameof(RepeatTip));
    }

    // ------------------------------------------------------------------ state

    private TrackViewModel? _current;
    public TrackViewModel? Current
    {
        get => _current;
        private set
        {
            var old = _current;
            if (!Set(ref _current, value)) return;
            if (old != null) old.IsCurrent = false;
            if (value != null) value.IsCurrent = true;
            OnChanged(nameof(HasTrack), nameof(ShowNowPlaying), nameof(CanGenerate), nameof(ContextName), nameof(IsTemporary));
        }
    }

    public bool HasTrack => Current != null;
    // The song playing isn't saved on the device: a suggested one, one of a link heard without saving it, or a song of the
    // library in the cloud (the "Save" button next to the heart).
    // (connected to the other device: also a song not saved on both, the save button asks where)
    public bool IsTemporary => Current != null && _room == null &&
                               (_main.Radio.Has(Current.Id) || !Current.IsSaved || _main.Shared is { Ready: true } s && s.NotEverywhere(Current.T));

    // The song playing was saved, or its audio removed from the device.
    public void RefreshSaved()
    {
        RefreshTemporary();
        _main.Host.OnTrackChanged();
    }

    // The connected device's music changed (where the song is saved).
    internal void RefreshTemporary()
    {
        OnChanged(nameof(IsTemporary));
        CommandManager.InvalidateRequerySuggested();
    }
    // The song block of the player bar: also in a room with nothing playing (it shows the room's name).
    public bool ShowNowPlaying => Current != null || _room != null || _remote != null || _main.PhoneLinked;
    // "Generate queue" (the song page): your own queue only.
    public bool CanGenerate => Current != null && _room == null;
    public string? ContextId => _queue.ContextId;

    // Name of the list being played, in the current language.
    public string? ContextName => _room != null ? _room.RoomName : _remote != null ? _remote.State.Context : NameOf(_queue.ContextId, _queue.ContextName);

    private string? NameOf(string? contextId, string? name) => contextId switch
    {
        "library" => L.T("All songs"),
        "unsorted" => L.T("Not in a playlist"),
        { } id when id.StartsWith("playlist:") && (_main.Profile.GetPlaylist(id[9..]) ?? _main.Shared?.Mirror?.GetPlaylist(id[9..])) is { } p => PlaylistViewModel.DisplayName(p),
        { } id when id.StartsWith("radio:") => L.F("Songs like “{0}”", name),
        { } id when id.StartsWith("artist:") => L.F("Songs by {0}", name),
        _ => name,
    };

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (!Set(ref _isPlaying, value)) return;
            OnChanged(nameof(PlayGlyph), nameof(Animate));
            Current?.RefreshPlaying();
            UpdateTimer();
            UpdateListening();
            _main.Host.OnPlaybackChanged();
        }
    }

    // A suggested song still downloading that will start by itself shows pause, like a song playing.
    public string PlayGlyph => IsPlaying || _waiting != null && _waitPlay ? "" : "";

    private double _position;
    public double Position
    {
        get => _position;
        private set
        {
            if (Math.Abs(_position - value) < 0.05) return;
            _position = value;
            OnChanged(nameof(Position), nameof(PositionText), nameof(Progress));
        }
    }

    private double _duration;
    public double Duration
    {
        get => _duration;
        private set { if (Set(ref _duration, value)) OnChanged(nameof(DurationText), nameof(Progress)); }
    }

    public double Progress => Duration > 0 ? Math.Clamp(Position / Duration, 0, 1) : 0;
    public string PositionText => Text.Duration(Position) is { Length: > 0 } s ? s : "0:00";
    public string DurationText => Text.Duration(Duration) is { Length: > 0 } s ? s : "0:00";

    private double _volume;
    public double Volume
    {
        get => _remote?.State.Volume ?? _volume;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (_remote != null)
            {
                // The computer's volume.
                if (Math.Abs(value - Volume) < 0.001) return;
                _remote.Do("volume", pos: value);
                OnChanged(nameof(Volume), nameof(Muted), nameof(VolumeGlyph));
                return;
            }
            if (!Set(ref _volume, value)) return;
            if (value > 0 && Muted) Muted = false;
            _main.Profile.Data.Volume = value;
            _main.Profile.Save();
            ApplyVolume();
            OnChanged(nameof(VolumeGlyph));
        }
    }

    private bool _muted;
    public bool Muted
    {
        get => _remote?.State.Muted ?? _muted;
        set
        {
            if (_remote != null)
            {
                if (value == Muted) return;
                _remote.Do("mute", flag: value);
                OnChanged(nameof(Muted), nameof(VolumeGlyph));
                return;
            }
            if (!Set(ref _muted, value)) return;
            _main.Profile.Data.Muted = value;
            _main.Profile.Save();
            ApplyVolume();
            OnChanged(nameof(VolumeGlyph));
        }
    }

    public string VolumeGlyph => Muted || Volume <= 0 ? "" : Volume < 0.34 ? "" : Volume < 0.67 ? "" : "";

    // (driving a computer: its volume, for the music heard here too; LinkSilent: the music is heard on the phone only)
    private void ApplyVolume() => _audio.Volume = Muted || _silenced || _linkSilent ? 0 : (float)Math.Pow(Volume, 2);

    // Phones in a room: headphones pulled out, a call, another app's music silence only this phone (the room goes on for
    // the others) until the play button, in the app or on the lock screen. Not kept like the mute (phones have no mute button).
    private bool _silenced;
    public bool Silenced => _silenced;

    // Connect phone with the music heard on the phone only: this computer's player goes on (it keeps the time and the
    // queue), silent.
    private bool _linkSilent;
    internal bool LinkSilent
    {
        set
        {
            if (_linkSilent == value) return;
            _linkSilent = value;
            ApplyVolume();
        }
    }

    public void SilenceInRoom()
    {
        if (_room == null || _silenced) return;
        _silenced = true;
        ApplyVolume();
        _main.Toast(L.T("Silenced on this phone while the room goes on: press play to hear it again."));
    }

    // The play button while silenced: the sound comes back; true = that was all (the room was playing).
    private bool Unsilence()
    {
        if (!_silenced) return false;
        _silenced = false;
        ApplyVolume();
        _main.Host.OnPlaybackChanged();
        return IsPlaying;
    }

    public bool Shuffle
    {
        get => _remote?.State.Shuffle ?? _queue.Shuffle;
        set
        {
            if (_remote != null)
            {
                if (value == Shuffle) return;
                _remote.Do("shuffle", flag: value);
                OnChanged(nameof(Shuffle), nameof(GenerateHint));
                return;
            }
            if (_queue.Shuffle == value || _room != null) return;
            _queue.Shuffle = value;
            _main.Profile.Data.Shuffle = value;
            _main.Profile.Save();
            OnChanged(nameof(Shuffle), nameof(GenerateHint));
        }
    }

    public RepeatMode Repeat
    {
        get => _remote?.State.Repeat ?? _queue.Repeat;
        internal set
        {
            if (_remote != null)
            {
                _remote.Do("repeat", index: (int)value);
                OnChanged(nameof(Repeat), nameof(RepeatOn), nameof(RepeatGlyph), nameof(RepeatTip), nameof(QueueEndHint));
                return;
            }
            _queue.Repeat = value;
            _main.Profile.Data.Repeat = value;
            _main.Profile.Save();
            OnChanged(nameof(Repeat), nameof(RepeatOn), nameof(RepeatGlyph), nameof(RepeatTip), nameof(QueueEndHint));
            ApplyBend();
        }
    }

    // In a room the button is the loop of the song playing (the room has no "repeat the queue").
    public bool RepeatOn => _room != null ? _room.RoomLoop : Repeat != RepeatMode.Off;
    public string RepeatGlyph => _room != null || Repeat == RepeatMode.One ? "" : "";
    // "Repeat one" loops the song until it's turned off, as in the other players (skipping still moves on).
    public string RepeatTip => _room != null
        ? !_room.CanPause ? L.T("Only people allowed to pause can loop the song (the host decides)")
        : _room.RoomLoop ? L.T("The song repeats for everyone (click to stop the loop)")
        : L.T("Repeat this song for everyone in the room")
        : L.T(Repeat switch
        {
            RepeatMode.All => "Repeat playlist (click to repeat song)",
            RepeatMode.One => "Repeat this song forever (click to turn off)",
            _ => "Repeat: off",
        });

    private void CycleRepeat() => Repeat = Repeat switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off,
    };

    public void ApplyEqualizer()
    {
        var eq = _main.Profile.Data.Eq;
        _audio.SetEqualizer(eq.Enabled, eq.Gains);
    }

    public void ApplyNormalization()
    {
        if (Current != null) _audio.SetTrackGain(TrackGain(Current.T));
    }

    public void ApplyCrossfade()
    {
        var d = _main.Profile.Data;
        // In a room songs change when the host says so, never early.
        _audio.CrossfadeSeconds = d.Crossfade && _room == null ? Math.Clamp(d.CrossfadeSeconds, 1, 12) : 0;
        ApplyBend();
    }

    // ------------------------------------------------------------------ the transition at the next song's tempo

    // The song playing glides to the next one's BPM in its last seconds (Settings), the key unchanged, when both BPMs are
    // known and close enough (half or double time too); it keeps that tempo while it fades out. Set again whenever the song
    // or what comes after it changes.
    private const double MaxBend = 0.15;

    public void ApplyBend()
    {
        var d = _main.Profile.Data;
        double ratio = 1;
        var coming = _queue.PeekNext() is { } next ? TrackOf(next) : null;
        if (d.BpmMatch && _room == null && Repeat != RepeatMode.One)
        {
            // (BPMs not known yet are detected, as the DJ does: the bend is set again once they are)
            DetectBpm(Current?.T);
            DetectBpm(coming);
            if (Current?.T.Bpm is double from and > 0 && coming?.Bpm is double to and > 0) ratio = BendRatio(from, to);
        }
        _audio.SetBend(ratio, Math.Clamp(d.BpmMatchSeconds, 2, 30));
    }

    // Once per song and session (one that can't be read isn't tried again at every change).
    private readonly HashSet<string> _bpmAsked = new();

    private async void DetectBpm(Track? t)
    {
        if (t == null || t.Bpm is > 0 || t.AudioPath is not { } path || !_bpmAsked.Add(t.Id)) return;
        DjAnalysis? a = null;
        try { a = await Task.Run(() => DjAnalyzer.AnalyzeAsync(path)); }
        catch { }
        if (a?.Bpm is not double bpm || t.Bpm is > 0) return;
        t.Bpm = Math.Round(bpm, 1);
        t.BeatOffset ??= a.FirstBeat;
        if (_main.Library.Get(t.Id) == t) _main.Library.Changed(t);
        ApplyBend();
    }

    // The tempo change that brings one BPM to the other: the nearest of the same, half and double time; 1 when it's too far.
    internal static double BendRatio(double from, double to)
    {
        var r = new[] { to / from, to * 2 / from, to / (2 * from) }.OrderBy(x => Math.Abs(Math.Log(x))).First();
        return Math.Abs(r - 1) < 0.005 || Math.Abs(r - 1) > MaxBend ? 1 : r;
    }

    private double TrackGain(Track t)
    {
        if (!_main.Profile.Data.Normalize || t.Loudness is not double lufs) return 0;
        double gain = TargetLufs - lufs;
        if (gain > 0 && t.Peak is double peak) gain = Math.Min(gain, Math.Max(0, -1 - peak) + 3);
        return Math.Clamp(gain, -15, 8);
    }

    // ------------------------------------------------------------------ playback

    // The rest of the list follows unless "automatic queue" is off (alwaysQueue: it follows anyway, e.g. several selected songs).
    // The song already playing doesn't start over: from its own list (or a search) it opens the player, from another list
    // it goes on and what follows becomes that list.
    public void PlayFrom(ITrackList list, TrackViewModel track, bool alwaysQueue = false)
    {
        // In a room nothing plays on its own: the song goes into the room's queue.
        if (_room != null)
        {
            _room.Add(new[] { track });
            return;
        }
        // Driving a computer: the list plays there.
        if (_remote != null && list.ContextId != "search")
        {
            if (Current?.Id == track.Id)
            {
                Play();
                _main.Navigate(_main.NowPlaying);
                return;
            }
            var songs = list.PlayOrder.Select(t => t.T).ToList();
            int at = songs.FindIndex(t => t.Id == track.Id);
            if (at < 0) PlaySingle(track.T);
            else _remote.PlayList(songs, at, NameOf(list.ContextId, list.ContextName), alwaysQueue);
            return;
        }
        bool playing = Current?.Id == track.Id && (_opened || _waiting == track.Id);
        if (playing && (list.ContextId == "search" || list.ContextId == _queue.ContextId))
        {
            Play();
            _main.Navigate(_main.NowPlaying);
            return;
        }
        // A search only finds the song: what follows comes from all the songs, as in the other players.
        if (list.ContextId == "search")
        {
            _main.PlayInLibrary(track);
            return;
        }
        var ids = list.PlayOrder.Select(t => t.Id).ToList();
        int index = ids.IndexOf(track.Id);
        if (index < 0)
        {
            PlaySingle(track.T);
            return;
        }
        _queue.Play(ids, index, list.ContextId, list.ContextName, alwaysQueue || AutoQueue);
        if (!playing)
        {
            _ = Load(track.T, true);
            return;
        }
        OnChanged(nameof(ContextName));
        _main.OnContextChanged();
        Play();
    }

    public void PlayAll(ITrackList list, bool shuffle)
    {
        if (_room != null)
        {
            _room.AddAll(list.PlayOrder, list.ContextName, shuffle);
            return;
        }
        if (_remote != null)
        {
            _remote.PlayList(list.PlayOrder.Select(t => t.T), -1, NameOf(list.ContextId, list.ContextName), shuffle);
            return;
        }
        var ids = list.PlayOrder.Select(t => t.Id).ToList();
        if (ids.Count == 0) return;
        if (shuffle)
        {
            Shuffle = true;
            _queue.PlayShuffled(ids, list.ContextId, list.ContextName);
        }
        else _queue.Play(ids, 0, list.ContextId, list.ContextName);
        if (_queue.Current is { } id && TrackOf(id) is { } t) _ = Load(t, true);
    }

    // A song on its own (from a menu, a file opened): after it the suggested songs, or all your songs (Settings).
    public void PlaySingle(Track t)
    {
        if (_room != null)
        {
            _room.Add(new[] { _main.Vm(t) });
            return;
        }
        if (_remote != null)
        {
            _remote.PlaySingle(t);
            return;
        }
        if (RadioAfterSingle)
        {
            PlayRadio(_main.Vm(t), false);
            return;
        }
        _queue.Play(new[] { t.Id }, 0, null, null);
        _ = Load(t, true);
    }

    // A song played outside a list is followed by songs like it found online (Settings), with the automatic queue on.
    public bool RadioAfterSingle => _main.Host.Settings.RadioAfterSingle && AutoQueue;

    // The song now (or on, if it's the one playing) and after it songs like it found online, more as they're heard.
    public void PlayRadio(TrackViewModel seed, bool announce = true)
    {
        if (_room != null) return;
        if (_remote != null)
        {
            _remote.PlayRadio(seed.T, false);
            if (announce) _main.Toast(L.F("Looking for songs like “{0}”…", seed.Title));
            return;
        }
        var t = seed.T;
        var contextId = "radio:" + t.Id;
        bool playing = Current?.Id == t.Id && (_opened || _waiting == t.Id);
        _main.Radio.Begin(t, contextId);
        _queue.Play(new[] { t.Id }, 0, contextId, t.Title, radio: true);
        if (!playing) _ = Load(t, true);
        if (announce) _main.Toast(L.F("Looking for songs like “{0}”…", t.Title));
    }

    // The song now (or on, if it's the one playing) and after it the songs of its artist found online, the most listened
    // first, more as they're heard (like the similar songs, the same cache).
    public void PlayArtist(TrackViewModel seed)
    {
        if (_room != null) return;
        if (_remote != null)
        {
            _remote.PlayRadio(seed.T, true);
            return;
        }
        var t = seed.T;
        var artist = t.Artist?.Split(',')[0].Trim();
        if (string.IsNullOrEmpty(artist))
        {
            _main.Toast(L.T("This song has no artist to look for."));
            return;
        }
        var contextId = "artist:" + t.Id;
        bool playing = Current?.Id == t.Id && (_opened || _waiting == t.Id);
        _main.Radio.Begin(t, contextId);
        _queue.Play(new[] { t.Id }, 0, contextId, artist, radio: true);
        if (!playing) _ = Load(t, true);
        _main.Toast(L.F("Looking for songs by {0}…", artist));
    }

    // The songs of a link heard without downloading them into the library (RadioViewModel.AddFromLink): each comes into
    // the cache just before its turn, like the suggested songs, and "Save" keeps one. A single song is followed by songs
    // like it (Settings), a playlist plays as a list of its own.
    public void PlayStream(IReadOnlyList<string> ids, string name)
    {
        if (_room != null || ids.Count == 0 || TrackOf(ids[0]) is not { } first) return;
        if (_remote != null)
        {
            _remote.PlayList(ids.Select(TrackOf).OfType<Track>(), 0, name, true);
            return;
        }
        if (ids.Count == 1)
        {
            if (RadioAfterSingle)
            {
                PlayRadio(_main.Vm(first), false);
                return;
            }
            _queue.Play(ids.ToList(), 0, null, null);
        }
        else _queue.Play(ids.ToList(), 0, "link:" + Ids.New(), name, true);
        _ = Load(first, true);
    }

    // The button next to the heart: a song not in the library (suggested, heard from a link) goes into it first, in the
    // cloud (the library icon: nothing downloaded); one of the library in the cloud is saved on the device (the download arrow).
    // (Connect phone: MainViewModel asks on which device, except from the lock screen: ask false, this one)
    private void SaveCurrent(bool ask)
    {
        if (Current == null) return;
        if (!Current.InLibrary) _main.AddToLibrary(Current, ask);
        else _ = _main.SaveToDevice(new[] { Current }, quiet: !ask);
    }

    // The heart and the save button of the lock screen and the notification, where nothing can be asked.
    public void KeepFromSystem(bool favorite)
    {
        if (Current == null) return;
        if (favorite) _main.ToggleFavorite(Current, ask: false);
        else if (IsTemporary) SaveCurrent(false);
    }

    public void Enqueue(TrackViewModel t, bool quiet = false)
    {
        if (_room != null)
        {
            _room.Add(new[] { t });
            return;
        }
        if (_remote != null) _remote.Enqueue(t.T, false);
        else _queue.Enqueue(t.Id);
        if (!quiet) _main.Toast(L.F("“{0}” added to queue", t.Title));
    }

    public void PlayNext(TrackViewModel t)
    {
        if (_room != null)
        {
            _room.Add(new[] { t }, next: true);
            return;
        }
        if (_remote != null) _remote.Enqueue(t.T, true);
        else _queue.PlayNextInQueue(t.Id);
        _main.Toast(L.F("“{0}” will play next", t.Title));
    }

    // Nothing after this song: it plays alone (Generate brings the list back).
    private void ClearQueue()
    {
        if (_remote != null) _remote.Do("clear");
        else _queue.ClearAll();
        _main.Toast(L.T("Queue cleared"));
    }

    // Full queue: generated again from scratch; otherwise the missing songs of the list go at the end.
    // A song played on its own takes the library.
    private void GenerateQueue()
    {
        if (Current == null) return;
        if (_remote != null)
        {
            _remote.Do("generate");
            return;
        }
        // Suggested songs: the ones already found come back (an emptied queue), and more of them, from the last one.
        if (_queue.Radio)
        {
            _queue.Generate();
            _main.Toast(_main.Radio.More() ? L.T("Looking for more similar songs…") : L.T("Already looking for more similar songs…"));
            return;
        }
        var (added, regenerated) = _queue.Generate();
        _main.Toast(added == 0 ? L.T("There are no other songs to queue.")
            : regenerated ? L.T("Queue generated again from scratch")
            : L.Count(added, "1 song added to the end of the queue", "{0} songs added to the end of the queue"));
    }

    // Automatic queue (button next to the song): the rest of the list comes with a song, and at every new song
    // the missing ones go back at the end, so the queue stays full.
    public bool AutoQueue
    {
        get => _remote?.State.AutoQueue ?? _main.Profile.Data.AutoQueue;
        set
        {
            if (_remote != null)
            {
                if (value == AutoQueue) return;
                _remote.Do("auto", flag: value);
                OnChanged(nameof(AutoQueue), nameof(AutoQueueTip), nameof(QueueEndHint));
                _main.Toast(value ? L.T("Automatic queue on: it refills itself at every song") : L.T("Automatic queue off: when the queue ends, the music stops"));
                return;
            }
            if (_main.Profile.Data.AutoQueue == value || _room != null) return;
            _main.Profile.Data.AutoQueue = value;
            _main.Profile.Save();
            _queue.AutoFill = value;
            if (value) _queue.TopUp();
            OnChanged(nameof(AutoQueue), nameof(AutoQueueTip), nameof(QueueEndHint));
            _main.Toast(value ? L.T("Automatic queue on: it refills itself at every song") : L.T("Automatic queue off: when the queue ends, the music stops"));
        }
    }

    public string AutoQueueTip => AutoQueue
        ? L.T("Automatic queue on: up next refills at every song (click to turn it off)")
        : L.T("Automatic queue off: clicking a song plays just that one (click to turn it on)");

    private async Task Load(Track t, bool play, bool crossfade = false)
    {
        // A saved song whose file is gone, with a link: it goes back to the cloud (heard from its link) instead of failing.
        if (t.IsSaved && !t.HasSavedFile && t.HasLink && _main.Library.Get(t.Id) == t)
        {
            t.Path = "";
            t.VideoPath = t.HasVideo && File.Exists(t.VideoPath) ? t.VideoPath : null;
            _main.Library.Changed(t);
            _main.Toast(L.F("The file of “{0}” is gone: you listen to it from its link, and you can save it again", t.Title));
        }
        var path = t.AudioPath ?? _main.Radio.PathFor(t.Id);
        if (path == null && _main.Radio.Streams(t))
        {
            Wait(t, play);
            return;
        }
        path ??= t.Path;
        int version = ++_loadVersion;
        double resume = Current?.Id == t.Id ? _resumeAt : 0;
        _resumeAt = 0;
        CountListening();
        NewListen();
        bool wasWaiting = IsWaiting;
        _waiting = null;
        _waitMore = false;
        if (wasWaiting) OnWaitChanged();
        SetCurrent(_main.Vm(t));
        _opened = false;
        try
        {
            if (crossfade) await _audio.CrossfadeToAsync(path, t.Duration, TrackGain(t));
            else await _audio.OpenAsync(path, t.Duration, TrackGain(t));
            if (version != _loadVersion) return;
            _opened = true;
            Duration = _audio.Duration.TotalSeconds > 0 ? _audio.Duration.TotalSeconds : t.Duration;
            ApplyBend();
            if (resume > 1 && resume < Duration - 1)
            {
                _audio.Seek(TimeSpan.FromSeconds(resume));
                Position = resume;
            }
            if (play)
            {
                if (!_audio.IsPlaying) _audio.Play();
                IsPlaying = _audio.IsPlaying;
                AddHistory(t);
                _main.OnPlayed();
            }
            else
            {
                _audio.Pause();
                IsPlaying = false;
            }
        }
        catch (Exception ex)
        {
            if (version != _loadVersion) return;
            _audio.Pause();
            IsPlaying = false;
            _main.Toast(ex is FileNotFoundException
                ? L.F("The file for “{0}” no longer exists: it was moved or deleted.", t.Title)
                : L.F("Couldn't play “{0}”: {1}", t.Title, ex.Message));
        }
        _main.Host.OnTrackChanged();
    }

    private void SetCurrent(TrackViewModel vm)
    {
        Current = vm;
        Position = 0;
        Duration = vm.T.Duration;
        _main.Profile.Data.LastTrack = vm.Id;
        _main.Profile.Save();
        _main.OnCurrentChanged();
        if (UpNextVisible) RefreshUpNext();
    }

    // ------------------------------------------------------------------ suggested songs

    // A suggested song still downloading is the song playing, silent: it starts by itself once it's here (unless paused
    // meanwhile). At the end of the queue the next suggestions may still be on their way: the same, until they come.
    private string? _waiting;
    private bool _waitPlay, _waitMore;

    public bool IsWaiting => _remote != null ? _remote.State.Waiting || _remote.State.WaitMore : _waiting != null || _waitMore;
    public string? WaitText => _remote != null ? RemoteWaitText
        : _waitMore ? L.T("Looking for more similar songs…")
        : _waiting is not { } id ? null
        : _main.Radio.StatusOf(id) is { State: Core.Together.FileState.Downloading } st ? L.F("Downloading · {0:0}%", st.Pct)
        : L.T("On its way…");

    private void OnWaitChanged() => OnChanged(nameof(IsWaiting), nameof(WaitText), nameof(PlayGlyph));

    private void Wait(Track t, bool play)
    {
        _loadVersion++;
        CountListening();
        NewListen();
        double resume = Current?.Id == t.Id ? _resumeAt : 0;
        _audio.Pause();
        _audio.Close();
        _opened = false;
        SetCurrent(_main.Vm(t));
        _resumeAt = resume;
        Position = resume;
        _waiting = t.Id;
        _waitPlay = play;
        _waitMore = false;
        IsPlaying = false;
        OnWaitChanged();
        _main.Radio.Prepare(t.Id);
        _main.Host.OnTrackChanged();
    }

    internal void OnRadioReady(string id)
    {
        if (_waiting == id && TrackOf(id) is { } t) _ = Load(t, _waitPlay);
    }

    internal void OnRadioProgress(string id)
    {
        if (_waiting == id) OnWaitChanged();
    }

    // The song waited for can't be downloaded: on to the next one.
    internal void OnRadioFailed(string id)
    {
        if (_waiting != id) return;
        _waiting = null;
        OnWaitChanged();
        var title = Current?.Title;
        _main.Toast(_main.Radio.IsGone(id) ? L.F("“{0}” is no longer available online and I couldn't find it elsewhere: skipping to the next one.", title)
            : _main.Radio.IsOffline(id)
                ? L.F("“{0}” isn't saved on your device and I can't download it: check your connection. Skipping to the next one.", title)
            : L.F("“{0}” can't be downloaded: on to the next one.", title));
        _queue.Forget(id);
        _ = Next(false);
    }

    internal void OnRadioBusy() => OnChanged(nameof(QueueEndHint), nameof(GenerateHint));

    // Suggestions arrived for this list. None new (offline, nothing found): all your songs follow.
    internal void ExtendRadio(string contextId, List<string> ids, bool first)
    {
        if (_room != null || _remote != null || _queue.ContextId != contextId || !_queue.Radio) return;
        int before = _queue.Context.Count;
        if (ids.Count > 0) _queue.Extend(ids);
        if (_queue.Context.Count == before && (first || _queue.UpcomingCount == 0))
        {
            if (first)
                _main.Toast(contextId.StartsWith("artist:", StringComparison.Ordinal)
                    ? L.T("No songs by the artist found online: all your songs follow this one.")
                    : L.T("No similar songs found online: all your songs follow this one."));
            _queue.UseFallback();
        }
        if (!_waitMore) return;
        _waitMore = false;
        OnWaitChanged();
        _ = Next(true);
    }

    // A suggested song saved to the library: it keeps its place in the queue, and plays on if it's playing.
    internal void ReplaceRadio(string oldId, Track saved)
    {
        if (Current?.Id == oldId)
        {
            Current = _main.Vm(saved);
            _main.Profile.Data.LastTrack = saved.Id;
            _main.OnCurrentChanged();
            _main.Host.OnTrackChanged();
        }
        _queue.Replace(oldId, saved.Id);
    }

    // The other way round: a suggested song saved in this session and now deleted from the library while it plays goes
    // on as a suggestion (from the cache) instead of jumping to the next song. False: it wasn't one of those.
    internal async Task<bool> KeepAsSuggestion(Track saved)
    {
        if (_room != null || Current?.Id != saved.Id || !_main.Radio.WasSaved(saved.Id)) return false;
        var item = _main.Radio.Unsave(saved, _opened ? _audio.SourcePath : null);
        if (item == null) return false;
        // Still playing the library copy (it's about to be deleted): on from the cache one, at the same point.
        if (_opened && string.Equals(_audio.SourcePath, saved.Path, StringComparison.OrdinalIgnoreCase) && item.T.CachePath is { } cached)
            await _audio.SwapAsync(cached, item.T.Duration > 0 ? item.T.Duration : Duration);
        _queue.Replace(saved.Id, item.Id);
        if (Current?.Id == saved.Id)
        {
            Current = _main.Vm(item.T);
            _main.Profile.Data.LastTrack = item.Id;
            _main.OnCurrentChanged();
            _main.Host.OnTrackChanged();
        }
        return true;
    }

    // The song playing had its audio removed from the device (it stays in the library, in the cloud): it goes on from the
    // copy moved into the cache, at the same point.
    internal async Task SwapToCache(Track t, string cached)
    {
        if (_room != null || Current?.Id != t.Id || !_opened) return;
        await _audio.SwapAsync(cached, t.Duration > 0 ? t.Duration : Duration);
    }

    // The file the player has open right now (null: nothing open).
    internal string? OpenPath => _opened ? _audio.SourcePath : null;

    // The song playing and the next ones: the suggested ones among them get ready.
    internal List<string> Window(int ahead)
    {
        var list = new List<string>();
        if (_queue.Current is { } c) list.Add(c);
        list.AddRange(_queue.Upcoming(ahead).Select(e => e.Id));
        return list;
    }

    internal HashSet<string> Referenced() => _queue.Referenced();

    // A list of suggestions with few songs left to come: the list (more are asked for).
    internal string? RadioRunningLow(int low) => _room == null && _queue.Radio && !_queue.Cleared && _queue.PlanCount < low ? _queue.ContextId : null;

    // More suggestions start from the newest one.
    internal RadioSeed? RadioSeed() => _queue.Radio && _queue.Context.LastOrDefault() is { } last && TrackOf(last) is { } t ? RadioViewModel.SeedOf(t) : null;

    // ------------------------------------------------------------------ listening statistics

    // The real time each song of the library is heard (in a room too, once its file is here), from its first second.
    // A play counts once at least three quarters of the song have been heard: song time, so at 2× it takes half the
    // real time, and jumping ahead doesn't count. The numbers change every second (the statistics follow them live);
    // they're saved now and then, at every play and at a pause.
    private const double PlayShare = 0.75;
    private readonly DispatcherTimer _listenTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _listenClock = new();
    private string? _listenId;
    private double _listenUnsaved, _listenHeard;
    private bool _listenCounted;

    private void UpdateListening()
    {
        if (IsPlaying && !_listenTimer.IsEnabled)
        {
            _listenClock.Restart();
            _listenTimer.Start();
        }
        else if (!IsPlaying && _listenTimer.IsEnabled)
        {
            CountListening();
            _listenTimer.Stop();
            SaveListening();
        }
    }

    // The song starts (again): what follows is a new play.
    private void NewListen()
    {
        SaveListening();
        _listenId = null;
    }

    private void CountListening()
    {
        if (!_listenTimer.IsEnabled) return;
        double dt = _listenClock.Elapsed.TotalSeconds;
        _listenClock.Restart();
        if (HeardId() is not { } id || dt > 5) return;
        bool started = id != _listenId;
        if (started)
        {
            SaveListening();
            _listenId = id;
            _listenHeard = 0;
            _listenCounted = false;
        }
        // The song goes by faster or slower than the clock.
        _listenHeard += dt * _audio.Speed;
        bool play = !_listenCounted && Duration > 0 && _listenHeard >= Duration * PlayShare;
        if (play) _listenCounted = true;
        // (connected to a phone: where the song is, SharedLibrary.Counters)
        var (here, there) = _main.Shared?.Counters(id) ?? (true, null);
        if (there != null) _main.Shared!.Heard(there, dt, play, false);
        if (here) _main.Profile.AddListening(id, dt, play, save: false);
        _listenUnsaved += dt;
        if (play || _listenUnsaved >= 60) SaveListening();
        _main.OnListened(id, play || started);
    }

    private void SaveListening()
    {
        if (_listenUnsaved <= 0) return;
        _listenUnsaved = 0;
        _main.Profile.Save();
    }

    // The song sounding here right now: one of the library (saved or in the cloud), or one heard without keeping it
    // (suggested, from a link: it stays in the statistics once a play of it counts).
    private string? HeardId()
    {
        // (driving a computer the computer counts, also while this phone plays along)
        if (Current is not { } c || !_opened || _remote != null) return null;
        if (_room != null)
        {
            if (_roomLoaded != _room.Session?.Current?.Id) return null;
            if (_main.Library.Get(c.Id) == null && !_main.Radio.Has(c.Id)) return _roomHeardId is { } kept && _main.Radio.Has(kept) ? kept : null;
        }
        return _main.Library.Get(c.Id) != null || _main.Radio.Has(c.Id) || _main.Shared?.Get(c.Id) != null ? c.Id : null;
    }

    // In a room: the id the song playing was kept by (RoomLoad).
    private string? _roomHeardId;

    public void PlayPause()
    {
        if (_room != null)
        {
            if (!Unsilence()) _room.TogglePause();
            return;
        }
        if (_remote != null)
        {
            if (Current != null || _remote.State.Upcoming > 0) _remote.Do("toggle");
            return;
        }
        if (Current == null)
        {
            if (_main.Library.Count > 0) PlayAll(_main.LibraryPage, Shuffle);
            return;
        }
        // Still downloading: whether it starts by itself once it's here.
        if (_waiting != null)
        {
            _waitPlay = !_waitPlay;
            OnWaitChanged();
            return;
        }
        if (IsPlaying)
        {
            _audio.Pause();
            IsPlaying = false;
            SaveState();
            return;
        }
        if (!_opened)
        {
            _ = Load(Current.T, true);
            return;
        }
        _audio.Play();
        IsPlaying = _audio.IsPlaying;
    }

    public void Play()
    {
        if (_room != null) { if (!Unsilence()) _room.SetPaused(false); }
        else if (_remote != null) _remote.Do("playing", flag: true);
        else if (!IsPlaying) PlayPause();
    }

    public void Pause()
    {
        if (_room != null) { if (!Unsilence()) _room.SetPaused(true); }
        else if (_remote != null) _remote.Do("playing", flag: false);
        else if (IsPlaying) PlayPause();
    }

    // First song that can play: its file is here, or it can be fetched from its link (a song in the cloud, a suggested one:
    // it's waited for). Songs that failed or are no longer available online are skipped; picked: the first one was chosen
    // by hand (tried anyway).
    private Track? NextPlayable(Func<string?> next, bool picked = false)
    {
        for (int guard = 0; guard < 50; guard++)
        {
            var id = next();
            if (id == null) return null;
            bool tryAnyway = picked && guard == 0;
            if (_main.Library.Get(id) is { } t)
            {
                if (t.HasSavedFile) return t;
                if (t.HasLink && (tryAnyway || !t.Unavailable && !_main.Radio.IsFailed(id))) return t;
            }
            else if (_main.Radio.Track(id) is { } s && (tryAnyway || !_main.Radio.IsFailed(id))) return s;
            _queue.Forget(id);
        }
        return null;
    }

    public async Task Next(bool auto)
    {
        if (_room != null)
        {
            if (!auto) _room.Skip();
            return;
        }
        if (_remote != null)
        {
            if (!auto) _remote.Do("next");
            return;
        }
        var t = NextPlayable(() => _queue.Next(auto));
        if (t == null)
        {
            _audio.Pause();
            IsPlaying = false;
            // The next suggestions are still on their way: the first one starts when they come.
            if (_queue.Radio && _main.Radio.Busy)
            {
                _waitMore = true;
                OnWaitChanged();
                return;
            }
            Seek(0);
            if (!auto && Current != null) _main.Toast(L.T("No more songs in the queue."));
            return;
        }
        await Load(t, true);
    }

    // restart: past the first 3 seconds the song starts over (the buttons); false = straight to the song before (the
    // phone's swipe on the cover).
    public async Task Previous(bool restart = true)
    {
        // In a room there's no going back to an earlier song: back to the start of this one.
        if (_room != null)
        {
            _room.Restart();
            return;
        }
        if (_remote != null)
        {
            _remote.Do("prev", flag: restart);
            return;
        }
        if (restart && Position > 3 || Current == null)
        {
            Seek(0);
            return;
        }
        var id = _queue.Previous();
        if (id == null || id == Current.Id)
        {
            Seek(0);
            return;
        }
        if (TrackOf(id) is { } t) await Load(t, true);
    }

    private void OnEnded()
    {
        // In a room the host's clock decides when the next song starts (driving a computer, the computer does).
        if (_room != null || _remote != null) return;
        if (Repeat == RepeatMode.One && Current != null)
        {
            CountListening();
            NewListen();
            Seek(0);
            AddHistory(Current.T);
            return;
        }
        _ = Next(true);
    }

    // Crossfade: next song starts while this one fades (a suggested one still downloading waits for the end).
    private void OnNearEnd()
    {
        if (_room != null || _remote != null || !IsPlaying || Repeat == RepeatMode.One || _audio.CrossfadeSeconds <= 0) return;
        if (_queue.PeekNext() is { } next && TrackOf(next) is { } coming && _main.Radio.Streams(coming) && coming.AudioPath == null &&
            _main.Radio.PathFor(next) == null) return;
        var t = NextPlayable(() => _queue.Next(true));
        if (t != null) _ = Load(t, true, crossfade: true);
    }

    public async Task JumpTo(int index)
    {
        if (_room != null)
        {
            _room.PlayNowAt(index);
            return;
        }
        if (_remote != null)
        {
            _remote.Do("jump", index: index);
            return;
        }
        var t = NextPlayable(() => _queue.JumpTo(index), picked: true);
        if (t != null)
        {
            if (_main.Radio.IsFailed(t.Id)) _main.Radio.Prepare(t.Id);
            await Load(t, true);
        }
    }

    public void RemoveUpcoming(int index)
    {
        if (_room != null) _room.RemoveAt(index);
        else if (_remote != null) _remote.Do("remove", index: index);
        else _queue.RemoveAt(index);
    }

    public void MoveUpcoming(int from, int to)
    {
        if (_room != null) _room.MoveAt(from, to);
        else if (_remote != null) _remote.Do("move", index: from, max: to);
        else _queue.Move(from, to);
    }

    // Several songs of "next up" at once (their places in it): out of it, or to its top or bottom keeping their order.
    public void RemoveUpcoming(IReadOnlyCollection<int> indexes)
    {
        foreach (var i in indexes.Distinct().OrderByDescending(i => i)) RemoveUpcoming(i);
    }

    public void MoveUpcomingToTop(IReadOnlyCollection<int> indexes)
    {
        int to = 0;
        foreach (var i in indexes.Distinct().OrderBy(i => i)) MoveUpcoming(i, to++);
    }

    public void MoveUpcomingToBottom(IReadOnlyCollection<int> indexes)
    {
        int to = (_remote?.State.Upcoming ?? _room?.RoomQueue.Count ?? _queue.UpcomingCount) - 1;
        foreach (var i in indexes.Distinct().OrderByDescending(i => i)) MoveUpcoming(i, to--);
    }

    public void Seek(double seconds)
    {
        if (_room != null)
        {
            _room.Seek(seconds);
            return;
        }
        if (Current == null) return;
        if (_remote != null)
        {
            seconds = Math.Clamp(seconds, 0, Math.Max(0, Duration - 0.25));
            _remote.Do("seek", pos: seconds);
            Position = seconds;
            _main.Host.OnSeek();
            return;
        }
        if (!_opened)
        {
            // Not opened yet: remembered for the first play.
            _resumeAt = Math.Clamp(seconds, 0, Math.Max(0, Duration - 1));
            Position = _resumeAt;
            return;
        }
        seconds = Math.Clamp(seconds, 0, Math.Max(0, Duration - 0.25));
        _audio.Seek(TimeSpan.FromSeconds(seconds));
        Position = seconds;
        _main.Host.OnSeek();
    }

    public void SeekFraction(double f) => Seek(f * Duration);

    // (driving a computer: where its song is)
    public TimeSpan EnginePosition => _remote != null ? TimeSpan.FromSeconds(_remote.Position) : _audio.Position;

    // Deleting the playing song moves to the next one.
    public async Task RemoveTracks(IReadOnlyCollection<string> trackIds)
    {
        bool wasCurrent = Current != null && trackIds.Contains(Current.Id);
        foreach (var id in trackIds) _queue.Forget(id);
        if (!wasCurrent || _room != null || _remote != null) return;
        bool wasPlaying = IsPlaying;
        var t = NextPlayable(() => _queue.Next(false));
        if (t != null && !trackIds.Contains(t.Id))
        {
            await Load(t, wasPlaying);
            return;
        }
        _loadVersion++;
        _audio.Close();
        _audio.Pause();
        _opened = false;
        IsPlaying = false;
        Current = null;
        Position = 0;
        Duration = 0;
        _main.Host.OnTrackChanged();
    }

    // ------------------------------------------------------------------ timer

    public void SetVisible(bool visible)
    {
        bool back = visible && !_visible;
        _visible = visible;
        UpdateTimer();
        if (visible) Tick();
        OnChanged(nameof(Animate));
        // What was heard while away: the statistics' page catches up (MainViewModel.OnListened waited).
        if (back && _listenId is { } heard) _main.OnListened(heard, true);
    }

    // Playing with the app on screen: only then the waveform's played part and the lyrics move by themselves every frame.
    public bool Animate => IsPlaying && _visible;

    public bool Shown => _visible;

    private void UpdateTimer()
    {
        if (_visible && IsPlaying) _timer.Start();
        else _timer.Stop();
    }

    private void Tick()
    {
        if (_remote != null) Position = _remote.Position;
        else if (_room != null) Position = _opened && _roomLoaded == _room.Session?.Current?.Id ? _audio.Position.TotalSeconds : _room.TargetPosition;
        else if (_opened) Position = _audio.Position.TotalSeconds;
    }

    // ------------------------------------------------------------------ listen together

    // In a room: the room decides what plays and where (TogetherViewModel.Follow); play, skip, seek and
    // "add to queue" go to the room. Your own queue waits untouched and comes back when you leave.
    private TogetherViewModel? _room;
    private string? _roomLoaded;

    public bool InRoom => _room != null;
    public bool NotInRoom => _room == null;
    public string? RoomName => _room?.RoomName;
    public ICommand OpenRoomCommand => new RelayCommand(() => _main.Navigate(_main.Together));
    internal string? RoomLoadedId => _roomLoaded;
    internal AudioEngine Audio => _audio;

    public void EnterRoom(TogetherViewModel room)
    {
        if (_room != null) return;
        SaveState();
        CountListening();
        NewListen();
        _loadVersion++;
        _audio.Pause();
        _audio.Close();
        _opened = false;
        _waiting = null;
        _waitMore = false;
        _room = room;
#if IOS_APP
        _audio.KeepAwake = true;
#endif
        _roomLoaded = null;
        ApplyCrossfade();
        // The room's speed from now on (Follow sets it).
        _audio.SetSpeed(1, false);
        _roomPitchApplied = false;
        _roomSpeedDraft = null;
        _roomPitchDraft = null;
        Current = null;
        Position = 0;
        Duration = 0;
        IsPlaying = false;
        OnRoomChanged();
        OnWaitChanged();
        _main.OnCurrentChanged();
        _main.Radio.Refresh();
    }

    public void LeaveRoom()
    {
        if (_room == null) return;
        CountListening();
        NewListen();
        _room = null;
#if IOS_APP
        _audio.KeepAwake = false;
#endif
        _silenced = false;
        ApplyVolume();
        _roomLoaded = null;
        _loadVersion++;
        _audio.Pause();
        _audio.Close();
        _opened = false;
        IsPlaying = false;
        Current = null;
        ApplyCrossfade();
        _speedTimer.Stop();
        _roomSpeedDraft = null;
        _roomPitchDraft = null;
        ApplySpeed();
        // Back to your own song, paused where you left it.
        Restore(false);
        OnRoomChanged();
        _main.OnCurrentChanged();
        _main.Radio.Refresh();
    }

    private void OnRoomChanged()
    {
        OnChanged(nameof(InRoom), nameof(NotInRoom), nameof(RoomName), nameof(ContextName), nameof(ShowNowPlaying), nameof(CanGenerate), nameof(PlayTip), nameof(NextTip), nameof(PreviousTip),
            nameof(SeekTip), nameof(QueueHint), nameof(RepeatOn), nameof(RepeatGlyph), nameof(RepeatTip), nameof(CanSetSpeed), nameof(SpeedTip), nameof(SpeedHint));
        OnSpeedChanged();
        RefreshUpNext();
        CommandManager.InvalidateRequerySuggested();
        _main.Host.OnTrackChanged();
    }

    // The host changed what we may do: buttons greyed out or back.
    public void OnRoomPermissions()
    {
        OnChanged(nameof(PlayTip), nameof(NextTip), nameof(PreviousTip), nameof(SeekTip), nameof(QueueHint), nameof(RoomName), nameof(RepeatTip),
            nameof(CanSetSpeed), nameof(SpeedTip), nameof(SpeedHint));
        CommandManager.InvalidateRequerySuggested();
    }

    public void OnRoomQueue()
    {
        RefreshUpNext();
        CommandManager.InvalidateRequerySuggested();
    }

    // The room's song, shown even before its file is here.
    internal void RoomShow(TrackViewModel? vm, double duration)
    {
        if (Current != vm)
        {
            Current = vm;
            if (vm == null) Duration = 0;
            _main.OnCurrentChanged();
            _main.Host.OnTrackChanged();
            if (UpNextVisible) RefreshUpNext();
        }
        if (!_opened || _roomLoaded != _room?.Session?.Current?.Id)
            if (duration > 0) Duration = duration;
    }

    // What the room is doing, for the buttons and the progress bar (the sound follows separately).
    internal void RoomState(bool playing, double position)
    {
        IsPlaying = playing;
        if (!_opened || _roomLoaded != _room?.Session?.Current?.Id) Position = position;
    }

    // False when the file couldn't be opened.
    // count false: the computer this phone drives counts the song (it plays here too, SyncFollower).
    internal async Task<bool> RoomLoad(string itemId, TrackViewModel vm, string path, bool count = true)
    {
        CountListening();
        NewListen();
        int version = ++_loadVersion;
        _roomLoaded = itemId;
        _opened = false;
        try
        {
            await _audio.OpenAsync(path, vm.T.Duration, TrackGain(vm.T));
            if (version != _loadVersion) return true;
            _opened = true;
            if (!count) return true;
            Duration = _audio.Duration.TotalSeconds > 0 ? _audio.Duration.TotalSeconds : vm.T.Duration;
            // A room's own song (not in the library) counts in the statistics as the copy kept among the recently played.
            _roomHeardId = AddHistory(vm.T);
            // Just downloaded and never analyzed (added to the room from a link): its waveform and loudness now, as for the
            // suggested songs, instead of a flat bar.
            if (vm.T.Wave == null) _ = AnalyzeRoomSong(vm.T, path, _roomHeardId);
            return true;
        }
        catch (Exception ex)
        {
            if (version != _loadVersion) return true;
            _roomLoaded = null;
            _main.Toast(L.F("Couldn't play “{0}”: {1}", vm.Title, ex.Message));
            return false;
        }
    }

    private async Task AnalyzeRoomSong(Track t, string path, string? kept)
    {
        await _main.Radio.Analyze(t, path);
        // The copy kept among the recently played gets them too.
        if (t.Wave != null && kept != null && kept != t.Id && _main.Radio.Track(kept) is { Wave: null } copy)
        {
            copy.Wave = t.Wave;
            copy.Loudness = t.Loudness;
            copy.Peak = t.Peak;
            _main.Vm(copy).Refresh();
        }
    }

    internal void RoomPlay(bool on)
    {
        if (on && !_audio.IsPlaying) _audio.Play();
        else if (!on && _audio.IsPlaying) _audio.Pause();
    }

    internal void RoomStop()
    {
        _loadVersion++;
        _roomLoaded = null;
        _opened = false;
        _audio.Pause();
        _audio.Close();
#if IOS_APP
        // (driving a computer: the silence that keeps the app and the link alive)
        if (_remote != null) _audio.Play();
#endif
    }

    // Tooltips of the transport: in a room without permission they say why the button is greyed out.
    public string PlayTip => _room != null && !_room.CanPause ? L.T("Only people with permission can pause (the host decides)") : L.T("Play / Pause (Space)");
    public string NextTip => _room != null && !_room.CanSkip ? L.T("Only people with permission can skip songs (the host decides)") : L.T("Next");
    public string? SeekTip => _room != null && !_room.CanPause ? L.T("Only people with permission can skip forward or back (the host decides)") : null;
    public string PreviousTip => _room == null ? L.T("Previous")
        : _room.CanPause ? L.T("From the start") : L.T("Only people with permission can skip forward or back (the host decides)");
    public string QueueHint => _room == null ? L.T("Double-click to jump to a song, drag to reorder.")
        : _room.CanRemove ? L.T("The room's queue: double-click to play a song now, drag to reorder.")
        : L.T("The room's queue, the same for everyone.");

    // ------------------------------------------------------------------ connect phone: this phone drives a computer

    // Connected to a computer (ComputerLinkViewModel): its player is the one shown and driven here, the songs play there
    // and this phone stays silent. Your own queue waits untouched and comes back, paused, when the computer goes.
    private ComputerLinkViewModel? _remote;

    public bool IsRemote => _remote != null;
    // Over the player: where the music plays.
    public string? RemoteText => _remote == null ? null
        : _remote.IsReconnecting ? L.F("Reconnecting to {0}…", _remote.ComputerName)
        : _main.Shared?.Audio switch
        {
            LinkAudio.Phone => L.F("Playing on this phone · {0}", _remote.ComputerName),
            LinkAudio.Both => L.F("Playing here and on {0}", _remote.ComputerName),
            _ => L.F("Playing on {0}", _remote.ComputerName),
        };
    public ICommand OpenRemoteCommand => new RelayCommand(() => _remote?.Open());

    private string RemoteWaitText => _remote!.State.WaitMore ? L.T("Looking for more similar songs…")
        : _remote.State.WaitPct > 0 ? L.F("Downloading · {0:0}%", _remote.State.WaitPct) : L.T("On its way…");

    public void EnterRemote(ComputerLinkViewModel link)
    {
        if (_remote != null || _room != null) return;
        SaveState();
        CountListening();
        NewListen();
        _loadVersion++;
        _audio.Pause();
        _audio.Close();
        _opened = false;
        _waiting = null;
        _waitMore = false;
        _remote = link;
        Current = null;
        Position = 0;
        Duration = 0;
        IsPlaying = false;
#if IOS_APP
        // iOS keeps an app running in the background only while it plays: silence, so the connection and the lock
        // screen's buttons stay alive (also when the music heard here too pauses).
        _audio.KeepAwake = true;
        _audio.Play();
#endif
        OnRemoteChanged();
        OnWaitChanged();
        _main.OnCurrentChanged();
        _main.Radio.Refresh();
    }

#if IOS_APP
    // After a call (an interruption) iOS has stopped the output: on again, driving a computer (the silence that keeps the
    // app and the link alive) or in a room (the music goes on for the others, here too).
    public void Wake() => _audio.Wake();

#endif
    public void LeaveRemote()
    {
        if (_remote == null) return;
        _remote = null;
        _loadVersion++;
#if IOS_APP
        _audio.KeepAwake = false;
#endif
        _audio.Pause();
        _audio.Close();
        _opened = false;
        _roomLoaded = null;
        IsPlaying = false;
        ApplyVolume();
        _roomPitchApplied = false;
        ApplySpeed();
        Current = null;
        Position = 0;
        Duration = 0;
        // Back to your own song, paused where you left it.
        Restore(false);
        OnRemoteChanged();
        OnWaitChanged();
        _main.OnCurrentChanged();
        _main.Radio.Refresh();
    }

    private void OnRemoteChanged()
    {
        OnRemoteLink();
        OnChanged(nameof(IsRemote), nameof(ShowNowPlaying), nameof(IsTemporary), nameof(Volume), nameof(Muted), nameof(VolumeGlyph));
        OnRemoteValues();
        RefreshUpNext();
        _main.Host.OnTrackChanged();
    }

    // Connected, reconnecting: what's said over the player.
    internal void OnRemoteLink() => OnChanged(nameof(RemoteText));

    private void OnRemoteValues()
    {
        OnChanged(nameof(ContextName), nameof(Shuffle), nameof(Repeat), nameof(RepeatOn), nameof(RepeatGlyph), nameof(RepeatTip), nameof(AutoQueue),
            nameof(AutoQueueTip), nameof(QueueEndHint), nameof(GenerateHint), nameof(UpcomingBadge), nameof(MoreText));
        OnSpeedChanged();
        CommandManager.InvalidateRequerySuggested();
    }

    // The computer's player changed (or one of this phone's buttons changed it).
    internal void RemoteState(TrackViewModel? current, LinkState s)
    {
        if (_remote == null) return;
        if (Current != current)
        {
            Current = current;
            _main.OnCurrentChanged();
            _main.Host.OnTrackChanged();
        }
        Duration = s.Duration > 0 ? s.Duration : current?.T.Duration ?? 0;
        IsPlaying = s.Playing;
        Position = _remote.Position;
        OnChanged(nameof(Volume), nameof(Muted), nameof(VolumeGlyph));
        ApplyVolume();
        OnRemoteValues();
        OnWaitChanged();
        _main.Host.OnSeek();
    }

    internal void OnRemoteQueue() => RefreshUpNext();

    // ------------------------------------------------------------------ connect phone: a phone drives this player

    // Under the song of the player bar: the phone connected (also with nothing playing), a click opens its window.
    public bool PhoneLinked => _main.PhoneLinked;
    public string? PhoneName => _main.PhoneLinked ? _main.PhoneLink.PhoneName : null;
    public string? PhoneTip => _main.PhoneLinked ? _main.PhoneLink.BarTip : null;
    public bool PhoneAway => _main.PhoneLinked && _main.PhoneLink.IsReconnecting;
    public ICommand OpenPhoneCommand => new RelayCommand(() => PhoneLinkViewModel.ShowWindow?.Invoke(_main));

    internal void OnPhoneLink() => OnChanged(nameof(ShowNowPlaying), nameof(PhoneLinked), nameof(PhoneName), nameof(PhoneTip), nameof(PhoneAway));

    internal string? WaitingId => _waiting;
    internal bool WaitingMore => _waitMore;
    // Playing, or about to (a song still on its way that starts by itself).
    internal bool PlaysSoon => IsPlaying || _waiting != null && _waitPlay;
    // Where the song is right now, also with the window away (the timer that moves Position stops then).
    internal double LivePosition => _room == null && _opened ? _audio.Position.TotalSeconds : Position;
    internal bool RadioQueue => _queue.Radio;
    internal int UpcomingCount => _queue.UpcomingCount;
    internal List<(string Id, bool Queued)> UpcomingIds(int max) => _queue.Upcoming(max).Select(e => (e.Id, e.Queued)).ToList();
    internal Track? TrackFor(string id) => TrackOf(id);

    // Songs of the connected phone (RadioViewModel.AddFromPhone), as its list said: from that one (all: the rest follows
    // anyway), or the whole list from its start (index -1; all: shuffled).
    internal void PlayPhone(List<string> ids, int index, string name, bool all)
    {
        if (_room != null || ids.Count == 0) return;
        var contextId = "phone:" + Ids.New();
        if (index < 0)
        {
            if (all)
            {
                Shuffle = true;
                _queue.PlayShuffled(ids, contextId, name);
            }
            else _queue.Play(ids, 0, contextId, name);
        }
        else _queue.Play(ids, Math.Min(index, ids.Count - 1), contextId, name, all || AutoQueue);
        if (_queue.Current is not { } id || TrackOf(id) is not { } t) return;
        if (Current?.Id == t.Id && (_opened || _waiting == t.Id))
        {
            OnChanged(nameof(ContextName));
            _main.OnContextChanged();
            Play();
        }
        else _ = Load(t, true);
    }

    // ------------------------------------------------------------------ up next

    public List<QueueRow> UpNext { get; private set; } = new();

    private bool _upNextVisible;
    public bool UpNextVisible
    {
        get => _upNextVisible;
        set { if (Set(ref _upNextVisible, value) && value) RefreshUpNext(); }
    }

    public void RefreshUpNext()
    {
        var rows = new List<QueueRow>();
        bool queuedHeader = false, planHeader = false;
        int index = 0;
        if (_room != null)
        {
            // The room's queue, the same for everyone.
            foreach (var t in _room.RoomQueue.Take(150))
            {
                rows.Add(new QueueRow(index, _room.TrackFor(t), true, index == 0 ? L.F("Queue of “{0}”", _room.RoomName) : null, this));
                index++;
            }
            UpNext = rows;
            OnChanged(nameof(UpNext), nameof(UpNextCount), nameof(ContextName), nameof(HasUpNext), nameof(UpcomingBadge), nameof(MoreText),
                nameof(GenerateHint), nameof(QueueEndHint), nameof(QueueHint));
            return;
        }
        if (_remote != null)
        {
            // The computer's "next up", with its own places in it.
            foreach (var (row, track) in _remote.Rows())
            {
                string? header = null;
                if (row.Queued && !queuedHeader) { header = L.T("Queued"); queuedHeader = true; }
                else if (!row.Queued && !planHeader)
                {
                    header = _remote.State.Radio ? ContextName : ContextName != null ? L.F("Up next from “{0}”", ContextName) : L.T("Up next");
                    planHeader = true;
                }
                rows.Add(new QueueRow(row.Index, track, row.Queued, header, this));
            }
            UpNext = rows;
            OnChanged(nameof(UpNext), nameof(UpNextCount), nameof(ContextName), nameof(HasUpNext), nameof(UpcomingBadge), nameof(MoreText),
                nameof(GenerateHint), nameof(QueueEndHint));
            CommandManager.InvalidateRequerySuggested();
            return;
        }
        foreach (var e in _queue.Upcoming(150))
        {
            if (TrackOf(e.Id) is { } t)
            {
                string? header = null;
                if (e.Queued && !queuedHeader) { header = L.T("Queued"); queuedHeader = true; }
                else if (!e.Queued && !planHeader)
                {
                    header = _queue.Radio ? ContextName : ContextName != null ? L.F("Up next from “{0}”", ContextName) : L.T("Up next");
                    planHeader = true;
                }
                rows.Add(new QueueRow(index, _main.Vm(t), e.Queued, header, this, _main.Radio.Item(e.Id)));
            }
            index++;
        }
        UpNext = rows;
        OnChanged(nameof(UpNext), nameof(UpNextCount), nameof(ContextName), nameof(HasUpNext), nameof(UpcomingBadge), nameof(MoreText),
            nameof(GenerateHint), nameof(QueueEndHint));
        CommandManager.InvalidateRequerySuggested();
    }

    public int UpNextCount => UpNext.Count;
    public bool HasUpNext => UpNext.Count > 0;

    // Count on the "show next up" button when the panel is folded away.
    public string? UpcomingBadge => (_remote?.State.Upcoming ?? _room?.RoomQueue.Count ?? _queue.UpcomingCount) switch
    {
        0 => null,
        > 999 => "999+",
        var n => n.ToString(L.Culture),
    };

    // Under the list: the songs that don't fit in it.
    public string? MoreText => (_remote?.State.Upcoming ?? _room?.RoomQueue.Count ?? _queue.UpcomingCount) - UpNext.Count is var more and > 0 ? L.Count(more, "and 1 more song", "and {0} more songs") : null;

    // Where "Generate" takes the songs from.
    public string GenerateHint
    {
        get
        {
            if (_remote != null && _remote.State.Radio) return L.T("More similar songs, found online");
            if (_remote == null && _queue.Radio) return _main.Radio.Busy ? L.T("Looking for more similar songs…") : L.T("More similar songs, found online");
            var name = _remote != null || _queue.CanGenerate ? ContextName : L.T("All songs");
            if (name == null) return Shuffle ? L.T("In random order") : "";
            return Shuffle ? L.F("From “{0}”, in random order", name) : L.F("From “{0}”", name);
        }
    }

    // What happens when this song ends with nothing after it.
    public string QueueEndHint => _room != null
        ? _room.CanAdd ? L.T("Add songs to the room with the + button in your playlists.") : L.T("Wait for someone to add a song to the room.")
        : Current == null
        ? L.T("Play a song: the ones after it will show up here.")
        : _remote != null && _remote.State.Radio && Repeat != RepeatMode.One
        ? L.T("When these are over, more similar ones come.")
        : _remote == null && _queue.Radio && Repeat != RepeatMode.One && !_queue.Cleared
        ? _main.Radio.Busy ? L.T("Looking for similar songs online…") : L.T("When these are over, more similar ones come.")
        : AutoQueue && Repeat != RepeatMode.One ? L.T("Automatic queue on: it refills itself when the song ends.")
        : L.T(Repeat switch
        {
            RepeatMode.One => "The current song repeats forever.",
            RepeatMode.All when _queue.Cleared || !_queue.CanGenerate => "The current song will start over.",
            RepeatMode.All => "Then the list starts over.",
            _ => "When this song ends, the music stops.",
        });
}
