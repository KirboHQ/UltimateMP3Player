namespace UltimateMP3Player.Core;

// ExtraKeys: the original link's identities when fetched elsewhere (its link then becomes the new one). AttachTo: the
// library song this is for (a song in the cloud saved on the device, a video found for a song), whatever the link says.
// VideoOnly: only the video, for AttachTo.
public sealed record TrackRequest(MediaItem Item, bool WantVideo, int? VideoMaxRes, string AudioFormat, string MusicDir, string? Cookies,
    IReadOnlyList<string>? ExtraKeys = null, string? AttachTo = null, bool VideoOnly = false);

// Saved: a song in the cloud whose audio is now on the device.
public enum TrackOutcome { Downloaded, AlreadyPresent, VideoAdded, Saved }

public sealed record TrackResult(Track Track, TrackOutcome Outcome, string? Note = null);

// Downloads one song into the library unless already there.
public static class TrackDownloader
{
    private static readonly Dictionary<string, Task> InFlight = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<TrackResult> RunAsync(Library lib, TrackRequest req, Action<JobProgress> progress, CancellationToken ct)
    {
        var item = req.Item;
        var keys = WithExtra(SourceKeys.ForItem(item), req);
        Track? target = null;
        if (req.AttachTo != null)
            target = lib.Get(req.AttachTo) ?? throw new EngineException(L.T("Il brano non è più nella libreria."));
        var known = target ?? lib.FindByKeys(keys);
        if (known == null && item.Source == SourceKind.Search) known = lib.FindSimilar(item.Title, item.Artist, item.Duration);
        // Already on the device (a song in the cloud gets its audio saved instead).
        if (known != null && (req.VideoOnly ? known.HasVideo : known.HasSavedFile && (!req.WantVideo || known.HasVideo)))
        {
            if (!req.VideoOnly) lib.AddKeys(known, keys);
            return new TrackResult(known, TrackOutcome.AlreadyPresent);
        }

        await ResolveAsync(item, req.Cookies, progress, ct);
        keys = WithExtra(SourceKeys.ForItem(item), req);
        var meta = SongMeta.From(item);
        double? duration = item.Info?.Duration ?? item.Duration;

        while (true)
        {
            Task? other;
            var mine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (InFlight)
            {
                other = keys.Select(k => InFlight.GetValueOrDefault(k)).FirstOrDefault(t => t != null);
                if (other == null)
                    foreach (var k in keys) InFlight[k] = mine.Task;
            }
            if (other != null)
            {
                progress(new JobProgress(JobPhase.Resolving, null, L.T("In attesa dello stesso brano in download…")));
                await other.WaitAsync(ct);
                continue;
            }
            try
            {
                return await DownloadAsync(lib, req, keys, meta, duration, target, progress, ct);
            }
            finally
            {
                lock (InFlight)
                    foreach (var k in keys)
                        if (InFlight.TryGetValue(k, out var t) && t == mine.Task) InFlight.Remove(k);
                mine.TrySetResult();
            }
        }
    }

