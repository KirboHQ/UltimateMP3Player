namespace UltimateMP3Player.Core;

public sealed class ProfileInfo
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#7C5CFF";
    public bool HasAvatar { get; set; }
    public int AvatarVersion { get; set; }

    public string AvatarPath => Path.Combine(AppPaths.ProfileDir(Id), "avatar.jpg");
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : char.ToUpper(Name.Trim()[0]).ToString();

    public static readonly string[] Palette =
        { "#7C5CFF", "#FF4FA3", "#1DB954", "#FF7A1A", "#1E9BFF", "#E5484D", "#12B5A5", "#F5B400" };
}

public sealed class ProfileList
{
    public List<ProfileInfo> Profiles { get; set; } = new();

    public static ProfileList Load() => JsonStore.Load<ProfileList>(AppPaths.ProfilesFile);

    public void Save()
    {
        try { JsonStore.Save(AppPaths.ProfilesFile, this); } catch { }
    }
}

public sealed class Playlist
{
    public const string FavoritesId = "favorites";

    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "";
    public List<string> Tracks { get; set; } = new();
    public bool HasCover { get; set; }
    public int CoverVersion { get; set; }
    public string? SourceUrl { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;

    public bool IsFavorites => Id == FavoritesId;
}

public sealed class HistoryEntry
{
    public string TrackId { get; set; } = "";
    public DateTime At { get; set; }
}

public sealed class EqPreset
{
    public string Name { get; set; } = "";
    public double[] Gains { get; set; } = new double[Equalizer.BandCount];
}

public sealed class EqSettings
{
    public bool Enabled { get; set; }
    public string Preset { get; set; } = "Piatto";
    public double[] Gains { get; set; } = new double[Equalizer.BandCount];
    public List<EqPreset> Custom { get; set; } = new();
}

public enum RepeatMode { Off, All, One }

// What was playing, saved so it survives a restart.
public sealed class QueueState
{
    public List<string> Context { get; set; } = new();
    public string? ContextId { get; set; }
    public string? ContextName { get; set; }
    public List<string> UpNext { get; set; } = new();
    public List<string> Plan { get; set; } = new();
    public List<string> History { get; set; } = new();
    public string? Current { get; set; }
    public bool CurrentQueued { get; set; }
    public double Position { get; set; }

    public void Forget(ISet<string> ids)
    {
        Context.RemoveAll(ids.Contains);
        UpNext.RemoveAll(ids.Contains);
        Plan.RemoveAll(ids.Contains);
        History.RemoveAll(ids.Contains);
        if (Current != null && ids.Contains(Current))
        {
            Current = null;
            Position = 0;
        }
    }
}

public sealed class ProfileData
{
    public List<Playlist> Playlists { get; set; } = new();
    public List<HistoryEntry> History { get; set; } = new();
    public EqSettings Eq { get; set; } = new();
    public double Volume { get; set; } = 0.8;
    public bool Muted { get; set; }
    public bool Shuffle { get; set; }
    public RepeatMode Repeat { get; set; }
    public bool Normalize { get; set; } = true;
    public bool Crossfade { get; set; }
    public int CrossfadeSeconds { get; set; } = 6;
    public bool ShowVideo { get; set; } = true;
    public string? LastTrack { get; set; }
    public QueueState? Queue { get; set; }
    public string LibrarySort { get; set; } = "added";
    // Theme id ("ultimate", "pink"...) or "profile".
    public string Theme { get; set; } = "ultimate";
}

// One profile's playlists, history and preferences.
public sealed class Profile
{
    private readonly object _lock = new();
    private readonly DebouncedSaver _saver;

    public ProfileInfo Info { get; }
    public ProfileData Data { get; }

    public event Action<Playlist>? PlaylistChanged;
    public event Action? PlaylistsChanged;

    private static readonly Dictionary<string, Profile> Open = new();

    private Profile(ProfileInfo info, ProfileData data)
    {
        Info = info;
        Data = data;
        _saver = new DebouncedSaver(SaveNow, 800);
        if (Data.Playlists.All(p => !p.IsFavorites))
            Data.Playlists.Insert(0, new Playlist { Id = Playlist.FavoritesId, Name = "Preferiti" });
        if (Data.Eq.Gains.Length != Equalizer.BandCount) Data.Eq.Gains = new double[Equalizer.BandCount];
    }

    private string FilePath => Path.Combine(AppPaths.ProfileDir(Info.Id), "data.json");

    // Stays loaded so running downloads keep filling its playlists.
    public static Profile Get(ProfileInfo info)
    {
        lock (Open)
        {
            if (Open.TryGetValue(info.Id, out var p)) return p;
            var data = JsonStore.Load<ProfileData>(Path.Combine(AppPaths.ProfileDir(info.Id), "data.json"));
            p = new Profile(info, data);
            Open[info.Id] = p;
            return p;
        }
    }

    public static void FlushAll()
    {
        lock (Open)
            foreach (var p in Open.Values) p.Flush();
    }

    private bool _deleted;

    // Removes the profile's data; songs stay in the library.
    public static void Delete(string id)
    {
        lock (Open)
        {
            if (Open.TryGetValue(id, out var p)) p._deleted = true;
            Open.Remove(id);
        }
        try { Directory.Delete(AppPaths.ProfileDir(id), true); } catch { }
    }

    public Playlist Favorites
    {
        get { lock (_lock) return Data.Playlists.First(p => p.IsFavorites); }
    }

    public List<Playlist> PlaylistsSnapshot()
    {
        lock (_lock) return Data.Playlists.ToList();
    }

