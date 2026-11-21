using System.IO;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// Music videos for the songs as they play (Settings › Music videos), or chosen by hand ("Find the video…"): found on YouTube
// when the song has none, only when they surely are its own (VideoFinder.Sure), downloaded without their sound (MutedVideo). A
// saved song keeps its video beside its audio (Track.VideoPath), any other in the video cache (VideoCache, the last 10), like
// its audio: back from its link when it plays again. The song's page shows it as soon as it's here; the next song's comes
// ahead, so it's there when that one starts.
public sealed class VideosViewModel
{
    private readonly MainViewModel _main;
    // Videos coming, by song id.
    private readonly Dictionary<string, CancellationTokenSource> _coming = new();

    public VideosViewModel(MainViewModel main) => _main = main;

    private VideoCache Cache => _main.Host.VideoCache;
    private AppSettings S => _main.Host.Settings;

    // This device can have them (something to download and show them with).
    public static bool Possible => Engines.Videos;

    private static string NameOf(Track t) => SongMeta.Key(t.Title, t.Artist);

    // "No video for this song" (this profile).
    public bool IsOff(Track t) => _main.Profile.IsNoVideo(NameOf(t));

    // The video the song's page shows: its own beside its audio, else the cache's; none for a song set without.
    public string? FileFor(Track t)
    {
        if (!Possible || IsOff(t)) return null;
        if (t.HasVideo && File.Exists(t.VideoPath)) return t.VideoPath;
        return t.CacheVideo is { } c && File.Exists(c) ? c : null;
    }

    // Songs that can have one: not a radio, not the other device's.
    public bool CanHave(Track t) => Possible && !Station.Is(t.Id) && !_main.IsTheirs(t.Id) && !string.IsNullOrWhiteSpace(t.Title);

    // The song playing changed (or the queue after it): their videos brought back, or found, when they should be.
    public void OnSong()
    {
        var p = _main.Player;
        if (p.IsRemote) return;
        var now = p.Current?.T;
        var next = p.NextTrack;
        // (the ones no longer wanted stop coming)
        foreach (var (id, cts) in _coming.ToList())
            if (id != now?.Id && id != next?.Id)
            {
                cts.Cancel();
                _coming.Remove(id);
            }
        if (now != null) _ = BringAsync(now);
        if (next != null) _ = BringAsync(next);
    }

    // The automatic videos switched on, or the connection changed: the songs playing and next get theirs now.
    public void Refresh() => OnSong();

