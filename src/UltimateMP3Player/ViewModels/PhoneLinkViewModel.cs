using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using UltimateMP3Player.Audio;
using UltimateMP3Player.Core;
using UltimateMP3Player.Core.Link;
using UltimateMP3Player.Core.Together;
using UltimateMP3Player.Services;

namespace UltimateMP3Player.ViewModels;

// A phone in "Connect to computer", found nearby.
public sealed class PhoneRowViewModel : Observable
{
    private readonly PhoneLinkViewModel _owner;

    public PhoneRowViewModel(PhoneAd ad, PhoneLinkViewModel owner)
    {
        Ad = ad;
        _owner = owner;
        ConnectCommand = new RelayCommand(() => _ = owner.Connect(Ad), () => !owner.IsConnecting);
    }

    public PhoneAd Ad { get; private set; }
    // Scans in a row it didn't answer.
    public int Missed { get; set; }
    public string Name => Ad.Name;
    public bool ViaCable => Ad.Usb > 0 || Ad.Cable;
    public string Detail => Ad.Linked != null ? L.F("Connected to {0}", Ad.Linked)
        : ViaCable ? L.T("Through the cable: no confirmation needed")
        : L.F("On the network · {0}", Ad.Ip);
    public ICommand ConnectCommand { get; }

    public void Update(PhoneAd ad)
    {
        Ad = ad;
        OnChanged(nameof(Name), nameof(ViaCable), nameof(Detail));
    }
}

// "Connect phone", on the computer: the phones nearby in "Connect to computer" (looked for while the window is open),
// the one connected and the bridge between it and the player. The phone shows what plays here and drives it as if it
// were this computer's own buttons; each device shows the other's music too (SharedLibrary), its songs come here from it
// (those in its cloud from their links, downloaded here). The computer works as always meanwhile: what the phone does is
// what its own buttons would do; with the music heard on the phone only, this player goes on silent.
public sealed class PhoneLinkViewModel : Observable
{
    // Rows of "next up" the phone gets, and the first ones whose covers it gets from the phone.
    private const int QueueRows = 150, CoverRows = 30;

    private readonly MainViewModel _main;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _push;
    private PhoneLink? _link;
    private bool _connectedOnce;
    private CancellationTokenSource? _scan;
    private LinkState? _sent;
    private long _sentAt;
    private string? _sentRows;
    private int _req;
    // What "To the phone" offers: entry → file (and the temporary description).
    private Dictionary<string, string>? _pack;
    private string? _packFile;
    // Songs of the phone whose cover was asked for (once per connection).
    private readonly HashSet<string> _coversAsked = new();
    private SharedLibrary? _shared;

    public PhoneLinkViewModel(MainViewModel main)
    {
        _main = main;
        _ui = Application.Current.Dispatcher;
        _push = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _push.Tick += (_, _) => Push();
        DisconnectCommand = new RelayCommand(() => _ = Disconnect());
        main.Radio.FromPhone = FetchAsync;
    }

    // Opens the "Connect phone" window (set by the window of the computer apps).
    public static Action<MainViewModel>? ShowWindow { get; set; }

    public ObservableCollection<PhoneRowViewModel> Phones { get; } = new();
    public bool NoPhones => Phones.Count == 0;
    public ICommand DisconnectCommand { get; }

    public LinkStatus Status => _link?.Status ?? LinkStatus.None;
    public bool IsLinked => _link?.IsLinked == true;
    public bool IsConnecting => Status == LinkStatus.Connecting;
    public bool IsReconnecting => Status == LinkStatus.Reconnecting;
    public bool HasPhone => _link != null;
    public string? PhoneName => _link?.OtherName;
    public string StatusText => Status switch
    {
        LinkStatus.Connecting => L.T("Waiting for the phone to allow it…"),
        LinkStatus.Connected => (_link!.ViaCable ? L.T("Connected through the cable: the phone controls the music") : L.T("Connected: the phone controls the music"))
            + (_shared?.Audio switch
            {
                LinkAudio.Phone => " · " + L.T("heard on the phone"),
                LinkAudio.Both => " · " + L.T("heard on both"),
                _ => "",
            }),
        LinkStatus.Reconnecting => L.T("Connection lost: reconnecting…"),
        _ => L.T("Not connected"),
    };
    // Next to the song in the player bar while a phone is connected.
    public string BarTip => IsReconnecting ? L.F("Connection to {0} lost: reconnecting…", PhoneName)
        : L.F("Connected to {0}: it controls the music on this computer (click for the options)", PhoneName);

