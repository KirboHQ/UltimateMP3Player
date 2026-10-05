using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace UltimateMP3Player.Core;

// A ".ump" pack: playlists, tags and their songs in one file (a zip, like osu!'s .osz), to share them or move them to
// another computer. Inside: pack.json, music\<id>.<ext>, videos\<id>.<ext>, covers\<id>.jpg, lyrics\<id>.lrc|.txt,
// playlists\<id>.jpg. Without the music ("links only") the songs are downloaded again from their links on import.
public static class Pack
{
    public const string Extension = ".ump";
    public const string FormatId = "ultimate-mp3-pack";
    public const int FormatVersion = 1;
    public const string ManifestName = "pack.json";

    public static bool IsPack(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    public static string MusicEntry(string id, string ext) => "music/" + id + ext.ToLowerInvariant();
    public static string VideoEntry(string id, string ext) => "videos/" + id + ext.ToLowerInvariant();
    public static string CoverEntry(string id) => "covers/" + id + ".jpg";
    public static string LyricsEntry(string id, bool synced) => "lyrics/" + id + (synced ? ".lrc" : ".txt");
    public static string PlaylistCoverEntry(string id) => "playlists/" + id + ".jpg";

    // Songs downloaded again from their link instead of travelling as files (local files and DJ mixes have none).
    public static bool HasLink(Track t)
        => t.SourceUrl is { } u && u.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !t.IsLocal && t.Site != "DJ";
}

public sealed class PackManifest
{
    public string Format { get; set; } = Pack.FormatId;
    public int Version { get; set; } = Pack.FormatVersion;
    public string? App { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    // The profile that made it.
    public string? Author { get; set; }
    // The audio travels inside; false = only the songs without a link do.
    public bool Music { get; set; } = true;
    public List<PackTrack> Tracks { get; set; } = new();
    public List<PackPlaylist> Playlists { get; set; } = new();
    public List<PackTag> Tags { get; set; } = new();
}

public sealed class PackTrack
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? Year { get; set; }
    public double Duration { get; set; }
    public string? SourceUrl { get; set; }
    public string? Site { get; set; }
    // Same-song identities without the "file:" ones (paths of the other computer).
    public List<string> Keys { get; set; } = new();
    public string? ArtUrl { get; set; }
    public byte[]? Wave { get; set; }
    public double? Loudness { get; set; }
    public double? Peak { get; set; }
    public double? Bpm { get; set; }
    public double? BeatOffset { get; set; }
    public LyricsKind? Lyrics { get; set; }
    // Entries of the zip, null = not inside.
    public string? File { get; set; }
    public string? Video { get; set; }
    // Audio file: size and SHA-256, to know the very same file is already here.
    public long Size { get; set; }
    public string? Hash { get; set; }
    // Ids of the pack's tags on this song.
    public List<string> Tags { get; set; } = new();
}

public sealed class PackPlaylist
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Favorites { get; set; }
    public List<string> Tracks { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public bool HasCover { get; set; }
    public string? SourceUrl { get; set; }
}

public sealed class PackTag
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#7C5CFF";
}

// An error already worded for the user.
public sealed class PackException : Exception
{
    public PackException(string message) : base(message) { }
}

// ====================================================================== export

// What goes in: these playlists, the songs with these tags, or the whole library.
public sealed class PackExport
{
    public List<Playlist> Playlists { get; init; } = new();
    public List<Tag> Tags { get; init; } = new();
    public bool WholeLibrary { get; init; }
    // Audio files inside (otherwise links only); videos too, if they have one.
    public bool Music { get; init; } = true;
    public bool Videos { get; init; }

    public bool IsEmpty => Playlists.Count == 0 && Tags.Count == 0 && !WholeLibrary;
}

public static class PackWriter
{
    // The songs, in the order of the playlists, then the tagged ones, then the rest of the library.
    public static List<Track> Songs(Library lib, Profile profile, PackExport e)
    {
        var seen = new HashSet<string>();
        var list = new List<Track>();
        void Add(string id)
        {
            if (seen.Add(id) && lib.Get(id) is { } t) list.Add(t);
        }
        foreach (var p in e.Playlists)
            foreach (var id in p.Tracks.ToList()) Add(id);
        var tags = e.Tags.Select(t => t.Id).ToHashSet();
        if (tags.Count > 0)
            foreach (var t in lib.Snapshot())
                if (profile.TagsOf(t.Id).Any(tags.Contains)) Add(t.Id);
        if (e.WholeLibrary)
            foreach (var t in lib.Snapshot().OrderBy(t => t.Added)) Add(t.Id);
        return list;
    }

