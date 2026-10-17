using UltimateMP3Player.Core.Together;

namespace UltimateMP3Player.Core.Link;

// Files over a link: in the connection's bulk lane, between a "fileStart" and a "fileEnd" message, like Listen
// together's. The receiver says first where each one goes (ReceiveAsync): a file nobody waits for is thrown away.
public sealed class FileLane
{
    private sealed class Incoming
    {
        public required Func<string, string> Target;
        public Action<long, long>? Progress;
        public readonly TaskCompletionSource<string?> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FileStream? Stream;
        public string? Path;
        public long Size, Got, LastReport;

        public void Drop()
        {
            try { Stream?.Dispose(); } catch { }
            Stream = null;
            if (Path != null) try { File.Delete(Path + ".part"); } catch { }
        }
    }

    private readonly Dictionary<string, Incoming> _in = new();

    // Waits for the file sent with this id; target: where it goes, from its extension (".mp3"); progress: bytes so far,
    // of how many. Its path, or null when it didn't come (not there, the connection gone, canceled).
    public async Task<string?> ReceiveAsync(string id, Func<string, string> target, Action<long, long>? progress, CancellationToken ct)
    {
        var inc = new Incoming { Target = target, Progress = progress };
        lock (_in)
        {
            if (_in.Remove(id, out var old)) Finish(old, false);
            _in[id] = inc;
        }
        using (ct.Register(() =>
        {
            lock (_in)
            {
                if (_in.TryGetValue(id, out var x) && x == inc) _in.Remove(id);
                Finish(inc, false);
            }
        }))
            return await inc.Done.Task;
    }

    private static void Finish(Incoming inc, bool ok)
    {
        if (ok)
        {
            try
            {
                inc.Stream!.Dispose();
                inc.Stream = null;
                File.Move(inc.Path + ".part", inc.Path!, true);
                inc.Done.TrySetResult(inc.Path);
                return;
            }
            catch { }
        }
        inc.Drop();
        inc.Done.TrySetResult(null);
    }

    // Network thread: true when the message was one of these.
    public bool OnMessage(Msg m)
    {
        if (m.T is not ("fileStart" or "fileEnd")) return false;
        if (m.Id is not { } id) return true;
        lock (_in)
        {
            if (!_in.TryGetValue(id, out var inc)) return true;
            if (m.T == "fileStart")
            {
                inc.Drop();
                try
                {
                    var ext = m.Text is { Length: > 1 and < 10 } e && e.StartsWith('.') && e.Skip(1).All(char.IsLetterOrDigit) ? e.ToLowerInvariant() : ".bin";
                    inc.Path = inc.Target(ext);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(inc.Path))!);
                    inc.Stream = new FileStream(inc.Path + ".part", FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
                    inc.Size = m.Size;
                    inc.Got = 0;
                }
                catch
                {
                    _in.Remove(id);
                    Finish(inc, false);
                }
                return true;
            }
            _in.Remove(id);
            Finish(inc, m.Flag && inc.Stream != null && inc.Got == inc.Size);
        }
        return true;
    }

    // Network thread.
    public void OnChunk(string id, ReadOnlyMemory<byte> data)
    {
        Action<long, long>? report = null;
        long got = 0, size = 0;
        lock (_in)
        {
            if (!_in.TryGetValue(id, out var inc) || inc.Stream == null) return;
            try
            {
                inc.Stream.Write(data.Span);
                inc.Got += data.Length;
            }
            catch
            {
                _in.Remove(id);
                Finish(inc, false);
                return;
            }
            long now = Environment.TickCount64;
            if (inc.Progress != null && now - inc.LastReport >= 200)
            {
                inc.LastReport = now;
                report = inc.Progress;
                got = inc.Got;
                size = inc.Size;
            }
        }
        report?.Invoke(got, size);
    }

    // The connection is gone: nothing more comes.
    public void FailAll()
    {
        lock (_in)
        {
            foreach (var inc in _in.Values) Finish(inc, false);
            _in.Clear();
        }
    }

    // Sends a file (null or missing: "it isn't here", and the other side stops waiting).
    public static async Task SendAsync(Connection c, string id, string? path, CancellationToken ct = default)
    {
        try
        {
            if (path == null || !File.Exists(path)) throw new FileNotFoundException();
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, true);
            await c.SendBulkAsync(new Msg { T = "fileStart", Id = id, Size = fs.Length, Text = System.IO.Path.GetExtension(path) }, ct);
            var buf = new byte[64 * 1024];
            int n;
            while ((n = await fs.ReadAsync(buf, ct)) > 0) await c.SendChunkAsync(id, buf, n, ct);
            await c.SendBulkAsync(new Msg { T = "fileEnd", Id = id, Flag = true }, ct);
        }
        catch
        {
            try { await c.SendBulkAsync(new Msg { T = "fileEnd", Id = id, Flag = false }); } catch { }
        }
    }
}