    private void Changed()
    {
        OnChanged(nameof(Status), nameof(IsLinked), nameof(IsConnecting), nameof(IsReconnecting), nameof(HasPhone), nameof(PhoneName),
            nameof(StatusText), nameof(BarTip));
        CommandManager.InvalidateRequerySuggested();
        _main.OnPhoneLinkChanged();
    }

    // ------------------------------------------------------------------ finding the phones

    // While the "Connect phone" window is open.
    public void StartScan()
    {
        if (_scan != null) return;
        var cts = _scan = new CancellationTokenSource();
        _ = ScanLoop(cts);
    }

    public void StopScan()
    {
        _scan?.Cancel();
        _scan = null;
        Phones.Clear();
        OnChanged(nameof(NoPhones));
    }

    private async Task ScanLoop(CancellationTokenSource cts)
    {
        while (!cts.IsCancellationRequested)
        {
            List<PhoneAd> found;
            try { found = await PhoneLink.ScanAsync(cts.Token); }
            catch (OperationCanceledException) { break; }
            catch { found = new List<PhoneAd>(); }
            if (cts.IsCancellationRequested) break;
            // (the one connected is shown on its own)
            found.RemoveAll(a => a.Id == _link?.OtherId);
            foreach (var ad in found)
            {
                if (Phones.FirstOrDefault(p => p.Ad.Id == ad.Id) is { } row)
                {
                    row.Missed = 0;
                    row.Update(ad);
                }
                else Phones.Add(new PhoneRowViewModel(ad, this));
            }
            // Gone after two scans without an answer (one can get lost).
            foreach (var row in Phones.Where(p => found.All(a => a.Id != p.Ad.Id)).ToList())
                if (++row.Missed >= 2 || row.Ad.Id == _link?.OtherId) Phones.Remove(row);
            OnChanged(nameof(NoPhones));
            try { await Task.Delay(1500, cts.Token); }
            catch { break; }
        }
    }

    // ------------------------------------------------------------------ connecting

    public async Task Connect(PhoneAd ad)
    {
        if (IsConnecting) return;
        if (!_main.ConfirmLeaveRoom(L.T("While a phone is connected it drives this computer's player, so you'll leave the room."), L.T("Leave and connect"))) return;
        if (_link != null) await Disconnect();
        var settings = _main.Host.Settings;
        if (string.IsNullOrEmpty(settings.TogetherId))
        {
            settings.TogetherId = Guid.NewGuid().ToString("N");
            settings.Save();
        }
        var me = new Member { Id = settings.TogetherId, Name = Environment.MachineName, Version = AppInfo.VersionText };
        var link = _link = new PhoneLink(ad, me, a => _ui.BeginInvoke(a));
        _connectedOnce = false;
        link.StatusChanged += () => OnStatus(link);
        link.Message += m =>
        {
            if (_link == link) OnMessage(link, m);
        };
        link.Ended += why => OnEnded(link, why);
        Phones.Remove(Phones.FirstOrDefault(p => p.Ad.Id == ad.Id)!);
        Changed();
        var error = await link.ConnectAsync();
        if (error != null) Dialogs.Alert(L.F("Couldn't connect to {0}", ad.Name), error);
    }

    public async Task Disconnect()
    {
        if (_link is not { } link) return;
        var name = link.OtherName;
        bool linked = link.IsLinked;
        await link.DisconnectAsync();
        if (linked) _main.Toast(L.F("{0} disconnected", name));
    }