    // The audio (and video) of a song travels inside the pack.
    public static bool CarriesFile(Track t, PackExport e) => e.Music || !Pack.HasLink(t);

    // Roughly how big the file will be (sizeOf: file sizes, cached by the caller).
    public static long EstimateSize(IEnumerable<Track> songs, PackExport e, Func<string?, long>? sizeOf = null)
    {
        sizeOf ??= SizeOf;
        long total = 0;
        foreach (var t in songs)
        {
            total += 1200 + (t.Wave?.Length ?? 0) * 4 / 3;
            if (t.HasCover) total += sizeOf(AppPaths.TrackCover(t.Id));
            if (!CarriesFile(t, e)) continue;
            total += sizeOf(t.Path);
            if (e.Videos && t.HasVideo && t.VideoPath != t.Path) total += sizeOf(t.VideoPath);
        }
        return total;
    }

    public static long SizeOf(string? path)
    {
        try { return path != null && File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    // Writes the pack (a .part file renamed at the end, so a failed export leaves nothing half done).
    // progress: share done 0-1 and the song being copied.
    public static async Task<PackManifest> WriteAsync(string path, Library lib, Profile profile, PackExport e, string? appVersion,
        Action<double, string?>? progress, CancellationToken ct)
    {
        var songs = Songs(lib, profile, e);
        var tags = e.Tags.ToList();
        var tagIds = tags.Select(t => t.Id).ToHashSet();
        var manifest = new PackManifest { App = appVersion, Author = profile.Info.Name, Music = e.Music };
        manifest.Tags = tags.Select(t => new PackTag { Id = t.Id, Name = t.Name, Color = t.Color }).ToList();

        long total = Math.Max(1, EstimateSize(songs, e)), done = 0;
        var part = path + ".part";
        try
        {
            await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var t in songs)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Invoke(done / (double)total, t.Title);
                    var pt = new PackTrack
                    {
                        Id = t.Id, Title = t.Title, Artist = t.Artist, Album = t.Album, Year = t.Year, Duration = t.Duration,
                        SourceUrl = t.SourceUrl, Site = t.Site, ArtUrl = t.ArtUrl, Wave = t.Wave, Loudness = t.Loudness, Peak = t.Peak,
                        Bpm = t.Bpm, BeatOffset = t.BeatOffset, Lyrics = t.Lyrics,
                        Keys = t.Keys.Where(k => !k.StartsWith("file:", StringComparison.OrdinalIgnoreCase)).ToList(),
                        Tags = profile.TagsOf(t.Id).Where(tagIds.Contains).ToList(),
                    };
                    if (CarriesFile(t, e) && File.Exists(t.Path))
                    {
                        pt.File = Pack.MusicEntry(t.Id, Path.GetExtension(t.Path));
                        var (size, hash) = await CopyIn(zip, t.Path, pt.File, CompressionLevel.NoCompression, n =>
                        {
                            done += n;
                            progress?.Invoke(done / (double)total, t.Title);
                        }, ct);
                        pt.Size = size;
                        pt.Hash = hash;
                        if (e.Videos && t.HasVideo && File.Exists(t.VideoPath))
                        {
                            if (t.VideoPath == t.Path) pt.Video = pt.File;
                            else
                            {
                                pt.Video = Pack.VideoEntry(t.Id, Path.GetExtension(t.VideoPath!));
                                await CopyIn(zip, t.VideoPath!, pt.Video, CompressionLevel.NoCompression, n => done += n, ct);
                            }
                        }
                    }
                    if (t.HasCover && File.Exists(AppPaths.TrackCover(t.Id)))
                    {
                        await CopyIn(zip, AppPaths.TrackCover(t.Id), Pack.CoverEntry(t.Id), CompressionLevel.NoCompression, n => done += n, ct);
                    }
                    foreach (var synced in new[] { true, false })
                        if (t.HasLyrics && File.Exists(LyricsStore.PathFor(t.Id, synced)))
                            await CopyIn(zip, LyricsStore.PathFor(t.Id, synced), Pack.LyricsEntry(t.Id, synced), CompressionLevel.Optimal, null, ct);
                    manifest.Tracks.Add(pt);
                    done += 1200;
                }

                var inPack = manifest.Tracks.Select(t => t.Id).ToHashSet();
                foreach (var p in e.Playlists)
                {
                    var pp = new PackPlaylist
                    {
                        Id = p.Id, Name = p.Name, Favorites = p.IsFavorites, SourceUrl = p.SourceUrl,
                        Tracks = p.Tracks.ToList().Where(inPack.Contains).ToList(),
                        Tags = p.Tags.ToList().Where(tagIds.Contains).ToList(),
                    };
                    var cover = profile.PlaylistCover(p);
                    if (p.HasCover && File.Exists(cover))
                    {
                        await CopyIn(zip, cover, Pack.PlaylistCoverEntry(p.Id), CompressionLevel.NoCompression, null, ct);
                        pp.HasCover = true;
                    }
                    manifest.Playlists.Add(pp);
                }

                var entry = zip.CreateEntry(Pack.ManifestName, CompressionLevel.Optimal);
                await using (var s = entry.Open())
                    await JsonSerializer.SerializeAsync(s, manifest, JsonStore.Options, ct);
            }
            File.Move(part, path, true);
            progress?.Invoke(1, null);
            return manifest;
        }
        catch
        {
            try { File.Delete(part); } catch { }
            throw;
        }
    }

    // Copies a file into the zip; returns its size and SHA-256.
    private static async Task<(long Size, string Hash)> CopyIn(ZipArchive zip, string file, string name, CompressionLevel level, Action<long>? copied,
        CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, level);
        entry.LastWriteTime = File.GetLastWriteTime(file);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        await using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, true))
        await using (var dst = entry.Open())
        {
            var buf = new byte[1 << 16];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                hash.AppendData(buf, 0, n);
                size += n;
                copied?.Invoke(n);
            }
        }
        return (size, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    // SHA-256 of a whole file (the same as CopyIn computes).
    public static string HashFile(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }
}

