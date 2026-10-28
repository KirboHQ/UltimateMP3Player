using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using UltimateMP3Player.Audio;
using UltimateMP3Player.Core;
using UltimateMP3Player.Core.Together;
using UltimateMP3Player.Services;

namespace UltimateMP3Player.ViewModels;

public sealed class EqBandViewModel : Observable
{
    private readonly EqualizerViewModel _owner;

    public EqBandViewModel(int index, EqualizerViewModel owner)
    {
        Index = index;
        _owner = owner;
    }

    public int Index { get; }
    public string Label => Equalizer.BandLabel(Index);

    public double Gain
    {
        get => _owner.Gains[Index];
        set
        {
            if (!_owner.Enabled) return;
            value = Math.Round(Math.Clamp(value, -Equalizer.MaxGain, Equalizer.MaxGain) * 2) / 2;
            if (Math.Abs(_owner.Gains[Index] - value) < 0.01) return;
            _owner.Gains[Index] = value;
            OnChanged(nameof(Gain), nameof(GainText));
            _owner.OnBandChanged();
        }
    }

    public string GainText => Gain == 0 ? "0" : Gain.ToString("+0.#;-0.#", L.Culture);

    public void Refresh() => OnChanged(nameof(Gain), nameof(GainText));
}

public sealed class EqualizerViewModel : Observable
{
    private readonly MainViewModel _main;
    private bool _applying;
    private const string Custom = "Custom";

    public EqualizerViewModel(MainViewModel main)
    {
        _main = main;
        Bands = Enumerable.Range(0, Equalizer.BandCount).Select(i => new EqBandViewModel(i, this)).ToList();
        SaveCommand = new RelayCommand(() => _ = SavePreset(), () => Enabled);
        DeleteCommand = new RelayCommand(() => _ = DeletePreset(), () => Enabled && IsCustomPreset);
        ResetCommand = new RelayCommand(() => Apply(Equalizer.BuiltIn[0]), () => Enabled);
        BuildPresets();
    }

