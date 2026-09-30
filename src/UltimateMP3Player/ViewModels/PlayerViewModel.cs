using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using UltimateMP3Player.Audio;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// A row of "next up": queued by hand or from the list.
public sealed class QueueRow
{
    public QueueRow(int index, TrackViewModel track, bool queued, string? header, PlayerViewModel player)
    {
        Index = index;
        Track = track;
        Queued = queued;
        Header = header;
        Player = player;
    }

    public int Index { get; }
    public TrackViewModel Track { get; }
    public bool Queued { get; }
    public string? Header { get; }
    public PlayerViewModel Player { get; }

    public ICommand PlayCommand => new RelayCommand(() => _ = Player.JumpTo(Index));
    public ICommand RemoveCommand => new RelayCommand(() => Player.RemoveUpcoming(Index));

    public override string ToString() => $"{Track.Title} – {Track.Artist}";
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

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick();
        _audio.Ended += OnEnded;
        _audio.NearEnd += OnNearEnd;
        _audio.Failed += OnFailed;

        PlayPauseCommand = new RelayCommand(PlayPause);
        NextCommand = new RelayCommand(() => _ = Next(false));
        PreviousCommand = new RelayCommand(() => _ = Previous());
        CycleRepeatCommand = new RelayCommand(CycleRepeat);
        ToggleMuteCommand = new RelayCommand(() => Muted = !Muted);
        FavoriteCommand = new RelayCommand(() => { if (Current != null) _main.ToggleFavorite(Current); }, () => Current != null);
        SeekCommand = new RelayCommand(p => { if (p is double f) SeekFraction(f); });
        ClearQueueCommand = new RelayCommand(ClearQueue, () => _queue.UpcomingCount > 0);
        GenerateQueueCommand = new RelayCommand(GenerateQueue, () => Current != null);

