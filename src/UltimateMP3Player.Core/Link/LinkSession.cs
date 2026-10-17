using System.Net;
using System.Net.Sockets;
using UltimateMP3Player.Core.Together;

namespace UltimateMP3Player.Core.Link;

// One end of "Connect phone": the connection, the pings, a connection lost and found again. The computer connects to
// the phone (outgoing, so no firewall stands in the way) and, when the connection drops, tries again for a few minutes
// (the phone may have changed address: it's looked for again); the phone keeps its place meanwhile. Everything happens
// on the owner's thread (post); the network threads only post to it, except the files (FileLane).
public abstract class LinkEnd : IDisposable
{
    // 1: 3.9.
    public const int Proto = 1;
    protected const long Timeout = 9000, PingEvery = 2000, GiveUp = 180000;

    protected readonly Action<Action> Post;
    private Timer? _timer;
    private long _lastPing;
    // (read by the network threads: the files of the connection in use)
    private volatile Connection? _conn;
    protected long LostAt;
    private bool _ended;

    protected LinkEnd(Action<Action> post) => Post = post;

    public LinkStatus Status { get; private set; }
    // The device on the other side.
    public string OtherId { get; protected set; } = "";
    public string OtherName { get; protected set; } = "";
    public FileLane Files { get; } = new();
    public bool IsLinked => Status is LinkStatus.Connected or LinkStatus.Reconnecting;

    public event Action? StatusChanged;
    public event Action<Msg>? Message;
    // The link is over, for good: why (null: this side ended it).
    public event Action<string?>? Ended;

    public Connection? Connection => Status == LinkStatus.Connected ? _conn : null;

    // Lost while reconnecting.
    public void Send(Msg m)
    {
        if (Status == LinkStatus.Connected) _conn?.Send(m);
    }

    protected void SetStatus(LinkStatus s)
    {
        if (Status == s) return;
        Status = s;
        StatusChanged?.Invoke();
    }

    protected void Hook(Connection c)
    {
        c.Message += (cc, m) =>
        {
            if (cc == _conn && Files.OnMessage(m)) return;
            Post(() => Receive(cc, m));
        };
        c.Chunk += (cc, id, data) =>
        {
            if (cc == _conn) Files.OnChunk(id, data);
        };
        c.Closed += cc => Post(() =>
        {
            if (cc == _conn) Lost();
        });
        c.Start();
    }

    private void Receive(Connection c, Msg m)
    {
        if (_ended) return;
        if (c != _conn)
        {
            FromOther(c, m);
            return;
        }
        switch (m.T)
        {
            case "ping":
                return;
            case "bye":
                End(L.F("{0} disconnected.", OtherName));
                return;
        }
        Message?.Invoke(m);
    }

    // A message of a connection that isn't (yet) the link's: the handshake.
    protected abstract void FromOther(Connection c, Msg m);

    // The connection in use from now on (a new one after a drop replaces the old one).
    protected void Use(Connection c)
    {
        var old = _conn;
        _conn = c;
        old?.Close();
        _lastPing = 0;
        _timer ??= new Timer(_ => Post(Tick), null, 500, 500);
        SetStatus(LinkStatus.Connected);
    }

    private void Lost()
    {
        _conn = null;
        Files.FailAll();
        if (_ended) return;
        LostAt = Environment.TickCount64;
        SetStatus(LinkStatus.Reconnecting);
        OnLost();
    }

    protected virtual void OnLost() { }

    private void Tick()
    {
        if (_ended) return;
        long now = Environment.TickCount64;
        if (Status == LinkStatus.Connected && _conn is { } c)
        {
            if (now - c.LastHeard > Timeout)
            {
                // Silent too long (Wi-Fi gone, the other device asleep): as good as closed.
                c.Close();
                return;
            }
            if (now - _lastPing >= PingEvery)
            {
                _lastPing = now;
                c.Send(new Msg { T = "ping" });
            }
        }
        else if (Status == LinkStatus.Reconnecting)
        {
            if (now - LostAt > GiveUp) End(GaveUpText);
            else Retry();
        }
    }

