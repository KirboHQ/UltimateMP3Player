using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using UltimateMP3Player.Core;
using UltimateMP3Player.Core.Mods;
using UltimateMP3Player.Views;

namespace UltimateMP3Player.ViewModels;

// Settings › Mods: the mods of this run (Core/Mods, ModManager) and what the app gives them (IModHost). Mods run on their own
// threads: what they ask of the app comes here from there, and goes to the UI thread before touching anything.
public sealed class ModsViewModel : Observable, IModHost
{
    private readonly MainViewModel _main;
    private readonly ModManager _manager;
    private readonly Action<Track> _added, _removed;
    private ModSong? _song;

    public ModsViewModel(MainViewModel main)
    {
        _main = main;
        _manager = new ModManager(main.Host.Settings, this);
        // (Appearance's tiles: the cover and the app's bars, until a mod brings its visualizers)
        RebuildScenes();
        InstallCommand = new RelayCommand(() => _ = PickAndInstall());
        LoadFolderCommand = new RelayCommand(() => _ = PickFolderAndLoad());
        _added = t => _manager.Emit("songAdded", ModSong.Of(t));
        _removed = t =>
        {
            _manager.Emit("songRemoved", ModSong.Of(t));
            _manager.ForgetSong(t.Id);
        };
        main.Host.Library.TrackAdded += _added;
        main.Host.Library.TrackRemoved += _removed;
        main.Player.PropertyChanged += OnPlayer;
        _song = main.Player.Current is { } now ? ModSong.Of(now.T) : null;
        // Their files are read away from the UI thread; each then starts on its own thread.
        _ = Task.Run(() =>
        {
            try { _manager.LoadAll(); }
            catch (Exception ex) { App.Log(ex); }
            Ui(() =>
            {
                Refresh();
                _ = UpdateModsAsync();
            });
        });
    }

    public ObservableCollection<ModItemViewModel> Items { get; } = new();
    public ICommand InstallCommand { get; }
    public ICommand LoadFolderCommand { get; }
    // A developer's folder: only where there are folders to work in.
    public bool CanLoadFolder => !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();
    public bool HasMods => Items.Count > 0;

    private static void Ui(Action a) => Application.Current.Dispatcher.BeginInvoke(a);

    public void Detach()
    {
        _main.Host.Library.TrackAdded -= _added;
        _main.Host.Library.TrackRemoved -= _removed;
        _main.Player.PropertyChanged -= OnPlayer;
        _gone.Cancel();
        _manager.Dispose();
    }

