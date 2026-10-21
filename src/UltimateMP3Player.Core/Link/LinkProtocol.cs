using UltimateMP3Player.Core.Together;

namespace UltimateMP3Player.Core.Link;

// "Connect phone": the phone becomes the remote of the computer's player (the music plays there, on the phone or on both)
// and each device shows the other's music too (SharedLibrary). The messages are Listen together's (Msg, Connection), with
// the fields below.

public enum LinkStatus { None, Connecting, Connected, Reconnecting, Closed }

// Whose music the two devices show while connected (the same on both): both, only the phone's, only the computer's.
public enum LinkMusic { Both, Phone, Computer }

// Where the music is heard: the computer (the phone stays silent), the phone (the computer stays silent), both together.
public enum LinkAudio { Computer, Phone, Both }

// A song as the other device needs it: to show it, play it (its file from there, or its link), edit it there.
public sealed class LinkTrack
{
    // Its id on the device that sends it.
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? Year { get; set; }
    public double Duration { get; set; }
    public string? SourceUrl { get; set; }
    public string? Site { get; set; }
    public List<string> Keys { get; set; } = new();
    public string? ArtUrl { get; set; }
    public byte[]? Wave { get; set; }
    public double? Loudness { get; set; }
    public double? Peak { get; set; }
    public double? Bpm { get; set; }
    // Its audio is there, on the device that sends it (sent from there); otherwise it comes from its link.
    public bool File { get; set; }
    // Saved on that device (false: in its cloud). File can still be false: the saved file has gone missing.
    public bool Saved { get; set; }
    public bool Unavailable { get; set; }
    public bool Cover { get; set; }
    public int CoverVersion { get; set; }
    public string? Quality { get; set; }
    public DateTime Added { get; set; }
    // The receiver's own id of the song, when the sender knows it (a song that came from there, or both have it).
    public string? Yours { get; set; }

    public static LinkTrack From(Track t) => new()
    {
        Id = t.Id, Title = t.Title, Artist = t.Artist, Album = t.Album, Year = t.Year, Duration = t.Duration, SourceUrl = t.SourceUrl, Site = t.Site,
        // ("file:" keys are paths of this device: they mean nothing on the other one)
        Keys = t.Keys.Where(k => !k.StartsWith("file:", StringComparison.OrdinalIgnoreCase)).ToList(),
        ArtUrl = t.ArtUrl, Loudness = t.Loudness, Peak = t.Peak, Bpm = t.Bpm, Cover = t.HasCover, CoverVersion = t.CoverVersion, Quality = t.AudioQuality,
        File = t.HasSavedFile, Saved = t.IsSaved, Unavailable = t.Unavailable, Added = t.Added,
    };

    // The key a song that came from the other device keeps (in the cache, and in the library once saved here): the phone's
    // id on the computer, the computer's on the phone.
    public static string PhoneKey(string phoneId) => "phone:" + phoneId;
    public static string ComputerKey(string pcId) => "computer:" + pcId;
}

// The profile of the other device, for the badge at the bottom left (Connect phone with one device's music shown).
public sealed class LinkProfile
{
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#7C5CFF";
    // Small JPEG of its picture.
    public byte[]? Avatar { get; set; }
}

// A device's music for the other one (SharedLibrary): all of it when connecting (Full, through the file lane), then what
// changes; the same shape for an edit of the receiver's own music made on the sender ("edit").
public sealed class LinkLib
{
    // Everything: what the receiver had of it is replaced.
    public bool Full { get; set; }
    public LinkProfile? Profile { get; set; }
    // Songs of the library, new or changed (all of them in Full), and the ones gone.
    public List<LinkTrack>? Tracks { get; set; }
    public List<string>? Gone { get; set; }
    // Playlists, new or changed, and the ones deleted.
    public List<Playlist>? Lists { get; set; }
    public List<string>? ListsGone { get; set; }
    // The whole list of tags, when it changed.
    public List<Tag>? Tags { get; set; }
    // The tags of these songs (an empty list: none any more).
    public Dictionary<string, List<string>>? TrackTags { get; set; }
    // The last songs heard.
    public List<HistoryEntry>? History { get; set; }
    // An edit: songs of the sender the receiver must keep first (by the sender's id); the edit refers to them as
    // AdoptPrefix + that id.
    public List<LinkTrack>? Adopt { get; set; }

    public const string AdoptPrefix = "+";

    public bool IsEmpty => Profile == null && Tracks is not { Count: > 0 } && Gone is not { Count: > 0 } && Lists is not { Count: > 0 } &&
                           ListsGone is not { Count: > 0 } && Tags == null && TrackTags is not { Count: > 0 } && History == null;
}

// A song of the computer's "next up".
public sealed class LinkRow
{
    // Its place in "next up" (what the phone's buttons on it refer to).
    public int Index { get; set; }
    public LinkTrack T { get; set; } = new();
    public bool Queued { get; set; }
    // Still on its way to the computer (from the phone or its link).
    public FileState State { get; set; }
    public double Pct { get; set; }
}

// The computer's player, as the phone shows it.
public sealed class LinkState
{
    public LinkTrack? Current { get; set; }
    public bool Playing { get; set; }
    // Where the song is heard at the computer's clock At (ms, LinkEnd.Clock): the phone playing too follows it.
    public double Position { get; set; }
    public long At { get; set; }
    public double Duration { get; set; }
    // The song is on its way (WaitPct: how far), or more similar songs are being looked for (WaitMore).
    public bool Waiting { get; set; }
    public bool WaitMore { get; set; }
    public double WaitPct { get; set; }
    public double Speed { get; set; } = 1;
    public bool Pitch { get; set; }
    public bool Shuffle { get; set; }
    public RepeatMode Repeat { get; set; }
    public bool AutoQueue { get; set; }
    public double Volume { get; set; }
    public bool Muted { get; set; }
    public string? Context { get; set; }
    public bool Radio { get; set; }
    public int Upcoming { get; set; }

    // The same, apart from where the song is (that moves by itself).
    public bool SameAs(LinkState o) => o.Current?.Id == Current?.Id && o.Playing == Playing && Math.Abs(o.Duration - Duration) < 0.5 &&
        o.Waiting == Waiting && o.WaitMore == WaitMore && Math.Abs(o.WaitPct - WaitPct) < 2 && o.Speed == Speed && o.Pitch == Pitch &&
        o.Shuffle == Shuffle && o.Repeat == Repeat && o.AutoQueue == AutoQueue && Math.Abs(o.Volume - Volume) < 0.005 && o.Muted == Muted &&
        o.Context == Context && o.Radio == Radio && o.Upcoming == Upcoming && o.Current?.Wave?.Length == Current?.Wave?.Length &&
        o.Current?.Cover == Current?.Cover && o.Current?.Yours == Current?.Yours;
}

// What a phone in "Connect to computer" says about itself.
public sealed class PhoneAd
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Port { get; set; }
    public int Proto { get; set; }
    // The question came through a cable (USB tethering): no need to confirm.
    public bool Cable { get; set; }
    // Already connected to this computer (by name).
    public string? Linked { get; set; }
    // Filled by the computer: where it answered from (an address, or an iPhone plugged in: its usbmuxd number).
    public string? Ip { get; set; }
    public int Usb { get; set; }
}

// The music the computer sends to the phone ("To the phone" in the export window), as its "pack.json" entry: what a .ump
// would hold, without the .ump (the phone asks for the entries it needs, one at a time).
public sealed class LinkPack
{
    public const string Entry = "pack.json";

    public PackManifest Manifest { get; set; } = new();
    public List<string> Entries { get; set; } = new();
    public long Size { get; set; }
}
