using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// An internet radio in a grid (saved, found, Home): its picture (or its initial on a colour), its name, what it plays; a tap
// plays it, the heart keeps it among your radios.
public sealed class StationViewModel : Observable
{
    private readonly StationsViewModel _owner;
    private bool _requested;
    private ImageSource? _picture;

    public StationViewModel(StationsViewModel owner, Station station)
    {
        _owner = owner;
        S = station;
        PlayCommand = new RelayCommand(() => owner.Play(this));
        SaveCommand = new RelayCommand(() => owner.Toggle(S));
    }

    public Station S { get; }
    public string Name => S.Name;
    public string Subtitle => S.Subtitle;
    public bool HasSubtitle => Subtitle.Length > 0;
    public string Quality => S.Quality;
    public string Initial => ArtistPictures.Initial(S.Name);
    public Brush Brush => ArtistPictures.BrushFor(S.Name);

    public ImageSource? Picture
    {
        get
        {
            if (!_requested && S.Favicon != null)
            {
                _requested = true;
                _ = LoadPictureAsync();
            }
            return _picture;
        }
    }

    public bool HasPicture => _picture != null;

    private async Task LoadPictureAsync()
    {
        _picture = await WebImages.LoadAsync(new[] { S.Favicon! }, 240);
        if (_picture != null) OnChanged(nameof(Picture), nameof(HasPicture));
    }

    public bool IsSaved => _owner.IsSaved(S.Id);
    public string SaveGlyph => IsSaved ? "\uEB52" : "\uEB51";
    public string SaveTip => IsSaved ? L.T("Remove from your radios") : L.T("Save to your radios");
    // The one playing now.
    public bool IsCurrent => _owner.IsCurrent(S.Id);

    public ICommand PlayCommand { get; }
    public ICommand SaveCommand { get; }

    public void Refresh() => OnChanged(nameof(IsSaved), nameof(SaveGlyph), nameof(SaveTip), nameof(IsCurrent));
}

// One of the lists over the stations found: near you (this device's country), the world's, a genre (a tag of radio-browser).
public sealed class RadioChip : Observable
{
    public RadioChip(string label, string? country, string? tag)
    {
        Label = label;
        Country = country;
        Tag = tag;
    }

    public string Label { get; }
    public string? Country { get; }
    public string? Tag { get; }
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

// Library › Radio: the saved internet radios, and radio-browser.info's (the most listened to near you, in the world, of a
// genre, or by name). A station plays like a song without an end (Station.ToTrack): next and previous go through the list it
// was chosen in, its picture becomes its cover, the song on air its artist line (LiveMp3Source). The heart keeps it; nothing
// of it goes into the statistics or the recently played.
public sealed class StationsViewModel : Observable
{
    private readonly MainViewModel _main;
    // Every station seen in this session, by its id: the queue's ids ("st-…") are found again through it.
    private readonly Dictionary<string, Station> _known = new();
    private readonly Dictionary<string, Track> _tracks = new();
    private CancellationTokenSource? _cts;
    private bool _loaded;
#if IOS_APP
    // (the iPhone decodes MP3 streams only: no ffmpeg)
    private const bool Mp3Only = true;
#else
    private const bool Mp3Only = false;
#endif

    public StationsViewModel(MainViewModel main)
    {
        _main = main;
        var region = RegionCode();
        Chips = new List<RadioChip>();
        if (region != null) Chips.Add(new RadioChip(L.T("Near you"), region, null));
        Chips.Add(new RadioChip(L.T("World"), null, null));
        foreach (var (label, tag) in new[] { ("Pop", "pop"), ("Rock", "rock"), ("Dance", "dance"), ("Hip hop", "hip"), ("Jazz", "jazz"),
                     (L.T("Classical"), "classical"), ("Lo-fi · Chill", "chill"), ("80s", "80s"), (L.T("News"), "news") })
            Chips.Add(new RadioChip(label, null, tag));
        Chips[0].IsSelected = true;
        SelectChipCommand = new RelayCommand(p => { if (p is RadioChip c) Select(c); });
        SearchCommand = new RelayCommand(() => _ = LoadAsync(null, Query));
        main.Profile.StationsChanged += () => Application.Current.Dispatcher.BeginInvoke(RefreshSaved);
        RefreshSaved();
    }