    private static List<string> WithExtra(List<string> keys, TrackRequest req)
        => req.ExtraKeys == null ? keys : keys.Concat(req.ExtraKeys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // Same song searched on YouTube Music, for blocked links.
    public static MediaItem YouTubeFallback(MediaItem failed)
    {
        var m = SongMeta.From(failed);
        return new MediaItem
        {
            Title = m.Title,
            Artist = m.Artist ?? ArtistFromUrl(failed.PageUrl ?? failed.Url),
            Album = m.Album,
            Year = m.Year,
            Duration = failed.Info?.Duration ?? failed.Duration,
            CoverUrl = failed.CoverUrl,
            Thumbnails = failed.Thumbnails.ToList(),
            Kind = MediaKind.Audio,
            Source = SourceKind.Search,
            Url = failed.Url,
            PageUrl = failed.PageUrl,
            SpotifyTrackId = failed.SpotifyTrackId,
            SiteName = "YouTube Music",
        };
    }

    // "soundcloud.com/some-artist/track" → "some artist".
    private static string? ArtistFromUrl(string? url)
    {
        if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        var host = Sites.HostOf(url);
        string? name = null;
        if (host.EndsWith("soundcloud.com")) name = u.AbsolutePath.Trim('/').Split('/').FirstOrDefault();
        else if (host.EndsWith(".bandcamp.com")) name = host[..^".bandcamp.com".Length];
        return string.IsNullOrWhiteSpace(name) ? null : name.Replace('-', ' ').Replace('_', ' ').Trim();
    }

    private static async Task ResolveAsync(MediaItem item, string? cookies, Action<JobProgress> progress, CancellationToken ct)
    {
        if (item.Source == SourceKind.Search)
        {
            progress(new JobProgress(JobPhase.Resolving, null, L.T("Ricerca del brano…")));
            if (item.SpotifyTrackId != null) await Spotify.EnrichAsync(item, ct);
            item.Info ??= await MusicMatcher.FindAsync(item, cookies, s => progress(new JobProgress(JobPhase.Resolving, null, s)), ct);
        }
        else if (item.Source == SourceKind.YtDlp && item.Info == null)
        {
            progress(new JobProgress(JobPhase.Resolving, null, L.T("Analisi…")));
            item.Info = await YtDlp.GetInfoAsync(item.Url, cookies, ct);
            if (item.Kind == MediaKind.Video && !item.Info.HasVideo) item.Kind = MediaKind.Audio;
            if (item.Thumbnails.Count == 0) item.Thumbnails = item.Info.Thumbnails;
            item.Duration ??= item.Info.Duration;
        }
    }

    private static async Task<TrackResult> DownloadAsync(Library lib, TrackRequest req, List<string> keys,
        (string Title, string? Artist, string? Album, string? Year) meta, double? duration, Track? target, Action<JobProgress> progress, CancellationToken ct)
    {
        var item = req.Item;
        bool videoAvailable = item.Info?.HasVideo == true || (item.Info == null && item.Kind == MediaKind.Video);
        var existing = target ?? lib.FindByKeys(keys) ?? lib.FindSimilar(meta.Title, meta.Artist, duration);
        if (existing != null && req.VideoOnly)
        {
            // A video found for the song (from its own link, or another one chosen among the search results).
            if (!videoAvailable) throw new EngineException(L.T("Nessun video trovato in questo link.")) { NoMedia = true };
            existing.VideoPath = await DownloadVideoAsync(req, progress, ct);
            lib.Changed(existing);
            return new TrackResult(existing, TrackOutcome.VideoAdded);
        }
        if (existing != null)
        {
            existing.ArtUrl ??= PublicArt(item);
            lib.AddKeys(existing, keys);
            if (req.ExtraKeys != null) Relink(existing, item);
            var outcome = TrackOutcome.AlreadyPresent;
            if (!existing.HasSavedFile)
            {
                await SaveAudioAsync(lib, existing, req, progress, ct);
                outcome = TrackOutcome.Saved;
            }
            if (!req.WantVideo || existing.HasVideo || !videoAvailable)
            {
                if (outcome == TrackOutcome.Saved) lib.Changed(existing);
                return new TrackResult(existing, outcome);
            }
            existing.VideoPath = await DownloadVideoAsync(req, progress, ct);
            lib.Changed(existing);
            return new TrackResult(existing, outcome == TrackOutcome.Saved ? TrackOutcome.Saved : TrackOutcome.VideoAdded);
        }

        var track = new Track
        {
            Title = meta.Title,
            Artist = meta.Artist,
            Album = meta.Album,
            Year = meta.Year,
            Duration = duration ?? 0,
            SourceUrl = item.PageUrl ?? item.Info?.WebpageUrl ?? item.Url,
            Site = item.SiteName,
            Keys = keys,
            ArtUrl = PublicArt(item),
        };
        if (req.ExtraKeys != null) Relink(track, item);
        var coverTmp = Path.Combine(AppPaths.TempDir, track.Id + ".jpg");
        var created = new List<string>();
        try
        {
            var audio = new DownloadJob(item, new DownloadOptions
            {
                AudioOnly = true,
                Audio = new AudioOptions(req.AudioFormat == "original" ? "original" : "mp3"),
                EmbedMetadata = true,
                CookiesBrowser = req.Cookies,
            }, req.MusicDir) { CoverOut = coverTmp };
            audio.Progress += progress;
            await audio.RunAsync(ct);
            track.Path = audio.ResultPath!;
            created.Add(track.Path);

            string? note = null;
            if (req.WantVideo && videoAvailable)
            {
                try
                {
                    track.VideoPath = await DownloadVideoAsync(req, progress, ct);
                    created.Add(track.VideoPath);
                }
                catch (EngineException ex) { note = L.T("Video non scaricato:") + " " + ex.Message; }
            }

            progress(new JobProgress(JobPhase.Converting, null, L.T("Analisi della traccia…")));
            var coverPath = AppPaths.TrackCover(track.Id);
            if (File.Exists(coverTmp))
            {
                File.Move(coverTmp, coverPath, true);
                track.HasCover = true;
            }
            else track.HasCover = await AudioAnalysis.ExtractCoverAsync(track.Path, coverPath, false, ct);
            created.Add(coverPath);

            try
            {
                var a = await AudioAnalysis.AnalyzeAsync(track.Path, ct);
                track.Wave = a.Wave;
                track.Loudness = a.Loudness;
                track.Peak = a.Peak;
                if (a.Duration > 0) track.Duration = a.Duration;
            }
            catch (EngineException) { }

            // Paid tracks sometimes come as a 30 s preview.
            if (duration is > 60 && track.Duration is > 0 and < 45 && track.Duration < duration * 0.6)
                throw new EngineException(L.F("Il sito ha dato solo un'anteprima di {0:0} secondi (brano a pagamento o bloccato).", track.Duration));

            lib.Add(track);
            progress(new JobProgress(JobPhase.Done, 100, L.T("Completato")));
            return new TrackResult(track, TrackOutcome.Downloaded, note);
        }
        catch
        {
            foreach (var f in created) { try { File.Delete(f); } catch { } }
            try { File.Delete(coverTmp); } catch { }
            throw;
        }
    }

    // Fetched from somewhere else than its link (the link was dead or blocked): that place is its link from now on (the
    // old one stays among its keys, so pasting it again still finds the song).
    private static void Relink(Track t, MediaItem item)
    {
        if (item.Info?.WebpageUrl is not { Length: > 0 } url) return;
        t.SourceUrl = url;
        t.Site = Sites.NameFor(url);
        t.Unavailable = false;
    }

    // A song of the library in the cloud gets its audio file on the device. Its cover, lyrics and statistics stay; the
    // cover is taken from the download only if it had none.
    private static async Task SaveAudioAsync(Library lib, Track t, TrackRequest req, Action<JobProgress> progress, CancellationToken ct)
    {
        var item = req.Item;
        var coverTmp = Path.Combine(AppPaths.TempDir, t.Id + "-" + Ids.New() + ".jpg");
        var audio = new DownloadJob(item, new DownloadOptions
        {
            AudioOnly = true,
            Audio = new AudioOptions(req.AudioFormat == "original" ? "original" : "mp3"),
            EmbedMetadata = true,
            CookiesBrowser = req.Cookies,
        }, req.MusicDir) { CoverOut = t.HasCover ? null : coverTmp };
        audio.Progress += progress;
        await audio.RunAsync(ct);
        var path = audio.ResultPath!;
        try
        {
            progress(new JobProgress(JobPhase.Converting, null, L.T("Analisi della traccia…")));
            double? expected = t.Duration > 0 ? t.Duration : item.Info?.Duration ?? item.Duration;
            AnalysisOutput? a = null;
            try { a = await AudioAnalysis.AnalyzeAsync(path, ct); }
            catch (EngineException) { }
            if (expected is > 60 && a?.Duration is > 0 and < 45 && a.Duration < expected * 0.6)
                throw new EngineException(L.F("Il sito ha dato solo un'anteprima di {0:0} secondi (brano a pagamento o bloccato).", a.Duration));
            if (!t.HasCover)
            {
                var coverPath = AppPaths.TrackCover(t.Id);
                if (File.Exists(coverTmp))
                {
                    File.Move(coverTmp, coverPath, true);
                    t.HasCover = true;
                }
                else t.HasCover = await AudioAnalysis.ExtractCoverAsync(path, coverPath, false, ct);
                if (t.HasCover) t.CoverVersion++;
            }
            if (a != null)
            {
                t.Wave = a.Wave;
                t.Loudness = a.Loudness;
                t.Peak = a.Peak;
                if (a.Duration > 0) t.Duration = a.Duration;
            }
        }
        catch
        {
            try { File.Delete(path); } catch { }
            throw;
        }
        finally
        {
            try { File.Delete(coverTmp); } catch { }
        }
        t.Path = path;
        t.CachePath = null;
        t.Unavailable = false;
        progress(new JobProgress(JobPhase.Done, 100, L.T("Completato")));
    }

    private static async Task<string> DownloadVideoAsync(TrackRequest req, Action<JobProgress> progress, CancellationToken ct)
    {
        var item = req.Item;
        var kind = item.Kind;
        item.Kind = MediaKind.Video;
        try
        {
            var job = new DownloadJob(item, new DownloadOptions
            {
                AudioOnly = false,
                Video = new VideoOptions("mp4", req.VideoMaxRes),
                Compat = VideoCompat.H264,
                EmbedMetadata = true,
                CookiesBrowser = req.Cookies,
            }, req.MusicDir);
            job.Progress += p => progress(p with { Text = "Video · " + p.Text });
            await job.RunAsync(ct);
            return job.ResultPath!;
        }
        finally
        {
            item.Kind = kind;
        }
    }

    // A web cover others can see (Discord), if any.
    public static string? PublicArt(MediaItem item)
    {
        var urls = new List<string?> { item.CoverUrl };
        urls.AddRange(item.Info?.Thumbnails ?? new List<string>());
        urls.AddRange(item.Thumbnails);
        return urls.FirstOrDefault(u => u != null && u.StartsWith("https://") && u.Length <= 250 &&
                                        !u.Contains("webp", StringComparison.OrdinalIgnoreCase));
    }
}

// Adds songs already on the computer.
public static class Importer
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".oga", ".opus", ".wma", ".aiff", ".aif", ".mka",
        ".mp4", ".m4v", ".mkv", ".webm", ".mov",
    };

    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mkv", ".webm", ".mov" };

    // Android: songs picked on the phone are copied into this folder (the app's): they're the app's files from then on,
    // deleting the song deletes them. Elsewhere songs added from the computer stay where they are.
    public static string? OwnedDir { get; set; }

    public static List<string> Expand(IEnumerable<string> paths)
    {
        var list = new List<string>();
        foreach (var p in paths)
        {
            try
            {
                if (Directory.Exists(p))
                    list.AddRange(Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories).Where(f => Extensions.Contains(Path.GetExtension(f))).OrderBy(f => f));
                else if (File.Exists(p) && Extensions.Contains(Path.GetExtension(p))) list.Add(p);
            }
            catch { }
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static async Task<Track> ImportAsync(Library lib, string file, CancellationToken ct)
    {
        file = Path.GetFullPath(file);
        var key = SourceKeys.FileKey(file);
        if ((lib.FindByKeys(new[] { key }) ?? lib.FindByPath(file)) is { } known) return known;

        var tags = await AudioAnalysis.ReadTagsAsync(file, ct);
        string title = tags.Title ?? Path.GetFileNameWithoutExtension(file);
        string? artist = tags.Artist;
        if (tags.Title == null && artist == null && title.Split(" - ", 2) is { Length: 2 } parts)
        {
            artist = parts[0].Trim();
            title = parts[1].Trim();
        }
        var track = new Track
        {
            Title = title,
            Artist = artist,
            Album = tags.Album,
            Year = tags.Year,
            Duration = tags.Duration ?? 0,
            Path = file,
            VideoPath = tags.HasVideo && VideoExtensions.Contains(Path.GetExtension(file)) ? file : null,
            // stored values, not shown
            Site = OwnedDir != null && file.StartsWith(Path.GetFullPath(OwnedDir), StringComparison.Ordinal) ? "Importato" : "File locale",
            Keys = new List<string> { key },
        };
        if (tags.HasCover)
            track.HasCover = await AudioAnalysis.ExtractCoverAsync(file, AppPaths.TrackCover(track.Id), tags.HasVideo, ct);
        try
        {
            var a = await AudioAnalysis.AnalyzeAsync(file, ct);
            track.Wave = a.Wave;
            track.Loudness = a.Loudness;
            track.Peak = a.Peak;
            if (a.Duration > 0) track.Duration = a.Duration;
        }
        catch (EngineException) { }
        lib.Add(track);
        return track;
    }
}
