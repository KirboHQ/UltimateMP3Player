using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace UltimateMP3Player.Core.Together;

public enum RoomStatus { None, Connecting, Connected, Reconnecting, Closed }

public sealed class RoomOptions
{
    public string Name { get; set; } = "";
    public int Max { get; set; } = 8;
    public string? Password { get; set; }
    public Perm DefaultPerms { get; set; }
}

public sealed class JoinResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public bool NeedsPassword { get; init; }
    public bool Denied { get; init; }
    public Msg? Redirect { get; init; }
}

// One room of "Listen together", from this computer's point of view: as its host (the room's state lives here,
// every request is checked and passed on to everyone) or as a guest (a copy of the state, requests go to the host).
// Every guest also listens for connections, so that when the host leaves (or its PC dies) the most important
// person who came first takes over and the others reconnect to them: the room goes on.
// All state is touched only on the owner's thread (post); the network threads only post to it.
public sealed class TogetherSession : IDisposable
{
    // 2: loop, speed, pause and seek as one permission (3.1).
    public const int Proto = 2;
    public const int FirstPort = 47800, LastPort = 47819;
    public const int MaxQueue = 200;
    // Songs after the one playing that each person gets ready (their choice, 3.1.1).
    public const int DefaultAhead = 2, MaxAhead = 6;
    private const int MaxChat = 200, ChatLength = 500, NameLength = 40;
    private const long WaitForAll = 8000, GiveUp = 90000, PeerTimeout = 12000, HostTimeout = 9000, PingEvery = 2000,
        KeepPlace = 20000, UploadTimeout = 90000;
    private const long MaxFile = 400L << 20;

    private readonly Action<Action> _post;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TcpListener? _listener;
    private int _listenPort;
    private Discovery.Responder? _discovery;
    private volatile RoomAd? _ad;
    private Timer? _timer;
    private Connection? _host;
    private readonly Dictionary<string, Connection> _peers = new();
    private readonly Dictionary<string, long> _keepPlace = new();
    private string? _password;
    private int _seq;
    private bool _leaving, _disposed, _ended;
    private long _lastPing, _lastFilesPush;
    private bool _filesDirty, _myFilesDirty;
    private Dictionary<string, FileStatus> _myFiles = new();
    private TaskCompletionSource<Msg?>? _joinWait;
    private readonly List<(long Rtt, double Off)> _samples = new();
    private double _offset;

    public TogetherSession(Member me, Action<Action> post)
    {
        Me = me;
        _post = post;
    }

    public Member Me { get; }
    public RoomStatus Status { get; private set; }

    public string RoomId { get; private set; } = "";
    public string RoomName { get; private set; } = "";
    public int Max { get; private set; } = 8;
    public bool Locked { get; private set; }
    public string HostId { get; private set; } = "";
    public List<Member> Members { get; private set; } = new();
    public List<RoomTrack> Queue { get; private set; } = new();
    public RoomTrack? Current { get; private set; }
    public Playback Play { get; private set; } = new();
    public List<ChatLine> Chat { get; private set; } = new();
    // Member id → song id → how far that person is with the file.
    public Dictionary<string, Dictionary<string, FileStatus>> Files { get; private set; } = new();
    public HashSet<string> Banned { get; private set; } = new();
    public Perm DefaultPerms { get; private set; }
    public Dictionary<string, byte[]> Covers { get; } = new();
    public int ListenPort => _listenPort;
    public long Latency { get; private set; }

    public bool IsHost => HostId == Me.Id && Status == RoomStatus.Connected;
    public Member? MeInRoom => Members.FirstOrDefault(m => m.Id == Me.Id);
    public Member? Host => Members.FirstOrDefault(m => m.Id == HostId);
    public Perm MyPerms => HostId == Me.Id ? Perm.All : MeInRoom?.Perms ?? Perm.None;
    public bool Can(Perm p) => (MyPerms & p) == p;

    public RoomTrack? Find(string id) => Current?.Id == id ? Current : Queue.FirstOrDefault(t => t.Id == id);

    public FileStatus? StatusOf(string member, string item)
        => Files.TryGetValue(member, out var map) && map.TryGetValue(item, out var s) ? s : null;

    // How many songs of the queue this person gets ready: the host its "as the host" choice.
    public int AheadOf(Member m) => Math.Clamp((m.Id == HostId ? m.HostAhead : m.Ahead) is > 0 and var n ? n : DefaultAhead, 1, MaxAhead);

    // The host sends the songs to whoever takes them from it first (both chose so).
    public bool HostSendsFiles => Host is { SendsFiles: true, NoP2P: false, Away: false } && HostId != Me.Id;
    // Songs go from one computer to another only through a host that allows it.
    public bool P2PAllowed => !Me.NoP2P && Host is not { NoP2P: true };

    // ------------------------------------------------------------------ clock

    public long LocalMs => _clock.ElapsedMilliseconds;
    // The host's clock as seen from here (from the ping with the shortest round trip).
    public double HostMs => HostId == Me.Id ? LocalMs : LocalMs + _offset;

    // Where the room's song is right now, in seconds.
    public double Position
    {
        get
        {
            var p = Play;
            double pos = p.State == PlayState.Playing ? p.Position + (HostMs - p.At) / 1000.0 * p.Speed : p.Position;
            return Math.Max(0, Current is { Duration: > 0 } c ? Math.Min(pos, c.Duration) : pos);
        }
    }

    // ------------------------------------------------------------------ events (owner thread)

    public event Action? RoomChanged;
    public event Action? QueueChanged;
    public event Action? PlaybackChanged;
    public event Action? FilesChanged;
    public event Action<ChatLine>? ChatAdded;
    public event Action<string>? Ended;
    // Something to tell the user (a permission missing, the queue full…): an Italian key, translate it.
    public event Action<string>? Notice;
    public event Action<string, byte[]>? CoverArrived;

    // A file of this song on this computer (to send it to someone), if any.
    public Func<string, string?>? LocalFile { get; set; }
    // Where an incoming file is written: song id, extension → path.
    public Func<string, string, string>? TempFile { get; set; }
    public event Action<string, string>? FileReceived;
    public event Action<string, double>? FileProgress;
    public event Action<string>? FileUnavailable;

    public void Post(Action a) => _post(a);

    private void RaiseRoom() => RoomChanged?.Invoke();
    private void RaiseQueue() => QueueChanged?.Invoke();
    private void RaisePlay() => PlaybackChanged?.Invoke();
    private void RaiseFiles() => FilesChanged?.Invoke();

    private void RaiseAll()
    {
        RaiseRoom();
        RaiseQueue();
        RaisePlay();
        RaiseFiles();
    }

