using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// What was asked: the words as heard, and the parts the assistant understood when it did (Android's extras).
public sealed record VoiceRequest(string Words, VoiceFocus Focus = VoiceFocus.Any, string? Title = null, string? Artist = null,
    string? Album = null, string? Playlist = null, string? Genre = null);

// "Play … on Ultimate MP3 Player": the Assistant on Android and in the car (CarLibrary), the ump://play link on an iPhone (a
// Shortcut Siri runs). The library first, by what the assistant understood, then by the words alone; what it doesn't have is
// searched online and heard without saving it, like a song of the search. Everything goes through the player's own PlayFrom,
// PlayAll, PlayStream, which already know about rooms and a linked computer.
public static class VoicePlay
{
    // Null when something plays; otherwise what to say. busy: an online search going on (the car shows it's coming).
    public static async Task<string?> PlayAsync(MainViewModel s, VoiceRequest r, Action<bool>? busy = null)
    {
        // (heard: all the words; words: the name in them, "Thriller" in "the album Thriller")
        var heard = VoiceWords.Clean(r.Words);
        var words = heard;
        var focus = r.Focus;
        bool understood = !string.IsNullOrWhiteSpace(r.Title) || !string.IsNullOrWhiteSpace(r.Artist) || !string.IsNullOrWhiteSpace(r.Album)
                          || !string.IsNullOrWhiteSpace(r.Playlist) || !string.IsNullOrWhiteSpace(r.Genre);
        if (!understood)
        {
            var (meaning, said, name) = VoiceWords.Read(words);
            if (meaning == VoiceMeaning.AnyMusic) return PlayDefault(s);
            if (meaning == VoiceMeaning.Shuffle) return Shuffle(s);
            if (meaning == VoiceMeaning.Favorites && Favorites(s)) return null;
            // ("the album Thriller": an album, "Thriller")
            if (focus == VoiceFocus.Any) (focus, words) = (said, name);
        }

        // What the assistant understood first, in the order of what it says it's about.
        bool song = !string.IsNullOrWhiteSpace(r.Title);
        foreach (var name in VoiceWords.Names(words))
            switch (focus)
            {
                case VoiceFocus.Playlist when PlayPlaylist(s, r.Playlist ?? name):
                case VoiceFocus.Album when PlayAlbum(s, r.Album ?? name, r.Artist):
                case VoiceFocus.Artist when PlayArtist(s, r.Artist ?? name):
                case VoiceFocus.Genre when PlayTag(s, r.Genre ?? name):
                case VoiceFocus.Station when PlayStation(s, name):
                    return null;
            }
        if (song && PlaySongs(s, r.Title + " " + r.Artist, words)) return null;
        if (!string.IsNullOrWhiteSpace(r.Playlist) && PlayPlaylist(s, r.Playlist)) return null;
        if (!string.IsNullOrWhiteSpace(r.Album) && PlayAlbum(s, r.Album, r.Artist)) return null;
        if (!string.IsNullOrWhiteSpace(r.Artist) && !song && PlayArtist(s, r.Artist)) return null;
        if (!string.IsNullOrWhiteSpace(r.Genre) && PlayTag(s, r.Genre)) return null;

        // Then all the words alone, whatever they are ("Radio Ga Ga" is a song too), and without an article before them ("i Queen").
        foreach (var name in VoiceWords.Names(heard))
            if (PlayPlaylist(s, name) || PlayArtist(s, name) || PlayAlbum(s, name, null) || PlayTag(s, name) || PlaySongs(s, name, name) || PlayStation(s, name))
                return null;

        // Not in the library: online, like a song found by the search.
        var asked = song ? (r.Title + " " + r.Artist).Trim() : focus == VoiceFocus.Artist ? r.Artist ?? words : heard;
        if (asked.Length == 0) return PlayDefault(s);
        return await OnlineAsync(s, asked, focus == VoiceFocus.Artist || (!song && r.Artist != null), busy);
    }

