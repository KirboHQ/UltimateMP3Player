using System.Text.Json;
using System.Text.Json.Serialization;

namespace UltimateMP3Player.Core;

public static class AppPaths
{
    private static string? _dataDir;
    // Decided the first time it's needed, not when the class is loaded: the Android app sets UMP_DATA as it starts, and
    // its compiled code may load this class before that.
    public static string DataDir => _dataDir ??= Init();

    private static string Init()
    {
        var env = Environment.GetEnvironmentVariable("UMP_DATA");
        var dir = !string.IsNullOrEmpty(env)
            ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ultimate MP3 Player");
        Directory.CreateDirectory(dir);
        // Names of 3.7 and before.
        Rename(Path.Combine(dir, "ascolta-insieme"), Path.Combine(dir, "cache"));
        Rename(Path.Combine(dir, "errori.log"), Path.Combine(dir, "errors.log"));
        return dir;
    }

    // A file or folder that has another name since an older version: moved once to the new one.
    public static void Rename(string from, string to)
    {
        try
        {
            if (Directory.Exists(from))
            {
                // A start where the move failed (a file still open) left the new folder made and empty: tried again.
                // Delete refuses a folder with something in it.
                if (Directory.Exists(to)) Directory.Delete(to);
                Directory.Move(from, to);
            }
            else if (File.Exists(from) && !File.Exists(to)) File.Move(from, to);
        }
        catch { }
    }

    public static string LibraryFile => Path.Combine(DataDir, "library.json");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string ProfilesFile => Path.Combine(DataDir, "profiles.json");
    public static string CoversDir => Dir(Path.Combine(DataDir, "covers"));
    public static string LyricsDir => Dir(Path.Combine(DataDir, "lyrics"));
    // Songs heard without saving them (SongCache).
    public static string CacheDir => Path.Combine(DataDir, "cache");
    public static string LogFile => Path.Combine(DataDir, "errors.log");
    // Not created here, so deleted profiles stay deleted.
    public static string ProfileDir(string id) => Path.Combine(DataDir, "profiles", id);
    public static string TempDir => Dir(Path.Combine(Path.GetTempPath(), "UltimateMP3Player"));
    public static string DefaultMusicDir => MusicDirOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Ultimate MP3 Player");

    // Android: the app's own music folder (set by the app before the settings are read).
    public static string? MusicDirOverride { get; set; }

    // iPhone: the app's folder can get another name with an update, so a path saved before is moved under today's one
    // (set by the app as it starts; null elsewhere).
    public static Func<string, string>? MovedPath { get; set; }

    // The other device's songs while connected to it (Connect phone: "ph-…" on the computer, "pc-…" on the phone) keep their
    // covers apart, in the temporary folder: they're that device's.
    public static string TrackCover(string trackId) => Path.Combine(IsLinkId(trackId) ? LinkDir : CoversDir, trackId + ".jpg");
    public static bool IsLinkId(string id) => id.StartsWith("ph-", StringComparison.Ordinal) || id.StartsWith("pc-", StringComparison.Ordinal);
    public static string LinkDir => Dir(Path.Combine(TempDir, "link"));

    private static string Dir(string d)
    {
        Directory.CreateDirectory(d);
        return d;
    }
}

public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Load<T>(string path) where T : new()
    {
        foreach (var p in new[] { path, path + ".bak" })
        {
            try
            {
                if (File.Exists(p)) return JsonSerializer.Deserialize<T>(File.ReadAllText(p), Options) ?? new T();
            }
            catch { }
        }
        return new T();
    }

    // Atomic write, previous version kept as .bak.
    public static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", true);
        else File.Move(tmp, path);
    }
}

// Files to delete that were still open (a song's old file while it played, after "Download again"): deleted at the next
// start, unless a song of the library uses that path again.
public static class OldFiles
{
    private static string ListFile => Path.Combine(AppPaths.DataDir, "old-files.txt");
    private static readonly object Lock = new();

    // True when it's gone now.
    public static bool Delete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch
        {
            lock (Lock)
            {
                try { File.AppendAllLines(ListFile, new[] { path }); } catch { }
            }
            return false;
        }
    }

    // The ones left from last time (Library.Load).
    public static void Sweep(Func<string, bool> inUse)
    {
        lock (Lock)
        {
            try
            {
                if (!File.Exists(ListFile)) return;
                var left = File.ReadAllLines(ListFile).Where(p => p.Length > 0 && !inUse(p)).Distinct(StringComparer.OrdinalIgnoreCase).Where(p =>
                {
                    try
                    {
                        if (File.Exists(p)) File.Delete(p);
                        return false;
                    }
                    catch { return true; }
                }).ToList();
                if (left.Count == 0) File.Delete(ListFile);
                else File.WriteAllLines(ListFile, left);
            }
            catch { }
        }
    }
}

// Many save requests become one write.
public sealed class DebouncedSaver
{
    private readonly Action _save;
    private readonly TimeSpan _delay;
    private readonly object _lock = new();
    private Timer? _timer;

    public DebouncedSaver(Action save, int delayMs = 800)
    {
        _save = save;
        _delay = TimeSpan.FromMilliseconds(delayMs);
    }

    public void Request()
    {
        lock (_lock)
        {
            _timer ??= new Timer(_ => Flush());
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            try { _save(); } catch { }
        }
    }
}

