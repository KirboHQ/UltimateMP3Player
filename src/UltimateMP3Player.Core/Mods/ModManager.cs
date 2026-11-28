namespace UltimateMP3Player.Core.Mods;

// The mods of this run: the installed ones (mods/<id>) and the folders a developer loaded, started unless switched off.
// Which are off, the folders and the permissions granted live in AppSettings (the same mods for every profile).
public sealed class ModManager : IDisposable
{
    private readonly AppSettings _s;
    private readonly IModHost _host;
    private readonly List<ModRuntime> _mods = new();
    private readonly Dictionary<ModRuntime, FileSystemWatcher> _watchers = new();

    public ModManager(AppSettings settings, IModHost host)
    {
        _s = settings;
        _host = host;
    }

    public IReadOnlyList<ModRuntime> Mods { get { lock (_mods) return _mods.ToList(); } }
    public IEnumerable<ModRuntime> Running => Mods.Where(m => m.Status == ModStatus.Running);

    public void LoadAll()
    {
        if (Directory.Exists(ModPackage.ModsDir))
            // (.new/.old: an install cut short)
            foreach (var dir in Directory.EnumerateDirectories(ModPackage.ModsDir).Where(d => !d.EndsWith(".new", StringComparison.Ordinal) && !d.EndsWith(".old", StringComparison.Ordinal)).OrderBy(d => d))
                Add(new ModRuntime(dir, false, _host));
        foreach (var dir in _s.ModFolders.ToList())
            if (Directory.Exists(dir)) Add(new ModRuntime(dir, true, _host));
        foreach (var m in Mods)
            if (!_s.ModsOff.Contains(m.Id)) m.Start();
    }

    private void Add(ModRuntime m)
    {
        // The same id twice (installed and loaded from a folder): the folder wins, it's the one being worked on.
        lock (_mods)
        {
            if (_mods.FirstOrDefault(x => x.Id == m.Id) is { } twin)
            {
                if (!m.IsDev) return;
                twin.Stop();
                _mods.Remove(twin);
            }
            m.Granted = _s.ModGranted.TryGetValue(m.Id, out var g) ? g.ToHashSet() : new();
            _mods.Add(m);
        }
        if (m.IsDev) Watch(m);
    }

    // What a new or updated mod asks for that wasn't granted yet (the app asks the user about these before installing).
    public IReadOnlyList<string> NewPermissions(ModManifest m) =>
        m.Permissions.Except(_s.ModGranted.TryGetValue(m.Id, out var g) ? g : new List<string>()).ToList();

    // After the user said yes: installs (or updates) and starts it. keepOff: an update by itself leaves a mod switched off as it was.
    public ModRuntime Install(string file, bool keepOff = false)
    {
        var peek = ModPackage.Peek(file);
        var old = Mods.FirstOrDefault(x => x.Id == peek.Id && !x.IsDev);
        old?.Stop();
        ModManifest m;
        try { m = ModPackage.Install(file); }
        catch
        {
            if (old != null && IsEnabled(old)) old.Start();
            throw;
        }
        lock (_mods) if (old != null) _mods.Remove(old);
        Grant(m);
        var mod = new ModRuntime(Path.Combine(ModPackage.ModsDir, m.Id), false, _host);
        Add(mod);
        bool start = !keepOff || IsEnabled(mod);
        if (start) _s.ModsOff.Remove(m.Id);
        _s.Save();
        if (start) mod.Start();
        return mod;
    }

    // A developer's folder, loaded as it is and loaded again when its files change.
    public ModRuntime AddFolder(string folder)
    {
        var m = ModManifest.Read(folder);
        if (!_s.ModFolders.Contains(folder)) _s.ModFolders.Add(folder);
        Grant(m);
        var mod = new ModRuntime(folder, true, _host);
        Add(mod);
        _s.ModsOff.Remove(m.Id);
        _s.Save();
        mod.Start();
        return mod;
    }

    private void Grant(ModManifest m)
    {
        _s.ModGranted[m.Id] = m.Permissions.ToList();
    }