    // ------------------------------------------------------------------ create / join / leave

    public void Create(RoomOptions o)
    {
        RoomId = Ids.New();
        RoomName = Clean(o.Name, 60) is { Length: > 0 } n ? n : L.F("Stanza di {0}", Me.Name);
        Max = Math.Clamp(o.Max, 2, 32);
        _password = string.IsNullOrWhiteSpace(o.Password) ? null : o.Password.Trim();
        Locked = _password != null;
        DefaultPerms = o.DefaultPerms & Perm.All;
        EnsureListener();
        HostId = Me.Id;
        var me = Me.Clone();
        me.Seq = _seq = 1;
        me.Perms = Perm.All;
        me.Ip = "127.0.0.1";
        me.Port = _listenPort;
        Members = new List<Member> { me };
        Status = RoomStatus.Connected;
        StartDiscovery();
        StartTimer();
        AddSystem("{0} ha creato la stanza", Me.Name);
        RefreshAd();
        RaiseAll();
    }

    public async Task<JoinResult> JoinAsync(string ip, int port, string? password)
    {
        _password = string.IsNullOrWhiteSpace(password) ? null : password;
        Status = RoomStatus.Connecting;
        RaiseRoom();
        try { EnsureListener(); } catch { }
        var r = await TryJoin(ip, port, false, null);
        if (r.Ok)
        {
            StartTimer();
            return r;
        }
        Shutdown();
        RaiseRoom();
        return r.Redirect != null ? new JoinResult { Error = L.T("Questa stanza ora è ospitata da un altro computer: aggiorna l'elenco e riprova.") } : r;
    }

    private Member Hello()
    {
        var me = Me.Clone();
        me.Port = _listenPort;
        return me;
    }

    private async Task<JoinResult> TryJoin(string ip, int port, bool rejoin, string? lost)
    {
        Connection c;
        try { c = await Connection.ConnectAsync(ip, port, TimeSpan.FromSeconds(rejoin ? 1.5 : 4)); }
        catch
        {
            return new JoinResult { Error = L.T("Nessuna risposta: la stanza potrebbe essere chiusa, oppure il firewall di Windows blocca la connessione.") };
        }
        if (_disposed)
        {
            c.Close();
            return new JoinResult();
        }
        var wait = _joinWait = new TaskCompletionSource<Msg?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host = c;
        Attach(c);
        c.Send(new Msg { T = "hello", Proto = Proto, Me = Hello(), Password = _password, Rejoin = rejoin, Lost = lost, Files = rejoin ? _myFiles : null });
        var done = await Task.WhenAny(wait.Task, Task.Delay(rejoin ? 3000 : 8000));
        var reply = done == wait.Task ? await wait.Task : null;
        if (_joinWait == wait) _joinWait = null;
        if (reply?.T == "welcome" && _host == c) return new JoinResult { Ok = true };
        if (_host == c) _host = null;
        c.Close();
        return reply?.T switch
        {
            "deny" => new JoinResult { Denied = true, Error = L.T(reply.Text ?? "Non puoi entrare in questa stanza."), NeedsPassword = reply.Flag },
            "redirect" => new JoinResult { Redirect = reply },
            _ => new JoinResult { Error = L.T("La stanza non ha risposto.") },
        };
    }

    // Leaving on purpose: the host hands the room to the next one first.
    public async Task LeaveAsync()
    {
        if (_leaving || _disposed) return;
        _leaving = true;
        try
        {
            if (IsHost)
            {
                if (Successor(new HashSet<string> { Me.Id }, true) is { } next)
                {
                    var m = new Msg { T = "migrate", HostId = next.Id, Password = _password, Banned = Banned.ToList() };
                    foreach (var c in _peers.Values) c.Send(m);
                    await Task.WhenAll(_peers.Values.ToList().Select(c => c.FlushAsync(700)));
                }
            }
            else if (_host != null)
            {
                _host.Send(new Msg { T = "bye" });
                await _host.FlushAsync(400);
            }
        }
        catch { }
        Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shutdown();
    }

    private void End(string reason)
    {
        if (_ended) return;
        _ended = true;
        Shutdown();
        RaiseRoom();
        Ended?.Invoke(reason);
    }

    private void Shutdown()
    {
        _timer?.Dispose();
        _timer = null;
        _discovery?.Dispose();
        _discovery = null;
        try { _listener?.Stop(); } catch { }
        _listener = null;
        foreach (var c in _peers.Values.ToList())
        {
            c.Tag = null;
            c.Close();
        }
        _peers.Clear();
        var h = _host;
        _host = null;
        h?.Close();
        _joinWait?.TrySetResult(null);
        lock (_incoming)
        {
            foreach (var inc in _incoming.Values) inc.Drop();
            _incoming.Clear();
        }
        _ad = null;
        Status = RoomStatus.Closed;
    }

    // ------------------------------------------------------------------ network plumbing

    private void EnsureListener()
    {
        if (_listener != null) return;
        TcpListener? l = null;
        for (int port = FirstPort; port <= LastPort && l == null; port++)
        {
            try
            {
                l = new TcpListener(IPAddress.Any, port);
                l.Start();
            }
            catch (SocketException) { l = null; }
        }
        if (l == null)
        {
            l = new TcpListener(IPAddress.Any, 0);
            l.Start();
        }
        _listener = l;
        _listenPort = ((IPEndPoint)l.LocalEndpoint).Port;
        _ = AcceptLoop(l);
    }

    private async Task AcceptLoop(TcpListener l)
    {
        while (true)
        {
            TcpClient tcp;
            try { tcp = await l.AcceptTcpClientAsync(); }
            catch { break; }
            var c = new Connection(tcp);
            Attach(c);
        }
    }

    private void Attach(Connection c)
    {
        c.Message += OnRaw;
        c.Chunk += OnChunk;
        c.Closed += x => _post(() => OnClosed(x));
        c.Start();
    }

    private void StartDiscovery()
    {
        if (_discovery != null) return;
        try { _discovery = new Discovery.Responder(() => _ad); }
        catch { }
    }

    private void StartTimer()
    {
        _timer ??= new Timer(_ => _post(Tick), null, 250, 250);
    }

    private void RefreshAd()
    {
        _ad = !IsHost ? null : new RoomAd
        {
            RoomId = RoomId, Name = RoomName, Host = Me.Name, HostColor = Me.Color, Members = Members.Count(m => !m.Away), Max = Max,
            Port = _listenPort, Locked = Locked, Proto = Proto, Playing = Current?.Title,
        };
    }

