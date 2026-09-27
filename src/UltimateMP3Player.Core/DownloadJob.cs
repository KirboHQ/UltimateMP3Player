namespace UltimateMP3Player.Core;

public enum JobPhase { Queued, Resolving, Downloading, Converting, Done, Failed, Canceled }

public sealed record JobProgress(JobPhase Phase, double? Percent, string Text);

// Downloads and converts one item; reusable for retries.
public sealed class DownloadJob
{
    public MediaItem Item { get; }
    public DownloadOptions Options { get; }
    public string OutputDir { get; }
    public string? ResultPath { get; private set; }
    public string? Summary { get; private set; }
    // If set, the tag cover is also saved here.
    public string? CoverOut { get; set; }

    public event Action<JobProgress>? Progress;

    public DownloadJob(MediaItem item, DownloadOptions options, string outputDir)
    {
        Item = item;
        Options = options;
        OutputDir = outputDir;
    }

    private void Report(JobPhase phase, double? pct, string text) => Progress?.Invoke(new JobProgress(phase, pct, text));

    public static Target TargetFor(MediaItem item, DownloadOptions o)
    {
        switch (item.Kind)
        {
            case MediaKind.Video when !o.AudioOnly:
                return new VideoTarget(o.Video.Container, o.Video.Res, o.Video.Fps, o.Compat);
            case MediaKind.Video:
            case MediaKind.Audio:
                return new AudioTarget(o.Audio.Format, o.Audio.Kbps, o.Audio.SampleRate, o.Audio.Channels, o.Audio.BitDepth);
            default:
                return new ImageTarget(o.Image.Format, o.Image.MaxSide);
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "UltimateMP3Player", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(temp);
        try
        {
            var target = TargetFor(Item, Options);
            string cookies = Options.CookiesBrowser ?? "";

            if (Item.Source == SourceKind.Search)
            {
                Report(JobPhase.Resolving, null, L.T("Ricerca del brano…"));
                if (Item.SpotifyTrackId != null) await Spotify.EnrichAsync(Item, ct);
                Item.Info ??= await MusicMatcher.FindAsync(Item, Options.CookiesBrowser, s => Report(JobPhase.Resolving, null, s), ct);
            }
            else if (Item.Source == SourceKind.YtDlp && Item.Info == null)
            {
                Report(JobPhase.Resolving, null, L.T("Analisi…"));
                Item.Info = await YtDlp.GetInfoAsync(Item.Url, Options.CookiesBrowser, ct);
                if (Item.Kind == MediaKind.Video && !Item.Info.HasVideo)
                {
                    Item.Kind = MediaKind.Audio;
                    target = TargetFor(Item, Options);
                }
                if (Item.Thumbnails.Count == 0) Item.Thumbnails = Item.Info.Thumbnails;
                Item.Duration ??= Item.Info.Duration;
                if (Item.TitleIsGuess)
                {
                    Item.Title = Item.Info.Track ?? Item.Info.Title;
                    if (Item.Info.Track != null) Item.Artist ??= Item.Info.Artist;
                    Item.TitleIsGuess = false;
                }
            }

            string input;
            if (Item.Info != null) input = await DownloadWithYtDlp(Item.Info, target, temp, ct);
            else input = await DownloadDirect(temp, ct);
            ct.ThrowIfCancellationRequested();

            Report(JobPhase.Converting, null, L.T("Preparazione…"));
            bool isImage = target is ImageTarget;
            var probe = await Ffmpeg.ProbeAsync(input, ct, countPackets: isImage);
            if (target is VideoTarget && probe.Video == null && probe.Audio != null)
                target = new AudioTarget(Options.Audio.Format, Options.Audio.Kbps, Options.Audio.SampleRate, Options.Audio.Channels, Options.Audio.BitDepth);
            if (target is ImageTarget && probe.Video == null)
                throw new EngineException(L.T("Il file scaricato non è un'immagine."));

            var outBase = Path.Combine(temp, "out");
            Ffmpeg.Plan plan;
            switch (target)
            {
                case VideoTarget vt:
                    {
                        if (probe.Video == null) throw new EngineException(L.T("Il file scaricato non contiene né video né audio."));
                        string? meta = Options.EmbedMetadata ? Ffmpeg.WriteMetadata(temp, BuildTags()) : null;
                        plan = Ffmpeg.ForVideo(input, probe, vt, meta, outBase + "." + vt.Container);
                        break;
                    }
                case AudioTarget at:
                    {
                        string? meta = null, cover = null;
                        if (Options.EmbedMetadata)
                        {
                            var tags = BuildTags();
                            var ext = at.Format == "original" ? "" : at.Format;
                            byte[]? oggPic = null;
                            if (at.Format is "mp3" or "m4a" or "flac" or "opus" or "ogg" or "original")
                            {
                                bool small = at.Format is "opus" or "ogg";
                                cover = await PrepareCoverAsync(temp, small ? 600 : 1200, ct);
                                if (cover != null && CoverOut != null)
                                {
                                    try { File.Copy(cover, CoverOut, true); } catch { }
                                }
                                if (cover != null && small)
                                {
                                    var cp = await Ffmpeg.ProbeAsync(cover, ct);
                                    oggPic = Ffmpeg.OggPictureBlock(await File.ReadAllBytesAsync(cover, ct), cp.Video?.Width ?? 0, cp.Video?.Height ?? 0);
                                }
                            }
                            meta = Ffmpeg.WriteMetadata(temp, tags, oggPic);
                        }
                        plan = Ffmpeg.ForAudio(input, probe, at, meta, cover, outBase + ".tmp");
                        break;
                    }
                case ImageTarget it:
                    plan = Ffmpeg.ForImage(input, probe, it, outBase + ".tmp");
                    break;
                default:
                    throw new InvalidOperationException();
            }

            string produced;
            if (plan.JustMove)
            {
                produced = input;
            }
            else
            {
                Report(JobPhase.Converting, 0, ConvertLabel(target));
                double? dur = probe.Duration ?? Item.Duration;
                await Ffmpeg.RunAsync(plan.Args, dur, f => Report(JobPhase.Converting, f * 100, ConvertLabel(target)), ct);
                produced = Path.ChangeExtension(outBase, plan.Ext);
                if (!File.Exists(produced)) throw new EngineException(L.T("La conversione non ha prodotto alcun file."));
            }
            Summary = plan.Description;

            Directory.CreateDirectory(OutputDir);
            var name = Text.SafeFileName(FileName());
            var final = Text.UniquePath(OutputDir, name, plan.Ext);
            File.Move(produced, final);
            ResultPath = final;
            Report(JobPhase.Done, 100, L.T("Completato"));
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    private static string ConvertLabel(Target t) => t switch
    {
        VideoTarget { Container: "gif" } => L.T("Creazione GIF"),
        VideoTarget => L.T("Conversione video"),
        AudioTarget => L.T("Conversione audio"),
        _ => L.T("Conversione immagine"),
    };

    private async Task<string> DownloadWithYtDlp(YtInfo info, Target target, string temp, CancellationToken ct)
    {
        Selection? sel = target switch
        {
            VideoTarget vt => FormatSelector.ForVideo(info, new VideoOptions(vt.Container, vt.Res, vt.Fps), vt.Compat),
            AudioTarget => FormatSelector.ForAudio(info, Options.Audio),
            _ => FormatSelector.ForVideo(info, new VideoOptions("mkv"), VideoCompat.Original),
        };
        if (sel == null)
        {
            if (target is VideoTarget) sel = FormatSelector.ForAudio(info, Options.Audio);
            if (sel == null) throw new EngineException(L.T("Nessun formato scaricabile trovato."));
        }

        var parts = new List<string>();
        if (sel.Video != null) parts.Add(sel.Video.Id);
        if (sel.Audio != null) parts.Add(sel.Audio.Id);
        string Label(string fmtId)
        {
            if (parts.Count < 2) return "Download";
            return fmtId == sel.Video?.Id ? "Download video (1/2)" : "Download audio (2/2)";
        }

        void OnProgress(DlProgress p)
        {
            double? pct = p.Total > 0 ? p.Done * 100.0 / p.Total.Value : null;
            var details = new List<string>();
            if (p.Total > 0) details.Add(L.F("{0} di {1}", Text.Size(p.Done), Text.Size(p.Total)));
            else if (p.Done > 0) details.Add(Text.Size(p.Done));
            if (p.Speed > 0) details.Add(Text.Speed(p.Speed));
            if (p.Eta is > 0 and < 360000) details.Add(L.F("mancano {0}", Text.Duration(p.Eta)));
            Report(JobPhase.Downloading, pct, Label(p.FormatId) + (details.Count > 0 ? " · " + string.Join(" · ", details) : ""));
        }

        Report(JobPhase.Downloading, null, L.T("Connessione…"));
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return await YtDlp.DownloadAsync(info.WebpageUrl, sel.Spec, sel.Merge, temp, Options.CookiesBrowser, OnProgress, ct);
                }
                catch (EngineException ex) when (attempt < 3 && IsTransient(ex))
                {
                    // 403/5xx mid-download: a fresh extraction usually works.
                    ClearDir(temp);
                    Report(JobPhase.Downloading, null, L.F("Errore temporaneo, nuovo tentativo ({0}/3)…", attempt + 1));
                    await Task.Delay(1500 * attempt, ct);
                }
            }
        }
        catch (EngineException ex) when (ex.FormatUnavailable)
        {
            // Format ids may change after analysis: let yt-dlp choose.
            string spec;
            var extra = new List<string>();
            if (target is VideoTarget vt)
            {
                spec = vt.Container == "gif" ? "bv*/b" : "bv*+ba/b";
                var sort = new List<string>();
                if (vt.Res is int r) sort.Add($"res:{r}");
                if (vt.Fps is double f) sort.Add($"fps:{(int)f}");
                if (sort.Count > 0) { extra.Add("-S"); extra.Add(string.Join(",", sort)); }
            }
            else spec = "ba/b";
            ClearDir(temp);
            return await YtDlp.DownloadAsync(info.WebpageUrl, spec, target is VideoTarget { Container: not "gif" }, temp, Options.CookiesBrowser, OnProgress, ct, extra);
        }
    }

