using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Services;

// Discord Rich Presence over the local IPC pipe.
public sealed class DiscordPresence : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly HttpClient Web = new() { Timeout = TimeSpan.FromSeconds(6) };

    private readonly SemaphoreSlim _gate = new(1, 1);
    // The named pipe on Windows, a Unix socket elsewhere.
    private Stream? _pipe;
    private Wanted? _wanted;
    private string? _sent;
    private DateTime _retryAfter;
    private bool _enabled;
    // A paused song stays shown for a while, but only once something played (not the song the app opens with).
    private static readonly TimeSpan PauseShown = TimeSpan.FromMinutes(5);
    private bool _played;
    private DateTime? _pausedAt;

    // The Listen together room: Discord shows its size, "(3 of 16)".
    public sealed record Room(string Id, string Name, int Members, int Max);

    // What should be shown (a song, a room, or both); the JSON is built when sending, once the cover has been checked.
    private sealed record Wanted(Track? Track, long Start, long? End, bool Paused, Room? Room);

    public bool Available => AppInfo.DiscordAppId.Length > 0;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value) Push(null);
        }
    }

    // Playing: song with progress bar; paused: the song without it for 5 minutes, then nothing (only the room, in one).
    public void Update(Track? t, bool playing, TimeSpan position, TimeSpan duration, Room? room = null)
    {
        if (!Enabled || !Available) return;
        if (t != null && playing)
        {
            _played = true;
            _pausedAt = null;
            // (to the second: the same song at the same place sends nothing new; Discord takes 5 changes every 20 seconds)
            long start = (DateTimeOffset.UtcNow - position).ToUnixTimeSeconds() * 1000;
            Push(new Wanted(t, start, duration > TimeSpan.Zero ? start + (long)duration.TotalMilliseconds : null, false, room));
            return;
        }
        if (t != null && _played)
        {
            if (_pausedAt == null)
            {
                _pausedAt = DateTime.UtcNow;
                _ = ForgetPause(_pausedAt.Value);
            }
            if (DateTime.UtcNow - _pausedAt < PauseShown)
            {
                Push(new Wanted(t, 0, null, true, room));
                return;
            }
        }
        Push(room != null ? new Wanted(null, 0, null, false, room) : null);
    }

    // Still paused since then: the song goes (on the UI thread, like Update).
    private async Task ForgetPause(DateTime at)
    {
        await Task.Delay(PauseShown);
        if (_pausedAt != at || _wanted is not { Paused: true } w) return;
        _played = false;
        Push(w.Room != null ? new Wanted(null, 0, null, false, w.Room) : null);
    }

    private static string Build(Wanted w, string? art)
    {
        var t = w.Track;
        var state = new List<string>();
        if (t != null) state.Add(t.DisplayArtist);
        if (w.Paused) state.Add(L.T("Paused"));
        if (w.Room != null) state.Add(L.T("Listen together"));
        var activity = new Dictionary<string, object?>
        {
            ["type"] = 2,
            // The status under the name: the song's title, or the app's name for a room alone.
            ["status_display_type"] = t != null ? 2 : 0,
            ["details"] = Fit(t?.Title ?? w.Room!.Name),
            ["state"] = Fit(string.Join(" · ", state.Where(s => !string.IsNullOrWhiteSpace(s)))),
            ["assets"] = new Dictionary<string, string?>
            {
                ["large_image"] = art ?? "logo",
                ["large_text"] = Fit(t != null ? t.Album ?? t.Title : w.Room!.Name),
            },
        };
        // Left out rather than null (Discord refuses a null; a dictionary's nulls are written anyway).
        if (t != null && !w.Paused)
            activity["timestamps"] = w.End is long end ? new Dictionary<string, long> { ["start"] = w.Start, ["end"] = end }
                : new Dictionary<string, long> { ["start"] = w.Start };
        if (w.Room is { } r)
            activity["party"] = new Dictionary<string, object> { ["id"] = r.Id, ["size"] = new[] { Math.Max(1, r.Members), Math.Max(r.Members, r.Max) } };
        if (AppInfo.RepoUrl is { } repo)
            activity["buttons"] = new[] { new Dictionary<string, string> { ["label"] = "Ultimate MP3 Player", ["url"] = repo } };
        return JsonSerializer.Serialize(activity, Json);
    }

    private static string Fit(string s)
    {
        s = s.Trim();
        if (s.Length > 128) s = s[..127] + "…";
        return s.Length < 2 ? s + "  " : s;
    }

    // ------------------------------------------------------------------ cover

    // Discord fetches the picture itself and silently shows nothing when it can't: a 404 (YouTube's "maxresdefault"
    // doesn't exist for every video), or a huge file (SoundCloud's "-original" artwork can be 3000×3000 and several MB).
    // So the cover goes as a small, always existing version, is checked once, and the app icon stands in when it fails.
    private static readonly Dictionary<string, bool> Checked = new();

    private static IEnumerable<string> Candidates(Track t)
    {
        if (t.ArtUrl is { } art && Friendly(art) is { } a) yield return a;
        var yt = t.Keys.FirstOrDefault(k => k.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase));
        if (yt != null) yield return $"https://i.ytimg.com/vi/{yt[8..]}/hqdefault.jpg";
    }

    // The same picture in a size Discord takes.
    public static string? Friendly(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return null;
        if (url.Contains("sndcdn.com", StringComparison.OrdinalIgnoreCase))
            url = Regex.Replace(url, @"-(original|large|crop|t\d+x\d+)\.(jpg|jpeg|png|webp)(\?.*)?$", "-t500x500.jpg", RegexOptions.IgnoreCase);
        var yt = Regex.Match(url, @"^https?://i\d?\.ytimg\.com/(vi|vi_webp)/([\w-]{11})/", RegexOptions.IgnoreCase);
        if (yt.Success) url = $"https://i.ytimg.com/vi/{yt.Groups[2].Value}/hqdefault.jpg";
        return url.Length <= 256 ? url : null;
    }

    private static async Task<string?> ArtFor(Track? t)
    {
        foreach (var url in t != null ? Candidates(t).Distinct() : [])
            if (await Works(url)) return url;
        return AppInfo.LogoUrl is { } logo && await Works(logo) ? logo : null;
    }

    private static async Task<bool> Works(string url)
    {
        lock (Checked)
            if (Checked.TryGetValue(url, out var ok)) return ok;
        bool good;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var res = await Web.SendAsync(req);
            var type = res.Content.Headers.ContentType?.MediaType ?? "";
            long size = res.Content.Headers.ContentLength ?? 0;
            good = res.IsSuccessStatusCode && (type.Length == 0 || type.StartsWith("image/")) && size < 3_000_000;
        }
        catch (HttpRequestException) { good = false; }
        catch (TaskCanceledException) { return false; }
        lock (Checked) Checked[url] = good;
        return good;
    }

    // ------------------------------------------------------------------ pipe

    private void Push(Wanted? w)
    {
        _wanted = w;
        _ = Task.Run(Flush);
    }

    private async Task Flush()
    {
        await _gate.WaitAsync();
        try
        {
            var w = _wanted;
            var wanted = w == null ? null : Build(w, await ArtFor(w.Track));
            // A newer song came while the cover was being checked: that one goes next.
            if (!ReferenceEquals(w, _wanted) || wanted == _sent) return;
            if (!await ConnectAsync()) return;
            var payload = "{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":" + Environment.ProcessId +
                          (wanted != null ? ",\"activity\":" + wanted : "") + "},\"nonce\":\"" + Guid.NewGuid() + "\"}";
            await WriteAsync(1, payload);
            await ReadAsync();
            _sent = wanted;
        }
        catch { Disconnect(); }
        finally { _gate.Release(); }
    }

    private async Task<bool> ConnectAsync()
    {
        if (_pipe != null) return true;
        if (DateTime.Now < _retryAfter) return false;
        for (int i = 0; i < 10; i++)
        {
            var pipe = await OpenAsync(i);
            if (pipe == null) continue;
            try
            {
                _pipe = pipe;
                await WriteAsync(0, "{\"v\":1,\"client_id\":\"" + AppInfo.DiscordAppId + "\"}");
                var (op, _) = await ReadAsync();
                if (op == 1) return true;
                Disconnect();
                break;
            }
            catch
            {
                pipe.Dispose();
                if (_pipe == pipe) _pipe = null;
            }
        }
        // Discord is not running: try again later.
        _retryAfter = DateTime.Now.AddSeconds(30);
        return false;
    }

    // Discord's socket number i: a named pipe on Windows; on Linux and macOS a Unix socket in the runtime or temp folder
    // (also where the Flatpak and Snap builds of Discord put it).
    private static async Task<Stream?> OpenAsync(int i)
    {
        var name = "discord-ipc-" + i;
        if (OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(300);
                return pipe;
            }
            catch
            {
                pipe.Dispose();
                return null;
            }
        }
        var roots = new[] { "XDG_RUNTIME_DIR", "TMPDIR", "TMP", "TEMP" }.Select(Environment.GetEnvironmentVariable).OfType<string>().Append("/tmp").Distinct();
        foreach (var root in roots)
            foreach (var sub in new[] { "", "app/com.discordapp.Discord", "snap.discord", ".flatpak/com.discordapp.Discord/xdg-run" })
            {
                var path = Path.Combine(root, sub, name);
                if (!File.Exists(path)) continue;
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
                try
                {
                    using var cts = new CancellationTokenSource(300);
                    await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(path), cts.Token);
                    return new System.Net.Sockets.NetworkStream(socket, true);
                }
                catch { socket.Dispose(); }
            }
        return null;
    }

    private async Task WriteAsync(int op, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + body.Length];
        BitConverter.GetBytes(op).CopyTo(frame, 0);
        BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
        body.CopyTo(frame, 8);
        await _pipe!.WriteAsync(frame);
        await _pipe.FlushAsync();
    }

    private async Task<(int Op, string Json)> ReadAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var head = await ReadExactly(8, cts.Token);
        int op = BitConverter.ToInt32(head, 0), len = BitConverter.ToInt32(head, 4);
        var body = len > 0 ? await ReadExactly(len, cts.Token) : Array.Empty<byte>();
        return (op, Encoding.UTF8.GetString(body));
    }

    private async Task<byte[]> ReadExactly(int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int got = 0;
        while (got < count)
        {
            int n = await _pipe!.ReadAsync(buf.AsMemory(got, count - got), ct);
            if (n <= 0) throw new IOException("closed");
            got += n;
        }
        return buf;
    }

    private void Disconnect()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        _sent = null;
    }

    // Discord clears the activity when the pipe closes.
    public void Dispose() => Disconnect();
}