    // The sites in the search's order, one after the other until one has it. An artist: their songs found, all of them, in a row.
    public static async Task<string?> OnlineAsync(MainViewModel s, string words, bool artist, Action<bool>? busy = null)
    {
        var settings = s.Host.Settings;
        var services = OnlineSearchServices.Ordered(settings.SearchServices, settings.SearchServicesOff);
        if (!settings.OnlineSearch || services.Count == 0) return L.F("I can't find “{0}” in your library.", words);
        if (s.InRoom && !s.Together.CanAdd) return L.T("The room doesn't let you add songs.");
        // Another song chosen meanwhile (by hand, while the sites answer): what was found doesn't start over it.
        int choice = s.Player.Choice;
        busy?.Invoke(true);
        try
        {
            foreach (var service in services)
            {
                List<SearchHit> hits;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    hits = await Task.Run(() => OnlineSearchServices.SearchAsync(service, words, 12, cts.Token));
                }
                catch (Exception ex)
                {
                    App.Log(ex);
                    continue;
                }
                var songs = hits.Where(h => h.Collection == null).OrderBy(h => h.Rank).ToList();
                if (artist) songs = songs.Where(h => ArtistNames.Has(h.Artist, words)).ToList();
                if (songs.Count == 0) continue;
                if (choice != s.Player.Choice) return null;
                if (s.InRoom)
                {
                    s.OpenHit(songs[0]);
                    return null;
                }
                var play = artist ? songs : songs.Take(1).ToList();
                s.Player.PlayStream(s.Radio.AddFromLink(play.Select(OnlineSearchServices.ToItem)), artist ? words : play[0].Title);
                return null;
            }
            return L.F("I can't find “{0}”.", words);
        }
        finally { busy?.Invoke(false); }
    }

    // Play with nothing asked for ("Ok Google, play music on…", the car just connected): the last song from where it was, or the
    // whole library shuffled. In a room or driving a computer, Play is theirs: the library must not land in the room's queue.
    public static string? PlayDefault(MainViewModel s)
    {
        if (s.Player.InRoom && s.Together.Session?.Current == null) return L.T("The room's queue is empty.");
        if (s.Player.Current != null || s.Player.InRoom || s.Player.IsRemote)
        {
            s.SetPlaying(true);
            return null;
        }
        return Shuffle(s);
    }

    private static string? Shuffle(MainViewModel s)
    {
        s.LibraryPage.Rebuild();
        if (s.LibraryPage.PlayOrder.Count == 0) return L.T("No songs yet");
        s.Player.PlayAll(s.LibraryPage, true);
        return null;
    }

    private static bool Favorites(MainViewModel s)
        => s.Playlists.FirstOrDefault(p => p.IsFavorites) is { } vm && PlayList(s, new PlaylistPageViewModel(vm, s), s.Player.Shuffle);

    private static bool PlayList(MainViewModel s, ITrackList list, bool shuffle)
    {
        if (list.PlayOrder.Count == 0) return false;
        s.Player.PlayAll(list, shuffle);
        return true;
    }

    private static bool PlayPlaylist(MainViewModel s, string name)
    {
        var key = Text.Normalize(name);
        return key.Length > 0 && s.Playlists.FirstOrDefault(p => Text.Normalize(p.Name) == key) is { } vm
            && PlayList(s, new PlaylistPageViewModel(vm, s), s.Player.Shuffle);
    }

    private static bool PlayArtist(MainViewModel s, string name) => Text.Normalize(name).Length > 0 && PlayList(s, ArtistList(s, name), false);

    // An artist's songs in the order of the artist's page: the most heard first.
    public static ITrackList ArtistList(MainViewModel s, string name)
    {
        var stats = s.Profile.StatsSnapshot();
        var songs = s.Songs().Where(t => ArtistNames.Has(t.Artist, name))
            .OrderByDescending(t => stats.GetValueOrDefault(t.Id)?.Plays ?? 0).ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(s.Vm).ToList();
        return new TrackListSource("artistpage:" + Text.Normalize(name), name, songs);
    }

    // The album's songs of the library, by title (the library keeps no track numbers).
    public static ITrackList? AlbumList(MainViewModel s, string name, string? artist)
    {
        var key = Text.Normalize(name);
        if (key.Length == 0) return null;
        var songs = s.Songs().Where(t => Text.Normalize(t.Album) == key && (artist == null || ArtistNames.Has(t.Artist, artist)))
            .OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase).Select(s.Vm).ToList();
        return songs.Count == 0 ? null : new TrackListSource("album:" + key, songs[0].T.Album ?? name, songs);
    }

    private static bool PlayAlbum(MainViewModel s, string name, string? artist)
        => (AlbumList(s, name, artist) ?? (artist != null ? AlbumList(s, name, null) : null)) is { } list && PlayList(s, list, false);

    // A genre is one of your tags when it has its name ("rock", "chill").
    public static ITrackList? TagList(MainViewModel s, TagViewModel tag)
    {
        var songs = s.AllVms().Where(t => t.TagIds.Contains(tag.Id)).ToList();
        return songs.Count == 0 ? null : new TrackListSource("tag:" + tag.Id, tag.Name, songs);
    }

    private static bool PlayTag(MainViewModel s, string name)
    {
        var key = Text.Normalize(name);
        return key.Length > 0 && s.Tags.FirstOrDefault(t => Text.Normalize(t.Name) == key) is { } tag && TagList(s, tag) is { } list
            && PlayList(s, list, true);
    }

    private static bool PlayStation(MainViewModel s, string name)
    {
        if (s.Stations.SavedMatching(name).FirstOrDefault() is not { } station) return false;
        s.Stations.Play(station);
        return true;
    }

    private static bool PlaySongs(MainViewModel s, string words, string name)
    {
        var q = new TrackQuery(words);
        if (q.IsEmpty) return false;
        var key = Text.Normalize(words);
        // (the one titled exactly that first)
        var found = s.AllVms().Where(q.Matches).OrderByDescending(t => Text.Normalize(t.Title) == key).ToList();
        if (found.Count == 0) return false;
        s.Player.PlayFrom(new TrackListSource("voice:" + key, name, found), found[0]);
        return true;
    }
}