    private void OnPlayer(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.Current))
        {
            var song = _main.Player.Current is { } t ? ModSong.Of(t.T) : null;
            if (song?.Id == _song?.Id) return;
            _song = song;
            _manager.Emit("song", song);
            UpdateScenes();
        }
        else if (e.PropertyName == nameof(PlayerViewModel.IsPlaying)) _manager.Emit("playing", _main.Player.IsPlaying);
    }

    // The list as the manager has it now (a mod reloaded is another object with the same id).
    private void Refresh()
    {
        var mods = _manager.Mods;
        for (int i = Items.Count - 1; i >= 0; i--)
            if (mods.All(m => m.Id != Items[i].Id)) Items.RemoveAt(i);
        foreach (var m in mods)
        {
            if (Items.FirstOrDefault(x => x.Id == m.Id) is { } item) item.Use(m);
            else Items.Add(new ModItemViewModel(this, m));
        }
        OnChanged(nameof(HasMods));
    }

    internal bool IsEnabled(ModRuntime m) => _manager.IsEnabled(m);

    internal void SetEnabled(ModItemViewModel item, bool on)
    {
        // Stopping waits for the mod a moment: not on the UI thread.
        var mod = item.Mod;
        _ = Task.Run(() => _manager.SetEnabled(mod, on));
    }

    internal void Reload(ModItemViewModel item)
    {
        var mod = item.Mod;
        _ = Task.Run(() => _manager.Reload(mod));
    }

    internal async Task<bool> RemoveAsync(ModItemViewModel item)
    {
        var answer = await Dialogs.ChooseAsync(L.F("Remove “{0}”?", item.Name),
            item.Mod.IsDev ? L.T("The folder stays where it is: only the app forgets it. What the mod saved can go too.")
                : L.T("What it saved (its settings and data) can go with it, or stay for when you install it again."),
            L.T("Remove it and its data"), L.T("Remove, keep its data"));
        if (answer < 0) return false;
        var mod = item.Mod;
        try { await Task.Run(() => _manager.Remove(mod, data: answer == 0)); }
        catch (Exception ex)
        {
            App.Log(ex);
            _main.Toast(ex.Message);
        }
        Refresh();
        return true;
    }

    internal async Task AddLooksAsync(ModItemViewModel item)
    {
        foreach (var look in item.Mod.Manifest.Contributes.Looks)
        {
            var file = Path.Combine(item.Mod.Folder, look);
            if (File.Exists(file)) await _main.Settings.Looks.ImportFile(file);
        }
    }

    private async Task PickAndInstall()
    {
        var file = await Dialogs.PickFileAsync(L.T("Install a mod"), (L.T("Mods"), new[] { ModPackage.Extension }));
        if (file != null) await InstallFile(file);
    }

    // A .umpmod file: what it is and what it asks for, then installed (or updated) if the user says so (ask: false, the tests).
    public async Task InstallFile(string file, bool ask = true)
    {
        ModManifest m;
        try { m = await Task.Run(() => ModPackage.Peek(file)); }
        catch (ModException ex)
        {
            Dialogs.Alert(L.T("This mod can't be installed"), ex.Message);
            return;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Dialogs.Alert(L.T("This mod can't be installed"), ex.Message);
            return;
        }
        var old = _manager.Mods.FirstOrDefault(x => x.Id == m.Id && !x.IsDev);
        if (ask && !await Dialogs.ConfirmAsync(old == null ? L.F("Install “{0}”?", m.Name) : L.F("Update “{0}” to {1}?", m.Name, m.Version), Describe(m),
            old == null ? L.T("Install") : L.T("Update"))) return;
        try { await Task.Run(() => _manager.Install(file)); }
        catch (Exception ex)
        {
            if (ex is not ModException) App.Log(ex);
            Dialogs.Alert(L.T("This mod can't be installed"), ex.Message);
            return;
        }
        Refresh();
        _main.Toast(old == null ? L.F("“{0}” installed", m.Name) : L.F("“{0}” updated", m.Name));
    }

    // ---- Get mods: the catalogue (Core/Mods/ModCatalogue.cs), read once a run when Settings shows it, again by its button
    private readonly ObservableCollection<CatalogueItemViewModel> _catalogue = new();
    private bool _catalogueAsked;
    private string _catalogueStatus = "";

    public ObservableCollection<CatalogueItemViewModel> Catalogue
    {
        get
        {
            if (!_catalogueAsked)
            {
                _catalogueAsked = true;
                Ui(() => _ = LoadCatalogueAsync());
            }
            return _catalogue;
        }
    }

    public string CatalogueStatus
    {
        get => _catalogueStatus;
        private set
        {
            _catalogueStatus = value;
            OnChanged(nameof(CatalogueStatus), nameof(HasCatalogueStatus));
        }
    }

    public bool HasCatalogueStatus => _catalogueStatus.Length > 0;

    public async Task LoadCatalogueAsync()
    {
        CatalogueStatus = L.T("Loading the list of mods…");
        try
        {
            var list = await ModCatalogue.LoadAsync(Services.AppInfo.Version, CancellationToken.None);
            _catalogue.Clear();
            foreach (var m in list) _catalogue.Add(new CatalogueItemViewModel(this, m));
            CatalogueStatus = list.Count == 0 ? L.T("No mods in the list for this device yet.") : "";
        }
        catch (Exception)
        {
            CatalogueStatus = L.T("The list of mods can't be reached: check your internet connection.");
        }
    }

    internal ModRuntime? Installed(string id) => _manager.Mods.FirstOrDefault(m => m.Id == id && !m.IsDev);

    // From the list: downloaded, checked, then the same question as a file (what it is, what it asks for).
    internal async Task InstallFromCatalogue(CatalogueItemViewModel item)
    {
        var dir = Path.Combine(Path.GetTempPath(), "UltimateMP3Player", "mods");
        try
        {
            item.Busy = true;
            var file = await ModCatalogue.DownloadAsync(item.Entry, dir, (done, total) => Ui(() => item.Progress(done, total)), CancellationToken.None);
            await InstallFile(file);
            try { File.Delete(file); } catch (Exception) { }
        }
        catch (Exception ex)
        {
            if (ex is not ModException) App.Log(ex);
            Dialogs.Alert(L.T("This mod can't be installed"), ex.Message);
        }
        finally
        {
            item.Busy = false;
            foreach (var c in _catalogue) c.Refresh();
        }
    }

    // ---- The catalogue's mods kept up to date: a newer version that asks for nothing new is installed by itself (as a browser's
    // extensions), once it isn't drawn or heard with the song playing (it starts again from the beginning); one that asks for more
    // waits for the user (Get mods' Update button), said in a toast.
    private readonly CancellationTokenSource _gone = new();

    private async Task UpdateModsAsync()
    {
        try
        {
            // (away from what the app does as it starts)
            await Task.Delay(TimeSpan.FromSeconds(20), _gone.Token);
            if (!_manager.Mods.Any(m => !m.IsDev)) return;
            if (!_catalogueAsked)
            {
                _catalogueAsked = true;
                await LoadCatalogueAsync();
            }
            var dir = Path.Combine(Path.GetTempPath(), "UltimateMP3Player", "mods");
            foreach (var entry in _catalogue.Select(c => c.Entry).ToList())
            {
                if (Installed(entry.Id) is not { } old || !ModCatalogue.IsNewer(entry.Version, old.Manifest.Version)) continue;
                try { await UpdateAsync(entry, dir); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (ex is not ModException) App.Log(ex);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.Log(ex); }
    }

    private async Task UpdateAsync(ModCatalogueEntry entry, string dir)
    {
        var file = await ModCatalogue.DownloadAsync(entry, dir, null, _gone.Token);
        try
        {
            var m = await Task.Run(() => ModPackage.Peek(file));
            if (Installed(entry.Id) is not { } old) return;
            if (m.AsksMoreThan(old.Manifest, old.Granted))
            {
                _main.Toast(L.F("An update of “{0}” asks for more: see Settings › Mods", m.Name));
                return;
            }
            while (InUse(entry.Id)) await Task.Delay(TimeSpan.FromSeconds(30), _gone.Token);
            await Task.Run(() => _manager.Install(file, keepOff: true));
            Refresh();
            foreach (var c in _catalogue) c.Refresh();
            _main.Toast(L.F("“{0}” updated to {1}", m.Name, m.Version));
        }
        finally
        {
            try { File.Delete(file); } catch (Exception) { }
        }
    }

    // Drawn on the song page or as the bars, or playing its sounds, with the song playing: not stopped under the user's eyes.
    private bool InUse(string id) => _main.Player.IsPlaying && (SongVisualizer?.Mod.Id == id || BarsVisualizer?.Mod.Id == id || _cuesOf?.Id == id);

    private async Task PickFolderAndLoad()
    {
        var dir = await Dialogs.PickFolderAsync(L.T("Choose the mod's folder (the one with mod.json)"));
        if (dir == null) return;
        ModManifest m;
        try { m = ModManifest.Read(dir); }
        catch (Exception ex)
        {
            Dialogs.Alert(L.T("This folder can't be loaded"), ex is FileNotFoundException ? L.T("There's no mod.json in it.") : ex.Message);
            return;
        }
        if (!await Dialogs.ConfirmAsync(L.F("Load “{0}” from a folder?", m.Name),
            Describe(m) + "\n\n" + L.T("It's loaded again by itself whenever its files change."), L.T("Load"))) return;
        try { await Task.Run(() => _manager.AddFolder(dir)); }
        catch (Exception ex)
        {
            Dialogs.Alert(L.T("This folder can't be loaded"), ex.Message);
            return;
        }
        Refresh();
    }

    // Who made it and what it may do beyond its own folder, before it's installed.
    private static string Describe(ModManifest m)
    {
        var lines = new List<string> { m.Author != null ? L.F("Version {0}, by {1}", m.Version, m.Author) : L.F("Version {0}", m.Version) };
        if (m.Description != null) lines.Add(m.Translate(m.Description));
        lines.Add("");
        lines.Add(m.Permissions.Count == 0 ? L.T("It asks for nothing beyond its own folder.")
            : L.T("It asks to:") + "\n" + string.Join("\n", ModPermissions.Lines(m, m.Permissions).Select(p => "• " + p)));
        lines.Add("");
        lines.Add(L.T("Mods are made by other people: install only the ones you trust."));
        return string.Join("\n", lines);
    }

    // The entries the mods put in this song's menu (under More).
    public IReadOnlyList<(string Title, Action Run)> SongEntries(TrackViewModel t)
    {
        var song = ModSong.Of(t.T);
        return _manager.SongEntries(song).Select(x => (x.Entry.Title, (Action)(() => x.Mod.RunEntry(x.Entry, song)))).ToList();
    }

    // Library's import menu: what the mods add to it (Core/Mods, ump.importers).
    public IReadOnlyList<(string Title, Action Run)> Importers =>
        _manager.Importers.Select(i => (i.Title, (Action)(() => i.Mod.RunImporter(i)))).ToList();

    // A file dropped on the window or opened with the app, of a type a mod's importer declared (an .osz): to that mod.
    public bool Open(string path)
    {
        if (!File.Exists(path) || _manager.OpenerOf(path) is not { } imp) return false;
        imp.Mod.OpenWithImporter(imp, path);
        return true;
    }

    // ---- IModHost (called from the mods' threads)
    public string AppVersion => Services.AppInfo.VersionText;
    public ModSong? Song => _song;
    public double Position => _main.Player.Position;
    public bool Playing => _main.Player.IsPlaying;

    public void Control(string action) => Ui(() =>
    {
        switch (action)
        {
            case "play": _main.Player.Play(); break;
            case "pause": _main.Player.Pause(); break;
            case "next": _ = _main.Player.Next(false); break;
            case "previous": _ = _main.Player.Previous(); break;
        }
    });

    public IReadOnlyList<ModSong> Songs()
    {
        IReadOnlyList<ModSong> list = Array.Empty<ModSong>();
        // (the songs the library pages show for this profile)
        Application.Current.Dispatcher.Invoke(() => list = _main.Songs().Select(ModSong.Of).ToList());
        return list;
    }

    public void Toast(ModRuntime mod, string text) => Ui(() =>
    {
        _main.Toast($"{mod.Manifest.Name}: {text}");
        Items.FirstOrDefault(x => x.Id == mod.Id)?.Toast(text);
    });

    public Task<bool> ConfirmAsync(ModRuntime mod, string title, string message, string ok)
    {
        var answer = new TaskCompletionSource<bool>();
        Ui(async () =>
        {
            try { answer.TrySetResult(await Dialogs.ConfirmAsync(Whose(mod, title), message, ok)); }
            catch { answer.TrySetResult(false); }
        });
        return answer.Task;
    }

    public Task<int> ChooseAsync(ModRuntime mod, string title, string message, string[] options)
    {
        var answer = new TaskCompletionSource<int>();
        Ui(async () =>
        {
            try { answer.TrySetResult(await Dialogs.ChooseAsync(Whose(mod, title), message, options)); }
            catch { answer.TrySetResult(-1); }
        });
        return answer.Task;
    }

    public Task<int> ListAsync(ModRuntime mod, string title, IReadOnlyList<(string Title, string? Hint)> items, int selected)
    {
        var answer = new TaskCompletionSource<int>();
        Ui(async () =>
        {
            try { answer.TrySetResult(await ModDialogs.ListAsync(Whose(mod, title), items, selected)); }
            catch { answer.TrySetResult(-1); }
        });
        return answer.Task;
    }

    public Task<int> PickAsync(ModRuntime mod, string title, string message, IReadOnlyList<(string Title, string? Hint)> items, int selected, string ok)
    {
        var answer = new TaskCompletionSource<int>();
        Ui(async () =>
        {
            try { answer.TrySetResult(await ModDialogs.PickAsync(Whose(mod, title), message, items, selected, ok)); }
            catch { answer.TrySetResult(-1); }
        });
        return answer.Task;
    }

    private static string Whose(ModRuntime mod, string title) => title.Length > 0 ? $"{title} ({mod.Manifest.Name})" : mod.Manifest.Name;

    public void Changed(ModRuntime mod) => Ui(() =>
    {
        Refresh();
        Items.FirstOrDefault(x => x.Id == mod.Id)?.Refresh();
        UpdateScenes();
        RebuildScenes();
    });

    public string MusicDir => _main.Host.Settings.MusicDir;
    // (the phones' pickers give copies of files, never a folder's place)
    public bool CanPickFolders => !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();

    public Task<string?> PickAsync(ModRuntime mod, bool folder, string title, IReadOnlyList<string> extensions, string? start)
    {
        var answer = new TaskCompletionSource<string?>();
        Ui(async () =>
        {
            try
            {
                var heading = Whose(mod, title.Length > 0 ? title : folder ? L.T("Choose a folder") : L.T("Choose a file"));
                var filters = extensions.Count > 0 ? new[] { (string.Join(", ", extensions), (IEnumerable<string>)extensions) } : Array.Empty<(string, IEnumerable<string>)>();
                answer.TrySetResult(folder ? await Dialogs.PickFolderAsync(heading, start != null && Directory.Exists(start) ? start : null)
                    : await Dialogs.PickFileAsync(heading, filters));
            }
            catch (Exception) { answer.TrySetResult(null); }
        });
        return answer.Task;
    }

    public Task<string?> AddSongAsync(ModRuntime mod, ModNewSong song)
    {
        var answer = new TaskCompletionSource<string?>();
        Ui(async () =>
        {
            try { answer.TrySetResult(await AddSong(song)); }
            catch (Exception ex) { answer.TrySetException(ex); }
        });
        return answer.Task;
    }

    // As files added by hand (MainViewModel.Import): read, analysed, in this profile's library; the mod's words over the file's tags,
    // its picture as the cover, its keys so the same song is known next time. A song already there keeps its own words.
    private async Task<string?> AddSong(ModNewSong s)
    {
        var lib = _main.Library;
        var t = s.Keys.Count > 0 ? lib.FindByKeys(s.Keys) : null;
        if (t == null)
        {
            var full = Path.GetFullPath(s.Path);
            bool known = (lib.FindByKeys(new[] { SourceKeys.FileKey(full) }) ?? lib.FindByPath(full)) != null;
            if (AudioAnalysis.Native == null && Engines.Missing().Contains(Path.GetFileName(Engines.Ffmpeg))) await _main.Host.EnsureEnginesAsync();
            t = await Task.Run(() => Importer.ImportAsync(lib, full, CancellationToken.None));
            if (!known)
            {
                bool changed = false;
                void Set(string? value, string? now, Action<string> set)
                {
                    if (value == null || value == now) return;
                    set(value);
                    changed = true;
                }
                Set(s.Title, t.Title, v => t.Title = v);
                Set(s.Artist, t.Artist, v => t.Artist = v);
                Set(s.Album, t.Album, v => t.Album = v);
                Set(s.Year, t.Year, v => t.Year = v);
                if (changed) lib.Changed(t);
                if (s.Cover != null && File.Exists(s.Cover) && await Task.Run(() => AudioAnalysis.MakeCoverFromImageAsync(s.Cover, AppPaths.TrackCover(t.Id))))
                {
                    t.HasCover = true;
                    t.CoverVersion++;
                    lib.Changed(t);
                }
            }
            if (s.Keys.Count > 0) lib.AddKeys(t, s.Keys);
        }
        _main.Profile.Own(new[] { t.Id });
        return t.Id;
    }

    public IReadOnlyList<ModPlaylist> Playlists()
    {
        IReadOnlyList<ModPlaylist> list = Array.Empty<ModPlaylist>();
        Application.Current.Dispatcher.Invoke(() => list = _main.Profile.PlaylistsSnapshot()
            .Select(p => new ModPlaylist(p.Id, PlaylistViewModel.DisplayName(p), p.Tracks.ToList())).ToList());
        return list;
    }

    public Task<string?> CreatePlaylistAsync(ModRuntime mod, string name)
    {
        var answer = new TaskCompletionSource<string?>();
        Ui(() => answer.TrySetResult(_main.Profile.CreatePlaylist(name).Id));
        return answer.Task;
    }

    public Task<bool> AddToPlaylistAsync(ModRuntime mod, string playlistId, IReadOnlyList<string> songIds)
    {
        var answer = new TaskCompletionSource<bool>();
        Ui(() =>
        {
            // (songs of the library only)
            var p = _main.Profile.GetPlaylist(playlistId);
            answer.TrySetResult(p != null && _main.Profile.AddTracks(p, songIds.Where(id => _main.Library.Get(id) != null)) > 0);
        });
        return answer.Task;
    }

    public ModImage? LoadImage(string file) => ModSkia.Decode(file);
    public ModImage? LoadImage(string file, int maxSide, float blur) => ModSkia.Decode(file, maxSide, blur);
    public float MeasureText(string text, float size, bool bold) => ModSkia.Measure(text, size, bold);
    // (every UI draws mods with Skia, WPF too)
    public bool CanBlend => true;

    // A mod's sound that isn't a plain WAV (Ogg, MP3): read whole by the platform's decoder, as stereo samples.
    public (float[] Samples, int Rate)? DecodeSound(string file)
    {
        try
        {
            using var source = Audio.AudioPlatform.Open(file, 0);
            var p = source.Samples;
            int rate = p.WaveFormat.SampleRate, channels = Math.Max(1, p.WaveFormat.Channels);
            var buffer = new float[4096 * channels];
            var stereo = new List<float>();
            for (int n; (n = p.Read(buffer, 0, buffer.Length)) > 0 && stereo.Count < rate * 2 * 20;)
                for (int i = 0; i + channels <= n; i += channels)
                {
                    stereo.Add(buffer[i]);
                    stereo.Add(channels > 1 ? buffer[i + 1] : buffer[i]);
                }
            return stereo.Count > 0 ? (stereo.ToArray(), rate) : null;
        }
        catch (Exception) { return null; }
    }

    // A mod's sounds for a song (hitsounds): to the engine, which plays them inside that song. One mod's at a time.
    private ModRuntime? _cuesOf;

    public void Cue(ModRuntime mod, CueSheet? sheet) => Ui(() =>
    {
        if (sheet == null && _cuesOf != mod) return;
        _cuesOf = sheet == null ? null : mod;
        _main.Host.Audio.SetCues(sheet);
    });

    public ModSong? FindSong(string title, string? artist, double? duration)
    {
        ModSong? found = null;
        Application.Current.Dispatcher.Invoke(() => found = _main.Library.FindByName(title, artist, duration) is { } t ? ModSong.Of(t) : null);
        return found;
    }

    // As "Remove duplicates" does with a copy: the old song's place in every profile (playlists, plays, likes) and its keys go to the
    // mod's song, then it leaves the library; its file, if it has one, stays where it is. Only a song this mod added takes a place.
    public Task<bool> ReplaceSongAsync(ModRuntime mod, string oldId, string newId)
    {
        var answer = new TaskCompletionSource<bool>();
        Ui(async () =>
        {
            try
            {
                var lib = _main.Library;
                if (lib.Get(oldId) is not { } old || lib.Get(newId) is not { } now || !now.Keys.Any(k => k.StartsWith(mod.Id + ":", StringComparison.Ordinal)))
                {
                    answer.TrySetResult(false);
                    return;
                }
                var map = new Dictionary<string, string> { [old.Id] = now.Id };
                await _main.Player.RemoveTracks(map.Keys.ToList());
                foreach (var info in _main.Host.Profiles.Profiles) Core.Profile.Get(info).ReplaceTracks(map);
                var keys = old.Keys.ToList();
                lib.Remove(old);
                lib.AddKeys(now, keys);
                try { File.Delete(AppPaths.TrackCover(old.Id)); } catch (Exception) { }
                LyricsStore.Delete(old.Id);
                _main.OnStatsChanged();
                answer.TrySetResult(true);
            }
            catch (Exception ex)
            {
                App.Log(ex);
                answer.TrySetResult(false);
            }
        });
        return answer.Task;
    }

    // The sign-ins made in the app (Settings › Downloads), when the downloads use them: a mod's downloads from those sites go as them.
    public string? CookieFile => S.CookiesBrowserOrNull is { } c && c.StartsWith("file:", StringComparison.Ordinal) ? c[5..] : null;
    public bool WantsVideos => S.AutoVideos;

    // A link pasted in the app that a mod takes (an osu! beatmap's page): to that mod, not to the downloads.
    public bool OpenLink(string url)
    {
        if (_manager.LinkOf(url) is not { } link) return false;
        link.Mod.RunLink(link, url);
        return true;
    }

    // The mods' own searches (osu! beatmaps), shown in Search under their title. A link: only the mod that takes it.
    public bool HasSearches => _manager.Searches.Any();
    public bool SearchesLink(string url) => _manager.LinkOf(url) is { } link && link.Mod.Searches.Count > 0;
    public List<ModSearch> SearchesFor(string words)
    {
        if (_manager.LinkOf(words) is { } link) return link.Mod.Searches.ToList();
        return MainViewModel.LooksLikeLink(words) ? new() : _manager.Searches.ToList();
    }

    // ---- the visualizers chosen (in their mod's page), for the song page
    private int _asked;
    // In place of the cover: the one chosen, if it can show the song playing (its canShow, asked on its thread).
    public ModVisualizer? SongVisualizer { get; private set; }
    public ModVisualizer? BarsVisualizer { get; private set; }

    private ModVisualizer? Find(string? key) =>
        key == null ? null : _manager.Running.SelectMany(m => m.Visualizers).FirstOrDefault(v => v.Key == key);

    public bool IsChosen(ModVisualizer v) => (v.Place == "bars" ? S.BarsScene : S.SongScene) == v.Key;

    public void Choose(ModVisualizer v, bool on)
    {
        // (a mod asks from its own thread)
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Ui(() => Choose(v, on));
            return;
        }
        var now = v.Place == "bars" ? S.BarsScene : S.SongScene;
        SetScene(v.Place, on ? v.Key : now == v.Key ? null : now);
    }

    private void SetScene(string place, string? key)
    {
        if (place == "bars") S.BarsScene = key;
        else S.SongScene = key;
        S.Save();
        UpdateScenes();
        foreach (var item in Items) item.Refresh();
        OnChanged(nameof(SongScene), nameof(BarsScene));
    }

    // Settings › Appearance: the same choice as in each mod's page (one per place, whatever mod), every mod's visualizers together,
    // where the bars and the cover are chosen. Lists made again only when the visualizers change (the tiles compare choices).
    private List<Choice> _songScenes = new(), _barsScenes = new();
    public IReadOnlyList<Choice> SongScenes => _songScenes;
    public IReadOnlyList<Choice> BarsScenes => _barsScenes;
    public bool HasSongScenes => _songScenes.Count > 1;
    public bool HasBarsScenes => _barsScenes.Count > 1;
    public bool HasVisualizers => HasSongScenes || HasBarsScenes;
    public Choice? SongScene
    {
        get => SceneOf(_songScenes, S.SongScene);
        set { if (value?.Value is string key) SetScene("song", key.Length > 0 ? key : null); }
    }
    public Choice? BarsScene
    {
        get => SceneOf(_barsScenes, S.BarsScene);
        set { if (value?.Value is string key) SetScene("bars", key.Length > 0 ? key : null); }
    }

    private static Choice? SceneOf(List<Choice> list, string? key) => list.FirstOrDefault(c => (string?)c.Value == (key ?? "")) ?? list.FirstOrDefault();

    private void RebuildScenes()
    {
        var all = _manager.Running.SelectMany(m => m.Visualizers).ToList();
        List<Choice> Of(string place, string none) => all.Where(v => v.Place == place).Select(v => new Choice(v.Title, v.Key, v.Mod.Manifest.Name))
            .Prepend(new Choice(none, "")).ToList();
        bool Same(List<Choice> a, List<Choice> b) => a.Select(c => c.Value).SequenceEqual(b.Select(c => c.Value));
        var song = Of("song", L.T("The cover"));
        var bars = Of("bars", L.T("The app's bars"));
        if (Same(song, _songScenes) && Same(bars, _barsScenes)) return;
        _songScenes = song;
        _barsScenes = bars;
        OnChanged(nameof(SongScenes), nameof(BarsScenes), nameof(HasSongScenes), nameof(HasBarsScenes), nameof(HasVisualizers), nameof(SongScene), nameof(BarsScene));
    }

    private AppSettings S => _main.Host.Settings;

    private void UpdateScenes()
    {
        var bars = Find(S.BarsScene);
        if (bars != BarsVisualizer)
        {
            BarsVisualizer = bars;
            _main.NowPlaying.Refresh();
        }
        // Every mod's song visualizers asked (the chosen one first, then the one offered so far: turned off, the button brings back
        // that same one): the song page's button offers the first that can show the song.
        var chosen = Find(S.SongScene);
        var all = _manager.Running.SelectMany(m => m.Visualizers).Where(v => v.Place == "song")
            .OrderBy(v => v == chosen ? 0 : v == SongOffer ? 1 : 2).ToList();
        int ask = ++_asked;
        if (all.Count == 0)
        {
            SetSong(null, null);
            return;
        }
        var answers = new bool?[all.Count];
        for (int i = 0; i < all.Count; i++)
        {
            int k = i;
            all[k].Mod.AskCanShow(all[k], _song, yes => Ui(() =>
            {
                // (a later question already went: this answer is old)
                if (ask != _asked) return;
                answers[k] = yes;
                // (the first that can, once all those before it said they can't)
                for (int j = 0; j < answers.Length; j++)
                {
                    if (answers[j] == null) return;
                    if (answers[j] == true)
                    {
                        SetSong(all[j], chosen);
                        return;
                    }
                }
                SetSong(null, chosen);
            }));
        }
    }

    // The song page's button (NowPlayingViewModel.ShowScene) turns this one on and off: the chosen one if it can show the song
    // playing, else the first of the others that can; null: none can.
    public ModVisualizer? SongOffer { get; private set; }

    private void SetSong(ModVisualizer? offer, ModVisualizer? chosen)
    {
        var now = offer != null && offer == chosen ? offer : null;
        if (offer == SongOffer && now == SongVisualizer) return;
        (SongOffer, SongVisualizer) = (offer, now);
        _main.NowPlaying.Refresh();
    }

    public void ShowOffer(bool on)
    {
        if (SongOffer is { } v) SetScene("song", on ? v.Key : null);
    }
}

