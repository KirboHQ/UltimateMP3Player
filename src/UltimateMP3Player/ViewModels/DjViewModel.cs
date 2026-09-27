using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using UltimateMP3Player.Audio;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// One deck: its song, transport, tempo and its channel on the mixer.
public sealed class DjDeckViewModel : Observable
{
    private int _loadId;

    public DjDeckViewModel(string name, string color, DjDeckEngine engine, DjViewModel dj)
    {
        Name = name;
        Brush = Ui.BrushFrom(color);
        Color = (Color)ColorConverter.ConvertFromString(color);
        Engine = engine;
        Dj = dj;
   }

    public string Name { get; }
    public Brush Brush { get; }
    public Color Color { get; }
    public DjDeckEngine Engine { get; }
    public DjViewModel Dj { get; }
    public DjDeckViewModel Other => this == Dj.A ? Dj.B : Dj.A;

    public Track? Track { get; private set; }
    public TrackViewModel? Song { get; private set; }
    public string? FilePath { get; private set; }
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public bool HasTrack { get; private set; }
    public bool IsTapper => this == Dj.Tapper;
    public string EmptyText => IsTapper ? L.T("Carica un brano per trovarne i BPM") : L.F("Deck {0}: carica un brano", Name);

    private bool _loading;
    public bool IsLoading { get => _loading; private set => Set(ref _loading, value); }

    // Detailed waveform and beat grid.
    public DjAnalysis? Analysis { get; private set; }

    public async Task LoadAsync(string path, string title, string? artist, Track? track, TrackViewModel? song, double startAt = 0, bool play = false)
    {
        int id = ++_loadId;
        Engine.Playing = false;
        IsLoading = true;
        Title = title;
        Artist = artist ?? "";
        Track = track;
        Song = song;
        FilePath = path;
        Analysis = null;
        _bpm = track?.Bpm;
        FirstBeat = track?.BeatOffset ?? 0;
        Cue = 0;
        IsSynced = false;
        TempoPercent = 0;
        OnChanged(nameof(Title), nameof(Artist), nameof(Song), nameof(Analysis), nameof(Bpm), nameof(BpmText), nameof(OriginalBpmText), nameof(BpmInput), nameof(ShowDetected), nameof(IsPlaying));
        try
        {
            await Engine.LoadAsync(path);
            if (id != _loadId) return;
            HasTrack = true;
            Engine.Seek(startAt);
            OnChanged(nameof(HasTrack));
            if (play) PlayPause();
            Dj.Poll();
            var a = await DjAnalyzer.AnalyzeAsync(path);
            if (id != _loadId) return;
            Analysis = a;
            // Only fills a song without BPM: a value typed by the user is never replaced.
            if (_bpm == null && a?.Bpm is { } bpm) Bpm = bpm;
            OnChanged(nameof(Analysis), nameof(ShowDetected), nameof(DetectedText));
        }
        catch (Exception ex)
        {
            if (id == _loadId) Dj.Status = ex.Message;
        }
        finally
        {
            if (id == _loadId) IsLoading = false;
        }
    }

    public void Eject()
    {
        _loadId++;
        Engine.Unload();
        HasTrack = false;
        Track = null;
        Song = null;
        FilePath = null;
        Analysis = null;
        Title = Artist = "";
        _bpm = null;
        IsSynced = false;
        IsLoading = false;
        OnChanged(nameof(HasTrack), nameof(Title), nameof(Artist), nameof(Song), nameof(Analysis), nameof(Bpm), nameof(BpmText), nameof(OriginalBpmText), nameof(BpmInput), nameof(ShowDetected), nameof(IsPlaying));
        Dj.Poll();
    }

    // ------------------------------------------------------------------ transport

    public bool IsPlaying => Engine.Playing;

    public void PlayPause()
    {
        if (!HasTrack) return;
        if (!Engine.Playing)
        {
            Dj.Engine.EnsureRunning();
            Dj.Main.Player.Pause();
            // The tap tool and the decks don't play over each other.
            if (IsTapper) foreach (var d in Dj.Decks) d.Engine.Playing = false;
            else Dj.Tapper.Engine.Playing = false;
        }
        Engine.Playing = !Engine.Playing;
        OnChanged(nameof(IsPlaying));
    }

    private double _cue;
    public double Cue { get => _cue; private set => Set(ref _cue, value); }

