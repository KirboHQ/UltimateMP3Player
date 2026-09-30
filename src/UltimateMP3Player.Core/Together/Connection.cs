using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace UltimateMP3Player.Core.Together;

// One TCP link between two apps: frames of [length][kind][body]; kind 1 = a message (JSON), 2 = a piece of a file.
// Messages go out before file pieces, so a big transfer never holds up the chat or the playback; the "bulk" lane
// keeps a file's pieces and the messages around them in order.
public sealed class Connection : IDisposable
{
    private const int MaxFrame = 16 << 20;
    private const byte KindMsg = 1, KindChunk = 2;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    // (Not "single reader": that kind of channel can't count what's waiting, and FlushAsync needs it.)
    private readonly Channel<byte[]> _control = Channel.CreateUnbounded<byte[]>();
    private readonly Channel<byte[]> _bulk = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _cts = new();
    private int _closed;

    public Connection(TcpClient tcp)
    {
        _tcp = tcp;
        tcp.NoDelay = true;
        try { tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }
        _stream = tcp.GetStream();
        var ip = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        RemoteIp = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
        LastHeard = Environment.TickCount64;
    }

    public IPAddress RemoteIp { get; }
    // Id of the person on the other side, once known.
    public string? Tag { get; set; }
    public long LastHeard { get; private set; }
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    // Raised on the network thread.
    public event Action<Connection, Msg>? Message;
    public event Action<Connection, string, ReadOnlyMemory<byte>>? Chunk;
    public event Action<Connection>? Closed;

    public void Start()
    {
        _ = Task.Run(ReadLoop);
        _ = Task.Run(WriteLoop);
    }

    public static async Task<Connection> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        var tcp = new TcpClient(AddressFamily.InterNetwork);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await tcp.ConnectAsync(host, port, cts.Token); }
        catch
        {
            tcp.Dispose();
            throw;
        }
        return new Connection(tcp);
    }

    public void Send(Msg m)
    {
        if (!IsClosed) _control.Writer.TryWrite(Frame(KindMsg, m.ToBytes()));
    }

    // In the file lane, after the pieces sent before it.
    public ValueTask SendBulkAsync(Msg m, CancellationToken ct = default)
        => IsClosed ? ValueTask.CompletedTask : _bulk.Writer.WriteAsync(Frame(KindMsg, m.ToBytes()), ct);

    public ValueTask SendChunkAsync(string id, byte[] data, int count, CancellationToken ct = default)
    {
        if (IsClosed) return ValueTask.CompletedTask;
        var idBytes = Encoding.UTF8.GetBytes(id);
        int body = 1 + 1 + idBytes.Length + count;
        var f = new byte[4 + body];
        BinaryPrimitives.WriteInt32LittleEndian(f, body);
        f[4] = KindChunk;
        f[5] = (byte)idBytes.Length;
        idBytes.CopyTo(f, 6);
        Buffer.BlockCopy(data, 0, f, 6 + idBytes.Length, count);
        return _bulk.Writer.WriteAsync(f, ct);
    }

    private static byte[] Frame(byte kind, byte[] body)
    {
        var f = new byte[5 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(f, body.Length + 1);
        f[4] = kind;
        body.CopyTo(f, 5);
        return f;
    }

    // Waits (a little) until everything queued is on the wire: before closing on purpose.
    public async Task FlushAsync(int maxMs)
    {
        try
        {
            long until = Environment.TickCount64 + maxMs;
            while (!IsClosed && (_control.Reader.Count > 0 || _bulk.Reader.Count > 0) && Environment.TickCount64 < until)
                await Task.Delay(15);
            if (!IsClosed) await Task.Delay(40);
        }
        catch { }
    }

    private async Task WriteLoop()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_control.Reader.TryRead(out var c))
                {
                    await _stream.WriteAsync(c, ct);
                    continue;
                }
                if (_bulk.Reader.TryRead(out var b))
                {
                    await _stream.WriteAsync(b, ct);
                    continue;
                }
                var waitControl = _control.Reader.WaitToReadAsync(ct).AsTask();
                var waitBulk = _bulk.Reader.WaitToReadAsync(ct).AsTask();
                await Task.WhenAny(waitControl, waitBulk);
                if (waitControl.IsCompletedSuccessfully && !waitControl.Result && waitBulk.IsCompleted) break;
            }
        }
        catch { }
        Close();
    }

    private async Task ReadLoop()
    {
        var head = new byte[4];
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(head, ct);
                int len = BinaryPrimitives.ReadInt32LittleEndian(head);
                if (len < 1 || len > MaxFrame) break;
                var body = new byte[len];
                await _stream.ReadExactlyAsync(body, ct);
                LastHeard = Environment.TickCount64;
                if (body[0] == KindMsg)
                {
                    if (Msg.From(body.AsSpan(1)) is { } m) Message?.Invoke(this, m);
                }
                else if (body[0] == KindChunk && len > 2)
                {
                    int idLen = body[1];
                    if (2 + idLen > len) break;
                    var id = Encoding.UTF8.GetString(body, 2, idLen);
                    Chunk?.Invoke(this, id, body.AsMemory(2 + idLen));
                }
            }
        }
        catch { }
        Close();
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _cts.Cancel();
        _control.Writer.TryComplete();
        _bulk.Writer.TryComplete();
        try { _tcp.Client.Shutdown(SocketShutdown.Both); } catch { }
        try { _tcp.Dispose(); } catch { }
        try { Closed?.Invoke(this); } catch { }
    }

    public void Dispose() => Close();
}
