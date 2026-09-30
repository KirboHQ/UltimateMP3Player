using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using UltimateMP3Player.Audio;
using UltimateMP3Player.Core;
using UltimateMP3Player.Services;
using UltimateMP3Player.ViewModels;
using UltimateMP3Player.Views;

namespace UltimateMP3Player;

// Lives for the whole run: settings, library, audio, downloads, tray.
public sealed class AppHost : Observable
{
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _updateTimer;
    private Task? _installTask;
    private bool _exiting;
    private DateTime _lastTimeline;
    private DateTime _hiddenSince = DateTime.MaxValue;

    public AppHost()
    {
        _ui = Application.Current.Dispatcher;
        Settings = AppSettings.Load();
        ApplyLanguage();
        Library = Library.Load();
        Profiles = ProfileList.Load();
        if (Profiles.Profiles.Count == 0)
        {
            var name = Environment.UserName;
            name = name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : L.T("Io");
            Profiles.Profiles.Add(new ProfileInfo { Name = name, Color = ProfileInfo.Palette[0] });
            Profiles.Save();
        }
        Ui.Animations = Settings.Animations;
        SmoothScroll.Enabled = Settings.SmoothScroll;

        Audio = new AudioEngine(_ui);
        Downloads = new DownloadQueue(this);
        Updates = new Updater();
        Presence = new DiscordPresence { Enabled = Settings.DiscordPresence };
        Lyrics = new LyricsService(this);
        Media = new MediaControls(_ui);
        Media.PlayPressed += () => Session?.SetPlaying(true);
        Media.PausePressed += () => Session?.SetPlaying(false);
        Media.NextPressed += () => Session?.Player.NextCommand.Execute(null);
        Media.PreviousPressed += () => Session?.Player.PreviousCommand.Execute(null);
        Media.SeekRequested += t => Session?.Player.Seek(t.TotalSeconds);
        Tray = new TrayIcon(ShowWindow,
            () => Session?.TogglePlay(),
            () => Session?.Player.NextCommand.Execute(null),
            () => Session?.Player.PreviousCommand.Execute(null),
            () => Exit());
        RestartToUpdateCommand = new RelayCommand(() => RestartToUpdate(false));

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _updateTimer.Tick += async (_, _) => await OnUpdateTick();
        _updateTimer.Start();
    }

    public AppSettings Settings { get; }
    public Library Library { get; }
    public ProfileList Profiles { get; }
    public AudioEngine Audio { get; }
    public DownloadQueue Downloads { get; }
    public Updater Updates { get; }
    public DiscordPresence Presence { get; }
    public LyricsService Lyrics { get; }
    public MediaControls Media { get; }
    public TrayIcon Tray { get; }
    public ICommand RestartToUpdateCommand { get; }

    private MainViewModel? _session;
    public MainViewModel? Session { get => _session; private set => Set(ref _session, value); }

    public MainWindow? Window { get; private set; }

    // ------------------------------------------------------------------ language and theme

    // Settings first, then the installer's choice; new users get English.
    private void ApplyLanguage()
    {
        if (Settings.Language == null)
        {
            string? chosen = null;
            try { chosen = Registry.CurrentUser.OpenSubKey(@"Software\Ultimate MP3 Player")?.GetValue("Language") as string; } catch { }
            Settings.Language = chosen is "it" or "en" ? chosen : Settings.IsNew ? "en" : "it";
            Settings.Save();
        }
        L.English = Settings.Language == "en";
    }

    public void SetLanguage(string code)
    {
        if (code == Settings.Language) return;
        // The window is rebuilt: a room can't come along.
        if (Session != null && !Session.ConfirmLeaveRoom(L.T("Cambiando lingua la finestra si ricrea e uscirai dalla stanza."), L.T("Esci e cambia lingua")))
        {
            Session.Settings.Refresh();
            return;
        }
        Settings.Language = code;
        Settings.Save();
        L.English = code == "en";
        Tray.Relabel();
        Reload();
    }

    public void ApplyTheme()
    {
        if (Session != null) Themes.Apply(Session.Profile.Data.Theme, Session.Profile.Info.Color);
    }

    // ------------------------------------------------------------------ profiles

    public ProfileInfo? ChooseStartupProfile(string? wanted)
    {
        var list = Profiles.Profiles;
        if (wanted != null && list.FirstOrDefault(p => p.Id == wanted) is { } w) return w;
        if (Settings.StartupProfile != null && list.FirstOrDefault(p => p.Id == Settings.StartupProfile) is { } fixedProfile) return fixedProfile;
        if (list.Count == 1) return list[0];
        return ProfilePickerWindow.Pick(this, null, true);
    }

    public void OpenProfile(ProfileInfo info)
    {
        if (Session != null)
        {
            Session.Player.SaveState();
            Session.Player.Pause();
            Session.Detach();
            Audio.Close();
            Session.Profile.Flush();
        }
        Session = new MainViewModel(this, Profile.Get(info));
        ApplyTheme();
        if (Window != null) Window.DataContext = Session;
        OnTrackChanged();
        OnPlaybackChanged();
    }

