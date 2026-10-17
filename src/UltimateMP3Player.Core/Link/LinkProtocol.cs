using UltimateMP3Player.Core.Together;

namespace UltimateMP3Player.Core.Link;

// "Connect phone": the phone becomes the remote of the computer's player (its own music plays there, its sound stays
// off) and the computer can send it music. The messages are Listen together's (Msg, Connection), with the fields below.

public enum LinkStatus { None, Connecting, Connected, Reconnecting, Closed }

// A song as the other device needs it: to show it, and (the computer) to find it, get its file from the phone or
// download it from its link.
public sealed class LinkTrack
{
    // Its id on the device that sends it.
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public double Duration { get; set; }
    public string? SourceUrl { get; set; }
    public string? Site { get; set; }
    public List<string> Keys { get; set; } = new();
    public string? ArtUrl { get; set; }
    public byte[]? Wave { get; set; }
    public double? Loudness { get; set; }
    public double? Peak { get; set; }
    public double? Bpm { get; set; }
    // The phone's song has its audio saved there (sent from there); otherwise the computer downloads it from its link.
    public bool File { get; set; }
    public bool Cover { get; set; }
    public string? Quality { get; set; }
    // From the computer: the phone's id of a song that came from the phone.
    public string? Phone { get; set; }

    public static LinkTrack From(Track t) => new()
    {
        Id = t.Id, Title = t.Title, Artist = t.Artist, Album = t.Album, Duration = t.Duration, SourceUrl = t.SourceUrl, Site = t.Site,
        // ("file:" keys are paths of this device: they mean nothing on the other one)
        Keys = t.Keys.Where(k => !k.StartsWith("file:", StringComparison.OrdinalIgnoreCase)).ToList(),
        ArtUrl = t.ArtUrl, Loudness = t.Loudness, Peak = t.Peak, Bpm = t.Bpm, Cover = t.HasCover, Quality = t.AudioQuality,
        File = t.HasSavedFile,
    };

    // The key the computer finds a song of the phone by (in its cache, and in its library once saved there).
    public static string PhoneKey(string phoneId) => "phone:" + phoneId;
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
    public double Position { get; set; }
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
        o.Current?.Cover == Current?.Cover && o.Current?.Phone == Current?.Phone;
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