// A mod in Settings' list, and its page.
public sealed class ModItemViewModel : Observable
{
    private readonly ModsViewModel _owner;

    public ModItemViewModel(ModsViewModel owner, ModRuntime mod)
    {
        _owner = owner;
        Mod = mod;
        Mod.LogChanged += OnLog;
        OpenCommand = new RelayCommand(() => _ = ModDialogs.ShowAsync(this));
    }

    public ModRuntime Mod { get; private set; }
    public ICommand OpenCommand { get; }
    public string Id => Mod.Id;
    public string Name => Mod.Manifest.Name;
    public string Line => string.Join(" · ", new[] { Mod.Manifest.Version, Mod.Manifest.Author, Mod.IsDev ? L.T("from a folder") : null }.Where(s => !string.IsNullOrEmpty(s)));
    public string? Description => Mod.Manifest.Description is { } d ? Mod.Manifest.Translate(d) : null;

    // Its own picture (mod.json "icon", inside its folder; read whole, so an update can swap the folder), else the views draw the
    // mods' puzzle piece.
    private BitmapSource? _icon;
    private bool _iconRead;
    public BitmapSource? Icon
    {
        get
        {
            if (_iconRead) return _icon;
            _iconRead = true;
            return _icon = Mod.Manifest.Icon is { } file ? Images.Decode(Path.Combine(Mod.Folder, file), 96) : null;
        }
    }
    public bool HasIcon => Icon != null;

