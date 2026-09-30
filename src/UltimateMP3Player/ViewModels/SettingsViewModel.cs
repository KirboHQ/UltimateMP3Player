using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using UltimateMP3Player.Core;
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
    private const string Custom = "Personalizzato";

    public EqualizerViewModel(MainViewModel main)
    {
        _main = main;
        Bands = Enumerable.Range(0, Equalizer.BandCount).Select(i => new EqBandViewModel(i, this)).ToList();
        SaveCommand = new RelayCommand(SavePreset, () => Enabled);
        DeleteCommand = new RelayCommand(DeletePreset, () => Enabled && IsCustomPreset);
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
        list.AddRange(Eq.Custom.Select(p => new Choice(p.Name, p, L.T("tuo"))));
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

    private void SavePreset()
    {
        var name = Dialogs.Prompt(L.T("Salva preset dell'equalizzatore"), L.T("Nome del preset"),
            Eq.Preset == Custom ? L.T("Il mio preset") : L.T(Eq.Preset) + " " + L.T("(mio)"));
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (Equalizer.BuiltIn.Any(b => b.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase) ||
                                       L.T(b.Name).Equals(name, StringComparison.CurrentCultureIgnoreCase))) name += " " + L.T("(mio)");
        Eq.Custom.RemoveAll(c => c.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase));
        Eq.Custom.Add(new EqPreset { Name = name, Gains = Eq.Gains.ToArray() });
        Eq.Preset = name;
        _main.Profile.Save();
        BuildPresets();
        _main.Toast(L.F("Preset «{0}» salvato", name));
    }

    private void DeletePreset()
    {
        if (_preset?.Value is not EqPreset p || !Eq.Custom.Contains(p)) return;
        if (!Dialogs.Confirm(L.T("Eliminare il preset?"), L.F("Il preset «{0}» verrà eliminato.", p.Name), L.T("Elimina"), true)) return;
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

public sealed class SettingsViewModel : Observable
{
    private readonly MainViewModel _main;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        Equalizer = new EqualizerViewModel(main);
        AudioFormats = new List<Choice>
        {
            new(L.T("MP3 · 320 kbps"), "mp3", L.T("si apre ovunque")),
            new(L.T("Originale (M4A/Opus)"), "original", L.T("nessuna conversione")),
        };
        VideoQualities = new List<Choice>
        {
            new("720p", 720), new("1080p (Full HD)", 1080, L.T("consigliata")), new("1440p (2K)", 1440), new("2160p (4K)", 2160),
            new(L.T("Massima disponibile"), null),
        };
        ParallelChoices = Enumerable.Range(1, 6).Select(n => new Choice(L.F("{0} alla volta", n), n)).ToList();
        Languages = new List<Choice> { new("English", "en"), new("Italiano", "it") };
        BrowseCommand = new RelayCommand(Browse);
        OpenMusicDirCommand = new RelayCommand(() => MainViewModel.OpenFolder(S.MusicDir));
        OpenDataDirCommand = new RelayCommand(() => MainViewModel.OpenFolder(AppPaths.DataDir));
        UpdateEnginesCommand = new RelayCommand(() => _ = UpdateEngines(), () => !_updating);
        CheckAppUpdateCommand = new RelayCommand(() => _ = _main.Host.CheckAppUpdateNow(), () => !Host.Updates.IsBusy);
        ManageProfilesCommand = new RelayCommand(() => _main.Host.SwitchProfile());
        ImportFilesCommand = new RelayCommand(() => _main.ImportDialog(false));
        ImportFolderCommand = new RelayCommand(() => _main.ImportDialog(true));
        SelectThemeCommand = new RelayCommand(p => { if (p is string id) SelectTheme(id); });
        RenameArtistCommand = new RelayCommand(RenameArtist, () => SelectedArtist != null && !string.IsNullOrWhiteSpace(NewArtistName));
        bool hasRepo = AppInfo.RepoUrl != null;
        ReportProblemCommand = new RelayCommand(() => MainViewModel.OpenUrl(AppInfo.NewIssueUrl(L.T("[Problema] "), BugTemplate())!), () => hasRepo);
        SuggestIdeaCommand = new RelayCommand(() => MainViewModel.OpenUrl(AppInfo.NewIssueUrl(L.T("[Idea] "), IdeaTemplate())!), () => hasRepo);
        OpenIssuesCommand = new RelayCommand(() => MainViewModel.OpenUrl(AppInfo.RepoUrl + "/issues"), () => hasRepo);
        OpenRepoCommand = new RelayCommand(() => MainViewModel.OpenUrl(AppInfo.RepoUrl!), () => hasRepo);
        BuildBrowsers();
        RefreshStartupChoices();
        _ = RefreshEngineInfo();
    }

    private AppSettings S => _main.Host.Settings;
    public AppHost Host => _main.Host;

    public EqualizerViewModel Equalizer { get; }
    public ICommand BrowseCommand { get; }
    public ICommand OpenMusicDirCommand { get; }
    public ICommand OpenDataDirCommand { get; }
    public ICommand UpdateEnginesCommand { get; }
    public ICommand CheckAppUpdateCommand { get; }
    public ICommand ManageProfilesCommand { get; }
    public ICommand ImportFilesCommand { get; }
    public ICommand ImportFolderCommand { get; }
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
            var current = UltimateMP3Player.Themes.Resolve(_main.Profile.Data.Theme, _main.Profile.Info.Color).Id;
            return UltimateMP3Player.Themes.All.Select(t => new ThemeOption(t.Id, t.Label, t.Swatch, current == t.Id)).ToList();
        }
    }

    private void SelectTheme(string id)
    {
        _main.Profile.Data.Theme = id;
        _main.Profile.Save();
        _main.Host.ApplyTheme();
        OnChanged(nameof(Themes));
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
        get => _main.Profile.Data.Normalize;
        set
        {
            _main.Profile.Data.Normalize = value;
            _main.Profile.Save();
            _main.Player.ApplyNormalization();
            OnChanged();
        }
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

    private void Browse()
    {
        var dlg = new OpenFolderDialog { Title = L.T("Dove salvare la musica scaricata"), InitialDirectory = S.MusicDir };
        if (dlg.ShowDialog() == true) MusicDir = dlg.FolderName;
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

    public bool PreferAudioOnly
    {
        get => S.PreferAudioOnly;
        set { S.PreferAudioOnly = value; Save(); OnChanged(); }
    }

    public List<Choice> VideoQualities { get; }
    public Choice VideoQuality
    {
        get => VideoQualities.FirstOrDefault(c => Equals(c.Value, S.VideoMaxRes)) ?? VideoQualities[1];
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
        if (list.Count == 0) list.Add(new Choice("Firefox", "firefox", L.T("consigliato")));
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
                .Select(g => new Choice(g.Key, g.Key, L.Count(g.Count(), "1 brano", "{0} brani")))
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

    private void RenameArtist()
    {
        if (SelectedArtist?.Value is not string from || string.IsNullOrWhiteSpace(NewArtistName)) return;
        var to = NewArtistName.Trim();
        if (to == from) return;
        if (!Dialogs.Confirm(L.T("Rinominare l'artista?"), L.F("«{0}» diventerà «{1}» su {2}.", from, to, SelectedArtist.Hint), L.T("Rinomina"), false)) return;
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
        var list = new List<Choice> { new(L.T("Chiedi quale profilo usare"), null) };
        list.AddRange(_main.Host.Profiles.Profiles.Select(p => new Choice(L.F("Sempre «{0}»", p.Name), p.Id)));
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
        var last = S.LastEngineUpdate is DateTime d ? " · " + L.F("ultimo controllo {0}", d.ToString("g", L.Culture)) : "";
        EngineInfo = $"yt-dlp {yt}{last}";
    }

    private async Task UpdateEngines()
    {
        _updating = true;
        EngineInfo = L.T("Aggiornamento in corso…");
        CommandManager.InvalidateRequerySuggested();
        var report = await _main.Host.UpdateEnginesAsync(force: true);
        await RefreshEngineInfo();
        if (report != null) _main.Toast(report.Replace("\r", "").Replace("\n", " · "));
        _updating = false;
        CommandManager.InvalidateRequerySuggested();
    }

    public string Version => L.F("Versione {0}", AppInfo.VersionText);
    public string DataDir => AppPaths.DataDir;

    // ------------------------------------------------------------------ help (GitHub issues)

    // Pre-filled issue text: the questions to answer, then the app and Windows versions.
    private static string BugTemplate() => string.Join("\n",
        "### " + L.T("Cosa è successo?"), "", "",
        "### " + L.T("Cosa ti aspettavi che succedesse?"), "", "",
        "### " + L.T("Come farlo succedere di nuovo"), "1. ", "2. ", "",
        "_" + L.T("Se è comparso un errore, allega il file errori.log (Impostazioni → Aggiornamenti → Apri la cartella dei dati).") + "_",
        "", "---", SystemLine());

    private static string IdeaTemplate() => string.Join("\n",
        "### " + L.T("La tua idea"), "", "",
        "### " + L.T("A cosa ti servirebbe?"), "", "",
        "---", SystemLine());

    private static string SystemLine()
    {
        var os = Environment.OSVersion.Version;
        string windows = os.Major == 10 && os.Build >= 22000 ? "Windows 11" : os.Major == 10 ? "Windows 10" : "Windows " + os;
        return $"Ultimate MP3 Player {AppInfo.VersionText} · {windows} (build {os.Build}) · {(L.English ? "English" : "Italiano")}";
    }

    public void Refresh()
    {
        RefreshStartupChoices();
        OnChanged(nameof(MusicDir), nameof(Themes), nameof(Artists));
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
        var list = new List<Found>();
        void Gecko(string name, string dir, bool main)
        {
            var profiles = Path.Combine(roaming, dir);
            if (NewestProfile(profiles) is not { } p) return;
            list.Add(new Found(name, main ? "firefox" : "firefox:" + p, "consigliato"));
        }
        void Chromium(string name, string spec, string dir, string hint)
        {
            if (Directory.Exists(dir)) list.Add(new Found(name, spec, hint));
        }
        Gecko("Firefox", @"Mozilla\Firefox\Profiles", true);
        Gecko("LibreWolf", @"librewolf\Profiles", false);
        Gecko("Zen Browser", @"zen\Profiles", false);
        Gecko("Waterfox", @"Waterfox\Profiles", false);
        Gecko("Floorp", @"Floorp\Profiles", false);
        Chromium("Chrome", "chrome", Path.Combine(local, @"Google\Chrome\User Data"), "può non funzionare");
        Chromium("Edge", "edge", Path.Combine(local, @"Microsoft\Edge\User Data"), "può non funzionare");
        Chromium("Brave", "brave", Path.Combine(local, @"BraveSoftware\Brave-Browser\User Data"), "può non funzionare");
        Chromium("Opera", "opera", Path.Combine(roaming, @"Opera Software\Opera Stable"), "");
        Chromium("Vivaldi", "vivaldi", Path.Combine(local, @"Vivaldi\User Data"), "");
        Chromium("Chromium", "chromium", Path.Combine(local, @"Chromium\User Data"), "");
        Chromium("Whale", "whale", Path.Combine(local, @"Naver\Naver Whale\User Data"), "");
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