// ====================================================================== import

public enum PackSongState
{
    // The same song is already in the library (same link, same file or same title, artist and length).
    InLibrary,
    // Its file is in the pack.
    New,
    // Downloaded again from its link.
    Download,
    // Neither the file nor a link: can't be added.
    Missing,
}

public sealed class PackSong
{
    public PackSong(PackTrack t, PackSongState state, Track? existing)
    {
        T = t;
        State = state;
        Existing = existing;
    }

    public PackTrack T { get; }
    public PackSongState State { get; }
    public Track? Existing { get; }
}

// An opened pack: its description and the zip, read on demand.
public sealed class PackFile : IDisposable
{
    private readonly ZipArchive _zip;
    private readonly HashSet<string> _entries;
    // One reader at a time: the entries share the file stream.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PackFile(string path, ZipArchive zip, PackManifest manifest, long size)
    {
        FilePath = path;
        _zip = zip;
        _entries = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Manifest = manifest;
        FileSize = size;
    }

    public string FilePath { get; }
    public string Name => Path.GetFileNameWithoutExtension(FilePath);
    public PackManifest Manifest { get; }
    public long FileSize { get; }

    public static PackFile Open(string path)
    {
        FileStream? fs = null;
        ZipArchive? zip = null;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            long size = fs.Length;
            zip = new ZipArchive(fs, ZipArchiveMode.Read);
            var entry = zip.GetEntry(Pack.ManifestName) ?? throw new PackException(L.T("Il file non è un pacchetto di Ultimate MP3 Player."));
            PackManifest? m;
            using (var s = entry.Open())
                m = JsonSerializer.Deserialize<PackManifest>(s, JsonStore.Options);
            if (m == null || m.Format != Pack.FormatId) throw new PackException(L.T("Il file non è un pacchetto di Ultimate MP3 Player."));
            if (m.Version > Pack.FormatVersion)
                throw new PackException(L.T("Il pacchetto è stato creato con una versione più nuova dell'app: aggiorna Ultimate MP3 Player per aprirlo."));
            m.Tracks.RemoveAll(t => string.IsNullOrEmpty(t.Id));
            return new PackFile(path, zip, m, size);
        }
        catch (Exception ex)
        {
            zip?.Dispose();
            fs?.Dispose();
            if (ex is PackException) throw;
            if (ex is IOException or UnauthorizedAccessException)
                throw new PackException(L.T("Impossibile leggere il file:") + " " + ex.Message);
            throw new PackException(L.T("Il file è danneggiato o non è un pacchetto di Ultimate MP3 Player."));
        }
    }

    public bool Has(string? entry) => entry != null && _entries.Contains(entry);

    // Copies an entry out of the zip; false when it isn't there.
    public async Task<bool> ExtractAsync(string entryName, string target, Action<long>? copied, CancellationToken ct)
    {
        if (!Has(entryName)) return false;
        var tmp = target + ".part";
        await _gate.WaitAsync(ct);
        try
        {
            if (_zip.GetEntry(entryName) is not { } entry) return false;
            // A profile never saved yet has no folder (its playlist pictures go there).
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            await using (var src = entry.Open())
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
            {
                var buf = new byte[1 << 16];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    copied?.Invoke(n);
                }
            }
            File.Move(tmp, target, true);
            return true;
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? ReadText(string entryName)
        => ReadBytes(entryName) is { } bytes ? new StreamReader(new MemoryStream(bytes)).ReadToEnd() : null;

    // A small entry (cover, lyrics) in memory.
    public byte[]? ReadBytes(string entryName)
    {
        if (!Has(entryName)) return null;
        _gate.Wait();
        try
        {
            if (_zip.GetEntry(entryName) is not { } e) return null;
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try { _zip.Dispose(); }
        finally { _gate.Release(); }
    }
}

// What to bring in: which playlists (merged into one with the same name, or as a copy), which tags, the songs outside both.
public sealed class PackImportPlan
{
    public HashSet<string> Playlists { get; init; } = new();
    // Pack playlist id → true: into the playlist of the same name (Favorites: into yours), false: a new one.
    public Dictionary<string, bool> Merge { get; init; } = new();
    public HashSet<string> Tags { get; init; } = new();
    public bool OtherSongs { get; init; }
}

// A song of the pack downloaded again: where it goes once it's in the library.
public sealed class PackDownload
{
    public PackDownload(PackTrack track, string? synced, string? plain)
    {
        Track = track;
        SyncedLyrics = synced;
        PlainLyrics = plain;
    }

    public PackTrack Track { get; }
    public string? SyncedLyrics { get; }
    public string? PlainLyrics { get; }
    // Profile playlist id → the pack playlist it comes from (for its place in the list).
    public List<(string PlaylistId, PackPlaylist From)> Playlists { get; } = new();
    public List<string> TagIds { get; } = new();
}

public sealed class PackImportResult
{
    public int Added { get; set; }
    public int Present { get; set; }
    public int Failed { get; set; }
    public int Missing { get; set; }
    public List<string> PlaylistIds { get; } = new();
    public int NewPlaylists { get; set; }
    public int NewTags { get; set; }
    public int Tags { get; set; }
    public List<PackDownload> Downloads { get; } = new();
    // Pack song id → library id.
    public Dictionary<string, string> Map { get; } = new();
}

// The library looked up by file size and by song title, built once for a whole pack (a pack of thousands of songs
// against a library of thousands would otherwise compare every pair).
public sealed class LibraryIndex
{
    private readonly Library _lib;
    private Dictionary<long, List<Track>>? _bySize;
    private readonly Dictionary<string, List<(Track T, SongIdentity Id)>> _byTitle = new();

    public LibraryIndex(Library lib)
    {
        _lib = lib;
        foreach (var t in lib.Snapshot()) AddTitles(t);
    }

    // A song added meanwhile (a pack can hold the same song twice).
    public void Add(Track t)
    {
        AddTitles(t);
        if (_bySize != null && SizeOf(t.Path) is > 0 and var size) Bucket(_bySize, size).Add(t);
    }

    private void AddTitles(Track t)
    {
        var id = SongMeta.Identity(t.Title, t.Artist);
        if (id.Artists.Count == 0) return;
        foreach (var title in id.Titles) Bucket(_byTitle, title).Add((t, id));
    }

    private static List<T> Bucket<TKey, T>(Dictionary<TKey, List<T>> d, TKey key) where TKey : notnull
    {
        if (!d.TryGetValue(key, out var list)) d[key] = list = new List<T>();
        return list;
    }

    private static long SizeOf(string path)
    {
        try { return new FileInfo(path) is { Exists: true } fi ? fi.Length : 0; }
        catch { return 0; }
    }

    // Same link, same file (same size and SHA-256) or the same song (title, artist, ±4 s, like Library.FindSimilar).
    public Track? Find(PackTrack t)
    {
        if (t.Keys.Count > 0 && _lib.FindByKeys(t.Keys) is { } byKey) return byKey;
        if (t.Hash != null && t.Size > 0)
        {
            if (_bySize == null)
            {
                _bySize = new Dictionary<long, List<Track>>();
                foreach (var x in _lib.Snapshot())
                    if (SizeOf(x.Path) is > 0 and var size) Bucket(_bySize, size).Add(x);
            }
            if (_bySize.TryGetValue(t.Size, out var same))
                foreach (var c in same)
                {
                    try
                    {
                        if (_lib.Get(c.Id) != null && File.Exists(c.Path) && PackWriter.HashFile(c.Path) == t.Hash) return c;
                    }
                    catch { }
                }
        }
        if (t.Duration <= 0) return null;
        var wanted = SongMeta.Identity(t.Title, t.Artist);
        if (wanted.Artists.Count == 0) return null;
        foreach (var title in wanted.Titles)
            if (_byTitle.TryGetValue(title, out var candidates))
                foreach (var (c, id) in candidates)
                    if (Math.Abs(c.Duration - t.Duration) <= 4 && SongMeta.SameSong(wanted, id) && _lib.Get(c.Id) != null) return c;
        return null;
    }
}

public static class PackImporter
{
    // Every song of the pack, with what happens to it.
    public static List<PackSong> Analyze(PackFile pack, Library lib)
    {
        var index = new LibraryIndex(lib);
        var list = new List<PackSong>();
        foreach (var t in pack.Manifest.Tracks)
        {
            if (index.Find(t) is { } have) list.Add(new PackSong(t, PackSongState.InLibrary, have));
            else if (pack.Has(t.File)) list.Add(new PackSong(t, PackSongState.New, null));
            else if (t.SourceUrl is { } u && u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) list.Add(new PackSong(t, PackSongState.Download, null));
            else list.Add(new PackSong(t, PackSongState.Missing, null));
        }
        return list;
    }

    // The pack songs the plan brings in, in pack order.
    public static List<PackTrack> Chosen(PackManifest m, PackImportPlan plan)
    {
        var ids = new HashSet<string>();
        foreach (var p in m.Playlists.Where(p => plan.Playlists.Contains(p.Id))) ids.UnionWith(p.Tracks);
        foreach (var t in m.Tracks.Where(t => t.Tags.Any(plan.Tags.Contains))) ids.Add(t.Id);
        if (plan.OtherSongs) ids.UnionWith(Loose(m).Select(t => t.Id));
        return m.Tracks.Where(t => ids.Contains(t.Id)).ToList();
    }

    // Songs in no playlist of the pack and without its tags (a whole library exported).
    public static List<PackTrack> Loose(PackManifest m)
    {
        var inLists = m.Playlists.SelectMany(p => p.Tracks).ToHashSet();
        var tags = m.Tags.Select(t => t.Id).ToHashSet();
        return m.Tracks.Where(t => !inLists.Contains(t.Id) && !t.Tags.Any(tags.Contains)).ToList();
    }

    // The playlist of the profile a pack playlist merges into, if any.
    public static Playlist? SameName(Profile profile, PackPlaylist p)
        => p.Favorites ? profile.Favorites
            : profile.PlaylistsSnapshot().FirstOrDefault(x => !x.IsFavorites && string.Equals(x.Name, p.Name.Trim(), StringComparison.CurrentCultureIgnoreCase));

    public static Tag? SameName(Profile profile, PackTag t)
        => profile.TagsSnapshot().FirstOrDefault(x => string.Equals(x.Name, t.Name.Trim(), StringComparison.CurrentCultureIgnoreCase));

    // Copies the new songs into the music folder and the library, then makes the playlists and tags.
    // Songs to download are only listed (the app queues them). progress: share 0-1 and the song being copied.
    public static async Task<PackImportResult> RunAsync(PackFile pack, PackImportPlan plan, Library lib, Profile profile, string musicDir,
        Action<double, string?>? progress, CancellationToken ct)
    {
        var m = pack.Manifest;
        var res = new PackImportResult();
        var chosen = Chosen(m, plan);
        var index = new LibraryIndex(lib);

        // Tags first: same name = the one you already have (its colour stays).
        var tagMap = new Dictionary<string, string>();
        foreach (var pt in m.Tags.Where(t => plan.Tags.Contains(t.Id)))
        {
            if (SameName(profile, pt) is { } have) tagMap[pt.Id] = have.Id;
            else
            {
                tagMap[pt.Id] = profile.CreateTag(pt.Name, pt.Color).Id;
                res.NewTags++;
            }
        }
        res.Tags = tagMap.Count;

        // The songs, the ones to copy weighted by size.
        long total = Math.Max(1, chosen.Sum(t => 64 * 1024 + (pack.Has(t.File) ? t.Size : 0))), done = 0;
        var downloads = new Dictionary<string, PackDownload>();
        foreach (var pt in chosen)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(done / (double)total, pt.Title);
            try
            {
                if (index.Find(pt) is { } have)
                {
                    res.Map[pt.Id] = have.Id;
                    res.Present++;
                    await FillIn(pack, pt, have, lib, ct);
                }
                else if (pack.Has(pt.File))
                {
                    var t = await AddSong(pack, pt, lib, musicDir, n =>
                    {
                        done += n;
                        progress?.Invoke(done / (double)total, pt.Title);
                    }, ct);
                    index.Add(t);
                    res.Map[pt.Id] = t.Id;
                    res.Added++;
                }
                else if (pt.SourceUrl is { } u && u.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    downloads[pt.Id] = new PackDownload(pt, pack.ReadText(Pack.LyricsEntry(pt.Id, true)), pack.ReadText(Pack.LyricsEntry(pt.Id, false)));
                }
                else res.Missing++;
            }
            catch (OperationCanceledException) { throw; }
            catch { res.Failed++; }
            done += 64 * 1024;
        }

        // Tags on the songs.
        foreach (var pt in chosen)
        {
            var mine = pt.Tags.Where(tagMap.ContainsKey).Select(id => tagMap[id]).ToList();
            if (mine.Count == 0) continue;
            if (res.Map.TryGetValue(pt.Id, out var id))
                foreach (var tag in mine) profile.SetTag(new[] { id }, tag, true);
            else if (downloads.TryGetValue(pt.Id, out var d)) d.TagIds.AddRange(mine);
        }

        // Playlists: into the one with the same name, or a new one (named "Name (2)" if taken).
        foreach (var pp in m.Playlists.Where(p => plan.Playlists.Contains(p.Id)))
        {
            ct.ThrowIfCancellationRequested();
            bool merge = plan.Merge.TryGetValue(pp.Id, out var mg) ? mg : true;
            Playlist? target = merge ? SameName(profile, pp) : null;
            if (target == null)
            {
                var name = pp.Favorites ? L.F("Preferiti di {0}", string.IsNullOrWhiteSpace(m.Author) ? "?" : m.Author) : pp.Name;
                target = profile.CreatePlaylist(name, pp.SourceUrl);
                res.NewPlaylists++;
                if (pp.HasCover)
                {
                    try
                    {
                        if (await pack.ExtractAsync(Pack.PlaylistCoverEntry(pp.Id), profile.PlaylistCover(target), null, ct))
                            profile.SetPlaylistCover(target, true);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
            }
            profile.AddTracks(target, pp.Tracks.Where(res.Map.ContainsKey).Select(id => res.Map[id]));
            foreach (var tag in pp.Tags.Where(tagMap.ContainsKey)) profile.SetPlaylistTag(target, tagMap[tag], true);
            foreach (var id in pp.Tracks)
                if (downloads.TryGetValue(id, out var d)) d.Playlists.Add((target.Id, pp));
            res.PlaylistIds.Add(target.Id);
        }

        res.Downloads.AddRange(chosen.Where(t => downloads.ContainsKey(t.Id)).Select(t => downloads[t.Id]));
        progress?.Invoke(1, null);
        return res;
    }

    // A new song: its file into the music folder ("Artist - Title.mp3"), cover, lyrics, video, then the library.
    private static async Task<Track> AddSong(PackFile pack, PackTrack pt, Library lib, string musicDir, Action<long> copied, CancellationToken ct)
    {
        Directory.CreateDirectory(musicDir);
        var name = Text.SafeFileName(string.IsNullOrWhiteSpace(pt.Artist) ? pt.Title : $"{pt.Artist.Split(',')[0].Trim()} - {pt.Title}");
        var target = Text.UniquePath(musicDir, name, Path.GetExtension(pt.File!));
        var t = new Track
        {
            Title = string.IsNullOrWhiteSpace(pt.Title) ? Path.GetFileNameWithoutExtension(target) : pt.Title,
            Artist = pt.Artist, Album = pt.Album, Year = pt.Year, Duration = pt.Duration, Path = target,
            // A local file of the other computer is a file of the app here (deleting the song deletes it).
            SourceUrl = pt.SourceUrl, Site = pt.Site is null or "File locale" ? "Pacchetto" : pt.Site,
            Keys = pt.Keys.ToList(), ArtUrl = pt.ArtUrl, Wave = pt.Wave, Loudness = pt.Loudness, Peak = pt.Peak,
            Bpm = pt.Bpm, BeatOffset = pt.BeatOffset,
        };
        var created = new List<string> { target, AppPaths.TrackCover(t.Id) };
        try
        {
            await pack.ExtractAsync(pt.File!, target, copied, ct);
            if (pt.Video != null && pack.Has(pt.Video))
            {
                if (pt.Video == pt.File) t.VideoPath = target;
                else
                {
                    var video = Text.UniquePath(musicDir, name, Path.GetExtension(pt.Video));
                    if (await pack.ExtractAsync(pt.Video, video, copied, ct))
                    {
                        created.Add(video);
                        t.VideoPath = video;
                    }
                }
            }
            t.HasCover = await pack.ExtractAsync(Pack.CoverEntry(pt.Id), AppPaths.TrackCover(t.Id), null, ct);
            await CopyLyrics(pack, pt, t, ct);
            if (t.Wave == null || t.Loudness == null || t.Duration <= 0)
            {
                try
                {
                    var a = await AudioAnalysis.AnalyzeAsync(target, ct);
                    t.Wave ??= a.Wave;
                    t.Loudness ??= a.Loudness;
                    t.Peak ??= a.Peak;
                    if (a.Duration > 0) t.Duration = a.Duration;
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            if (!t.HasCover)
            {
                try { t.HasCover = await AudioAnalysis.ExtractCoverAsync(target, AppPaths.TrackCover(t.Id), false, ct); }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            lib.Add(t);
            return t;
        }
        catch
        {
            foreach (var f in created) { try { File.Delete(f); } catch { } }
            LyricsStore.Delete(t.Id);
            throw;
        }
    }

    // A song already here: what it lacks comes from the pack (lyrics, BPM, cover, video); nothing it has is replaced.
    private static async Task FillIn(PackFile pack, PackTrack pt, Track t, Library lib, CancellationToken ct)
    {
        bool changed = false;
        if (t.Lyrics == null && pt.Lyrics != null) changed |= await CopyLyrics(pack, pt, t, ct);
        if (t.Bpm == null && pt.Bpm != null)
        {
            t.Bpm = pt.Bpm;
            t.BeatOffset ??= pt.BeatOffset;
            changed = true;
        }
        if (!t.HasCover && await pack.ExtractAsync(Pack.CoverEntry(pt.Id), AppPaths.TrackCover(t.Id), null, ct))
        {
            t.HasCover = true;
            t.CoverVersion++;
            changed = true;
        }
        lib.AddKeys(t, pt.Keys);
        if (changed) lib.Changed(t);
    }

    private static async Task<bool> CopyLyrics(PackFile pack, PackTrack pt, Track t, CancellationToken ct)
    {
        if (pt.Lyrics == null) return false;
        if (pt.Lyrics is LyricsKind.None or LyricsKind.Instrumental)
        {
            t.Lyrics = pt.Lyrics;
            return true;
        }
        bool any = false;
        foreach (var synced in new[] { true, false })
            any |= await pack.ExtractAsync(Pack.LyricsEntry(pt.Id, synced), LyricsStore.PathFor(t.Id, synced), null, ct);
        if (any) t.Lyrics = pt.Lyrics;
        return any;
    }

    // A downloaded song of the pack: into its playlists (where it was in the pack's order), its tags, and what the pack knew.
    public static void Place(PackDownload d, Track t, Library lib, Profile profile, IReadOnlyDictionary<string, string> map)
    {
        foreach (var tag in d.TagIds)
            if (profile.GetTag(tag) != null) profile.SetTag(new[] { t.Id }, tag, true);
        foreach (var (playlistId, from) in d.Playlists)
        {
            if (profile.GetPlaylist(playlistId) is not { } pl || profile.Contains(pl, t.Id)) continue;
            int i = from.Tracks.IndexOf(d.Track.Id);
            int? at = null;
            for (int j = i - 1; j >= 0 && at == null; j--)
                if (map.TryGetValue(from.Tracks[j], out var prev) && pl.Tracks.IndexOf(prev) is int p and >= 0) at = p + 1;
            for (int j = i + 1; j < from.Tracks.Count && at == null; j++)
                if (map.TryGetValue(from.Tracks[j], out var next) && pl.Tracks.IndexOf(next) is int p and >= 0) at = p;
            profile.AddTrack(pl, t.Id, at);
        }
        bool changed = false;
        if (t.Bpm == null && d.Track.Bpm != null)
        {
            t.Bpm = d.Track.Bpm;
            t.BeatOffset ??= d.Track.BeatOffset;
            changed = true;
        }
        if (t.Lyrics == null && (d.SyncedLyrics != null || d.PlainLyrics != null))
        {
            try
            {
                if (d.SyncedLyrics != null) File.WriteAllText(LyricsStore.PathFor(t.Id, true), d.SyncedLyrics);
                if (d.PlainLyrics != null) File.WriteAllText(LyricsStore.PathFor(t.Id, false), d.PlainLyrics);
                t.Lyrics = d.Track.Lyrics ?? (d.SyncedLyrics != null ? LyricsKind.Synced : LyricsKind.Plain);
                changed = true;
            }
            catch { }
        }
        lib.AddKeys(t, d.Track.Keys);
        if (changed) lib.Changed(t);
    }

    // How a song of the pack is downloaded again: Spotify, Deezer and Apple Music songs are searched on YouTube Music like
    // a normal download from those sites, the others come from their own link.
    public static MediaItem ToMediaItem(PackTrack t)
    {
        var url = t.SourceUrl!;
        var spotify = t.Keys.FirstOrDefault(k => k.StartsWith("spotify:", StringComparison.OrdinalIgnoreCase))?["spotify:".Length..];
        var site = Sites.Find(url)?.Name;
        bool search = spotify != null || site is "Spotify" or "Deezer" or "Apple Music";
        var item = new MediaItem
        {
            Title = t.Title,
            Artist = t.Artist,
            Album = t.Album,
            Year = t.Year,
            Duration = t.Duration > 0 ? t.Duration : null,
            Kind = MediaKind.Audio,
            Source = search ? SourceKind.Search : SourceKind.YtDlp,
            Url = url,
            PageUrl = url,
            SpotifyTrackId = spotify,
            CoverUrl = t.ArtUrl,
            SiteName = t.Site is { Length: > 0 } s && s != "Pacchetto" ? s : Sites.NameFor(url),
        };
        if (t.ArtUrl != null) item.Thumbnails.Add(t.ArtUrl);
        return item;
    }
}
