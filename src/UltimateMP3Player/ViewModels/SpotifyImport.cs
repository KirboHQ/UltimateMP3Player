using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// "Import from Spotify" (Settings › Listening statistics, the statistics page, the empty Home): what Spotify's files hold,
// chosen with ticks. The liked songs into Favorites and the playlists as playlists here (their songs into the library, in the
// cloud unless Settings › Download says to save the audio, read in the background: the downloads panel); the plays into the
// profile's statistics and recaps, then the most played songs that aren't in the library into it (a list, like a pasted one).
public static class SpotifyImport
{
    private static bool _busy;

    public static async Task RunAsync(MainViewModel main)
    {
        if (_busy) return;
        int how = await Dialogs.ChooseAsync(L.T("Import from Spotify"),
            L.T("Your liked songs and playlists come here, their songs in your library, and your plays go into this profile's statistics and yearly recaps.") + "\n\n" +
            L.T("Get the files from Spotify: Account › Privacy settings › Download your data. “Account data” has your library, your playlists and the last year's plays; “Extended streaming history” every play since you started. Spotify emails a .zip within a few days: choose it here (both, if you asked for both), or the .json files inside.") + "\n\n" +
            L.T("Do you use Last.fm? Connect it in Settings › Integrations: its loved songs and its scrobbles can come here too."),
            L.T("Choose the files"), L.T("Open Spotify's page"));
        if (how == 1)
        {
            MainViewModel.OpenUrl(SpotifyHistory.PrivacyPage);
            return;
        }
        if (how != 0) return;
        if (await Dialogs.PickFilesAsync(L.T("Spotify's files"), true, (L.T("Spotify's files"), new[] { ".zip", ".json" })) is not { Length: > 0 } files) return;
        _busy = true;
        List<SpotifyHistory.Play> plays;
        List<SpotifyHistory.Song> liked;
        List<SpotifyHistory.SavedPlaylist> lists;
        try
        {
            main.Toast(L.T("Reading Spotify's files…"));
            (plays, liked, lists) = await Task.Run(() =>
            {
                var (l, p) = SpotifyHistory.ReadLibrary(files);
                return (SpotifyHistory.Read(files), l, p);
            });
        }
        catch (Exception ex)
        {
            main.Toast(L.T("Couldn't read the files:") + " " + ex.Message);
            return;
        }
        finally { _busy = false; }
        if (plays.Count == 0 && liked.Count == 0 && lists.Count == 0)
        {
            main.Toast(L.T("Nothing to import in these files: choose the .zip from Spotify or the .json files inside it."));
            return;
        }

        // What there is, all ticked.
        var options = new List<(string Text, bool On)>();
        var kinds = new List<int>();
        if (liked.Count > 0)
        {
            options.Add((L.F("Your liked songs ({0}), into Favorites", liked.Count.ToString("N0")), true));
            kinds.Add(0);
        }
        if (lists.Count > 0)
        {
            options.Add((L.F("Your playlists ({0}, {1} songs)", lists.Count.ToString("N0"), lists.Sum(p => p.Songs.Count).ToString("N0")), true));
            kinds.Add(1);
        }
        if (plays.Count > 0)
        {
            options.Add((L.F("Your listening history ({0} plays), into the statistics", plays.Count.ToString("N0")), true));
            kinds.Add(2);
        }
        if (await Dialogs.ChecksAsync(L.T("Import from Spotify"), L.T("Choose what to bring here."), options.ToArray(), L.T("Import")) is not { } ticks) return;
        bool Ticked(int kind) => kinds.IndexOf(kind) is >= 0 and var i && ticks[i];

        // The library in the background, one list after the other.
        var jobs = new List<(string Text, string Name, string? Id)>();
        if (Ticked(0)) jobs.Add((SpotifyHistory.AsList(liked), L.T("Liked on Spotify"), Playlist.FavoritesId));
        if (Ticked(1)) jobs.AddRange(lists.Select(p => (SpotifyHistory.AsList(p.Songs), p.Name, (string?)null)));
        if (jobs.Count > 0)
        {
            _ = ImportAll(main, jobs);
            main.Toast(L.T("Your Spotify library is on its way: follow it in the downloads panel."));
        }
        if (Ticked(2)) await ImportPlaysAsync(main, plays, offerMissing: jobs.Count == 0);
    }

