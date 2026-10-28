using System.IO.Compression;
using System.Text.Json;

namespace UltimateMP3Player.Core;

// What a backup says about itself (backup.json inside the zip).
public sealed class BackupInfo
{
    public DateTime Created { get; set; }
    public string? Device { get; set; }
    public string? Version { get; set; }
    public int Songs { get; set; }
    public int Profiles { get; set; }
}

// Where the backups go: a folder of the computer, or on the phones one chosen through the system (a bookmark).
public interface IBackupFolder
{
    // What Settings shows.
    string Name { get; }
    Task<Stream> CreateAsync(string fileName);
    Task<List<string>> NamesAsync();
    Task DeleteAsync(string fileName);
}

public sealed class LocalBackupFolder : IBackupFolder
{
    public LocalBackupFolder(string path) => Dir = path;

    public string Dir { get; }
    public string Name => Dir;

    public Task<Stream> CreateAsync(string fileName)
    {
        Directory.CreateDirectory(Dir);
        return Task.FromResult<Stream>(File.Create(Path.Combine(Dir, fileName)));
    }

    public Task<List<string>> NamesAsync()
        => Task.FromResult(Directory.Exists(Dir) ? Directory.GetFiles(Dir).Select(Path.GetFileName).OfType<string>().ToList() : new List<string>());

    public Task DeleteAsync(string fileName)
    {
        File.Delete(Path.Combine(Dir, fileName));
        return Task.CompletedTask;
    }
}

// The data of the app (not the music): the library, the profiles with their playlists, tags and statistics, the settings,
// the covers and the lyrics, in one zip. Restoring one puts it in place of today's data; the app then starts again.
public static class Backup
{
    public const string Prefix = "UltimateMP3Player-backup-", Extension = ".zip", InfoName = "backup.json";
    // What goes in: these files and these folders of the data folder (the cache, the engines, the logins to the sites and the
    // log stay out: things to download again, or not to carry around).
    private static readonly string[] Files_ = { "library.json", "settings.json", "profiles.json", "artists.json" };
    private static readonly string[] Folders = { "profiles", "covers", "lyrics" };

    // "UltimateMP3Player-backup-2026-10-28-153000.zip"; the safety copy taken before a restore ends in "-before-restore".
    public static string FileName(DateTime at, bool beforeRestore = false)
        => Prefix + at.ToString("yyyy-MM-dd-HHmmss") + (beforeRestore ? "-before-restore" : "") + Extension;

    public static bool IsBackup(string name) => name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    // When a backup was made, from its name (null for another file).
    public static DateTime? DateOf(string name)
        => IsBackup(name) && name.Length >= Prefix.Length + 17 && DateTime.TryParseExact(name.Substring(Prefix.Length, 17), "yyyy-MM-dd-HHmmss", null,
            System.Globalization.DateTimeStyles.None, out var d) ? d : null;

    // The data as it's saved now (flush what's waiting first). Pictures are stored as they are: they don't shrink.
    public static BackupInfo Write(Stream output, string device, string version, int songs, int profiles)
    {
        var info = new BackupInfo { Created = DateTime.Now, Device = device, Version = version, Songs = songs, Profiles = profiles };
        var dir = AppPaths.DataDir;
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);
        foreach (var name in Files_)
            Add(zip, Path.Combine(dir, name), name, CompressionLevel.Optimal);
        foreach (var folder in Folders)
        {
            var root = Path.Combine(dir, folder);
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".tmp" or ".bak" or ".unreadable") continue;
                var entry = Path.GetRelativePath(dir, file).Replace('\\', '/');
                Add(zip, file, entry, ext is ".jpg" or ".jpeg" or ".png" or ".webp" ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            }
        }
        var e = zip.CreateEntry(InfoName);
        using (var s = e.Open()) JsonSerializer.Serialize(s, info, JsonStore.Options);
        return info;
    }

    private static void Add(ZipArchive zip, string path, string entry, CompressionLevel level)
    {
        // (a file being written right now is skipped, not a reason to fail the whole backup)
        try
        {
            if (!File.Exists(path)) return;
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = zip.CreateEntry(entry, level).Open();
            input.CopyTo(output);
        }
        catch (IOException) { }
    }

    // What a zip says about itself; null when it isn't a backup of the app.
    public static BackupInfo? Read(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            if (zip.GetEntry(InfoName) is not { } e || zip.GetEntry("library.json") == null) return null;
            using var s = e.Open();
            return JsonSerializer.Deserialize<BackupInfo>(s, JsonStore.Options);
        }
        catch { return null; }
    }

    // The oldest backups beyond keep are deleted (only the app's own, never the safety copies of a restore).
    public static async Task PruneAsync(IBackupFolder folder, int keep)
    {
        var mine = (await folder.NamesAsync()).Where(n => IsBackup(n) && !n.Contains("-before-restore") && DateOf(n) != null)
            .OrderByDescending(n => DateOf(n)).ToList();
        foreach (var old in mine.Skip(Math.Max(1, keep)))
            try { await folder.DeleteAsync(old); } catch { }
    }

    // Puts the backup in place of today's data. Nothing is saved any more afterwards (JsonStore.Frozen): what the app holds
    // in memory would write the old data back. The app must start again. What belongs to this device stays as it is: the
    // music folder, the audio output, the window, the identity in rooms, the browser for the cookies.
    public static void Restore(string zipPath, AppSettings current)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.GetEntry("library.json") == null) throw new InvalidDataException(L.T("This file isn't a backup of Ultimate MP3 Player."));
        JsonStore.Frozen = true;
        var dir = AppPaths.DataDir;
        foreach (var name in Files_)
            foreach (var p in new[] { name, name + ".bak" })
                try { File.Delete(Path.Combine(dir, p)); } catch { }
        foreach (var folder in Folders)
            try { if (Directory.Exists(Path.Combine(dir, folder))) Directory.Delete(Path.Combine(dir, folder), true); } catch { }
        var full = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        foreach (var e in zip.Entries)
        {
            if (e.FullName == InfoName || e.FullName.EndsWith('/')) continue;
            var target = Path.GetFullPath(Path.Combine(dir, e.FullName));
            // (an entry pointing outside the data folder isn't written)
            if (!target.StartsWith(full, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, true);
        }
        var path = AppPaths.SettingsFile;
        if (!File.Exists(path)) return;
        try
        {
            var restored = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonStore.Options) ?? new AppSettings();
            restored.MusicDir = current.MusicDir;
            restored.OutputDevice = current.OutputDevice;
            restored.OutputDeviceName = current.OutputDeviceName;
            restored.TogetherId = current.TogetherId;
            restored.CookiesBrowser = current.CookiesBrowser;
            restored.UseCookies = current.UseCookies;
            restored.BackupFolder = current.BackupFolder;
            restored.BackupFolderName = current.BackupFolderName;
            restored.LastBackup = current.LastBackup;
            restored.WindowWidth = current.WindowWidth;
            restored.WindowHeight = current.WindowHeight;
            restored.WindowMaximized = current.WindowMaximized;
            File.WriteAllText(path, JsonSerializer.Serialize(restored, JsonStore.Options));
        }
        catch { }
    }
}
