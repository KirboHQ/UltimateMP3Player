using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace UltimateMP3Player.Core;

// One stream overlay: a Browser source for OBS with its own look (Settings › Stream overlay).
public sealed class OverlaySource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    // compact, boxy, gallery, minimal, macos, shell, discord, pill, neon, cassette, polaroid, broadcast
    public string Skin { get; set; } = "compact";
    // square, canvas, vinyl, none
    public string Cover { get; set; } = "vinyl";
    // The colours taken from the cover; otherwise Tint. Light: the pale look.
    public bool MagicColors { get; set; } = true;
    public bool Light { get; set; }
    public string Tint { get; set; } = "#7C5CFF";
    // Their animations: original, fade, slide-left/right/up/down, grow, shrink, swing-left/right, tilt-left/right.
    public string Reveal { get; set; } = "original";
    public string Exit { get; set; } = "original";
    // Empty: the default one. Any font installed on this computer (OBS draws with the same ones).
    public string Font { get; set; } = "";
    public string NothingTitle { get; set; } = "";
    public string NothingArtist { get; set; } = "";
    // When it's on screen: "playing" (with the music, gone HideAfter seconds after it stops), "change" (ShowFor seconds at every
    // new song), "always".
    public string Show { get; set; } = "playing";
    public int HideAfter { get; set; } = 5;
    public int ShowFor { get; set; } = 8;
    // Where it sits in the browser source (0 left or top, 1 right or bottom) and how much of the source it takes.
    public double AnchorX { get; set; } = 0.5;
    public double AnchorY { get; set; } = 0.5;
    public double Size { get; set; } = 1;
    // Its corners (0 square, 1 as drawn, 2 very round) and how solid its cards are (1 solid, lower: the stream shows through).
    public double Corners { get; set; } = 1;
    public double Solid { get; set; } = 1;
    // The light around the cover, the cover beating with the bass, the needle on the vinyl.
    public bool Glow { get; set; } = true;
    public bool Pulse { get; set; }
    public bool Arm { get; set; } = true;
    public bool ShowArtist { get; set; } = true;
    public bool ShowTime { get; set; } = true;
    // Inside the card: the small bars, the line of the progress, both or neither.
    public bool Bars { get; set; } = true;
    public bool ShowBar { get; set; }
    // The big bars behind the card, and where: Bottom = rising above it, Top = hanging below it, Both, Center = through its middle.
    public bool BarsBehind { get; set; }
    public VisualizerPlace BarsPlace { get; set; } = VisualizerPlace.Bottom;
    public VisualizerFlow Flow { get; set; } = VisualizerFlow.MirrorSides;
    public VisualizerStyle Style { get; set; } = VisualizerStyle.Bars;
}

// What's playing, as the overlays need it. CoverPath and VideoPath stay here: the pages only get a link to the current ones.
public sealed record OverlayState(string? Title, string? Artist, string? CoverKey, string? CoverPath, string? VideoKey, string? VideoPath,
    double Position, double Duration, double Rate, bool Playing, bool Live)
{
    public static readonly OverlayState Nothing = new(null, null, null, null, null, null, 0, 0, 1, false, false);

    public string Json() => JsonSerializer.Serialize(new
    {
        title = Title, artist = Artist,
        cover = CoverKey != null ? "/cover/" + CoverKey : null,
        video = VideoKey != null ? "/video/" + VideoKey : null,
        pos = Position, dur = Duration, rate = Rate, playing = Playing, live = Live,
        // (the bars' fade goes at the speed of the app's animations)
        speed = Glide.Speed,
    });
}

// The little web server behind the overlays: only on this computer (127.0.0.1, nothing for the firewall to ask), only while
// there's a source, and only these four addresses: a source's page (Overlay.html, one page drawing every look), its events
// (what's playing, the source's settings as they change, the bars at 60 a second while the music plays), the current cover,
// the current video. Nothing of the disk can be asked for by name, and the Host must be this computer (a web site can't
// reach it through a DNS name of its own).
public sealed class OverlayServer
{
    public static readonly OverlayServer Shared = new();

    public const int FirstPort = 47800;
    private const int PortsTried = 20;

    private readonly object _lock = new();
    private readonly List<Client> _clients = new();
    private List<OverlaySource> _sources = new();
    private OverlayState _state = OverlayState.Nothing;
    private TcpListener? _listener;
    private byte[]? _page;

    public int Port { get; private set; }
    public bool Running => _listener != null;
    public string LinkOf(OverlaySource s) => $"http://127.0.0.1:{Port}/s/{s.Id}";

