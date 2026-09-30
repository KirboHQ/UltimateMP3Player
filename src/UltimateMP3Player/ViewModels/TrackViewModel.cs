using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Media;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// One song, shared by every list so its state updates everywhere.
public sealed class TrackViewModel : Observable
{
    private readonly HashSet<string> _loading = new();

    public TrackViewModel(Track track, MainViewModel main)
    {
        T = track;
        Main = main;
    }

    public Track T { get; }
    public MainViewModel Main { get; }
    public string Id => T.Id;
    public string Title => T.Title;
    public string Artist => T.DisplayArtist;
    public string? Album => T.Album;
    public string AlbumText => T.Album ?? "";
    public string DurationText => Text.Duration(T.Duration);
    public string BpmText => T.Bpm is double b ? b.ToString("0.#", L.Culture) : "";
    public bool HasVideo => T.HasVideo;
    public byte[]? Wave => T.Wave;
    public string SearchText { get; private set; } = "";

    public bool IsFavorite => Main.Profile.Contains(Main.Profile.Favorites, T.Id);

    private bool _isCurrent;
    public bool IsCurrent { get => _isCurrent; set { if (Set(ref _isCurrent, value)) OnChanged(nameof(IsPlaying)); } }

    public bool IsPlaying => IsCurrent && Main.Player.IsPlaying;

    public ImageSource? Cover48 => Cover(96);
    public ImageSource? Cover160 => Cover(256);
    public ImageSource? Cover300 => Cover(600);

    private ImageSource? Cover(int size, [CallerMemberName] string? prop = null)
    {
        if (!T.HasCover) return null;
        var path = AppPaths.TrackCover(T.Id);
        var img = Images.TryGet(path, size, T.CoverVersion);
        if (img != null) return img;
        if (_loading.Add(prop!)) _ = LoadCover(path, size, T.CoverVersion, prop!);
        return null;
    }

    private async Task LoadCover(string path, int size, int version, string prop)
    {
        var img = await Images.LoadAsync(path, size, version);
        _loading.Remove(prop);
        if (img != null) OnChanged(prop);
    }

    // This profile's tags on the song, in the order of the tag list.
    private IReadOnlyList<string>? _tagIds;
    public IReadOnlyList<string> TagIds => _tagIds ??= Main.Profile.TagsOf(Id);
    public List<TagViewModel> Tags => Main.Tags.Where(t => TagIds.Contains(t.Id)).ToList();
    public string TagsText => string.Join(", ", Tags.Select(t => t.Name));
    public bool HasTags => TagIds.Count > 0;

    public void UpdateSearchText() => SearchText = Text.Normalize($"{T.Title} {T.Artist} {T.Album} {TagsText}");

    public void Refresh()
    {
        UpdateSearchText();
        OnChanged(nameof(Title), nameof(Artist), nameof(Album), nameof(AlbumText), nameof(DurationText), nameof(HasVideo), nameof(Wave),
            nameof(Cover48), nameof(Cover160), nameof(Cover300));
    }

    public void RefreshTags()
    {
        _tagIds = null;
        UpdateSearchText();
        OnChanged(nameof(Tags), nameof(TagsText), nameof(HasTags));
    }

    public void RefreshFavorite() => OnChanged(nameof(IsFavorite));
    public void RefreshPlaying() => OnChanged(nameof(IsPlaying));
}

// A list to play from: library, playlist, search...
public interface ITrackList
{
    string ContextId { get; }
    string ContextName { get; }
    IReadOnlyList<TrackViewModel> PlayOrder { get; }
    Playlist? Playlist { get; }
}

public sealed class TrackRow
{
    public TrackRow(int number, TrackViewModel track, ITrackList owner)
    {
        Number = number;
        Track = track;
        Owner = owner;
    }

    public int Number { get; }
    public TrackViewModel Track { get; }
    public ITrackList Owner { get; }

