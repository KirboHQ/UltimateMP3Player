using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using UltimateMP3Player.Core;
using UltimateMP3Player.Core.Link;
using UltimateMP3Player.Core.Together;
using UltimateMP3Player.Services;

namespace UltimateMP3Player.ViewModels;

// "Connect to computer", on the phone: visible to the computers nearby while its page is open, it asks before one
// connects (not through a cable), then drives that computer's player (PlayerViewModel's remote mode): the phone shows
// what plays there and its buttons work there. Each device shows the other's music too (SharedLibrary, as the settings
// chose); the music is heard on the computer, on the phone or on both (the phone then plays along, on the computer's
// clock: SyncFollower). The computer can also send music here ("To the phone" in its export window): the usual import
// window, the files coming one at a time.
public sealed class ComputerLinkViewModel : Observable
{
    private readonly MainViewModel _main;
    private readonly Dispatcher _ui;
    private ComputerLink _link;
    private string _myName = "";
    private bool _visible, _remote, _shut;
    private LinkState _state = new();
    private List<LinkRow> _rows = new();
    // Where the computer's song was at _posAt (the phone moves it on by itself between two messages).
    private double _posBase;
    private long _posAt;
    private int _req;
    private SharedLibrary? _shared;
    // Songs of the computer's queue that aren't in its library (its suggestions): "pc-" + their id there.
    private readonly Dictionary<string, Track> _standIns = new();
    // ---- the music heard here too: the files of the computer's songs sent for it (the one playing, the next one)
    private readonly DispatcherTimer _follow;
    private readonly SyncFollower _sync;
    private readonly Dictionary<string, string> _files = new();
    private readonly HashSet<string> _fetching = new();
    private readonly Dictionary<string, long> _failedUntil = new();

    // Opens the "Connect to computer" page (set by the phone's screen).
    public static Action? ShowPage { get; set; }

    public ComputerLinkViewModel(MainViewModel main)
    {
        _main = main;
        _ui = Application.Current.Dispatcher;
        _sync = new SyncFollower(main.Player, false);
        _follow = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _follow.Tick += (_, _) => Follow();
        _link = NewLink();
        DisconnectCommand = new RelayCommand(() => _ = Disconnect());
    }

    private ComputerLink NewLink()
    {
        var settings = _main.Host.Settings;
        if (string.IsNullOrEmpty(settings.TogetherId))
        {
            settings.TogetherId = Guid.NewGuid().ToString("N");
            settings.Save();
        }
        _myName = DeviceName();
        var me = new Member { Id = settings.TogetherId, Name = _myName, Version = AppInfo.VersionText };
        var link = new ComputerLink(me, AskAsync, a => _ui.BeginInvoke(a));
        link.StatusChanged += () => OnStatus(link);
        link.Message += m =>
        {
            if (_link == link) OnMessage(m);
        };
        link.Ended += why => OnEnded(link, why);
        return link;
    }

    // How the computers list this phone: its name (the one in the phone's settings, where there is one) and the profile.
    private string DeviceName()
    {
        string device = "";
#if ANDROID_APP
        try { device = Android.Provider.Settings.Global.GetString(Android.App.Application.Context.ContentResolver, "device_name") ?? ""; }
        catch { }
        if (device.Length == 0) device = Android.OS.Build.Model ?? "Android";
#elif IOS_APP
        try { device = UIKit.UIDevice.CurrentDevice.Name; }
        catch { }
        if (device.Length == 0) device = "iPhone";
#else
        device = Environment.MachineName;
#endif
        return $"{device} · {_main.Profile.Info.Name}";
    }

    public ICommand DisconnectCommand { get; }
    public LinkStatus Status => _link.Status;
    public bool IsLinked => _link.IsLinked;
    public bool IsReconnecting => _link.Status == LinkStatus.Reconnecting;
    public string ComputerName => _link.OtherName;
    public string MyName => _myName;
    // The phone can't wait for computers (the network can't be used).
    public string? Error => _link.Error;
    public string StatusText => Status switch
    {
        LinkStatus.Connected => _shared?.Audio switch
        {
            LinkAudio.Phone => L.F("Connected to {0}: the music plays on this phone", ComputerName),
            LinkAudio.Both => L.F("Connected to {0}: the music plays on both", ComputerName),
            _ => L.F("Connected to {0}: the music plays there", ComputerName),
        },
        LinkStatus.Reconnecting => L.F("Connection to {0} lost: reconnecting…", ComputerName),
        _ => L.T("Waiting for a computer…"),
    };
    // The row in Settings.
    public string SummaryText => IsLinked ? StatusText : L.T("Not connected");
    internal LinkState State => _state;