    // The list of sources: the server starts with the first (on the last port if it's free: OBS keeps the link), stops with the
    // last. Returns the port in use, 0 if stopped or impossible.
    public int Apply(IReadOnlyList<OverlaySource> sources, int port)
    {
        lock (_lock) _sources = sources.ToList();
        if (sources.Count == 0)
        {
            Stop();
            return 0;
        }
        if (_listener != null) return Port;
        for (int i = -1; i < PortsTried; i++)
        {
            int p = i < 0 ? port : FirstPort + i;
            if (p <= 0) continue;
            try
            {
                var l = new TcpListener(IPAddress.Loopback, p);
                l.Start();
                _listener = l;
                Port = p;
                // (off the UI thread: started from it, every await of the pages would come back there)
                _ = Task.Run(() => AcceptLoop(l));
                return p;
            }
            catch (SocketException) { }
        }
        return 0;
    }

    public void Stop()
    {
        _listener?.Stop();
        _listener = null;
        Port = 0;
        lock (_lock)
        {
            foreach (var c in _clients) c.Close();
            _clients.Clear();
        }
    }

    // What's playing changed (a song, a pause, a jump; and every few seconds, for the clock).
    public void Push(OverlayState state)
    {
        _state = state;
        lock (_lock)
        {
            // (no page open, the usual case: nothing to write)
            if (_clients.Count == 0) return;
            var json = state.Json();
            foreach (var c in _clients) c.Send("state", json);
        }
    }

    // A source's settings changed: its pages follow at once (OBS's too), no reload.
    public void Changed(OverlaySource source)
    {
        var json = JsonSerializer.Serialize(source);
        lock (_lock)
            foreach (var c in _clients)
                if (c.Source == source) c.Send("config", json);
    }

    private async Task AcceptLoop(TcpListener listener)
    {
        while (_listener == listener)
        {
            try
            {
                var tcp = await listener.AcceptTcpClientAsync();
                _ = Serve(tcp);
            }
            catch (Exception) when (_listener != listener) { return; }
            catch (SocketException) { }
        }
    }

