using System.Windows.Input;
using System.Windows.Media;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// A song or an artist of the recap's lists: its place, its cover (a song of the library) or picture (an artist, Deezer's).
public sealed class RecapRow : Observable
{
    private ImageSource? _picture;

    public RecapRow(int rank, string title, string subtitle, string detail, TrackViewModel? track)
    {
        Rank = rank;
        Title = title;
        Subtitle = subtitle;
        Detail = detail;
        Track = track;
    }

    public int Rank { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public bool HasSubtitle => Subtitle.Length > 0;
    public string Detail { get; }
    public TrackViewModel? Track { get; }
    public bool HasTrack => Track != null;
    public string Initial => ArtistPictures.Initial(Title);
    public Brush Brush => ArtistPictures.BrushFor(Title);
    public ImageSource? Picture => _picture;
    public bool HasPicture => _picture != null;

    // An artist's picture: the one chosen by hand, the followed one's, or Deezer's found by its name (kept on the disk once seen).
    public async Task LoadPictureAsync(Profile profile)
    {
        try
        {
            var followed = profile.FollowedArtist(Title);
            _picture = await ArtistPictures.LoadAsync(Title, followed?.DeezerId, followed?.Picture, 320);
            OnChanged(nameof(Picture), nameof(HasPicture));
        }
        catch { }
    }
}

// One of the bars of "when you listen" (an hour of the day).
public sealed record RecapBar(double Height, bool IsTop);

// One of the lines at the top, like stories: filled up to the page shown.
public sealed record RecapStep(bool Done);

// The recap of a year or of a month, one page at a time like stories: the time, the song and the artist of the period,
// the top fives, when you listen, and a summary to share as a picture. Home opens the year's in December and early January;
// the statistics any period.
public sealed class RecapViewModel : Observable
{
    private readonly MainViewModel _main;
    private List<string> _pages = new() { "intro" };
    private int _index;

    public RecapViewModel(MainViewModel main, int year, int month)
    {
        _main = main;
        Periods = Recap.Periods(main.Profile.Log).Select(p => new Choice(PeriodName(p.Year, p.Month), p)).ToList();
        NextCommand = new RelayCommand(() => Go(_index + 1));
        PrevCommand = new RelayCommand(() => Go(_index - 1));
        CloseCommand = new RelayCommand(() => _main.BackCommand.Execute(null));
        _period = Periods.FirstOrDefault(c => c.Value is (int y, int m) && y == year && m == month) ?? Periods.FirstOrDefault();
        Load();
    }

    public ICommand NextCommand { get; }
    public ICommand PrevCommand { get; }
    public ICommand CloseCommand { get; }

    // ------------------------------------------------------------------ the period

    public List<Choice> Periods { get; }

    private Choice? _period;
    public Choice? Period
    {
        get => _period;
        set
        {
            if (value == null || value == _period) return;
            _period = value;
            OnChanged();
            Load();
        }
    }

    public static string PeriodName(int year, int month)
    {
        if (month == 0) return year.ToString();
        var s = new DateTime(year, month, 1).ToString("MMMM yyyy", L.Culture);
        return char.ToUpper(s[0], L.Culture) + s[1..];
    }

    public Recap? Data { get; private set; }
    public bool IsYear => Data?.Month == 0;
    public string PeriodTitle => Data == null ? "" : PeriodName(Data.Year, Data.Month);

    private void Load()
    {
        Data = _period?.Value is (int y, int m) ? Recap.Make(_main.Profile.Log, y, m) : null;
        _pages = new List<string> { "intro" };
        if (Data != null)
        {
            _pages.Add("time");
            if (Data.TopSongs.Count > 0) _pages.Add("song");
            if (Data.TopSongs.Count >= 3) _pages.Add("songs");
            if (Data.TopArtists.Count > 0) _pages.Add("artist");
            if (Data.TopArtists.Count >= 3) _pages.Add("artists");
            _pages.Add("clock");
            _pages.Add("summary");
        }
        Songs = Data?.TopSongs.Select((s, i) => new RecapRow(i + 1, s.Title, s.Artist, L.Count(s.Plays, "1 play", "{0} plays"),
            _main.Get(s.Id) is { } t ? _main.Vm(t) : null)).ToList() ?? new();
        Artists = Data?.TopArtists.Select((a, i) => new RecapRow(i + 1, a.Name, "", L.Count(a.Plays, "1 play", "{0} plays") + " · " + StatsViewModel.TimeText(a.Seconds),
            null)).ToList() ?? new();
        foreach (var a in Artists) _ = a.LoadPictureAsync(_main.Profile);
        var hours = Data?.Hours ?? new double[24];
        double max = Math.Max(1, hours.Max());
        int top = Data?.TopHour ?? -1;
        Bars = hours.Select((s, h) => new RecapBar(6 + 110 * s / max, h == top)).ToList();
        _index = 0;
        OnChanged(string.Empty);
    }