    // Network thread: file frames are handled here, in order with the pieces; everything else goes to the owner.
    private void OnRaw(Connection c, Msg m)
    {
        if (m.T is "fileStart" or "fileEnd")
        {
            OnFileFrame(c, m);
            return;
        }
        _post(() => Handle(c, m));
    }

    private void Handle(Connection c, Msg m)
    {
        if (_disposed || c.IsClosed) return;
        if (c == _host)
        {
            FromHost(m);
            return;
        }
        if (c.Tag is { } id && _peers.TryGetValue(id, out var p) && p == c)
        {
            FromMember(id, c, m);
            return;
        }
        if (m.T == "hello") OnHello(c, m);
        else c.Close();
    }

    private void OnClosed(Connection c)
    {
        DropIncoming(c);
        if (c == _host)
        {
            _host = null;
            _joinWait?.TrySetResult(null);
            if (Status == RoomStatus.Connected && !_leaving && !_disposed && !_ended) HostLost(null, false);
            return;
        }
        if (c.Tag is not { } id || !_peers.TryGetValue(id, out var p) || p != c) return;
        _peers.Remove(id);
        if (!IsHost || Members.FirstOrDefault(m => m.Id == id) is not { } who) return;
        // Their place is kept for a while: a hiccup of the network doesn't cost them their permissions.
        who.Away = true;
        _keepPlace[id] = LocalMs + KeepPlace;
        foreach (var list in _waiting.Values) list.Remove(id);
        BroadcastMembers();
    }

    private static async Task CloseSoon(Connection c)
    {
        await c.FlushAsync(500);
        c.Close();
    }

    private void Broadcast(Msg m)
    {
        foreach (var c in _peers.Values) c.Send(m);
    }

    private void BroadcastMembers()
    {
        Broadcast(new Msg { T = "members", Members = Members, HostId = HostId, Banned = Banned.ToList() });
        RefreshAd();
        RaiseRoom();
    }

    private void BroadcastQueue()
    {
        Broadcast(new Msg { T = "queue", Queue = Queue, Current = Current });
        RefreshAd();
        RaiseQueue();
    }

    private void BroadcastPlay()
    {
        Broadcast(new Msg { T = "play", Play = Play });
        RaisePlay();
    }

    private void PushFiles()
    {
        _filesDirty = false;
        _lastFilesPush = LocalMs;
        Broadcast(new Msg { T = "files", AllFiles = Files });
        RaiseFiles();
    }

    // ------------------------------------------------------------------ host: someone knocks

    private void OnHello(Connection c, Msg m)
    {
        if (!IsHost)
        {
            // Someone lost the host and thinks I'm next: if the host is really gone, I take over.
            if (Status == RoomStatus.Connected && m.Rejoin && m.Lost == HostId && ShouldTakeOver()) BecomeHost(HostId, false);
            else
            {
                c.Send(new Msg { T = "redirect", HostId = HostId, Members = Members });
                _ = CloseSoon(c);
                return;
            }
        }
        if (m.Proto != Proto)
        {
            Deny(c, "Versione diversa dell'app: aggiornate tutti all'ultima versione.");
            return;
        }
        if (m.Me is not { Id.Length: > 0 } who)
        {
            c.Close();
            return;
        }
        if (Banned.Contains(who.Id))
        {
            Deny(c, "Sei stato espulso da questa stanza.");
            return;
        }
        var existing = Members.FirstOrDefault(x => x.Id == who.Id);
        if (existing == null)
        {
            if (Members.Count(x => !x.Away) >= Max)
            {
                Deny(c, "La stanza è piena.");
                return;
            }
            if (_password != null && m.Password != _password)
            {
                Deny(c, m.Password == null ? "Questa stanza ha una password." : "Password sbagliata.", true);
                return;
            }
        }
        var mem = existing ?? new Member { Id = who.Id, Seq = ++_seq, Perms = DefaultPerms };
        mem.Name = Clean(who.Name, NameLength) is { Length: > 0 } n ? n : "?";
        mem.Color = Clean(who.Color, 12) is { Length: > 0 } col ? col : "#7C5CFF";
        mem.Avatar = who.Avatar is { Length: < 64_000 } a ? a : null;
        mem.Port = who.Port;
        mem.Ip = c.RemoteIp.ToString();
        mem.Version = Clean(who.Version, 20);
        CopyPrefs(who, mem);
        mem.Away = false;
        if (existing == null) Members.Add(mem);
        _keepPlace.Remove(mem.Id);
        if (_peers.TryGetValue(mem.Id, out var old) && old != c)
        {
            old.Tag = null;
            old.Close();
        }
        _peers[mem.Id] = c;
        c.Tag = mem.Id;
        if (m.Files != null) Files[mem.Id] = m.Files;
        c.Send(new Msg
        {
            T = "welcome", You = mem.Id, RoomId = RoomId, RoomName = RoomName, Max = Max, Locked = Locked, HostId = HostId, Members = Members,
            Queue = Queue, Current = Current, Play = Play, Chat = Chat.TakeLast(100).ToList(), AllFiles = Files, Banned = Banned.ToList(),
            DefaultPerms = DefaultPerms, Password = _password, T1 = LocalMs,
        });
        foreach (var t in new[] { Current }.Concat(Queue).OfType<RoomTrack>().Take(40))
            if (Covers.TryGetValue(t.Id, out var cover)) c.Send(new Msg { T = "cover", Id = t.Id, Data = cover });
        if (existing == null) AddSystem("{0} è entrato nella stanza", mem.Name);
        BroadcastMembers();
        PushFiles();
    }

    private void Deny(Connection c, string key, bool needsPassword = false)
    {
        c.Send(new Msg { T = "deny", Text = key, Flag = needsPassword });
        _ = CloseSoon(c);
    }

    private static void CopyPrefs(Member from, Member to)
    {
        to.Ahead = Math.Clamp(from.Ahead, 0, MaxAhead);
        to.HostAhead = Math.Clamp(from.HostAhead, 0, MaxAhead);
        to.SendsFiles = from.SendsFiles;
        to.NoP2P = from.NoP2P;
    }

    private bool ShouldTakeOver()
    {
        if (_host != null && Environment.TickCount64 - _host.LastHeard < 2500) return false;
        return Successor(new HashSet<string> { HostId }, false)?.Id == Me.Id;
    }

    // Who gets the room: the one allowed to do the most; among equals, who came first.
    private Member? Successor(ISet<string> exclude, bool onlyConnected)
        => Members.Where(m => !exclude.Contains(m.Id) && (!onlyConnected || !m.Away))
            .OrderByDescending(m => Perms.Weight(m.Perms)).ThenBy(m => m.Seq).FirstOrDefault();

    // ------------------------------------------------------------------ host: requests