    protected abstract string GaveUpText { get; }

    // Reconnecting: one more try (the computer).
    protected virtual void Retry() { }

    protected void End(string? why)
    {
        if (_ended) return;
        _ended = true;
        _timer?.Dispose();
        _timer = null;
        var c = _conn;
        _conn = null;
        c?.Close();
        Files.FailAll();
        Stopped();
        SetStatus(LinkStatus.Closed);
        Ended?.Invoke(why);
    }

    protected virtual void Stopped() { }

    // On purpose: the other side hears it at once.
    public async Task DisconnectAsync()
    {
        if (_conn is { } c && Status == LinkStatus.Connected)
        {
            c.Send(new Msg { T = "bye" });
            await c.FlushAsync(400);
        }
        End(null);
    }

    public void Dispose()
    {
        if (_ended) return;
        if (_conn is { } c && Status == LinkStatus.Connected)
        {
            // (the app is closing: a moment for the goodbye to go out, off the UI thread)
            c.Send(new Msg { T = "bye" });
            try { Task.Run(() => c.FlushAsync(300)).Wait(400); } catch { }
        }
        End(null);
    }
}

// ====================================================================== the computer

public sealed class PhoneLink : LinkEnd
{
    private readonly Member _me;
    private PhoneAd _phone;
    private string? _token;
    private Connection? _pending;
    private TaskCompletionSource<Msg?>? _reply;
    private bool _trying;
    private long _lastTry;
    private int _tries;

    public PhoneLink(PhoneAd phone, Member me, Action<Action> post) : base(post)
    {
        _phone = phone;
        _me = me;
        OtherId = phone.Id;
        OtherName = phone.Name;
    }

    public PhoneAd Phone => _phone;
    public bool ViaCable => _phone.Usb > 0 || _phone.Cable;

    protected override string GaveUpText => L.F("{0} is no longer reachable: connect it again from Settings.", OtherName);

    // The first connection: the phone may ask its user (a minute at most). Null when connected, otherwise why not.
    public async Task<string?> ConnectAsync()
    {
        SetStatus(LinkStatus.Connecting);
        var (c, reply) = await Handshake(_phone, 65000);
        // Canceled meanwhile.
        if (Status != LinkStatus.Connecting)
        {
            c?.Close();
            return null;
        }
        if (c != null && reply?.T == "welcome")
        {
            Welcomed(c, reply);
            return null;
        }
        c?.Close();
        End(null);
        return reply?.T == "deny" ? L.T(reply.Text ?? "The phone said no.")
            : c == null ? L.T("No answer: the phone may have left “Connect to computer”, or it's on another network.")
            : L.T("The phone didn't answer.");
    }

    private void Welcomed(Connection c, Msg reply)
    {
        _token = reply.Text;
        if (reply.Me is { } m && m.Name.Length > 0) OtherName = m.Name;
        _tries = 0;
        Use(c);
    }

    private static async Task<Connection> Open(PhoneAd ad)
    {
        if (ad.Usb > 0)
        {
            using var cts = new CancellationTokenSource(4000);
            return new Connection(await Usbmux.ConnectAsync(ad.Usb, ad.Port, cts.Token));
        }
        return await Connection.ConnectAsync(ad.Ip!, ad.Port, TimeSpan.FromSeconds(4));
    }

    private async Task<(Connection?, Msg?)> Handshake(PhoneAd ad, int waitMs)
    {
        Connection c;
        try { c = await Open(ad); }
        catch { return (null, null); }
        var reply = _reply = new TaskCompletionSource<Msg?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = c;
        c.Closed += _ => reply.TrySetResult(null);
        Hook(c);
        c.Send(new Msg { T = "hello", Proto = Proto, Me = _me, Text = _token });
        var done = await Task.WhenAny(reply.Task, Task.Delay(waitMs));
        var m = done == reply.Task ? await reply.Task : null;
        if (_pending == c) _pending = null;
        return (c, m);
    }

