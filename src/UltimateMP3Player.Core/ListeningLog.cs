using System.Text.Json.Serialization;

namespace UltimateMP3Player.Core;

// One song in one month.
public sealed class MonthSong
{
    // (masked in memory: Sealed)
    private int _plays = Sealed.Mask(0);
    private long _seconds = Sealed.Mask(0.0);
    [JsonPropertyName("p")] public int Plays { get => Sealed.Mask(_plays); set => _plays = Sealed.Mask(value); }
    [JsonPropertyName("s")] public double Seconds { get => Sealed.Unmask(_seconds); set => _seconds = Sealed.Mask(value); }
    // Its title and artist as they were: the recaps name songs deleted since, and the ones imported that aren't in the library.
    [JsonPropertyName("t")] public string? Title { get; set; }
    [JsonPropertyName("a")] public string? Artist { get; set; }
    // Imported from another service: its link there (to add it to the library).
    [JsonPropertyName("u")] public string? Url { get; set; }

    public MonthSong Clone() => (MonthSong)MemberwiseClone();
}

public sealed class MonthStats
{
    // Song id → plays and time; plays imported for songs not in the library have a key of their own ("sp:<Spotify id>").
    public Dictionary<string, MonthSong> Songs { get; set; } = new();
    // Seconds listened by hour of the day and by day of the month.
    public double[] Hours { get; set; } = new double[24];
    public double[] Days { get; set; } = new double[31];

    [JsonIgnore] public double Seconds => Songs.Values.Sum(s => s.Seconds);
    [JsonIgnore] public int Plays => Songs.Values.Sum(s => s.Plays);

    public MonthStats Clone() => new()
    {
        Songs = Songs.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()), Hours = (double[])Hours.Clone(), Days = (double[])Days.Clone(),
    };
}

public sealed class YearStats
{
    // Month (1-12) → what was listened to in it.
    public Dictionary<int, MonthStats> Months { get; set; } = new();

    public YearStats Clone() => new() { Months = Months.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()) };
}

// What a profile listened to, month by month (the all-time counts are Profile.Data.Stats): the recaps of a year or of a
// month, the "Your month" mix, the plays imported from Spotify. One file per year in the profile's folder (stats/2026.dat,
// sealed: Sealed): the past years are written once, only the current one keeps changing. A profile of the connected device
// (dir null) keeps it in memory only.
public sealed class ListeningLog
{
    // The title and artist of a song by its id (the library, the songs heard without saving them): set by the app.
    public static Func<string, (string? Title, string? Artist)>? Names { get; set; }

    private readonly string? _dir;
    private readonly object _lock = new();
    private readonly Dictionary<int, YearStats> _years = new();
    private readonly HashSet<int> _dirty = new();
    private readonly DebouncedSaver _saver;

    public ListeningLog(string? dir)
    {
        _dir = dir;
        _saver = new DebouncedSaver(SaveNow, 5000);
    }

    private string? FileOf(int year) => _dir == null ? null : Path.Combine(_dir, year + ".dat");
    // (4.3's test builds wrote them as plain JSON: sealed at the next save)
    private string? PlainFileOf(int year) => _dir == null ? null : Path.Combine(_dir, year + ".json");

    // (under the lock)
    private YearStats? YearOf(int year, bool create)
    {
        if (_years.TryGetValue(year, out var y)) return y;
        if (FileOf(year) is { } f && (File.Exists(f) || File.Exists(f + ".bak"))) y = Sealed.Load<YearStats>(f);
        else if (PlainFileOf(year) is { } plain && File.Exists(plain))
        {
            y = JsonStore.Load<YearStats>(plain);
            _dirty.Add(year);
            _saver.Request();
        }
        else if (!create) return null;
        y ??= new YearStats();
        foreach (var m in y.Months.Values)
        {
            if (m.Hours.Length != 24) m.Hours = new double[24];
            if (m.Days.Length != 31) m.Days = new double[31];
        }
        _years[year] = y;
        return y;
    }

    private static MonthStats MonthIn(YearStats y, int month)
    {
        if (!y.Months.TryGetValue(month, out var m)) y.Months[month] = m = new MonthStats();
        return m;
    }

    public void Add(string id, double seconds, bool play, DateTime at)
    {
        if (seconds <= 0 && !play) return;
        lock (_lock)
        {
            var m = MonthIn(YearOf(at.Year, true)!, at.Month);
            if (!m.Songs.TryGetValue(id, out var s))
            {
                var (title, artist) = Names?.Invoke(id) ?? (null, null);
                m.Songs[id] = s = new MonthSong { Title = title, Artist = artist };
            }
            s.Seconds += seconds;
            if (play) s.Plays++;
            m.Hours[at.Hour] += seconds;
            m.Days[at.Day - 1] += seconds;
            _dirty.Add(at.Year);
        }
        if (_dir != null) _saver.Request();
    }

