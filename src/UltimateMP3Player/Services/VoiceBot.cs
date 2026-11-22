using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Discord;
using Discord.Audio;
using Discord.WebSocket;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using UltimateMP3Player.Audio;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Services;

// The user's own Discord bot, run by the app: it follows its application's owner into the server voice channel they're
// in and plays what the app plays, taken before the app's volume (each listener sets it in Discord, right-click the bot).
// Bots can't join private or group calls. Discord's voice is end-to-end encrypted (DAVE): Discord's libdave, downloaded
// into the engines folder the first time, like ffmpeg; opus and libsodium come with the app (NuGet).
public sealed class VoiceBot
{
    // libdave 1.3 changed its functions: Discord.Net 3.20 calls 1.2's.
    private const string DaveVersion = "1.2.2";

    private readonly AudioEngine _audio;
    private readonly AppSettings _settings;
    // Saves the settings on the UI thread (what the bot was given last).
    private readonly Action _save;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DiscordSocketClient? _client;
    private CancellationTokenSource? _run;
    private string? _token;
    private ulong _owner;
    // Where the owner is, as the gateway said last (events come in order; the joins run after, one at a time).
    private SocketVoiceChannel? _target;
    private SocketVoiceChannel? _in;
    private AudioOutStream? _out;

    // The tap's sound waiting to be sent, at the output's rate, and the same at Discord's 48 kHz (one pair, swapped whole).
    private sealed record Pipe(BufferedWaveProvider In, ISampleProvider Out);
    private volatile Pipe? _pipe;
    private byte[] _bytes = Array.Empty<byte>();

    public VoiceBot(AudioEngine audio, AppSettings settings, Action save)
    {
        _audio = audio;
        _settings = settings;
        _save = save;
    }

    // Any thread.
    public event Action? Changed;
    public string Status { get; private set; } = "";
    // The bot's owner's name on Discord, once connected: its default name is made of it.
    public string? OwnerName { get; private set; }

    // (Discord's usernames are at most 32 characters)
    public static string DefaultName(string owner) => ("UMP - " + owner) is var n && n.Length > 32 ? n[..32] : n;

    // What the app plays ("Title · Artist", null = nothing or paused), and what the bot shows of it: "Listening to …" under
    // its name, only while it plays it in a channel. On the UI thread (AppHost.UpdateTimeline); sent only when it changes.
    private string? _song, _shown;
    private readonly object _showing = new();

    public void Song(string? song)
    {
        _song = song is { Length: > 128 } ? song[..127] + "…" : song;
        ShowSong();
    }

    private void ShowSong()
    {
        var client = _client;
        string? want = _out != null ? _song : null;
        lock (_showing)
        {
            if (want == _shown || client is not { ConnectionState: ConnectionState.Connected }) return;
            _shown = want;
        }
        _ = ShowAsync(client, want);
    }

    private static async Task ShowAsync(DiscordSocketClient client, string? song)
    {
        try { await client.SetActivityAsync(song != null ? new Game(song, ActivityType.Listening) : null); }
        catch (Exception e) { AppPaths.AppendLog("Discord bot: activity: " + e.Message); }
    }

    // libdave has a build for this computer (none for Windows on ARM).
    public static bool Possible => DaveZip != null;

    private static string? DaveZip
    {
        get
        {
            var arch = RuntimeInformation.ProcessArchitecture;
            string? os = arch is not (Architecture.X64 or Architecture.Arm64) ? null
                : OperatingSystem.IsWindows() ? (arch == Architecture.X64 ? "Windows-X64" : null)
                : OperatingSystem.IsMacOS() ? (arch == Architecture.Arm64 ? "macOS-ARM64" : "macOS-X64")
                : OperatingSystem.IsLinux() ? (arch == Architecture.Arm64 ? "Linux-ARM64" : "Linux-X64")
                : null;
            return os == null ? null : $"https://github.com/discord/libdave/releases/download/v{DaveVersion}/cpp/libdave-{os}-boringssl.zip";
        }
    }

    private static string DaveExt => OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";
    // (the version in the name: another one, for a later Discord.Net, never takes this one's place by mistake)
    private static string DaveFile => Path.Combine(Engines.Dir, $"libdave-{DaveVersion}{DaveExt}");

