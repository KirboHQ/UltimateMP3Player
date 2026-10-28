using System.Text.Json.Serialization;

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
    // How its page shows it ("title", "artist"…); null = its own order (Tracks, arranged by dragging the songs), which the
    // other orders never change: choosing it again brings it back.
    public string? Sort { get; set; }

    // (the other device's Favorites, while connected to it, are "ph-favorites" / "pc-favorites": SharedLibrary)
    public bool IsFavorites => Id.EndsWith(FavoritesId, StringComparison.Ordinal);

    public Playlist Clone() => new()
    {
        Id = Id, Name = Name, Tracks = Tracks.ToList(), HasCover = HasCover, CoverVersion = CoverVersion, SourceUrl = SourceUrl, Created = Created,
        Tags = Tags.ToList(), Sort = Sort,
    };
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
    // (masked in memory: Sealed)
    private int _plays = Sealed.Mask(0);
    private long _seconds = Sealed.Mask(0.0);
    public int Plays { get => Sealed.Mask(_plays); set => _plays = Sealed.Mask(value); }
    public double Seconds { get => Sealed.Unmask(_seconds); set => _seconds = Sealed.Mask(value); }
    public DateTime? Last { get; set; }
}

// What stats.dat holds (sealed: Sealed).
public sealed class SealedStats
{
    public Dictionary<string, TrackStats> Stats { get; set; } = new();
    public Dictionary<string, int> Imported { get; set; } = new();
}

public sealed class EqPreset
{
    public string Name { get; set; } = "";
    public double[] Gains { get; set; } = new double[Equalizer.BandCount];
}

public sealed class EqSettings
{
    public bool Enabled { get; set; }
    public string Preset { get; set; } = "Flat";
    public double[] Gains { get; set; } = new double[Equalizer.BandCount];
    public List<EqPreset> Custom { get; set; } = new();
}

public enum RepeatMode { Off, All, One }