    // ------------------------------------------------------------------ the pages

    public string Page => _pages[_index];
    public int Index => _index;
    public bool IsIntro => Page == "intro";
    public bool IsTime => Page == "time";
    public bool IsSong => Page == "song";
    public bool IsSongs => Page == "songs";
    public bool IsArtist => Page == "artist";
    public bool IsArtists => Page == "artists";
    public bool IsClock => Page == "clock";
    public bool IsSummary => Page == "summary";
    public bool IsEmpty => Data == null;
    public bool CanGoBack => _index > 0;
    public bool CanGoOn => _index < _pages.Count - 1;
    public List<RecapStep> Steps => _pages.Select((_, i) => new RecapStep(i <= _index)).ToList();

    public void Go(int index)
    {
        if (index < 0 || index >= _pages.Count || index == _index) return;
        _index = index;
        OnChanged(nameof(Page), nameof(Index), nameof(IsIntro), nameof(IsTime), nameof(IsSong), nameof(IsSongs), nameof(IsArtist), nameof(IsArtists),
            nameof(IsClock), nameof(IsSummary), nameof(CanGoBack), nameof(CanGoOn), nameof(Steps));
    }

    // ------------------------------------------------------------------ what they say

    public string IntroKicker => IsYear ? L.T("YOUR YEAR IN MUSIC") : L.T("YOUR MONTH IN MUSIC");
    public string IntroText => IsEmpty ? L.T("Nothing listened to in this period.") : L.T("Let's look back at what you listened to.");
    public string HintText => L.T("Tap on the right to go on, on the left to go back");

    public int Minutes => (int)((Data?.Seconds ?? 0) / 60);
    public string MinutesText => Minutes.ToString("N0", L.Culture);
    public string CountsText => Data == null ? "" : string.Join(" · ", L.Count(Data.Plays, "1 play", "{0} plays"),
        L.Count(Data.SongCount, "1 song", "{0} songs"), L.Count(Data.ArtistCount, "1 artist", "{0} artists"));

    // Against the period before, or how long it is in a row.
    public string CompareText
    {
        get
        {
            if (Data == null) return "";
            if (Data.PreviousSeconds >= 600)
            {
                var before = Data.Month == 0 ? PeriodName(Data.Year - 1, 0)
                    : PeriodName(new DateTime(Data.Year, Data.Month, 1).AddMonths(-1).Year, new DateTime(Data.Year, Data.Month, 1).AddMonths(-1).Month);
                int pct = (int)Math.Round((Data.Seconds - Data.PreviousSeconds) * 100 / Data.PreviousSeconds);
                // (Italian says "al 2025" but "a settembre")
                if (pct >= 1) return L.F(Data.Month == 0 ? "{0}% more than in {1}" : "{0}% more than in {1}|month", pct, before);
                if (pct <= -1) return L.F(Data.Month == 0 ? "{0}% less than in {1}" : "{0}% less than in {1}|month", -pct, before);
            }
            return Data.Seconds >= 86400 ? L.F("That's {0} days of music, non-stop", (Data.Seconds / 86400).ToString("0.#", L.Culture))
                : L.F("That's {0} of music, non-stop", StatsViewModel.TimeText(Data.Seconds));
        }
    }

    public string SongKicker => IsYear ? L.T("YOUR SONG OF THE YEAR") : L.T("YOUR SONG OF THE MONTH");
    public string ArtistKicker => IsYear ? L.T("YOUR ARTIST OF THE YEAR") : L.T("YOUR ARTIST OF THE MONTH");
    public List<RecapRow> Songs { get; private set; } = new();
    public List<RecapRow> Artists { get; private set; } = new();
    public RecapRow? TopSong => Songs.FirstOrDefault();
    public RecapRow? TopArtist => Artists.FirstOrDefault();
    public string TopSongText => Data?.TopSongs.FirstOrDefault() is { } s
        ? L.F("{0} times · {1}", s.Plays, StatsViewModel.TimeText(s.Seconds)) : "";
    public List<RecapRow> TopFiveSongs => Songs.Take(5).ToList();
    public List<RecapRow> TopFiveArtists => Artists.Take(5).ToList();

    public List<RecapBar> Bars { get; private set; } = new();
    public string HourText => Data == null ? "" : L.F("{0}:00 – {1}:00", Data.TopHour, (Data.TopHour + 1) % 24);
    public string HourLine => L.T("the hour of the day you listened to the most");
    public string BusiestText => Data == null || Data.BusiestPart == 0 ? ""
        : Data.Month == 0 ? L.F("Your month: {0}", new DateTime(Data.Year, Data.BusiestPart, 1).ToString("MMMM", L.Culture))
        : L.F("Your day: {0}", new DateTime(Data.Year, Data.Month, Data.BusiestPart).ToString("d MMMM", L.Culture));

    public string ShareName => L.F("Recap {0}", PeriodTitle) + ".png";
    public void Toast(string text) => _main.Toast(text);
}