    private void FromMember(string id, Connection c, Msg m)
    {
        switch (m.T)
        {
            case "ping":
                c.Send(new Msg { T = "pong", T0 = m.T0, T1 = LocalMs });
                break;
            case "status":
                if (m.Files == null) break;
                Files[id] = m.Files;
                FillDurations(m.Files);
                _filesDirty = true;
                RaiseFiles();
                break;
            case "bye":
                RemoveMember(id, "{0} è uscito dalla stanza");
                break;
            case "fileReq":
                if (m.Id != null) ServeFile(id, m.Id);
                break;
            case "prefs":
                if (m.Me == null || Members.FirstOrDefault(x => x.Id == id) is not { } who) break;
                CopyPrefs(m.Me, who);
                BroadcastMembers();
                break;
            default:
                Apply(id, m);
                break;
        }
    }

    // Everything that changes the room, from anyone (the host included): checked against their permissions.
    private void Apply(string from, Msg m)
    {
        var who = Members.FirstOrDefault(x => x.Id == from);
        if (who == null) return;
        var perms = from == Me.Id ? Perm.All : who.Perms;
        bool Allowed(Perm p)
        {
            if ((perms & p) == p) return true;
            Tell(from, p switch
            {
                Perm.Add => "Non hai il permesso di aggiungere brani.",
                Perm.Remove => "Non hai il permesso di togliere o spostare brani.",
                Perm.Skip => "Non hai il permesso di saltare i brani.",
                Perm.Pause => "Non hai il permesso di mettere in pausa o andare avanti e indietro.",
                Perm.Speed => "Non hai il permesso di cambiare la velocità.",
                _ => "Non hai il permesso.",
            });
            return false;
        }
        bool hostOnly = from == Me.Id;

        switch (m.T)
        {
            case "chat":
                if (Clean(m.Text, ChatLength) is not { Length: > 0 } text) return;
                AddChat(new ChatLine { From = from, Name = who.Name, Color = who.Color, Text = text, Time = Now() });
                break;

            case "add":
                if (m.Track is not { } t || !Allowed(Perm.Add) || (m.Flag && !Allowed(Perm.Remove))) return;
                if (Queue.Count >= MaxQueue)
                {
                    Tell(from, "La coda della stanza è piena.");
                    return;
                }
                t.Id = Ids.New();
                t.AddedBy = from;
                Sanitize(t);
                if (m.Data is { Length: > 100 and < 400_000 } cover)
                {
                    Covers[t.Id] = cover;
                    Broadcast(new Msg { T = "cover", Id = t.Id, Data = cover });
                    CoverArrived?.Invoke(t.Id, cover);
                }
                if (m.Flag) Queue.Insert(0, t);
                else Queue.Add(t);
                if (Current == null) Advance();
                else BroadcastQueue();
                break;

            case "remove":
                if (m.Id == null) return;
                if (Queue.FirstOrDefault(x => x.Id == m.Id) is not { } item)
                {
                    if (Current?.Id == m.Id && Allowed(Perm.Skip)) Advance();
                    return;
                }
                // Your own songs you can always take back.
                if (item.AddedBy != from && !Allowed(Perm.Remove)) return;
                Queue.Remove(item);
                Covers.Remove(item.Id);
                BroadcastQueue();
                break;

            case "move":
                if (m.Id == null || !Allowed(Perm.Remove) || Queue.FirstOrDefault(x => x.Id == m.Id) is not { } moving) return;
                Queue.Remove(moving);
                Queue.Insert(Math.Clamp(m.Index, 0, Queue.Count), moving);
                BroadcastQueue();
                break;

            case "playNow":
                if (m.Id == null || !Allowed(Perm.Remove) || !Allowed(Perm.Skip) || Queue.FirstOrDefault(x => x.Id == m.Id) is not { } now) return;
                Queue.Remove(now);
                Queue.Insert(0, now);
                Advance();
                break;

            case "skip":
                if (Current == null || !Allowed(Perm.Skip)) return;
                AddSystem("{0} ha saltato «{1}»", who.Name, Current.Title);
                Advance();
                break;

            case "pause":
                if (Current == null || !Allowed(Perm.Pause)) return;
                SetPausedNow(m.Flag);
                break;

            case "seek":
                if (Current == null || !Allowed(Perm.Pause)) return;
                SeekNow(m.Pos);
                break;

            case "loop":
                if (!Allowed(Perm.Pause) || Play.Loop == m.Flag) return;
                var looped = Play.Clone();
                looped.Loop = m.Flag;
                Play = looped;
                AddSystem(m.Flag ? "{0} ha messo in loop il brano" : "{0} ha tolto il loop", who.Name);
                BroadcastPlay();
                break;

            case "speed":
                if (!Allowed(Perm.Speed) || double.IsNaN(m.Pos)) return;
                SetSpeedNow(m.Pos, m.Flag);
                break;

            case "perms":
                if (!hostOnly || m.Ids == null) return;
                foreach (var target in Members.Where(x => m.Ids.Contains(x.Id) && x.Id != Me.Id))
                    target.Perms = m.Flag ? target.Perms | (m.Perms & Perm.All) : target.Perms & ~m.Perms;
                BroadcastMembers();
                break;

            case "kick":
                if (!hostOnly || m.Id == null || m.Id == Me.Id) return;
                Kick(m.Id);
                break;

            case "promote":
                if (!hostOnly || m.Id == null || m.Id == Me.Id) return;
                _ = HandOver(m.Id);
                break;
        }
    }

    private void Tell(string to, string key)
    {
        if (to == Me.Id) Notice?.Invoke(key);
        else if (_peers.TryGetValue(to, out var c)) c.Send(new Msg { T = "err", Text = key });
    }

