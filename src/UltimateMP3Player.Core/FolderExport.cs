using System.Text.RegularExpressions;

namespace UltimateMP3Player.Core;

// How a folder export went: the songs written, and the ones left out (no audio and no link that worked).
public sealed record FolderExportResult(int Written, int Skipped);

// Songs as plain MP3 files in a folder: for a USB stick, an SD card or the car's stereo, which don't know the app's packs.
// Each file is called "Artist - Title.mp3" and carries its title, artist, album, year and cover in ID3v2.3 tags (the ones
// car stereos read); MP3s are only re-tagged, the other formats become MP3 at 320 kbps, and the songs in the cloud are
// downloaded on the way. The names also suit FAT32 (no characters it refuses), the file system of most sticks and cards.
public static class FolderExport
{
    private static readonly Regex Unsafe = new(@"[\\/:*?""<>|\x00-\x1F]");

    // deliver(file, name): puts the finished file into the chosen folder (a plain copy on the computers; Android reaches its
    // folders through the system). progress(0..1, the song being written).
    public static async Task<FolderExportResult> WriteAsync(IReadOnlyList<Track> songs, string? cookies, Func<string, string, CancellationToken, Task> deliver,
        Action<double, string?> progress, CancellationToken ct)
    {
        var work = Path.Combine(AppPaths.TempDir, "export-" + Ids.New());
        Directory.CreateDirectory(work);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int written = 0, skipped = 0;
        try
        {
            for (int i = 0; i < songs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var t = songs[i];
                double from = (double)i / songs.Count, share = 1.0 / songs.Count;
                progress(from, t.Title);
                var dir = Path.Combine(work, i.ToString());
                Directory.CreateDirectory(dir);
                try
                {
                    var source = t.AudioPath;
                    if (source == null && t.HasLink)
                    {
                        (source, _) = await CloudSongs.DownloadAsync(CloudSongs.StreamUrl(t.SourceUrl!, t.Keys), t.Title, t.Artist, t.Duration, Quality.Mp3, cookies, dir,
                            pct => progress(from + share * 0.6 * pct / 100, t.Title), ct);
                    }
                    if (source == null)
                    {
                        skipped++;
                        continue;
                    }
                    var file = await ToMp3Async(t, source, dir, p => progress(from + share * (0.6 + 0.4 * p), t.Title), ct);
                    await deliver(file, NameFor(t, names), ct);
                    written++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is EngineException or IOException or HttpRequestException or UnauthorizedAccessException)
                {
                    skipped++;
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
            progress(1, null);
            return new FolderExportResult(written, skipped);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    // The song's audio with the library's tags and cover, as MP3 (only re-tagged when it already is one).
    private static async Task<string> ToMp3Async(Track t, string source, string dir, Action<double> progress, CancellationToken ct)
    {
        var probe = await Ffmpeg.ProbeAsync(source, ct);
        var tags = new Tags(t.Title, t.Artist, t.Album, t.Year, null, null);
        var cover = t.HasCover && File.Exists(AppPaths.TrackCover(t.Id)) ? AppPaths.TrackCover(t.Id) : null;
        var target = new AudioTarget(Quality.Mp3, null, null, null, 16);
        // (the iPhone, without ffmpeg: the system's decoders and LAME)
        if (AudioAnalysis.Native is { } native)
            return (await native.ConvertAudioAsync(source, probe, target, tags, cover, Path.Combine(dir, "song"), progress, ct)).File;
        var meta = Ffmpeg.WriteMetadata(dir, tags);
        var plan = Ffmpeg.ForAudio(source, probe, target, meta, cover, Path.Combine(dir, "song"));
        await Ffmpeg.RunAsync(plan.Args, probe.Duration ?? t.Duration, progress, ct);
        var output = plan.Args[^1];
        if (!File.Exists(output)) throw new EngineException(L.T("La conversione non ha prodotto alcun file."));
        return output;
    }

    // "Artist - Title.mp3", without the characters FAT32 refuses; a second song with the same name gets " (2)".
    private static string NameFor(Track t, HashSet<string> taken)
    {
        var raw = string.IsNullOrWhiteSpace(t.Artist) ? t.Title : $"{t.Artist.Split(',')[0].Trim()} - {t.Title}";
        var name = Regex.Replace(Unsafe.Replace(raw, " "), @"\s+", " ").Trim().TrimEnd('.', ' ');
        if (name.Length > 120) name = name[..120].TrimEnd('.', ' ');
        if (name.Length == 0) name = "Song";
        var unique = name;
        for (int n = 2; !taken.Add(unique + ".mp3"); n++) unique = $"{name} ({n})";
        return unique + ".mp3";
    }
}