    public void SwitchProfile()
    {
        var picked = ProfilePickerWindow.Pick(this, Window, false);
        if (picked != null && picked.Id != Session?.Profile.Info.Id &&
            Session?.ConfirmLeaveRoom(L.T("Cambiando profilo uscirai dalla stanza."), L.T("Esci e cambia profilo")) == false) picked = null;
        if (picked != null && picked.Id != Session?.Profile.Info.Id) OpenProfile(picked);
        else if (Session != null && Window != null)
        {
            Window.DataContext = null;
            Window.DataContext = Session;
            ApplyTheme();
        }
        Session?.Settings.Refresh();
    }

    // Rebuilds window and texts (language change); music keeps playing.
    public void Reload()
    {
        if (Session == null) return;
        var page = Session.Section;
        Session.Player.SaveState();
        Session.Detach();
        Session.Profile.Flush();
        Session = new MainViewModel(this, Session.Profile, adopt: true);
        ReloadResources();
        ApplyTheme();
        if (Window != null)
        {
            var old = Window;
            var state = old.WindowState;
            Window = null;
            ShowWindow();
            if (Window != null)
            {
                Window.Left = old.Left;
                Window.Top = old.Top;
                Window.WindowState = state;
            }
            old.CloseSilently();
        }
        if (page == "settings") Session.GoSettings();
        OnTrackChanged();
        OnPlaybackChanged();
    }