    public bool Enabled
    {
        get => _owner.IsEnabled(Mod);
        set
        {
            if (value == Enabled) return;
            _owner.SetEnabled(this, value);
            OnChanged();
        }
    }
    public string StatusText => Mod.Broken != null ? L.F("It can't be used: {0}", Mod.Broken)
        : Mod.Status switch
        {
            ModStatus.Running => L.T("On"),
            ModStatus.Failed => L.F("Stopped by an error: {0}", Mod.Error),
            _ => L.T("Off"),
        };
    public bool HasProblem => Mod.Status == ModStatus.Failed;
    public string PermissionsText => Mod.Granted.Count == 0 ? L.T("It asks for nothing beyond its own folder.")
        : L.T("It may:") + "\n" + string.Join("\n", ModPermissions.Lines(Mod.Manifest, Mod.Granted).Select(p => "• " + p));
    public IReadOnlyList<ModSettingViewModel> Settings => Mod.Settings.Select(s => new ModSettingViewModel(Mod, s)).ToList();
    public string LogText => string.Join("\n", Mod.Log);
    public bool HasLooks => Mod.Manifest.Contributes.Looks.Count > 0;
    public IReadOnlyList<ModVisualizerViewModel> Visualizers => Mod.Visualizers.Select(v => new ModVisualizerViewModel(_owner, v)).ToList();
    public string LooksText => L.Count(Mod.Manifest.Contributes.Looks.Count, "Add its look to this profile", "Add its {0} looks to this profile");

