namespace UltimateMP3Player.Core;

public sealed record RecapSong(string Id, string Title, string Artist, int Plays, double Seconds);
public sealed record RecapArtist(string Name, int Plays, double Seconds);

// A year (Month 0) or a month of listening, from the profile's ListeningLog: the time, the most played songs and artists,
// when you listen. What the recap's pages show (RecapViewModel).
public sealed class Recap
{
    public int Year { get; init; }
    public int Month { get; init; }
    public double Seconds { get; init; }
    public int Plays { get; init; }
    public int SongCount { get; init; }
    public int ArtistCount { get; init; }
    public List<RecapSong> TopSongs { get; init; } = new();
    public List<RecapArtist> TopArtists { get; init; } = new();
    // Seconds by hour of the day; by month (a year: 12) or by day (a month: its days).
    public double[] Hours { get; init; } = new double[24];
    public double[] Parts { get; init; } = Array.Empty<double>();
    // The same period before (the year or the month before): to compare.
    public double PreviousSeconds { get; init; }

    public int TopHour => Array.IndexOf(Hours, Hours.Max());
    // The busiest month (1-12) or day (1-31).
    public int BusiestPart => Parts.Length == 0 ? 0 : Array.IndexOf(Parts, Parts.Max()) + 1;

    // Null: nothing listened in it.
    public static Recap? Make(ListeningLog log, int year, int month, int top = 5)
    {
        var months = new List<MonthStats>();
        double[] parts;
        double previous;
        if (month == 0)
        {
            var y = log.Year(year);
            parts = new double[12];
            if (y != null)
                foreach (var (m, s) in y.Months)
                {
                    months.Add(s);
                    if (m is >= 1 and <= 12) parts[m - 1] = s.Seconds;
                }
            previous = log.Year(year - 1)?.Months.Values.Sum(s => s.Seconds) ?? 0;
        }
        else
        {
            if (log.Month(year, month) is { } m) months.Add(m);
            parts = months.Count > 0 ? months[0].Days.Take(DateTime.DaysInMonth(year, month)).ToArray() : Array.Empty<double>();
            var before = new DateTime(year, month, 1).AddMonths(-1);
            previous = log.Month(before.Year, before.Month)?.Seconds ?? 0;
        }
        if (months.Count == 0) return null;

        var songs = new Dictionary<string, (string Title, string Artist, int Plays, double Seconds)>();
        var hours = new double[24];
        foreach (var m in months)
        {
            for (int h = 0; h < 24 && h < m.Hours.Length; h++) hours[h] += m.Hours[h];
            foreach (var (id, s) in m.Songs)
            {
                var (title, artist) = ListeningLog.Names?.Invoke(id) ?? (null, null);
                var had = songs.GetValueOrDefault(id, (title ?? s.Title ?? "", artist ?? s.Artist ?? "", 0, 0));
                songs[id] = (had.Title, had.Artist, had.Plays + s.Plays, had.Seconds + s.Seconds);
            }
        }
        double seconds = songs.Values.Sum(s => s.Seconds);
        if (seconds < 1 && songs.Values.All(s => s.Plays == 0)) return null;

        var artists = new Dictionary<string, (string Name, int Plays, double Seconds)>();
        foreach (var s in songs.Values)
            foreach (var name in ArtistNames.Of(s.Artist))
            {
                var key = Text.Normalize(name);
                if (key.Length == 0) continue;
                var had = artists.GetValueOrDefault(key, (name, 0, 0));
                artists[key] = (had.Name, had.Plays + s.Plays, had.Seconds + s.Seconds);
            }

        return new Recap
        {
            Year = year, Month = month, Seconds = seconds, Plays = songs.Values.Sum(s => s.Plays), SongCount = songs.Count, ArtistCount = artists.Count,
            TopSongs = songs.Where(kv => kv.Value.Title.Length > 0).OrderByDescending(kv => kv.Value.Plays).ThenByDescending(kv => kv.Value.Seconds).Take(top)
                .Select(kv => new RecapSong(kv.Key, kv.Value.Title, kv.Value.Artist, kv.Value.Plays, kv.Value.Seconds)).ToList(),
            TopArtists = artists.Values.OrderByDescending(a => a.Plays).ThenByDescending(a => a.Seconds).Take(top)
                .Select(a => new RecapArtist(a.Name, a.Plays, a.Seconds)).ToList(),
            Hours = hours, Parts = parts, PreviousSeconds = previous,
        };
    }

    // The periods with something listened: each year, then its months, the newest first.
    public static List<(int Year, int Month)> Periods(ListeningLog log)
    {
        var list = new List<(int, int)>();
        foreach (var y in log.Years())
        {
            if (log.Year(y) is not { } stats || stats.Months.Count == 0) continue;
            list.Add((y, 0));
            foreach (var m in stats.Months.Where(kv => kv.Value.Songs.Count > 0).Select(kv => kv.Key).OrderByDescending(m => m)) list.Add((y, m));
        }
        return list;
    }
}