    // Fresh styles and templates so their texts follow the language.
    private static void ReloadResources()
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        var sources = merged.Select(d => d.Source).Where(s => s != null).ToList();
        merged.Clear();
        foreach (var s in sources) merged.Add(new ResourceDictionary { Source = s });
    }

    // ------------------------------------------------------------------ window

    public void ShowWindow()
    {
        if (_exiting || Session == null) return;
        _hiddenSince = DateTime.MaxValue;
        if (Window == null)
        {
            Window = new MainWindow(this) { DataContext = Session };
            var w = Window;
            w.Closed += (_, _) => { if (Window == w) Window = null; };
            w.Show();
        }
        else
        {
            if (Window.WindowState == WindowState.Minimized) Window.WindowState = Settings.WindowMaximized ? WindowState.Maximized : WindowState.Normal;
            Window.Show();
        }
        Window.Activate();
        Window.Topmost = true;
        Window.Topmost = false;
        Session.Player.SetVisible(true);
    }

    // Closing the window: stay in the tray or quit; false cancels.
    public bool OnWindowClosing()
    {
        if (_exiting) return true;
        if (Settings.CloseToTray)
        {
            Session?.Player.SetVisible(false);
            Session?.Player.SaveState();
            Tray.ShowBackgroundHint();
            _hiddenSince = DateTime.Now;
            _ui.BeginInvoke(() =>
            {
                Images.Release();
                Ui.TrimMemory();
            }, DispatcherPriority.ApplicationIdle);
            return true;
        }
        return Exit();
    }

    public bool Exit(bool force = false)
    {
        if (_exiting) return true;
        if (!force && Downloads.HasActive &&
            !Dialogs.Confirm(L.T("Ci sono download in corso"), L.T("Vuoi interromperli e chiudere Ultimate MP3 Player?"), L.T("Chiudi"), true))
            return false;
        if (!force && Session?.InRoom == true &&
            !Dialogs.Confirm(L.T("Uscire dalla stanza?"), L.F("Chiudendo l'app uscirai dalla stanza «{0}».", Session.Together.RoomName), L.T("Chiudi"), true))
            return false;
        // The others are told (and a host hands the room over) before the app goes.
        if (Session?.InRoom == true) Session.Together.Shutdown();
        _exiting = true;
        Downloads.CancelAll();
        Session?.Player.SaveState();
        Session?.Player.Pause();
        Settings.Save();
        try { Library.Flush(); } catch { }
        Profile.FlushAll();
        if (Updates.IsReady) Updates.Apply(false, null, false);
        Presence.Dispose();
        Tray.Dispose();
        Media.Dispose();
        Audio.Dispose();
        Window?.Close();
        Application.Current.Shutdown();
        return true;
    }

    // Command line or second launch: files to play, links to download.
    public void HandleArgs(string[] args)
    {
        ShowWindow();
        if (Session == null) return;
        var files = args.Where(a => File.Exists(a) || Directory.Exists(a)).ToList();
        if (files.Count > 0) _ = Session.Import(files, true);
        var url = args.FirstOrDefault(a => a.StartsWith("http", StringComparison.OrdinalIgnoreCase) || a.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase));
        if (url != null) Session.StartDownload(url);
    }

    // ------------------------------------------------------------------ now playing → Windows and Discord

    public void OnPlaybackChanged()
    {
        var p = Session?.Player;
        bool playing = p?.IsPlaying == true;
        Media.SetState(playing);
        Tray.Update(NowPlayingText(), playing);
        UpdateTimeline(true);
    }

    public void OnTrackChanged()
    {
        var c = Session?.Player.Current;
        Media.SetTrack(c?.Title, c?.T.DisplayArtist, c?.Album, c is { T.HasCover: true } ? AppPaths.TrackCover(c.Id) : null);
        Tray.Update(NowPlayingText(), Session?.Player.IsPlaying == true);
        if (Window != null) Window.Title = c != null ? $"{c.Title} · {c.Artist} — Ultimate MP3 Player" : "Ultimate MP3 Player";
        UpdateTimeline(true);
    }

    public void OnSeek() => UpdateTimeline(true);

    private void UpdateTimeline(bool force)
    {
        var p = Session?.Player;
        if (!force && DateTime.Now - _lastTimeline < TimeSpan.FromSeconds(5)) return;
        _lastTimeline = DateTime.Now;
        var duration = TimeSpan.FromSeconds(p?.Duration ?? 0);
        if (p?.Current != null) Media.SetTimeline(p.EnginePosition, duration);
        // Discord runs its bar on the clock: at another speed the song lasts that much more or less.
        double speed = p?.Speed is > 0 and var s ? s : 1;
        Presence.Update(p?.Current?.T, p?.IsPlaying == true, (p?.EnginePosition ?? TimeSpan.Zero) / speed, duration / speed);
    }

    public void SetDiscordPresence(bool on)
    {
        Presence.Enabled = on;
        if (on) UpdateTimeline(true);
    }

    private string? NowPlayingText()
    {
        var c = Session?.Player.Current;
        return c == null ? null : $"{c.Title} · {c.Artist}";
    }

    // ------------------------------------------------------------------ updates

    private DateTime _lastAppCheck = DateTime.MinValue;

    private async Task OnUpdateTick()
    {
        _updateTimer.Interval = TimeSpan.FromMinutes(10);
        await UpdateEnginesAsync(false);
        if (Settings.AutoUpdateApp && DateTime.Now - _lastAppCheck > TimeSpan.FromHours(6))
        {
            _lastAppCheck = DateTime.Now;
            await Updates.CheckAsync(false);
        }
        // In the tray, idle for a while: install silently.
        if (Updates.IsReady && Window == null && DateTime.Now - _hiddenSince > TimeSpan.FromMinutes(5) &&
            Session?.Player.IsPlaying != true && Session?.InRoom != true && !Downloads.HasActive)
            RestartToUpdate(true);
    }

    public async Task CheckAppUpdateNow()
    {
        if (await Updates.CheckAsync(true)) RestartToUpdate(false);
    }

    public void RestartToUpdate(bool background)
    {
        if (!Updates.IsReady) return;
        if (Downloads.HasActive &&
            !Dialogs.Confirm(L.T("Ci sono download in corso"), L.T("Vuoi interromperli e aggiornare adesso?"), L.T("Aggiorna"), false))
            return;
        if (!Updates.Apply(true, Session?.Profile.Info.Id, background)) return;
        Exit(true);
    }

    // ------------------------------------------------------------------ engines

    private string? _engineStatus;
    public string? EngineStatus { get => _engineStatus; private set => Set(ref _engineStatus, value); }

    // Fetches missing engines (only the exe was copied).
    public Task EnsureEnginesAsync()
    {
        if (Engines.Missing().Count == 0) return Task.CompletedTask;
        return _installTask ??= InstallEngines();
    }

    private async Task InstallEngines()
    {
        try
        {
            EngineStatus = L.T("Download dei motori di download…");
            await Task.Run(() => Engines.InstallMissingAsync((text, pct) =>
                _ui.BeginInvoke(() => EngineStatus = text + (pct is double p ? $" · {p:0}%" : "…")), CancellationToken.None));
            EngineStatus = null;
        }
        catch (Exception ex)
        {
            EngineStatus = L.F("Motori di download non disponibili: {0} (controlla la connessione e riavvia)", ex.Message);
            _installTask = null;
        }
    }

    // yt-dlp and gallery-dl break when sites change: keep them current.
    public async Task<string?> UpdateEnginesAsync(bool force)
    {
        if (!force)
        {
            if (!Settings.AutoUpdateEngines || Downloads.HasActive) return null;
            if (Settings.LastEngineUpdate is DateTime last && DateTime.Now - last < TimeSpan.FromHours(24)) return null;
        }
        if (Engines.Missing().Count > 0)
        {
            await EnsureEnginesAsync();
            if (Engines.Missing().Count > 0) return L.T("Motori non disponibili.");
        }
        try
        {
            var report = await Task.Run(() => Engines.UpdateAsync(null));
            Settings.LastEngineUpdate = DateTime.Now;
            Settings.Save();
            return report;
        }
        catch (Exception ex)
        {
            return L.T("Aggiornamento non riuscito:") + " " + ex.Message;
        }
    }
}