    // The profile closes (another one, the language, the app): the phone hears it at once.
    public void Shutdown()
    {
        _scan?.Cancel();
        _push.Stop();
        _main.Radio.FromPhone = null;
        var link = _link;
        _link = null;
        link?.Dispose();
        DropPack();
        DropShared();
    }

    // The phone's music (made once linked: its first messages may come before the status).
    private SharedLibrary Shared(PhoneLink link)
    {
        if (_shared == null)
        {
            _shared = new SharedLibrary(_main, link, false);
            _shared.ModesChanged += OnModes;
            _main.AttachShared(_shared);
        }
        return _shared;
    }

    private void DropShared()
    {
        _main.Player.LinkSilent = false;
        if (_shared is not { } s) return;
        _shared = null;
        s.ModesChanged -= OnModes;
        _main.DetachShared(s);
    }

    // Where the music is heard changed (here or on the phone): with the phone only, this player goes on silent.
    private void OnModes()
    {
        _main.Player.LinkSilent = _shared?.Audio == LinkAudio.Phone && _link?.Status == LinkStatus.Connected;
        Changed();
    }

    private void OnStatus(PhoneLink link)
    {
        if (_link != link) return;
        switch (link.Status)
        {
            case LinkStatus.Connected:
                _sent = null;
                _sentRows = null;
                _coversAsked.Clear();
                _push.Start();
                _main.Toast(_connectedOnce ? L.F("{0} is back", link.OtherName) : L.F("{0} connected: it controls the music on this computer", link.OtherName));
                _connectedOnce = true;
                // The songs that couldn't come while it was away.
                _main.Radio.RetryPhone();
                // (again after a reconnection: what changed meanwhile wasn't sent)
                Shared(link).Start();
                Push();
                break;
            case LinkStatus.Reconnecting:
                _push.Stop();
                _main.Toast(L.F("Connection to {0} lost: reconnecting…", link.OtherName));
                break;
        }
        // (heard here meanwhile: the phone can't play along now)
        _main.Player.LinkSilent = _shared?.Audio == LinkAudio.Phone && link.Status == LinkStatus.Connected;
        Changed();
    }

    private void OnEnded(PhoneLink link, string? why)
    {
        if (_link != link) return;
        _link = null;
        _push.Stop();
        DropPack();
        DropShared();
        if (why != null && _connectedOnce) _main.Toast(why);
        Changed();
    }

    // ------------------------------------------------------------------ the player → the phone

    // A few times a second: what changed goes to the phone (where the song is only when it's not where the phone
    // expects it, and every few seconds anyway).
    private void Push()
    {
        if (_link?.Connection is not { } c) return;
        var state = BuildState();
        long now = Environment.TickCount64;
        bool send = _sent == null || !state.SameAs(_sent) || now - _sentAt > 5000;
        if (!send)
        {
            double expected = _sent!.Playing && !_sent.Waiting ? _sent.Position + (now - _sentAt) / 1000.0 * _sent.Speed : _sent.Position;
            send = Math.Abs(expected - state.Position) > 0.6;
        }
        if (send)
        {
            c.Send(new Msg { T = "state", State = state });
            _sent = state;
            _sentAt = now;
        }
        var rows = BuildRows();
        var json = JsonSerializer.Serialize(rows, Msg.Json);
        if (json != _sentRows)
        {
            _sentRows = json;
            c.Send(new Msg { T = "queue", Rows = rows });
        }
        AskCovers(rows);
    }

    private LinkState BuildState()
    {
        var p = _main.Player;
        var waiting = p.WaitingId;
        // Where the song is heard (what the player has read minus its output's delay): the phone playing along aims there.
        double position = p.IsPlaying ? Math.Max(0, p.LivePosition - (AudioEngine.LatencyMs + AudioEngine.DeviceLatencyMs) / 1000.0 * p.Speed) : p.LivePosition;
        return new LinkState
        {
            Current = p.Current is { } c ? Describe(c.T, true) : null,
            Playing = p.PlaysSoon, Position = position, Duration = p.Duration, At = LinkEnd.Clock,
            Waiting = waiting != null, WaitMore = p.WaitingMore, WaitPct = waiting != null ? _main.Radio.StatusOf(waiting)?.Pct ?? 0 : 0,
            Speed = p.Speed, Pitch = p.SpeedPitch, Shuffle = p.Shuffle, Repeat = p.Repeat, AutoQueue = p.AutoQueue,
            Volume = p.Volume, Muted = p.Muted, Context = p.ContextName, Radio = p.RadioQueue, Upcoming = p.UpcomingCount,
        };
    }

