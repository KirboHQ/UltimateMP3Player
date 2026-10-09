using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using UltimateMP3Player.Audio;
using UltimateMP3Player.Core;

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
        _queue.Fallback = () => (_main.Library.Snapshot().OrderByDescending(t => t.Added).Select(t => t.Id).ToList(), "library", "Tutti i brani");
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
        FavoriteCommand = new RelayCommand(() => { if (Current != null) _main.ToggleFavorite(Current); }, () => Current != null);
        SeekCommand = new RelayCommand(p => { if (p is double f) SeekFraction(f); }, _ => _room == null || _room.CanPause);
        SpeedStepCommand = new RelayCommand(p => { if (p is string s && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) Speed += d; },
            _ => CanSetSpeed);
        SetSpeedCommand = new RelayCommand(p => { if (p is string s && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) Speed = d; },
            _ => CanSetSpeed);
        ClearQueueCommand = new RelayCommand(ClearQueue, () => _room == null && _queue.UpcomingCount > 0);
        GenerateQueueCommand = new RelayCommand(GenerateQueue, () => _room == null && Current != null);
        SaveCurrentCommand = new RelayCommand(SaveCurrent, () => IsTemporary);

        Restore(adopt);
        _queue.Changed += () =>
        {
            if (UpNextVisible) RefreshUpNext();
            SaveState();
            _main.Radio.Refresh();
        };
    }

    // A song of the library, or one not in it still in the queue or among the recently played.
    private Track? TrackOf(string id) => _main.Library.Get(id) ?? _main.Radio.Track(id);
    private bool Exists(string id) => TrackOf(id) != null;

    // Among the recently played: a song of the library, and one heard without saving it (suggested, from a link, or a
    // room's own, kept from now on like those).
    private void AddHistory(Track t)
    {
        if (_main.Library.Get(t.Id) != null || _main.Radio.Has(t.Id)) _main.Profile.AddHistory(t.Id);
        else if (_main.Radio.KeepHeard(t) is { } id) _main.Profile.AddHistory(id);
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
            _queue.Play(ids, ids.IndexOf(last.Id), "library", "Tutti i brani");
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
        // In a room the saved song is still your own one, from before.
        if (_room != null) return;
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
        get => _room != null ? _roomSpeedDraft ?? _room.RoomSpeed : _main.Profile.Data.Speed;
        set
        {
            value = Math.Round(Math.Clamp(value, 0.5, 2) * 20) / 20;
            if (Math.Abs(value - Speed) < 0.001) return;
            if (_room != null)
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
        get => _room != null ? _roomPitchDraft ?? _room.RoomPitch : _main.Profile.Data.SpeedPitch;
        set
        {
            if (value == SpeedPitch) return;
            if (_room != null)
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
    public string SpeedTip => _room == null ? L.T("Velocità di riproduzione (tasti < e >)")
        : _room.CanSpeed ? L.T("Velocità di riproduzione per tutti nella stanza (tasti < e >)")
        : L.T("Solo chi ha il permesso può cambiare la velocità (lo decide l'host)");
    public string SpeedHint => _room == null ? L.T("Vale per tutti i brani, finché non la cambi.")
        : _room.CanSpeed ? L.T("Cambia la velocità per tutti nella stanza.")
        : L.T("Solo chi ha il permesso può cambiare la velocità (lo decide l'host).");

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
    public bool IsTemporary => Current != null && _room == null && (_main.Radio.Has(Current.Id) || !Current.IsSaved);

    // The song playing was saved, or its audio removed from the device.
    public void RefreshSaved()
    {
        OnChanged(nameof(IsTemporary));
        CommandManager.InvalidateRequerySuggested();
        _main.Host.OnTrackChanged();
    }
    // The song block of the player bar: also in a room with nothing playing (it shows the room's name).
    public bool ShowNowPlaying => Current != null || _room != null;
    // "Generate queue" (the song page): your own queue only.
    public bool CanGenerate => Current != null && _room == null;
    public string? ContextId => _queue.ContextId;

    // Name of the list being played, in the current language.
    public string? ContextName => _queue.ContextId switch
    {
        "library" => L.T("Tutti i brani"),
        "unsorted" => L.T("Senza playlist"),
        { } id when id.StartsWith("playlist:") && _main.Profile.GetPlaylist(id[9..]) is { } p => PlaylistViewModel.DisplayName(p),
        { } id when id.StartsWith("radio:") => L.F("Brani simili a «{0}»", _queue.ContextName),
        _ => _queue.ContextName,
    };

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (!Set(ref _isPlaying, value)) return;
            OnChanged(nameof(PlayGlyph));
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
        get => _volume;
        set
        {
            value = Math.Clamp(value, 0, 1);
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
        get => _muted;
        set
        {
            if (!Set(ref _muted, value)) return;
            _main.Profile.Data.Muted = value;
            _main.Profile.Save();
            ApplyVolume();
            OnChanged(nameof(VolumeGlyph));
        }
    }

    public string VolumeGlyph => Muted || Volume <= 0 ? "" : Volume < 0.34 ? "" : Volume < 0.67 ? "" : "";

    private void ApplyVolume() => _audio.Volume = Muted ? 0 : (float)Math.Pow(_volume, 2);

    public bool Shuffle
    {
        get => _queue.Shuffle;
        set
        {
            if (_queue.Shuffle == value || _room != null) return;
            _queue.Shuffle = value;
            _main.Profile.Data.Shuffle = value;
            _main.Profile.Save();
            OnChanged(nameof(Shuffle), nameof(GenerateHint));
        }
    }

    public RepeatMode Repeat
    {
        get => _queue.Repeat;
        private set
        {
            _queue.Repeat = value;
            _main.Profile.Data.Repeat = value;
            _main.Profile.Save();
            OnChanged(nameof(Repeat), nameof(RepeatOn), nameof(RepeatGlyph), nameof(RepeatTip), nameof(QueueEndHint));
        }
    }

    // In a room the button is the loop of the song playing (the room has no "repeat the queue").
    public bool RepeatOn => _room != null ? _room.RoomLoop : Repeat != RepeatMode.Off;
    public string RepeatGlyph => _room != null || Repeat == RepeatMode.One ? "" : "";
    // "Repeat one" loops the song until it's turned off, as in the other players (skipping still moves on).
    public string RepeatTip => _room != null
        ? !_room.CanPause ? L.T("Solo chi ha il permesso di mettere in pausa può ripetere il brano (lo decide l'host)")
        : _room.RoomLoop ? L.T("Il brano si ripete per tutti (clicca per togliere il loop)")
        : L.T("Ripeti questo brano per tutti nella stanza")
        : L.T(Repeat switch
        {
            RepeatMode.All => "Ripeti la playlist (clicca per ripetere il brano)",
            RepeatMode.One => "Ripeti questo brano all'infinito (clicca per disattivare)",
            _ => "Ripeti: disattivato",
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
    public void PlayFrom(ITrackList list, TrackViewModel track, bool alwaysQueue = false)
    {
        // In a room nothing plays on its own: the song goes into the room's queue.
        if (_room != null)
        {
            _room.Add(new[] { track });
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
        _ = Load(track.T, true);
    }

    public void PlayAll(ITrackList list, bool shuffle)
    {
        if (_room != null)
        {
            _room.AddAll(list.PlayOrder, list.ContextName, shuffle);
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
        if (_queue.Current is { } id && _main.Library.Get(id) is { } t) _ = Load(t, true);
    }

    // A song on its own (from a menu, a file opened): after it the suggested songs, or all your songs (Settings).
    public void PlaySingle(Track t)
    {
        if (_room != null)
        {
            _room.Add(new[] { _main.Vm(t) });
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
        var t = seed.T;
        var contextId = "radio:" + t.Id;
        bool playing = Current?.Id == t.Id && (_opened || _waiting == t.Id);
        _main.Radio.Begin(t, contextId);
        _queue.Play(new[] { t.Id }, 0, contextId, t.Title, radio: true);
        if (!playing) _ = Load(t, true);
        if (announce) _main.Toast(L.F("Cerco brani simili a «{0}»…", t.Title));
    }

    // The songs of a link heard without downloading them into the library (RadioViewModel.AddFromLink): each comes into
    // the cache just before its turn, like the suggested songs, and "Save" keeps one. A single song is followed by songs
    // like it (Settings), a playlist plays as a list of its own.
    public void PlayStream(IReadOnlyList<string> ids, string name)
    {
        if (_room != null || ids.Count == 0 || TrackOf(ids[0]) is not { } first) return;
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
    private void SaveCurrent()
    {
        if (Current == null) return;
        if (!Current.InLibrary) _main.AddToLibrary(Current);
        else _ = _main.SaveToDevice(new[] { Current });
    }

    public void Enqueue(TrackViewModel t, bool quiet = false)
    {
        if (_room != null)
        {
            _room.Add(new[] { t });
            return;
        }
        _queue.Enqueue(t.Id);
        if (!quiet) _main.Toast(L.F("«{0}» aggiunto alla coda", t.Title));
    }

    public void PlayNext(TrackViewModel t)
    {
        if (_room != null)
        {
            _room.Add(new[] { t }, next: true);
            return;
        }
        _queue.PlayNextInQueue(t.Id);
        _main.Toast(L.F("«{0}» sarà il prossimo brano", t.Title));
    }

    // Nothing after this song: it plays alone (Generate brings the list back).
    private void ClearQueue()
    {
        _queue.ClearAll();
        _main.Toast(L.T("Coda svuotata"));
    }

    // Full queue: generated again from scratch; otherwise the missing songs of the list go at the end.
    // A song played on its own takes the library.
    private void GenerateQueue()
    {
        if (Current == null) return;
        // Suggested songs: the ones already found come back (an emptied queue), and more of them, from the last one.
        if (_queue.Radio)
        {
            _queue.Generate();
            _main.Toast(_main.Radio.More() ? L.T("Cerco altri brani simili…") : L.T("Sto già cercando altri brani simili…"));
            return;
        }
        var (added, regenerated) = _queue.Generate();
        _main.Toast(added == 0 ? L.T("Non ci sono altri brani da mettere in coda.")
            : regenerated ? L.T("Coda generata di nuovo da capo")
            : L.Count(added, "1 brano aggiunto in fondo alla coda", "{0} brani aggiunti in fondo alla coda"));
    }

    // Automatic queue (button next to the song): the rest of the list comes with a song, and at every new song
    // the missing ones go back at the end, so the queue stays full.
    public bool AutoQueue
    {
        get => _main.Profile.Data.AutoQueue;
        set
        {
            if (_main.Profile.Data.AutoQueue == value || _room != null) return;
            _main.Profile.Data.AutoQueue = value;
            _main.Profile.Save();
            _queue.AutoFill = value;
            if (value) _queue.TopUp();
            OnChanged(nameof(AutoQueue), nameof(AutoQueueTip), nameof(QueueEndHint));
            _main.Toast(value ? L.T("Coda automatica attiva: si riempie da sola a ogni brano") : L.T("Coda automatica spenta: finita la coda, la musica si ferma"));
        }
    }

    public string AutoQueueTip => AutoQueue
        ? L.T("Coda automatica attiva: a ogni brano i successivi si rigenerano (clicca per spegnerla)")
        : L.T("Coda automatica spenta: cliccando un brano suona solo quello (clicca per accenderla)");

    private async Task Load(Track t, bool play, bool crossfade = false)
    {
        // A saved song whose file is gone, with a link: it goes back to the cloud (heard from its link) instead of failing.
        if (t.IsSaved && !t.HasSavedFile && t.HasLink && _main.Library.Get(t.Id) == t)
        {
            t.Path = "";
            t.VideoPath = t.HasVideo && File.Exists(t.VideoPath) ? t.VideoPath : null;
            _main.Library.Changed(t);
            _main.Toast(L.F("Il file di «{0}» non c'è più: lo ascolti dal suo link, e puoi salvarlo di nuovo", t.Title));
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
                ? L.F("Il file di «{0}» non esiste più: è stato spostato o eliminato.", t.Title)
                : L.F("Impossibile riprodurre «{0}»: {1}", t.Title, ex.Message));
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

    public bool IsWaiting => _waiting != null || _waitMore;
    public string? WaitText => _waitMore ? L.T("Cerco altri brani simili…")
        : _waiting is not { } id ? null
        : _main.Radio.StatusOf(id) is { State: Core.Together.FileState.Downloading } st ? L.F("Lo sto scaricando · {0:0}%", st.Pct)
        : L.T("In arrivo…");

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
        _main.Toast(_main.Radio.IsGone(id) ? L.F("«{0}» non è più disponibile online e non l'ho trovato altrove: passo al successivo.", title)
            : _main.Radio.IsOffline(id)
                ? L.F("«{0}» non è salvato sul dispositivo e non riesco a scaricarlo: controlla la connessione. Passo al successivo.", title)
            : L.F("Non si riesce a scaricare «{0}»: passo al successivo.", title));
        _queue.Forget(id);
        _ = Next(false);
    }

    internal void OnRadioBusy() => OnChanged(nameof(QueueEndHint), nameof(GenerateHint));

    // Suggestions arrived for this list. None new (offline, nothing found): all your songs follow.
    internal void ExtendRadio(string contextId, List<string> ids, bool first)
    {
        if (_room != null || _queue.ContextId != contextId || !_queue.Radio) return;
        int before = _queue.Context.Count;
        if (ids.Count > 0) _queue.Extend(ids);
        if (_queue.Context.Count == before && (first || _queue.UpcomingCount == 0))
        {
            if (first) _main.Toast(L.T("Nessun brano simile trovato online: dopo questo continuano tutti i tuoi brani."));
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
        _main.Profile.AddListening(id, dt, play, save: false);
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
        => Current is { } c && _opened && (_room == null || _roomLoaded == _room.Session?.Current?.Id) &&
           (_main.Library.Get(c.Id) != null || _main.Radio.Has(c.Id)) ? c.Id : null;

    public void PlayPause()
    {
        if (_room != null)
        {
            _room.TogglePause();
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
        if (_room != null) _room.SetPaused(false);
        else if (!IsPlaying) PlayPause();
    }

    public void Pause()
    {
        if (_room != null) _room.SetPaused(true);
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
            if (!auto && Current != null) _main.Toast(L.T("Nessun altro brano in coda."));
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
        // In a room the host's clock decides when the next song starts.
        if (_room != null) return;
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
        if (_room != null || !IsPlaying || Repeat == RepeatMode.One || _audio.CrossfadeSeconds <= 0) return;
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
        else _queue.RemoveAt(index);
    }

    public void MoveUpcoming(int from, int to)
    {
        if (_room != null) _room.MoveAt(from, to);
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
        int to = (_room?.RoomQueue.Count ?? _queue.UpcomingCount) - 1;
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

    public TimeSpan EnginePosition => _audio.Position;

    // Deleting the playing song moves to the next one.
    public async Task RemoveTracks(IReadOnlyCollection<string> trackIds)
    {
        bool wasCurrent = Current != null && trackIds.Contains(Current.Id);
        foreach (var id in trackIds) _queue.Forget(id);
        if (!wasCurrent || _room != null) return;
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
        _visible = visible;
        UpdateTimer();
        if (visible) Tick();
    }

    private void UpdateTimer()
    {
        if (_visible && IsPlaying) _timer.Start();
        else _timer.Stop();
    }

    private void Tick()
    {
        if (_room != null) Position = _opened && _roomLoaded == _room.Session?.Current?.Id ? _audio.Position.TotalSeconds : _room.TargetPosition;
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
        OnChanged(nameof(InRoom), nameof(NotInRoom), nameof(RoomName), nameof(ShowNowPlaying), nameof(CanGenerate), nameof(PlayTip), nameof(NextTip), nameof(PreviousTip),
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
    internal async Task<bool> RoomLoad(string itemId, TrackViewModel vm, string path)
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
            Duration = _audio.Duration.TotalSeconds > 0 ? _audio.Duration.TotalSeconds : vm.T.Duration;
            AddHistory(vm.T);
            return true;
        }
        catch (Exception ex)
        {
            if (version != _loadVersion) return true;
            _roomLoaded = null;
            _main.Toast(L.F("Impossibile riprodurre «{0}»: {1}", vm.Title, ex.Message));
            return false;
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
    }

    // Tooltips of the transport: in a room without permission they say why the button is greyed out.
    public string PlayTip => _room != null && !_room.CanPause ? L.T("Solo chi ha il permesso può mettere in pausa (lo decide l'host)") : L.T("Riproduci / Pausa (Spazio)");
    public string NextTip => _room != null && !_room.CanSkip ? L.T("Solo chi ha il permesso può saltare i brani (lo decide l'host)") : L.T("Successivo");
    public string? SeekTip => _room != null && !_room.CanPause ? L.T("Solo chi ha il permesso può andare avanti o indietro (lo decide l'host)") : null;
    public string PreviousTip => _room == null ? L.T("Precedente")
        : _room.CanPause ? L.T("Dall'inizio") : L.T("Solo chi ha il permesso può andare avanti o indietro (lo decide l'host)");
    public string QueueHint => _room == null ? L.T("Doppio clic per saltare a un brano, trascina per riordinare.")
        : _room.CanRemove ? L.T("La coda della stanza: doppio clic per farlo partire, trascina per riordinare.")
        : L.T("La coda della stanza, uguale per tutti.");

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
                rows.Add(new QueueRow(index, _room.TrackFor(t), true, index == 0 ? L.F("Coda di «{0}»", _room.RoomName) : null, this));
                index++;
            }
            UpNext = rows;
            OnChanged(nameof(UpNext), nameof(UpNextCount), nameof(ContextName), nameof(HasUpNext), nameof(UpcomingBadge), nameof(MoreText),
                nameof(GenerateHint), nameof(QueueEndHint), nameof(QueueHint));
            return;
        }
        foreach (var e in _queue.Upcoming(150))
        {
            if (TrackOf(e.Id) is { } t)
            {
                string? header = null;
                if (e.Queued && !queuedHeader) { header = L.T("In coda"); queuedHeader = true; }
                else if (!e.Queued && !planHeader)
                {
                    header = _queue.Radio ? ContextName : ContextName != null ? L.F("Successivi da «{0}»", ContextName) : L.T("Successivi");
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
    public string? UpcomingBadge => (_room?.RoomQueue.Count ?? _queue.UpcomingCount) switch
    {
        0 => null,
        > 999 => "999+",
        var n => n.ToString(L.Culture),
    };

    // Under the list: the songs that don't fit in it.
    public string? MoreText => (_room?.RoomQueue.Count ?? _queue.UpcomingCount) - UpNext.Count is var more and > 0 ? L.Count(more, "e un altro brano", "e altri {0} brani") : null;

    // Where "Generate" takes the songs from.
    public string GenerateHint
    {
        get
        {
            if (_queue.Radio) return _main.Radio.Busy ? L.T("Cerco altri brani simili…") : L.T("Altri brani simili, trovati online");
            var name = _queue.CanGenerate ? ContextName : L.T("Tutti i brani");
            if (name == null) return Shuffle ? L.T("In ordine casuale") : "";
            return Shuffle ? L.F("Da «{0}», in ordine casuale", name) : L.F("Da «{0}»", name);
        }
    }

    // What happens when this song ends with nothing after it.
    public string QueueEndHint => _room != null
        ? _room.CanAdd ? L.T("Aggiungi brani alla stanza con il tasto + dalle tue playlist.") : L.T("Aspetta che qualcuno aggiunga un brano alla stanza.")
        : Current == null
        ? L.T("Avvia un brano: quelli dopo di lui compariranno qui.")
        : _queue.Radio && Repeat != RepeatMode.One && !_queue.Cleared
        ? _main.Radio.Busy ? L.T("Cerco brani simili online…") : L.T("Finiti questi, ne arrivano altri simili.")
        : AutoQueue && Repeat != RepeatMode.One ? L.T("Coda automatica attiva: si riempie da sola alla fine del brano.")
        : L.T(Repeat switch
        {
            RepeatMode.One => "Il brano in riproduzione si ripete all'infinito.",
            RepeatMode.All when _queue.Cleared || !_queue.CanGenerate => "Il brano in riproduzione ricomincerà da capo.",
            RepeatMode.All => "Poi la lista ricomincia da capo.",
            _ => "Finito questo brano, la musica si ferma.",
        });
}