    // The page that adds the bot to a server, allowed to connect and speak. A bot's token starts with its id (its
    // application's) in base64.
    public static string? InviteUrl(string? token)
    {
        var first = token?.Trim().Split('.')[0];
        if (string.IsNullOrEmpty(first)) return null;
        try
        {
            var b64 = first.Replace('-', '+').Replace('_', '/');
            var id = Encoding.ASCII.GetString(Convert.FromBase64String(b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=')));
            return id.Length is >= 15 and <= 22 && id.All(char.IsAsciiDigit)
                ? $"https://discord.com/oauth2/authorize?client_id={id}&scope=bot&permissions=3145728" : null;
        }
        catch (FormatException) { return null; }
    }

    public void Start(string token)
    {
        if (_run != null && token == _token) return;
        Stop();
        _token = token;
        _run = new CancellationTokenSource();
        _ = RunAsync(token, _run.Token);
    }

    // Leaves the channel and goes offline; wait = how long the caller can wait for it (closing the app).
    public void Stop(int wait = 0)
    {
        var run = _run;
        if (run == null) return;
        _run = null;
        _token = null;
        run.Cancel();
        var client = _client;
        _client = null;
        var done = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try { await LeaveAsync(); }
            finally { _gate.Release(); }
            if (client == null) return;
            try { await client.StopAsync(); } catch { }
            client.Dispose();
        });
        if (wait > 0) done.Wait(wait);
        Set("");
    }

    private void Set(string status)
    {
        Status = status;
        Changed?.Invoke();
    }