    // Something about the mod changed (its settings, its state): an open page draws itself again.
    public event Action? Changed;
    public event Action? LogUpdated;
    // Its toasts, for its page when open (the window's own shows under it).
    public event Action<string>? Toasted;
    internal void Toast(string text) => Toasted?.Invoke(text);

    private void OnLog() => Application.Current.Dispatcher.BeginInvoke(() => LogUpdated?.Invoke());

    internal void Use(ModRuntime m)
    {
        if (m == Mod) return;
        Mod.LogChanged -= OnLog;
        Mod = m;
        Mod.LogChanged += OnLog;
        _iconRead = false;
        Refresh();
    }

    internal void Refresh()
    {
        OnChanged(nameof(Name), nameof(Line), nameof(Description), nameof(Enabled), nameof(StatusText), nameof(HasProblem), nameof(PermissionsText), nameof(HasLooks),
            nameof(Icon), nameof(HasIcon));
        Changed?.Invoke();
    }

    public void Reload() => _owner.Reload(this);
    public Task<bool> RemoveAsync() => _owner.RemoveAsync(this);
    public Task AddLooksAsync() => _owner.AddLooksAsync(this);
    public override string ToString() => Name;
}

// A visualizer of the mod, and whether it's the one shown (on the song page, or as the bars of the music).
public sealed class ModVisualizerViewModel(ModsViewModel owner, ModVisualizer v) : Observable
{
    public ModVisualizer V => v;
    public string Title => v.Place == "bars" ? L.F("“{0}” as the bars of the music", v.Title) : L.F("“{0}” on the song page", v.Title);
    public string Hint => v.Place == "bars" ? L.T("In place of the bars' own shape, where they are.") : L.T("In place of the cover, for the songs it can show.");
    public bool On
    {
        get => owner.IsChosen(v);
        set { owner.Choose(v, value); OnChanged(); }
    }
}

