using System.Text.Json;
using System.Text.Json.Serialization;

namespace UltimateMP3Player.Core.Together;

// "Listen together": what the host of a room lets each person do. The host can always do everything.
[Flags]
public enum Perm
{
    None = 0,
    // Put songs in the room's queue.
    Add = 1,
    // Take songs out of the queue and move them around.
    Remove = 2,
    // Skip the song playing.
    Skip = 4,
    // Pause and resume, move forward and back in the song, repeat it (the same thing: where the song is).
    Pause = 8,
    // Play faster or slower, for everyone.
    Speed = 16,
    All = Add | Remove | Skip | Pause | Speed,
}

public static class Perms
{
    public static readonly Perm[] Each = { Perm.Add, Perm.Remove, Perm.Skip, Perm.Pause, Perm.Speed };

    // How "important" someone is when the host has to be handed over: the one allowed to do more.
    public static int Weight(Perm p)
    {
        int n = 0;
        foreach (var f in Each) if ((p & f) != 0) n++;
        return n;
    }
}

public sealed class Member
{
    // Stable per installation (settings), so a kicked person stays out and a dropped one gets their place back.
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#7C5CFF";
    // Small JPEG of the profile picture.
    public byte[]? Avatar { get; set; }
    public Perm Perms { get; set; }
    // Order of arrival: the first one who came gets the room when the host leaves (among the most important).
    public int Seq { get; set; }
    // Where this person can be reached if they become the host.
    public string? Ip { get; set; }
    public int Port { get; set; }
    public string? Version { get; set; }
    // Lost the connection a moment ago: the place is kept for a while.
    public bool Away { get; set; }

    public Member Clone() => (Member)MemberwiseClone();
}

// A song in the room: what everyone needs to find it or download it.
public sealed class RoomTrack
{
    public string Id { get; set; } = "";
    public string AddedBy { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public double Duration { get; set; }
    // Web page it comes from (downloadable), null for files from someone's computer.
    public string? SourceUrl { get; set; }
    public string? Site { get; set; }
    public List<string> Keys { get; set; } = new();
    public string? ArtUrl { get; set; }
    public byte[]? Wave { get; set; }
    public double? Loudness { get; set; }
    public double? Peak { get; set; }
    public double? Bpm { get; set; }
    // Extension of the file of whoever added it (".mp3"...).
    public string? Ext { get; set; }
}

public enum PlayState { Idle, Waiting, Playing, Paused }

// Where the song is: Position seconds at host time At (ms), moving Speed seconds of song per second.
public sealed class Playback
{
    public string? ItemId { get; set; }
    public PlayState State { get; set; }
    public double Position { get; set; }
    public long At { get; set; }
    // Waiting: since when (host ms).
    public long WaitSince { get; set; }
    // The song playing starts over when it ends (only that one: the room has no "repeat the queue").
    public bool Loop { get; set; }
    // Playback speed for everyone, 0.5-2; Pitch = the key follows it (like a record), otherwise it stays.
    public double Speed { get; set; } = 1;
    public bool Pitch { get; set; }

    public Playback Clone() => (Playback)MemberwiseClone();

    // A new state of the room that keeps its loop and speed.
    public Playback Next(string? item, PlayState state, double position, long at) => new()
    {
        ItemId = item, State = state, Position = position, At = at, WaitSince = state == PlayState.Waiting ? at : 0,
        Loop = Loop, Speed = Speed, Pitch = Pitch,
    };
}

public sealed class ChatLine
{
    public string? From { get; set; }
    public string Name { get; set; } = "";
    public string? Color { get; set; }
    public string Text { get; set; } = "";
    // Unix ms.
    public long Time { get; set; }
    // Someone came, left, was kicked...: shown differently. Text is then the Italian template (translated on
    // each computer into its own language) and Args what goes in it.
    public bool System { get; set; }
    public List<string>? Args { get; set; }

    public string Display => System ? L.F(Text, (Args ?? new List<string>()).Cast<object?>().ToArray()) : Text;
}

public enum FileState { None, Downloading, Transfer, Ready, Failed }

public sealed class FileStatus
{
    public FileState State { get; set; }
    public double Pct { get; set; }
    // Real length of the file, when known (fills songs whose length the adder didn't have).
    public double Duration { get; set; }

    public bool SameAs(FileStatus? o) => o != null && o.State == State && Math.Abs(o.Pct - Pct) < 0.5;
}

// What a room says about itself on the network.
public sealed class RoomAd
{
    public string RoomId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public string? HostColor { get; set; }
    public int Members { get; set; }
    public int Max { get; set; }
    public int Port { get; set; }
    public bool Locked { get; set; }
    public int Proto { get; set; }
    public string? Playing { get; set; }
    // Filled by the scanner: where the answer came from.
    public string? Ip { get; set; }
}

// Every message, client ↔ host. Only the fields a type needs are set.
public sealed class Msg
{
    public string T { get; set; } = "";
    public string? Id { get; set; }
    public string? Text { get; set; }
    public Member? Me { get; set; }
    public string? Password { get; set; }
    public int Proto { get; set; }
    public bool Rejoin { get; set; }
    public string? Lost { get; set; }
    public string? RoomId { get; set; }
    public string? RoomName { get; set; }
    public int Max { get; set; }
    public bool Locked { get; set; }
    public string? HostId { get; set; }
    public string? You { get; set; }
    public List<Member>? Members { get; set; }
    public List<RoomTrack>? Queue { get; set; }
    public RoomTrack? Track { get; set; }
    public RoomTrack? Current { get; set; }
    public Playback? Play { get; set; }
    public List<ChatLine>? Chat { get; set; }
    public ChatLine? Line { get; set; }
    public List<string>? Ids { get; set; }
    public Perm Perms { get; set; }
    public Perm DefaultPerms { get; set; }
    public int Index { get; set; }
    public double Pos { get; set; }
    public bool Flag { get; set; }
    public long T0 { get; set; }
    public long T1 { get; set; }
    public byte[]? Data { get; set; }
    public long Size { get; set; }
    public Dictionary<string, FileStatus>? Files { get; set; }
    public Dictionary<string, Dictionary<string, FileStatus>>? AllFiles { get; set; }
    public List<string>? Banned { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    public static Msg? From(ReadOnlySpan<byte> utf8)
    {
        try { return JsonSerializer.Deserialize<Msg>(utf8, Json); }
        catch { return null; }
    }
}
