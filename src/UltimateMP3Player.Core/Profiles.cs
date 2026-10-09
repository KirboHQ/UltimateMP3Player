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
    // Ids of the profile's tags put on the playlist itself.
    public List<string> Tags { get; set; } = new();

    public bool IsFavorites => Id == FavoritesId;
}

// A label of the profile, with its colour (like a Discord role).
public sealed class Tag
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#7C5CFF";

    public static readonly string[] Palette =
    {
        "#E5484D", "#FF7A1A", "#F5B400", "#1DB954", "#12B5A5", "#1E9BFF", "#3E63DD", "#7C5CFF",
        "#B45CFF", "#FF4FA3", "#E54666", "#A18072", "#8B93A7", "#5EEAD4", "#A3E635", "#FDE047",
    };
}

public sealed class HistoryEntry
{
    public string TrackId { get; set; } = "";
    public DateTime At { get; set; }
}

// How much a song has been listened to on this profile: a play counts once three quarters of the song have been heard,
// the time is the real time it played.
public sealed class TrackStats
{
    public int Plays { get; set; }
    public double Seconds { get; set; }
    public DateTime? Last { get; set; }
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
    // "Next up" emptied by hand: the list doesn't refill it.
    public bool Cleared { get; set; }
    // Shuffle: this pass through the list in random order (the queue is dealt from it).
    public List<string> Round { get; set; } = new();
    // The list is songs suggested online, it grows as they play.
    public bool Radio { get; set; }

    public void Forget(ISet<string> ids)
    {
        Context.RemoveAll(ids.Contains);
        UpNext.RemoveAll(ids.Contains);
        Plan.RemoveAll(ids.Contains);
        History.RemoveAll(ids.Contains);
        Round.RemoveAll(ids.Contains);
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
    // Playback speed 0.5-2; SpeedPitch = the key follows it (like a record), otherwise it stays.
    public double Speed { get; set; } = 1;
    public bool SpeedPitch { get; set; }
    public bool ShowVideo { get; set; } = true;
    // The song page shows the lyrics instead of the cover or the video (when the song has them).
    public bool ShowLyrics { get; set; }
    // Automatic queue: a song started from a list brings the rest of it, and the queue refills at every new song.
    public bool AutoQueue { get; set; } = true;
    // "Next up" panel folded away on the song page.
    public bool QueueHidden { get; set; }
    public string? LastTrack { get; set; }
    public QueueState? Queue { get; set; }
    public string LibrarySort { get; set; } = "added";
    // The library pages show only the songs saved on the device (not the ones in the cloud).
    public bool LibraryOnDevice { get; set; }
    // Theme id ("ultimate", "pink"...) or "profile".
    public string Theme { get; set; } = "ultimate";
    public List<Tag> Tags { get; set; } = new();
    // Song id → ids of its tags.
    public Dictionary<string, List<string>> TrackTags { get; set; } = new();
    // Song id → how much it was listened to, counted since StatsSince.
    public Dictionary<string, TrackStats> Stats { get; set; } = new();
    public DateTime? StatsSince { get; set; }
    // Songs not in the library (suggested, heard from a link or in a room) the saved queue or the recently played refer to.
    public List<RadioSong> Radio { get; set; } = new();
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
    // Tags created, edited or deleted.
    public event Action? TagsChanged;
    // Tags added to or removed from these songs.
    public event Action<IReadOnlyCollection<string>>? TrackTagsChanged;

    private static readonly Dictionary<string, Profile> Open = new();

    private Profile(ProfileInfo info, ProfileData data)
    {
        Info = info;
        Data = data;
        _saver = new DebouncedSaver(SaveNow, 800);
        if (Data.Playlists.All(p => !p.IsFavorites))
            Data.Playlists.Insert(0, new Playlist { Id = Playlist.FavoritesId, Name = "Preferiti" });
        if (Data.Eq.Gains.Length != Equalizer.BandCount) Data.Eq.Gains = new double[Equalizer.BandCount];
        DropUnknownTags();
        SeedStats();
    }

    // Before 3.3 only the recent history existed: its plays are where the statistics start from.
    private void SeedStats()
    {
        if (Data.StatsSince != null) return;
        foreach (var g in Data.History.GroupBy(h => h.TrackId))
            Data.Stats[g.Key] = new TrackStats { Plays = g.Count(), Last = g.Max(h => h.At) };
        Data.StatsSince = Data.History.Count > 0 ? Data.History.Min(h => h.At) : DateTime.Now;
    }

    // References to tags that no longer exist (e.g. a file edited by hand).
    private void DropUnknownTags()
    {
        var known = Data.Tags.Select(t => t.Id).ToHashSet();
        foreach (var list in Data.TrackTags.Values) list.RemoveAll(id => !known.Contains(id));
        foreach (var k in Data.TrackTags.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) Data.TrackTags.Remove(k);
        foreach (var p in Data.Playlists) p.Tags.RemoveAll(id => !known.Contains(id));
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
            foreach (var id in set)
            {
                Data.TrackTags.Remove(id);
                Data.Stats.Remove(id);
            }
            Data.Queue?.Forget(set);
            if (Data.LastTrack != null && set.Contains(Data.LastTrack)) Data.LastTrack = null;
        }
        Save();
        foreach (var p in touched) PlaylistChanged?.Invoke(p);
    }