// One setting a mod asked for, for the control the app draws for it.
public sealed class ModSettingViewModel : Observable
{
    private readonly ModRuntime _mod;

    public ModSettingViewModel(ModRuntime mod, ModSetting s)
    {
        _mod = mod;
        S = s;
        Choices = s.Choices.Select(c => new Choice(c.Text, c.Value)).ToList();
        PressCommand = new RelayCommand(() => _mod.SetSettingValue(S.Id, true));
    }

    public ModSetting S { get; }
    public string Type => S.Type;
    public string Title => S.Title;
    public string? Hint => S.Hint;
    public ICommand PressCommand { get; }

    public bool On
    {
        get => _mod.SettingValue(S.Id) is true;
        set { _mod.SetSettingValue(S.Id, value); OnChanged(); }
    }

    public double Number
    {
        get => _mod.SettingValue(S.Id) is double d ? d : S.Min;
        set
        {
            var v = Math.Clamp(S.Step > 0 ? S.Min + Math.Round((value - S.Min) / S.Step) * S.Step : value, S.Min, S.Max);
            if (v == Number) return;
            _mod.SetSettingValue(S.Id, v);
            OnChanged();
            OnChanged(nameof(NumberText));
        }
    }
    public string NumberText => Number.ToString(S.Step >= 1 || S.Step == 0 && S.Max - S.Min >= 10 ? "0" : "0.##", L.Culture);