    private static async Task ImportAll(MainViewModel main, List<(string Text, string Name, string? Id)> jobs)
    {
        foreach (var (text, name, id) in jobs) await main.Downloads.ImportIntoPlaylistAsync(text, name, id);
    }

    // The plays into the statistics; then (offerMissing: the library wasn't imported too) the songs played most that aren't in
    // the library, to add.
    private static async Task ImportPlaysAsync(MainViewModel main, List<SpotifyHistory.Play> plays, bool offerMissing)
    {
        List<ImportedPlay> added;
        try
        {
            var library = main.Library;
            var profile = main.Profile;
            added = await Task.Run(() => profile.AddImported(SpotifyHistory.Source, SpotifyHistory.Match(plays, library)));
        }
        catch (Exception ex)
        {
            main.Toast(L.T("Couldn't read the files:") + " " + ex.Message);
            return;
        }
        main.OnStatsChanged();
        if (added.Count == 0)
        {
            main.Toast(L.T("These plays were already imported."));
            return;
        }
        var summary = Summary(added);
        var missing = offerMissing ? SpotifyHistory.Missing(added) : new();
        if (missing.Count == 0)
        {
            await Dialogs.ConfirmAsync(L.T("Spotify history imported"), summary, L.T("Done"));
            return;
        }
        // The ones played most, into the library (in the cloud unless Settings › Download says to save the audio).
        var answers = new List<(string Text, int Count)>();
        foreach (var n in new[] { 50, 200 })
            if (missing.Count > n) answers.Add((L.F("The top {0}", n), n));
        answers.Add((missing.Count == 1 ? L.T("Add") : L.F("All {0}", missing.Count.ToString("N0")), missing.Count));
        int pick = await Dialogs.ChooseAsync(L.T("Spotify history imported"),
            summary + "\n\n" + L.Count(missing.Count, "1 song you played isn't in your library: add it?",
                "{0} songs you played aren't in your library: add the ones you played most?"),
            answers.Select(a => a.Text).ToArray());
        if (pick < 0) return;
        main.Navigate(main.Downloads);
        _ = main.Downloads.ImportListAsync(SpotifyHistory.AsList(missing.Take(answers[pick].Count)), L.T("Most played on Spotify"));
    }

    public static string Summary(List<ImportedPlay> added)
    {
        var hours = added.Sum(p => p.Seconds) / 3600;
        var songs = added.Select(p => p.Id).Distinct().Count();
        var inLibrary = added.Where(p => p.InLibrary).Select(p => p.Id).Distinct().Count();
        return L.F("{0} plays from {1} to {2}, {3} hours of music.", added.Count(p => p.Play).ToString("N0"),
                   added.Min(p => p.At).ToString("d"), added.Max(p => p.At).ToString("d"), Math.Round(hours).ToString("N0")) + "\n" +
               L.F("{0} different songs, {1} of them in your library.", songs.ToString("N0"), inLibrary.ToString("N0"));
    }
}

// "Import from Last.fm" (Settings › Integrations, the statistics page): the connected account's loved songs as a playlist,
// its scrobbles into the statistics: only the ones before it was connected here (the later ones went there from here: they're
// counted already).
public static class LastFmImport
{
    public const string Source = "lastfm";
    // 4.3.0, the first with scrobbling: an account connected before LastFmSince was kept can't have had plays from here before.
    private static readonly DateTime FirstScrobbling = new(2026, 10, 28);
    // shortcut: a scrobble has no length; a song not in the library counts as 3 minutes and a half, until Last.fm gives it.
    private const double GuessSeconds = 210;
    private static bool _busy;

