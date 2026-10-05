using System.Windows.Input;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// "Listening statistics": every song with how many times and how long it was heard on this profile, the favourite
// ones first or the forgotten ones (selected, they're deleted like anywhere else).
public sealed class StatsViewModel : Observable, ITrackList
{
    private readonly MainViewModel _main;
    private List<TrackViewModel> _order = new();

    public StatsViewModel(MainViewModel main)
    {
        _main = main;
        Sorts = new List<Choice>
        {
            new(L.T("Più ascoltati"), "most"), new(L.T("Meno ascoltati"), "least"), new(L.T("Più tempo di ascolto"), "time"),
            new(L.T("Ascoltati di recente"), "recent"), new(L.T("Mai ascoltati"), "never"),
        };
        _sort = Sorts[0];
        PlayCommand = new RelayCommand(() => _main.Player.PlayAll(this, false), () => _order.Count > 0);
        TagFilter.Changed += ApplyFilter;
    }

    public string ContextId => "stats";
    public string ContextName => L.T("Statistiche di ascolto");
    public IReadOnlyList<TrackViewModel> PlayOrder => _order;
    public Playlist? Playlist => null;
    public TagFilter TagFilter { get; } = new();
    public ICommand PlayCommand { get; }

    public List<Choice> Sorts { get; }
    private Choice _sort;
    public Choice Sort
    {
        get => _sort;
        set
        {
            if (value != null && Set(ref _sort, value)) Rebuild();
        }
    }

    public List<TrackRow> Rows { get; private set; } = new();
    public bool IsEmpty => _order.Count == 0;
    public string EmptyText => (string)_sort.Value! == "never" ? L.T("Hai ascoltato tutti i tuoi brani almeno una volta.") : L.T("Ascolta qualcosa: i brani compariranno qui.");

    private string _filter = "";
    public string Filter { get => _filter; set { if (Set(ref _filter, value)) ApplyFilter(); } }
    public bool NoMatches => Rows.Count == 0 && _order.Count > 0;

    // The header: time, plays and songs heard, since when.
    public string TimeTotal { get; private set; } = "";
    public string PlaysTotal { get; private set; } = "";
    public string HeardTotal { get; private set; } = "";
    public string SinceText { get; private set; } = "";
    public int NeverCount { get; private set; }
    public bool HasNever => NeverCount > 0;
    public string NeverText => L.Count(NeverCount, "Seleziona l'unico mai ascoltato", "Seleziona i {0} mai ascoltati");

    public void ApplyFilter()
    {
        Rows = TrackFilter.Apply(_order, _filter, TagFilter, this);
        OnChanged(nameof(Rows), nameof(NoMatches));
    }

    public void Rebuild()
    {
        var stats = _main.Profile.StatsSnapshot();
        TrackStats? Of(Track t) => stats.GetValueOrDefault(t.Id);
        var all = _main.Library.Snapshot();
        IEnumerable<Track> sorted = (string)_sort.Value! switch
        {
            "least" => all.OrderBy(t => Of(t)?.Plays ?? 0).ThenBy(t => Of(t)?.Seconds ?? 0).ThenBy(t => t.Added),
            "time" => all.Where(t => Of(t) is { Seconds: > 0 }).OrderByDescending(t => Of(t)!.Seconds).ThenByDescending(t => Of(t)!.Plays),
            "recent" => all.Where(t => Of(t)?.Last != null).OrderByDescending(t => Of(t)!.Last),
            "never" => all.Where(t => Of(t) is not { Plays: > 0 }).OrderByDescending(t => t.Added),
            _ => all.Where(t => Of(t) is { Plays: > 0 }).OrderByDescending(t => Of(t)!.Plays).ThenByDescending(t => Of(t)!.Seconds),
        };
        _order = sorted.Select(_main.Vm).ToList();
        foreach (var vm in _order) vm.RefreshStats();
        var mine = all.Select(Of).OfType<TrackStats>().ToList();
        TimeTotal = TimeText(mine.Sum(s => s.Seconds));
        PlaysTotal = L.Count(mine.Sum(s => s.Plays), "1 ascolto", "{0} ascolti");
        HeardTotal = L.F("{0} brani su {1}", mine.Count(s => s.Plays > 0), all.Count);
        NeverCount = all.Count - mine.Count(s => s.Plays > 0);
        SinceText = _main.Profile.Data.StatsSince is { } since ? L.F("Contati dal {0}", since.ToString("d MMMM yyyy", L.Culture)) : "";
        ApplyFilter();
        OnChanged(nameof(IsEmpty), nameof(EmptyText), nameof(TimeTotal), nameof(PlaysTotal), nameof(HeardTotal), nameof(SinceText), nameof(NeverCount),
            nameof(HasNever), nameof(NeverText));
    }

    // "2 h 5 min", "12 min", "less than a minute".
    public static string TimeText(double seconds)
    {
        if (seconds < 60) return seconds > 0 ? L.T("meno di un minuto") : L.F("{0} min", 0);
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? L.F("{0} h {1} min", (int)t.TotalHours, t.Minutes) : L.F("{0} min", t.Minutes);
    }

    // "today", "yesterday", "3 days ago"... or the date, long ago.
    public static string AgoText(DateTime d)
    {
        int days = (DateTime.Today - d.Date).Days;
        return days switch
        {
            <= 0 => L.T("oggi"),
            1 => L.T("ieri"),
            < 7 => L.F("{0} giorni fa", days),
            < 30 => L.Count(days / 7, "1 settimana fa", "{0} settimane fa"),
            < 365 => L.Count(days / 30, "1 mese fa", "{0} mesi fa"),
            _ => d.ToString("d MMM yyyy", L.Culture),
        };
    }
}
