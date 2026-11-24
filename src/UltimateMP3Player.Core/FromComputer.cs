using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UltimateMP3Player.Core;

// What the installer says it brought (ready.json, the last file to arrive).
public sealed class ComputerReady
{
    public int Version { get; set; }
    public string Id { get; set; } = "";
    public string? Computer { get; set; }
    public int Songs { get; set; }
    public int Playlists { get; set; }
    public int Files { get; set; }
    public long Bytes { get; set; }
    // What was chosen on the computer: "songs", "stats" (with them), "look", "settings".
    public List<string> Take { get; set; } = new();

    // Made by an installer that knows more than this app.
    [JsonIgnore] public bool Newer => Version > FromComputer.Version;
    [JsonIgnore] public bool WithSongs => Take.Contains("songs");
    [JsonIgnore] public bool WithStats => WithSongs && Take.Contains("stats");
    [JsonIgnore] public bool WithLook => Take.Contains("look");
    [JsonIgnore] public bool WithSettings => Take.Contains("settings");
}

// What came of it.
public sealed class ComputerResult
{
    public int Added { get; set; }
    public int Present { get; set; }
    // Songs whose file is on this device now; the ones heard from their link, their file being in a format it can't play.
    public int Saved { get; set; }
    public int Linked { get; set; }
    // Songs that couldn't come in: a format it can't play, or no file, and no link.
    public int BadFormat { get; set; }
    public int NoFile { get; set; }
    public int NewPlaylists { get; set; }
    public int NewTags { get; set; }
    public int Plays { get; set; }
    public List<string> NewProfiles { get; } = new();
    // The looks and settings, for Apply. Fresh: a profile made for them, with no look of its own to keep.
    public List<(Profile To, SettingsFile File, List<string> Groups, bool Fresh)> Looks { get; } = new();

    // The summary, a paragraph each: what's here now, then what isn't and why.
    public List<string> Report()
    {
        var songs = new List<string>();
        if (Added > 0) songs.Add(L.Count(Added, "1 song added", "{0} songs added"));
        if (Present > 0) songs.Add(L.F("{0} already in your library", Present));
        if (NewPlaylists > 0) songs.Add(L.Count(NewPlaylists, "1 new playlist", "{0} new playlists"));
        if (NewTags > 0) songs.Add(L.Count(NewTags, "1 new tag", "{0} new tags"));
        if (Saved > 0) songs.Add(L.Count(Saved, "1 song saved on the iPhone", "{0} songs saved on the iPhone"));
        if (Plays > 0) songs.Add(L.Count(Plays, "1 play in the statistics", "{0} plays in the statistics"));
        if (NewProfiles.Count > 0) songs.Add(L.F(NewProfiles.Count == 1 ? "New profile: {0}" : "New profiles: {0}", string.Join(", ", NewProfiles)));
        var lines = new List<string>();
        if (songs.Count > 0) lines.Add(string.Join("\u00A0· ", songs));
        bool look = Looks.Any(l => l.Groups.Contains(SettingsFile.LookGroup)), settings = Looks.Any(l => l.Groups.Any(g => g != SettingsFile.LookGroup));
        if (look) lines.Add(L.T("The look is the computer's: the one from before is in Settings › Appearance, among the looks."));
        if (settings) lines.Add(L.T("The settings are the computer's."));
        if (Linked > 0) lines.Add(L.Count(Linked, "1 song is in a format the iPhone can't play: it's heard from its link.", "{0} songs are in a format the iPhone can't play: they're heard from their links."));
        if (BadFormat > 0) lines.Add(L.Count(BadFormat, "1 song not added: the iPhone can't play its format, and it has no link.", "{0} songs not added: the iPhone can't play their format, and they have no link."));
        if (NoFile > 0) lines.Add(L.Count(NoFile, "1 song not added: its file wasn't brought, and it has no link.", "{0} songs not added: their files weren't brought, and they have no link."));
        if (lines.Count == 0) lines.Add(L.T("It was all here already."));
        return lines;
    }
}