    // Plays from elsewhere (an import): each one into its month. Returns how many were added.
    public int Import(IEnumerable<(DateTime At, string Id, string? Title, string? Artist, string? Url, double Seconds, bool Play)> plays)
    {
        int n = 0;
        lock (_lock)
        {
            foreach (var p in plays)
            {
                var m = MonthIn(YearOf(p.At.Year, true)!, p.At.Month);
                if (!m.Songs.TryGetValue(p.Id, out var s)) m.Songs[p.Id] = s = new MonthSong { Title = p.Title, Artist = p.Artist, Url = p.Url };
                s.Seconds += p.Seconds;
                if (p.Play) s.Plays++;
                m.Hours[p.At.Hour] += p.Seconds;
                m.Days[p.At.Day - 1] += p.Seconds;
                _dirty.Add(p.At.Year);
                n++;
            }
        }
        if (_dir != null && n > 0) _saver.Request();
        return n;
    }

    // A copy of a year (null: nothing listened that year).
    public YearStats? Year(int year)
    {
        lock (_lock) return YearOf(year, false)?.Clone();
    }

    public MonthStats? Month(int year, int month)
    {
        lock (_lock) return YearOf(year, false) is { } y && y.Months.TryGetValue(month, out var m) ? m.Clone() : null;
    }

    // The years with something in them, the newest first.
    public List<int> Years()
    {
        lock (_lock)
        {
            var years = _years.Where(kv => kv.Value.Months.Count > 0).Select(kv => kv.Key).ToHashSet();
            if (_dir != null && Directory.Exists(_dir))
                foreach (var f in Directory.GetFiles(_dir))
                    if (Path.GetExtension(f) is ".dat" or ".json" && int.TryParse(Path.GetFileNameWithoutExtension(f), out var y)) years.Add(y);
            return years.OrderByDescending(y => y).ToList();
        }
    }

    // A song's plays and time in every month, and the last month it was heard in (its first day).
    public (int Plays, double Seconds, DateTime? Last) Totals(string id)
    {
        lock (_lock)
        {
            int plays = 0;
            double seconds = 0;
            DateTime? last = null;
            foreach (var year in Years())
                if (YearOf(year, false) is { } y)
                    foreach (var (month, m) in y.Months)
                        if (m.Songs.TryGetValue(id, out var s))
                        {
                            plays += s.Plays;
                            seconds += s.Seconds;
                            var at = new DateTime(year, month, 1);
                            if (last == null || at > last) last = at;
                        }
            return (plays, seconds, last);
        }
    }

    // A song heard without saving it, kept in the library since: its plays are the library song's.
    public void Rename(string oldId, string newId)
    {
        lock (_lock)
        {
            foreach (var year in Years())
            {
                var y = YearOf(year, false);
                if (y == null) continue;
                foreach (var m in y.Months.Values)
                {
                    if (!m.Songs.Remove(oldId, out var s)) continue;
                    if (m.Songs.TryGetValue(newId, out var had))
                    {
                        had.Plays += s.Plays;
                        had.Seconds += s.Seconds;
                    }
                    else m.Songs[newId] = s;
                    _dirty.Add(year);
                }
            }
        }
        if (_dir != null) _saver.Request();
    }

    public void Flush() => _saver.Flush();

    private volatile bool _closed;
    // The profile was deleted: its folder must not come back.
    public void Close() => _closed = true;

    private void SaveNow()
    {
        if (_dir == null || _closed) return;
        List<(int Number, string Path, YearStats Year)> todo;
        lock (_lock)
        {
            todo = new();
            foreach (var year in _dirty)
            {
                if (!_years.TryGetValue(year, out var y)) continue;
                // (tenths of a second are plenty, and the file stays small)
                foreach (var m in y.Months.Values)
                {
                    foreach (var s in m.Songs.Values) s.Seconds = Math.Round(s.Seconds, 1);
                    for (int i = 0; i < 24; i++) m.Hours[i] = Math.Round(m.Hours[i], 1);
                    for (int i = 0; i < 31; i++) m.Days[i] = Math.Round(m.Days[i], 1);
                }
                todo.Add((year, FileOf(year)!, y.Clone()));
            }
            _dirty.Clear();
        }
        if (todo.Count == 0) return;
        try
        {
            Directory.CreateDirectory(_dir);
            foreach (var (number, path, y) in todo)
            {
                Sealed.Save(path, y);
                if (PlainFileOf(number) is { } plain && File.Exists(plain)) File.Delete(plain);
            }
        }
        catch
        {
            // (written again at the next flush: DebouncedSaver asks once more)
            lock (_lock) foreach (var t in todo) _dirty.Add(t.Number);
            throw;
        }
    }
}