    private List<LinkRow> BuildRows()
    {
        var p = _main.Player;
        var rows = new List<LinkRow>();
        int index = 0;
        foreach (var (id, queued) in p.UpcomingIds(QueueRows))
        {
            if (p.TrackFor(id) is { } t)
            {
                var st = _main.Radio.StatusOf(id);
                rows.Add(new LinkRow { Index = index, T = Describe(t, false), Queued = queued, State = st?.State ?? FileState.None, Pct = st?.Pct ?? 0 });
            }
            index++;
        }
        return rows;
    }

    // A song as the phone needs it; one that came from the phone says which one it is there.
    private LinkTrack Describe(Track t, bool wave)
    {
        var lt = _shared?.Describe(t) ?? LinkTrack.From(t);
        lt.Yours ??= t.Keys.FirstOrDefault(k => k.StartsWith("phone:", StringComparison.Ordinal))?[6..];
        if (wave) lt.Wave = t.Wave;
        return lt;
    }

    // The covers of the phone's songs about to be seen (the one playing, the first ones of "next up"), from the phone.
    private void AskCovers(List<LinkRow> rows)
    {
        var ids = new List<string>();
        if (_main.Player.Current is { } c) ids.Add(c.Id);
        ids.AddRange(_main.Player.UpcomingIds(CoverRows).Select(x => x.Id));
        foreach (var id in ids)
        {
            if (_main.Radio.Item(id) is not { T.HasCover: false } item || item.T.Keys.FirstOrDefault(k => k.StartsWith("phone:", StringComparison.Ordinal)) is not { } key) continue;
            if (_coversAsked.Add(id)) _ = FetchCover(id, key[6..]);
        }
    }

    // (through SharedLibrary: the same request as the phone's songs shown here)
    private async Task FetchCover(string id, string phoneId)
    {
        if (_shared is not { } s || await s.CoverFile(phoneId) is not { } file) return;
        try { File.Copy(file, AppPaths.TrackCover(id), true); }
        catch { return; }
        _main.Radio.CoverArrived(id);
    }

    // ------------------------------------------------------------------ the phone → the player