// The data of the computer's app, put in the iPhone's one by the installer over the cable (Library/FromComputer: data.zip, which is a
// backup, the music and the videos beside it), joined to what's here: nothing is replaced or deleted. All of it can be done again, after
// the app was closed halfway or from the same computer another day, without doubling anything: so it's told apart by the songs' ids and
// the profiles' names, and the plays by how many came before (Profile.AddStats).
public static class FromComputer
{
    public const int Version = 1;
    public const string ReadyName = "ready.json";

    private static readonly JsonSerializerOptions ReadyOptions = new(JsonSerializerDefaults.Web);

    // Whether something's there, and what it says (null: it can't be read). An empty ready.json is one being written.
    public static (bool There, ComputerReady? Ready) Look(string dir)
    {
        var file = new FileInfo(Path.Combine(dir, ReadyName));
        if (!file.Exists || file.Length == 0) return (false, null);
        try { return (true, JsonSerializer.Deserialize<ComputerReady>(File.ReadAllText(file.FullName), ReadyOptions) is { Take: not null } ready ? ready : null); }
        catch (JsonException) { return (true, null); }
    }

    // Away with it, unless it's another one by now, or is becoming one (the installer, again): that stays.
    public static void Delete(string dir, string? id)
    {
        if (Look(dir).Ready?.Id != id) return;
        File.Delete(Path.Combine(dir, ReadyName));
        Directory.Delete(dir, true);
    }

    private sealed record Source(ProfileInfo Info, ProfileData? Data, string Dir);

    // Ids: the songs' there (the songs themselves get the ones they have here).
    private sealed record Contents(List<Track> Tracks, HashSet<string> Ids, AppSettings? Settings, List<Source> Profiles, Dictionary<string, ArtistSite> Artists, string Dir);

    // playable, video: whether this device can play a song's file, show a video's. heard: the songs heard without being in a library that
    // a profile's statistics name, for the profile's session (null: straight into its data). Called on the screen's thread, which does the
    // profiles and heard; the rest happens away from it. InvalidDataException, worded for the user: what arrived can't be read (and
    // nothing was touched).
    public static async Task<ComputerResult> RunAsync(string dir, ComputerReady ready, Library lib, ProfileList list, ProfileInfo open, string musicDir,
        Func<string, bool> playable, Func<string, bool> video, Action<Profile, List<RadioSong>>? heard = null, Action<double>? progress = null)
    {
        var r = new ComputerResult();
        var c = await Task.Run(() => Read(dir));
        var places = Place(c, list, open, r);
        if (ready.WithSongs)
        {
            var map = await Task.Run(() =>
            {
                var moves = new List<(string From, string To)>();
                var fresh = new List<Track>();
                var songs = FindSongs(c, dir, lib, musicDir, playable, video, fresh, moves, r, p => progress?.Invoke(0.75 * p));
                // (their profiles' before they're in the library: one nobody has yet is taken by the profile that's open)
                foreach (var (s, to, _) in places) to.Own(SongsOf(c, s, s == places[0].From).Where(songs.ContainsKey).Select(id => songs[id]).ToList());
                foreach (var t in fresh) lib.Add(t);
                AddArtists(c);
                foreach (var (s, to, _) in places.Where(p => p.From.Data != null)) AddLists(s, to, songs, r);
                // (the library first: a song whose file hasn't moved yet is found again here and gets it, a file moved without its song
                // would be lost)
                lib.Flush();
                Profile.FlushAll();
                for (int i = 0; i < moves.Count; i++)
                {
                    progress?.Invoke(0.75 + 0.2 * i / moves.Count);
                    File.Move(moves[i].From, moves[i].To);
                }
                return songs;
            });
            if (ready.WithStats)
                foreach (var (s, to, _) in places.Where(p => p.From.Data != null))
                {
                    var here = new Dictionary<string, string>(map);
                    var totals = await Task.Run(() => Totals(s));
                    var bring = HeardSongs(c, s, to, lib, totals, here);
                    if (bring.Count > 0)
                    {
                        if (heard != null) heard(to, bring);
                        else to.Data.Radio = to.Data.Radio.Concat(bring).ToList();
                    }
                    r.Plays += await Task.Run(() => AddStats(s, to, totals, here));
                }
        }
        progress?.Invoke(0.97);
        if (ready.WithLook || ready.WithSettings && c.Settings != null)
            foreach (var (s, to, fresh) in places.Where(p => p.From.Data != null))
            {
                var groups = SettingsFile.All.Select(g => g.Key).Where(k => k == SettingsFile.LookGroup ? ready.WithLook : ready.WithSettings && c.Settings != null && k != "discord").ToList();
                // (the bars of the music are the profile's; the app's are the ones of the last profile open there)
                var app = Copy(c.Settings ?? new AppSettings());
                BarsLook.Open(s.Data!, app);
                var file = SettingsFile.Make(s.Data!, app, s.Info.Id, s.Info.Name, ready.Computer ?? "", Path.Combine(s.Dir, "backdrop.jpg"), groups);
                file.LookPictures = s.Data!.Looks.Where(l => Safe(l.Id)).Select(l => (l.Id, Path: Path.Combine(s.Dir, "looks", l.Id + ".jpg"))).Where(p => File.Exists(p.Path))
                    .GroupBy(p => p.Id).ToDictionary(g => g.Key, g => File.ReadAllBytes(g.First().Path));
                r.Looks.Add((to, file, groups, fresh));
            }
        progress?.Invoke(1);
        return r;
    }

