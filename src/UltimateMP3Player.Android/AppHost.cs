using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using UltimateMP3Player.Audio;
using UltimateMP3Player.Core;
using UltimateMP3Player.Platform;
using UltimateMP3Player.Services;
using UltimateMP3Player.ViewModels;
using UltimateMP3Player.Views;

namespace UltimateMP3Player;

// Lives as long as the app's process (the Windows app's AppHost, same members): settings, library, audio, downloads, the
// player's notification. The screen (MainView) can come and go (the app closed while the music plays), this stays.
public sealed class AppHost : Observable
{
    private readonly DispatcherTimer _updateTimer;
    private Task? _installTask;
    private DateTime _lastTimeline;

    public AppHost()
    {
        Settings = AppSettings.Load();
        ApplyLanguage();
        // The music folder of a phone is the app's own (a restored backup of another phone points elsewhere).
        if (!Writable(Settings.MusicDir))
        {
            Settings.MusicDir = AppPaths.DefaultMusicDir;
            Settings.Save();
        }
        Importer.OwnedDir = Settings.MusicDir;
        Library = Library.Load();
        Profiles = ProfileList.Load();
        if (Profiles.Profiles.Count == 0)
        {
            Profiles.Profiles.Add(new ProfileInfo { Name = L.T("Io"), Color = ProfileInfo.Palette[0] });
            Profiles.Save();
        }
        Ui.Animations = Settings.Animations;

        Audio = new AudioEngine(System.Windows.Application.Current.Dispatcher);
        Downloads = new DownloadQueue(this);
        Downloads.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DownloadQueue.Summary) or nameof(DownloadQueue.HasActive)) OnDownloadsChanged();
        };
        Updates = new Updater();
        Presence = new DiscordPresence();
        Lyrics = new LyricsService(this);
        Media = new MediaControls();
        Media.PlayPressed += () => Session?.SetPlaying(true);
        Media.PausePressed += () => Session?.SetPlaying(false);
        Media.PlayPausePressed += () => Session?.TogglePlay();
        Media.NextPressed += () => Session?.Player.NextCommand.Execute(null);
        Media.PreviousPressed += () => Session?.Player.PreviousCommand.Execute(null);
        Media.SeekRequested += t => Session?.Player.Seek(t.TotalSeconds);
        Media.FavoritePressed += () => Session?.Player.FavoriteCommand.Execute(null);
        Media.SavePressed += () => Session?.Player.SaveCurrentCommand.Execute(null);
        Media.StopPressed += () => Session?.SetPlaying(false);
        RestartToUpdateCommand = new RelayCommand(() => RestartToUpdate(false));

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _updateTimer.Tick += async (_, _) => await OnUpdateTick();
        _updateTimer.Start();
    }

    private static bool Writable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".ump-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
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
    public ICommand RestartToUpdateCommand { get; }

    // Suggested songs that aren't in the library (the folder keeps the name of the computer apps' 3.0).
    private Core.Together.TogetherCache? _songCache;
    public Core.Together.TogetherCache SongCache => _songCache ??= new Core.Together.TogetherCache(Path.Combine(AppPaths.DataDir, "ascolta-insieme"));

    private MainViewModel? _session;
    public MainViewModel? Session { get => _session; private set => Set(ref _session, value); }

    // The phone's screen (null while the app is closed and the music plays on).
    public MainView? View { get; set; }

    // What Ui, FrameClock and Marquee call "the window": the screen's top level.
    public TopLevel? Window => View is { } v ? TopLevel.GetTopLevel(v) : null;

    // ------------------------------------------------------------------ terms of use

    public bool TermsAccepted => Settings.TermsAccepted >= Dialogs.TermsVersion;

    public void AcceptTerms()
    {
        Settings.TermsAccepted = Dialogs.TermsVersion;
        Settings.Save();
    }

    // ------------------------------------------------------------------ language and theme

    // A new phone gets its own language (Italian or English).
    private void ApplyLanguage()
    {
        if (Settings.Language == null)
        {
            Settings.Language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? "it" : "en";
            Settings.Save();
        }
        L.English = Settings.Language == "en";
    }

    public void SetLanguage(string code)
    {
        if (code == Settings.Language) return;
        Settings.Language = code;
        Settings.Save();
        L.English = code == "en";
        PlaybackService.CreateChannel(Android.App.Application.Context);
        Reload();
    }

    public void ApplyTheme()
    {
        if (Session != null) Themes.Apply(Session.Profile.Data.Theme, Session.Profile.Info.Color);
    }

    // ------------------------------------------------------------------ profiles

    // The profile the app opens with: the only one, the one chosen in the settings, or none (the picker asks).
    public ProfileInfo? StartupProfile()
    {
        var list = Profiles.Profiles;
        if (Settings.StartupProfile != null && list.FirstOrDefault(p => p.Id == Settings.StartupProfile) is { } fixedProfile) return fixedProfile;
        return list.Count == 1 ? list[0] : null;
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
        View?.Attach(Session);
        OnTrackChanged();
        OnPlaybackChanged();
    }

    // "Who's listening?": the profiles' page (Netflix-like), also to add and edit them.
    public void SwitchProfile() => View?.ShowProfiles(false);

    // Rebuilds the screen and its texts (language change); the music keeps playing.
    public void Reload()
    {
        if (Session == null) return;
        Session.Player.SaveState();
        Session.Detach();
        Session.Profile.Flush();
        Session = new MainViewModel(this, Session.Profile, adopt: true);
        // Fresh styles and templates so their texts follow the language (as in App.axaml).
        try
        {
            AppResources.Reload(new[]
            {
                "avares://UltimateMP3Player/Theme.axaml", "avares://UltimateMP3Player/Mobile/MobileTheme.axaml", "avares://UltimateMP3Player/Mobile/Templates.axaml",
            }, new[] { "avares://UltimateMP3Player/Mobile/MobileStyles.axaml" });
        }
        catch (Exception ex) { App.Log(ex); }
        ApplyTheme();
        try { View?.Rebuild(Session); }
        catch (Exception ex)
        {
            // At least the screen of before goes on, with the new session.
            App.Log(ex);
            View?.Attach(Session);
        }
        Session.GoSettings();
        OnTrackChanged();
        OnPlaybackChanged();
    }

    // ------------------------------------------------------------------ the app in front or not

    private bool _visible = true;

    private IDisposable? _awayTimer;

    public void SetVisible(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        // Away, the sound waits in a much bigger buffer (first of all: it fills while the rest happens).
        TrackOut.Background = !visible;
        Ui.Hidden = !visible;
        Session?.Player.SetVisible(visible);
        _awayTimer?.Dispose();
        _awayTimer = null;
        if (visible) return;
        // Saved a moment later, once the bigger buffer is full: writing the library makes garbage, and the collector
        // stops every thread of the app for a while, the sound's too.
        _awayTimer = DispatcherTimer.RunOnce(() =>
        {
            _awayTimer = null;
            if (_visible) return;
            Session?.Player.SaveState();
            Settings.Save();
            try { Library.Flush(); } catch { }
            Profile.FlushAll();
            Images.Release();
            // Memory given back while nothing plays (playing, it would stop the sound for as long as it takes).
            if (Session?.Player.IsPlaying != true) GC.Collect(2, GCCollectionMode.Optimized, false);
        }, TimeSpan.FromMilliseconds(400));
    }

    // A link shared from another app (YouTube, Spotify...), a .ump pack opened, the notification tapped.
    public void HandleIncoming(Incoming what)
    {
        if (Session == null) return;
        switch (what.Kind)
        {
            case IncomingKind.Link:
                Session.StartDownload(what.Value!);
                break;
            case IncomingKind.Pack:
                Session.OpenPack(what.Value!);
                break;
            case IncomingKind.Files:
                _ = Session.Import(what.Values!, true);
                break;
            case IncomingKind.Player:
                if (Session.Player.HasTrack && Session.Page is not NowPlayingViewModel) Session.Navigate(Session.NowPlaying);
                break;
            case IncomingKind.Downloads:
                if (Session.Page is not DownloadsPageViewModel) Session.Navigate(Session.Downloads);
                break;
        }
    }

    // ------------------------------------------------------------------ now playing → the notification and the lock screen

    public void OnPlaybackChanged()
    {
        Media.SetState(Session?.Player.IsPlaying == true);
        UpdateTimeline(true);
    }

    public void OnTrackChanged()
    {
        var c = Session?.Player.Current;
        Media.SetTrack(c?.Title, c?.T.DisplayArtist, c?.Album, c is { T.HasCover: true } ? AppPaths.TrackCover(c.Id) : null, c?.T.Duration ?? 0);
        Media.SetFavorite(c?.IsFavorite == true);
        Media.SetTemporary(Session?.Player.IsTemporary == true, c?.InLibrary == true);
        WatchFavorite(c);
        UpdateTimeline(true);
    }

    // The heart in the notification follows the one in the app.
    private TrackViewModel? _favWatched;

    private void WatchFavorite(TrackViewModel? t)
    {
        if (_favWatched == t) return;
        if (_favWatched != null) _favWatched.PropertyChanged -= OnFavorite;
        _favWatched = t;
        if (t != null) t.PropertyChanged += OnFavorite;
    }

    private void OnFavorite(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackViewModel.IsFavorite) && sender is TrackViewModel t) Media.SetFavorite(t.IsFavorite);
    }

    public void OnSeek() => UpdateTimeline(true);

    public void SetDiscordPresence(bool on) { }

    private void UpdateTimeline(bool force)
    {
        var p = Session?.Player;
        if (!force && DateTime.Now - _lastTimeline < TimeSpan.FromSeconds(5)) return;
        _lastTimeline = DateTime.Now;
        if (p?.Current != null) Media.SetTimeline(p.EnginePosition, TimeSpan.FromSeconds(p.Duration), p.Speed);
    }

    // ------------------------------------------------------------------ downloads in the background

    private void OnDownloadsChanged()
    {
        var running = Downloads.Jobs.Where(j => j.IsRunning).ToList();
        double? pct = running.Count == 1 && !running[0].Indeterminate ? running[0].Percent : null;
        DownloadService.Update(Downloads.HasActive, Downloads.Summary, pct);
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
    }

    public async Task CheckAppUpdateNow()
    {
        if (await Updates.CheckAsync(true)) RestartToUpdate(false);
    }

    public async void RestartToUpdate(bool background)
    {
        if (!Updates.IsReady) return;
        if (Downloads.HasActive &&
            !await Dialogs.ConfirmAsync(L.T("Ci sono download in corso"), L.T("Vuoi interromperli e aggiornare adesso?"), L.T("Aggiorna"), false))
            return;
        Session?.Player.SaveState();
        Settings.Save();
        try { Library.Flush(); } catch { }
        Profile.FlushAll();
        Updates.Apply(true, Session?.Profile.Info.Id, background);
    }

    // ------------------------------------------------------------------ engines

    private string? _engineStatus;
    public string? EngineStatus { get => _engineStatus; private set => Set(ref _engineStatus, value); }

    // Prepares the engines that come with the app (the first start, after an update).
    public Task EnsureEnginesAsync()
    {
        if (Engines.Missing().Count == 0) return Task.CompletedTask;
        return _installTask ??= InstallEngines();
    }

    private async Task InstallEngines()
    {
        try
        {
            EngineStatus = L.T("Preparazione dei motori di download…");
            await Task.Run(() => Engines.InstallMissingAsync((text, pct) =>
                Dispatcher.UIThread.Post(() => EngineStatus = text + (pct is double p ? $" · {p:0}%" : "…")), CancellationToken.None));
            EngineStatus = null;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            EngineStatus = L.F("Motori di download non disponibili: {0}", ex.Message);
        }
        finally { _installTask = null; }
    }

    // yt-dlp breaks when the sites change: kept current.
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

public enum IncomingKind { Link, Pack, Files, Player, Downloads }

public sealed record Incoming(IncomingKind Kind, string? Value = null, List<string>? Values = null);
