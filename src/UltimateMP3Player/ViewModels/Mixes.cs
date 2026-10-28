using System.Windows.Input;
using System.Windows.Media;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// A mix of Home › For you: songs picked from what you listen to; a tap plays them. An artist's mix goes on with songs like
// its last ones found online, like the suggested songs (kept with the button by the heart).
public sealed class MixViewModel : Observable
{
    private readonly MainViewModel _main;
    private readonly Func<Task<List<TrackViewModel>>> _songs;
    private bool _starting;

    public MixViewModel(MainViewModel main, string id, string title, string subtitle, string glyph, int colour, TrackViewModel cover, bool online,
        Func<Task<List<TrackViewModel>>> songs)
    {
        _main = main;
        Id = id;
        Title = title;
        Subtitle = subtitle;
        Glyph = glyph;
        Brush = Ui.BrushFrom(ProfileInfo.Palette[colour % ProfileInfo.Palette.Length]);
        Cover = cover;
        Online = online;
        _songs = songs;
        PlayCommand = new RelayCommand(() => _ = Play());
    }

    public string Id { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string Glyph { get; }
    public Brush Brush { get; }
    public TrackViewModel Cover { get; }
    public bool Online { get; }
    public ICommand PlayCommand { get; }

    private async Task Play()
    {
        if (_starting) return;
        _starting = true;
        try
        {
            var list = await _songs();
            if (list.Count > 0) _main.Player.PlayMix(list, "mix:" + Id, Title, Online);
        }
        finally { _starting = false; }
    }
}

// The mixes made again each time Home is: your month, the favourites not heard for a while, the artists you listen to most.
public static class Mixes
{
    // Songs in a mix; a favourite (or a song played often) not heard for this long is "rediscovered".
    private const int Size = 30;
    private static readonly TimeSpan Forgotten = TimeSpan.FromDays(60);

    public static List<MixViewModel> Build(MainViewModel main)
    {
        var mixes = new List<MixViewModel>();
        var p = main.Profile;
        var stats = p.StatsSnapshot();
        var now = DateTime.Now;
        int Plays(Track t) => stats.GetValueOrDefault(t.Id)?.Plays ?? 0;

        // Your month: the most played of this month (of the last one, in the first days of a new one).
        var month = new DateTime(now.Year, now.Month, 1);
        var heard = p.Log.Month(month.Year, month.Month);
        if (now.Day <= 7 && (heard == null || heard.Songs.Count < 8))
        {
            month = month.AddMonths(-1);
            heard = p.Log.Month(month.Year, month.Month);
        }
        if (heard != null)
        {
            var songs = heard.Songs.OrderByDescending(kv => kv.Value.Plays).ThenByDescending(kv => kv.Value.Seconds)
                .Select(kv => main.Get(kv.Key)).OfType<Track>().Take(Size).Select(main.Vm).ToList();
            if (songs.Count >= 5)
                mixes.Add(new MixViewModel(main, "month", L.F("Your {0}", month.ToString("MMMM", L.Culture)),
                    month.Month == now.Month ? L.T("What you played most this month") : L.T("What you played most last month"),
                    "\uE787", 4, songs[0], false, () => Task.FromResult(songs)));
        }

        // The artists you listen to most lately (the last three months; all time while there's little of them).
        var recent = new Dictionary<string, (string Name, int Plays)>();
        for (int i = 0; i < 3; i++)
        {
            var m = month.AddMonths(-i);
            if (p.Log.Month(m.Year, m.Month) is not { } ms) continue;
            foreach (var (id, s) in ms.Songs)
                if (main.Get(id) is { } t)
                    foreach (var name in ArtistNames.Of(t.Artist))
                    {
                        var key = Text.Normalize(name);
                        if (key.Length == 0) continue;
                        var had = recent.GetValueOrDefault(key, (Name: name, Plays: 0));
                        recent[key] = (had.Name, had.Plays + s.Plays);
                    }
        }
        var top = recent.Values.OrderByDescending(a => a.Plays).Select(a => a.Name)
            // (Home is made before the artists, at the start)
            .Concat(main.Artists is { } artists ? artists.TopArtists(6).Select(a => a.Name) : Enumerable.Empty<string>()).Distinct(StringComparer.CurrentCultureIgnoreCase);
        var library = main.Songs();
        int made = 0;
        foreach (var name in top)
        {
            var own = library.Where(t => ArtistNames.Has(t.Artist, name)).OrderByDescending(Plays).ToList();
            if (own.Count < 3) continue;
            var artist = name;
            mixes.Add(new MixViewModel(main, "artist:" + Text.Normalize(artist), L.F("{0} Mix", artist), L.F("{0} and artists like them, then new songs", artist),
                "\uE77B", 1 + made * 2, main.Vm(own[0]), true, () => ArtistMixAsync(main, artist)));
            if (++made == 3) break;
        }

        // Rediscovered: favourites and songs played often, not heard for months.
        var favourites = p.Favorites.Tracks.ToHashSet();
        var forgotten = library.Where(t => (favourites.Contains(t.Id) || Plays(t) >= 5) && stats.GetValueOrDefault(t.Id)?.Last is { } last && now - last > Forgotten)
            .OrderByDescending(Plays).Take(Size).Select(main.Vm).ToList();
        if (forgotten.Count >= 5)
            mixes.Add(new MixViewModel(main, "rediscovered", L.T("Rediscovered"), L.T("Favorites you haven't played in months"), "\uE823", 6,
                forgotten[0], false, () => Task.FromResult(forgotten.OrderBy(_ => Random.Shared.Next()).ToList())));
        return mixes;
    }

    // An artist's mix: your songs of it and of the artists like it (Deezer's), taking turns, shuffled; then (PlayMix) songs
    // like them found online. Without a connection, only its own songs.
    private static async Task<List<TrackViewModel>> ArtistMixAsync(MainViewModel main, string name)
    {
        var stats = main.Profile.StatsSnapshot();
        int Plays(Track t) => stats.GetValueOrDefault(t.Id)?.Plays ?? 0;
        var library = main.Songs();
        var own = library.Where(t => ArtistNames.Has(t.Artist, name)).OrderByDescending(Plays).Take(18).OrderBy(_ => Random.Shared.Next()).ToList();
        var others = new List<Track>();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var id = main.Profile.FollowedArtist(name)?.DeezerId ?? (await OnlineSearchServices.ArtistInfoAsync(name, cts.Token))?.Id;
            if (id != null)
                foreach (var a in await OnlineSearchServices.RelatedArtistsAsync(id, 20, cts.Token))
                    others.AddRange(library.Where(t => ArtistNames.Has(t.Artist, a.Name)).OrderByDescending(Plays).Take(3));
        }
        catch { }
        others = others.Distinct().Except(own).OrderBy(_ => Random.Shared.Next()).Take(12).ToList();
        var mix = new List<Track>();
        for (int i = 0, j = 0; i < own.Count || j < others.Count;)
        {
            // two of its, one of another's
            for (int k = 0; k < 2 && i < own.Count; k++) mix.Add(own[i++]);
            if (j < others.Count) mix.Add(others[j++]);
        }
        return mix.Select(main.Vm).ToList();
    }
}