    // The looks and settings into their profiles and the app's (the caller saves the app's and opens the profile again). What they'd
    // replace that's a list of the person's own stays: the look from before, the artists never suggested, the equalizer's presets.
    public static void Apply(ComputerResult r, Profile open, AppSettings settings)
    {
        if (r.Looks.Count == 0) return;
        var main = r.Looks.FirstOrDefault(l => l.To == open, r.Looks[0]);
        foreach (var look in r.Looks)
        {
            var (to, file, groups, fresh) = look;
            var d = to.Data;
            // (the app's settings once; another profile's bars of the music aren't the ones in use)
            var s = look == main ? settings : Copy(settings);
            if (to != open) BarsLook.Open(d, s);
            if (groups.Contains(SettingsFile.LookGroup) && !fresh) KeepLook(to, s);
            var (never, presets) = (d.Unsuggested.ToList(), d.Eq.Custom.ToList());
            file.Apply(d, s, to.Info.Id, to.BackdropPicture, groups, again: true);
            d.Unsuggested.AddRange(never.Where(n => !to.IsUnsuggested(n)));
            d.Eq.Custom.AddRange(presets.Where(p => d.Eq.Custom.All(e => e.Name != p.Name)));
            to.Save();
        }
        open.Data.Bars?.ApplyTo(settings);
    }

    private static void KeepLook(Profile p, AppSettings s)
    {
        var mine = p.Data.Looks.FirstOrDefault(l => l.Id == p.Data.LookId);
        if (mine == null) p.Data.Looks.Add(mine = new LookPreset { Name = L.T("My look") });
        mine.Take(p.Data, s, p.Info.Id, p.BackdropPicture);
    }

