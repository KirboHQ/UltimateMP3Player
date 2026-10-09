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
        WasSaved = track.IsSaved;
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
    public bool HasLyrics => T.HasLyrics;
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
        OnChanged(nameof(Title), nameof(Artist), nameof(Album), nameof(AlbumText), nameof(DurationText), nameof(HasVideo), nameof(HasLyrics), nameof(Wave),
            nameof(Cover48), nameof(Cover160), nameof(Cover300));
        RefreshCloud();
    }

    // ------------------------------------------------------------------ saved on the device, or in the cloud

    // Its audio is on the device (false: in the cloud, a suggested song, one heard from a link).
    public bool IsSaved => T.IsSaved;
    public bool IsCloud => !T.IsSaved;
    // Last known to the lists (MainViewModel rebuilds them when it changes).
    internal bool WasSaved;
    // Gone from its site and found nowhere else.
    public bool IsUnavailable => T.Unavailable;
    // A cloud on the rows of the songs not saved (a warning sign for the ones no longer available): only a sign, saving
    // is done with the download button (next to the heart, in the menus).
    public bool ShowCloud => IsCloud && !IsSaving;
    public string CloudGlyph => IsUnavailable ? "" : "";

    // Being saved on the device right now (its download in the queue).
    private DownloadJobViewModel? SaveJob => Main.Queue.SavingOf(Id);
    public bool IsSaving => SaveJob != null;
    public double SavePct => SaveJob?.Percent ?? 0;
    public bool SaveIndeterminate => SaveJob is { } j && (j.Indeterminate || !j.IsRunning);

    private string? SavingTip => SaveJob is { } job
        ? job.IsRunning ? L.F("Lo sto salvando sul dispositivo · {0:0}%", job.Percent) : L.T("In coda per essere salvato sul dispositivo")
        : null;

    // What the cloud sign means.
    public string CloudTip => SavingTip
        ?? (IsUnavailable ? L.T("Non è più disponibile online e non è salvato sul dispositivo.")
            : Main.Radio.Has(Id) ? L.T("Non è nella libreria: lo ascolti dal suo link.")
            : L.T("Nel cloud: non è salvato sul dispositivo, lo ascolti dal suo link."));

    // In the library (false: a suggested song, one heard from a link).
    public bool InLibrary => !Main.Radio.Has(Id);

    // The button next to the heart in the player: the library icon into the library (in the cloud, nothing downloaded) for a
    // song not in it, the download arrow for one of the library in the cloud ("+" stays "add to a playlist").
    public string SaveGlyph => InLibrary ? "" : "";
    public string SaveTip => SavingTip ?? (!InLibrary ? L.T("Aggiungi alla libreria (senza scaricarlo)")
        : IsUnavailable ? L.T("Riprova a salvarlo sul dispositivo") : L.T("Salva sul dispositivo"));

    public void RefreshCloud() => OnChanged(nameof(IsSaved), nameof(IsCloud), nameof(IsUnavailable), nameof(ShowCloud), nameof(CloudGlyph), nameof(CloudTip),
        nameof(SaveTip), nameof(SaveGlyph), nameof(InLibrary), nameof(IsSaving), nameof(SavePct), nameof(SaveIndeterminate));

    public void RefreshSaving() => OnChanged(nameof(IsSaving), nameof(SavePct), nameof(SaveIndeterminate), nameof(ShowCloud), nameof(CloudTip), nameof(SaveTip));

    public void RefreshTags()
    {
        _tagIds = null;
        UpdateSearchText();
        OnChanged(nameof(Tags), nameof(TagsText), nameof(HasTags));
    }

    public void RefreshFavorite() => OnChanged(nameof(IsFavorite));
    public void RefreshPlaying() => OnChanged(nameof(IsPlaying));

    // How much it was listened to on this profile (the statistics page).
    public TrackStats? Stats => Main.Profile.StatsOf(Id);
    public int Plays => Stats?.Plays ?? 0;
    public string PlaysText => Plays == 0 ? L.T("mai") : L.Count(Plays, "1 ascolto", "{0} ascolti");
    public string ListenedText => Stats?.Seconds is > 0 and var s ? StatsViewModel.TimeText(s) : "–";
    public string LastPlayedText => Stats?.Last is { } d ? StatsViewModel.AgoText(d) : "";

    public void RefreshStats() => OnChanged(nameof(Stats), nameof(Plays), nameof(PlaysText), nameof(ListenedText), nameof(LastPlayedText));
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
    // Its songs not saved on the device.
    public int CloudCount => P.Tracks.Count(id => _main.Library.Get(id) is { IsSaved: false });

    public string TotalText
    {
        get
        {
            var secs = P.Tracks.Select(id => _main.Library.Get(id)?.Duration ?? 0).Sum();
            var t = TimeSpan.FromSeconds(secs);
            var dur = t.TotalHours >= 1 ? L.F("{0} h {1} min", (int)t.TotalHours, t.Minutes) : L.F("{0} min {1} s", t.Minutes, t.Seconds);
            if (Count == 0) return L.T("Nessun brano");
            int cloud = CloudCount;
            return $"{CountText} · {dur}" + (cloud == 0 ? "" : cloud == Count ? " · " + L.T("tutti nel cloud") : " · " + L.F("{0} nel cloud", cloud));
        }
    }

    // The songs of the playlist, as they're shown.
    public List<TrackViewModel> Songs => P.Tracks.ToList().Select(id => _main.Library.Get(id)).OfType<Track>().Select(_main.Vm).ToList();

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