    // Like a CDJ: paused, CUE marks the spot; playing, it goes back there and stops.
    public void CuePressed()
    {
        if (!HasTrack) return;
        if (Engine.Playing)
        {
            Engine.Playing = false;
            Engine.Seek(Cue);
            OnChanged(nameof(IsPlaying));
        }
        else Cue = Engine.Position;
        Dj.Poll();
    }

    public void Seek(double seconds)
    {
        if (!HasTrack) return;
        Engine.Seek(seconds);
        Dj.Poll();
    }

    private double _firstBeat;
    // Where the beat grid starts (seconds of the song); every other beat follows from here and the BPM.
    public double FirstBeat { get => _firstBeat; internal set => Set(ref _firstBeat, value); }

    // "1st beat here": the grid starts at the playhead (put it on a kick first). Saved on the song.
    public void SetBeatHere()
    {
        if (!HasTrack || Engine.Position < 0) return;
        FirstBeat = Engine.Position;
        if (Track != null)
        {
            Track.BeatOffset = Math.Round(FirstBeat, 3);
            Dj.Main.Library.Changed(Track);
            Dj.ShareGrid(this);
        }
        // A synced deck lines its beats up again on the new grid.
        if (IsSynced) AlignPhase();
        else if (Other.IsSynced) Other.AlignPhase();
    }

    // ------------------------------------------------------------------ nudge

    // « / » held: the deck slips back or ahead of the other, to put it on the beat (like a hand on the record).
    // Playing it runs slower/faster while held: 2 % at first for small fixes, up to 8 % after a second and a half.
    // Stopped it moves by the same amount. The mouse wheel on the waveform moves it in 10 ms steps.
    private int _nudge;
    private long _nudgeFrom;
    private double _bend;

    public void StartNudge(int dir)
    {
        if (!HasTrack) return;
        _nudge = Math.Sign(dir);
        _nudgeFrom = Stopwatch.GetTimestamp();
    }

    public bool IsNudging => _nudge != 0;

    public void StopNudge()
    {
        if (_nudge == 0) return;
        _nudge = 0;
        SetBend(0);
    }

    public void NudgeStep(int dir) => Seek(Engine.Position + Math.Sign(dir) * 0.01);

    private void Nudging(double dt)
    {
        double held = (Stopwatch.GetTimestamp() - _nudgeFrom) / (double)Stopwatch.Frequency;
        double amount = Math.Min(0.08, 0.02 + 0.04 * held);
        if (Engine.Playing) SetBend(_nudge * amount);
        else
        {
            SetBend(0);
            Engine.Seek(Engine.Position + _nudge * amount * dt);
        }
    }

    private void SetBend(double bend)
    {
        if (_bend == bend) return;
        _bend = bend;
        Engine.Tempo = Rate * (1 + _bend);
        OnChanged(nameof(BpmText));
    }

    // ------------------------------------------------------------------ position

    private double _position;
    // Where the waveforms are drawn. The engine's position moves in steps (audio goes out in blocks): in between,
    // it runs on at the deck's speed and glides onto the engine's value, so the waves scroll at the screen's frame rate.
    public double Position { get => _position; private set => Set(ref _position, value); }
    private long _frameAt;
    private int _jumps = -1;

    // Every frame while the DJ page is open.
    public void Frame()
    {
        long now = Stopwatch.GetTimestamp();
        double dt = Math.Min(0.1, (now - _frameAt) / (double)Stopwatch.Frequency);
        _frameAt = now;
        if (!HasTrack) { Position = 0; return; }
        if (_nudge != 0) Nudging(dt);
        double engine = Engine.Position;
        if (!Engine.Playing || Engine.Jumps != _jumps)
        {
            _jumps = Engine.Jumps;
            Position = engine;
            return;
        }
        double next = _position + dt * Engine.Tempo, err = engine - next;
        Position = Math.Abs(err) > 0.2 ? engine : next + err * (1 - Math.Exp(-dt / 0.1));
    }
    public string ElapsedText => _position < 0 ? "-" + Text.Duration(-_position) : Text.Duration(_position) is { Length: > 0 } s ? s : "0:00";
    public string RemainingText => "-" + Text.Duration(Math.Max(0, Engine.Duration - _position) / Rate);

    private float _level;
    public float Level { get => _level; private set => Set(ref _level, value); }