    // This device's country (two letters), for "Near you".
    private static string? RegionCode()
    {
        try
        {
            var r = new RegionInfo(CultureInfo.CurrentCulture.Name).TwoLetterISORegionName;
            return r.Length == 2 && char.IsLetter(r[0]) ? r : null;
        }
        catch { return null; }
    }

    public ObservableCollection<StationViewModel> Saved { get; } = new();
    public bool HasSaved => Saved.Count > 0;
    public string SavedText => L.Count(Saved.Count, "1 radio saved", "{0} radios saved");
    public List<RadioChip> Chips { get; }
    public ICommand SelectChipCommand { get; }
    public ICommand SearchCommand { get; }

    private string _query = "";
    public string Query { get => _query; set => Set(ref _query, value); }

    private List<StationViewModel> _found = new();
    public List<StationViewModel> Found { get => _found; private set { _found = value; OnChanged(nameof(Found), nameof(HasFound)); } }
    public bool HasFound => _found.Count > 0;

    private string _foundTitle = "";
    public string FoundTitle { get => _foundTitle; private set => Set(ref _foundTitle, value); }

    private bool _loading;
    public bool Loading { get => _loading; private set => Set(ref _loading, value); }

    private string? _error;
    public string? Error { get => _error; private set { _error = value; OnChanged(nameof(Error), nameof(HasError)); } }
    public bool HasError => _error != null;

    // The page opened: its first list comes now, not when the app starts.
    public void Opened()
    {
        RefreshPlaying();
        if (_loaded) return;
        _loaded = true;
        Select(Chips.First(c => c.IsSelected));
    }

    private void Select(RadioChip chip)
    {
        foreach (var c in Chips) c.IsSelected = c == chip;
        Query = "";
        _ = LoadAsync(chip, null);
    }

    private async Task LoadAsync(RadioChip? chip, string? name)
    {
        if (chip == null && string.IsNullOrWhiteSpace(name)) return;
        if (chip == null)
            foreach (var c in Chips) c.IsSelected = false;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Loading = true;
        Error = null;
        FoundTitle = chip == null ? L.F("Radios named “{0}”", name!.Trim())
            : chip.Country != null ? L.T("Most listened to near you")
            : chip.Tag != null ? L.F("{0}: the most listened to", chip.Label)
            : L.T("Most listened to in the world");
        try
        {
            var list = await Task.Run(() => RadioBrowser.SearchAsync(name, chip?.Country, chip?.Tag, Mp3Only, cts.Token));
            if (cts.IsCancellationRequested) return;
            Found = list.Select(Vm).ToList();
            if (list.Count == 0) Error = L.T("No radios found.");
        }
        catch (Exception) when (!cts.IsCancellationRequested)
        {
            Found = new();
            Error = L.T("The radio directory didn't answer: check your connection.");
        }
        finally
        {
            if (_cts == cts) Loading = false;
        }
    }

    private StationViewModel Vm(Station s)
    {
        _known[s.Id] = s;
        return new StationViewModel(this, s);
    }

    private void RefreshSaved()
    {
        Saved.Clear();
        foreach (var s in _main.Profile.StationsSnapshot()) Saved.Add(Vm(s));
        foreach (var vm in _found.Concat(_searched)) vm.Refresh();
        OnChanged(nameof(HasSaved), nameof(SavedText));
    }

    // The playing marks follow the player.
    public void RefreshPlaying()
    {
        foreach (var vm in Saved.Concat(_found).Concat(_searched)) vm.Refresh();
    }

    // The search's radios (SearchViewModel): yours whose name has the words, then radio-browser's ones of that name.
    private List<StationViewModel> _searched = new();

    public List<StationViewModel> SavedMatching(string words)
    {
        var tokens = Text.Normalize(words).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 0 ? new() : Saved.Where(s => tokens.All(Text.Normalize(s.Name + " " + s.Subtitle).Contains)).ToList();
    }