    private async Task Serve(TcpClient tcp)
    {
        using (tcp)
        {
            try
            {
                tcp.NoDelay = true;
                var stream = tcp.GetStream();
                if (await ReadHead(stream) is not { } head) return;
                var lines = head.Split("\r\n");
                var first = lines[0].Split(' ');
                if (first.Length < 3 || first[0] != "GET")
                {
                    await Answer(stream, "405 Method Not Allowed", "text/plain", "Only GET"u8.ToArray());
                    return;
                }
                string Header(string name) => lines.FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..].Trim() ?? "";
                var host = Header("Host");
                if (host != $"127.0.0.1:{Port}" && host != $"localhost:{Port}")
                {
                    await Answer(stream, "403 Forbidden", "text/plain", "Forbidden"u8.ToArray());
                    return;
                }
                var path = first[1];
                int query = path.IndexOf('?');
                if (query >= 0) path = path[..query];
                var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var state = _state;
                switch (parts)
                {
                    case ["s", var id] when Find(id) != null:
                        await Answer(stream, "200 OK", "text/html; charset=utf-8", Page());
                        break;
                    case ["events", var id] when Find(id) is { } source:
                        await Events(stream, source);
                        break;
                    case ["cover", var key] when key == state.CoverKey && state.CoverPath != null:
                        await SendFile(stream, state.CoverPath, "image/jpeg", "");
                        break;
                    case ["video", var key] when key == state.VideoKey && state.VideoPath != null:
                        await SendFile(stream, state.VideoPath, Path.GetExtension(state.VideoPath).ToLowerInvariant() == ".webm" ? "video/webm" : "video/mp4", Header("Range"));
                        break;
                    default:
                        await Answer(stream, "404 Not Found", "text/plain", "Not found"u8.ToArray());
                        break;
                }
            }
            // (OBS closed, the source hidden, the app closing)
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (OperationCanceledException) { }
        }
    }

    private OverlaySource? Find(string id)
    {
        lock (_lock) return _sources.FirstOrDefault(s => s.Id == id);
    }

    private byte[] Page()
    {
        if (_page != null) return _page;
        using var res = typeof(OverlayServer).Assembly.GetManifestResourceStream("UltimateMP3Player.Core.Overlay.html")!;
        using var mem = new MemoryStream();
        res.CopyTo(mem);
        return _page = mem.ToArray();
    }

    private static async Task<string?> ReadHead(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[8192];
        int got = 0;
        while (got < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(got), timeout.Token);
            if (n == 0) return null;
            got += n;
            var text = Encoding.ASCII.GetString(buffer, 0, got);
            int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0) return text[..end];
        }
        return null;
    }

    private static async Task Answer(Stream stream, string status, string type, byte[] body)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"));
        await stream.WriteAsync(body);
    }

    // A file, or the piece a Range asks for (a <video> needs them to play and to jump).
    private static async Task SendFile(Stream stream, string path, string type, string range)
    {
        FileStream file;
        try { file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await Answer(stream, "404 Not Found", "text/plain", "Not found"u8.ToArray());
            return;
        }
        using (file)
        {
            long length = file.Length, from = 0, to = length - 1;
            bool partial = false;
            if (range.StartsWith("bytes=", StringComparison.Ordinal) && range[6..].Split('-') is [var a, var b] && long.TryParse(a, out var start) && start >= 0 && start < length)
            {
                from = start;
                if (long.TryParse(b, out var end) && end >= start) to = Math.Min(end, length - 1);
                partial = true;
            }
            var head = (partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n")
                       + $"Content-Type: {type}\r\nContent-Length: {to - from + 1}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n"
                       + (partial ? $"Content-Range: bytes {from}-{to}/{length}\r\n" : "") + "\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            file.Seek(from, SeekOrigin.Begin);
            var buffer = new byte[65536];
            for (long left = to - from + 1; left > 0;)
            {
                int n = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)));
                if (n == 0) break;
                await stream.WriteAsync(buffer.AsMemory(0, n));
                left -= n;
            }
        }
    }

    // A page's stream of events. Each page has its own loop, so one that's stuck doesn't hold back the others.
    private async Task Events(Stream stream, OverlaySource source)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"));
        var client = new Client(stream, source);
        lock (_lock) _clients.Add(client);
        bool listening = false;
        try
        {
            client.Send("config", JsonSerializer.Serialize(source));
            client.Send("state", _state.Json());
            long lastWrite = Environment.TickCount64;
            var text = new StringBuilder();
            while (!client.Closed)
            {
                bool bars = _state.Playing && (source.Bars || source.BarsBehind || source.Glow || source.Pulse);
                if (bars != listening)
                {
                    listening = bars;
                    if (bars) Spectrum.Shared.Acquire();
                    else Spectrum.Shared.Release();
                }
                await client.WaitAsync(bars ? 16 : 5000);
                text.Clear();
                client.Drain(text);
                if (bars)
                {
                    text.Append("event: bars\ndata: ");
                    // (every second band is plenty for a card, and each one in two hex digits)
                    var bands = Spectrum.Shared.Read();
                    for (int i = 0; i < bands.Length; i += 2) text.Append(((int)(bands[i] * 255)).ToString("x2"));
                    text.Append("\n\n");
                }
                // (a comment now and then: a page gone is noticed by the write failing)
                if (text.Length == 0 && Environment.TickCount64 - lastWrite > 15000) text.Append(": still here\n\n");
                if (text.Length == 0) continue;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await stream.WriteAsync(Encoding.UTF8.GetBytes(text.ToString()), timeout.Token);
                lastWrite = Environment.TickCount64;
            }
        }
        finally
        {
            if (listening) Spectrum.Shared.Release();
            lock (_lock) _clients.Remove(client);
        }
    }

    private sealed class Client
    {
        private readonly Stream _stream;
        private readonly SemaphoreSlim _signal = new(0);
        private readonly List<(string Event, string Data)> _pending = new();
        public bool Closed { get; private set; }
        public OverlaySource Source { get; }

        public Client(Stream stream, OverlaySource source) => (_stream, Source) = (stream, source);

        public void Send(string name, string data)
        {
            lock (_pending)
            {
                // (only the last of a kind counts)
                _pending.RemoveAll(p => p.Event == name);
                _pending.Add((name, data));
            }
            if (_signal.CurrentCount == 0) _signal.Release();
        }

        public Task WaitAsync(int ms) => _signal.WaitAsync(ms);

        public void Drain(StringBuilder to)
        {
            lock (_pending)
            {
                foreach (var (name, data) in _pending) to.Append("event: ").Append(name).Append("\ndata: ").Append(data).Append("\n\n");
                _pending.Clear();
            }
        }

        public void Close()
        {
            Closed = true;
            try { _stream.Close(); } catch (IOException) { }
            _signal.Release();
        }
    }
}