    // The page is open: the computers nearby can find this phone.
    public bool Visible
    {
        get => _visible;
        set
        {
            _visible = value;
            _link.Visible = value;
            OnChanged(nameof(Visible), nameof(Error));
        }
    }

    // Being found by a computer means it drives that player: not from a room, so leaving it is asked first.
    public void Open() => _ = OpenAsync();

    private async Task OpenAsync()
    {
        if (!await _main.ConfirmLeaveRoomAsync(L.T("While connected to a computer this phone drives its player, so you'll leave the room."), L.T("Leave and connect"))) return;
        ShowPage?.Invoke();
    }

    private async Task<bool> AskAsync(string computer)
        => await Dialogs.ConfirmAsync(L.T("Connect to the computer?"),
            L.F("“{0}” wants to connect. This phone then controls the music on that computer, and each one shows the other's music.", computer),
            L.T("Allow"));

    // The other device's music (made once linked: its first messages may come before the status).
    private SharedLibrary Shared()
    {
        if (_shared == null)
        {
            _shared = new SharedLibrary(_main, _link, true);
            _shared.ModesChanged += OnModes;
            _main.AttachShared(_shared);
        }
        return _shared;
    }

    private void OnStatus(ComputerLink link)
    {
        if (_link != link || _shut) return;
        if (link.Status == LinkStatus.Connected)
        {
            if (!_remote)
            {
                _remote = true;
                _state = new LinkState();
                _rows = new List<LinkRow>();
                _posBase = 0;
                _posAt = Environment.TickCount64;
                _main.Player.EnterRemote(this);
                _main.Toast(L.F("Connected to {0}: the music plays there", link.OtherName));
            }
            // (again after a reconnection: what changed meanwhile wasn't sent)
            Shared().Start();
            UpdateFollow();
        }
        else if (link.Status == LinkStatus.Reconnecting) _main.Toast(L.F("Connection to {0} lost: reconnecting…", link.OtherName));
        Changed();
    }

    private void OnEnded(ComputerLink link, string? why)
    {
        if (_link != link || _shut) return;
        Leave();
        if (why != null) _main.Toast(why);
        // Ready for the next one (still visible if the page is open).
        _link = NewLink();
        _link.Visible = _visible;
        Changed();
    }

    private void Leave()
    {
        StopFollow();
        if (_remote)
        {
            _remote = false;
            _main.Player.LeaveRemote();
        }
        foreach (var t in _standIns.Values) _main.ForgetVm(t.Id);
        _standIns.Clear();
        if (_shared is { } s)
        {
            _shared = null;
            s.ModesChanged -= OnModes;
            _main.DetachShared(s);
        }
    }

    private void Changed()
    {
        OnChanged(nameof(Status), nameof(IsLinked), nameof(IsReconnecting), nameof(ComputerName), nameof(StatusText), nameof(SummaryText), nameof(Error));
        _main.Player.OnRemoteLink();
    }

    // Whose music and where it's heard changed (here or on the computer).
    private void OnModes()
    {
        UpdateFollow();
        Changed();
    }

    public async Task Disconnect()
    {
        var name = ComputerName;
        bool linked = IsLinked;
        await _link.DisconnectAsync();
        if (linked) _main.Toast(L.F("Disconnected from {0}", name));
    }

    // The profile closes (another one, the language, the app): the computer hears it at once.
    public void Shutdown()
    {
        _shut = true;
        Leave();
        _link.Dispose();
    }

    // ------------------------------------------------------------------ the computer's player, shown here

    // Where the computer's song is now.
    public double Position
    {
        get
        {
            double pos = _posBase;
            if (_state.Playing && !_state.Waiting) pos += (Environment.TickCount64 - _posAt) / 1000.0 * _state.Speed;
            return Math.Clamp(pos, 0, _state.Duration > 0 ? _state.Duration : double.MaxValue);
        }
    }

    // "Next up" of the computer: its place there and the song.
    internal IEnumerable<(LinkRow Row, TrackViewModel Track)> Rows() => _rows.Select(r => (r, Vm(r.T)!));

    private void OnMessage(Msg m)
    {
        if (Shared().OnMessage(m)) return;
        switch (m.T)
        {
            case "state" when m.State != null:
                bool other = m.State.Current?.Id != _state.Current?.Id;
                _state = m.State;
                _posBase = m.State.Position;
                _posAt = Environment.TickCount64;
                _main.Player.RemoteState(Vm(_state.Current), _state);
                if (other) DropFiles();
                break;
            case "queue" when m.Rows != null:
                _rows = m.Rows;
                _main.Player.OnRemoteQueue();
                break;
            case "pack":
                _ = ReceivePack(m.Text);
                break;
        }
    }

