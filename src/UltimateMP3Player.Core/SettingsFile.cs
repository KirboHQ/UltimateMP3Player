using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UltimateMP3Player.Core;

// Settings carried to another device or profile (Settings › Backup, export and import; or straight over the link with the computer):
// the groups ticked, as JSON. With all of them it's the profile's whole way of being (its look and presets too, not its music, playlists
// or plays: those are the backups' and the packs'); its look can also come in as a preset (LookPreset.Read). Left out: what belongs to
// one device (music folder, audio output, interface size, windows, backups, startup) and the keys and logins of accounts.
public sealed class SettingsFile
{
    public const string Extension = ".umpsettings", Kind = "Ultimate MP3 Player settings";
    public const string LookGroup = "look";

    public string? App { get; set; }
    public int Version { get; set; } = 1;
    public string? Profile { get; set; }
    public string? Device { get; set; }
    public DateTime Made { get; set; }
    public Dictionary<string, JsonObject> Groups { get; set; } = new();
    // The "look" group: the look as a preset and its picture, the profile's presets with theirs, the one chosen.
    public LookPreset? Look { get; set; }
    public byte[]? Picture { get; set; }
    public List<LookPreset>? Looks { get; set; }
    public Dictionary<string, byte[]>? LookPictures { get; set; }
    public string? LookId { get; set; }

    // The groups, in Settings' order, with the words the ticks show (English keys of L).
    public static readonly (string Key, string Name)[] All =
    {
        (LookGroup, "Appearance"), ("playback", "Playback"), ("eq", "Equalizer"), ("library", "Library"), ("downloads", "Download"),
        ("search", "Search and suggestions"), ("interface", "Interface"), ("together", "Listen together"), ("discord", "Discord"),
    };

    private static readonly Dictionary<string, (string[] Profile, string[] App)> Fields = new()
    {
        ["playback"] = (new[] { "Shuffle", "Repeat", "Normalize", "NormalizeTarget", "Crossfade", "CrossfadeSeconds", "BpmMatch", "BpmMatchSeconds", "Speed", "SpeedPitch",
            "ShowVideo", "ShowLyrics", "AutoQueue", "QueueHidden" }, new[] { "PlayWhileDownloading", "RadioAfterSingle", "RadioSource", "RadioAhead", "CoverTilt" }),
        ["eq"] = (new[] { "Eq" }, Array.Empty<string>()),
        ["library"] = (new[] { "LibrarySort", "StorageFilter", "Unsuggested" }, new[] { "ReleaseNotifications", "TagsFolded", "PlaylistsFolded", "ArtistsFolded", "RadiosFolded" }),
        ["downloads"] = (Array.Empty<string>(), new[] { "AudioFormat", "PreferAudioOnly", "VideoMaxRes", "MaxParallel", "SaveAudio", "AutoLyrics" }),
        ["search"] = (Array.Empty<string>(), new[] { "OnlineSearch", "SearchServices", "SearchServicesOff", "SearchParallel" }),
        ["interface"] = (Array.Empty<string>(), new[] { "Language", "Animations", "AnimationSpeed", "SmoothScroll", "ShowWhatsNew" }),
        ["together"] = (Array.Empty<string>(), new[] { "TogetherMax", "TogetherRoomPerms", "TogetherVoteShare", "TogetherAhead", "TogetherHostAhead",
            "TogetherSendAsHost", "TogetherTakeFromHost", "TogetherP2P" }),
        ["discord"] = (Array.Empty<string>(), new[] { "DiscordPresence", "DiscordAppId" }),
    };

    public IEnumerable<string> Has => All.Select(g => g.Key).Where(k => k == LookGroup ? Look != null : Groups.ContainsKey(k));