    public async Task<List<StationViewModel>> FindAsync(string name, CancellationToken ct)
    {
        var list = await Task.Run(() => RadioBrowser.SearchAsync(name, null, null, Mp3Only, ct), ct);
        ct.ThrowIfCancellationRequested();
        var mine = Saved.Select(s => s.S.Id).ToHashSet();
        return _searched = list.Where(s => !mine.Contains(s.Id)).Take(24).Select(Vm).ToList();
    }

    public bool IsSaved(string stationId) => _main.Profile.HasStation(stationId);
    public bool IsCurrent(string stationId) => _main.Player?.Current?.Id == Station.Prefix + stationId;

    public void Toggle(Station s)
    {
        if (IsSaved(s.Id))
        {
            _main.Profile.ForgetStation(s.Id);
            _main.Toast(L.F("“{0}” removed from your radios", s.Name));
        }
        else
        {
            _main.Profile.SaveStation(s);
            _main.Toast(L.F("“{0}” saved to your radios", s.Name));
        }
        if (_tracks.TryGetValue(Station.Prefix + s.Id, out var t)) _main.Vm(t).RefreshFavorite();
    }

    // The player's heart on a station.
    public void Toggle(string trackId)
    {
        if (Find(trackId) is { } s) Toggle(s);
    }

    // A station tapped: it plays, the list it's in (saved or found) after it.
    public void Play(StationViewModel station)
    {
        var list = Saved.Contains(station) ? Saved.ToList() : _found.Contains(station) ? _found : _searched.Contains(station) ? _searched
            : new List<StationViewModel> { station };
        int start = Math.Max(0, list.IndexOf(station));
        var ids = list.Select(s => TrackOf(s.S).Id).ToList();
        if (_main.Player.PlayStations(ids, start, L.T("Radio"))) RadioBrowser.Clicked(station.S.Id);
    }

    // ------------------------------------------------------------------ as the player knows them

    private Station? Find(string trackId)
    {
        if (!Station.Is(trackId)) return null;
        var id = trackId[Station.Prefix.Length..];
        return _known.GetValueOrDefault(id) ?? _main.Profile.StationsSnapshot().FirstOrDefault(s => s.Id == id)
            ?? (_main.Profile.Data.LastStation is { } last && last.Id == id ? last : null);
    }

    private Track TrackOf(Station s)
    {
        var id = Station.Prefix + s.Id;
        if (!_tracks.TryGetValue(id, out var t))
        {
            _tracks[id] = t = s.ToTrack();
            // (its picture, once fetched, is its cover: the player, the lock screen)
            t.HasCover = File.Exists(AppPaths.TrackCover(id));
        }
        return t;
    }

    // A station's song (null: not a station, or one not known any more).
    public Track? TrackOf(string trackId) => _tracks.GetValueOrDefault(trackId) ?? (Find(trackId) is { } s ? TrackOf(s) : null);

    public SongStream? StreamOf(Track t) => Station.Is(t.Id) ? new SongStream(t.SourceUrl!, "User-Agent: UltimateMP3Player/4.3\r\n") : null;

    public string? HomepageOf(Track t) => Find(t.Id)?.Homepage;

    // What a station plays (its artist line while no song on air is known).
    public string? SubtitleOf(Track t) => Find(t.Id)?.Subtitle;

    // A station starts: its logo becomes its square cover.
    public async Task StartedAsync(Track t)
    {
        RefreshPlaying();
        var station = Find(t.Id);
        if (station != null && _main.Profile.Data.LastStation?.Id != station.Id)
        {
            _main.Profile.Data.LastStation = station;
            _main.Profile.Save();
        }
        // Its cover made again once a session (a station may change its logo).
        if (station?.Favicon is not { } url || !_covered.Add(t.Id)) return;
        try
        {
            var bytes = await Http.GetBytesAsync(url);
            var own = ArtistPictures.ColorFor(station.Name);
            if (Images.TileJpeg(bytes, CoverSize, (w, h, px) => Tile(w, h, px, own)) is not { } jpeg) return;
            await File.WriteAllBytesAsync(AppPaths.TrackCover(t.Id), jpeg);
            t.HasCover = true;
            t.CoverVersion++;
            _main.Vm(t).Refresh();
            if (_main.Player.Current?.T == t) _main.Host.OnTrackChanged();
        }
        catch { }
    }