    // ------------------------------------------------------------------ tags

    public List<Tag> TagsSnapshot()
    {
        lock (_lock) return Data.Tags.ToList();
    }

    public Tag? GetTag(string id)
    {
        lock (_lock) return Data.Tags.FirstOrDefault(t => t.Id == id);
    }

    public Tag CreateTag(string name, string color)
    {
        var t = new Tag { Name = UniqueTagName(name, null), Color = color };
        lock (_lock) Data.Tags.Add(t);
        Save();
        TagsChanged?.Invoke();
        return t;
    }

    public void UpdateTag(Tag t, string name, string color)
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(name)) t.Name = UniqueTagName(name, t);
            t.Color = color;
        }
        Save();
        TagsChanged?.Invoke();
    }

    // Gone from every song and playlist too.
    public void DeleteTag(Tag t)
    {
        List<string> songs;
        lock (_lock)
        {
            Data.Tags.Remove(t);
            songs = Data.TrackTags.Where(kv => kv.Value.Remove(t.Id)).Select(kv => kv.Key).ToList();
            foreach (var id in songs.Where(id => Data.TrackTags[id].Count == 0)) Data.TrackTags.Remove(id);
            foreach (var p in Data.Playlists) p.Tags.Remove(t.Id);
        }
        Save();
        TagsChanged?.Invoke();
        if (songs.Count > 0) TrackTagsChanged?.Invoke(songs);
    }

    private string UniqueTagName(string name, Tag? self)
    {
        name = string.IsNullOrWhiteSpace(name) ? L.T("Nuovo tag") : name.Trim();
        var n = name;
        for (int i = 2; Data.Tags.Any(x => x != self && string.Equals(x.Name, n, StringComparison.CurrentCultureIgnoreCase)); i++)
            n = $"{name} ({i})";
        return n;
    }

    public IReadOnlyList<string> TagsOf(string trackId)
    {
        lock (_lock) return Data.TrackTags.TryGetValue(trackId, out var list) ? list.ToList() : Array.Empty<string>();
    }

    public int CountTagged(string tagId)
    {
        lock (_lock) return Data.TrackTags.Values.Count(l => l.Contains(tagId));
    }

    // Puts the tag on the songs (or takes it off); returns how many changed.
    public int SetTag(IEnumerable<string> trackIds, string tagId, bool on)
    {
        var changed = new List<string>();
        lock (_lock)
        {
            foreach (var id in trackIds.Distinct())
            {
                Data.TrackTags.TryGetValue(id, out var list);
                bool has = list?.Contains(tagId) == true;
                if (has == on) continue;
                if (on)
                {
                    if (list == null) Data.TrackTags[id] = list = new List<string>();
                    list.Add(tagId);
                }
                else
                {
                    list!.Remove(tagId);
                    if (list.Count == 0) Data.TrackTags.Remove(id);
                }
                changed.Add(id);
            }
        }
        if (changed.Count == 0) return 0;
        Save();
        TrackTagsChanged?.Invoke(changed);
        return changed.Count;
    }

    public void SetPlaylistTag(Playlist p, string tagId, bool on)
    {
        lock (_lock)
        {
            if (p.Tags.Contains(tagId) == on) return;
            if (on) p.Tags.Add(tagId);
            else p.Tags.Remove(tagId);
        }
        Save();
        PlaylistChanged?.Invoke(p);
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

    public TrackStats? StatsOf(string trackId)
    {
        lock (_lock) return Data.Stats.TryGetValue(trackId, out var s) ? s : null;
    }

    public Dictionary<string, TrackStats> StatsSnapshot()
    {
        lock (_lock) return Data.Stats.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    // Time listened to a song, and a play when it just counted. save = false: the caller saves now and then.
    public void AddListening(string trackId, double seconds, bool play, bool save = true)
    {
        lock (_lock)
        {
            if (!Data.Stats.TryGetValue(trackId, out var s)) Data.Stats[trackId] = s = new TrackStats();
            s.Seconds += seconds;
            if (play) s.Plays++;
            s.Last = DateTime.Now;
        }
        if (save) Save();
    }

    // The last songs heard, newest first, each once. exists: the ones that can still be shown (songs no longer around are
    // skipped, not counted).
    public List<string> RecentTracks(int count, Func<string, bool>? exists = null)
    {
        lock (_lock)
        {
            var seen = new HashSet<string>();
            var list = new List<string>();
            for (int i = Data.History.Count - 1; i >= 0 && list.Count < count; i--)
                if (seen.Add(Data.History[i].TrackId) && (exists == null || exists(Data.History[i].TrackId))) list.Add(Data.History[i].TrackId);
            return list;
        }
    }

    // A song heard without being in the library and then kept in it: its listens (recently played, statistics) are the
    // library song's.
    public void RenameHistory(string oldId, string newId)
    {
        lock (_lock)
        {
            foreach (var h in Data.History)
                if (h.TrackId == oldId) h.TrackId = newId;
            if (Data.Stats.Remove(oldId, out var s))
            {
                if (Data.Stats.TryGetValue(newId, out var had))
                {
                    had.Plays += s.Plays;
                    had.Seconds += s.Seconds;
                    if (s.Last > had.Last || had.Last == null) had.Last = s.Last;
                }
                else Data.Stats[newId] = s;
            }
        }
        Save();
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