    public static SettingsFile Make(ProfileData d, AppSettings s, string profileId, string profileName, string device, string backdrop, IEnumerable<string> groups)
    {
        var file = new SettingsFile { App = Kind, Profile = profileName, Device = device, Made = DateTime.Now };
        var profileJson = JsonSerializer.SerializeToNode(d, JsonStore.Options)!.AsObject();
        var appJson = JsonSerializer.SerializeToNode(s, JsonStore.Options)!.AsObject();
        foreach (var group in groups)
        {
            if (group == LookGroup)
            {
                file.Look = LookPreset.Snap(d, s, profileName);
                if (d.Glass.Picture && File.Exists(backdrop)) file.Picture = File.ReadAllBytes(backdrop);
                file.Looks = d.Looks;
                file.LookId = d.LookId;
                file.LookPictures = d.Looks.Select(l => (l.Id, Path: LookPreset.PictureOf(profileId, l.Id))).Where(p => File.Exists(p.Path))
                    .ToDictionary(p => p.Id, p => File.ReadAllBytes(p.Path));
                continue;
            }
            if (!Fields.TryGetValue(group, out var f)) continue;
            var o = new JsonObject();
            foreach (var name in f.Profile)
                if (profileJson[name] is { } v) o["p:" + name] = v.DeepClone();
            foreach (var name in f.App)
                if (appJson[name] is { } v) o["a:" + name] = v.DeepClone();
            file.Groups[group] = o;
        }
        return file;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonStore.Options);

    // A file of settings, or null (another kind of file, or one that can't be read).
    public static SettingsFile? TryRead(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<SettingsFile>(json, JsonStore.Options);
            return file?.App == Kind ? file : null;
        }
        catch (JsonException) { return null; }
    }

    // The groups chosen, into the profile and the app's settings (the caller saves them and opens the profile again). The look comes in
    // as a preset of its own, chosen (named after the profile it came from); the other presets are added, the ones with a name already
    // there left out. again: a look just like it that's here already is the one chosen, not another preset.
    public void Apply(ProfileData d, AppSettings s, string profileId, string backdrop, IEnumerable<string> groups, bool again = false)
    {
        foreach (var group in groups)
        {
            if (group == LookGroup)
            {
                if (Look == null) continue;
                var names = d.Looks.Select(l => l.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
                foreach (var other in Looks ?? new())
                {
                    if (other.Id == LookId || !names.Add(other.Name)) continue;
                    var added = Adopt(other, LookPictures?.GetValueOrDefault(other.Id), profileId);
                    d.Looks.Add(added);
                }
                var look = again ? d.Looks.FirstOrDefault(l => SameAs(l, profileId)) : null;
                if (look == null)
                {
                    look = Adopt(Look, Picture, profileId);
                    look.Name = Free(names, string.IsNullOrWhiteSpace(Look.Name) ? Profile ?? "Look" : Look.Name);
                    d.Looks.Add(look);
                }
                look.Give(d, s, profileId, backdrop);
                d.LookId = look.Id;
                continue;
            }
            if (!Groups.TryGetValue(group, out var o)) continue;
            foreach (var (key, value) in o)
            {
                if (value == null || key.Length < 3) continue;
                object target = key[0] == 'p' ? d : s;
                var prop = target.GetType().GetProperty(key[2..], BindingFlags.Public | BindingFlags.Instance);
                if (prop is not { CanWrite: true }) continue;
                try { prop.SetValue(target, value.Deserialize(prop.PropertyType, JsonStore.Options)); }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException) { }
            }
        }
    }

    private bool SameAs(LookPreset mine, string profileId)
    {
        static string Of(LookPreset l)
        {
            var o = JsonSerializer.SerializeToNode(l, JsonStore.Options)!.AsObject();
            o.Remove(nameof(LookPreset.Id));
            o.Remove(nameof(LookPreset.Name));
            return o.ToJsonString();
        }
        var picture = LookPreset.PictureOf(profileId, mine.Id);
        return Of(mine) == Of(Look!) && (File.Exists(picture) ? Picture != null && File.ReadAllBytes(picture).AsSpan().SequenceEqual(Picture) : Picture is not { Length: > 0 });
    }

    private static LookPreset Adopt(LookPreset look, byte[]? picture, string profileId)
    {
        var mine = JsonSerializer.Deserialize<LookPreset>(JsonSerializer.Serialize(look, JsonStore.Options), JsonStore.Options)!;
        mine.Id = Ids.New();
        if (picture is { Length: > 0 })
        {
            var path = LookPreset.PictureOf(profileId, mine.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, picture);
        }
        return mine;
    }

    private static string Free(HashSet<string> names, string name)
    {
        var free = name;
        for (int n = 2; !names.Add(free); n++) free = $"{name} {n}";
        return free;
    }
}