    private readonly HashSet<string> _covered = new();
    private const int CoverSize = 512;

    // A station's logo as a square cover (the full screen, the lock screen): a logo with a one-colour edge sits on that colour
    // (it seems to go on); a picture (edges of many colours) sits rounded on a shade from its top's colour to its bottom's;
    // one with see-through parts on the station's colour, or a dark or light one when that would hide it. Never blown up
    // past twice its size (a small one would blur); a big solid one fills the square.
    // px: BGRA, not premultiplied. Top, Bottom: ARGB; Side: the share of the square its longer side takes; Round: its corners.
    public static (uint Top, uint Bottom, double Side, bool Round) Tile(int w, int h, byte[] px, string own)
    {
        Edge top = default, bottom = default, sides = default;
        long edge = 0;
        void Take(int x, int y, ref Edge into)
        {
            int i = (y * w + x) * 4;
            edge++;
            if (px[i + 3] >= 200) into.Add(px, i);
        }
        for (int x = 0; x < w; x++)
        {
            Take(x, 0, ref top);
            Take(x, h - 1, ref bottom);
        }
        for (int y = 1; y < h - 1; y++)
        {
            Take(0, y, ref sides);
            Take(w - 1, y, ref sides);
        }
        double grow = Math.Min(1, 2.0 * Math.Max(w, h) / CoverSize);
        var all = top.Plus(bottom).Plus(sides);
        if (all.N > edge * 0.9)
        {
            if (all.Spread < 24) return (all.Color(1), all.Color(1), grow, false);
            return (top.Color(0.8), bottom.Color(0.8), Math.Max(0.3, grow), grow < 1);
        }

        // See-through: how light the logo itself is (a pixel every few, enough for an average).
        long lr = 0, lg = 0, lb = 0, n = 0;
        int step = 4 * Math.Max(1, w * h / 4096);
        for (int i = 0; i + 3 < px.Length; i += step)
            if (px[i + 3] >= 128)
            {
                lb += px[i];
                lg += px[i + 1];
                lr += px[i + 2];
                n++;
            }
        uint back = 0xFF000000 | Convert.ToUInt32(own.TrimStart('#'), 16);
        if (n > 0)
        {
            double logo = Luma(lr / n, lg / n, lb / n), tile = Luma((back >> 16) & 255, (back >> 8) & 255, back & 255);
            if (Math.Abs(logo - tile) < 0.3) back = logo > 0.5 ? 0xFF1C1C22 : 0xFFF2F2F5;
        }
        return (back, back, Math.Min(0.72, grow), false);
    }

    private static double Luma(double r, double g, double b) => (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;

    // Pixels of an edge: their average colour and how far they are from it (0: all one colour).
    private struct Edge
    {
        public long R, G, B, N, Squares;

        public void Add(byte[] px, int i)
        {
            B += px[i];
            G += px[i + 1];
            R += px[i + 2];
            Squares += px[i] * px[i] + px[i + 1] * px[i + 1] + px[i + 2] * px[i + 2];
            N++;
        }

        public readonly Edge Plus(Edge o) => new() { R = R + o.R, G = G + o.G, B = B + o.B, N = N + o.N, Squares = Squares + o.Squares };
        public readonly uint Color(double k) => N == 0 ? 0xFF000000 : 0xFF000000 | (uint)(R * k / N) << 16 | (uint)(G * k / N) << 8 | (uint)(B * k / N);
        public readonly double Spread => N == 0 ? 0 : Math.Sqrt(Math.Max(0, (Squares - ((double)R * R + (double)G * G + (double)B * B) / N) / N / 3));
    }
}
