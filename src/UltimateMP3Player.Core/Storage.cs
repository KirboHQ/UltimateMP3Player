using System.Text.Json;
using System.Text.Json.Serialization;

namespace UltimateMP3Player.Core;

public static class AppPaths
{
    public static string DataDir { get; } = Init();

    private static string Init()
    {
        var env = Environment.GetEnvironmentVariable("UMP_DATA");
        var dir = !string.IsNullOrEmpty(env)
            ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ultimate MP3 Player");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string LibraryFile => Path.Combine(DataDir, "library.json");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string ProfilesFile => Path.Combine(DataDir, "profiles.json");
    public static string CoversDir => Dir(Path.Combine(DataDir, "covers"));
    // Not created here, so deleted profiles stay deleted.
    public static string ProfileDir(string id) => Path.Combine(DataDir, "profiles", id);
    public static string TempDir => Dir(Path.Combine(Path.GetTempPath(), "UltimateMP3Player"));
    public static string DefaultMusicDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Ultimate MP3 Player");

    public static string TrackCover(string trackId) => Path.Combine(CoversDir, trackId + ".jpg");

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
    // "mp3" (320 kbps) or "original".
    public string AudioFormat { get; set; } = "mp3";
    public bool PreferAudioOnly { get; set; } = true;
    // Highest video resolution, null = best.
    public int? VideoMaxRes { get; set; } = 1080;
    public bool UseCookies { get; set; }
    // yt-dlp browser spec: "firefox" or "firefox:C:\profile".
    public string CookiesBrowser { get; set; } = "firefox";
    public int MaxParallel { get; set; } = 3;
    public bool CloseToTray { get; set; } = true;
    // Profile opened at startup, null = ask.
    public string? StartupProfile { get; set; }
    public bool AutoUpdateEngines { get; set; } = true;
    public DateTime? LastEngineUpdate { get; set; }
    public bool AutoUpdateApp { get; set; } = true;
    public bool Animations { get; set; } = true;
    public bool SmoothScroll { get; set; } = true;
    public bool CoverTilt { get; set; } = true;
    public bool DiscordPresence { get; set; } = true;
    // "Listen together": who this installation is in rooms (kept, so a kicked person stays out),
    // how many songs of rooms are kept, the last choices when creating a room.
    public string? TogetherId { get; set; }
    public int TogetherCacheSize { get; set; } = 10;
    public int TogetherMax { get; set; } = 8;
    public int TogetherPerms { get; set; } = 1;
    public string? TogetherLastAddress { get; set; }
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public bool WindowMaximized { get; set; }

    [JsonIgnore] public string? CookiesBrowserOrNull => UseCookies ? CookiesBrowser : null;

    // True when settings.json did not exist yet.
    [JsonIgnore] public bool IsNew { get; private set; }

    public static AppSettings Load()
    {
        bool isNew = !File.Exists(AppPaths.SettingsFile) && !File.Exists(AppPaths.SettingsFile + ".bak");
        var s = JsonStore.Load<AppSettings>(AppPaths.SettingsFile);
        s.IsNew = isNew;
        return s;
    }

    public void Save()
    {
        try { JsonStore.Save(AppPaths.SettingsFile, this); } catch { }
    }
}