    public void Poll()
    {
        Frame();
        Level = Engine.Level;
        OnChanged(nameof(ElapsedText), nameof(RemainingText), nameof(IsPlaying));
    }

    // ------------------------------------------------------------------ tempo

    private double? _bpm;
    // The song's own tempo; saved on the library song.
    public double? Bpm
    {
        get => _bpm;
        set
        {
            if (value is { } v) value = Math.Round(Math.Clamp(v, 30, 300), 2);
            if (!Set(ref _bpm, value)) return;
            if (Track != null && value != null)
            {
                Track.Bpm = value;
                Dj.Main.Library.Changed(Track);
                Dj.ShareGrid(this);
            }
            OnChanged(nameof(BpmText), nameof(OriginalBpmText), nameof(BpmInput), nameof(ShowDetected), nameof(DetectedText));
            if (IsSynced) Follow(false);
            else if (Other.IsSynced) Other.Follow(false);
        }
    }

    // What the analysis measured, offered when the BPM in the box differs (e.g. a song sped up on upload).
    public double? DetectedBpm => Analysis?.Bpm;
    public bool ShowDetected => DetectedBpm is { } d && _bpm is { } b && Math.Abs(d - b) >= 0.05;
    public string DetectedText => DetectedBpm is { } d ? L.F("rilevati {0} — usa", d.ToString("0.##", CultureInfo.CurrentCulture)) : "";

    public void UseDetected()
    {
        if (DetectedBpm is { } d) Bpm = d;
    }

    // The original BPM as typed in the box.
    public string BpmInput
    {
        get => _bpm?.ToString("0.##", CultureInfo.CurrentCulture) ?? "";
        set
        {
            var s = value.Trim().Replace(',', '.');
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0) Bpm = v;
            else OnChanged();
        }
    }

    public double Rate => 1 + _tempo / 100;
    public double? PlayedBpm => _bpm * Rate;
    // With « / » held it shows the nudged speed, so you see it working.
    public string BpmText => PlayedBpm is { } b ? (b * (1 + _bend)).ToString("0.0", CultureInfo.CurrentCulture) : "—";
    public string OriginalBpmText => _bpm is { } b ? L.F("originale {0}", b.ToString("0.0", CultureInfo.CurrentCulture)) : L.T("BPM sconosciuti");

    private double _range = 8;
    // Tempo fader range in percent, like on a CDJ.
    public double Range { get => _range; private set { if (Set(ref _range, value)) OnChanged(nameof(RangeText), nameof(TempoMin), nameof(TempoMax)); } }
    public string RangeText => "±" + _range + "%";
    public double TempoMin => -_range;
    public double TempoMax => _range;

    public void CycleRange()
    {
        Range = _range switch { 8 => 16, 16 => 50, _ => 8 };
        TempoPercent = Math.Clamp(_tempo, -_range, _range);
    }

    private double _tempo;
    public double TempoPercent
    {
        get => _tempo;
        set
        {
            value = Math.Round(Math.Clamp(value, -50, 50), 2);
            if (Math.Abs(value) > _range) Range = Math.Abs(value) > 16 ? 50 : 16;
            if (!Set(ref _tempo, value)) return;
            Engine.Tempo = Rate * (1 + _bend);
            OnChanged(nameof(TempoText), nameof(BpmText), nameof(RemainingText));
            if (Dj.Linking) return;
            // Synced decks change tempo together, so their beats stay on each other.
            Dj.Linking = true;
            try
            {
                if (IsSynced && Other.Bpm is { } master && PlayedBpm is { } played) Other.TempoPercent = (played / master - 1) * 100;
                else if (Other.IsSynced) Other.Follow(false);
            }
            finally { Dj.Linking = false; }
        }
    }

    private bool _synced;
    // SYNC on: this deck follows the other's tempo; its beats were lined up on the other's when it was turned on.
    public bool IsSynced
    {
        get => _synced;
        set
        {
            if (value && (_bpm == null || Other.PlayedBpm == null))
            {
                Dj.Status = L.T("Servono i BPM di entrambe le tracce.");
                value = false;
            }
            if (!Set(ref _synced, value)) return;
            if (!value) return;
            Other.IsSynced = false;
            Dj.Status = null;
            Follow(true);
        }
    }

    // On the other deck's tempo; with phase, the beat grids (from each first beat) lined up too.
    public void Follow(bool phase)
    {
        var o = Other;
        if (_bpm is not { } mine || o.PlayedBpm is not { } target) return;
        bool was = Dj.Linking;
        Dj.Linking = true;
        try { TempoPercent = (target / mine - 1) * 100; }
        finally { Dj.Linking = was; }
        if (phase) AlignPhase();
    }

    public string TempoText => (_tempo > 0 ? "+" : "") + _tempo.ToString("0.00", CultureInfo.CurrentCulture) + " %";

    public bool KeyLock
    {
        get => Engine.KeyLock;
        set { Engine.KeyLock = value; OnChanged(); }
    }

    private double _semitones;
    public double Semitones
    {
        get => _semitones;
        set
        {
            value = Math.Round(Math.Clamp(value, -12, 12));
            if (!Set(ref _semitones, value)) return;
            Engine.Semitones = value;
            OnChanged(nameof(KeyText));
        }
    }

    public string KeyText => _semitones == 0 ? L.T("Tonalità originale") : (_semitones > 0 ? "+" : "") + L.F("{0} semitoni", _semitones);

    // Moves this deck by less than a beat so its grid (counted from its first beat) falls on the other's.
    private void AlignPhase()
    {
        var o = Other;
        if (_bpm is not { } mine || o._bpm is not { } theirs || !HasTrack || !o.HasTrack) return;
        double myPeriod = 60 / mine, theirPeriod = 60 / theirs;
        double theirPhase = Frac((o.Engine.Position - o.FirstBeat) / theirPeriod);
        double myPhase = Frac((Engine.Position - FirstBeat) / myPeriod);
        double diff = theirPhase - myPhase;
        if (diff > 0.5) diff -= 1;
        if (diff < -0.5) diff += 1;
        Engine.Seek(Engine.Position + diff * myPeriod);
        Dj.Poll();
    }

    private static double Frac(double x) => x - Math.Floor(x);

    // ------------------------------------------------------------------ mixer channel

    private double _gain;
    public double GainDb { get => _gain; set { if (Set(ref _gain, Math.Round(value, 1))) Engine.Gain = (float)Math.Pow(10, _gain / 20); } }

    private double _fader = 1;
    public double Fader { get => _fader; set { if (Set(ref _fader, value)) Engine.Fader = (float)(value * value); } }

    public double Low { get => Engine.Low; set { Engine.Low = (float)value; OnChanged(); } }
    public double Mid { get => Engine.Mid; set { Engine.Mid = (float)value; OnChanged(); } }
    public double High { get => Engine.High; set { Engine.High = (float)value; OnChanged(); } }
}