    protected override void FromOther(Connection c, Msg m)
    {
        if (c == _pending && m.T is "welcome" or "deny") _reply?.TrySetResult(m);
    }

    protected override void Stopped() => _pending?.Close();

    protected override void Retry()
    {
        long now = Environment.TickCount64;
        if (_trying || now - _lastTry < 2000) return;
        _lastTry = now;
        _trying = true;
        _ = TryAgain();
    }

    private async Task TryAgain()
    {
        try
        {
            // Every few tries the phone is looked for again (another address, plugged in again).
            if (++_tries % 3 == 0 && await FindAsync(_phone.Id) is { } found) _phone = found;
            if (Status != LinkStatus.Reconnecting) return;
            var (c, reply) = await Handshake(_phone, 5000);
            if (c == null) return;
            if (reply?.T == "welcome" && Status == LinkStatus.Reconnecting) Welcomed(c, reply);
            else
            {
                c.Close();
                // It said no (it was disconnected there meanwhile): no use trying again.
                if (reply?.T == "deny" && Status == LinkStatus.Reconnecting) End(L.T(reply.Text ?? "The phone said no."));
            }
        }
        finally { _trying = false; }
    }

    // ------------------------------------------------------------------ finding the phones

    // The phones in "Connect to computer": on the networks and on a cable.
    public static async Task<List<PhoneAd>> ScanAsync(CancellationToken ct)
    {
        var lan = LinkDiscovery.ScanAsync(1200, ct);
        var usb = ScanUsbAsync(ct);
        var list = await lan;
        foreach (var u in await usb)
        {
            list.RemoveAll(a => a.Id == u.Id);
            list.Add(u);
        }
        return list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static async Task<List<PhoneAd>> ScanUsbAsync(CancellationToken ct)
    {
        var list = new List<PhoneAd>();
        foreach (var device in await Usbmux.DevicesAsync(ct))
        {
            try
            {
                using var tcp = await Usbmux.ConnectAsync(device, LinkDiscovery.Port, ct);
                using var c = new Connection(tcp);
                var got = new TaskCompletionSource<PhoneAd?>(TaskCreationOptions.RunContinuationsAsynchronously);
                c.Message += (_, m) => got.TrySetResult(m.T == "ad" ? m.Ad : null);
                c.Closed += _ => got.TrySetResult(null);
                c.Start();
                c.Send(new Msg { T = "who" });
                var done = await Task.WhenAny(got.Task, Task.Delay(1500, ct));
                if (done == got.Task && await got.Task is { } ad && ad.Proto > 0)
                {
                    ad.Usb = device;
                    ad.Port = LinkDiscovery.Port;
                    ad.Cable = true;
                    list.Add(ad);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        }
        return list;
    }

    private static async Task<PhoneAd?> FindAsync(string id)
    {
        try { return (await ScanAsync(CancellationToken.None)).FirstOrDefault(a => a.Id == id); }
        catch { return null; }
    }
}

// ====================================================================== the phone

public sealed class ComputerLink : LinkEnd
{
    private readonly Member _me;
    // Asks the user whether this computer may connect (its name, through a cable).
    private readonly Func<string, Task<bool>> _ask;
    private TcpListener? _listener;
    private LinkDiscovery.Responder? _responder;
    private int _port;
    private string? _token;
    private bool _visible, _asking;

    public ComputerLink(Member me, Func<string, Task<bool>> ask, Action<Action> post) : base(post)
    {
        _me = me;
        _ask = ask;
        StatusChanged += Listen;
    }

    protected override string GaveUpText => L.F("{0} is no longer reachable: the music stays on the computer.", OtherName);

    // "Connect to computer" is open: the computers can find this phone.
    public bool Visible
    {
        get => _visible;
        set
        {
            _visible = value;
            Listen();
        }
    }

    public string? Error { get; private set; }

    // Waiting for computers while visible, and for the one linked to come back.
    private void Listen()
    {
        if (Status == LinkStatus.Closed) return;
        bool want = _visible || IsLinked;
        if (want && _listener == null)
        {
            Error = null;
            try
            {
                TcpListener l;
                try
                {
                    l = new TcpListener(IPAddress.Any, LinkDiscovery.Port);
                    l.Start();
                }
                catch (SocketException)
                {
                    // (taken: any port, only the network finds it then, not the cable)
                    l = new TcpListener(IPAddress.Any, 0);
                    l.Start();
                }
                _listener = l;
                _port = ((IPEndPoint)l.LocalEndpoint).Port;
                _ = AcceptLoop(l);
            }
            catch (Exception ex) { Error = ex.Message; }
            try { _responder ??= new LinkDiscovery.Responder(Answer); }
            catch (Exception ex) { Error ??= ex.Message; }
        }
        else if (!want) Stopped();
    }

    protected override void Stopped()
    {
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _responder?.Dispose();
        _responder = null;
    }

    // To the computers looking: while "Connect to computer" is open, or for the computer looking for this phone again.
    private PhoneAd? Answer(IPAddress from)
        => _visible || Status == LinkStatus.Reconnecting ? Ad(LinkDiscovery.ViaCable(from)) : null;

    private PhoneAd Ad(bool cable) => new()
    {
        Id = _me.Id, Name = _me.Name, Port = _port, Proto = Proto, Cable = cable, Linked = IsLinked ? OtherName : null,
    };

    private async Task AcceptLoop(TcpListener l)
    {
        while (true)
        {
            TcpClient tcp;
            try { tcp = await l.AcceptTcpClientAsync(); }
            catch { break; }
            Hook(new Connection(tcp));
        }
    }

    protected override void FromOther(Connection c, Msg m)
    {
        if (m.T == "who")
        {
            c.Send(new Msg { T = "ad", Ad = Ad(true) });
            _ = Close(c);
        }
        else if (m.T == "hello") _ = Hello(c, m);
        else c.Close();
    }

    private static async Task Close(Connection c)
    {
        await c.FlushAsync(500);
        c.Close();
    }

    private void Deny(Connection c, string why)
    {
        c.Send(new Msg { T = "deny", Text = why });
        _ = Close(c);
    }

    private async Task Hello(Connection c, Msg m)
    {
        if (m.Proto != Proto || m.Me is not { } pc)
        {
            Deny(c, "The app on the phone is another version: update it on both devices.");
            return;
        }
        // The same computer coming back after a drop: straight in. Without its key (the app there started again) it's
        // asked again, even with the page closed.
        bool same = IsLinked && pc.Id == OtherId;
        if (same && m.Text != null && m.Text == _token)
        {
            Welcome(c, pc);
            return;
        }
        if (IsLinked && !same)
        {
            Deny(c, "The phone is connected to another computer.");
            return;
        }
        if (!_visible && !same)
        {
            Deny(c, "Open “Connect to computer” in the phone's settings.");
            return;
        }
        if (_asking)
        {
            Deny(c, "The phone is already answering another computer.");
            return;
        }
        bool ok = LinkDiscovery.ViaCable(c.RemoteIp);
        if (!ok)
        {
            _asking = true;
            try { ok = await _ask(Clean(pc.Name)); }
            finally { _asking = false; }
        }
        if (c.IsClosed || IsLinked && !same || Status == LinkStatus.Closed) return;
        if (!ok)
        {
            Deny(c, "The phone said no.");
            return;
        }
        _token = Ids.New();
        Welcome(c, pc);
    }

    private void Welcome(Connection c, Member pc)
    {
        OtherId = pc.Id;
        OtherName = Clean(pc.Name);
        c.Send(new Msg { T = "welcome", Text = _token, Me = _me });
        Use(c);
    }

    private static string Clean(string name) => name.Trim() is { Length: > 0 } n ? n.Length > 60 ? n[..60] : n : "PC";
}