    private async Task RunAsync(string token, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(DaveFile))
            {
                Set(L.T("Downloading Discord's voice library…"));
                await DownloadDaveAsync(ct);
            }
            UseDave();
            Set(L.T("Connecting to Discord…"));
            var client = new DiscordSocketClient(new DiscordSocketConfig
            {
                // (neither is privileged: nothing to switch on in the developer portal)
                GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates,
                EnableVoiceDaveEncryption = true,
                LogGatewayIntentWarnings = false,
            });
            if (ct.IsCancellationRequested) { client.Dispose(); return; }
            _client = client;
            client.Log += m =>
            {
                if (m.Severity <= LogSeverity.Error) AppPaths.AppendLog($"Discord bot: {m.Source}: {m.Message} {m.Exception}");
                return Task.CompletedTask;
            };
            // (the gateway waits for its handlers: the joins run apart)
            client.Ready += () =>
            {
                _ = Task.Run(() => ReadyAsync(client, ct));
                return Task.CompletedTask;
            };
            client.UserVoiceStateUpdated += (user, before, after) =>
            {
                // (a client being stopped says nothing more: a new one may be running already)
                if (!ct.IsCancellationRequested && user.Id == _owner && _owner != 0)
                {
                    _target = after.VoiceChannel;
                    _ = Task.Run(() => FollowAsync(ct));
                }
                return Task.CompletedTask;
            };
            await client.LoginAsync(TokenType.Bot, token);
            await client.StartAsync();
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            AppPaths.AppendLog("Discord bot: " + e);
            Set(e is Discord.Net.HttpException { HttpCode: System.Net.HttpStatusCode.Unauthorized }
                ? L.T("Discord refused the token: copy it again from your application's Bot page")
                : L.F("Couldn't connect to Discord: {0}", e.Message));
        }
        catch { }
    }

    // Connected (again): whose bot it is, and where they are now.
    private async Task ReadyAsync(DiscordSocketClient client, CancellationToken ct)
    {
        try
        {
            var app = await client.GetApplicationInfoAsync();
            _owner = app.Team?.OwnerUserId ?? app.Owner?.Id ?? 0;
            OwnerName = app.Owner?.GlobalName ?? app.Owner?.Username;
            // (a new session starts with no activity)
            lock (_showing) _shown = null;
            _target = client.Guilds.Select(g => g.GetUser(_owner)?.VoiceChannel).FirstOrDefault(c => c != null);
            await FollowAsync(ct);
            if (!ct.IsCancellationRequested) await LookAsync(client);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            AppPaths.AppendLog("Discord bot: " + e);
            Set(L.F("Couldn't connect to Discord: {0}", e.Message));
        }
        catch { }
    }

    private async Task FollowAsync(CancellationToken ct)
    {
        try { await _gate.WaitAsync(ct); }
        catch (OperationCanceledException) { return; }
        var target = _target;
        try
        {
            if (ct.IsCancellationRequested) return;
            if (target != null && target.Id == _in?.Id && target.Guild.AudioClient?.ConnectionState == ConnectionState.Connected) return;
            await LeaveAsync();
            if (target == null)
            {
                Set(L.T("On: it joins you when you're in a voice channel of a server it's in"));
                return;
            }
            var where = $"{target.Name} ({target.Guild.Name})";
            Set(L.F("Joining {0}…", where));
            // (before connecting: a join that fails halfway is left too)
            _in = target;
            // Deafened: it never hears the channel (less to receive and decrypt).
            var voice = await target.ConnectAsync(selfDeaf: true);
            // (Discord.Net's encoder goes up to 128 kbps; a channel's own bitrate is 64 unless the server is boosted)
            var output = voice.CreatePCMStream(AudioApplication.Music, Math.Min(target.Bitrate, 128 * 1024), 200);
            _out = output;
            _audio.Tap = OnTap;
            DjEngine.Tap = OnDjTap;
            _ = SendAsync(output, ct);
            ShowSong();
            Set(L.F("Playing in {0}", where));
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            AppPaths.AppendLog("Discord bot: " + e);
            await LeaveAsync();
            Set(L.F("Couldn't join {0}: the bot must be allowed to connect and speak there", target?.Name ?? ""));
        }
        catch { }
        finally { _gate.Release(); }
    }

    // (inside the gate)
    private async Task LeaveAsync()
    {
        _audio.Tap = null;
        DjEngine.Tap = null;
        _pipe = null;
        var output = _out;
        var channel = _in;
        _out = null;
        _in = null;
        try { output?.Dispose(); } catch { }
        ShowSong();
        if (channel != null)
            try { await channel.DisconnectAsync(); } catch { }
    }

    // The DJ's decks, while they play, go before the player (the two are never mixed: a deck starting pauses the player,
    // and for a moment both could still be heard).
    private long _djUntil;

    private void OnDjTap(float[] samples, int count, int rate)
    {
        Volatile.Write(ref _djUntil, Environment.TickCount64 + 250);
        Add(samples, count, rate);
    }

    private void OnTap(float[] samples, int count, int rate)
    {
        if (Environment.TickCount64 >= Volatile.Read(ref _djUntil)) Add(samples, count, rate);
    }

    private readonly object _adding = new();

    // On an audio thread (the player's or the DJ's): a copy into the waiting buffer, nothing more.
    private void Add(float[] samples, int count, int rate)
    {
        lock (_adding) AddLocked(samples, count, rate);
    }

    private void AddLocked(float[] samples, int count, int rate)
    {
        var pipe = _pipe;
        if (pipe == null || pipe.In.WaveFormat.SampleRate != rate)
        {
            // A second of room: the output's clock and Discord's drift apart by a little; past that the newest is dropped.
            var buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(rate, 2))
            {
                BufferDuration = TimeSpan.FromSeconds(1),
                DiscardOnBufferOverflow = true,
            };
            var samplesIn = buffer.ToSampleProvider();
            _pipe = pipe = new Pipe(buffer, rate == 48000 ? samplesIn : new WdlResamplingSampleProvider(samplesIn, 48000));
        }
        int bytes = count * sizeof(float);
        if (_bytes.Length < bytes) _bytes = new byte[bytes];
        Buffer.BlockCopy(samples, 0, _bytes, 0, bytes);
        pipe.In.AddSamples(_bytes, 0, bytes);
    }

    // 20 ms at a time, as Discord wants it (48 kHz, 16 bits, stereo); its stream paces the writes. Nothing heard (paused,
    // between songs): nothing written, and Discord stops showing the bot speaking.
    private async Task SendAsync(AudioOutStream output, CancellationToken ct)
    {
        const int frame = 960 * 2;
        var floats = new float[frame];
        var pcm = new byte[frame * 2];
        try
        {
            while (!ct.IsCancellationRequested && ReferenceEquals(output, _out))
            {
                var pipe = _pipe;
                // (a little more than a frame: the resampler reads ahead; nothing at all, paused: looked at less often)
                if (pipe == null || pipe.In.BufferedDuration < TimeSpan.FromMilliseconds(30))
                {
                    await Task.Delay(pipe == null || pipe.In.BufferedBytes == 0 ? 50 : 10, ct);
                    continue;
                }
                int n = pipe.Out.Read(floats, 0, frame);
                for (int i = 0; i < n; i++)
                    BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)(Math.Clamp(floats[i], -1f, 1f) * short.MaxValue));
                await output.WriteAsync(pcm, 0, n * 2, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (ReferenceEquals(output, _out)) AppPaths.AppendLog("Discord bot: " + e);
        }
    }

    // ------------------------------------------------------------------ name and picture

    // The name or picture changed in Settings: given to the bot now, if it's online.
    public void Restyle()
    {
        if (_client is { ConnectionState: ConnectionState.Connected } client) _ = Task.Run(() => LookAsync(client));
    }

    // "UMP - <owner>" and the app's logo, or the user's own: given only when different from what the app gave last.
    private async Task LookAsync(DiscordSocketClient client)
    {
        if (client.CurrentUser is not { } me) return;
        var mine = File.Exists(AppPaths.BotPicture);
        var nameKey = $"{me.Id}|{_settings.DiscordBotName ?? DefaultName(OwnerName ?? me.Username)}";
        var pictureKey = $"{me.Id}|{(mine ? "mine " + File.GetLastWriteTimeUtc(AppPaths.BotPicture).Ticks : "logo")}";
        bool name = nameKey != _settings.DiscordBotNameSet, picture = pictureKey != _settings.DiscordBotPictureSet;
        if (!name && !picture) return;
        try
        {
            byte[]? bytes = !picture ? null : mine ? await File.ReadAllBytesAsync(AppPaths.BotPicture)
                : AppInfo.LogoUrl is { } logo ? await Http.GetBytesAsync(logo) : null;
            using var stream = bytes != null ? new MemoryStream(bytes) : null;
            // (a refusal comes back at once: Discord.Net would otherwise wait out the limit on names, up to an hour)
            await me.ModifyAsync(p =>
            {
                if (name) p.Username = nameKey[(nameKey.IndexOf('|') + 1)..];
                if (stream != null) p.Avatar = new Image(stream);
            }, new RequestOptions { RetryMode = RetryMode.AlwaysFail });
            if (name) _settings.DiscordBotNameSet = nameKey;
            if (stream != null) _settings.DiscordBotPictureSet = pictureKey;
            _save();
        }
        catch (Exception e)
        {
            AppPaths.AppendLog("Discord bot: name or picture: " + e);
            Set(e is Discord.Net.RateLimitedException || e is Discord.Net.HttpException { HttpCode: System.Net.HttpStatusCode.TooManyRequests }
                ? L.T("Discord lets a bot change its name only a few times an hour: the new one is given when the bot connects again later")
                : L.F("Discord didn't take the bot's name or picture: {0}", e.Message));
        }
    }

    // ------------------------------------------------------------------ libdave

    private static async Task DownloadDaveAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Engines.Dir);
        var zip = DaveFile + ".zip";
        try
        {
            await Http.DownloadFileAsync(DaveZip!, zip, null, null, ct);
            using var archive = ZipFile.OpenRead(zip);
            var entry = archive.Entries.FirstOrDefault(e => e.Name == "libdave" + DaveExt)
                        ?? throw new IOException("libdave" + DaveExt + " isn't in " + DaveZip);
            entry.ExtractToFile(DaveFile + ".part", true);
            File.Move(DaveFile + ".part", DaveFile, true);
        }
        finally
        {
            try { File.Delete(zip); } catch { }
        }
    }

    private static int _resolving;

    // Discord.Net looks for "libdave" beside the app: here it's in the engines folder, under its versioned name.
    private static void UseDave()
    {
        if (Interlocked.Exchange(ref _resolving, 1) == 1) return;
        NativeLibrary.SetDllImportResolver(typeof(Discord.LibDave.DaveSession).Assembly,
            (name, _, _) => name == "libdave" ? NativeLibrary.Load(DaveFile) : IntPtr.Zero);
    }
}