// The DJ page: two decks live through one mixer; what you hear is the mix, and REC saves it as a song.
public sealed class DjViewModel : Observable
{
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private DateTime _recStart;

    public DjViewModel(MainViewModel main)
    {
        Main = main;
        A = new DjDeckViewModel("A", "#1E9BFF", Engine.A, this);
        B = new DjDeckViewModel("B", "#FF4FA3", Engine.B, this);
        Tapper = new DjDeckViewModel("T", "#A970FF", Engine.T, this);
        Decks = new[] { A, B };
        PlayCommand = new RelayCommand(p => (p as DjDeckViewModel)?.PlayPause());
        CueCommand = new RelayCommand(p => (p as DjDeckViewModel)?.CuePressed());
        SyncCommand = new RelayCommand(p => { if (p is DjDeckViewModel d) d.IsSynced = !d.IsSynced; });
        TapCommand = new RelayCommand(Tap);
        TapResetCommand = new RelayCommand(ResetTaps);
        TapSaveCommand = new RelayCommand(p => SaveTaps(p is "round"));
        RangeCommand = new RelayCommand(p => (p as DjDeckViewModel)?.CycleRange());
        EjectCommand = new RelayCommand(p => (p as DjDeckViewModel)?.Eject());
        RecordCommand = new RelayCommand(() => _ = ToggleRecord());
        ToggleAllCommand = new RelayCommand(ToggleAll);
        BeatHereCommand = new RelayCommand(p => (p as DjDeckViewModel)?.SetBeatHere());
        UseDetectedCommand = new RelayCommand(p => (p as DjDeckViewModel)?.UseDetected());
        _clock.Tick += (_, _) => Poll();
    }

