using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using UltimateMP3Player.Core;
using UltimateMP3Player.Core.Link;
using UltimateMP3Player.Core.Together;
using UltimateMP3Player.Services;

namespace UltimateMP3Player.ViewModels;

// Connect phone: the other device's music on this one (and this one's there), while connected. Its songs are Tracks kept in
// memory ("ph-" + their id on the computer, "pc-" + it on the phone), its profile a mirror Profile (playlists, Favorites,
// tags, history) with the same ids; a song both have shows once, as this device's. What the pages show depends on the
// choice of music (LinkMusic: both, only the phone's, only the computer's). An edit of the mirror is sent there and done
// there (the other device's own songs it needs are sent along, "Adopt"); what changes there comes back the same way, so
// both devices always show the same. Songs of the other device play from there (their files sent over the link) or from
// their links; plays count where the songs are.
public sealed class SharedLibrary
{
    // Songs heard recently the other device gets (Home's "Recently played"), covers fetched at a time.
    private const int HistoryLength = 60;
    // A saved song of the other device: its file is there (Track.IsSaved true, HasSavedFile false).
    public const string RemotePath = "remote:";

    private readonly MainViewModel _main;
    private readonly LinkEnd _link;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _flush = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _rebuild = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _heardTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly SemaphoreSlim _coverGate = new(4);
    private bool _disposed, _applying;
    private int _req;
    private Task _edits = Task.CompletedTask;

    // ---- the other device's music, by its own ids
    private readonly Dictionary<string, LinkTrack> _theirs = new();
    private List<Playlist> _rawLists = new();
    private List<Tag> _rawTags = new();
    private Dictionary<string, List<string>> _rawTrackTags = new();
    private List<HistoryEntry> _rawHistory = new();
    // ---- as shown here
    private readonly Dictionary<string, Track> _tracks = new();
    private List<Track> _visible = new();
    // Songs both have: their id → this device's, and back.
    private readonly Dictionary<string, string> _dupe = new();
    private readonly Dictionary<string, string> _dupeBack = new();
    // For the songs both have without the same link: this device's by title (built when first needed, kept up to date),
    // the other device's as compared (worked out once per song: every rebuild goes through all of them).
    private LibraryIndex? _similar;
    private readonly ConditionalWeakTable<LinkTrack, SongIdentity> _theirIds = new();
    private HashSet<string> _mirrorLists = new();
    private readonly Dictionary<string, int> _listCovers = new();

    // ---- this device's changes not sent yet (to the other's mirror), and edits of the mirror not sent yet (to the owner)
    private readonly HashSet<string> _ownTracks = new(), _ownGone = new(), _ownLists = new(), _ownTrackTags = new();
    private HashSet<string> _sentLists = new();
    private bool _ownTags, _ownHistory;
    private readonly HashSet<string> _editLists = new(), _editTrackTags = new();
    private bool _editTags, _editListSet;

    // ---- covers and files
    private readonly HashSet<string> _coverFresh = new();
    private readonly Dictionary<string, Task<string?>> _coversInFlight = new();
    private readonly Dictionary<string, string> _offers = new();
    private readonly Dictionary<string, (double Seconds, bool Play, bool Start)> _heard = new();

    public SharedLibrary(MainViewModel main, LinkEnd link, bool onPhone)
    {
        _main = main;
        _link = link;
        _ui = Application.Current.Dispatcher;
        OnPhone = onPhone;
        Prefix = onPhone ? "pc-" : "ph-";
        Music = main.Host.Settings.LinkMusic;
        Audio = main.Host.Settings.LinkAudio;
        _flush.Tick += (_, _) => Flush();
        _rebuild.Tick += (_, _) => Rebuild();
        _heardTimer.Tick += (_, _) => FlushHeard();
        main.Library.TrackAdded += OnOwnTrack;
        main.Library.TrackChanged += OnOwnTrack;
        main.Library.TrackRemoved += OnOwnTrackGone;
        main.Profile.PlaylistChanged += OnOwnList;
        main.Profile.PlaylistsChanged += OnOwnLists;
        main.Profile.TagsChanged += OnOwnTags;
        main.Profile.TrackTagsChanged += OnOwnTrackTags;
        _sentLists = main.Profile.PlaylistsSnapshot().Select(p => p.Id).ToHashSet();
    }

    // This device is the phone (the other one the computer).
    public bool OnPhone { get; }
    // What the ids of the other device's songs, playlists and tags start with here.
    public string Prefix { get; }
    public Profile? Mirror { get; private set; }
    // The other device's music has arrived.
    public bool Ready => Mirror != null;
    public LinkProfile? OtherProfile { get; private set; }
    public string OtherName => OtherProfile?.Name is { Length: > 0 } n ? n : _link.OtherName;
    public string OtherColor => OtherProfile?.Color ?? "#7C5CFF";

    private ImageSource? _otherAvatar;
    private bool _avatarDone;
    public ImageSource? OtherAvatar
    {
        get
        {
            if (!_avatarDone)
            {
                _avatarDone = true;
                _otherAvatar = OtherProfile?.Avatar is { Length: > 0 } a ? Images.FromBytes(a, 96) : null;
            }
            return _otherAvatar;
        }
    }

    public LinkMusic Music { get; private set; }
    public LinkAudio Audio { get; private set; }
    private LinkMusic Mine => OnPhone ? LinkMusic.Phone : LinkMusic.Computer;
    // This device's music is shown (also until the other's arrives), and the other's.
    public bool ShowsOwn => !Ready || Music == LinkMusic.Both || Music == Mine;
    public bool ShowsOther => Ready && Music != Mine;
    public bool ShowsBoth => ShowsOwn && ShowsOther;

    // The modes changed (the connection windows show them), the music to hear changed (the player follows).
    public event Action? ModesChanged;

    // ------------------------------------------------------------------ what the pages ask

    public bool IsTheirs(string id) => id.StartsWith(Prefix, StringComparison.Ordinal);
    public string Strip(string id) => IsTheirs(id) ? id[Prefix.Length..] : id;