    private static bool IsTransient(EngineException ex)
    {
        var d = ex.Details;
        return !ex.NeedsLogin && (d.Contains("HTTP Error 403") || d.Contains("HTTP Error 5") || d.Contains("Connection reset") ||
                                  d.Contains("timed out") || d.Contains("IncompleteRead") || d.Contains("RemoteDisconnected") ||
                                  d.Contains("fragment") || d.Contains("Connection aborted"));
    }

    private static void ClearDir(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir)) { try { File.Delete(f); } catch { } }
    }

    private async Task<string> DownloadDirect(string temp, CancellationToken ct)
    {
        var ext = string.IsNullOrEmpty(Item.Ext) ? Direct.ExtOf(Item.Url) ?? "bin" : Item.Ext;
        var path = Path.Combine(temp, "media." + ext);
        Report(JobPhase.Downloading, null, L.T("Connessione…"));
        try
        {
            await Http.DownloadFileAsync(Item.Url, path, null, (done, total) =>
            {
                double? pct = total > 0 ? done * 100.0 / total.Value : null;
                var text = total > 0 ? L.F("Download · {0} di {1}", Text.Size(done), Text.Size(total)) : $"Download · {Text.Size(done)}";
                Report(JobPhase.Downloading, pct, text);
            }, ct, referer: Item.PageUrl);
            return path;
        }
        catch (Exception ex) when (Item.GalleryUrl != null && ex is EngineException or HttpRequestException)
        {
            // Some hosts need cookies/headers: gallery-dl handles them.
            Report(JobPhase.Downloading, null, L.T("Download tramite gallery-dl…"));
            return await GalleryDl.DownloadOneAsync(Item.GalleryUrl, Item.GalleryIndex, temp, Options.CookiesBrowser, ct);
        }
    }

    private async Task<string?> PrepareCoverAsync(string temp, int maxSize, CancellationToken ct)
    {
        var urls = new List<string>();
        if (Item.CoverUrl != null) urls.Add(Item.CoverUrl);
        if (Item.Info != null) urls.AddRange(Item.Info.Thumbnails);
        urls.AddRange(Item.Thumbnails);
        bool fromMusic = Item.CoverUrl == null && (Item.Info?.WebpageUrl.Contains("music.youtube.com") == true || Item.Info?.Track != null);
        foreach (var u in urls.Distinct())
        {
            try
            {
                var data = await Http.GetBytesAsync(u, ct: ct);
                if (data.Length < 500) continue;
                var cover = await Ffmpeg.MakeCoverAsync(data, temp, square: fromMusic || Item.Source == SourceKind.Search && Item.CoverUrl == null, maxSize, ct);
                if (cover != null) return cover;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        }
        return null;
    }

    private Tags BuildTags()
    {
        var m = SongMeta.From(Item);
        return new Tags(m.Title, m.Artist, m.Album, m.Year, Item.TrackNo, Item.PageUrl ?? Item.Info?.WebpageUrl);
    }

    private string FileName()
    {
        var m = SongMeta.From(Item);
        return string.IsNullOrWhiteSpace(m.Artist) ? m.Title : $"{m.Artist.Split(',')[0].Trim()} - {m.Title}";
    }
}
