using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Services;

// Discord Rich Presence over the local IPC pipe.
public sealed class DiscordPresence : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private string? _wanted;
    private string? _sent;
    private DateTime _retryAfter;
    private bool _enabled;

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

    // Playing: song with progress bar; paused: cleared.
    public void Update(Track? t, bool playing, TimeSpan position, TimeSpan duration)
    {
        if (!Enabled || !Available) return;
        if (t == null || !playing)
        {
            Push(null);
            return;
        }
        var now = DateTimeOffset.UtcNow;
        long start = (now - position).ToUnixTimeMilliseconds();
        var activity = new Dictionary<string, object?>
        {
            ["type"] = 2,
            ["status_display_type"] = 2,
            ["details"] = Fit(t.Title),
            ["state"] = Fit(t.DisplayArtist),
            ["timestamps"] = duration > TimeSpan.Zero
                ? new Dictionary<string, long> { ["start"] = start, ["end"] = start + (long)duration.TotalMilliseconds }
                : new Dictionary<string, long> { ["start"] = start },
            ["assets"] = new Dictionary<string, string?>
            {
                ["large_image"] = Art(t) ?? "logo",
                ["large_text"] = Fit(t.Album ?? t.Title),
            },
        };
        if (AppInfo.RepoUrl is { } repo)
            activity["buttons"] = new[] { new Dictionary<string, string> { ["label"] = "Ultimate MP3 Player", ["url"] = repo } };
        Push(JsonSerializer.Serialize(activity, Json));
    }

    private static string Fit(string s)
    {
        s = s.Trim();
        if (s.Length > 128) s = s[..127] + "…";
        return s.Length < 2 ? s + "  " : s;
    }

    private static string? Art(Track t)
    {
        if (t.ArtUrl != null) return t.ArtUrl;
        var yt = t.Keys.FirstOrDefault(k => k.StartsWith("youtube:", StringComparison.OrdinalIgnoreCase));
        return yt != null ? $"https://i.ytimg.com/vi/{yt[8..]}/hqdefault.jpg" : AppInfo.LogoUrl;
    }

    private void Push(string? activityJson)
    {
        _wanted = activityJson;
        _ = Task.Run(Flush);
    }

    private async Task Flush()
    {
        await _gate.WaitAsync();
        try
        {
            var wanted = _wanted;
            if (wanted == _sent) return;
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
        if (_pipe is { IsConnected: true }) return true;
        if (DateTime.Now < _retryAfter) return false;
        for (int i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(300);
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
