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
// The iPhone app uses this file too: the few Android-only lines are marked ANDROID_APP.
public sealed class AppHost : Observable
{
    private readonly DispatcherTimer _updateTimer;
    private Task? _installTask;
    private DateTime _lastTimeline;

    public AppHost()
    {
        Settings = AppSettings.Load();
        ApplyLanguage();
        ApplyTheme();
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
            Profiles.Profiles.Add(new ProfileInfo { Name = L.T("Me"), Color = ProfileInfo.Palette[0] });
            Profiles.Save();
        }
        Ui.Animations = Settings.Animations;

        Audio = new AudioEngine(System.Windows.Application.Current.Dispatcher) { OutputDevice = Settings.OutputDevice };
        Downloads = new DownloadQueue(this);
        Downloads.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DownloadQueue.Summary) or nameof(DownloadQueue.HasActive)) OnDownloadsChanged();
        };
        // (the notification's bar follows the group's, twice a second; DownloadService shows it at most once a second; iOS
        // learns here of a list being read)
        Downloads.GroupsRefreshed += OnDownloadsChanged;
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
        Media.FavoritePressed += () => Session?.Player.KeepFromSystem(true);
        Media.SavePressed += () => Session?.Player.KeepFromSystem(false);
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
    private SongCache? _songCache;
    public SongCache SongCache => _songCache ??= new SongCache(AppPaths.CacheDir);

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

    public async void SetLanguage(string code)
    {
        if (code == Settings.Language) return;
        if (Session != null && !await Session.ConfirmLeaveAllAsync(L.T("Changing the language rebuilds the window, so you'll leave the room."),
                L.T("Leave and change language"))) return;
        Settings.Language = code;
        Settings.Save();
        L.English = code == "en";
#if ANDROID_APP
        PlaybackService.CreateChannel(Android.App.Application.Context);
#endif
        Reload();
    }

    public void ApplyTheme()
    {
        // (before a profile is open, the profile picker: the device's look)
        Themes.Apply(Session?.Profile.Data.Theme, Session?.Profile.Info.Color ?? ThemeColors.All[0].A, Session?.Profile.Data.Appearance);
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
#if ANDROID_APP
        // Away, the sound waits in a much bigger buffer (first of all: it fills while the rest happens).
        TrackOut.Background = !visible;
#endif
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
            _songCache?.Flush();
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
            // The notification's Cancel on a group with songs already downloaded: asked here, they'd go too.
            case IncomingKind.CancelDownload:
                if (Session.Page is not DownloadsPageViewModel) Session.Navigate(Session.Downloads);
                Downloads.CancelGroup(what.Value!, ask: true);
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
    public void OnRoomChanged() { }

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
#if ANDROID_APP
        // The oldest group by its name ("Song" or "Playlist · 3 of 20"), its Cancel for that group. A list being read counts as
        // one (a list already read only waits in the app to be opened).
        var groups = Downloads.Groups.Where(x => x.Batch != null || x.Reading).ToList();
        var g = groups.FirstOrDefault();
        var more = groups.Count > 1 ? L.Count(groups.Count - 1, "1 more download", "{0} more downloads") : null;
        DownloadService.Update(Downloads.HasActive || Downloads.IsReading, g?.Title, g != null ? string.Join(" · ", new[] { g.Detail, more }.OfType<string>()) : Downloads.Summary,
            g is { Indeterminate: false } ? g.Percent : null, g?.Id, g?.Brought > 0);
#elif IOS_APP
        Background.Downloading(Downloads.HasActive || Downloads.IsReading);
#endif
    }

    // ------------------------------------------------------------------ updates

    private DateTime _lastAppCheck = DateTime.MinValue;

    private async Task OnUpdateTick()
    {
        _updateTimer.Interval = TimeSpan.FromMinutes(10);
        await UpdateEnginesAsync(false);
        if (Session != null)
        {
            await Session.Backups.AutoAsync();
            await Session.Artists.CheckAsync(false);
            // (the plays that couldn't be sent: offline, a service not answering)
            await Session.Scrobbler.SendAsync();
        }
        if (Settings.AutoUpdateApp && DateTime.Now - _lastAppCheck > TimeSpan.FromHours(6))
        {
            _lastAppCheck = DateTime.Now;
            await Updates.CheckAsync(false);
        }
    }

    // A notification of the system: a followed artist's new release.
    public void Notify(string title, string text) => Notices.Show(title, text);

    // A backup was put in place of the data (BackupsViewModel): the app starts again with it (nothing is saved meanwhile).
    public async void RestartAfterRestore()
    {
#if IOS_APP
        // An iPhone app can't start itself again: closed after saying so, opened again by the user.
        await Dialogs.ConfirmAsync(L.T("Backup restored"), L.T("The app closes now: open it again to find the restored data."), L.T("Close the app"));
        System.Environment.Exit(0);
#else
        await Task.Yield();
        var ctx = Android.App.Application.Context;
        if (ctx.PackageManager?.GetLaunchIntentForPackage(ctx.PackageName!) is { } intent)
        {
            intent.AddFlags(Android.Content.ActivityFlags.NewTask | Android.Content.ActivityFlags.ClearTask);
            ctx.StartActivity(intent);
        }
        Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
#endif
    }

    public async Task CheckAppUpdateNow()
    {
        if (await Updates.CheckAsync(true)) RestartToUpdate(false);
    }

    public async void RestartToUpdate(bool background)
    {
        if (!Updates.IsReady) return;
        if (Downloads.HasActive &&
            !await Dialogs.ConfirmAsync(L.T("Downloads are in progress"), L.T("Stop them and update now?"), L.T("Update"), false))
            return;
        Session?.Player.SaveState();
        Settings.Save();
        try { Library.Flush(); } catch { }
        _songCache?.Flush();
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
            EngineStatus = L.T("Preparing download engines…");
            await Task.Run(() => Engines.InstallMissingAsync((text, pct) =>
                Dispatcher.UIThread.Post(() => EngineStatus = text + (pct is double p ? $" · {p:0}%" : "…")), CancellationToken.None));
            EngineStatus = null;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            EngineStatus = L.F("Download engines not available: {0}", ex.Message);
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
            if (Engines.Missing().Count > 0) return L.T("Engines unavailable.");
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
            return L.T("Update failed:") + " " + ex.Message;
        }
    }
}

public enum IncomingKind { Link, Pack, Files, Player, Downloads, CancelDownload }

public sealed record Incoming(IncomingKind Kind, string? Value = null, List<string>? Values = null);
