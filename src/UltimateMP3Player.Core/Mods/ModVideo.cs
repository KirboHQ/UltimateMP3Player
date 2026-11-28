using System.Security.Cryptography;
using System.Text;

namespace UltimateMP3Player.Core.Mods;

// A video under a mod's "page" visualizer (an osu! beatmap's: AVI, FLV, MP4) for a player that only takes some kinds (Windows'
// MediaElement: MP4, WMV): the others made into a muted H.264 copy once, by ffmpeg, kept in the cache and found again next time.
public static class ModVideo
{
    private static readonly HashSet<string> Playable = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".wmv" };
    private static readonly Dictionary<string, Task<string?>> Making = new();

    public static Task<string?> PlayableAsync(string path)
    {
        if (Playable.Contains(Path.GetExtension(path))) return Task.FromResult<string?>(path);
        string target;
        try
        {
            var stamp = path + "|" + File.GetLastWriteTimeUtc(path).Ticks;
            var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp)))[..24].ToLowerInvariant() + ".mp4";
            target = Path.Combine(AppPaths.CacheDir, "mod-videos", name);
        }
        catch (Exception) { return Task.FromResult<string?>(null); }
        if (File.Exists(target)) return Task.FromResult<string?>(target);
        lock (Making)
        {
            if (!Making.TryGetValue(target, out var task)) Making[target] = task = Task.Run(() => MakeAsync(path, target));
            return task;
        }
    }

    private static async Task<string?> MakeAsync(string path, string target)
    {
        if (!File.Exists(Engines.Ffmpeg)) return null;
        var part = Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target) + ".part.mp4");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var p = ChildProcess.Start(Engines.Ffmpeg, new[]
            {
                "-y", "-v", "error", "-i", path, "-an", "-c:v", "libx264", "-preset", "veryfast", "-crf", "26", "-pix_fmt", "yuv420p",
                "-vf", "scale='min(1280,iw)':-2", "-movflags", "+faststart", part,
            }, errors: false);
            p.LowerPriority();
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            await p.WaitForExitAsync(cts.Token);
            if (p.ExitCode != 0 || !File.Exists(part)) return null;
            File.Move(part, target, true);
            return target;
        }
        catch (Exception ex)
        {
            AppPaths.AppendLog($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] mod video {Path.GetFileName(path)}: {ex.Message}\n");
            try { File.Delete(part); } catch (Exception) { }
            return null;
        }
        finally
        {
            lock (Making) Making.Remove(target);
        }
    }
}