    public static async Task RunAsync(MainViewModel main)
    {
        var s = main.Host.Settings;
        var p = main.Profile.Data;
        if (string.IsNullOrWhiteSpace(s.LastFmKey) || string.IsNullOrWhiteSpace(s.LastFmSecret) || p.LastFmUser is not { } user)
        {
            if (await Dialogs.ConfirmAsync(L.T("Import from Last.fm"),
                    L.T("Connect your Last.fm account first (Settings › Integrations): then its loved songs and its scrobbles can come here."), L.T("Open Settings")))
                main.SearchSettings("Last.fm");
            return;
        }
        if (_busy) return;
        var to = p.LastFmSince ?? FirstScrobbling;
        var ticks = await Dialogs.ChecksAsync(L.T("Import from Last.fm"), L.F("From the account {0}:", user), new[]
        {
            (L.T("Your loved songs, into the playlist “Loved on Last.fm”"), true),
            (L.F("Your scrobbles until {0}, into the statistics (the later ones went there from here: they're counted already)", to.ToString("d")), true),
        }, L.T("Import"));
        if (ticks == null) return;
        string key = s.LastFmKey!, secret = s.LastFmSecret!;
        _busy = true;
        try
        {
            if (ticks[0]) await LovedAsync(main, key, secret, user);
            if (ticks[1]) await ScrobblesAsync(main, key, secret, user, to);
        }
        finally { _busy = false; }
    }

    private static async Task LovedAsync(MainViewModel main, string key, string secret, string user)
    {
        List<(string Title, string Artist)> loved;
        try { loved = await Task.Run(() => LastFm.LovedAsync(key, secret, user, CancellationToken.None)); }
        catch (Exception ex)
        {
            main.Toast(L.T("Last.fm didn't answer: check your connection") + " (" + ex.Message + ")");
            return;
        }
        if (loved.Count == 0)
        {
            main.Toast(L.T("No loved songs on this Last.fm account."));
            return;
        }
        _ = main.Downloads.ImportIntoPlaylistAsync(SpotifyHistory.AsList(loved.Select(l => new SpotifyHistory.Song(l.Title, l.Artist, null, null))),
            L.T("Loved on Last.fm"));
        main.Toast(L.T("Your loved songs are on their way: follow them in the downloads panel."));
    }

    private static async Task ScrobblesAsync(MainViewModel main, string key, string secret, string user, DateTime to)
    {
        var cts = new CancellationTokenSource();
        var group = main.Downloads.Queue.AddReading(L.T("Last.fm scrobbles"), cts.Cancel);
        var ui = System.Windows.Application.Current.Dispatcher;
        List<ImportedPlay> added;
        try
        {
            var library = main.Library;
            var profile = main.Profile;
            added = await Task.Run(async () =>
            {
                var scrobbles = await LastFm.ScrobblesAsync(key, secret, user, to, (page, pages) => ui.BeginInvoke(() => group.Read(page, pages, false)), cts.Token);
                // (the library's songs count with their own length)
                var lengths = new Dictionary<string, double>();
                foreach (var t in library.Snapshot())
                    if (t.Duration > 0) lengths.TryAdd(SongMeta.Key(t.Title, t.Artist), t.Duration);
                var plays = scrobbles.Select(x => new SpotifyHistory.Play(x.At, x.Title, x.Artist, null,
                    lengths.TryGetValue(SongMeta.Key(x.Title, x.Artist), out var d) ? d : GuessSeconds)).ToList();
                return profile.AddImported(Source, SpotifyHistory.Match(plays, library));
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            main.Downloads.Queue.RemoveGroup(group);
            return;
        }
        catch (Exception ex)
        {
            group.Ready(L.T("Last.fm didn't answer: check your connection"));
            main.Toast(L.T("Last.fm didn't answer: check your connection") + " (" + ex.Message + ")");
            return;
        }
        group.Ready(L.Count(added.Count, "1 play imported", "{0} plays imported"));
        main.OnStatsChanged();
        if (added.Count == 0) main.Toast(L.T("These plays were already imported."));
        else await Dialogs.ConfirmAsync(L.T("Last.fm scrobbles imported"), SpotifyImport.Summary(added), L.T("Done"));
    }
}