    private static void Sanitize(RoomTrack t)
    {
        t.Title = Clean(t.Title, 300) is { Length: > 0 } title ? title : "?";
        t.Artist = Clean(t.Artist, 300);
        t.Album = Clean(t.Album, 300);
        t.Site = Clean(t.Site, 60);
        t.Ext = Clean(t.Ext, 10);
        if (t.SourceUrl != null && (t.SourceUrl.Length > 2000 || !t.SourceUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))) t.SourceUrl = null;
        if (t.ArtUrl is { Length: > 500 }) t.ArtUrl = null;
        t.Keys = t.Keys.Where(k => k.Length < 400).Take(12).ToList();
        if (t.Wave is { Length: > 8192 }) t.Wave = null;
        if (double.IsNaN(t.Duration) || t.Duration < 0 || t.Duration > 24 * 3600) t.Duration = 0;
    }

    private void Kick(string id)
    {
        if (Members.FirstOrDefault(x => x.Id == id) is not { } who) return;
        Banned.Add(id);
        if (_peers.Remove(id, out var c))
        {
            c.Send(new Msg { T = "kicked" });
            c.Tag = null;
            _ = CloseSoon(c);
        }
        RemoveMember(id, "{0} è stato espulso dalla stanza");
    }

    private void RemoveMember(string id, string? template)
    {
        if (Members.FirstOrDefault(x => x.Id == id) is not { } who) return;
        Members.Remove(who);
        Files.Remove(id);
        _keepPlace.Remove(id);
        foreach (var list in _waiting.Values) list.Remove(id);
        if (_peers.Remove(id, out var c))
        {
            c.Tag = null;
            c.Close();
        }
        if (template != null) AddSystem(template, who.Name);
        BroadcastMembers();
        PushFiles();
        // Songs waiting only for them can start.
        CheckWaiting();
    }

    // The host gives the room to someone else and stays as a guest.
    private async Task HandOver(string id)
    {
        if (Members.FirstOrDefault(x => x.Id == id) is not { Away: false } target) return;
        AddSystem("{0} ha passato l'host a {1}", Me.Name, target.Name);
        var m = new Msg { T = "migrate", HostId = id, Flag = true, Password = _password, Banned = Banned.ToList() };
        foreach (var c in _peers.Values) c.Send(m);
        var peers = _peers.Values.ToList();
        await Task.WhenAll(peers.Select(c => c.FlushAsync(600)));
        _discovery?.Dispose();
        _discovery = null;
        _peers.Clear();
        foreach (var c in peers)
        {
            c.Tag = null;
            c.Close();
        }
        _ad = null;
        var me = Me.Id;
        HostId = id;
        HostLost(id, true, me);
    }

    // ------------------------------------------------------------------ host: playback

    private void Advance()
    {
        Current = Queue.Count > 0 ? Queue[0] : null;
        if (Current != null) Queue.RemoveAt(0);
        long now = LocalMs;
        Play = Current == null
            ? Play.Next(null, PlayState.Idle, 0, now)
            : Play.Next(Current.Id, PlayState.Waiting, 0, now);
        var keep = new[] { Current }.Concat(Queue).OfType<RoomTrack>().Select(t => t.Id).ToHashSet();
        foreach (var k in Covers.Keys.Where(k => !keep.Contains(k)).ToList()) Covers.Remove(k);
        BroadcastQueue();
        BroadcastPlay();
        CheckWaiting();
    }

    // A new song starts when everyone has it, or after a few seconds for those who have it:
    // who is still downloading joins in at the right point once done.
    private void CheckWaiting()
    {
        if (!IsHost || Play.State != PlayState.Waiting || Current == null) return;
        var id = Current.Id;
        int ready = 0, failed = 0, total = 0;
        foreach (var m in Members.Where(x => !x.Away))
        {
            total++;
            var s = StatusOf(m.Id, id)?.State;
            if (s == FileState.Ready) ready++;
            else if (s == FileState.Failed) failed++;
        }
        long waited = LocalMs - Play.WaitSince;
        if (ready > 0 && (ready == total || ready + failed == total || waited > WaitForAll))
        {
            Play = Play.Next(id, PlayState.Playing, 0, LocalMs);
            BroadcastPlay();
        }
        else if (waited > GiveUp || (total > 0 && failed == total))
        {
            AddSystem("Nessuno è riuscito a preparare «{0}»: si passa al prossimo", Current.Title);
            Advance();
        }
    }

    private void SetPausedNow(bool paused)
    {
        var p = Play;
        long now = LocalMs;
        if (paused && p.State is PlayState.Playing or PlayState.Waiting)
            Play = p.Next(p.ItemId, PlayState.Paused, Position, now);
        else if (!paused && p.State == PlayState.Paused)
            Play = p.Next(p.ItemId, PlayState.Playing, p.Position, now);
        else return;
        BroadcastPlay();
    }

    // Faster or slower for everyone: the song goes on from where it is now at the new speed.
    private void SetSpeedNow(double speed, bool pitch)
    {
        speed = Math.Round(Math.Clamp(speed, 0.5, 2), 2);
        var p = Play.Clone();
        if (p.Speed == speed && p.Pitch == pitch) return;
        p.Position = Position;
        p.At = LocalMs;
        p.Speed = speed;
        p.Pitch = pitch;
        Play = p;
        BroadcastPlay();
    }

    // Loop: the song playing starts over instead of moving on.
    private void Restart()
    {
        if (Current == null) return;
        var p = Play.Clone();
        p.Position = 0;
        p.At = LocalMs;
        Play = p;
        BroadcastPlay();
    }

    private void SeekNow(double pos)
    {
        if (Current == null) return;
        double max = Current.Duration > 1 ? Current.Duration - 0.5 : double.MaxValue;
        var p = Play.Clone();
        p.Position = Math.Clamp(pos, 0, max);
        p.At = LocalMs;
        Play = p;
        BroadcastPlay();
    }

    // Songs whose length the adder didn't know get it from whoever has the file.
    private void FillDurations(Dictionary<string, FileStatus> files)
    {
        bool changed = false;
        foreach (var (item, s) in files)
            if (s.Duration > 0 && Find(item) is { Duration: <= 0 } t)
            {
                t.Duration = s.Duration;
                changed = true;
            }
        if (changed) BroadcastQueue();
    }

    private void Tick()
    {
        if (_disposed || Status is RoomStatus.Closed or RoomStatus.None) return;
        long now = LocalMs;
        if (IsHost)
        {
            if (Play.State == PlayState.Playing && Current is { Duration: > 0 } cur && Position >= cur.Duration - 0.05)
            {
                if (Play.Loop) Restart();
                else Advance();
            }
            CheckWaiting();
            foreach (var (id, c) in _peers.ToList())
                if (Environment.TickCount64 - c.LastHeard > PeerTimeout) c.Close();
            foreach (var (id, until) in _keepPlace.ToList())
                if (now > until) RemoveMember(id, "{0} ha perso la connessione");
            foreach (var (id, since) in _uploading.ToList())
                if (now - since > UploadTimeout) FailUpload(id);
            if (_filesDirty && now - _lastFilesPush > 300) PushFiles();
        }
        else if (Status == RoomStatus.Connected && _host != null)
        {
            if (now - _lastPing >= PingEvery)
            {
                _lastPing = now;
                _host.Send(new Msg { T = "ping", T0 = now });
            }
            if (Environment.TickCount64 - _host.LastHeard > HostTimeout) _host.Close();
            if (_myFilesDirty && now - _lastFilesPush > 250)
            {
                _myFilesDirty = false;
                _lastFilesPush = now;
                _host.Send(new Msg { T = "status", Files = _myFiles });
            }
        }
    }

    // ------------------------------------------------------------------ guest: from the host

    private void FromHost(Msg m)
    {
        switch (m.T)
        {
            case "welcome":
                RoomId = m.RoomId ?? RoomId;
                RoomName = m.RoomName ?? RoomName;
                Max = m.Max;
                Locked = m.Locked;
                HostId = m.HostId ?? HostId;
                Members = m.Members ?? new();
                Queue = m.Queue ?? new();
                Current = m.Current;
                Play = m.Play ?? new Playback();
                Chat = m.Chat ?? new();
                Files = m.AllFiles ?? new();
                Banned = (m.Banned ?? new()).ToHashSet();
                DefaultPerms = m.DefaultPerms;
                if (m.Password != null) _password = m.Password;
                _offset = m.T1 - LocalMs;
                _samples.Clear();
                _lastPing = 0;
                _ended = false;
                Status = RoomStatus.Connected;
                _joinWait?.TrySetResult(m);
                RaiseAll();
                break;
            case "deny":
            case "redirect":
                _joinWait?.TrySetResult(m);
                break;
            case "members":
                Members = m.Members ?? new();
                if (m.HostId != null) HostId = m.HostId;
                if (m.Banned != null) Banned = m.Banned.ToHashSet();
                RaiseRoom();
                break;
            case "queue":
                Queue = m.Queue ?? new();
                Current = m.Current;
                var keep = new[] { Current }.Concat(Queue).OfType<RoomTrack>().Select(t => t.Id).ToHashSet();
                foreach (var k in Covers.Keys.Where(k => !keep.Contains(k)).ToList()) Covers.Remove(k);
                RaiseQueue();
                break;
            case "play":
                if (m.Play != null) Play = m.Play;
                RaisePlay();
                break;
            case "chat":
                if (m.Line == null) break;
                Chat.Add(m.Line);
                if (Chat.Count > MaxChat) Chat.RemoveAt(0);
                ChatAdded?.Invoke(m.Line);
                break;
            case "files":
                Files = m.AllFiles ?? new();
                RaiseFiles();
                break;
            case "cover":
                if (m.Id == null || m.Data == null) break;
                Covers[m.Id] = m.Data;
                CoverArrived?.Invoke(m.Id, m.Data);
                break;
            case "pong":
                OnPong(m);
                break;
            case "err":
                if (m.Text != null) Notice?.Invoke(m.Text);
                break;
            case "kicked":
                End("Sei stato espulso dalla stanza.");
                break;
            case "migrate":
                if (m.HostId == null) break;
                if (m.Password != null) _password = m.Password;
                if (m.Banned != null) Banned = m.Banned.ToHashSet();
                var lost = HostId;
                var old = _host;
                _host = null;
                old?.Close();
                if (m.HostId == Me.Id) BecomeHost(lost, m.Flag);
                else HostLost(m.HostId, m.Flag, lost);
                break;
            case "upload":
                if (m.Id != null && _host != null) _ = SendTo(_host, m.Id, Me.NoP2P ? null : LocalFile?.Invoke(m.Id));
                break;
            case "fileNone":
                if (m.Id != null) FileUnavailable?.Invoke(m.Id);
                break;
        }
    }

    private void OnPong(Msg m)
    {
        long now = LocalMs, rtt = now - m.T0;
        if (rtt < 0 || rtt > 10000) return;
        _samples.Add((rtt, m.T1 - (m.T0 + now) / 2.0));
        if (_samples.Count > 12) _samples.RemoveAt(0);
        var best = _samples.MinBy(s => s.Rtt);
        _offset = best.Off;
        Latency = best.Rtt;
    }

    // ------------------------------------------------------------------ host gone: the room moves

    private void HostLost(string? preferred, bool keepOld, string? lost = null)
    {
        lost ??= HostId;
        Status = RoomStatus.Reconnecting;
        foreach (var m in Members) if (m.Id != Me.Id) m.Away = m.Id == lost && !keepOld;
        RaiseRoom();
        _ = Recover(lost, preferred, keepOld);
    }

    private async Task Recover(string lost, string? preferred, bool keepOld)
    {
        long deadline = LocalMs + 30000;
        var dead = new HashSet<string>();
        if (preferred == null && Members.FirstOrDefault(x => x.Id == lost) is { } old)
        {
            // The same host first: often it was only the network hiccupping.
            for (int i = 0; i < 2 && Status == RoomStatus.Reconnecting && !_disposed; i++)
            {
                if (await Rejoin(old, lost)) return;
                await Task.Delay(600);
            }
        }
        if (!keepOld) dead.Add(lost);
        while (!_disposed && Status == RoomStatus.Reconnecting && LocalMs < deadline)
        {
            var next = preferred != null && !dead.Contains(preferred) ? Members.FirstOrDefault(x => x.Id == preferred) : Successor(dead, false);
            if (next == null) break;
            if (next.Id == Me.Id)
            {
                BecomeHost(lost, keepOld);
                return;
            }
            for (int i = 0; i < 6 && Status == RoomStatus.Reconnecting && !_disposed; i++)
            {
                if (await Rejoin(next, lost)) return;
                await Task.Delay(800);
            }
            dead.Add(next.Id);
        }
        if (!_disposed && Status == RoomStatus.Reconnecting) End("Connessione alla stanza persa.");
    }

    private async Task<bool> Rejoin(Member target, string lost)
    {
        if (target.Ip == null || target.Port <= 0) return false;
        var r = await TryJoin(target.Ip, target.Port, true, lost);
        if (r.Ok) return true;
        if (r.Denied)
        {
            End(r.Error ?? "Non puoi entrare in questa stanza.");
            return true;
        }
        // They know who the host is now.
        if (r.Redirect is { HostId: { } h } red && h != lost && h != Me.Id && red.Members?.FirstOrDefault(x => x.Id == h) is { Ip: not null } nh)
            return (await TryJoin(nh.Ip, nh.Port, true, lost)).Ok;
        return false;
    }

    private void BecomeHost(string? lost, bool keepOld)
    {
        long now = LocalMs;
        var p = Play.Clone();
        if (p.State == PlayState.Playing) p.Position = Position;
        p.At = now;
        if (p.State == PlayState.Waiting) p.WaitSince = now;
        Play = p;
        var old = lost == null ? null : Members.FirstOrDefault(x => x.Id == lost);
        var h = _host;
        _host = null;
        h?.Close();
        HostId = Me.Id;
        _offset = 0;
        _samples.Clear();
        if (!keepOld && lost != null)
        {
            Members.RemoveAll(x => x.Id == lost);
            Files.Remove(lost);
        }
        foreach (var m in Members.Where(x => x.Id != Me.Id))
        {
            m.Away = true;
            _keepPlace[m.Id] = now + KeepPlace;
        }
        if (MeInRoom is { } me)
        {
            me.Perms = Perm.All;
            me.Away = false;
            me.Port = _listenPort;
            CopyPrefs(Me, me);
        }
        _seq = Members.Count == 0 ? 0 : Members.Max(x => x.Seq);
        Files[Me.Id] = _myFiles;
        Status = RoomStatus.Connected;
        EnsureListener();
        StartDiscovery();
        StartTimer();
        if (old != null && !keepOld) AddSystem("{0} è uscito dalla stanza", old.Name);
        AddSystem("{0} ora è l'host della stanza", Me.Name);
        RefreshAd();
        RaiseAll();
    }

    // ------------------------------------------------------------------ actions (any member)

    public void Say(string text) => Do(new Msg { T = "chat", Text = text });
    public void Add(RoomTrack t, byte[]? cover, bool next) => Do(new Msg { T = "add", Track = t, Data = cover, Flag = next });
    public void Remove(string itemId) => Do(new Msg { T = "remove", Id = itemId });
    public void Move(string itemId, int index) => Do(new Msg { T = "move", Id = itemId, Index = index });
    public void PlayNow(string itemId) => Do(new Msg { T = "playNow", Id = itemId });
    public void Skip() => Do(new Msg { T = "skip" });
    public void SetPaused(bool paused) => Do(new Msg { T = "pause", Flag = paused });
    public void Seek(double seconds) => Do(new Msg { T = "seek", Pos = seconds });
    public void SetLoop(bool on) => Do(new Msg { T = "loop", Flag = on });
    public void SetSpeed(double speed, bool pitch) => Do(new Msg { T = "speed", Pos = speed, Flag = pitch });
    public void SetPerm(IEnumerable<string> ids, Perm perm, bool on) => Do(new Msg { T = "perms", Ids = ids.ToList(), Perms = perm, Flag = on });
    public void KickMember(string id) => Do(new Msg { T = "kick", Id = id });
    public void Promote(string id) => Do(new Msg { T = "promote", Id = id });

    // Songs to get ready and P2P, changed in the settings while in the room: the others see it.
    public void SetPrefs(int ahead, int hostAhead, bool sendsFiles, bool noP2P)
    {
        Me.Ahead = Math.Clamp(ahead, 1, MaxAhead);
        Me.HostAhead = Math.Clamp(hostAhead, 1, MaxAhead);
        Me.SendsFiles = sendsFiles;
        Me.NoP2P = noP2P;
        if (Status != RoomStatus.Connected) return;
        if (HostId == Me.Id)
        {
            if (MeInRoom is { } me) CopyPrefs(Me, me);
            if (noP2P) DropTransfers();
            BroadcastMembers();
        }
        else _host?.Send(new Msg { T = "prefs", Me = new Member { Id = Me.Id, Ahead = Me.Ahead, HostAhead = Me.HostAhead, SendsFiles = sendsFiles, NoP2P = noP2P } });
    }

    private void Do(Msg m)
    {
        if (IsHost) Apply(Me.Id, m);
        else if (_host != null && Status == RoomStatus.Connected) _host.Send(m);
        else Notice?.Invoke("Riconnessione alla stanza in corso…");
    }

    // How far this computer is with each song (the host decides from these when a song can start).
    public void ReportFiles(Dictionary<string, FileStatus> files)
    {
        _myFiles = new Dictionary<string, FileStatus>(files);
        if (HostId == Me.Id && Status == RoomStatus.Connected)
        {
            Files[Me.Id] = _myFiles;
            _filesDirty = true;
            ServeOwn();
            RaiseFiles();
            CheckWaiting();
        }
        else _myFilesDirty = true;
    }

    private void AddChat(ChatLine line)
    {
        Chat.Add(line);
        if (Chat.Count > MaxChat) Chat.RemoveAt(0);
        if (IsHost) Broadcast(new Msg { T = "chat", Line = line });
        ChatAdded?.Invoke(line);
    }

    private void AddSystem(string template, params string[] args)
        => AddChat(new ChatLine { System = true, Text = template, Args = args.ToList(), Time = Now() });

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string? Clean(string? s, int max)
    {
        if (s == null) return null;
        s = new string(s.Where(ch => !char.IsControl(ch) || ch == '\n').ToArray()).Trim();
        return s.Length > max ? s[..max] : s;
    }

    // ------------------------------------------------------------------ files between computers

    // Host: item → people waiting for it; items being sent to the host by someone who has them.
    private readonly Dictionary<string, List<string>> _waiting = new();
    private readonly Dictionary<string, long> _uploading = new();

    // A song this computer can't download: someone in the room sends it (through the host).
    public void RequestFile(string itemId)
    {
        if (!P2PAllowed) FileUnavailable?.Invoke(itemId);
        else if (HostId == Me.Id && Status == RoomStatus.Connected) ServeFile(Me.Id, itemId);
        else if (_host != null) _host.Send(new Msg { T = "fileReq", Id = itemId });
        else FileUnavailable?.Invoke(itemId);
    }

    private void ServeFile(string to, string itemId)
    {
        Connection? c = null;
        if (to != Me.Id && !_peers.TryGetValue(to, out c)) return;
        // A host without P2P passes nothing on; who turned it off gets nothing.
        if (Me.NoP2P || Members.FirstOrDefault(x => x.Id == to) is { NoP2P: true })
        {
            NoFile(to, itemId);
            return;
        }
        var local = to == Me.Id ? null : LocalFile?.Invoke(itemId);
        if (local != null && File.Exists(local))
        {
            _ = SendTo(c!, itemId, local);
            return;
        }
        if (!_waiting.TryGetValue(itemId, out var list)) _waiting[itemId] = list = new List<string>();
        if (!list.Contains(to)) list.Add(to);
        if (_uploading.ContainsKey(itemId)) return;
        // The host is getting it itself: it goes on as soon as it's here (ReportFiles).
        if (to != Me.Id && GettingIt(itemId)) return;
        var holder = Holder(itemId, to);
        if (holder == null)
        {
            list.Remove(to);
            NoFile(to, itemId);
            return;
        }
        _uploading[itemId] = LocalMs;
        holder.Send(new Msg { T = "upload", Id = itemId });
    }

    // Someone else who has the file, whoever added it first.
    private Connection? Holder(string itemId, string except)
    {
        var item = Find(itemId);
        var ids = Members.Where(x => x.Id != except && x.Id != Me.Id && !x.Away && !x.NoP2P && StatusOf(x.Id, itemId)?.State == FileState.Ready)
            .OrderBy(x => x.Id == item?.AddedBy ? 0 : 1).Select(x => x.Id).ToList();
        if (item != null && item.AddedBy != except && item.AddedBy != Me.Id && !ids.Contains(item.AddedBy) &&
            Members.FirstOrDefault(x => x.Id == item.AddedBy) is not { NoP2P: true }) ids.Add(item.AddedBy);
        foreach (var id in ids)
            if (_peers.TryGetValue(id, out var c)) return c;
        return null;
    }

    private void NoFile(string to, string itemId)
    {
        if (to == Me.Id) FileUnavailable?.Invoke(itemId);
        else if (_peers.TryGetValue(to, out var c)) c.Send(new Msg { T = "fileNone", Id = itemId });
    }

    private void ServeWaiting(string itemId)
    {
        _uploading.Remove(itemId);
        if (!_waiting.Remove(itemId, out var list)) return;
        var local = LocalFile?.Invoke(itemId);
        foreach (var to in list)
        {
            if (to == Me.Id) continue;
            if (local != null && _peers.TryGetValue(to, out var c)) _ = SendTo(c, itemId, local);
            else NoFile(to, itemId);
        }
    }

    private bool GettingIt(string itemId) => _myFiles.TryGetValue(itemId, out var s) && s.State is FileState.Downloading or FileState.Transfer;

    // Host: people waiting for a song the host was getting itself get it now (or hear it didn't come).
    private void ServeOwn()
    {
        foreach (var itemId in _waiting.Keys.ToList())
        {
            if (_uploading.ContainsKey(itemId) || GettingIt(itemId)) continue;
            if (LocalFile?.Invoke(itemId) != null) ServeWaiting(itemId);
            else FailUpload(itemId);
        }
    }

    // One copy of a song at a time to each person (a second request while it's on its way changes nothing).
    private readonly HashSet<(Connection, string)> _sending = new();

    private async Task SendTo(Connection c, string itemId, string? path)
    {
        if (!_sending.Add((c, itemId))) return;
        try { await SendFile(c, itemId, path); }
        finally { _sending.Remove((c, itemId)); }
    }

    private void FailUpload(string itemId)
    {
        _uploading.Remove(itemId);
        if (_waiting.Remove(itemId, out var list))
            foreach (var to in list) NoFile(to, itemId);
    }

    // The host turned P2P off: whoever was waiting for a song from someone hears it won't come.
    private void DropTransfers()
    {
        foreach (var itemId in _waiting.Keys.ToList()) FailUpload(itemId);
    }

    private static async Task SendFile(Connection c, string itemId, string? path)
    {
        try
        {
            if (path == null || !File.Exists(path)) throw new FileNotFoundException();
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, true);
            if (fs.Length <= 0 || fs.Length > MaxFile) throw new IOException("size");
            await c.SendBulkAsync(new Msg { T = "fileStart", Id = itemId, Size = fs.Length, Text = Path.GetExtension(path) });
            var buf = new byte[64 * 1024];
            int n;
            while ((n = await fs.ReadAsync(buf)) > 0) await c.SendChunkAsync(itemId, buf, n);
            await c.SendBulkAsync(new Msg { T = "fileEnd", Id = itemId, Flag = true });
        }
        catch
        {
            try { await c.SendBulkAsync(new Msg { T = "fileEnd", Id = itemId, Flag = false }); } catch { }
        }
    }

    private sealed class Incoming
    {
        public required FileStream Stream;
        public required string Path;
        public long Size, Got, LastReport;

        public void Drop()
        {
            try { Stream.Dispose(); } catch { }
            try { File.Delete(Path); } catch { }
        }
    }

    private readonly Dictionary<(Connection, string), Incoming> _incoming = new();

    // Network thread.
    private void OnFileFrame(Connection c, Msg m)
    {
        if (m.Id is not { Length: > 0 and < 64 } id) return;
        lock (_incoming)
        {
            if (m.T == "fileStart")
            {
                if (m.Size <= 0 || m.Size > MaxFile || Me.NoP2P) return;
                var ext = m.Text is { Length: > 1 and < 10 } e && e.StartsWith('.') && e.Skip(1).All(char.IsLetterOrDigit) ? e : ".bin";
                var path = TempFile?.Invoke(id, ext) ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ump-lt-" + id + ext);
                try
                {
                    if (_incoming.Remove((c, id), out var stale)) stale.Drop();
                    _incoming[(c, id)] = new Incoming { Stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16), Path = path, Size = m.Size };
                }
                catch { }
                return;
            }
            if (!_incoming.Remove((c, id), out var inc))
            {
                // "I don't have it after all" (nothing was sent), or a file this computer doesn't take (P2P off).
                if (!m.Flag || Me.NoP2P) _post(() => OnFileFailed(id));
                return;
            }
            try { inc.Stream.Dispose(); } catch { }
            if (m.Flag && inc.Got == inc.Size) _post(() => OnFileDone(id, inc.Path));
            else
            {
                inc.Drop();
                _post(() => OnFileFailed(id));
            }
        }
    }

    private void OnChunk(Connection c, string id, ReadOnlyMemory<byte> data)
    {
        Incoming? inc;
        lock (_incoming) _incoming.TryGetValue((c, id), out inc);
        if (inc == null) return;
        try
        {
            inc.Stream.Write(data.Span);
            inc.Got += data.Length;
        }
        catch { return; }
        long now = Environment.TickCount64;
        if (now - inc.LastReport < 200) return;
        inc.LastReport = now;
        double pct = inc.Size > 0 ? inc.Got * 100.0 / inc.Size : 0;
        _post(() => FileProgress?.Invoke(id, pct));
    }

    private void DropIncoming(Connection c)
    {
        List<string> ids;
        lock (_incoming)
        {
            var mine = _incoming.Where(kv => kv.Key.Item1 == c).ToList();
            foreach (var kv in mine)
            {
                kv.Value.Drop();
                _incoming.Remove(kv.Key);
            }
            ids = mine.Select(kv => kv.Key.Item2).ToList();
        }
        foreach (var id in ids) OnFileFailed(id);
    }

    private void OnFileDone(string itemId, string path)
    {
        if (_disposed)
        {
            try { File.Delete(path); } catch { }
            return;
        }
        FileReceived?.Invoke(itemId, path);
        if (HostId == Me.Id) ServeWaiting(itemId);
    }

    private void OnFileFailed(string itemId)
    {
        if (HostId == Me.Id) FailUpload(itemId);
        else FileUnavailable?.Invoke(itemId);
    }
}