// Which songs the library pages and the playlists show: all of them, only the ones saved on the device, only the ones in the cloud.
public enum StorageFilter { All, Device, Cloud }

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
    // Before the next song comes in, the one playing glides to its BPM (when both are known and close enough).
    public bool BpmMatch { get; set; }
    public int BpmMatchSeconds { get; set; } = 8;
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
    // The library pages and the playlists show all the songs, only the ones saved on the device or only the ones in the cloud.
    public StorageFilter StorageFilter { get; set; }
    // Before 3.6.2 the only filter was "on the device" (read once into StorageFilter, then no longer written).
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool LibraryOnDevice { get; set; }
    // Theme id ("ultimate", "pink"...) or "profile".
    public string Theme { get; set; } = "ultimate";
    // Light or dark look: ThemeColors.System (follows the device), Light or Dark.
    public string Appearance { get; set; } = ThemeColors.System;
    public List<Tag> Tags { get; set; } = new();
    // Song id → ids of its tags.
    public Dictionary<string, List<string>> TrackTags { get; set; } = new();
    // Song id → how much it was listened to, counted since StatsSince: saved apart and sealed (stats.dat, Profile.LoadStats).
    [JsonIgnore] public Dictionary<string, TrackStats> Stats { get; set; } = new();
    // Plays already imported from other services, per "source|song|month" (Profile.AddImported): in stats.dat too.
    [JsonIgnore] public Dictionary<string, int> ImportCounts { get; set; } = new();
    // (where 4.2 and before kept Stats: read once)
    [JsonPropertyName("Stats")] public Dictionary<string, TrackStats>? OldStats { get; set; }
    public DateTime? StatsSince { get; set; }
    // Songs not in the library (suggested, heard from a link or in a room) the saved queue or the recently played refer to.
    public List<RadioSong> Radio { get; set; } = new();
    // The times already imported from other services (Spotify's history): not counted twice.
    public List<ImportedRange> Imported { get; set; } = new();
    // Library › Artists: the ones followed, their latest releases, when they were last looked for.
    public List<FollowedArtist> Artists { get; set; } = new();
    public List<Release> Releases { get; set; } = new();
    public DateTime? ReleasesChecked { get; set; }
    // Library › Radio: the internet radios saved, the latest first.
    public List<Station> Stations { get; set; } = new();
    // The last one played, saved or not: the player finds it again at the next start.
    public Station? LastStation { get; set; }
    // Settings › Integrations: the accounts this profile's plays go to (Scrobbler).
    public string? LastFmUser { get; set; }
    public string? LastFmSession { get; set; }
    public string? ListenBrainzUser { get; set; }
    public string? ListenBrainzToken { get; set; }
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
            Data.Playlists.Insert(0, new Playlist { Id = Playlist.FavoritesId, Name = "Favorites" });
        if (Data.Eq.Gains.Length != Equalizer.BandCount) Data.Eq.Gains = new double[Equalizer.BandCount];
        if (Data.LibraryOnDevice)
        {
            Data.StorageFilter = StorageFilter.Device;
            Data.LibraryOnDevice = false;
        }
        DropUnknownTags();
        SeedStats();
    }

    private const string StatsFile = "stats.dat";

    // The all-time counts, from stats.dat. A profile saved by 4.2 or before had them in data.json: taken once, unless this
    // profile was sealed already (its months in stats/ are): then plain numbers there are someone's edit.
    private static void LoadStats(string id, ProfileData data)
    {
        var dir = AppPaths.ProfileDir(id);
        var file = Path.Combine(dir, StatsFile);
        if (Sealed.Load<SealedStats>(file) is { } kept)
        {
            data.Stats = kept.Stats;
            data.ImportCounts = kept.Imported;
        }
        else if (!File.Exists(file) && !File.Exists(file + ".bak")
                 && !(Directory.Exists(Path.Combine(dir, "stats")) && Directory.EnumerateFiles(Path.Combine(dir, "stats"), "*.dat").Any()))
            data.Stats = data.OldStats ?? new();
        data.OldStats = null;
    }

    // Before 3.3 only the recent history existed: its plays are where the statistics start from.
    private void SeedStats()
    {
        if (Data.StatsSince != null) return;
        // (sealed counts are there already: a date gone from data.json is no reason to count the history, easy to edit, again)
        if (!File.Exists(Path.Combine(AppPaths.ProfileDir(Info.Id), StatsFile)))
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

    // The other device's profile while connected to it (SharedLibrary): kept in memory only, its playlist pictures in
    // coverDir. Changed through the usual methods; SharedLibrary sends the changes there.
    private string? _coverDir;
    // (what the ids of its new playlists and tags start with, like the ones it got from there)
    private string _prefix = "";
    public bool IsMirror => _coverDir != null;

    public static Profile Mirror(ProfileInfo info, ProfileData data, string coverDir, string prefix)
    {
        var p = new Profile(info, data) { _coverDir = coverDir, _prefix = prefix };
        Directory.CreateDirectory(coverDir);
        return p;
    }

    // Stays loaded so running downloads keep filling its playlists.
    public static Profile Get(ProfileInfo info)
    {
        lock (Open)
        {
            if (Open.TryGetValue(info.Id, out var p)) return p;
            var data = JsonStore.Load<ProfileData>(Path.Combine(AppPaths.ProfileDir(info.Id), "data.json"));
            LoadStats(info.Id, data);
            // (3.7 and before saved the built-in presets by their Italian name)
            data.Eq.Preset = L.KeyOf(data.Eq.Preset);
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
            if (Open.TryGetValue(id, out var p))
            {
                p._deleted = true;
                p._log?.Close();
            }
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

    public string PlaylistCover(Playlist p) => Path.Combine(_coverDir ?? AppPaths.ProfileDir(Info.Id), "pl-" + p.Id + ".jpg");

    // ------------------------------------------------------------------ the other device's changes (Connect phone)

    // A playlist as the other device says it is now (its own, or one of this profile changed there): put in place of this
    // one's with the same id, or added. The same object stays, so what shows it follows.
    public Playlist PutPlaylist(Playlist state)
    {
        Playlist? p;
        bool added = false, renamed = false;
        lock (_lock)
        {
            p = Data.Playlists.FirstOrDefault(x => x.Id == state.Id);
            if (p == null)
            {
                p = state.Clone();
                Data.Playlists.Add(p);
                added = true;
            }
            else
            {
                renamed = p.Name != state.Name;
                p.Name = state.Name;
                p.Tracks = state.Tracks.Distinct().ToList();
                p.HasCover = state.HasCover;
                p.CoverVersion = state.CoverVersion;
                p.SourceUrl = state.SourceUrl;
                p.Tags = state.Tags.ToList();
                p.Sort = state.Sort;
            }
        }
        Save();
        PlaylistChanged?.Invoke(p);
        if (added || renamed) PlaylistsChanged?.Invoke();
        return p;
    }

    public void RemovePlaylists(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        List<Playlist> gone;
        lock (_lock)
        {
            gone = Data.Playlists.Where(p => set.Contains(p.Id) && !p.IsFavorites).ToList();
            foreach (var p in gone) Data.Playlists.Remove(p);
        }
        if (gone.Count == 0) return;
        foreach (var p in gone)
            try { File.Delete(PlaylistCover(p)); } catch { }
        Save();
        PlaylistsChanged?.Invoke();
    }

    // The whole list of tags (the same objects kept by id); the songs and playlists lose the tags no longer there.
    public void PutTags(List<Tag> tags)
    {
        List<string> songs;
        lock (_lock)
        {
            var old = Data.Tags.ToDictionary(t => t.Id);
            Data.Tags = tags.Select(t =>
            {
                if (!old.TryGetValue(t.Id, out var o)) return new Tag { Id = t.Id, Name = t.Name, Color = t.Color };
                o.Name = t.Name;
                o.Color = t.Color;
                return o;
            }).ToList();
            var known = Data.Tags.Select(t => t.Id).ToHashSet();
            songs = Data.TrackTags.Where(kv => kv.Value.RemoveAll(id => !known.Contains(id)) > 0).Select(kv => kv.Key).ToList();
            foreach (var id in songs.Where(id => Data.TrackTags[id].Count == 0)) Data.TrackTags.Remove(id);
            foreach (var p in Data.Playlists) p.Tags.RemoveAll(id => !known.Contains(id));
        }
        Save();
        TagsChanged?.Invoke();
        if (songs.Count > 0) TrackTagsChanged?.Invoke(songs);
    }

    // The tags of these songs (an empty list: none); only tags of the profile are kept.
    public void PutTrackTags(IReadOnlyDictionary<string, List<string>> tags)
    {
        var changed = new List<string>();
        lock (_lock)
        {
            var known = Data.Tags.Select(t => t.Id).ToHashSet();
            foreach (var (id, list) in tags)
            {
                var now = list.Where(known.Contains).Distinct().ToList();
                var had = Data.TrackTags.GetValueOrDefault(id);
                if (had == null ? now.Count == 0 : had.SequenceEqual(now)) continue;
                if (now.Count == 0) Data.TrackTags.Remove(id);
                else Data.TrackTags[id] = now;
                changed.Add(id);
            }
        }
        if (changed.Count == 0) return;
        Save();
        TrackTagsChanged?.Invoke(changed);
    }

    // The mirror's history: replaced (the other device keeps the real one).
    public void PutHistory(List<HistoryEntry> history)
    {
        lock (_lock) Data.History = history;
    }

    public List<HistoryEntry> HistorySnapshot(int last)
    {
        lock (_lock) return Data.History.Skip(Math.Max(0, Data.History.Count - last)).ToList();
    }

    public Dictionary<string, List<string>> TrackTagsSnapshot()
    {
        lock (_lock) return Data.TrackTags.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
    }

    public Playlist CreatePlaylist(string name, string? sourceUrl = null)
    {
        var p = new Playlist { Id = _prefix + Ids.New(), Name = UniqueName(name), SourceUrl = sourceUrl };
        lock (_lock) Data.Playlists.Add(p);
        Save();
        PlaylistsChanged?.Invoke();
        return p;
    }

    private string UniqueName(string name)
    {
        name = string.IsNullOrWhiteSpace(name) ? L.T("New playlist") : name.Trim();
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

    // Something of the playlist set directly (how its page sorts it): saved, and said.
    public void Touch(Playlist p)
    {
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
        var t = new Tag { Id = _prefix + Ids.New(), Name = UniqueTagName(name, null), Color = color };
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
        name = string.IsNullOrWhiteSpace(name) ? L.T("New tag") : name.Trim();
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
        var now = DateTime.Now;
        lock (_lock)
        {
            if (!Data.Stats.TryGetValue(trackId, out var s)) Data.Stats[trackId] = s = new TrackStats();
            s.Seconds += seconds;
            if (play) s.Plays++;
            s.Last = now;
        }
        Log.Add(trackId, seconds, play, now);
        if (save) Save();
    }

    // Plays from another service (Spotify's history), song by song and month by month: an earlier import of that song in that
    // month counted so many, only the ones past them are added (the same files again: none; a longer export after a shorter
    // one: the difference). A song of the library adds them to the plays it has here. The ones added.
    // shortcut: counts, not single plays (Spotify's two shapes don't share their times), so a month split over two files
    // imported one at a time keeps only the larger half; keep the plays' times if that matters.
    public List<ImportedPlay> AddImported(string source, IReadOnlyList<ImportedPlay> plays)
    {
        var fresh = new List<ImportedPlay>();
        lock (_lock)
        {
            // (imports before 4.3 kept only the times they covered: the plays in them were counted then)
            var before = Data.Imported.Where(r => r.Source == source).ToList();
            var counts = Data.ImportCounts;
            foreach (var g in plays.GroupBy(p => source + "|" + SongMeta.Key(p.Title, p.Artist) + "|" + p.At.ToString("yyyy-MM")))
            {
                var list = g.OrderBy(p => p.At).ToList();
                int had = counts.TryGetValue(g.Key, out var n) ? n : list.Count(p => before.Any(r => p.At >= r.From && p.At <= r.To));
                if (list.Count > had) fresh.AddRange(list.Skip(had));
                counts[g.Key] = Math.Max(had, list.Count);
            }
            if (fresh.Count == 0) return fresh;
            foreach (var p in fresh.Where(p => p.InLibrary))
            {
                if (!Data.Stats.TryGetValue(p.Id, out var s)) Data.Stats[p.Id] = s = new TrackStats();
                s.Seconds += p.Seconds;
                if (p.Play) s.Plays++;
                if (s.Last == null || p.At > s.Last) s.Last = p.At;
            }
            var first = fresh.Min(p => p.At);
            if (Data.StatsSince == null || first < Data.StatsSince) Data.StatsSince = first;
        }
        Log.Import(fresh.Select(p => (p.At, p.Id, p.Title, p.Artist, p.Url, p.Seconds, p.Play)));
        Save();
        return fresh;
    }

    // ------------------------------------------------------------------ followed artists (Library › Artists)

    public event Action? ArtistsChanged;

    public List<FollowedArtist> ArtistsSnapshot()
    {
        lock (_lock) return Data.Artists.ToList();
    }

    public FollowedArtist? FollowedArtist(string name)
    {
        var key = Text.Normalize(name);
        lock (_lock) return Data.Artists.FirstOrDefault(a => Text.Normalize(a.Name) == key);
    }

    public void Follow(FollowedArtist artist)
    {
        lock (_lock)
        {
            var key = Text.Normalize(artist.Name);
            if (Data.Artists.Any(a => Text.Normalize(a.Name) == key)) return;
            Data.Artists.Add(artist);
            // (its releases are looked for at once)
            Data.ReleasesChecked = null;
        }
        Save();
        ArtistsChanged?.Invoke();
    }

    public void Unfollow(string name)
    {
        var key = Text.Normalize(name);
        lock (_lock)
        {
            if (Data.Artists.RemoveAll(a => Text.Normalize(a.Name) == key) == 0) return;
            Data.Releases.RemoveAll(r => Text.Normalize(r.Artist) == key);
        }
        Save();
        ArtistsChanged?.Invoke();
    }

    // An artist renamed on the songs (its page): still followed, under the new name (once, if that one was followed too).
    public void RenameFollowed(string from, string to)
    {
        lock (_lock)
        {
            if (Data.Artists.FirstOrDefault(a => Text.Normalize(a.Name) == Text.Normalize(from)) is not { } a) return;
            if (Data.Artists.Any(o => o != a && Text.Normalize(o.Name) == Text.Normalize(to))) Data.Artists.Remove(a);
            else a.Name = to;
        }
        Save();
        ArtistsChanged?.Invoke();
    }

    public void SetArtistPicture(string name, string? id, string? picture)
    {
        lock (_lock)
            if (Data.Artists.FirstOrDefault(a => Text.Normalize(a.Name) == Text.Normalize(name)) is { } a)
            {
                a.DeezerId ??= id;
                a.Picture = picture ?? a.Picture;
            }
        Save();
    }

    // The releases of the last weeks, the newest first.
    public List<Release> ReleasesSnapshot()
    {
        lock (_lock) return Data.Releases.OrderByDescending(r => r.Date).ToList();
    }

    // The releases found now in place of the old list (the older than since go): the ones it didn't have and not seen yet.
    public List<Release> MergeReleases(List<Release> found, DateTime since)
    {
        List<Release> fresh;
        lock (_lock)
        {
            var known = Data.Releases.ToDictionary(r => r.Url);
            fresh = found.Where(r => !known.ContainsKey(r.Url) && !r.Seen).ToList();
            foreach (var r in found)
                if (known.TryGetValue(r.Url, out var had)) r.Seen |= had.Seen;
            Data.Releases = found.Where(r => r.Date >= since).GroupBy(r => r.Url).Select(g => g.First()).OrderByDescending(r => r.Date).ToList();
            Data.ReleasesChecked = DateTime.Now;
        }
        Save();
        ArtistsChanged?.Invoke();
        return fresh;
    }

    public int UnseenReleases()
    {
        lock (_lock) return Data.Releases.Count(r => !r.Seen);
    }

    public void SeeReleases()
    {
        lock (_lock)
        {
            if (Data.Releases.All(r => r.Seen)) return;
            foreach (var r in Data.Releases) r.Seen = true;
        }
        Save();
        ArtistsChanged?.Invoke();
    }

    // ------------------------------------------------------------------ internet radios (Library › Radio)

    public event Action? StationsChanged;

    public List<Station> StationsSnapshot()
    {
        lock (_lock) return Data.Stations.ToList();
    }

    public bool HasStation(string id)
    {
        lock (_lock) return Data.Stations.Any(s => s.Id == id);
    }

    public void SaveStation(Station station)
    {
        lock (_lock)
        {
            if (Data.Stations.Any(s => s.Id == station.Id)) return;
            Data.Stations.Insert(0, station);
        }
        Save();
        StationsChanged?.Invoke();
    }

    public void ForgetStation(string id)
    {
        lock (_lock)
            if (Data.Stations.RemoveAll(s => s.Id == id) == 0) return;
        Save();
        StationsChanged?.Invoke();
    }

    // A song added to the library that had imported plays ("sp:<Spotify id>"): they're the library song's from now on.
    public void AdoptImported(string importedId, string trackId)
    {
        var (plays, seconds, last) = Log.Totals(importedId);
        if (plays == 0 && seconds <= 0) return;
        lock (_lock)
        {
            if (!Data.Stats.TryGetValue(trackId, out var s)) Data.Stats[trackId] = s = new TrackStats();
            s.Plays += plays;
            s.Seconds += seconds;
            if (last != null && (s.Last == null || last > s.Last)) s.Last = last;
        }
        Log.Rename(importedId, trackId);
        Save();
    }

    private ListeningLog? _log;
    // Month by month (the recaps, "Your month"); the connected device's profile keeps it in memory only.
    public ListeningLog Log
    {
        get
        {
            lock (_lock) return _log ??= new ListeningLog(IsMirror ? null : Path.Combine(AppPaths.ProfileDir(Info.Id), "stats"));
        }
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
        Log.Rename(oldId, newId);
        Save();
    }

    public void Save()
    {
        if (!IsMirror) _saver.Request();
    }

    public void Flush()
    {
        _saver.Flush();
        _log?.Flush();
    }

    private void SaveNow()
    {
        if (_deleted || JsonStore.Frozen) return;
        string json;
        SealedStats stats;
        lock (_lock)
        {
            json = System.Text.Json.JsonSerializer.Serialize(Data, JsonStore.Options);
            stats = new SealedStats
            {
                Stats = Data.Stats.ToDictionary(kv => kv.Key, kv => new TrackStats { Plays = kv.Value.Plays, Seconds = Math.Round(kv.Value.Seconds, 1), Last = kv.Value.Last }),
                Imported = new(Data.ImportCounts),
            };
        }
        Directory.CreateDirectory(AppPaths.ProfileDir(Info.Id));
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(FilePath)) File.Replace(tmp, FilePath, FilePath + ".bak", true);
        else File.Move(tmp, FilePath);
        Sealed.Save(Path.Combine(AppPaths.ProfileDir(Info.Id), StatsFile), stats);
    }
}