public sealed class AppSettings
{
    // "it" or "en"; null until chosen (installer or first run).
    public string? Language { get; set; }
    public string MusicDir { get; set; } = AppPaths.DefaultMusicDir;
    // A Quality value: "mp3" (320 kbps), "mp3-256"/"-192"/"-128" or "original".
    public string AudioFormat { get; set; } = "mp3";
    public bool PreferAudioOnly { get; set; } = true;
    // Highest video resolution, null = best.
    public int? VideoMaxRes { get; set; } = 1080;
    public bool UseCookies { get; set; }
    // yt-dlp browser spec: "firefox" or "firefox:C:\profile".
    public string CookiesBrowser { get; set; } = "firefox";
    public int MaxParallel { get; set; } = 3;
    // Songs in the cloud and suggested ones start from their site while they download (off: once downloaded, less data).
    public bool PlayWhileDownloading { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    // Profile opened at startup, null = ask.
    public string? StartupProfile { get; set; }
    public bool AutoUpdateEngines { get; set; } = true;
    public DateTime? LastEngineUpdate { get; set; }
    public bool AutoUpdateApp { get; set; } = true;
    // The version that last showed its "What's new" (ReleaseNotes), "4.0.0"; null before 4.0.0.
    public string? LastVersion { get; set; }
    // Off: "Don't show again" was ticked (Settings › Updates turns it back on).
    public bool ShowWhatsNew { get; set; } = true;
    public bool Animations { get; set; } = true;
    public bool SmoothScroll { get; set; } = true;
    public bool CoverTilt { get; set; } = true;
    public bool DiscordPresence { get; set; } = true;
    // The output the music plays on (an id of AudioPlatform.OutputDevices: the endpoint's on Windows, the name on Linux
    // and macOS, a kind and a name on the phone), null = the system's default; its name, shown while it isn't connected.
    public string? OutputDevice { get; set; }
    public string? OutputDeviceName { get; set; }
    // Connect phone: whose music both devices show and where it's heard (the computer's choice counts when they connect).
    public Link.LinkMusic LinkMusic { get; set; }
    public Link.LinkAudio LinkAudio { get; set; }
    // "Listen together": who this installation is in rooms (kept, so a kicked person stays out),
    // how many songs of rooms are kept, the last choices when creating a room.
    public string? TogetherId { get; set; }
    [JsonPropertyName("TogetherCacheSize")] public int CacheSize { get; set; } = 10;
    public int TogetherMax { get; set; } = 8;
    // Permissions of who joins (3.1: pause and seek became one, speed is new; the 3.0 value isn't read).
    public int TogetherRoomPerms { get; set; } = 1;
    public string? TogetherLastAddress { get; set; }
    // Songs of the room's queue got ready ahead, as a guest and as the host (1-6).
    public int TogetherAhead { get; set; } = 2;
    public int TogetherHostAhead { get; set; } = 2;
    // P2P: as the host, send the songs to whoever takes them from you; as a guest, take them from the host first
    // (only when the host sends them). Off: from the link first, from someone in the room as a last resort.
    public bool TogetherSendAsHost { get; set; }
    public bool TogetherTakeFromHost { get; set; }
    // P2P at all: off, this computer neither receives songs from the room nor sends them to anyone (the two above too).
    public bool TogetherP2P { get; set; } = true;
    // Suggested songs: after a song that isn't in a list (search, downloads, played alone) the queue goes on with similar
    // songs found online instead of all your songs; where they come from ("auto", "mix" or a site), how many get ready ahead.
    public bool RadioAfterSingle { get; set; } = true;
    public string RadioSource { get; set; } = RadioSources.Auto;
    public int RadioAhead { get; set; } = 2;
    // Version of the terms of use accepted on this computer (0 = never).
    public int TermsAccepted { get; set; }
    // The search box also searches these sites (in this order); parallel = all at once, otherwise the next one
    // only when the one before found nothing.
    public bool OnlineSearch { get; set; } = true;
    public List<string> SearchServices { get; set; } = new(OnlineSearchServices.Default);
    public List<string> SearchServicesOff { get; set; } = new();
    public bool SearchParallel { get; set; } = true;
    // The user's own Spotify app (developer.spotify.com): with its key the search box also searches Spotify.
    public string? SpotifyClientId { get; set; }
    public string? SpotifyClientSecret { get; set; }
    // Lyrics searched by themselves: for new songs and for the song playing, when never searched before.
    public bool AutoLyrics { get; set; } = true;
    // The songs of a link get their audio saved on the device (the last choice of the download card); off = they go into
    // the library and the playlist in the cloud, heard from their link.
    public bool SaveLinkAudio { get; set; } = true;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public bool WindowMaximized { get; set; }

    // The logins made inside the app (the phone's: a cookies.txt written from its web page) instead of a browser's; set at
    // start by the Android app, null on the computers.
    public static string? CookieFile { get; set; }

    // What the engines get: "file:<cookies.txt>" or a browser spec.
    [JsonIgnore] public string? CookiesBrowserOrNull =>
        !UseCookies ? null
        : CookieFile != null ? (File.Exists(CookieFile) ? "file:" + CookieFile : null)
        : CookiesBrowser;

    // True when settings.json did not exist yet.
    [JsonIgnore] public bool IsNew { get; private set; }

    public static AppSettings Load()
    {
        bool isNew = !File.Exists(AppPaths.SettingsFile) && !File.Exists(AppPaths.SettingsFile + ".bak");
        var s = JsonStore.Load<AppSettings>(AppPaths.SettingsFile);
        s.IsNew = isNew;
        OnlineSearchServices.UseSpotifyKey(s.SpotifyClientId, s.SpotifyClientSecret);
        return s;
    }

    public void Save()
    {
        try { JsonStore.Save(AppPaths.SettingsFile, this); } catch { }
    }
}
