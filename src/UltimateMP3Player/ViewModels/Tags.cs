using System.Windows.Media;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

public sealed class TagViewModel : Observable
{
    private readonly MainViewModel _main;

    public TagViewModel(Tag t, MainViewModel main)
    {
        T = t;
        _main = main;
    }

    public Tag T { get; }
    public string Id => T.Id;
    public string Name => T.Name;
    public string Color => T.Color;
    public Brush Brush => Ui.BrushFrom(T.Color);
    public string SearchName => Text.Normalize(T.Name);

    public int Count => _main.Profile.CountTagged(Id);
    public string CountText => L.Count(Count, "1 brano", "{0} brani");

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public void Refresh() => OnChanged(nameof(Name), nameof(Color), nameof(Brush), nameof(SearchName), nameof(Count), nameof(CountText));
    public void RefreshCount() => OnChanged(nameof(Count), nameof(CountText));

    public override string ToString() => Name;
}

// The tags chosen to filter a list: songs with all of them, or with at least one.
public sealed class TagFilter : Observable
{
    private readonly HashSet<string> _ids = new();

    public event Action? Changed;

    public IReadOnlyCollection<string> Ids => _ids;
    public bool IsActive => _ids.Count > 0;
    public string Label => _ids.Count == 0 ? L.T("Tag") : L.F("Tag · {0}", _ids.Count);

    private bool _matchAll = true;
    public bool MatchAll
    {
        get => _matchAll;
        set { if (Set(ref _matchAll, value) && IsActive) Raise(); }
    }

    public bool Contains(string id) => _ids.Contains(id);

    public void Toggle(string id)
    {
        if (!_ids.Remove(id)) _ids.Add(id);
        Raise();
    }

    public void Clear()
    {
        if (_ids.Count == 0) return;
        _ids.Clear();
        Raise();
    }

    // Deleted tags stop filtering.
    public void Keep(ISet<string> existing)
    {
        if (_ids.RemoveWhere(id => !existing.Contains(id)) > 0) Raise();
    }

    public bool Matches(TrackViewModel t)
    {
        if (_ids.Count == 0) return true;
        var tags = t.TagIds;
        return _matchAll ? _ids.All(tags.Contains) : _ids.Any(tags.Contains);
    }

    private void Raise()
    {
        OnChanged(nameof(IsActive), nameof(Label));
        Changed?.Invoke();
    }
}

// A song with a tick box (tagging a playlist's songs).
public sealed class SongChoice : Observable
{
    public SongChoice(TrackViewModel t) => Track = t;

    public TrackViewModel Track { get; }

    private bool _isSelected = true;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

// Words of a search: plain ones look everywhere (tag names included), "#word" only in the tags.
public sealed class TrackQuery
{
    private readonly string[] _words;
    private readonly string[] _tags;

    public TrackQuery(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _tags = parts.Where(p => p.StartsWith('#') && p.Length > 1).Select(p => Text.Normalize(p[1..])).ToArray();
        _words = parts.Where(p => !p.StartsWith('#')).SelectMany(p => Text.Normalize(p).Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
    }

    public bool IsEmpty => _words.Length == 0 && _tags.Length == 0;
    public string FirstWord => _words.FirstOrDefault() ?? "";

    public bool Matches(TrackViewModel t)
        => _words.All(w => t.SearchText.Contains(w)) && _tags.All(q => t.Tags.Any(tag => tag.SearchName.StartsWith(q)));

    public bool Matches(PlaylistViewModel p)
    {
        var name = Text.Normalize(p.Name);
        var tags = p.Tags;
        return _words.All(w => name.Contains(w) || tags.Any(t => t.SearchName.Contains(w))) &&
               _tags.All(q => tags.Any(t => t.SearchName.StartsWith(q)));
    }
}