        Restore(adopt);
        _queue.Changed += () =>
        {
            if (UpNextVisible) RefreshUpNext();
            SaveState();
        };
    }

    // Brings back the song, the list and "next up" of the last session.
    private void Restore(bool adopt)
    {
        var d = _main.Profile.Data;
        var lib = _main.Library;
        if (d.Queue is { } q && q.Current != null && lib.Get(q.Current) != null)
            _queue.Restore(q, id => lib.Get(id) != null);
        else if (d.LastTrack != null && lib.Get(d.LastTrack) is { } last)
        {
            // No saved queue yet: continue with the library.
            var ids = lib.Snapshot().OrderByDescending(t => t.Added).Select(t => t.Id).ToList();
            _queue.Play(ids, ids.IndexOf(last.Id), "library", "Tutti i brani");
        }
        if (_queue.Current == null || lib.Get(_queue.Current) is not { } track) return;
        Current = _main.Vm(track);
        Duration = track.Duration;
        if (adopt && _audio.HasSource && _audio.SourcePath == track.Path)
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
        double pos = 0;
        if (Current != null && Current.Id == _queue.Current) pos = _opened ? _audio.Position.TotalSeconds : _resumeAt;
        _main.Profile.Data.Queue = _queue.Save(pos);
        _main.Profile.Save();
    }

    public void Detach()
    {
        _timer.Stop();
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
            OnChanged(nameof(HasTrack), nameof(ContextName));
        }
    }

    public bool HasTrack => Current != null;
    public string? ContextId => _queue.ContextId;

    // Name of the list being played, in the current language.
    public string? ContextName => _queue.ContextId switch
    {
        "library" => L.T("Tutti i brani"),
        "unsorted" => L.T("Senza playlist"),
        { } id when id.StartsWith("playlist:") && _main.Profile.GetPlaylist(id[9..]) is { } p => PlaylistViewModel.DisplayName(p),
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
            _main.Host.OnPlaybackChanged();
        }
    }

    public string PlayGlyph => IsPlaying ? "" : "";

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
            if (_queue.Shuffle == value) return;
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

    public bool RepeatOn => Repeat != RepeatMode.Off;
    public string RepeatGlyph => Repeat == RepeatMode.One ? "" : "";
    // "Repeat one" loops the song until it's turned off, as in the other players (skipping still moves on).
    public string RepeatTip => L.T(Repeat switch
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
        _audio.CrossfadeSeconds = d.Crossfade ? Math.Clamp(d.CrossfadeSeconds, 1, 12) : 0;
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

    public void PlaySingle(Track t)
    {
        _queue.Play(new[] { t.Id }, 0, null, null);
        _ = Load(t, true);
    }

    public void Enqueue(TrackViewModel t, bool quiet = false)
    {
        _queue.Enqueue(t.Id);
        if (!quiet) _main.Toast(L.F("«{0}» aggiunto alla coda", t.Title));
    }

    public void PlayNext(TrackViewModel t)
    {
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
            if (_main.Profile.Data.AutoQueue == value) return;
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
        int version = ++_loadVersion;
        double resume = Current?.Id == t.Id ? _resumeAt : 0;
        _resumeAt = 0;
        SetCurrent(_main.Vm(t));
        _opened = false;
        try
        {
            if (crossfade) await _audio.CrossfadeToAsync(t.Path, t.Duration, TrackGain(t));
            else await _audio.OpenAsync(t.Path, t.Duration, TrackGain(t));
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
                _main.Profile.AddHistory(t.Id);
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

    public void PlayPause()
    {
        if (Current == null)
        {
            if (_main.Library.Count > 0) PlayAll(_main.LibraryPage, Shuffle);
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
        if (!IsPlaying) PlayPause();
    }

    public void Pause()
    {
        if (IsPlaying) PlayPause();
    }

    // First song whose file still exists.
    private Track? NextPlayable(Func<string?> next)
    {
        for (int guard = 0; guard < 50; guard++)
        {
            var id = next();
            if (id == null) return null;
            if (_main.Library.Get(id) is { } t && File.Exists(t.Path)) return t;
            _queue.Forget(id);
        }
        return null;
    }

    public async Task Next(bool auto)
    {
        var t = NextPlayable(() => _queue.Next(auto));
        if (t == null)
        {
            _audio.Pause();
            IsPlaying = false;
            Seek(0);
            if (!auto && Current != null) _main.Toast(L.T("Nessun altro brano in coda."));
            return;
        }
        await Load(t, true);
    }

    public async Task Previous()
    {
        if (Position > 3 || Current == null)
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
        if (_main.Library.Get(id) is { } t) await Load(t, true);
    }

    private void OnEnded()
    {
        if (Repeat == RepeatMode.One && Current != null)
        {
            Seek(0);
            _main.Profile.AddHistory(Current.Id);
            return;
        }
        _ = Next(true);
    }

    // Crossfade: next song starts while this one fades.
    private void OnNearEnd()
    {
        if (!IsPlaying || Repeat == RepeatMode.One || _audio.CrossfadeSeconds <= 0) return;
        var t = NextPlayable(() => _queue.Next(true));
        if (t != null) _ = Load(t, true, crossfade: true);
    }

    public async Task JumpTo(int index)
    {
        var t = NextPlayable(() => _queue.JumpTo(index));
        if (t != null) await Load(t, true);
    }

    public void RemoveUpcoming(int index) => _queue.RemoveAt(index);

    public void MoveUpcoming(int from, int to) => _queue.Move(from, to);

    public void Seek(double seconds)
    {
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
        if (!wasCurrent) return;
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
        if (_opened) Position = _audio.Position.TotalSeconds;
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
        foreach (var e in _queue.Upcoming(150))
        {
            if (_main.Library.Get(e.Id) is { } t)
            {
                string? header = null;
                if (e.Queued && !queuedHeader) { header = L.T("In coda"); queuedHeader = true; }
                else if (!e.Queued && !planHeader)
                {
                    header = ContextName != null ? L.F("Successivi da «{0}»", ContextName) : L.T("Successivi");
                    planHeader = true;
                }
                rows.Add(new QueueRow(index, _main.Vm(t), e.Queued, header, this));
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
    public string? UpcomingBadge => _queue.UpcomingCount switch
    {
        0 => null,
        > 999 => "999+",
        var n => n.ToString(L.Culture),
    };

    // Under the list: the songs that don't fit in it.
    public string? MoreText => _queue.UpcomingCount - UpNext.Count is var more and > 0 ? L.Count(more, "e un altro brano", "e altri {0} brani") : null;

    // Where "Generate" takes the songs from.
    public string GenerateHint
    {
        get
        {
            var name = _queue.CanGenerate ? ContextName : L.T("Tutti i brani");
            if (name == null) return Shuffle ? L.T("In ordine casuale") : "";
            return Shuffle ? L.F("Da «{0}», in ordine casuale", name) : L.F("Da «{0}»", name);
        }
    }

    // What happens when this song ends with nothing after it.
    public string QueueEndHint => Current == null
        ? L.T("Avvia un brano: quelli dopo di lui compariranno qui.")
        : AutoQueue && Repeat != RepeatMode.One ? L.T("Coda automatica attiva: si riempie da sola alla fine del brano.")
        : L.T(Repeat switch
        {
            RepeatMode.One => "Il brano in riproduzione si ripete all'infinito.",
            RepeatMode.All when _queue.Cleared || !_queue.CanGenerate => "Il brano in riproduzione ricomincerà da capo.",
            RepeatMode.All => "Poi la lista ricomincia da capo.",
            _ => "Finito questo brano, la musica si ferma.",
        });
}
