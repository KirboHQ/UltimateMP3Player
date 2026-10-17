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
// what plays there and its buttons work there, its own sound off. Its songs go there (the saved ones from here, the ones
// in its cloud downloaded there). The computer can also send music here ("To the phone" in its export window): the
// usual import window, the files coming one at a time.
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
    // The computer's songs shown here ("pc-" + their id there).
    private readonly Dictionary<string, Track> _standIns = new();
    private readonly HashSet<string> _coversAsked = new();
    // The computer's songs kept on this phone: their id there → the library song here.
    private readonly Dictionary<string, string> _kept = new();

    // Opens the "Connect to computer" page (set by the phone's screen).
    public static Action? ShowPage { get; set; }

    public ComputerLinkViewModel(MainViewModel main)
    {
        _main = main;
        _ui = Application.Current.Dispatcher;
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
        LinkStatus.Connected => L.F("Connected to {0}: the music plays there", ComputerName),
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
            L.F("“{0}” wants to connect. This phone then controls the music on that computer: the songs play there and the phone stays silent.", computer),
            L.T("Allow"));

    private void OnStatus(ComputerLink link)
    {
        if (_link != link || _shut) return;
        if (link.Status == LinkStatus.Connected && !_remote)
        {
            _remote = true;
            _state = new LinkState();
            _rows = new List<LinkRow>();
            _posBase = 0;
            _posAt = Environment.TickCount64;
            _main.Player.EnterRemote(this);
            _main.Toast(L.F("Connected to {0}: the music plays there", link.OtherName));
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
        if (!_remote) return;
        _remote = false;
        _main.Player.LeaveRemote();
        foreach (var t in _standIns.Values)
        {
            _main.ForgetVm(t.Id);
            try { File.Delete(AppPaths.TrackCover(t.Id)); } catch { }
        }
        _standIns.Clear();
        _coversAsked.Clear();
        _kept.Clear();
    }

    private void Changed()
    {
        OnChanged(nameof(Status), nameof(IsLinked), nameof(IsReconnecting), nameof(ComputerName), nameof(StatusText), nameof(SummaryText), nameof(Error));
        _main.Player.OnRemoteLink();
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

    // A song of the computer that isn't one of the phone's.
    public bool IsStandIn(TrackViewModel? vm) => vm != null && vm.Id.StartsWith("pc-", StringComparison.Ordinal) && _main.Library.Get(vm.Id) == null;

    private void OnMessage(Msg m)
    {
        switch (m.T)
        {
            case "state" when m.State != null:
                _state = m.State;
                _posBase = m.State.Position;
                _posAt = Environment.TickCount64;
                _main.Player.RemoteState(Vm(_state.Current), _state);
                break;
            case "queue" when m.Rows != null:
                _rows = m.Rows;
                _main.Player.OnRemoteQueue();
                break;
            case "want":
                _ = SendSong(m.Id, m.Text);
                break;
            case "cover":
                if (m.Id is { Length: > 0 and < 64 } id && _link.Connection is { } c)
                    _ = FileLane.SendAsync(c, "c:" + id, File.Exists(AppPaths.TrackCover(id)) ? AppPaths.TrackCover(id) : null);
                break;
            case "pack":
                _ = ReceivePack(m.Text);
                break;
            case "take":
                // Kept from the computer's side, on this phone too (its Save or heart).
                if (m.Tracks?.FirstOrDefault() is { } lt && Vm(lt) is { } taken) _ = KeepHere(taken, lt.Id, m.Flag);
                break;
        }
    }

    // The phone's own song when it came from here, otherwise a stand-in with what the computer said of it.
    private TrackViewModel? Vm(LinkTrack? lt)
    {
        if (lt == null) return null;
        if (lt.Phone != null && (_main.Library.Get(lt.Phone) ?? _main.Radio.Track(lt.Phone)) is { } mine) return _main.Vm(mine);
        // (kept here, or already in the library: the same link)
        if (_kept.TryGetValue(lt.Id, out var keptId) && _main.Library.Get(keptId) is { } kept) return _main.Vm(kept);
        if (lt.Keys.Count > 0 && _main.Library.FindByKeys(lt.Keys) is { } same) return _main.Vm(same);
        if (!_standIns.TryGetValue(lt.Id, out var t))
        {
            var id = "pc-" + lt.Id;
            t = new Track
            {
                Id = id, Title = lt.Title, Artist = lt.Artist, Album = lt.Album, Duration = lt.Duration, SourceUrl = lt.SourceUrl, Site = lt.Site,
                Keys = lt.Keys.ToList(), ArtUrl = lt.ArtUrl, Loudness = lt.Loudness, Peak = lt.Peak, Bpm = lt.Bpm, HasCover = File.Exists(AppPaths.TrackCover(id)),
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
        if (lt.Cover && !t.HasCover && _coversAsked.Add(lt.Id)) _ = FetchCover(lt.Id, t);
        return vm;
    }

    private async Task FetchCover(string pcId, Track t)
    {
        if (_link.Connection == null) return;
        var wait = _link.Files.ReceiveAsync("c:" + pcId, _ => AppPaths.TrackCover(t.Id), null, CancellationToken.None);
        _link.Send(new Msg { T = "cover", Id = pcId });
        if (await wait == null) return;
        t.HasCover = true;
        t.CoverVersion++;
        _main.Vm(t).Refresh();
        if (_main.Player.Current?.T == t) _main.Host.OnTrackChanged();
    }

    // The computer wants a song of the phone's to play it: its file (the saved one, or the copy in the cache).
    private async Task SendSong(string? id, string? req)
    {
        if (req is not { Length: > 0 and < 64 } || _link.Connection is not { } c) return;
        var t = id == null ? null : _main.Library.Get(id) ?? _main.Radio.Track(id);
        var path = t == null ? null : t.HasSavedFile ? t.Path : t.CachePath is { } cached && File.Exists(cached) ? cached : null;
        await FileLane.SendAsync(c, req, path);
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
                _posBase = pos;
                _posAt = Environment.TickCount64;
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
    private void Hold()
    {
        _posBase = Position;
        _posAt = Environment.TickCount64;
    }

    // A list from a song (index -1: all of it, shuffled if flag; otherwise flag: the rest follows anyway).
    public void PlayList(IEnumerable<Track> tracks, int index, string? name, bool flag)
        => _link.Send(new Msg { T = "play", Tracks = tracks.Select(LinkTrack.From).ToList(), Index = index, Text = name, Flag = flag });

    public void PlaySingle(Track t) => _link.Send(new Msg { T = "single", Tracks = new() { LinkTrack.From(t) } });

    public void PlayRadio(Track t, bool artist) => _link.Send(new Msg { T = "radio", Tracks = new() { LinkTrack.From(t) }, Flag = artist });

    public void Enqueue(Track t, bool next) => _link.Send(new Msg { T = "enqueue", Tracks = new() { LinkTrack.From(t) }, Flag = next });

    // ------------------------------------------------------------------ keeping a song (Save, the heart)

    // A song this phone hasn't kept (the computer's, or a suggestion): here, on the computer or both, asked each time;
    // from the lock screen (ask false), where nothing can be asked, here.
    public async Task Keep(TrackViewModel vm, bool favorite, bool ask)
    {
        var pcId = PcId(vm);
        int where = !ask ? 0 : await Dialogs.ChooseAsync(favorite ? L.T("Add to Favorites") : L.T("Add to library"),
            L.F("“{0}”: on which device?", vm.Title), L.T("This phone"), L.T("The computer"), L.T("Both"));
        if (where < 0) return;
        if (where != 0 && pcId != null)
        {
            _link.Send(new Msg { T = "keep", Id = pcId, Flag = favorite });
            if (where == 1) _main.Toast(favorite ? L.F("Added to Favorites on {0}", ComputerName) : L.F("Added to the library on {0}", ComputerName));
        }
        if (where != 1) await KeepHere(vm, pcId, favorite);
    }

    // The computer's id of one of its songs shown here.
    private string? PcId(TrackViewModel vm)
    {
        if (vm.Id.StartsWith("pc-", StringComparison.Ordinal)) return vm.Id[3..];
        if (_main.Player.Current == vm && _state.Current is { } cur) return cur.Id;
        return _rows.FirstOrDefault(r => Vm(r.T) == vm)?.T.Id;
    }

    // Into this phone's library: a song of the computer with its file sent from there (no new download), a suggestion of
    // the phone's own as usual. favorite: into the Favorites too.
    private async Task KeepHere(TrackViewModel vm, string? pcId, bool favorite)
    {
        TrackViewModel kept;
        if (IsStandIn(vm) && pcId != null)
        {
            var pc = vm.T;
            var t = _main.Library.FindByKeys(pc.Keys);
            var file = t is { HasSavedFile: true } ? null : await FetchSong(pcId);
            if (t == null)
            {
                if (file == null && !pc.HasLink)
                {
                    _main.Toast(L.F("Couldn't save “{0}”: the computer didn't send it", pc.Title));
                    return;
                }
                t = new Track
                {
                    Title = pc.Title, Artist = pc.Artist, Album = pc.Album, Duration = pc.Duration, SourceUrl = pc.SourceUrl, Site = pc.Site,
                    Keys = pc.Keys.ToList(), ArtUrl = pc.ArtUrl, Wave = pc.Wave, Loudness = pc.Loudness, Peak = pc.Peak, Bpm = pc.Bpm,
                };
                try
                {
                    if (pc.HasCover && File.Exists(AppPaths.TrackCover(pc.Id)))
                    {
                        File.Copy(AppPaths.TrackCover(pc.Id), AppPaths.TrackCover(t.Id), true);
                        t.HasCover = true;
                    }
                }
                catch { }
                _main.Library.Add(t);
            }
            _kept[pcId] = t.Id;
            kept = _main.Vm(t);
            if (file != null)
            {
                if (await _main.SaveFromCache(t, file)) _main.Toast(L.F("“{0}” saved on your device", t.Title));
                try { File.Delete(file); } catch { }
            }
            else if (!t.HasSavedFile) await _main.SaveToDevice(new[] { kept });
        }
        else kept = favorite ? _main.KeepInLibrary(vm) : _main.AddToLibrary(vm);
        if (favorite && !kept.IsFavorite) _main.ToggleFavorite(kept);
        // The phone's own now: the player shows it (the heart, the save button) at once.
        if (_remote) _main.Player.RemoteState(Vm(_state.Current), _state);
    }

    // The file of a song of the computer, into the cache's temporary folder; null when it can't send it.
    private async Task<string?> FetchSong(string pcId)
    {
        var link = _link;
        if (link.Connection == null) return null;
        var dir = _main.Host.SongCache.TempDir;
        Directory.CreateDirectory(dir);
        var req = "s" + Interlocked.Increment(ref _req);
        var wait = link.Files.ReceiveAsync(req, ext => Path.Combine(dir, Ids.New() + ext), null, CancellationToken.None);
        link.Send(new Msg { T = "song", Id = pcId, Text = req });
        return await wait;
    }

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