    public void SetEnabled(ModRuntime m, bool on)
    {
        if (on) { _s.ModsOff.Remove(m.Id); m.Start(); }
        else
        {
            if (!_s.ModsOff.Contains(m.Id)) _s.ModsOff.Add(m.Id);
            m.Stop();
        }
        _s.Save();
    }

    public bool IsEnabled(ModRuntime m) => !_s.ModsOff.Contains(m.Id);

    public void Remove(ModRuntime m, bool data)
    {
        m.Stop();
        lock (_mods) _mods.Remove(m);
        if (_watchers.Remove(m, out var w)) w.Dispose();
        if (m.IsDev) _s.ModFolders.Remove(m.Folder);
        else ModPackage.Remove(m.Id, data);
        if (data && m.IsDev && Directory.Exists(m.DataDir)) Directory.Delete(m.DataDir, true);
        _s.ModsOff.Remove(m.Id);
        _s.ModGranted.Remove(m.Id);
        _s.Save();
        _host.Changed(m);
    }

    // Stopped and started again from its files (a developer's change, or a mod that failed).
    public ModRuntime Reload(ModRuntime m)
    {
        m.Stop();
        var fresh = new ModRuntime(m.Folder, m.IsDev, _host);
        lock (_mods)
        {
            var i = _mods.IndexOf(m);
            if (i < 0) return m;
            _mods[i] = fresh;
        }
        if (_watchers.Remove(m, out var w)) { w.Dispose(); Watch(fresh); }
        fresh.Granted = _s.ModGranted.TryGetValue(fresh.Id, out var g) ? g.ToHashSet() : new();
        // A permission added to mod.json while working on it is granted only through "Load a folder" again.
        if (IsEnabled(fresh)) fresh.Start();
        _host.Changed(fresh);
        return fresh;
    }

    // Library's import menu: every running mod's importers that can be chosen there.
    public IReadOnlyList<ModImporter> Importers => Running.SelectMany(m => m.Importers).Where(i => i.Run is Jint.Native.Function.Function).ToList();

    // A file dropped or opened with the app: the importer that declared its type, if any.
    public ModImporter? OpenerOf(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length == 0 ? null : Running.SelectMany(m => m.Importers)
            .FirstOrDefault(i => i.Open is Jint.Native.Function.Function && i.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase));
    }

    // A link pasted in the app (a beatmap's page): the mod that takes links of that site, if any (before the downloads try it).
    public ModLink? LinkOf(string url) => Running.SelectMany(m => m.Links).FirstOrDefault(l => l.Matches(url));
    // The mods' searches (ump.search.add), for the search box's words.
    public IEnumerable<ModSearch> Searches => Running.SelectMany(m => m.Searches);

    // A song gone from the library: what each mod kept for it goes too.
    public void ForgetSong(string id)
    {
        foreach (var m in Mods) m.ForgetSong(id);
    }

    public void Emit(string name, object? arg = null)
    {
        foreach (var m in Running) m.Emit(name, arg);
    }

    public IEnumerable<(ModRuntime Mod, ModMenuEntry Entry)> SongEntries(ModSong song)
    {
        foreach (var m in Running.Where(m => m.HasSongEntries))
            foreach (var e in m.SongEntries(song, 40))
                yield return (m, e);
    }

    private void Watch(ModRuntime m)
    {
        try
        {
            var w = new FileSystemWatcher(m.Folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName };
            Timer? wait = null;
            void Changed(object? _, FileSystemEventArgs e)
            {
                // Editors write a file in several steps: once things are quiet for half a second.
                wait?.Dispose();
                wait = new Timer(_ =>
                {
                    var now = Mods.FirstOrDefault(x => x.Folder == m.Folder && x.IsDev);
                    if (now != null) Reload(now);
                }, null, 500, Timeout.Infinite);
            }
            w.Changed += Changed;
            w.Created += Changed;
            w.Deleted += Changed;
            w.Renamed += Changed;
            w.EnableRaisingEvents = true;
            _watchers[m] = w;
        }
        catch { } // no watching where the system can't: Reload by hand
    }

    public void Dispose()
    {
        foreach (var w in _watchers.Values) w.Dispose();
        _watchers.Clear();
        foreach (var m in Mods) m.Stop();
    }
}