    public MainViewModel Main { get; }
    public DjEngine Engine { get; } = new();
    public DjDeckViewModel A { get; }
    public DjDeckViewModel B { get; }
    // The song player of the "Tap BPM" tab.
    public DjDeckViewModel Tapper { get; }
    public DjDeckViewModel[] Decks { get; }

    public ICommand PlayCommand { get; }
    public ICommand CueCommand { get; }
    public ICommand SyncCommand { get; }
    public ICommand TapCommand { get; }
    public ICommand TapResetCommand { get; }
    public ICommand TapSaveCommand { get; }
    public ICommand RangeCommand { get; }
    public ICommand EjectCommand { get; }
    public ICommand RecordCommand { get; }
    public ICommand ToggleAllCommand { get; }
    public ICommand BeatHereCommand { get; }
    public ICommand UseDetectedCommand { get; }

    // Set while one deck's tempo is being copied onto the other (no ping-pong).
    public bool Linking { get; set; }

    // Dragging the shared playhead: both decks move together, each at its own speed, so they stay in sync.
    private readonly double[] _scrubFrom = new double[2];

    public void BeginScrubBoth()
    {
        for (int i = 0; i < 2; i++) _scrubFrom[i] = Decks[i].Engine.Position;
    }

    public void ScrubBoth(double seconds)
    {
        for (int i = 0; i < 2; i++)
            if (Decks[i].HasTrack) Decks[i].Engine.Seek(_scrubFrom[i] + seconds * Decks[i].Rate);
        Poll();
    }

    public bool AnyPlaying => TapMode ? Tapper.IsPlaying : Decks.Any(d => d.IsPlaying);

    // Space / the "A + B" button: both decks start together, or everything stops. On the tap tab: its song.
    public void ToggleAll()
    {
        if (TapMode)
        {
            Tapper.PlayPause();
            Poll();
            return;
        }
        var loaded = Decks.Where(d => d.HasTrack).ToList();
        if (loaded.Count == 0) return;
        if (loaded.Any(d => d.IsPlaying))
        {
            foreach (var d in loaded) d.Engine.Playing = false;
        }
        else
        {
            Engine.EnsureRunning();
            Main.Player.Pause();
            Tapper.Engine.Playing = false;
            foreach (var d in loaded) d.Engine.Playing = true;
        }
        Poll();
    }

    // A BPM or first beat set on one deck goes to the others holding the same song.
    public void ShareGrid(DjDeckViewModel from)
    {
        if (from.Track == null) return;
        foreach (var d in new[] { A, B, Tapper })
        {
            if (d == from || d.Track != from.Track) continue;
            // First beat first: setting the BPM shares again from d.
            d.FirstBeat = from.FirstBeat;
            d.Bpm = from.Bpm;
        }
    }

    // ------------------------------------------------------------------ tap tab

    private bool _tapMode;
    // The "Tap BPM" tab: the song plays as it is and you tap along to find its BPM.
    public bool TapMode
    {
        get => _tapMode;
        set
        {
            if (!Set(ref _tapMode, value)) return;
            OnChanged(nameof(ConsoleMode), nameof(AnyPlaying));
            if (!value)
            {
                if (Tapper.IsPlaying) Tapper.PlayPause();
                return;
            }
            // Starts with the song of deck A (or the one playing).
            if (Tapper.HasTrack || Tapper.IsLoading) return;
            if (A.HasTrack && A.FilePath != null) _ = Tapper.LoadAsync(A.FilePath, A.Title, A.Artist, A.Track, A.Song);
            else if (Main.Player.Current is { } cur && File.Exists(cur.T.Path)) Load(cur, Tapper);
        }
    }

    public bool ConsoleMode { get => !_tapMode; set => TapMode = !value; }

    public void LoadFrom(DjDeckViewModel from, DjDeckViewModel to)
    {
        if (from.FilePath != null) _ = to.LoadAsync(from.FilePath, from.Title, from.Artist, from.Track, from.Song);
    }

    private readonly List<long> _taps = new();
    private double? _tapBpm;

