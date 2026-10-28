using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// "Import your Spotify history" (Settings › Listening statistics, the statistics page): Spotify's files into the profile's
// statistics and recaps, then the most played songs that aren't in the library into it (a list, like a pasted one).
public static class SpotifyImport
{
    private static bool _busy;

    public static async Task RunAsync(MainViewModel main)
    {
        if (_busy) return;
        int how = await Dialogs.ChooseAsync(L.T("Import your Spotify history"),
            L.T("Your plays on Spotify go into this profile's statistics and yearly recaps, and you can add the songs you played most.") + "\n\n" +
            L.T("Get the files from Spotify: Account › Privacy settings › Download your data, tick “Extended streaming history” (every year) or “Account data” (the last year). Spotify emails a .zip within a few days: choose it here, or the .json files inside it."),
            L.T("Choose the files"), L.T("Open Spotify's page"));
        if (how == 1)
        {
            MainViewModel.OpenUrl(SpotifyHistory.PrivacyPage);
            return;
        }
        if (how != 0) return;
        if (await Dialogs.PickFilesAsync(L.T("Spotify history"), true, (L.T("Spotify history"), new[] { ".zip", ".json" })) is not { Length: > 0 } files) return;
        _busy = true;
        main.Toast(L.T("Reading the Spotify history…"));
        List<ImportedPlay> added;
        List<ImportedPlay> all;
        try
        {
            var library = main.Library;
            all = await Task.Run(() => SpotifyHistory.Match(SpotifyHistory.Read(files), library));
            if (all.Count == 0)
            {
                main.Toast(L.T("No songs found in these files: choose the .zip from Spotify or the Streaming_History files inside it."));
                return;
            }
            var profile = main.Profile;
            added = await Task.Run(() => profile.AddImported(SpotifyHistory.Source, all));
        }
        catch (Exception ex)
        {
            main.Toast(L.T("Couldn't read the files:") + " " + ex.Message);
            return;
        }
        finally { _busy = false; }

        main.OnStatsChanged();
        if (added.Count == 0)
        {
            main.Toast(L.T("These plays were already imported."));
            return;
        }
        var hours = added.Sum(p => p.Seconds) / 3600;
        var songs = added.Select(p => p.Id).Distinct().Count();
        var inLibrary = added.Where(p => p.InLibrary).Select(p => p.Id).Distinct().Count();
        var summary = L.F("{0} plays from {1} to {2}, {3} hours of music.", added.Count(p => p.Play).ToString("N0"),
                          added.Min(p => p.At).ToString("d"), added.Max(p => p.At).ToString("d"), Math.Round(hours).ToString("N0")) + "\n" +
                      L.F("{0} different songs, {1} of them in your library.", songs.ToString("N0"), inLibrary.ToString("N0"));
        var missing = SpotifyHistory.Missing(added);
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
}