    // A song without its own video on the device: the cache's, else downloaded from the link known or found, when the automatic
    // videos are on (or the link was chosen by hand) and the connection allows it.
    private async Task BringAsync(Track t)
    {
        if (!CanHave(t) || t.HasVideo && File.Exists(t.VideoPath)) return;
        var keys = VideoCache.KeysOf(t);
        if (Cache.FileFor(keys) is { } cached)
        {
            if (t.CacheVideo != cached)
            {
                t.CacheVideo = cached;
                Shown(t);
            }
            return;
        }
        if (IsOff(t) || _coming.ContainsKey(t.Id)) return;
        var known = Cache.Known(keys);
        if (!S.AutoVideos && !known.Chosen || !NetworkOk || known.None && known.Url == null) return;
        var cts = new CancellationTokenSource();
        _coming[t.Id] = cts;
        try
        {
            var url = known.Url;
            if (url == null)
            {
                var found = await Task.Run(() => VideoFinder.SearchAsync(t.Title, t.Artist, t.Duration, cts.Token), cts.Token);
                url = VideoFinder.Sure(found, t.Duration)?.Url;
                if (url == null)
                {
                    Cache.SetNone(keys);
                    return;
                }
                Cache.SetUrl(keys, url, chosen: false);
            }
            await GetAsync(t, url, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log(ex); }
        finally
        {
            if (_coming.TryGetValue(t.Id, out var c) && c == cts) _coming.Remove(t.Id);
            cts.Dispose();
        }
    }

    // On the phones, only on Wi-Fi when asked (Settings).
    private bool NetworkOk => !S.VideosWifiOnly || !NetworkCost.IsMetered;

    // "Find the video…" (or another one): from the song's own link when that's a YouTube video and it has none yet, otherwise
    // chosen among the videos found on YouTube. Chosen by hand, it comes back whenever the song plays.
    public async Task FindAsync(TrackViewModel vm)
    {
        var t = vm.T;
        if (!CanHave(t)) return;
        string? url = FileFor(t) == null ? VideoFinder.OwnVideo(t) : null;
        if (url == null)
        {
            if (await Dialogs.PickVideoAsync(_main, vm) is not { } pick) return;
            url = pick.Url;
        }
        Cache.SetUrl(VideoCache.KeysOf(t), url, chosen: true);
        _main.Profile.SetNoVideo(NameOf(t), false);
        if (_coming.Remove(t.Id, out var old)) old.Cancel();
        var cts = new CancellationTokenSource();
        _coming[t.Id] = cts;
        _main.Toast(L.F("Downloading the video of “{0}”…", t.Title));
        try
        {
            await GetAsync(t, url, cts.Token);
            _main.Toast(L.F("The video of “{0}” is ready", t.Title));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Log(ex);
            _main.Toast(L.F("Couldn't download the video of “{0}”: {1}", t.Title, ex.Message));
        }
        finally
        {
            if (_coming.TryGetValue(t.Id, out var c) && c == cts) _coming.Remove(t.Id);
            cts.Dispose();
        }
    }

    // "No video for this song" / "Show its video again" (this profile): nothing brought for it, nothing shown.
    public void SetOff(Track t, bool off)
    {
        _main.Profile.SetNoVideo(NameOf(t), off);
        if (off && _coming.Remove(t.Id, out var cts)) cts.Cancel();
        if (_main.Player.Current?.T == t) _main.NowPlaying.Refresh();
        if (!off) OnSong();
        _main.Toast(off ? L.F("No video for “{0}” from now on", t.Title) : L.F("“{0}” shows its video again", t.Title));
    }

    // Downloaded (on the UI thread from here): a saved song's beside its audio, any other's into the cache.
    private async Task GetAsync(Track t, string url, CancellationToken ct)
    {
        var res = Quality.VideoFor(t, S.VideoMaxRes);
        var cookies = S.CookiesBrowserOrNull;
        var file = await Task.Run(() => MutedVideo.DownloadAsync(url, res, cookies, Cache.TempDir, ct), ct);
        if (_main.Library.Get(t.Id) == t && t.HasSavedFile)
        {
            var old = t.HasOwnVideo ? t.VideoPath : null;
            t.VideoPath = Beside(_main.Host, t, file);
            t.CacheVideo = null;
            _main.Library.Changed(t);
            if (old != null && !old.Equals(t.VideoPath, StringComparison.OrdinalIgnoreCase)) OldFiles.Delete(old);
        }
        else
        {
            var keep = new HashSet<string>(new[] { _main.Player.Current?.T.CacheVideo, _main.Player.NextTrack?.CacheVideo }.OfType<string>(),
                StringComparer.OrdinalIgnoreCase);
            t.CacheVideo = Cache.Adopt(VideoCache.KeysOf(t), t.Title, t.Artist, file, keep);
        }
        Shown(t);
    }

    // A video file into the music folder, named after its song.
    private static string Beside(AppHost host, Track t, string file)
    {
        var dir = host.Settings.MusicDir;
        Directory.CreateDirectory(dir);
        var name = Text.SafeFileName((string.IsNullOrWhiteSpace(t.Artist) ? t.Title : $"{t.Artist.Split(',')[0].Trim()} - {t.Title}") + " (video)");
        var target = Text.UniquePath(dir, name, Path.GetExtension(file).TrimStart('.'));
        File.Move(file, target);
        return target;
    }

    // A song just saved on the device: the video brought while it was in the cloud goes beside its audio, like the audio.
    public static bool Attach(AppHost host, Track t)
    {
        if (t.HasVideo || !t.HasSavedFile) return false;
        var keys = VideoCache.KeysOf(t);
        if (host.VideoCache.FileFor(keys) is not { } cached) return false;
        var dir = host.Settings.MusicDir;
        Directory.CreateDirectory(dir);
        var name = Text.SafeFileName((string.IsNullOrWhiteSpace(t.Artist) ? t.Title : $"{t.Artist.Split(',')[0].Trim()} - {t.Title}") + " (video)");
        if (host.VideoCache.MoveOut(keys, Text.UniquePath(dir, name, Path.GetExtension(cached).TrimStart('.'))) is not { } moved) return false;
        t.VideoPath = moved;
        t.CacheVideo = null;
        return true;
    }

    // A song back in the cloud (its audio off the device): its video goes to the cache, a song in the cloud's like the others.
    // False: it couldn't move (in use): then it's deleted.
    public bool ToCache(Track t)
    {
        if (!t.HasOwnVideo || t.VideoPath is not { } path || !File.Exists(path)) return false;
        try
        {
            t.CacheVideo = Cache.Adopt(VideoCache.KeysOf(t), t.Title, t.Artist, path);
            return true;
        }
        catch { return false; }
    }

    // The song shown with its video now (the page, the stream overlay).
    private void Shown(Track t)
    {
        if (_main.Player.Current?.T != t) return;
        _main.NowPlaying.Refresh();
        _main.Host.OnTrackChanged();
    }
}