    // The BPM of the taps: the straight line that fits them all best, so one tap a bit off barely moves it.
    // A pause of 2 s starts over.
    public void Tap()
    {
        long now = Stopwatch.GetTimestamp();
        if (_taps.Count > 0 && (now - _taps[^1]) / (double)Stopwatch.Frequency > 2) _taps.Clear();
        _taps.Add(now);
        if (_taps.Count > 64) _taps.RemoveAt(0);
        _tapBpm = null;
        int n = _taps.Count;
        if (n >= 4)
        {
            double mi = (n - 1) / 2.0, mt = _taps.Average(t => (t - _taps[0]) / (double)Stopwatch.Frequency), num = 0, den = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (_taps[i] - _taps[0]) / (double)Stopwatch.Frequency;
                num += (i - mi) * (t - mt);
                den += (i - mi) * (i - mi);
            }
            if (num > 0) _tapBpm = 60 / (num / den);
        }
        OnTaps();
    }

    public void ResetTaps()
    {
        _taps.Clear();
        _tapBpm = null;
        OnTaps();
    }

    private void OnTaps() => OnChanged(nameof(TapText), nameof(TapCountText), nameof(HasTapBpm), nameof(TapRoundText), nameof(TapExactText));

    public bool HasTapBpm => _tapBpm != null;
    public string TapText => _tapBpm is { } b ? b.ToString("0.0", CultureInfo.CurrentCulture) : "—";
    public string TapCountText => _taps.Count == 0 ? L.T("Clicca o premi T a tempo con la musica") :
        _taps.Count < 4 ? L.T("Continua… (almeno 4 colpi)") : L.F("{0} colpi (una pausa di 2 secondi ricomincia)", _taps.Count);
    public string TapRoundText => _tapBpm is { } b ? L.F("Salva {0}", Math.Round(b).ToString(CultureInfo.CurrentCulture)) : "";
    public string TapExactText => _tapBpm is { } b ? L.F("Salva {0}", Math.Round(b, 2).ToString("0.##", CultureInfo.CurrentCulture)) : "";

    private void SaveTaps(bool round)
    {
        if (_tapBpm is not { } b || !Tapper.HasTrack) return;
        Tapper.Bpm = round ? Math.Round(b) : Math.Round(b, 2);
        Main.Toast(Tapper.Track != null ? L.F("BPM salvati: {0}", Tapper.BpmInput) : L.F("BPM impostati: {0}", Tapper.BpmInput));
    }

    public double Crossfader { get => Engine.Crossfader; set { Engine.Crossfader = (float)value; OnChanged(); } }
    public double Master { get => Engine.Master; set { Engine.Master = (float)value; OnChanged(); } }

    private float _level;
    public float Level { get => _level; private set => Set(ref _level, value); }

    private string? _status;
    public string? Status { get => _status; set => Set(ref _status, value); }

    public void Poll()
    {
        A.Poll();
        B.Poll();
        Tapper.Poll();
        Level = Engine.Level;
        OnChanged(nameof(AnyPlaying));
        if (Engine.IsRecording) OnChanged(nameof(RecordText));
        Frames(_open && Decks.Append(Tapper).Any(d => d.IsPlaying || d.IsNudging));
    }

    // The waveforms move at the screen's frame rate while the page is open and something moves
    // (a frame loop running for nothing keeps the whole window redrawing).
    private bool _open, _frames;

    private void Frames(bool on)
    {
        if (on == _frames) return;
        _frames = on;
        if (on) CompositionTarget.Rendering += OnFrame;
        else CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        A.Frame();
        B.Frame();
        Tapper.Frame();
    }

    // Opening the page: the song being listened to moves onto deck A, from where it was.
    public void Enter()
    {
        _clock.Start();
        _open = true;
        var player = Main.Player;
        if (A.HasTrack || A.IsLoading || player.Current is not { } cur || !File.Exists(cur.T.Path)) return;
        bool playing = player.IsPlaying;
        double at = player.Position;
        _ = A.LoadAsync(cur.T.Path, cur.Title, cur.T.Artist, cur.T, cur, at, playing);
    }

    // Leaving: the decks stop (a recording keeps its file until saved).
    public void Leave()
    {
        _clock.Stop();
        _open = false;
        Frames(false);
        foreach (var d in Decks.Append(Tapper))
        {
            d.StopNudge();
            if (d.Engine.Playing) d.PlayPause();
        }
    }

    public void Detach()
    {
        _clock.Stop();
        Frames(false);
        Engine.Dispose();
    }

    // ------------------------------------------------------------------ loading

    public void Load(TrackViewModel t, DjDeckViewModel deck)
    {
        if (!File.Exists(t.T.Path)) { Main.Toast(L.T("Il file non esiste più.")); return; }
        _ = deck.LoadAsync(t.T.Path, t.Title, t.T.Artist, t.T, t);
    }

    public void LoadCurrent(DjDeckViewModel deck)
    {
        if (Main.Player.Current is { } cur) Load(cur, deck);
    }

    public void PickFromLibrary(DjDeckViewModel deck)
    {
        var all = Main.AllVms().OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        var filter = new TextBox { Style = (Style)Application.Current.Resources["FilterBox"], Tag = L.T("Cerca nei brani"), Margin = new Thickness(0, 0, 0, 10) };
        var list = new ListBox
        {
            Style = (Style)Application.Current.Resources["PlainList"], Height = 380, ItemsSource = all,
            ItemTemplate = (DataTemplate)XamlReader.Parse(
                "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Margin='8,5'>" +
                "<TextBlock Text='{Binding Title}' FontSize='13.5' TextTrimming='CharacterEllipsis'/>" +
                "<TextBlock Text='{Binding Artist}' FontSize='12' Opacity='0.6' TextTrimming='CharacterEllipsis'/></StackPanel></DataTemplate>"),
        };
        filter.TextChanged += (_, _) =>
        {
            var q = new TrackQuery(filter.Text);
            list.ItemsSource = all.Where(q.Matches).ToList();
        };
        var body = new StackPanel();
        body.Children.Add(filter);
        body.Children.Add(list);
        var ok = Dialogs.Button(L.T("Carica"), "PrimaryButton", isDefault: true);
        var win = Dialogs.Frame(L.F("Scegli il brano per il deck {0}", deck.Name), body, 460, Dialogs.Button(L.T("Annulla"), "GhostButton", isCancel: true), ok);
        TrackViewModel? picked = null;
        void Pick() { if (list.SelectedItem is TrackViewModel t) { picked = t; win.DialogResult = true; } }
        ok.Click += (_, _) => Pick();
        list.MouseDoubleClick += (_, _) => Pick();
        win.Loaded += (_, _) => filter.Focus();
        win.ShowDialog();
        if (picked != null) Load(picked, deck);
    }

    public void PickFile(DjDeckViewModel deck)
    {
        var dlg = new OpenFileDialog
        {
            Title = L.T("Scegli un file audio"),
            Filter = L.T("Audio e video") + "|" + string.Join(";", Importer.Extensions.Select(e => "*" + e)) + "|" + L.T("Tutti i file") + "|*.*",
        };
        if (dlg.ShowDialog() == true) _ = deck.LoadAsync(dlg.FileName, Path.GetFileNameWithoutExtension(dlg.FileName), null, null, null);
    }

    // ------------------------------------------------------------------ recording

    public bool IsRecording => Engine.IsRecording;
    public string RecordText => IsRecording ? "REC " + Text.Duration((DateTime.Now - _recStart).TotalSeconds) : "REC";

    private async Task ToggleRecord()
    {
        if (!IsRecording)
        {
            Engine.StartRecording();
            _recStart = DateTime.Now;
            _clock.Start();
            OnChanged(nameof(IsRecording), nameof(RecordText));
            return;
        }
        var wav = Engine.StopRecording();
        OnChanged(nameof(IsRecording), nameof(RecordText));
        if (wav != null) await SaveRecording(wav);
    }

    private async Task SaveRecording(string wav)
    {
        var decks = Decks.Where(d => d.HasTrack).ToList();
        var res = Application.Current.Resources;
        var body = new StackPanel();
        TextBox Field(string label, string value)
        {
            body.Children.Add(new TextBlock { Text = label, Style = (Style)res["FieldLabel"], Margin = new Thickness(2, 8, 0, 6) });
            var box = new TextBox { Text = value, Style = (Style)res["BoxTextBox"] };
            body.Children.Add(box);
            return box;
        }
        var title = Field(L.T("Titolo"), string.Join(" x ", decks.Select(d => d.Title)) + " (mix)");
        var artist = Field(L.T("Artista"), string.Join(", ", decks.Select(d => d.Artist).Where(a => a.Length > 0).Distinct()));
        body.Children.Add(new TextBlock { Text = L.T("Copertina"), Style = (Style)res["FieldLabel"], Margin = new Thickness(2, 8, 0, 6) });
        string? cover = null;
        var coverButton = new Button { Content = L.T("Scegli un'immagine…"), Style = (Style)res["GhostButton"], HorizontalAlignment = HorizontalAlignment.Left };
        coverButton.Click += (_, _) =>
        {
            var d = new OpenFileDialog { Filter = L.T("Immagini") + "|*.jpg;*.jpeg;*.png;*.webp;*.bmp" };
            if (d.ShowDialog() != true) return;
            cover = d.FileName;
            coverButton.Content = Path.GetFileName(cover);
        };
        body.Children.Add(coverButton);
        body.Children.Add(new TextBlock { Text = L.T("Salva nella playlist"), Style = (Style)res["FieldLabel"], Margin = new Thickness(2, 12, 0, 6) });
        const string NewOne = "\0new";
        var choices = new List<Choice> { new(L.T("Nessuna (solo in «Tutti i brani»)"), null), new(L.T("Nuova playlist…"), NewOne) };
        choices.AddRange(Main.Playlists.Select(p => new Choice(p.Name, p.P)));
        var playlist = new ComboBox { ItemsSource = choices, SelectedIndex = 0 };
        body.Children.Add(playlist);
        // Name of the new playlist, shown when "New playlist…" is picked.
        var newName = new TextBox { Text = Main.NewPlaylistName(), Style = (Style)res["BoxTextBox"], Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        body.Children.Add(newName);
        bool IsNew() => Equals((playlist.SelectedItem as Choice)?.Value, NewOne);
        playlist.SelectionChanged += (_, _) =>
        {
            newName.Visibility = IsNew() ? Visibility.Visible : Visibility.Collapsed;
            if (IsNew()) { newName.Focus(); newName.SelectAll(); }
        };

        var ok = Dialogs.Button(L.T("Salva"), "PrimaryButton", isDefault: true);
        var win = Dialogs.Frame(L.T("Salva la registrazione"), body, 420, Dialogs.Button(L.T("Scarta"), "GhostButton", isCancel: true), ok);
        ok.Click += (_, _) => { if (title.Text.Trim().Length > 0 && (!IsNew() || newName.Text.Trim().Length > 0)) win.DialogResult = true; };
        if (win.ShowDialog() != true)
        {
            try { File.Delete(wav); } catch { }
            return;
        }

        Status = L.T("Salvataggio del mix…");
        try
        {
            string name = string.Join("_", $"{artist.Text.Trim()} - {title.Text.Trim()}".Trim(' ', '-').Split(Path.GetInvalidFileNameChars()));
            var dir = Main.Host.Settings.MusicDir;
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, name + ".mp3");
            for (int i = 2; File.Exists(file); i++) file = Path.Combine(dir, $"{name} ({i}).mp3");
            var r = await ProcRunner.RunAsync(Engines.Ffmpeg, new[]
            {
                "-v", "error", "-y", "-i", wav, "-c:a", "libmp3lame", "-b:a", "320k", "-id3v2_version", "3",
                "-metadata", "title=" + title.Text.Trim(), "-metadata", "artist=" + artist.Text.Trim(), file,
            }, captureOut: false);
            if (r.ExitCode != 0) throw new EngineException(r.StdErr.Trim().Split('\n').LastOrDefault() ?? "ffmpeg");
            var t = await Task.Run(() => Importer.ImportAsync(Main.Library, file, CancellationToken.None));
            // Made here: the app owns the file (deleting the song deletes it).
            t.Site = "DJ";
            if (cover != null && await AudioAnalysis.MakeCoverFromImageAsync(cover, AppPaths.TrackCover(t.Id)))
            {
                t.HasCover = true;
                t.CoverVersion++;
            }
            Main.Library.Changed(t);
            var target = IsNew() ? Main.Profile.CreatePlaylist(newName.Text.Trim()) : (playlist.SelectedItem as Choice)?.Value as Playlist;
            if (target != null) Main.Profile.AddTrack(target, t.Id);
            Status = null;
            Main.Toast(L.F("Mix «{0}» salvato nella libreria", t.Title));
        }
        catch (Exception ex) { Status = L.T("Mix non riuscito:") + " " + ex.Message; }
        finally { try { File.Delete(wav); } catch { } }
    }
}