    // The phone's own song when it came from here, one of the computer's shown here (or both have), otherwise a stand-in
    // with what the computer said of it.
    private TrackViewModel? Vm(LinkTrack? lt)
    {
        if (lt == null) return null;
        if (lt.Yours != null && (_main.Library.Get(lt.Yours) ?? _main.Radio.Track(lt.Yours)) is { } mine) return _main.Vm(mine);
        if (_shared?.Here(lt.Id) is { } here && _main.Get(here) is { } shown) return _main.Vm(shown);
        if (_shared?.FindLocal(lt) is { } same) return _main.Vm(same);
        if (!_standIns.TryGetValue(lt.Id, out var t))
        {
            t = new Track
            {
                Id = "pc-" + lt.Id, Title = lt.Title, Artist = lt.Artist, Album = lt.Album, Duration = lt.Duration, SourceUrl = lt.SourceUrl, Site = lt.Site,
                Keys = lt.Keys.Append(LinkTrack.ComputerKey(lt.Id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ArtUrl = lt.ArtUrl,
                Loudness = lt.Loudness, Peak = lt.Peak, Bpm = lt.Bpm, AudioQuality = lt.Quality,
                // (its cover comes from the computer when shown: TrackViewModel, SharedLibrary.CoverReady)
                HasCover = lt.Cover, CoverVersion = lt.CoverVersion,
            };
            _standIns[lt.Id] = t;
        }
        var vm = _main.Vm(t);
        if (lt.Wave != null && t.Wave == null)
        {
            t.Wave = lt.Wave;
            vm.Refresh();
        }
        if (lt.Duration > 0 && t.Duration <= 0) t.Duration = lt.Duration;
        return vm;
    }

    // ------------------------------------------------------------------ the music heard here too

    // On while connected with the music heard on the phone (or on both).
    private void UpdateFollow()
    {
        if (_remote && _shared is { Audio: LinkAudio.Phone or LinkAudio.Both })
        {
            if (!_follow.IsEnabled) _follow.Start();
        }
        else StopFollow();
    }

    private void StopFollow()
    {
        if (!_follow.IsEnabled && _files.Count == 0 && _main.Player.RoomLoadedId == null) return;
        _follow.Stop();
        _sync.Reset();
        if (_remote) _main.Player.RoomStop();
        foreach (var f in _files.Values) Delete(f);
        _files.Clear();
        _failedUntil.Clear();
    }

    // A few times a second: the computer's song open here, playing where it is there (by the computer's clock).
    private void Follow()
    {
        var p = _main.Player;
        if (!_follow.IsEnabled || _link.ClockSamples == 0 || _state.At == 0) return;
        if (_state.Current is not { } cur || Vm(cur) is not { } vm)
        {
            if (p.RoomLoadedId != null) p.RoomStop();
            return;
        }
        p.RoomSpeed(_state.Speed, _state.Pitch);
        bool playing = _state.Playing && !_state.Waiting && _link.Status == LinkStatus.Connected;
        double target = _state.Position + (playing ? (_link.OtherClock - _state.At) / 1000.0 * _state.Speed : 0);
        string path = "";
        if (p.RoomLoadedId != cur.Id)
        {
            if (FileFor(cur) is not { } file)
            {
                // (still coming from the computer: the song before doesn't go on meanwhile)
                p.RoomPlay(false);
                return;
            }
            path = file;
        }
        _sync.Follow(cur.Id, vm, path, Math.Max(0, target), playing, _state.Speed, Follow);
        // The next one on its way while this one plays.
        if (p.RoomLoadedId == cur.Id && _rows.FirstOrDefault()?.T is { } next) FileFor(next);
    }

    // The file of a song of the computer to play here: the phone's own, or the one sent from the computer (asked once; again
    // after a while when it couldn't).
    private string? FileFor(LinkTrack lt)
    {
        var mine = lt.Yours != null ? _main.Library.Get(lt.Yours) ?? _main.Radio.Track(lt.Yours) : _shared?.FindLocal(lt);
        if (mine != null && new[] { mine.HasSavedFile ? mine.Path : null, _main.Radio.PathFor(mine.Id), mine.CachePath }.FirstOrDefault(File.Exists) is { } own)
            return own;
        if (_files.TryGetValue(lt.Id, out var got)) return got;
        if (_shared == null || _fetching.Contains(lt.Id) || _failedUntil.GetValueOrDefault(lt.Id) > Environment.TickCount64) return null;
        _fetching.Add(lt.Id);
        _ = Fetch(_shared, lt.Id);
        return null;
    }

    private async Task Fetch(SharedLibrary shared, string pcId)
    {
        var path = await shared.FetchSong(pcId);
        _fetching.Remove(pcId);
        if (path == null)
        {
            _failedUntil[pcId] = Environment.TickCount64 + 3000;
            return;
        }
        if (_shared != shared || !_follow.IsEnabled) Delete(path);
        else
        {
            _files[pcId] = path;
            DropFiles();
        }
    }

    // Only the files of the song playing there, the one open here and the next one stay.
    private void DropFiles()
    {
        var keep = new HashSet<string?> { _state.Current?.Id, _rows.FirstOrDefault()?.T.Id, _main.Player.RoomLoadedId };
        foreach (var id in _files.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            Delete(_files[id]);
            _files.Remove(id);
        }
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    // ------------------------------------------------------------------ the phone's buttons, for the computer

    // What a button changes shows at once (the computer's answer confirms it).
    public void Do(string what, int index = 0, double pos = 0, bool flag = false, int max = 0)
    {
        switch (what)
        {
            case "toggle":
                Hold();
                _state.Playing = !_state.Playing;
                break;
            case "playing":
                Hold();
                _state.Playing = flag;
                break;
            case "seek":
                Rebase(pos);
                break;
            case "shuffle": _state.Shuffle = flag; break;
            case "repeat": _state.Repeat = (RepeatMode)index; break;
            case "auto": _state.AutoQueue = flag; break;
            case "volume":
                _state.Volume = pos;
                if (pos > 0) _state.Muted = false;
                break;
            case "mute": _state.Muted = flag; break;
            case "speed":
                Hold();
                _state.Speed = pos;
                _state.Pitch = flag;
                break;
        }
        _link.Send(new Msg { T = what, Index = index, Pos = pos, Flag = flag, Max = max });
        if (_remote) _main.Player.RemoteState(_main.Player.Current, _state);
    }

    // Where the song is now becomes the base (before its speed or its playing changes).
    private void Hold() => Rebase(Position);

    // (the music heard here follows the same point until the computer answers)
    private void Rebase(double pos)
    {
        _posBase = pos;
        _posAt = Environment.TickCount64;
        _state.Position = pos;
        if (_link.ClockSamples > 0) _state.At = (long)_link.OtherClock;
    }

    // A song as the computer needs it (its own id there when known).
    private LinkTrack Describe(Track t) => _shared?.Describe(t) ?? LinkTrack.From(t);

    // A list from a song (index -1: all of it, shuffled if flag; otherwise flag: the rest follows anyway).
    public void PlayList(IEnumerable<Track> tracks, int index, string? name, bool flag)
        => _link.Send(new Msg { T = "play", Tracks = tracks.Select(Describe).ToList(), Index = index, Text = name, Flag = flag });

    public void PlaySingle(Track t) => _link.Send(new Msg { T = "single", Tracks = new() { Describe(t) } });

    public void PlayRadio(Track t, bool artist) => _link.Send(new Msg { T = "radio", Tracks = new() { Describe(t) }, Flag = artist });

    public void Enqueue(Track t, bool next) => _link.Send(new Msg { T = "enqueue", Tracks = new() { Describe(t) }, Flag = next });

    // ------------------------------------------------------------------ music from the computer

    private async Task ReceivePack(string? from)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "ump-link-" + Ids.New() + ".json");
        try
        {
            if (!await FetchEntry(LinkPack.Entry, tmp, null, CancellationToken.None)) return;
            var pack = JsonSerializer.Deserialize<LinkPack>(await File.ReadAllTextAsync(tmp), JsonStore.Options);
            if (pack == null) return;
            var link = _link;
            _main.OpenPack(PackFile.Remote(from ?? ComputerName, pack.Manifest, pack.Size, pack.Entries, FetchEntry, () =>
            {
                if (_link == link) link.Send(new Msg { T = "packDone" });
            }));
        }
        catch (Exception ex) { _main.Toast(L.T("Couldn't receive the music:") + " " + ex.Message); }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    // An entry of the computer's music, into target (PackFile.Remote): false when the computer hasn't got it, an
    // exception when it's gone.
    private async Task<bool> FetchEntry(string entry, string target, Action<long>? copied, CancellationToken ct)
    {
        var link = _link;
        if (link.Connection == null) throw new IOException(L.T("The computer isn't connected any more."));
        var id = "e" + Interlocked.Increment(ref _req);
        long last = 0;
        // (the import counts on its own thread)
        Action<long, long>? progress = copied == null ? null : (got, _) =>
        {
            long n = got - last;
            last = got;
            _ui.BeginInvoke(() => copied(n));
        };
        var wait = link.Files.ReceiveAsync(id, _ => target, progress, ct);
        link.Send(new Msg { T = "entry", Id = id, Text = entry });
        if (await wait is { } path)
        {
            // what the last chunks added
            progress?.Invoke(new FileInfo(path).Length, 0);
            return true;
        }
        ct.ThrowIfCancellationRequested();
        if (_link != link || link.Status != LinkStatus.Connected) throw new IOException(L.T("The computer isn't connected any more."));
        return false;
    }
}