    public ICommand PlayCommand => new RelayCommand(() => Track.Main.Player.PlayFrom(Owner, Track));
    // The row's play button: on the song already playing it pauses and resumes it instead of starting it over.
    // In a room it's a "+": the song goes into the room's queue.
    public ICommand PlayPauseCommand => new RelayCommand(() =>
    {
        if (Track.Main.InRoom) Track.Main.Together.Add(new[] { Track });
        else if (Track.IsCurrent) Track.Main.Player.PlayPause();
        else Track.Main.Player.PlayFrom(Owner, Track);
    });
    public ICommand FavoriteCommand => new RelayCommand(() => Track.Main.ToggleFavorite(Track));

    public override string ToString() => $"{Track.Title} – {Track.Artist}";
}

public sealed class PlaylistViewModel : Observable
{
    private readonly MainViewModel _main;

    public PlaylistViewModel(Playlist p, MainViewModel main)
    {
        P = p;
        _main = main;
    }

    public Playlist P { get; }
    public string Id => P.Id;
    public string Name => DisplayName(P);
    public bool IsFavorites => P.IsFavorites;

    // Favorites are stored as "Preferiti" but shown in the app language.
    public static string DisplayName(Playlist p) => p.IsFavorites ? L.T("Preferiti") : p.Name;

    public int Count => P.Tracks.Count(id => _main.Library.Get(id) != null);
    public string CountText => L.Count(Count, "1 brano", "{0} brani");
    public string Subtitle => "Playlist · " + CountText;

    public string TotalText
    {
        get
        {
            var secs = P.Tracks.Select(id => _main.Library.Get(id)?.Duration ?? 0).Sum();
            var t = TimeSpan.FromSeconds(secs);
            var dur = t.TotalHours >= 1 ? L.F("{0} h {1} min", (int)t.TotalHours, t.Minutes) : L.F("{0} min {1} s", t.Minutes, t.Seconds);
            return Count == 0 ? L.T("Nessun brano") : $"{CountText} · {dur}";
        }
    }

    public bool HasCustomCover => P.HasCover;
    public ImageSource? CustomCover => P.HasCover ? Images.TryGet(_main.Profile.PlaylistCover(P), 400, P.CoverVersion) ?? Load() : null;

    private ImageSource? Load()
    {
        _ = LoadAsync();
        return null;
    }

    private async Task LoadAsync()
    {
        var img = await Images.LoadAsync(_main.Profile.PlaylistCover(P), 400, P.CoverVersion);
        if (img != null) OnChanged(nameof(CustomCover));
    }

    // One cover, or a 2×2 mosaic with four.
    public List<TrackViewModel> CoverTracks =>
        P.Tracks.Select(id => _main.Library.Get(id)).Where(t => t is { HasCover: true }).Take(4).Select(t => _main.Vm(t!)).ToList();

    public bool ShowMosaic => !P.HasCover && !IsFavorites && CoverTracks.Count >= 4;
    public TrackViewModel? FirstCover => !P.HasCover && !IsFavorites ? CoverTracks.FirstOrDefault() : null;
    public bool ShowPlaceholder => !P.HasCover && !IsFavorites && CoverTracks.Count == 0;

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    private bool _isPlayingFrom;
    public bool IsPlayingFrom { get => _isPlayingFrom; set => Set(ref _isPlayingFrom, value); }

    // Tags of the playlist itself (not of its songs).
    public List<TagViewModel> Tags => _main.Tags.Where(t => P.Tags.Contains(t.Id)).ToList();
    public bool HasTags => Tags.Count > 0;

    public void Refresh() => OnChanged(nameof(Name), nameof(Count), nameof(CountText), nameof(Subtitle), nameof(TotalText),
        nameof(HasCustomCover), nameof(CustomCover), nameof(CoverTracks), nameof(ShowMosaic), nameof(FirstCover), nameof(ShowPlaceholder),
        nameof(Tags), nameof(HasTags));

    public override string ToString() => Name;
}