    private void OnMessage(PhoneLink link, Msg m)
    {
        if (Shared(link).OnMessage(m)) return;
        var p = _main.Player;
        switch (m.T)
        {
            case "play":
                // A list (from a song, or all of it: Index -1, shuffled if Flag), a song on its own, songs like it.
                var ids = _main.Radio.AddFromPhone(m.Tracks ?? new List<LinkTrack>(), link.OtherName);
                if (ids.Count > 0) p.PlayPhone(ids, m.Index, m.Text ?? link.OtherName, m.Flag);
                break;
            case "single":
                if (First(m, link) is { } single) p.PlaySingle(single);
                break;
            case "radio":
                if (First(m, link) is { } seed)
                {
                    if (m.Flag) p.PlayArtist(_main.Vm(seed));
                    else p.PlayRadio(_main.Vm(seed));
                }
                break;
            case "enqueue":
                foreach (var id in _main.Radio.AddFromPhone(m.Tracks ?? new List<LinkTrack>(), link.OtherName))
                    if (p.TrackFor(id) is { } t)
                    {
                        if (m.Flag) p.PlayNext(_main.Vm(t));
                        else p.Enqueue(_main.Vm(t));
                    }
                break;
            case "toggle": p.PlayPause(); break;
            case "playing":
                if (m.Flag) p.Play();
                else p.Pause();
                break;
            case "next": _ = p.Next(false); break;
            case "prev": _ = p.Previous(m.Flag); break;
            case "seek": p.Seek(m.Pos); break;
            case "jump": _ = p.JumpTo(m.Index); break;
            case "remove": p.RemoveUpcoming(m.Index); break;
            // (Max: where it goes)
            case "move": p.MoveUpcoming(m.Index, m.Max); break;
            case "shuffle": p.Shuffle = m.Flag; break;
            case "repeat": p.Repeat = (RepeatMode)m.Index; break;
            case "auto": p.AutoQueue = m.Flag; break;
            case "volume": p.Volume = m.Pos; break;
            case "mute": p.Muted = m.Flag; break;
            case "speed":
                p.Speed = m.Pos;
                p.SpeedPitch = m.Flag;
                break;
            case "clear":
                if (p.ClearQueueCommand.CanExecute(null)) p.ClearQueueCommand.Execute(null);
                break;
            case "generate":
                if (p.GenerateQueueCommand.CanExecute(null)) p.GenerateQueueCommand.Execute(null);
                break;
            case "entry":
                if (m.Id is { Length: > 0 and < 64 } req && link.Connection is { } ec)
                    _ = FileLane.SendAsync(ec, req, m.Text != null && _pack?.GetValueOrDefault(m.Text) is { } file ? file : null);
                break;
            case "packDone":
                DropPack();
                break;
        }
        // The answer at once, not at the next tick.
        Push();
    }

    private Track? First(Msg m, PhoneLink link)
        => _main.Radio.AddFromPhone((m.Tracks ?? new List<LinkTrack>()).Take(1), link.OtherName).FirstOrDefault() is { } id ? _main.Player.TrackFor(id) : null;

    // A song of the phone, from the phone (RadioFetcher.FromPhone): waited for while the phone is away for a moment.
    private async Task<string?> FetchAsync(RadioSong s, string dir, Action<double> progress, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            while (_link is { Status: LinkStatus.Reconnecting or LinkStatus.Connecting }) await Task.Delay(500, ct);
            if (_link is not { Connection: not null } link) return null;
            var id = "f" + ++_req;
            var wait = link.Files.ReceiveAsync(id, ext => Path.Combine(dir, Ids.New() + ext), (got, size) => progress(size > 0 ? got * 100.0 / size : 0), ct);
            link.Send(new Msg { T = "want", Id = s.Phone, Text = id });
            if (await wait is { } path) return path;
            // It answered that it hasn't got it: no use asking again.
            if (_link != link || link.Status != LinkStatus.Reconnecting) return null;
        }
        return null;
    }

    // ------------------------------------------------------------------ "To the phone" (the export window)

    // The phone opens its import window and asks for the files it doesn't have, one at a time.
    public bool SendPack(PackExport e)
    {
        if (_link?.Connection is not { } c) return false;
        DropPack();
        var (manifest, entries) = PackWriter.Describe(_main.Library, _main.Profile, e, AppInfo.VersionText);
        var pack = entries.GroupBy(x => x.Entry).ToDictionary(g => g.Key, g => g.First().File);
        var description = new LinkPack { Manifest = manifest, Entries = pack.Keys.ToList(), Size = pack.Values.Sum(PackWriter.SizeOf) };
        var file = Path.Combine(Path.GetTempPath(), "ump-link-" + Ids.New() + ".json");
        try { File.WriteAllText(file, JsonSerializer.Serialize(description, JsonStore.Options)); }
        catch (Exception ex)
        {
            Dialogs.Alert(L.T("Couldn't send the music"), ex.Message);
            return false;
        }
        pack[LinkPack.Entry] = file;
        _pack = pack;
        _packFile = file;
        c.Send(new Msg { T = "pack", Text = Environment.MachineName });
        _main.Toast(L.F("Sent to {0}: choose there what to import", _link.OtherName));
        return true;
    }

    private void DropPack()
    {
        _pack = null;
        if (_packFile != null) try { File.Delete(_packFile); } catch { }
        _packFile = null;
    }
}