    private static AppSettings Copy(AppSettings s) => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, JsonStore.Options), JsonStore.Options)!;

    // ------------------------------------------------------------------ what arrived

    // An id that's only a name: it makes paths, and what arrives comes from outside.
    private static bool Safe(string? id) => id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    // All of it, before anything's touched.
    private static Contents Read(string dir)
    {
        var aside = Path.Combine(dir, "data");
        try
        {
            var path = Path.Combine(dir, "data.zip");
            if (Backup.Read(path) == null) throw new InvalidDataException("data.zip");
            using (var zip = ZipFile.OpenRead(path)) Backup.Extract(zip, aside);
            T? Json<T>(params string[] name) where T : class
            {
                var file = Path.Combine(name.Prepend(aside).ToArray());
                return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file), JsonStore.Options) : null;
            }
            var tracks = (Json<LibraryFile>("library.json")?.Tracks ?? throw new InvalidDataException("library.json")).Where(t => !string.IsNullOrEmpty(t.Id)).GroupBy(t => t.Id).Select(g => g.First()).ToList();
            foreach (var t in tracks)
            {
                t.Path ??= "";
                t.Title ??= "";
                t.Keys ??= new();
            }
            var profiles = (Json<ProfileList>("profiles.json")?.Profiles ?? new()).Where(p => Safe(p.Id) && p.Name != null)
                .Select(p => new Source(p, Json<ProfileData>("profiles", p.Id, "data.json"), Path.Combine(aside, "profiles", p.Id))).ToList();
            return new Contents(tracks, tracks.Select(t => t.Id).ToHashSet(), Json<AppSettings>("settings.json"), profiles, Json<Dictionary<string, ArtistSite>>("artists.json") ?? new(), aside);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or EndOfStreamException)
        {
            throw new InvalidDataException(L.T("What arrived from the computer can't be read: send it again from there. If it happens again, update the app on both."), e);
        }
    }

    // Where each of the computer's profiles goes: the only one into the profile open here; several, each into the one with its name, else
    // a new one. The profile open here, when it's still empty, takes one (the first that has nowhere to go), with its name, colour and
    // picture: an empty one beside it would be of no use.
    private static List<(Source From, Profile To, bool Fresh)> Place(Contents c, ProfileList list, ProfileInfo open, ComputerResult r)
    {
        var places = new List<(Source, Profile, bool)>();
        ProfileInfo? Named(string name) => list.Profiles.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.CurrentCultureIgnoreCase));
        bool empty = Profile.Get(open).Data.Songs is not { Count: > 0 };
        var first = c.Profiles.Count == 1 ? c.Profiles[0]
            : empty && c.Profiles.All(s => Named(s.Info.Name) != open) ? c.Profiles.FirstOrDefault(s => Named(s.Info.Name) == null) : null;
        foreach (var s in c.Profiles)
        {
            var to = s == first ? open : Named(s.Info.Name);
            bool fresh = to == null;
            if (to == null)
            {
                list.Profiles.Add(to = new ProfileInfo());
                r.NewProfiles.Add(s.Info.Name);
            }
            if (fresh || to == open && empty)
            {
                var name = s.Info.Name.Trim();
                if (name.Length > 0 && (Named(name) ?? to) == to) to.Name = name.Length > 40 ? name[..40] : name;
                to.Color = s.Info.Color;
                var picture = Path.Combine(s.Dir, "avatar.jpg");
                if (s.Info.HasAvatar && File.Exists(picture))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to.AvatarPath)!);
                    File.Copy(picture, to.AvatarPath, true);
                    to.HasAvatar = true;
                    to.AvatarVersion++;
                }
                list.Save();
            }
            places.Add((s, Profile.Get(to), fresh));
        }
        if (places.Count == 0) places.Add((new Source(open, null, c.Dir), Profile.Get(open), false));
        return places;
    }

    // ------------------------------------------------------------------ the songs

    private static Dictionary<string, string> FilesIn(string dir, HashSet<string> extensions) => !Directory.Exists(dir) ? new()
        : Directory.EnumerateFiles(dir).Where(f => extensions.Contains(Path.GetExtension(f)) && new FileInfo(f).Length > 0)
            .GroupBy(f => Path.GetFileNameWithoutExtension(f)).ToDictionary(g => g.Key, g => g.First());

    // The computer's songs found in the library, or made ready for it (fresh): the computer's id of each → the one here. moves: the files
    // still to be put where their songs say.
    private static Dictionary<string, string> FindSongs(Contents c, string dir, Library lib, string musicDir, Func<string, bool> playable, Func<string, bool> canShow,
        List<Track> fresh, List<(string From, string To)> moves, ComputerResult r, Action<double> progress)
    {
        Directory.CreateDirectory(musicDir);
        var music = FilesIn(Path.Combine(dir, "music"), Importer.Extensions);
        var videos = FilesIn(Path.Combine(dir, "videos"), Importer.VideoExtensions);
        // (also the files not there yet: an import stopped halfway)
        var taken = lib.Snapshot().SelectMany(t => new[] { t.Path, t.VideoPath }).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var index = new LibraryIndex(lib);
        var map = new Dictionary<string, string>();

        string Free(Track t, string from)
        {
            var name = Text.SafeFileName(string.IsNullOrWhiteSpace(t.Artist) ? t.Title : $"{t.Artist.Split(',')[0].Trim()} - {t.Title}");
            var ext = Path.GetExtension(from);
            var path = Text.UniquePath(musicDir, name, ext);
            for (int i = 2; !taken.Add(path); i++) path = Text.UniquePath(musicDir, $"{name} ({i})", ext);
            moves.Add((from, path));
            return path;
        }

        // The files that came, for a song with none here (one stopped halfway had its place already). True: it has changed.
        var put = new HashSet<string>();
        bool Put(Track t, string? audio, string? video, bool same)
        {
            bool any = false;
            if (audio != null && !t.HasSavedFile && put.Add(t.Id))
            {
                taken.Remove(t.Path);
                t.Path = Free(t, audio);
                if (same && Importer.VideoExtensions.Contains(Path.GetExtension(audio))) t.VideoPath = t.Path;
                r.Saved++;
                any = true;
            }
            if (video != null && t.IsSaved && !(t.HasVideo && File.Exists(t.VideoPath)) && put.Add("video " + t.Id))
            {
                taken.Remove(t.VideoPath ?? "");
                t.VideoPath = Free(t, video);
                any = true;
            }
            return any;
        }

        for (int i = 0; i < c.Tracks.Count; i++)
        {
            progress(i / (double)c.Tracks.Count);
            var t = c.Tracks[i];
            var was = t.Id;
            string? audio = music.GetValueOrDefault(was), video = videos.GetValueOrDefault(was);
            bool format = audio != null && !playable(audio);
            if (format) audio = null;
            if (video != null && !canShow(video)) video = null;
            bool same = t.HasVideo && string.Equals(t.VideoPath, t.Path, StringComparison.OrdinalIgnoreCase);
            t.Keys.RemoveAll(k => k.StartsWith("file:", StringComparison.OrdinalIgnoreCase));

            // Here already: under its own id (brought before), else as a pack's songs are told (LibraryIndex), but never another of the
            // computer's own songs: those are two there too.
            var id = Safe(was) && !AppPaths.IsLinkId(was) && !RadioSong.IsRadio(was) && !was.StartsWith("lt-", StringComparison.Ordinal) ? was : Next(was);
            var have = lib.Get(id);
            while (have != null && !Alike(have, t)) have = lib.Get(id = Next(id));
            if (have == null && index.Find(Describe(t, audio)) is { } found && !c.Ids.Contains(found.Id)) have = found;

            if (have != null)
            {
                r.Present++;
                if (format && !have.HasSavedFile && have.HasLink) r.Linked++;
                bool changed = Put(have, audio, video, same);
                if (have.Bpm == null && t.Bpm != null)
                {
                    have.Bpm = t.Bpm;
                    have.BeatOffset ??= t.BeatOffset;
                    changed = true;
                }
                if (!have.HasCover && TakeCover(c, was, have.Id))
                {
                    have.HasCover = true;
                    have.CoverVersion++;
                    changed = true;
                }
                if (have.Lyrics == null && t.Lyrics != null && TakeLyrics(c, was, have, t.Lyrics)) changed = true;
                lib.AddKeys(have, t.Keys);
                if (changed) lib.Changed(have);
                map[was] = have.Id;
                continue;
            }
            if (audio == null && !t.HasLink)
            {
                if (format) r.BadFormat++;
                else r.NoFile++;
                continue;
            }
            t.Id = id;
            t.Path = "";
            t.VideoPath = null;
            // (a file of the computer's own folders is the app's file here: deleting the song deletes it)
            if (audio == null && t.Site is null or "File locale" or "Pacchetto") t.Site = Sites.NameFor(t.SourceUrl!);
            else if (t.Site is null or "File locale") t.Site = "Pacchetto";
            if (audio == null && format) r.Linked++;
            Put(t, audio, video, same);
            t.HasCover = TakeCover(c, was, id);
            var lyrics = t.Lyrics;
            t.Lyrics = null;
            TakeLyrics(c, was, t, lyrics);
            fresh.Add(t);
            r.Added++;
            map[was] = id;
        }
        return map;
    }

    // The id a song gets instead of one that's taken: always the same, so it's found again the next time.
    private static string Next(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("from-computer|" + id)))[..12].ToLowerInvariant();

    // Two songs with the same id, the same song: they may have been renamed since (a song with no link, by its length).
    private static bool Alike(Track a, Track b)
        => a.Keys.Intersect(b.Keys, StringComparer.OrdinalIgnoreCase).Any() || a.HasLink && a.SourceUrl == b.SourceUrl
           || SongMeta.Key(a.Title, a.Artist) == SongMeta.Key(b.Title, b.Artist) || a.Keys.Count + b.Keys.Count == 0 && a.Duration > 0 && Math.Abs(a.Duration - b.Duration) < 0.05;

    private static PackTrack Describe(Track t, string? audio)
    {
        var p = new PackTrack { Id = t.Id, Title = t.Title, Artist = t.Artist, Duration = t.Duration, Keys = t.Keys };
        // (by its contents only when its name doesn't say enough: that's reading it all)
        if (audio != null && string.IsNullOrWhiteSpace(t.Artist) && t.Keys.Count == 0)
        {
            p.Size = new FileInfo(audio).Length;
            p.Hash = PackWriter.HashFile(audio);
        }
        return p;
    }

    private static bool TakeCover(Contents c, string was, string id) => Safe(was) && Take(Path.Combine(c.Dir, "covers", was + ".jpg"), AppPaths.TrackCover(id));

    private static bool TakeLyrics(Contents c, string was, Track t, LyricsKind? kind)
    {
        if (kind is null or LyricsKind.None or LyricsKind.Instrumental)
        {
            t.Lyrics = kind;
            return kind != null;
        }
        bool any = false;
        if (Safe(was))
            foreach (var synced in new[] { true, false })
                any |= Take(Path.Combine(c.Dir, "lyrics", was + (synced ? ".lrc" : ".txt")), LyricsStore.PathFor(t.Id, synced));
        if (any) t.Lyrics = kind;
        return any;
    }

    private static bool Take(string from, string to)
    {
        if (!File.Exists(from)) return false;
        File.Move(from, to, true);
        return true;
    }

    // Who the library's artists are, and their pictures: the ones missing here, and the ones chosen by hand there (not here).
    private static void AddArtists(Contents c)
    {
        foreach (var (name, site) in c.Artists)
            if (site != null && (ArtistSites.Get(name) is not { } have || site.Mine && !have.Mine)) ArtistSites.Set(name, site);
        var dir = Path.Combine(c.Dir, "covers", "artists");
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*.jpg"))
            if (Path.GetFileNameWithoutExtension(file) is var id && Safe(id) && !File.Exists(AppPaths.ArtistPicture(id))) Take(file, AppPaths.ArtistPicture(id));
    }

    // ------------------------------------------------------------------ a profile's own

    // A profile's songs there (one from before 5.5 has the whole library). rest: also the ones no profile has.
    private static IEnumerable<string> SongsOf(Contents c, Source s, bool rest)
    {
        IEnumerable<string> Own(ProfileData? d) => d == null ? Array.Empty<string>() : d.Songs ?? c.Ids;
        return rest ? Own(s.Data).Concat(c.Ids.Except(c.Profiles.SelectMany(p => Own(p.Data)))) : Own(s.Data);
    }

    // Its tags, playlists, followed artists and radios.
    private static void AddLists(Source s, Profile to, Dictionary<string, string> map, ComputerResult r)
    {
        List<string> Here(IEnumerable<string> ids) => ids.Where(map.ContainsKey).Select(id => map[id]).ToList();
        var d = s.Data!;
        var tags = new Dictionary<string, string>();
        foreach (var tag in d.Tags)
            if (PackImporter.SameName(to, new PackTag { Name = tag.Name }) is { } have) tags[tag.Id] = have.Id;
            else
            {
                tags[tag.Id] = to.CreateTag(tag.Name, tag.Color).Id;
                r.NewTags++;
            }
        foreach (var (tag, mine) in tags) to.SetTag(Here(d.TrackTags.Where(kv => kv.Value.Contains(tag)).Select(kv => kv.Key)), mine, true);

        foreach (var p in d.Playlists)
        {
            var target = PackImporter.SameName(to, new PackPlaylist { Name = p.Name, Favorites = p.IsFavorites });
            if (target == null)
            {
                target = to.CreatePlaylist(p.Name, p.SourceUrl);
                target.Sort = p.Sort;
                r.NewPlaylists++;
            }
            if (p.HasCover && !target.HasCover && Safe(p.Id) && Take(Path.Combine(s.Dir, "pl-" + p.Id + ".jpg"), to.PlaylistCover(target))) to.SetPlaylistCover(target, true);
            to.AddTracks(target, Here(p.Tracks));
            foreach (var tag in p.Tags.Where(tags.ContainsKey)) to.SetPlaylistTag(target, tags[tag], true);
        }

        foreach (var artist in d.Artists) to.Follow(artist);
        foreach (var station in d.Stations.AsEnumerable().Reverse()) to.SaveStation(station);
        foreach (var song in d.NoVideo) to.SetNoVideo(song, true);
    }

    private static Dictionary<string, TrackStats> Totals(Source s) => Sealed.Load<SealedStats>(Path.Combine(s.Dir, "stats.dat"))?.Stats ?? s.Data!.OldStats ?? new();

    // The songs of its statistics and of its recently played that aren't in a library (suggested, heard from a link): the ones this
    // profile doesn't know, to be given to it, and all of them in here, like the library's.
    private static List<RadioSong> HeardSongs(Contents c, Source s, Profile to, Library lib, Dictionary<string, TrackStats> totals, Dictionary<string, string> here)
    {
        var named = s.Data!.History.Select(h => h.TrackId).Concat(totals.Keys).ToHashSet();
        var bring = new List<RadioSong>();
        foreach (var song in s.Data.Radio.Where(x => RadioSong.IsRadio(x.Id) && Safe(x.Id) && named.Contains(x.Id) && x.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)))
        {
            // (one that's in its library here: as RadioViewModel.Adopt takes them)
            if (lib.FindByKeys(song.Keys) is { } have && to.Has(have.Id))
            {
                here[song.Id] = have.Id;
                continue;
            }
            var known = to.Data.Radio.FirstOrDefault(x => x.Id == song.Id) ?? to.Data.Radio.FirstOrDefault(x => x.Keys.Intersect(song.Keys, StringComparer.OrdinalIgnoreCase).Any());
            here[song.Id] = known?.Id ?? song.Id;
            if (known != null) continue;
            // (another phone's, connected to that computer)
            song.Phone = null;
            TakeCover(c, song.Id, song.Id);
            bring.Add(song);
        }
        return bring;
    }

    // How many plays they added.
    private static int AddStats(Source s, Profile to, Dictionary<string, TrackStats> totals, Dictionary<string, string> here)
    {
        // (its own folder only to be read: never saved)
        var log = new ListeningLog(Path.Combine(s.Dir, "stats"));
        log.Close();
        var months = log.Years().SelectMany(y => (log.Year(y)?.Months ?? new()).Where(m => m.Key is >= 1 and <= 12).Select(m => (y, m.Key, m.Value))).ToList();
        int plays = to.AddStats(s.Info.Id, totals, months, id => here.GetValueOrDefault(id), s.Data!.StatsSince);
        to.MergeHistory(s.Data.History.Where(h => here.ContainsKey(h.TrackId)).Select(h => new HistoryEntry { TrackId = here[h.TrackId], At = h.At }));
        to.Flush();
        return plays;
    }
}