    public Playlist? GetPlaylist(string id)
    {
        lock (_lock) return Data.Playlists.FirstOrDefault(p => p.Id == id);
    }

    public string PlaylistCover(Playlist p) => Path.Combine(AppPaths.ProfileDir(Info.Id), "pl-" + p.Id + ".jpg");

    public Playlist CreatePlaylist(string name, string? sourceUrl = null)
    {
        var p = new Playlist { Name = UniqueName(name), SourceUrl = sourceUrl };
        lock (_lock) Data.Playlists.Add(p);
        Save();
        PlaylistsChanged?.Invoke();
        return p;
    }

    private string UniqueName(string name)
    {
        name = string.IsNullOrWhiteSpace(name) ? L.T("Nuova playlist") : name.Trim();
        lock (_lock)
        {
            var n = name;
            for (int i = 2; Data.Playlists.Any(p => string.Equals(p.Name, n, StringComparison.CurrentCultureIgnoreCase)); i++)
                n = $"{name} ({i})";
            return n;
        }
    }

    public void Rename(Playlist p, string name)
    {
        if (p.IsFavorites || string.IsNullOrWhiteSpace(name) || name.Trim() == p.Name) return;
        p.Name = UniqueName(name);
        Save();
        PlaylistChanged?.Invoke(p);
        PlaylistsChanged?.Invoke();
    }

    public void DeletePlaylist(Playlist p)
    {
        if (p.IsFavorites) return;
        lock (_lock) Data.Playlists.Remove(p);
        try { File.Delete(PlaylistCover(p)); } catch { }
        Save();
        PlaylistsChanged?.Invoke();
    }

    public bool Contains(Playlist p, string trackId)
    {
        lock (_lock) return p.Tracks.Contains(trackId);
    }

    // Adds a track once, at index or at the end.
    public bool AddTrack(Playlist p, string trackId, int? index = null)
    {
        lock (_lock)
        {
            if (p.Tracks.Contains(trackId)) return false;
            if (index is int i && i >= 0 && i < p.Tracks.Count) p.Tracks.Insert(i, trackId);
            else p.Tracks.Add(trackId);
        }
        Save();
        PlaylistChanged?.Invoke(p);
        return true;
    }

    public void RemoveTrack(Playlist p, string trackId) => RemoveTracks(p, new[] { trackId });

    public void RemoveTracks(Playlist p, IEnumerable<string> trackIds)
    {
        var set = trackIds.ToHashSet();
        int removed;
        lock (_lock) removed = p.Tracks.RemoveAll(set.Contains);
        if (removed == 0) return;
        Save();
        PlaylistChanged?.Invoke(p);
    }

    // Appends new tracks, returns how many.
    public int AddTracks(Playlist p, IEnumerable<string> trackIds)
    {
        int added = 0;
        lock (_lock)
            foreach (var id in trackIds)
                if (!p.Tracks.Contains(id)) { p.Tracks.Add(id); added++; }
        if (added == 0) return 0;
        Save();
        PlaylistChanged?.Invoke(p);
        return added;
    }

    public void MoveTrack(Playlist p, int from, int to)
    {
        lock (_lock)
        {
            if (from < 0 || from >= p.Tracks.Count || to < 0 || to >= p.Tracks.Count || from == to) return;
            var id = p.Tracks[from];
            p.Tracks.RemoveAt(from);
            p.Tracks.Insert(to, id);
        }
        Save();
        PlaylistChanged?.Invoke(p);
    }

    public void SetPlaylistCover(Playlist p, bool has)
    {
        p.HasCover = has;
        p.CoverVersion++;
        Save();
        PlaylistChanged?.Invoke(p);
    }

    // Drops every reference to deleted songs.
    public void ForgetTracks(IEnumerable<string> trackIds)
    {
        var set = trackIds.ToHashSet();
        List<Playlist> touched;
        lock (_lock)
        {
            touched = Data.Playlists.Where(p => p.Tracks.Any(set.Contains)).ToList();
            foreach (var p in touched) p.Tracks.RemoveAll(set.Contains);
            Data.History.RemoveAll(h => set.Contains(h.TrackId));
            Data.Queue?.Forget(set);
            if (Data.LastTrack != null && set.Contains(Data.LastTrack)) Data.LastTrack = null;
        }
        Save();
        foreach (var p in touched) PlaylistChanged?.Invoke(p);
    }

    public void AddHistory(string trackId)
    {
        lock (_lock)
        {
            Data.History.Add(new HistoryEntry { TrackId = trackId, At = DateTime.Now });
            if (Data.History.Count > 500) Data.History.RemoveRange(0, Data.History.Count - 500);
        }
        Save();
    }

    public List<string> RecentTracks(int count)
    {
        lock (_lock)
        {
            var seen = new HashSet<string>();
            var list = new List<string>();
            for (int i = Data.History.Count - 1; i >= 0 && list.Count < count; i--)
                if (seen.Add(Data.History[i].TrackId)) list.Add(Data.History[i].TrackId);
            return list;
        }
    }

    public void Save() => _saver.Request();

    public void Flush() => _saver.Flush();

    private void SaveNow()
    {
        if (_deleted) return;
        string json;
        lock (_lock) json = System.Text.Json.JsonSerializer.Serialize(Data, JsonStore.Options);
        Directory.CreateDirectory(AppPaths.ProfileDir(Info.Id));
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(FilePath)) File.Replace(tmp, FilePath, FilePath + ".bak", true);
        else File.Move(tmp, FilePath);
    }
}