    private EqSettings Eq => _main.Profile.Data.Eq;
    public double[] Gains => Eq.Gains;
    public List<EqBandViewModel> Bands { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ResetCommand { get; }

    // Bumped on every change, redraws the response curve.
    private int _curve;
    public int CurveVersion { get => _curve; private set => Set(ref _curve, value); }

    public bool Enabled
    {
        get => Eq.Enabled;
        set
        {
            Eq.Enabled = value;
            Commit();
            OnChanged();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public List<Choice> Presets { get; private set; } = new();

    private Choice? _preset;
    public Choice? Preset
    {
        get => _preset;
        set
        {
            if (value == null || !Set(ref _preset, value) || _applying) return;
            if (value.Value is EqPreset p) Apply(p);
            OnChanged(nameof(IsCustomPreset));
        }
    }

    public bool IsCustomPreset => _preset?.Value is EqPreset p && Eq.Custom.Contains(p);

    private void BuildPresets()
    {
        var list = Equalizer.BuiltIn.Select(p => new Choice(L.T(p.Name), p)).ToList();
        list.AddRange(Eq.Custom.Select(p => new Choice(p.Name, p, L.T("yours"))));
        var current = list.FirstOrDefault(c => ((EqPreset)c.Value!).Name == Eq.Preset && ((EqPreset)c.Value!).Gains.SequenceEqual(Eq.Gains));
        if (current == null) list.Add(current = new Choice(L.T(Custom), null));
        Presets = list;
        _applying = true;
        _preset = null;
        OnChanged(nameof(Preset));
        OnChanged(nameof(Presets));
        _preset = current;
        OnChanged(nameof(Preset));
        _applying = false;
        OnChanged(nameof(IsCustomPreset));
    }

    private void Apply(EqPreset p)
    {
        Array.Copy(p.Gains, Eq.Gains, Equalizer.BandCount);
        Eq.Preset = p.Name;
        foreach (var b in Bands) b.Refresh();
        Commit();
        BuildPresets();
    }

    internal void OnBandChanged()
    {
        Eq.Preset = Custom;
        Commit();
        if (_preset?.Value != null) BuildPresets();
    }

    private void Commit()
    {
        _main.Player.ApplyEqualizer();
        _main.Profile.Save();
        CurveVersion++;
    }

    private async Task SavePreset()
    {
        var name = await Dialogs.PromptAsync(L.T("Save equalizer preset"), L.T("Preset name"),
            Eq.Preset == Custom ? L.T("My preset") : L.T(Eq.Preset) + " " + L.T("(mine)"));
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (Equalizer.BuiltIn.Any(b => b.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase) ||
                                       L.T(b.Name).Equals(name, StringComparison.CurrentCultureIgnoreCase))) name += " " + L.T("(mine)");
        Eq.Custom.RemoveAll(c => c.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        Eq.Custom.Add(new EqPreset { Name = name, Gains = Eq.Gains.ToArray() });
        Eq.Preset = name;
        _main.Profile.Save();
        BuildPresets();
        _main.Toast(L.F("Preset “{0}” saved", name));
    }

    private async Task DeletePreset()
    {
        if (_preset?.Value is not EqPreset p || !Eq.Custom.Contains(p)) return;
        if (!await Dialogs.ConfirmAsync(L.T("Delete this preset?"), L.F("The preset “{0}” will be deleted.", p.Name), L.T("Delete"), true)) return;
        Eq.Custom.Remove(p);
        Eq.Preset = Custom;
        _main.Profile.Save();
        BuildPresets();
    }
}

public sealed class ThemeOption
{
    public ThemeOption(string id, string label, Brush swatch, bool selected)
    {
        Id = id;
        Label = label;
        Swatch = swatch;
        IsSelected = selected;
    }

    public string Id { get; }
    public string Label { get; }
    public Brush Swatch { get; }
    public bool IsSelected { get; }
}

// A site of the online search in Settings: on/off, dragged to another place (SettingsViewModel.MoveService).
public sealed class SearchServiceOption : Observable
{
    private readonly SettingsViewModel _owner;

    public SearchServiceOption(string name, int index, SettingsViewModel owner)
    {
        Name = name;
        Index = index;
        _owner = owner;
    }

    public string Name { get; }
    public int Index { get; }
    public string Number => (Index + 1).ToString();
    public Brush Brush => Ui.BrushFrom(Sites.ColorFor(Name));
    public string Description => L.T(Name switch
    {
        OnlineSearchServices.YouTubeMusic => "the official songs, with albums and covers",
        OnlineSearchServices.YouTube => "videos: covers, live, versions found nowhere else",
        OnlineSearchServices.SoundCloud => "remixes, mashups and independent artists",
        OnlineSearchServices.Spotify => "with your key; the audio comes from YouTube Music",
        _ => "a catalogue like Spotify's; the audio comes from YouTube Music",
    });
    public bool Enabled
    {
        get => _owner.IsServiceOn(Name);
        set
        {
            _owner.SetServiceOn(Name, value);
            OnChanged();
        }
    }
}

public sealed class SettingsViewModel : Observable
{
    private readonly MainViewModel _main;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        Equalizer = new EqualizerViewModel(main);
        // (the button next to the song turns it on and off too)
        main.Player.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PlayerViewModel.Normalize)) OnChanged(nameof(Normalize)); };
        AudioFormats = QualityChoices.Audio();
        VideoQualities = QualityChoices.Video();
        ParallelChoices = Enumerable.Range(1, 6).Select(n => new Choice(L.F("{0} at a time", n), n)).ToList();
        Languages = new List<Choice> { new("English", "en"), new("Italiano", "it") };
        BrowseCommand = new RelayCommand(() => _ = Browse());
        OpenMusicDirCommand = new RelayCommand(() => MainViewModel.OpenFolder(S.MusicDir));
        OpenDataDirCommand = new RelayCommand(() => MainViewModel.OpenFolder(AppPaths.DataDir));
        UpdateEnginesCommand = new RelayCommand(() => _ = UpdateEngines(), () => !_updating);
        CheckAppUpdateCommand = new RelayCommand(() => _ = _main.Host.CheckAppUpdateNow(), () => !Host.Updates.IsBusy);
        WhatsNewCommand = new RelayCommand(() => _ = OpenWhatsNew());
        SpotifyKeyCommand = new RelayCommand(() => _ = AskSpotifyKey());
        ManageProfilesCommand = new RelayCommand(() => _main.Host.SwitchProfile());
        ImportFilesCommand = new RelayCommand(() => _ = _main.ImportDialog(false));
        ImportFolderCommand = new RelayCommand(() => _ = _main.ImportDialog(true));
        ExportPackCommand = new RelayCommand(() => _main.ExportPack());
        ImportPackCommand = new RelayCommand(() => _ = _main.PickPack());
        SelectThemeCommand = new RelayCommand(p => { if (p is string id) SelectTheme(id); });
        RenameArtistCommand = new RelayCommand(() => _ = RenameArtist(), () => SelectedArtist != null && !string.IsNullOrWhiteSpace(NewArtistName));
        bool hasRepo = AppInfo.RepoUrl != null;
        ReportProblemCommand = new RelayCommand(() => MainViewModel.OpenUrl(AppInfo.NewIssueUrl(L.T("[Bug] "), BugTemplate())!), () => hasRepo);
        SuggestIdeaCommand = new RelayCommand(() => MainViewModel.OpenUrl(AppInfo.NewIssueUrl(L.T("[Idea] "), IdeaTemplate())!), () => hasRepo);
        OpenIssuesCommand = new RelayCommand(() => MainViewModel.OpenUrl(AppInfo.RepoUrl + "/issues"), () => hasRepo);
        OpenRepoCommand = new RelayCommand(() => MainViewModel.OpenUrl(AppInfo.RepoUrl!), () => hasRepo);
        BuildBrowsers();
        RefreshStartupChoices();
        RefreshOutputDevices();
        _ = RefreshEngineInfo();
    }

    private AppSettings S => _main.Host.Settings;
    public AppHost Host => _main.Host;

    public EqualizerViewModel Equalizer { get; }
    public BackupsViewModel Backups => _main.Backups;
    public ScrobblingViewModel Scrobbling => _main.Scrobbling;
    public ICommand BrowseCommand { get; }
    public ICommand OpenMusicDirCommand { get; }
    public ICommand OpenDataDirCommand { get; }
    public ICommand UpdateEnginesCommand { get; }
    public ICommand CheckAppUpdateCommand { get; }
    public ICommand WhatsNewCommand { get; }
    public ICommand SpotifyKeyCommand { get; }
    public ICommand ManageProfilesCommand { get; }
    public ICommand ImportFilesCommand { get; }
    public ICommand ImportFolderCommand { get; }
    public ICommand ExportPackCommand { get; }
    // Connect phone (the computer apps) and Connect to computer (the phones).
    public PhoneLinkViewModel PhoneLink => _main.PhoneLink;
    public ComputerLinkViewModel ComputerLink => _main.ComputerLink;
    public ICommand ConnectPhoneCommand => new RelayCommand(() => PhoneLinkViewModel.ShowWindow?.Invoke(_main));
    public ICommand ImportPackCommand { get; }
    public ICommand SelectThemeCommand { get; }
    public ICommand RenameArtistCommand { get; }
    public ICommand ReportProblemCommand { get; }
    public ICommand SuggestIdeaCommand { get; }
    public ICommand OpenIssuesCommand { get; }
    public ICommand OpenRepoCommand { get; }

    private void Save() => S.Save();

    // ------------------------------------------------------------------ appearance

    public List<ThemeOption> Themes
    {
        get
        {
            var current = ThemeColors.Resolve(_main.Profile.Data.Theme, _main.Profile.Info.Color).Id;
            return ThemeColors.All.Select(t => new ThemeOption(t.Id, L.T(t.Name), UltimateMP3Player.Themes.Swatch(t), current == t.Id)).ToList();
        }
    }

    private void SelectTheme(string id)
    {
        _main.Profile.Data.Theme = id;
        _main.Profile.Save();
        _main.Host.ApplyTheme();
        OnChanged(nameof(Themes));
    }

    public List<Choice> Appearances { get; } = new()
    {
        new(L.T("Same as the system"), ThemeColors.System), new(L.T("Light"), ThemeColors.Light), new(L.T("Dark"), ThemeColors.Dark),
    };

    public Choice Appearance
    {
        get => Appearances.FirstOrDefault(c => Equals(c.Value, _main.Profile.Data.Appearance)) ?? Appearances[0];
        set
        {
            if (value?.Value is not string mode || mode == _main.Profile.Data.Appearance) return;
            _main.Profile.Data.Appearance = mode;
            _main.Profile.Save();
            _main.Host.ApplyTheme();
            // (the swatches show the light or the dark colours)
            OnChanged(nameof(Appearance), nameof(Themes));
        }
    }

    public List<Choice> Languages { get; }
    public Choice Language
    {
        get => Languages.FirstOrDefault(c => Equals(c.Value, S.Language)) ?? Languages[0];
        set
        {
            if (value?.Value is not string code || code == S.Language) return;
            // After the ComboBox is done: the whole window is rebuilt.
            Application.Current.Dispatcher.BeginInvoke(() => _main.Host.SetLanguage(code));
        }
    }

    public bool ReleaseNotifications
    {
        get => S.ReleaseNotifications;
        set { S.ReleaseNotifications = value; Save(); OnChanged(); }
    }

    public bool Animations
    {
        get => S.Animations;
        set { S.Animations = value; Ui.Animations = value; Save(); OnChanged(); }
    }

    public bool SmoothScrolling
    {
        get => S.SmoothScroll;
        set { S.SmoothScroll = value; SmoothScroll.Enabled = value; Save(); OnChanged(); }
    }

    public bool CoverTilt
    {
        get => S.CoverTilt;
        set { S.CoverTilt = value; Save(); OnChanged(); _main.NowPlaying.Refresh(); }
    }

    // ------------------------------------------------------------------ playback (profile)

    public bool Normalize
    {
        get => _main.Player.Normalize;
        set => _main.Player.SetNormalize(value);
    }

    public bool Crossfade
    {
        get => _main.Profile.Data.Crossfade;
        set
        {
            _main.Profile.Data.Crossfade = value;
            _main.Profile.Save();
            _main.Player.ApplyCrossfade();
            OnChanged();
        }
    }

    public double CrossfadeSeconds
    {
        get => _main.Profile.Data.CrossfadeSeconds;
        set
        {
            int s = (int)Math.Round(Math.Clamp(value, 1, 12));
            if (s == _main.Profile.Data.CrossfadeSeconds) return;
            _main.Profile.Data.CrossfadeSeconds = s;
            _main.Profile.Save();
            _main.Player.ApplyCrossfade();
            OnChanged(nameof(CrossfadeSeconds), nameof(CrossfadeText));
        }
    }

    public string CrossfadeText => $"{_main.Profile.Data.CrossfadeSeconds} s";

    // The transition at the next song's tempo (PlayerViewModel.ApplyBend).
    public bool BpmMatch
    {
        get => _main.Profile.Data.BpmMatch;
        set
        {
            _main.Profile.Data.BpmMatch = value;
            _main.Profile.Save();
            _main.Player.ApplyBend();
            OnChanged();
        }
    }

    public double BpmMatchSeconds
    {
        get => _main.Profile.Data.BpmMatchSeconds;
        set
        {
            int s = (int)Math.Round(Math.Clamp(value, 2, 30));
            if (s == _main.Profile.Data.BpmMatchSeconds) return;
            _main.Profile.Data.BpmMatchSeconds = s;
            _main.Profile.Save();
            _main.Player.ApplyBend();
            OnChanged(nameof(BpmMatchSeconds), nameof(BpmMatchText));
        }
    }

    public string BpmMatchText => $"{_main.Profile.Data.BpmMatchSeconds} s";

    // ------------------------------------------------------------------ output device (app: it belongs to this device)

    public List<Choice> OutputDevices { get; private set; } = new();

    // The outputs there now (read again every time the page opens); the chosen one even when it isn't connected, the
    // music goes to the default one until it's back.
    public void RefreshOutputDevices()
    {
        var list = new List<Choice> { new(L.T("System default"), null) };
        list.AddRange(AudioPlatform.OutputDevices().Select(d => new Choice(d.Name, d.Id)));
        if (S.OutputDevice is { } id && list.All(c => !Equals(c.Value, id)))
            list.Add(new Choice(S.OutputDeviceName ?? id, id, L.T("not connected")));
        OutputDevices = list;
        OnChanged(nameof(OutputDevices), nameof(OutputDevice));
    }

    public Choice OutputDevice
    {
        get => OutputDevices.FirstOrDefault(c => Equals(c.Value, S.OutputDevice)) ?? OutputDevices[0];
        set
        {
            if (value == null || Equals(value.Value, S.OutputDevice)) return;
            S.OutputDevice = value.Value as string;
            S.OutputDeviceName = S.OutputDevice == null ? null : value.Label;
            Save();
            _main.Host.Audio.OutputDevice = S.OutputDevice;
            OnChanged();
        }
    }

    // ------------------------------------------------------------------ online search (app)

    public bool OnlineSearch
    {
        get => S.OnlineSearch;
        set
        {
            S.OnlineSearch = value;
            Save();
            OnChanged();
            _main.Search.OnSettingsChanged();
        }
    }

    // The sites in the order they're asked, each on or off.
    public List<SearchServiceOption> SearchServices
        => OnlineSearchServices.Ordered(S.SearchServices, Array.Empty<string>())
            .Select((name, i) => new SearchServiceOption(name, i, this)).ToList();

    internal bool IsServiceOn(string name) => !S.SearchServicesOff.Contains(name, StringComparer.OrdinalIgnoreCase);

    internal void SetServiceOn(string name, bool on)
    {
        S.SearchServicesOff.RemoveAll(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (!on) S.SearchServicesOff.Add(name);
        Save();
        _main.Search.OnSettingsChanged();
    }

    // A site dragged to another place of the list (to: its new index).
    public void MoveService(string name, int to)
    {
        var order = OnlineSearchServices.Ordered(S.SearchServices, Array.Empty<string>());
        int i = order.IndexOf(name);
        if (i < 0 || to < 0 || to >= order.Count || to == i) return;
        order.RemoveAt(i);
        order.Insert(to, name);
        S.SearchServices = order;
        Save();
        OnChanged(nameof(SearchServices));
        _main.Search.OnSettingsChanged();
    }

    public List<Choice> SearchModes { get; } = new()
    {
        new(L.T("All at once"), true, L.T("more results")),
        new(L.T("One at a time"), false, L.T("the next one if the first finds nothing")),
    };

    public Choice SearchMode
    {
        get => SearchModes.First(c => (bool)c.Value! == S.SearchParallel);
        set
        {
            if (value == null) return;
            S.SearchParallel = (bool)value.Value!;
            Save();
            OnChanged(nameof(SearchModeHint));
            _main.Search.OnSettingsChanged();
        }
    }

    public string SearchModeHint => S.SearchParallel
        ? L.T("Searches every site that's on at the same time: the results come mixed, each site's best first.")
        : L.T("Searches the first site; if it finds nothing or doesn't answer it moves to the next, in the order above.");

    // Spotify only lets apps with a key search it: the user's own (a free app on developer.spotify.com).
    public string SpotifyKeyText => OnlineSearchServices.SpotifyKey != null ? L.T("Set: the search also looks on Spotify") : L.T("Not set");

    private async Task AskSpotifyKey()
    {
        // The guide: Spotify's own page names (Create app, Redirect URI, Web API...) stay as Spotify writes them.
        var r = await Dialogs.FormAsync(L.T("Spotify key"),
            L.T("Spotify only answers apps with a key: make yours once, it's free (Spotify wants your account to have Premium).\n\n1. Open developer.spotify.com (the button below) and sign in with your Spotify account.\n2. Dashboard › Create app: any name and description; as Redirect URI write http://127.0.0.1:8888/callback (it isn't used); tick Web API and save.\n3. In the app's Settings copy the Client ID, then press View client secret and copy that too.\n4. Paste them here. The key stays on this device; empty fields remove it.\n\nGood to know: Spotify's rules for developers don't allow using its search to get the songs from another service, as this app does, so Spotify could turn your key off."),
            (L.T("Open developer.spotify.com"), "https://developer.spotify.com/dashboard"),
            ("Client ID", S.SpotifyClientId ?? ""), ("Client secret", S.SpotifyClientSecret ?? ""));
        if (r == null) return;
        OnlineSearchServices.UseSpotifyKey(r[0], r[1]);
        S.SpotifyClientId = OnlineSearchServices.SpotifyKey?.Id;
        S.SpotifyClientSecret = OnlineSearchServices.SpotifyKey?.Secret;
        Save();
        OnChanged(nameof(SpotifyKeyText), nameof(SearchServices));
        _main.Search.OnSettingsChanged();
        if (OnlineSearchServices.SpotifyKey == null) return;
        try
        {
            await Task.Run(() => OnlineSearchServices.SearchAsync(OnlineSearchServices.Spotify, "music", 1, CancellationToken.None));
            _main.Toast(L.T("The Spotify key works: the search now finds Spotify's songs and artists too"));
        }
        catch (Exception ex)
        {
            _main.Toast(OnlineSearchServices.SpotifyRefused(ex)
                ? L.T("Spotify refused the key: check the Client ID, the secret and that your Spotify account has Premium")
                : L.T("Spotify didn't answer: the key is saved, the search will try it again"));
        }
    }

    // ------------------------------------------------------------------ lyrics (app)

    public Services.LyricsService Lyrics => _main.Host.Lyrics;

    public bool AutoLyrics
    {
        get => S.AutoLyrics;
        set
        {
            S.AutoLyrics = value;
            Save();
            OnChanged();
            if (value && _main.Player.Current is { } c) Lyrics.Auto(c.T, true);
        }
    }

    // How the library stands: with lyrics (with times), without, never searched.
    public string LyricsStats
    {
        get
        {
            var all = _main.Library.Snapshot();
            int synced = all.Count(t => t.Lyrics == LyricsKind.Synced), plain = all.Count(t => t.Lyrics == LyricsKind.Plain);
            int missing = Lyrics.Missing().Count;
            return L.F("{0} with lyrics ({1} synced) · {2} to look for", synced + plain, synced, missing);
        }
    }

    public ICommand ScanLyricsCommand => new RelayCommand(() =>
    {
        Lyrics.ScanLibrary();
        OnChanged(nameof(LyricsStats));
    }, () => !Lyrics.IsScanning);

    public void RefreshLyricsStats() => OnChanged(nameof(LyricsStats));

    // ------------------------------------------------------------------ downloads (app)

    public string MusicDir
    {
        get => S.MusicDir;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            S.MusicDir = value;
            Save();
            OnChanged();
        }
    }

    private async Task Browse()
    {
        if (await Dialogs.PickFolderAsync(L.T("Where to save downloaded music"), S.MusicDir) is { } dir) MusicDir = dir;
    }

    public List<Choice> AudioFormats { get; }
    public Choice AudioFormat
    {
        get => AudioFormats.FirstOrDefault(c => Equals(c.Value, S.AudioFormat)) ?? AudioFormats[0];
        set
        {
            if (value == null) return;
            S.AudioFormat = (string)value.Value!;
            Save();
        }
    }

    public bool PlayWhileDownloading
    {
        get => S.PlayWhileDownloading;
        set { S.PlayWhileDownloading = value; Save(); OnChanged(); }
    }

    public bool PreferAudioOnly
    {
        get => S.PreferAudioOnly;
        set { S.PreferAudioOnly = value; Save(); OnChanged(); }
    }

    // Where the download card's "save the audio" starts: off, songs only go into the library (in the cloud).
    public bool SaveAudio
    {
        get => S.SaveAudio;
        set { S.SaveAudio = value; Save(); OnChanged(); }
    }

    public List<Choice> VideoQualities { get; }
    public Choice VideoQuality
    {
        get => VideoQualities.FirstOrDefault(c => Equals(c.Value, S.VideoMaxRes)) ?? QualityChoices.Pick(VideoQualities, 1080);
        set
        {
            if (value == null) return;
            S.VideoMaxRes = value.Value as int?;
            Save();
        }
    }

    public List<Choice> ParallelChoices { get; }
    public Choice Parallel
    {
        get => ParallelChoices[Math.Clamp(S.MaxParallel, 1, 6) - 1];
        set
        {
            if (value == null) return;
            S.MaxParallel = (int)value.Value!;
            Save();
            _main.Host.Downloads.Pump();
        }
    }

    public bool UseCookies
    {
        get => S.UseCookies;
        set { S.UseCookies = value; Save(); OnChanged(); }
    }

    public List<Choice> BrowserChoices { get; private set; } = new();
    public Choice? Browser
    {
        get => BrowserChoices.FirstOrDefault(c => Equals(c.Value, S.CookiesBrowser)) ?? BrowserChoices.FirstOrDefault();
        set
        {
            if (value == null) return;
            S.CookiesBrowser = (string)value.Value!;
            Save();
        }
    }

    // Only the browsers installed on this PC (yt-dlp can read their cookies).
    private void BuildBrowsers()
    {
        var list = BrowserDetector.Find().Select(b => new Choice(b.Name, b.Spec, L.T(b.Hint))).ToList();
        if (list.Count == 0) list.Add(new Choice("Firefox", "firefox", L.T("recommended")));
        if (list.All(c => !Equals(c.Value, S.CookiesBrowser)))
        {
            var fallback = list.FirstOrDefault(c => ((string)c.Value!).StartsWith("firefox")) ?? list[0];
            S.CookiesBrowser = (string)fallback.Value!;
            Save();
        }
        BrowserChoices = list;
    }

    // ------------------------------------------------------------------ bulk edit

    private string _artistFilter = "";
    public string ArtistFilter { get => _artistFilter; set { if (Set(ref _artistFilter, value)) OnChanged(nameof(Artists)); } }

    public List<Choice> Artists
    {
        get
        {
            var words = Text.Normalize(_artistFilter).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return _main.Library.Snapshot()
                .Where(t => !string.IsNullOrWhiteSpace(t.Artist))
                .GroupBy(t => t.Artist!.Trim(), StringComparer.CurrentCultureIgnoreCase)
                .Where(g => words.All(w => Text.Normalize(g.Key).Contains(w)))
                .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => new Choice(g.Key, g.Key, L.Count(g.Count(), "1 song", "{0} songs")))
                .ToList();
        }
    }

    private Choice? _selectedArtist;
    public Choice? SelectedArtist
    {
        get => _selectedArtist;
        set
        {
            if (!Set(ref _selectedArtist, value)) return;
            if (value != null) NewArtistName = value.Label;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _newArtistName = "";
    public string NewArtistName { get => _newArtistName; set { if (Set(ref _newArtistName, value)) CommandManager.InvalidateRequerySuggested(); } }

    private async Task RenameArtist()
    {
        if (SelectedArtist?.Value is not string from || string.IsNullOrWhiteSpace(NewArtistName)) return;
        var to = NewArtistName.Trim();
        if (to == from) return;
        if (!await Dialogs.ConfirmAsync(L.T("Rename artist?"), L.F("“{0}” will become “{1}” on {2}.", from, to, SelectedArtist.Hint), L.T("Rename"), false)) return;
        _main.RenameArtist(from, to);
        _selectedArtist = null;
        NewArtistName = "";
        OnChanged(nameof(Artists), nameof(SelectedArtist));
    }

    // ------------------------------------------------------------------ app

    public bool CloseToTray
    {
        get => S.CloseToTray;
        set { S.CloseToTray = value; Save(); OnChanged(); }
    }

    public List<Choice> StartupChoices { get; private set; } = new();

    // One list instance, so the ComboBox finds the selected item.
    private void RefreshStartupChoices()
    {
        var list = new List<Choice> { new(L.T("Ask which profile to use"), null) };
        list.AddRange(_main.Host.Profiles.Profiles.Select(p => new Choice(L.F("Always “{0}”", p.Name), p.Id)));
        StartupChoices = list;
        OnChanged(nameof(StartupChoices), nameof(StartupProfile));
    }

    public Choice StartupProfile
    {
        get => StartupChoices.FirstOrDefault(c => Equals(c.Value, S.StartupProfile)) ?? StartupChoices[0];
        set
        {
            if (value == null) return;
            S.StartupProfile = value.Value as string;
            Save();
        }
    }

    public bool DiscordPresence
    {
        get => S.DiscordPresence;
        set { S.DiscordPresence = value; Save(); _main.Host.SetDiscordPresence(value); OnChanged(); }
    }

    public bool DiscordAvailable => _main.Host.Presence.Available;

    public bool AutoUpdateApp
    {
        get => S.AutoUpdateApp;
        set { S.AutoUpdateApp = value; Save(); OnChanged(); }
    }

    // The notes of this version, shown at the first start after the update.
    public bool ShowWhatsNew
    {
        get => S.ShowWhatsNew;
        set { S.ShowWhatsNew = value; Save(); OnChanged(); }
    }

    // This version's notes again (the dialog's "Don't show again" can change ShowWhatsNew).
    private async Task OpenWhatsNew()
    {
        var notes = await ReleaseNotes.ForAsync(AppInfo.GitHubRepo, AppInfo.Version);
        if (notes.Count == 0)
        {
            _main.Toast(L.T("Couldn't get this version's notes: check your connection."));
            return;
        }
        await Dialogs.WhatsNewAsync(notes, S);
        OnChanged(nameof(ShowWhatsNew));
    }

    public bool AutoUpdateEngines
    {
        get => S.AutoUpdateEngines;
        set { S.AutoUpdateEngines = value; Save(); OnChanged(); }
    }

    private string _engineInfo = "";
    public string EngineInfo { get => _engineInfo; private set => Set(ref _engineInfo, value); }

    private bool _updating;

    private async Task RefreshEngineInfo()
    {
        var yt = await Task.Run(() => Engines.VersionAsync(Engines.YtDlp));
        var last = S.LastEngineUpdate is DateTime d ? " · " + L.F("last checked {0}", d.ToString("g", L.Culture)) : "";
        EngineInfo = $"yt-dlp {yt}{last}";
    }

    private async Task UpdateEngines()
    {
        _updating = true;
        EngineInfo = L.T("Updating…");
        CommandManager.InvalidateRequerySuggested();
        var report = await _main.Host.UpdateEnginesAsync(force: true);
        await RefreshEngineInfo();
        if (report != null) _main.Toast(report.Replace("\r", "").Replace("\n", " · "));
        _updating = false;
        CommandManager.InvalidateRequerySuggested();
    }

    public string Version => L.F("Version {0}", AppInfo.VersionText);
    public string DataDir => AppPaths.DataDir;

    // ------------------------------------------------------------------ help (GitHub issues)

    // Pre-filled issue text: the questions to answer, then the app and Windows versions.
    private static string BugTemplate() => string.Join("\n",
        "### " + L.T("What happened?"), "", "",
        "### " + L.T("What did you expect to happen?"), "", "",
        "### " + L.T("How to make it happen again"), "1. ", "2. ", "",
        "_" + L.T("If an error showed up, attach the errors.log file (Settings → Updates → Open the data folder).") + "_",
        "", "---", SystemLine());

    private static string IdeaTemplate() => string.Join("\n",
        "### " + L.T("Your idea"), "", "",
        "### " + L.T("What would you use it for?"), "", "",
        "---", SystemLine());

    private static string SystemLine()
        => $"Ultimate MP3 Player {AppInfo.VersionText} · {Ui.SystemName} · {(L.English ? "English" : "Italiano")}";

    // What the page searches as it opens (MainViewModel.SearchSettings), taken by the view.
    public string? StartSearch { get; set; }

    public void Refresh()
    {
        RefreshStartupChoices();
        RefreshOutputDevices();
        OnChanged(nameof(MusicDir), nameof(Themes), nameof(Appearance), nameof(Artists), nameof(Language), nameof(CacheText), nameof(LyricsStats), nameof(StatsSummary),
            nameof(TopTracks));
    }

    // ------------------------------------------------------------------ listen together

    public List<Choice> CacheChoices { get; } =
        new[] { 3, 5, 10, 15, 20, 30, 50, 100, 200 }.Select(n => new Choice(L.F("{0} songs", n), n)).ToList();

    public Choice CacheSize
    {
        get => CacheChoices.FirstOrDefault(c => (int)c.Value! == S.CacheSize) ?? CacheChoices[2];
        set
        {
            if (value == null) return;
            S.CacheSize = (int)value.Value!;
            Save();
        }
    }

    public string CacheText
    {
        get
        {
            var (count, bytes) = _main.Host.SongCache.Stats();
            return count == 0 ? L.T("It's empty now.") : L.F("Now: {0} · {1}.", L.Count(count, "1 song", "{0} songs"), Text.Size(bytes));
        }
    }

    public ICommand ClearCacheCommand => new RelayCommand(() =>
    {
        _main.Together.ClearCache();
        OnChanged(nameof(CacheText));
        _main.Toast(L.T("Song storage emptied (the ones in the queue stay)"));
    });

    public ICommand OpenCacheCommand => new RelayCommand(() => MainViewModel.OpenFolder(_main.Host.SongCache.Dir));

    // Songs of the queue got ready ahead, as a guest and as the host.
    public List<Choice> TogetherAheadChoices { get; } =
        Enumerable.Range(1, TogetherSession.MaxAhead).Select(n => new Choice(L.Count(n, "1 song", "{0} songs"), n)).ToList();

    private Choice AheadChoice(int n) => TogetherAheadChoices.FirstOrDefault(c => (int)c.Value! == n) ?? TogetherAheadChoices[TogetherSession.DefaultAhead - 1];

    public Choice TogetherAhead
    {
        get => AheadChoice(S.TogetherAhead);
        set
        {
            if (value == null) return;
            S.TogetherAhead = (int)value.Value!;
            OnTogetherPrefs();
        }
    }

    public Choice TogetherHostAhead
    {
        get => AheadChoice(S.TogetherHostAhead);
        set
        {
            if (value == null) return;
            S.TogetherHostAhead = (int)value.Value!;
            OnTogetherPrefs();
        }
    }

    // P2P at all: off, nothing goes between computers (the two choices below are greyed out).
    public bool TogetherP2P
    {
        get => S.TogetherP2P;
        set
        {
            S.TogetherP2P = value;
            OnTogetherPrefs();
            OnChanged();
        }
    }

    // P2P: two separate choices, it happens only when the host sends and the guest takes.
    public bool TogetherSendAsHost
    {
        get => S.TogetherSendAsHost;
        set
        {
            S.TogetherSendAsHost = value;
            OnTogetherPrefs();
            OnChanged();
        }
    }

    public bool TogetherTakeFromHost
    {
        get => S.TogetherTakeFromHost;
        set
        {
            S.TogetherTakeFromHost = value;
            OnTogetherPrefs();
            OnChanged();
        }
    }

    private void OnTogetherPrefs()
    {
        Save();
        _main.Together.OnPrefsChanged();
    }

    public ICommand FirewallCommand => _main.Together.FirewallCommand;

    // ------------------------------------------------------------------ suggested songs

    public bool RadioAfterSingle
    {
        get => S.RadioAfterSingle;
        set { S.RadioAfterSingle = value; Save(); OnChanged(); }
    }

    public List<Choice> RadioSourceChoices { get; } = new()
    {
        new(L.T("Automatic"), RadioSources.Auto, L.T("from the song's site")),
        new(L.T("A bit of everything"), RadioSources.Mix, L.T("taking turns")),
        new("YouTube Music", OnlineSearchServices.YouTubeMusic),
        new("SoundCloud", OnlineSearchServices.SoundCloud),
        new("Deezer", OnlineSearchServices.Deezer),
    };

    public Choice RadioSource
    {
        get => RadioSourceChoices.FirstOrDefault(c => Equals(c.Value, S.RadioSource)) ?? RadioSourceChoices[0];
        set
        {
            if (value == null) return;
            S.RadioSource = (string)value.Value!;
            Save();
            OnChanged(nameof(RadioSourceHint));
        }
    }

    public string RadioSourceHint => L.T(S.RadioSource switch
    {
        RadioSources.Mix => "YouTube Music, SoundCloud and Deezer taking turns: more variety.",
        OnlineSearchServices.YouTubeMusic => "YouTube Music's song radio: the most similar ones, with albums and covers.",
        OnlineSearchServices.SoundCloud => "The tracks SoundCloud relates to this one: remixes and independent artists.",
        OnlineSearchServices.Deezer => "Deezer's artist radio; the audio comes from YouTube Music.",
        _ => "SoundCloud songs from SoundCloud, all the others from YouTube Music's song radio. If a site finds nothing, the next one is tried.",
    });

    public Choice RadioAhead
    {
        get => AheadChoice(S.RadioAhead);
        set
        {
            if (value == null) return;
            S.RadioAhead = (int)value.Value!;
            Save();
            _main.Radio.Refresh();
        }
    }

    // ------------------------------------------------------------------ listening statistics

    public string StatsSummary
    {
        get
        {
            var stats = _main.Profile.StatsSnapshot().Where(kv => _main.Library.Get(kv.Key) != null).Select(kv => kv.Value).ToList();
            return stats.Count == 0 ? L.T("No plays counted yet.")
                : L.F("{0} listened · {1} · {2}", StatsViewModel.TimeText(stats.Sum(s => s.Seconds)),
                    L.Count(stats.Sum(s => s.Plays), "1 play", "{0} plays"), L.Count(stats.Count(s => s.Plays > 0), "1 song played", "{0} songs played"));
        }
    }

    // The three most played, for a taste of it.
    public List<TrackViewModel> TopTracks => _main.Profile.StatsSnapshot().Where(kv => kv.Value.Plays > 0).OrderByDescending(kv => kv.Value.Plays)
        .ThenByDescending(kv => kv.Value.Seconds).Select(kv => _main.Library.Get(kv.Key)).OfType<Track>().Take(3).Select(_main.Vm).ToList();

    public ICommand OpenStatsCommand => new RelayCommand(_main.OpenStats);
    public ICommand ImportSpotifyCommand => new RelayCommand(() => _ = SpotifyImport.RunAsync(_main));

    // A song is playing: the card follows it.
    public void OnListened(bool reorder)
    {
        OnChanged(nameof(StatsSummary));
        if (reorder) OnChanged(nameof(TopTracks));
    }

    public ICommand ShowTermsCommand => new RelayCommand(() => Dialogs.Terms(false));

    // ------------------------------------------------------------------ support the project

    // GitHub Sponsors (a GitHub account needed), Ko-fi for everyone else (card or PayPal); the same as .github/FUNDING.yml.
    private const string SponsorsUrl = "https://github.com/sponsors/KirboHQ", KofiUrl = "https://ko-fi.com/kirbohq";
    public ICommand SponsorCommand => new RelayCommand(() => MainViewModel.OpenUrl(SponsorsUrl));
    public ICommand KofiCommand => new RelayCommand(() => MainViewModel.OpenUrl(KofiUrl));

    // The people who support it: supporters.json in the repository (a list of names kept by hand), read from GitHub so it
    // changes without a new version. null: not read (offline), the line is hidden.
    private string? _supporters;
    private bool _supportersAsked;
    public string? Supporters
    {
        get
        {
            if (!_supportersAsked && AppInfo.GitHubRepo.Length > 0)
            {
                _supportersAsked = true;
                _ = LoadSupportersAsync();
            }
            return _supporters;
        }
    }

    private async Task LoadSupportersAsync()
    {
        try
        {
            var json = await Task.Run(() => Http.GetStringAsync($"https://raw.githubusercontent.com/{AppInfo.GitHubRepo}/main/supporters.json"));
            var names = System.Text.Json.JsonSerializer.Deserialize<List<string>>(json)?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList() ?? new();
            _supporters = names.Count > 0 ? string.Join(" · ", names) : L.T("No one yet: your name could be the first here.");
            OnChanged(nameof(Supporters));
        }
        catch { }
    }
}

// Browsers installed here whose cookies yt-dlp can read.
public static class BrowserDetector
{
    public sealed record Found(string Name, string Spec, string Hint);

    public static List<Found> Find()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string support = Path.Combine(home, "Library", "Application Support");
        var list = new List<Found>();
        void Gecko(string name, bool main, params string[] dirs)
        {
            if (dirs.Select(NewestProfile).FirstOrDefault(p => p != null) is not { } p) return;
            list.Add(new Found(name, main ? "firefox" : "firefox:" + p, "recommended"));
        }
        void Chromium(string name, string spec, string dir, string hint)
        {
            if (Directory.Exists(dir)) list.Add(new Found(name, spec, hint));
        }
        if (OperatingSystem.IsWindows())
        {
            Gecko("Firefox", true, Path.Combine(roaming, @"Mozilla\Firefox\Profiles"));
            Gecko("LibreWolf", false, Path.Combine(roaming, @"librewolf\Profiles"));
            Gecko("Zen Browser", false, Path.Combine(roaming, @"zen\Profiles"));
            Gecko("Waterfox", false, Path.Combine(roaming, @"Waterfox\Profiles"));
            Gecko("Floorp", false, Path.Combine(roaming, @"Floorp\Profiles"));
            Chromium("Chrome", "chrome", Path.Combine(local, @"Google\Chrome\User Data"), "può non funzionare");
            Chromium("Edge", "edge", Path.Combine(local, @"Microsoft\Edge\User Data"), "può non funzionare");
            Chromium("Brave", "brave", Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"), "può non funzionare");
            Chromium("Opera", "opera", Path.Combine(roaming, @"Opera Software\Opera Stable"), "");
            Chromium("Vivaldi", "vivaldi", Path.Combine(local, @"Vivaldi\User Data"), "");
            Chromium("Chromium", "chromium", Path.Combine(local, @"Chromium\User Data"), "");
            Chromium("Whale", "whale", Path.Combine(local, @"Naver\Naver Whale\User Data"), "");
        }
        else if (OperatingSystem.IsMacOS())
        {
            Gecko("Firefox", true, Path.Combine(support, "Firefox", "Profiles"));
            Gecko("LibreWolf", false, Path.Combine(support, "librewolf", "Profiles"));
            Gecko("Zen Browser", false, Path.Combine(support, "zen", "Profiles"));
            Gecko("Waterfox", false, Path.Combine(support, "Waterfox", "Profiles"));
            Gecko("Floorp", false, Path.Combine(support, "Floorp", "Profiles"));
            Chromium("Safari", "safari", Path.Combine(home, "Library", "Safari"), "may not work");
            Chromium("Chrome", "chrome", Path.Combine(support, "Google", "Chrome"), "may not work");
            Chromium("Edge", "edge", Path.Combine(support, "Microsoft Edge"), "may not work");
            Chromium("Brave", "brave", Path.Combine(support, "BraveSoftware", "Brave-Browser"), "may not work");
            Chromium("Opera", "opera", Path.Combine(support, "com.operasoftware.Opera"), "");
            Chromium("Vivaldi", "vivaldi", Path.Combine(support, "Vivaldi"), "");
            Chromium("Chromium", "chromium", Path.Combine(support, "Chromium"), "");
        }
        else
        {
            string config = Path.Combine(home, ".config");
            Gecko("Firefox", true, Path.Combine(home, ".mozilla", "firefox"), Path.Combine(home, "snap", "firefox", "common", ".mozilla", "firefox"),
                Path.Combine(home, ".var", "app", "org.mozilla.firefox", ".mozilla", "firefox"));
            Gecko("LibreWolf", false, Path.Combine(home, ".librewolf"), Path.Combine(home, ".var", "app", "io.gitlab.librewolf-community", ".librewolf"));
            Gecko("Zen Browser", false, Path.Combine(home, ".zen"), Path.Combine(home, ".var", "app", "app.zen_browser.zen", ".zen"));
            Gecko("Waterfox", false, Path.Combine(home, ".waterfox"));
            Gecko("Floorp", false, Path.Combine(home, ".floorp"));
            Chromium("Chrome", "chrome", Path.Combine(config, "google-chrome"), "may not work");
            Chromium("Edge", "edge", Path.Combine(config, "microsoft-edge"), "may not work");
            Chromium("Brave", "brave", Path.Combine(config, "BraveSoftware", "Brave-Browser"), "may not work");
            Chromium("Opera", "opera", Path.Combine(config, "opera"), "");
            Chromium("Vivaldi", "vivaldi", Path.Combine(config, "vivaldi"), "");
            Chromium("Chromium", "chromium", Path.Combine(config, "chromium"), "");
        }
        return list;
    }

    // The profile used last (its cookies file changed most recently).
    private static string? NewestProfile(string profilesDir)
    {
        try
        {
            if (!Directory.Exists(profilesDir)) return null;
            return Directory.EnumerateDirectories(profilesDir)
                .Select(d => new FileInfo(Path.Combine(d, "cookies.sqlite")))
                .Where(f => f.Exists)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.DirectoryName)
                .FirstOrDefault();
        }
        catch { return null; }
    }
}