    // A song of the other device shown here.
    public Track? Get(string id) => _tracks.GetValueOrDefault(id);

    // The other device's songs the pages show (not the ones this device has too: those are this device's rows).
    public IReadOnlyList<Track> OtherSongs => _visible;
    // This device's songs the other device has too.
    public IEnumerable<string> SharedIds => _dupe.Values;

    // The other device's id of a song shown here: one of its own, or one both have.
    public string? TheirId(string id)
    {
        // (also a song of the other device's queue that isn't in its library: a suggestion there)
        if (IsTheirs(id)) return id[Prefix.Length..];
        if (_dupeBack.TryGetValue(id, out var their)) return their;
        // (kept here from there a moment ago: not matched yet)
        return _main.Library.Get(id) is { } t && KeyId(t) is { } k && _theirs.ContainsKey(k) ? k : null;
    }

    // Kept here from the other device: the other device's id it remembers.
    private string? KeyId(Track t)
    {
        var prefix = OnPhone ? "computer:" : "phone:";
        return t.Keys.FirstOrDefault(k => k.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    }

    // The key a song kept here from the other device gets (its id there).
    private string TheirKey(string their) => OnPhone ? LinkTrack.ComputerKey(their) : LinkTrack.PhoneKey(their);

    // This device's song for one of the other device's (it came from here, or both have it: the same link, or the same
    // title, artist and length, as for downloads and packs: songs added from files have no link).
    public Track? FindLocal(LinkTrack lt)
    {
        if (lt.Yours is { } y && _main.Library.Get(y) is { } mine) return mine;
        if (_main.Library.FindByKeys(lt.Keys.Append(TheirKey(lt.Id))) is { } byKey) return byKey;
        if (lt.Duration <= 0) return null;
        _similar ??= new LibraryIndex(_main.Library);
        return _similar.FindSimilar(_theirIds.GetValue(lt, x => SongMeta.Identity(x.Title, x.Artist)), lt.Duration);
    }

    // The id a song of the other device has here (its own row, or this device's song when both have it); null when unknown.
    public string? Here(string their) => _dupe.TryGetValue(their, out var l) ? l : _theirs.ContainsKey(their) ? Prefix + their : null;

    // This song as the other device needs it (its own id there when known).
    public LinkTrack Describe(Track t)
    {
        var lt = LinkTrack.From(t);
        if (IsTheirs(t.Id))
        {
            // (one of the other device's: its id there, which is what it'll look for)
            lt.Id = t.Id;
            lt.Yours = Strip(t.Id);
        }
        else lt.Yours = TheirId(t.Id);
        return lt;
    }

    // Played from the other device: one of its songs, one kept here from it whose file hasn't come yet, one in this
    // device's cloud saved there (quicker than its link, which stays for when the other device can't send it).
    public bool Streams(Track t) => _link.IsLinked && !t.HasSavedFile && TheirId(t.Id) is { } their &&
                                     (IsTheirs(t.Id) || _main.Library.Get(t.Id) == t && (!t.HasLink || _theirs.TryGetValue(their, out var lt) && lt.File));

    // How the cache gets it: from the other device (its link, when that one hasn't got the file).
    public RadioSong StreamOf(Track t)
    {
        var s = CloudSongs.StreamOf(t);
        s.Phone = TheirId(t.Id);
        s.Service = OtherName;
        return s;
    }

    // Not saved on both devices: "Save" (which asks where) has something to do.
    public bool NotEverywhere(Track t)
    {
        if (IsTheirs(t.Id)) return true;
        return TheirId(t.Id) is not { } their || !_theirs.TryGetValue(their, out var lt) || !lt.Saved;
    }

    // Who counts a song heard: this profile, and/or the other device (its id there).
    public (bool Here, string? There) Counters(string id)
    {
        var their = TheirId(id);
        bool mine = !IsTheirs(id);
        bool here = mine && (ShowsOwn || their == null);
        return (here, their != null && (ShowsOther || !mine) ? their : null);
    }

    // ------------------------------------------------------------------ starting, stopping

    // Connected (again): the other device's music is asked for, all of it.
    public void Start()
    {
        if (_disposed) return;
        if (!OnPhone) SendModes();
        _ = AskCatalog();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _flush.Stop();
        _rebuild.Stop();
        FlushHeard();
        _heardTimer.Stop();
        _main.Library.TrackAdded -= OnOwnTrack;
        _main.Library.TrackChanged -= OnOwnTrack;
        _main.Library.TrackRemoved -= OnOwnTrackGone;
        _main.Profile.PlaylistChanged -= OnOwnList;
        _main.Profile.PlaylistsChanged -= OnOwnLists;
        _main.Profile.TagsChanged -= OnOwnTags;
        _main.Profile.TrackTagsChanged -= OnOwnTrackTags;
        if (Mirror is { } m) Unhook(m);
        foreach (var id in _tracks.Keys) _main.ForgetVm(id);
        _tracks.Clear();
        _visible = new();
        _offers.Clear();
        // (the other device's covers were only for while connected)
        var dir = AppPaths.LinkDir;
        _ = Task.Run(() =>
        {
            try { foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.Delete(f); }
            catch { }
        });
    }

    // ------------------------------------------------------------------ the modes

    public void SetModes(LinkMusic music, LinkAudio audio) => SetModes(music, audio, true);

    private void SetModes(LinkMusic music, LinkAudio audio, bool send)
    {
        bool musicChanged = Music != music, audioChanged = Audio != audio;
        Music = music;
        Audio = audio;
        var s = _main.Host.Settings;
        if (s.LinkMusic != music || s.LinkAudio != audio)
        {
            s.LinkMusic = music;
            s.LinkAudio = audio;
            s.Save();
        }
        if (send) SendModes();
        if (musicChanged && Ready) _main.OnSharedChanged();
        if (musicChanged || audioChanged) ModesChanged?.Invoke();
    }

    private void SendModes() => _link.Send(new Msg { T = "modes", Index = (int)Music, Max = (int)Audio });

    // ------------------------------------------------------------------ messages

    // True when the message was one of these.
    public bool OnMessage(Msg m)
    {
        if (_disposed) return false;
        switch (m.T)
        {
            case "modes":
                if (Enum.IsDefined(typeof(LinkMusic), m.Index) && Enum.IsDefined(typeof(LinkAudio), m.Max))
                    SetModes((LinkMusic)m.Index, (LinkAudio)m.Max, false);
                return true;
            case "libAsk":
                if (m.Text is { Length: > 0 and < 64 } req) _ = SendCatalog(req);
                return true;
            case "lib":
                if (m.Lib != null) Apply(m.Lib);
                return true;
            case "edit":
                if (m.Lib != null)
                {
                    var e = m.Lib;
                    _edits = _edits.ContinueWith(_ => ApplyEdit(e), TaskScheduler.FromCurrentSynchronizationContext()).Unwrap();
                }
                return true;
            case "want":
                if (m.Text is { Length: > 0 and < 64 } songReq && _link.Connection is { } wc)
                    _ = FileLane.SendAsync(wc, songReq, m.Id == null ? null : FileOf(m.Id));
                return true;
            case "cover":
                if (m.Id is { Length: > 0 and < 64 } coverOf && _link.Connection is { } cc)
                {
                    var path = AppPaths.TrackCover(coverOf);
                    _ = FileLane.SendAsync(cc, "c:" + coverOf, _main.Library.Get(coverOf) is { HasCover: true } || _main.Radio.Track(coverOf) is { HasCover: true } ? path : null);
                }
                return true;
            case "plc":
                if (m.Id is { Length: > 0 and < 64 } listId && _link.Connection is { } pc)
                    _ = FileLane.SendAsync(pc, "p:" + listId, ListCover(listId));
                return true;
            case "get":
                if (m.Text is { } offer && _offers.Remove(offer, out var file) && _link.Connection is { } gc)
                    _ = FileLane.SendAsync(gc, offer, file);
                return true;
            case "heard":
                OnHeard(m);
                return true;
            case "doSave":
            case "doUnsave":
            case "doDelete":
            case "doInfo":
            case "doCover":
            case "doKeep":
                _ = Do(m);
                return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ this device's music, for the other one

    private async Task AskCatalog()
    {
        var link = _link;
        var tmp = Path.Combine(AppPaths.TempDir, "link-" + Ids.New() + ".gz");
        var req = "l" + Interlocked.Increment(ref _req);
        var wait = link.Files.ReceiveAsync(req, _ => tmp, null, CancellationToken.None);
        link.Send(new Msg { T = "libAsk", Text = req });
        var file = await wait;
        if (file == null || _disposed) return;
        try
        {
            var lib = await Task.Run(() =>
            {
                using var fs = File.OpenRead(file);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                return JsonSerializer.Deserialize<LinkLib>(gz, Msg.Json);
            });
            if (lib != null && !_disposed)
            {
                lib.Full = true;
                Apply(lib);
            }
        }
        catch (Exception ex) { _main.Toast(L.F("Couldn't get the music of {0}: {1}", OtherName, ex.Message)); }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    // All of this device's music, compressed, through the file lane (it can be big: the messages go on meanwhile).
    private async Task SendCatalog(string req)
    {
        if (_link.Connection is not { } c) return;
        var profile = _main.Profile;
        var lib = new LinkLib
        {
            Full = true, Profile = OwnProfile(), Tracks = _main.Library.Snapshot().Select(Describe).ToList(),
            Lists = profile.PlaylistsSnapshot().Select(p => p.Clone()).ToList(), Tags = profile.TagsSnapshot().Select(CloneTag).ToList(),
            TrackTags = profile.TrackTagsSnapshot(), History = profile.HistorySnapshot(HistoryLength),
        };
        _sentLists = lib.Lists.Select(p => p.Id).ToHashSet();
        var tmp = Path.Combine(AppPaths.TempDir, "link-" + Ids.New() + ".gz");
        try
        {
            await Task.Run(() =>
            {
                using var fs = File.Create(tmp);
                using var gz = new GZipStream(fs, CompressionLevel.Fastest);
                JsonSerializer.Serialize(gz, lib, Msg.Json);
            });
            await FileLane.SendAsync(c, req, tmp);
        }
        catch { await FileLane.SendAsync(c, req, null); }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private LinkProfile OwnProfile()
    {
        var info = _main.Profile.Info;
        return new LinkProfile { Name = info.Name, Color = info.Color, Avatar = info.HasAvatar ? Images.Jpeg(info.AvatarPath, 96) : null };
    }

    private static Tag CloneTag(Tag t) => new() { Id = t.Id, Name = t.Name, Color = t.Color };

    // (the library and the profile raise these on any thread)
    private void OnOwnTrack(Track t) => _ui.BeginInvoke(() =>
    {
        if (_disposed) return;
        _ownTracks.Add(t.Id);
        _ownGone.Remove(t.Id);
        _similar?.Add(t);
        Schedule();
        // (a song kept here may be one of the other device's: shown once from now on)
        _rebuild.Stop();
        _rebuild.Start();
    });

    private void OnOwnTrackGone(Track t) => _ui.BeginInvoke(() =>
    {
        if (_disposed) return;
        _ownGone.Add(t.Id);
        _ownTracks.Remove(t.Id);
        Schedule();
        _rebuild.Stop();
        _rebuild.Start();
    });

    private void OnOwnList(Playlist p) => _ui.BeginInvoke(() =>
    {
        if (_disposed) return;
        _ownLists.Add(p.Id);
        Schedule();
    });

    private void OnOwnLists() => _ui.BeginInvoke(() =>
    {
        if (_disposed) return;
        _ownLists.Add("");
        Schedule();
    });

    private void OnOwnTags() => _ui.BeginInvoke(() =>
    {
        if (_disposed) return;
        _ownTags = true;
        Schedule();
    });

    private void OnOwnTrackTags(IReadOnlyCollection<string> ids)
    {
        var copy = ids.ToList();
        _ui.BeginInvoke(() =>
        {
            if (_disposed) return;
            _ownTrackTags.UnionWith(copy);
            Schedule();
        });
    }

    // The songs heard here changed (the player): the other device's "Recently played".
    public void OwnHistory()
    {
        _ownHistory = true;
        Schedule();
    }

    private void Schedule()
    {
        if (!_flush.IsEnabled) _flush.Start();
    }

    // A burst of changes goes out as one message: this device's (for the other's mirror), and edits of the mirror (for the
    // other device).
    private void Flush()
    {
        _flush.Stop();
        if (_disposed) return;
        if (_link.Connection == null)
        {
            // (sent once connected again: the other device asks for everything then anyway)
            ClearOwn();
            ClearEdits();
            return;
        }
        var own = OwnDelta();
        if (own != null) _link.Send(new Msg { T = "lib", Lib = own });
        var edit = EditDelta();
        if (edit != null) _link.Send(new Msg { T = "edit", Lib = edit });
    }

    private void ClearOwn()
    {
        _ownTracks.Clear();
        _ownGone.Clear();
        _ownLists.Clear();
        _ownTrackTags.Clear();
        _ownTags = _ownHistory = false;
    }

    private LinkLib? OwnDelta()
    {
        var lib = new LinkLib();
        var library = _main.Library;
        var profile = _main.Profile;
        if (_ownTracks.Count > 0) lib.Tracks = _ownTracks.Select(library.Get).OfType<Track>().Select(Describe).ToList();
        if (_ownGone.Count > 0) lib.Gone = _ownGone.ToList();
        if (_ownLists.Count > 0)
        {
            var now = profile.PlaylistsSnapshot();
            var ids = now.Select(p => p.Id).ToHashSet();
            lib.Lists = now.Where(p => _ownLists.Contains(p.Id) || !_sentLists.Contains(p.Id)).Select(p => p.Clone()).ToList();
            lib.ListsGone = _sentLists.Where(id => !ids.Contains(id)).ToList();
            _sentLists = ids;
        }
        if (_ownTags) lib.Tags = profile.TagsSnapshot().Select(CloneTag).ToList();
        if (_ownTrackTags.Count > 0) lib.TrackTags = _ownTrackTags.ToDictionary(id => id, id => profile.TagsOf(id).ToList());
        if (_ownHistory) lib.History = profile.HistorySnapshot(HistoryLength);
        ClearOwn();
        return lib.IsEmpty ? null : lib;
    }

    // ------------------------------------------------------------------ the other device's music, here

    private void Apply(LinkLib lib)
    {
        bool tracks = false;
        if (lib.Full)
        {
            _theirs.Clear();
            _rawLists = new();
            _rawTags = new();
            _rawTrackTags = new();
            _rawHistory = new();
            tracks = true;
        }
        if (lib.Profile != null)
        {
            OtherProfile = lib.Profile;
            _avatarDone = false;
            _otherAvatar = null;
        }
        foreach (var lt in lib.Tracks ?? new())
        {
            if (lt.Id.Length is 0 or > 64) continue;
            _theirs[lt.Id] = lt;
            var id = Prefix + lt.Id;
            _tracks.TryGetValue(id, out var t);
            bool isNew = t == null;
            _tracks[id] = Fill(t ?? new Track { Id = id }, lt);
            if (isNew)
            {
                tracks = true;
                // (shown before as a stand-in of the other device's queue: the rows take this one)
                _main.ForgetVm(id);
            }
            else _main.RefreshVm(id);
        }
        var removed = new HashSet<string>();
        // (everything again: the songs no longer there are gone)
        var stale = lib.Full ? _tracks.Keys.Where(id => !_theirs.ContainsKey(id[Prefix.Length..])).ToList() : new List<string>();
        foreach (var id in stale)
        {
            _tracks.Remove(id);
            _main.ForgetVm(id);
            removed.Add(id);
        }
        foreach (var gone in lib.Gone ?? new())
        {
            _theirs.Remove(gone);
            if (_tracks.Remove(Prefix + gone))
            {
                _main.ForgetVm(Prefix + gone);
                removed.Add(Prefix + gone);
            }
            tracks = true;
        }
        // Deleted there: out of the queue here (the next one plays if it was this one).
        if (removed.Count > 0) _ = _main.Player.RemoveTracks(removed);
        if (lib.Lists != null)
        {
            var map = _rawLists.ToDictionary(p => p.Id);
            foreach (var p in lib.Lists) map[p.Id] = p;
            _rawLists = (lib.Full ? lib.Lists : _rawLists.Select(p => map[p.Id]).Concat(lib.Lists.Where(p => _rawLists.All(r => r.Id != p.Id)))).ToList();
        }
        if (lib.ListsGone != null) _rawLists.RemoveAll(p => lib.ListsGone.Contains(p.Id));
        if (lib.Tags != null) _rawTags = lib.Tags;
        if (lib.TrackTags != null)
            foreach (var (id, list) in lib.TrackTags)
                if (list.Count == 0) _rawTrackTags.Remove(id);
                else _rawTrackTags[id] = list;
        if (lib.History != null) _rawHistory = lib.History;
        bool first = Mirror == null;
        Rebuild();
        if (first) _main.OnSharedChanged();
        else
        {
            if (tracks) _main.OnSharedTracks();
            if (lib.Profile != null || lib.History != null) _main.OnSharedProfile();
        }
    }

    // A song of the other device as shown here (the same object stays, so the rows showing it follow).
    private Track Fill(Track t, LinkTrack lt)
    {
        t.Title = lt.Title;
        t.Artist = lt.Artist;
        t.Album = lt.Album;
        t.Year = lt.Year;
        t.Duration = lt.Duration;
        t.SourceUrl = lt.SourceUrl;
        t.Site = lt.Site;
        t.Keys = lt.Keys.Append(TheirKey(lt.Id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        t.ArtUrl = lt.ArtUrl;
        t.Loudness = lt.Loudness;
        t.Peak = lt.Peak;
        t.Bpm = lt.Bpm;
        t.Path = lt.Saved ? RemotePath : "";
        t.Unavailable = lt.Unavailable;
        t.AudioQuality = lt.Quality;
        t.Added = lt.Added;
        if (lt.Wave != null) t.Wave = lt.Wave;
        if (t.HasCover != lt.Cover || t.CoverVersion != lt.CoverVersion)
        {
            t.HasCover = lt.Cover;
            t.CoverVersion = lt.CoverVersion;
            _coverFresh.Remove(t.Id);
        }
        // (heard here from the cache before: found again there)
        t.CachePath ??= _main.Host.SongCache.Find(t.Keys);
        return t;
    }

    // The mirror again from the other device's music: which songs both have, the ids as shown here, the playlists, tags
    // and history put in place (only what differs raises a change).
    private void Rebuild()
    {
        _rebuild.Stop();
        if (_disposed) return;
        _dupe.Clear();
        _dupeBack.Clear();
        var visible = new List<Track>();
        foreach (var (id, lt) in _theirs)
        {
            if (FindLocal(lt) is { } mine)
            {
                _dupe[id] = mine.Id;
                _dupeBack.TryAdd(mine.Id, id);
            }
            else if (_tracks.TryGetValue(Prefix + id, out var t)) visible.Add(t);
        }
        bool changedSet = visible.Count != _visible.Count || !visible.SequenceEqual(_visible);
        _visible = visible;

        string? Map(string their) => Here(their);
        var lists = _rawLists.Select(p =>
        {
            var c = p.Clone();
            c.Id = Prefix + p.Id;
            c.Tracks = p.Tracks.Select(Map).OfType<string>().Distinct().ToList();
            c.Tags = p.Tags.Select(t => Prefix + t).ToList();
            return c;
        }).ToList();
        if (lists.All(p => !p.IsFavorites)) lists.Insert(0, new Playlist { Id = Prefix + Playlist.FavoritesId, Name = "Favorites" });
        var tags = _rawTags.Select(t => new Tag { Id = Prefix + t.Id, Name = t.Name, Color = t.Color }).ToList();
        var trackTags = new Dictionary<string, List<string>>();
        foreach (var (their, list) in _rawTrackTags)
            if (Map(their) is { } id) trackTags[id] = list.Select(t => Prefix + t).ToList();
        var history = _rawHistory.Select(h => Map(h.TrackId) is { } id ? new HistoryEntry { TrackId = id, At = h.At } : null).OfType<HistoryEntry>().ToList();

        _applying = true;
        try
        {
            if (Mirror is not { } m)
            {
                var info = new ProfileInfo { Id = "link-" + Prefix.TrimEnd('-'), Name = OtherName, Color = OtherColor };
                m = Mirror = Profile.Mirror(info, new ProfileData { Playlists = lists, Tags = tags, TrackTags = trackTags, History = history },
                    Path.Combine(AppPaths.LinkDir, "lists"), Prefix);
                Hook(m);
            }
            else
            {
                if (!SameTags(m.TagsSnapshot(), tags)) m.PutTags(tags);
                var ids = lists.Select(p => p.Id).ToHashSet();
                var gone = m.PlaylistsSnapshot().Select(p => p.Id).Where(id => !ids.Contains(id)).ToList();
                if (gone.Count > 0) m.RemovePlaylists(gone);
                foreach (var p in lists)
                    if (m.GetPlaylist(p.Id) is not { } had || !SameList(had, p)) m.PutPlaylist(p);
                var put = new Dictionary<string, List<string>>(trackTags);
                foreach (var k in m.TrackTagsSnapshot().Keys)
                    if (!trackTags.ContainsKey(k)) put[k] = new();
                m.PutTrackTags(put);
                m.PutHistory(history);
            }
            _mirrorLists = lists.Select(p => p.Id).ToHashSet();
        }
        finally { _applying = false; }
        FetchListCovers();
        if (changedSet && Mirror != null) _main.OnSharedTracks();
    }

    private static bool SameTags(List<Tag> a, List<Tag> b)
        => a.Count == b.Count && a.Zip(b).All(x => x.First.Id == x.Second.Id && x.First.Name == x.Second.Name && x.First.Color == x.Second.Color);

    private static bool SameList(Playlist a, Playlist b)
        => a.Name == b.Name && a.HasCover == b.HasCover && a.CoverVersion == b.CoverVersion && a.Sort == b.Sort && a.SourceUrl == b.SourceUrl &&
           a.Tracks.SequenceEqual(b.Tracks) && a.Tags.SequenceEqual(b.Tags);

    // The pictures of the other device's playlists, when new or changed.
    private void FetchListCovers()
    {
        if (Mirror is not { } m) return;
        foreach (var p in m.PlaylistsSnapshot())
        {
            if (!p.HasCover || _listCovers.TryGetValue(p.Id, out var v) && v == p.CoverVersion) continue;
            _listCovers[p.Id] = p.CoverVersion;
            var path = m.PlaylistCover(p);
            try { File.Delete(path); } catch { }
            var list = p;
            _ = Fetch();

            async Task Fetch()
            {
                if (await FetchFile("p:" + Strip(list.Id), path, new Msg { T = "plc", Id = Strip(list.Id) }) == null || _disposed || Mirror != m) return;
                // (as it is now: songs added meanwhile stay; deleted meanwhile, nothing to show)
                if (m.GetPlaylist(list.Id) is not { } now) return;
                _applying = true;
                try { m.PutPlaylist(now.Clone()); }
                finally { _applying = false; }
                _main.RefreshList(list.Id);
            }
        }
    }

    // ------------------------------------------------------------------ edits of the mirror, sent to the other device

    private void Hook(Profile m)
    {
        m.PlaylistChanged += OnMirrorList;
        m.PlaylistsChanged += OnMirrorLists;
        m.TagsChanged += OnMirrorTags;
        m.TrackTagsChanged += OnMirrorTrackTags;
        _main.AttachMirror(m);
    }

    private void Unhook(Profile m)
    {
        m.PlaylistChanged -= OnMirrorList;
        m.PlaylistsChanged -= OnMirrorLists;
        m.TagsChanged -= OnMirrorTags;
        m.TrackTagsChanged -= OnMirrorTrackTags;
        _main.DetachMirror(m);
    }

    // (raised right away by the mirror's methods: what the mirror got from there isn't sent back)
    private void OnMirrorList(Playlist p)
    {
        if (_applying || _disposed) return;
        _editLists.Add(p.Id);
        Schedule();
    }

    private void OnMirrorLists()
    {
        if (_applying || _disposed) return;
        _editListSet = true;
        Schedule();
    }

    private void OnMirrorTags()
    {
        if (_applying || _disposed) return;
        _editTags = true;
        Schedule();
    }

    private void OnMirrorTrackTags(IReadOnlyCollection<string> ids)
    {
        if (_applying || _disposed) return;
        _editTrackTags.UnionWith(ids);
        Schedule();
    }

    private void ClearEdits()
    {
        _editLists.Clear();
        _editTrackTags.Clear();
        _editTags = _editListSet = false;
    }

    private LinkLib? EditDelta()
    {
        if (Mirror is not { } m) return null;
        var lib = new LinkLib();
        var adopt = new Dictionary<string, LinkTrack>();
        // A song for the other device: its id there, or one of this device's it must keep first.
        string? Ref(string id)
        {
            if (TheirId(id) is { } their) return their;
            var t = _main.Library.Get(id) ?? _main.Radio.Track(id);
            if (t == null) return null;
            if (!adopt.ContainsKey(id)) adopt[id] = Describe(t);
            return LinkLib.AdoptPrefix + id;
        }
        if (_editListSet || _editLists.Count > 0)
        {
            var now = m.PlaylistsSnapshot();
            var ids = now.Select(p => p.Id).ToHashSet();
            lib.Lists = now.Where(p => _editLists.Contains(p.Id) || !_mirrorLists.Contains(p.Id)).Select(p =>
            {
                var c = p.Clone();
                c.Id = Strip(p.Id);
                c.Tracks = p.Tracks.Select(Ref).OfType<string>().ToList();
                c.Tags = p.Tags.Where(IsTheirs).Select(Strip).ToList();
                // (its picture changed here: the other device takes it from here, this version is already here)
                if (p.HasCover) _listCovers[p.Id] = p.CoverVersion;
                return c;
            }).ToList();
            lib.ListsGone = _mirrorLists.Where(id => !ids.Contains(id)).Select(Strip).ToList();
            _mirrorLists = ids;
        }
        if (_editTags) lib.Tags = m.TagsSnapshot().Select(t => new Tag { Id = Strip(t.Id), Name = t.Name, Color = t.Color }).ToList();
        if (_editTrackTags.Count > 0)
        {
            lib.TrackTags = new();
            foreach (var id in _editTrackTags)
                if (Ref(id) is { } r) lib.TrackTags[r] = m.TagsOf(id).Where(IsTheirs).Select(Strip).ToList();
        }
        if (adopt.Count > 0) lib.Adopt = adopt.Values.ToList();
        ClearEdits();
        return lib.IsEmpty ? null : lib;
    }

    // An edit of this device's music made on the other one: its songs kept here first, then done as if here (the changes go
    // back by themselves).
    private async Task ApplyEdit(LinkLib e)
    {
        if (_disposed) return;
        var adopted = new Dictionary<string, string>();
        foreach (var lt in e.Adopt ?? new())
            if (await Adopt(lt, false) is { } t) adopted[LinkLib.AdoptPrefix + lt.Id] = t.Id;
        // (a suggestion of this device's queue the other one showed is kept in the library first)
        string? Mine(string r) => r.StartsWith(LinkLib.AdoptPrefix, StringComparison.Ordinal) ? adopted.GetValueOrDefault(r)
            : _main.Library.Get(r) != null ? r
            : _main.Radio.Item(r) is { } suggested ? _main.KeepInLibrary(_main.Vm(suggested.T)).Id : null;
        var profile = _main.Profile;
        if (e.Tags != null) profile.PutTags(e.Tags.Where(t => t.Id.Length is > 0 and < 64).ToList());
        foreach (var l in e.Lists ?? new())
        {
            if (l.Id.Length is 0 or > 64) continue;
            l.Tracks = l.Tracks.Select(Mine).OfType<string>().ToList();
            var had = profile.GetPlaylist(l.Id);
            if (l.HasCover && (had is not { HasCover: true } || had.CoverVersion != l.CoverVersion))
                l.HasCover = await FetchFile("p:" + l.Id, profile.PlaylistCover(l), new Msg { T = "plc", Id = l.Id }) != null;
            else if (!l.HasCover && had is { HasCover: true })
                try { File.Delete(profile.PlaylistCover(had)); } catch { }
            profile.PutPlaylist(l);
        }
        if (e.ListsGone is { Count: > 0 }) profile.RemovePlaylists(e.ListsGone);
        if (e.TrackTags != null)
        {
            var put = new Dictionary<string, List<string>>();
            foreach (var (r, list) in e.TrackTags)
                if (Mine(r) is { } id) put[id] = list;
            profile.PutTrackTags(put);
        }
    }

    // ------------------------------------------------------------------ songs going from one device to the other

    // Keeps here a song of the other device (an edit there needs it, or "Add to library" / "Save" for this device chose it):
    // into the library in the cloud when it has a link; its file from there when it has none or save asks for it. The song
    // here; null when it can't be heard here (no link and no file).
    public async Task<Track?> Adopt(LinkTrack lt, bool save)
    {
        var t = FindLocal(lt);
        if (t == null)
        {
            t = NewTrack(lt);
            if (lt.Cover && await FetchFile("c:" + lt.Id, AppPaths.TrackCover(t.Id), new Msg { T = "cover", Id = lt.Id }) != null) t.HasCover = true;
            if (!t.HasLink)
            {
                // (no link: only its file makes it a song here)
                var file = await FetchSong(lt.Id);
                if (file == null)
                {
                    _main.Toast(L.F("Couldn't keep “{0}”: {1} didn't send it", lt.Title, OtherName));
                    return null;
                }
                _main.Library.Add(t);
                await KeepFile(t, file);
                return t;
            }
            _main.Library.Add(t);
        }
        if (save && !t.HasSavedFile) await SaveHere(t, lt.Id);
        return t;
    }

    // The same, at once, for a song of the other device shown here (a playlist, the Favorites, a tag of this device need it
    // in the library now): its file comes after, meanwhile it's heard from there.
    public Track AdoptNow(Track shown, bool save)
    {
        var their = Strip(shown.Id);
        if (!_theirs.TryGetValue(their, out var lt))
        {
            // (a song of its queue that isn't in its library)
            lt = LinkTrack.From(shown);
            lt.Id = their;
        }
        if (FindLocal(lt) is { } have)
        {
            if (save && !have.HasSavedFile) _ = SaveHere(have, their);
            return have;
        }
        var t = NewTrack(lt);
        try
        {
            if (shown.HasCover && File.Exists(AppPaths.TrackCover(shown.Id)))
            {
                File.Copy(AppPaths.TrackCover(shown.Id), AppPaths.TrackCover(t.Id), true);
                t.HasCover = true;
            }
        }
        catch { }
        t.Wave = shown.Wave;
        t.CachePath = shown.CachePath;
        _main.Library.Add(t);
        if (!t.HasCover && lt.Cover) _ = CoverLater(t, their);
        if (save || !t.HasLink) _ = SaveHere(t, their);
        _rebuild.Stop();
        Rebuild();
        return t;
    }

    private async Task CoverLater(Track t, string their)
    {
        if (await FetchFile("c:" + their, AppPaths.TrackCover(t.Id), new Msg { T = "cover", Id = their }) == null) return;
        t.HasCover = true;
        t.CoverVersion++;
        _main.Library.Changed(t);
    }

    private Track NewTrack(LinkTrack lt) => new()
    {
        Title = lt.Title, Artist = lt.Artist, Album = lt.Album, Year = lt.Year, Duration = lt.Duration, SourceUrl = lt.SourceUrl, Site = lt.Site,
        Keys = lt.Keys.Append(TheirKey(lt.Id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ArtUrl = lt.ArtUrl, Wave = lt.Wave,
        Loudness = lt.Loudness, Peak = lt.Peak, Bpm = lt.Bpm, AudioQuality = lt.Quality,
    };

    // Saved on this device: the copy in the cache, or its file from the other device (no new download), otherwise from its
    // link. (A song without a link the other device didn't send stays unsaved: it goes when the app starts again.)
    public async Task SaveHere(Track t, string their)
    {
        if (t.HasSavedFile) return;
        if (t.CachePath is { } cached && File.Exists(cached))
        {
            if (await _main.SaveFromCache(t, cached)) _main.Toast(L.F("“{0}” saved on your device", t.Title));
            return;
        }
        var file = await FetchSong(their);
        if (file != null) await KeepFile(t, file);
        else if (t.HasLink) await _main.SaveOnThisDevice(new[] { _main.Vm(t) }, quiet: true, fromOther: false);
        else _main.Toast(L.F("Couldn't keep “{0}”: {1} didn't send it", t.Title, OtherName));
    }

    private async Task KeepFile(Track t, string file)
    {
        if (await _main.SaveFromCache(t, file)) _main.Toast(L.F("“{0}” saved on your device", t.Title));
        try { File.Delete(file); } catch { }
    }

    // The file of a song of the other device, into the cache's temporary folder; null when it can't send it.
    public Task<string?> FetchSong(string their, Action<long, long>? progress = null, CancellationToken ct = default)
    {
        var dir = _main.Host.SongCache.TempDir;
        Directory.CreateDirectory(dir);
        var req = "s" + Interlocked.Increment(ref _req);
        return FetchFile(req, Path.Combine(dir, Ids.New()), new Msg { T = "want", Id = their, Text = req }, progress, ct, extension: true);
    }

    // Asks the other device for a file (the message says which) and waits for it into target (with the file's own extension
    // when extension is true).
    private async Task<string?> FetchFile(string req, string target, Msg ask, Action<long, long>? progress = null, CancellationToken ct = default,
        bool extension = false)
    {
        var link = _link;
        if (link.Connection == null) return null;
        var wait = link.Files.ReceiveAsync(req, ext => extension ? target + ext : target, progress, ct);
        link.Send(ask);
        return await wait;
    }

    // The audio of one of this device's songs (the saved file, the suggestion's, the copy in the cache, the one playing).
    private string? FileOf(string id)
    {
        var t = _main.Get(id) ?? _main.Radio.Track(id);
        if (t == null) return null;
        var playing = _main.Player.Current?.Id == id ? _main.Player.OpenPath : null;
        return new[] { t.HasSavedFile ? t.Path : null, _main.Radio.PathFor(id), t.CachePath, playing }.FirstOrDefault(File.Exists);
    }

    // The picture of a playlist asked for by the other device: one of this profile's, or one of the mirror changed here.
    private string? ListCover(string id)
    {
        if (_main.Profile.GetPlaylist(id) is { HasCover: true } own) return _main.Profile.PlaylistCover(own);
        if (Mirror?.GetPlaylist(Prefix + id) is { HasCover: true } m) return Mirror.PlaylistCover(m);
        return null;
    }

    // The cover of a song of the other device, as its rows show it: fetched once per connection, a few at a time.
    public bool CoverReady(Track t)
    {
        if (_coverFresh.Contains(t.Id)) return true;
        if (!IsTheirs(t.Id) || _disposed) return false;
        _ = CoverFile(Strip(t.Id)).ContinueWith(r =>
        {
            if (r.Result == null || _disposed) return;
            _coverFresh.Add(t.Id);
            _main.RefreshVm(t.Id);
            if (_main.Player.Current?.T == t) _main.Host.OnTrackChanged();
        }, TaskScheduler.FromCurrentSynchronizationContext());
        return false;
    }

    // The cover of the other device's song, into AppPaths.TrackCover(Prefix + its id); the same request for whoever asks
    // meanwhile.
    public Task<string?> CoverFile(string their)
    {
        if (_coversInFlight.TryGetValue(their, out var running)) return running;
        var task = Fetch();
        _coversInFlight[their] = task;
        return task;

        async Task<string?> Fetch()
        {
            await _coverGate.WaitAsync();
            try { return await FetchFile("c:" + their, AppPaths.TrackCover(Prefix + their), new Msg { T = "cover", Id = their }); }
            finally
            {
                _coverGate.Release();
                _ui.BeginInvoke(() => _coversInFlight.Remove(their));
            }
        }
    }

    // A file this device offers the other one (it asks for it with "get" and req).
    private string Offer(string path)
    {
        var req = "o" + Interlocked.Increment(ref _req);
        _offers[req] = path;
        return req;
    }

    // ------------------------------------------------------------------ actions on the other device's songs

    // Saved on the other device (its own songs), or kept there (this device's: in its cloud, or with their file if save).
    public void SaveThere(IReadOnlyList<Track> tracks, bool save)
    {
        var theirs = new List<string>();
        var mine = new List<LinkTrack>();
        foreach (var t in tracks)
        {
            if (TheirId(t.Id) is { } their) theirs.Add(their);
            else mine.Add(Describe(t));
        }
        if (theirs.Count > 0 && save) _link.Send(new Msg { T = "doSave", Ids = theirs });
        if (mine.Count > 0) _link.Send(new Msg { T = "doKeep", Tracks = mine, Flag = save });
        int n = tracks.Count;
        _main.Toast(save ? n == 1 ? L.F("Saving “{0}” on {1}…", tracks[0].Title, OtherName) : L.F("Saving {0} songs on {1}…", n, OtherName)
            : n == 1 ? L.F("Added to the library on {0}", OtherName) : L.F("{0} songs added to the library on {1}", n, OtherName));
    }

    // These songs of the other device (their ids there): back to its cloud, deleted (what kind says), searched lyrics...
    public void Send(string what, IReadOnlyList<string> theirs, int index = 0)
    {
        if (theirs.Count > 0) _link.Send(new Msg { T = what, Ids = theirs.ToList(), Index = index });
    }

    public void EditThere(string their, string title, string? artist, string? album, double? bpm)
        => _link.Send(new Msg { T = "doInfo", Id = their, Tracks = new() { new LinkTrack { Id = their, Title = title, Artist = artist, Album = album, Bpm = bpm } } });

    public void CoverThere(string their, string image) => _link.Send(new Msg { T = "doCover", Id = their, Text = Offer(image) });

    // Done here for the other device.
    private async Task Do(Msg m)
    {
        var vms = (m.Ids ?? new()).Select(id => _main.Library.Get(id) ?? _main.Radio.Track(id)).OfType<Track>().Select(_main.Vm).ToList();
        switch (m.T)
        {
            case "doSave":
                await _main.SaveOnThisDevice(vms);
                break;
            case "doUnsave":
                await _main.RemoveFromDevice(vms);
                break;
            case "doDelete":
                if (Enum.IsDefined(typeof(RemoveKind), m.Index)) await _main.RemoveNow(vms, (RemoveKind)m.Index);
                break;
            case "doInfo":
                if (m.Id != null && _main.Library.Get(m.Id) is { } t && m.Tracks?.FirstOrDefault() is { } info && info.Title.Trim().Length > 0)
                {
                    t.Title = info.Title.Trim();
                    t.Artist = string.IsNullOrWhiteSpace(info.Artist) ? null : info.Artist.Trim();
                    t.Album = string.IsNullOrWhiteSpace(info.Album) ? null : info.Album.Trim();
                    t.Bpm = info.Bpm is > 20 and < 400 ? info.Bpm : null;
                    _main.Library.Changed(t);
                }
                break;
            case "doCover":
                if (m.Id != null && _main.Library.Get(m.Id) is { } ct && m.Text is { Length: > 0 and < 64 } offer)
                {
                    var tmp = Path.Combine(AppPaths.TempDir, "link-" + Ids.New());
                    var image = await FetchFile(offer, tmp, new Msg { T = "get", Text = offer }, extension: true);
                    if (image == null) break;
                    await _main.SetTrackCover(_main.Vm(ct), image);
                    try { File.Delete(image); } catch { }
                }
                break;
            case "doKeep":
                int kept = 0;
                foreach (var lt in m.Tracks ?? new())
                    if (await Adopt(lt, m.Flag) != null) kept++;
                if (kept > 0)
                    _main.Toast(kept == 1 ? L.F("A song from {0} was added to your library", OtherName) : L.F("{0} songs from {1} were added to your library", kept, OtherName));
                break;
        }
    }

    // ------------------------------------------------------------------ plays

    // A song of the other device heard here (or one both have): its plays count there, a few seconds at a time.
    public void Heard(string their, double seconds, bool play, bool start)
    {
        var (s, p, st) = _heard.GetValueOrDefault(their);
        _heard[their] = (s + seconds, p || play, st || start);
        if (play || start) FlushHeard();
        else if (!_heardTimer.IsEnabled) _heardTimer.Start();
    }

    private void FlushHeard()
    {
        _heardTimer.Stop();
        if (_heard.Count == 0) return;
        foreach (var (id, (seconds, play, start)) in _heard)
            _link.Send(new Msg { T = "heard", Id = id, Pos = seconds, Flag = play, Index = start ? 1 : 0 });
        _heard.Clear();
    }

    private void OnHeard(Msg m)
    {
        if (m.Id == null || _main.Library.Get(m.Id) == null || m.Pos is < 0 or > 3600) return;
        var profile = _main.Profile;
        if (m.Index == 1)
        {
            profile.AddHistory(m.Id);
            OwnHistory();
            _main.OnPlayed();
        }
        if (m.Pos > 0 || m.Flag) profile.AddListening(m.Id, m.Pos, m.Flag);
        _main.OnListened(m.Id, m.Flag || m.Index == 1);
    }
}