    public IReadOnlyList<Choice> Choices { get; }
    public Choice? Selected
    {
        get => Choices.FirstOrDefault(c => Equals(c.Value, _mod.SettingValue(S.Id) as string)) ?? Choices.FirstOrDefault();
        set
        {
            if (value?.Value is not string v) return;
            _mod.SetSettingValue(S.Id, v);
            OnChanged();
        }
    }

    public string Text
    {
        get => _mod.SettingValue(S.Id) as string ?? "";
        set { _mod.SetSettingValue(S.Id, value.Length > 2000 ? value[..2000] : value); OnChanged(); }
    }
}

// A mod of the catalogue in Settings' "Get mods": who made it, what it does, and its button (Install, Update, Installed).
public sealed class CatalogueItemViewModel : Observable
{
    private readonly ModsViewModel _owner;
    private bool _busy;
    private string? _progress;

    public CatalogueItemViewModel(ModsViewModel owner, ModCatalogueEntry entry)
    {
        _owner = owner;
        Entry = entry;
        InstallCommand = new RelayCommand(() => _ = _owner.InstallFromCatalogue(this), () => CanInstall);
    }

    public ModCatalogueEntry Entry { get; }
    public string Name => Entry.Name;
    public string Description => Entry.Text;
    public bool HasDescription => Description.Length > 0;
    public string Line => string.Join(" · ", new[] { Entry.Official ? L.T("Official") : L.T("Certified"), Entry.Author is { } a ? L.F("by {0}", a) : null,
        Entry.Version }.Where(s => !string.IsNullOrEmpty(s)));
    public ICommand InstallCommand { get; }

    private ModRuntime? Installed => _owner.Installed(Entry.Id);
    public bool CanInstall => !_busy && (Installed is not { } m || ModCatalogue.IsNewer(Entry.Version, m.Manifest.Version));
    public string ActionText => _progress ?? (Installed is not { } m ? L.T("Install")
        : ModCatalogue.IsNewer(Entry.Version, m.Manifest.Version) ? L.F("Update to {0}", Entry.Version) : L.T("Installed|f"));

    internal bool Busy
    {
        set
        {
            _busy = value;
            _progress = value ? L.T("Downloading…") : null;
            Refresh();
        }
    }

    internal void Progress(long done, long? total)
    {
        if (!_busy) return;
        _progress = total > 0 ? $"{done * 100 / total.Value} %" : L.T("Downloading…");
        OnChanged(nameof(ActionText));
    }

    public void Refresh()
    {
        OnChanged(nameof(ActionText), nameof(CanInstall));
        CommandManager.InvalidateRequerySuggested();
    }

    public override string ToString() => Name;
}
