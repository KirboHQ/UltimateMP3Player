using System.Text;
using Tmds.DBus.Protocol;

namespace UltimateMP3Player.Services;

// The MPRIS player on the session bus (https://specifications.freedesktop.org/mpris-spec/latest/): GNOME, KDE, Cinnamon,
// Xfce and the media keys talk to it. Title, artist, album, cover, position, volume and the buttons, like the Windows flyout.
// Tmds.DBus.Protocol 0.21 (the one Avalonia uses).
internal sealed class Mpris : IMethodHandler, IDisposable
{
    private const string ObjectPathName = "/org/mpris/MediaPlayer2";
    private const string BusName = "org.mpris.MediaPlayer2.UltimateMP3Player";
    private const string RootInterface = "org.mpris.MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string NoTrack = "/org/mpris/MediaPlayer2/TrackList/NoTrack";

    private readonly MediaControls _owner;
    private Connection? _bus;
    private bool _disposed;

    public Mpris(MediaControls owner)
    {
        _owner = owner;
        _ = StartAsync();
    }

    public string Path => ObjectPathName;

    // Calls are short and touch only the app's own state through MediaControls.
    public bool RunMethodHandlerSynchronously(Message message) => true;

    private async Task StartAsync()
    {
        try
        {
            if (Address.Session is not { Length: > 0 } address) return;
            var bus = new Connection(address);
            await bus.ConnectAsync();
            bus.AddMethodHandler(this);
            // One copy runs at a time; another program with the same name gets a distinct one.
            if (!await RequestNameAsync(bus, BusName))
                await RequestNameAsync(bus, BusName + ".instance" + Environment.ProcessId);
            if (_disposed) bus.Dispose();
            else _bus = bus;
        }
        catch { }
    }

    // org.freedesktop.DBus.RequestName, without queueing: true when the name is ours.
    private static async Task<bool> RequestNameAsync(Connection bus, string name)
    {
        try
        {
            var reply = await bus.CallMethodAsync(RequestNameMessage(bus, name), (m, _) => m.GetBodyReader().ReadUInt32(), null);
            return reply is 1 or 4; // primary owner, or already the owner
        }
        catch { return false; }
    }

    // (The writer is a ref struct: not inside the async method.)
    private static MessageBuffer RequestNameMessage(Connection bus, string name)
    {
        using var w = bus.GetMessageWriter();
        w.WriteMethodCallHeader("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "RequestName", "su", MessageFlags.None);
        w.WriteString(name);
        w.WriteUInt32(4); // DBUS_NAME_FLAG_DO_NOT_QUEUE
        return w.CreateMessage();
    }

    // ------------------------------------------------------------------ the app tells what changed

    public void OnTrack() => Changed(PlayerInterface, "Metadata", "CanPlay", "CanPause", "CanSeek", "CanGoNext", "CanGoPrevious", "PlaybackStatus");
    public void OnState() => Changed(PlayerInterface, "PlaybackStatus");
    public void OnRate() => Changed(PlayerInterface, "Rate");
    public void OnVolume() => Changed(PlayerInterface, "Volume");

    // The position isn't announced while it runs (players compute it from Rate), only when it jumps.
    public void OnSeeked(TimeSpan position)
    {
        if (_bus is not { } bus) return;
        try
        {
            using var w = bus.GetMessageWriter();
            w.WriteSignalHeader(null, ObjectPathName, PlayerInterface, "Seeked", "x");
            w.WriteInt64(Micro(position));
            bus.TrySendMessage(w.CreateMessage());
        }
        catch { }
    }

    private void Changed(string iface, params string[] names)
    {
        if (_bus is not { } bus) return;
        try
        {
            using var w = bus.GetMessageWriter();
            w.WriteSignalHeader(null, ObjectPathName, PropertiesInterface, "PropertiesChanged", "sa{sv}as");
            w.WriteString(iface);
            var dict = w.WriteDictionaryStart();
            foreach (var n in names)
            {
                if (Get(iface, n) is not { } v) continue;
                w.WriteDictionaryEntryStart();
                w.WriteString(n);
                w.WriteVariant(v);
            }
            w.WriteDictionaryEnd(dict);
            w.WriteArray(Array.Empty<string>());
            bus.TrySendMessage(w.CreateMessage());
        }
        catch { }
    }

    // ------------------------------------------------------------------ properties

    private static readonly string[] RootNames = { "CanQuit", "CanRaise", "HasTrackList", "Identity", "DesktopEntry", "SupportedUriSchemes", "SupportedMimeTypes" };
    private static readonly string[] PlayerNames =
    {
        "PlaybackStatus", "Rate", "Metadata", "Volume", "Position", "MinimumRate", "MaximumRate",
        "CanGoNext", "CanGoPrevious", "CanPlay", "CanPause", "CanSeek", "CanControl",
    };

    private VariantValue? Get(string iface, string name)
    {
        var t = _owner.Current;
        if (iface == RootInterface)
            return name switch
            {
                "CanQuit" or "CanRaise" => VariantValue.Bool(true),
                "HasTrackList" => VariantValue.Bool(false),
                "Identity" => VariantValue.String("Ultimate MP3 Player"),
                "DesktopEntry" => VariantValue.String("ultimate-mp3-player"),
                "SupportedUriSchemes" or "SupportedMimeTypes" => VariantValue.Array(Array.Empty<string>()),
                _ => (VariantValue?)null,
            };
        if (iface == PlayerInterface)
            return name switch
            {
                "PlaybackStatus" => VariantValue.String(t == null ? "Stopped" : _owner.Playing ? "Playing" : "Paused"),
                "Rate" => VariantValue.Double(_owner.Speed),
                "Metadata" => Metadata(t),
                "Volume" => VariantValue.Double(_owner.Volume),
                "Position" => VariantValue.Int64(Micro(_owner.Position)),
                "MinimumRate" => VariantValue.Double(0.5),
                "MaximumRate" => VariantValue.Double(2),
                "CanGoNext" or "CanGoPrevious" or "CanPlay" or "CanPause" => VariantValue.Bool(t != null),
                "CanSeek" => VariantValue.Bool(t != null && t.Duration > TimeSpan.Zero),
                "CanControl" => VariantValue.Bool(true),
                _ => (VariantValue?)null,
            };
        return null;
    }

    private static VariantValue Metadata(MediaControls.Track? t)
    {
        var d = new Dict<string, VariantValue>();
        if (t == null)
        {
            d.Add("mpris:trackid", VariantValue.ObjectPath(new ObjectPath(NoTrack)));
            return d.AsVariantValue();
        }
        d.Add("mpris:trackid", VariantValue.ObjectPath(new ObjectPath(TrackPath(t))));
        if (t.Duration > TimeSpan.Zero) d.Add("mpris:length", VariantValue.Int64(Micro(t.Duration)));
        d.Add("xesam:title", VariantValue.String(t.Title));
        if (t.Artist.Length > 0) d.Add("xesam:artist", VariantValue.Array(new[] { t.Artist }));
        if (t.Album.Length > 0) d.Add("xesam:album", VariantValue.String(t.Album));
        if (t.Cover != null) d.Add("mpris:artUrl", VariantValue.String(new Uri(t.Cover).AbsoluteUri));
        return d.AsVariantValue();
    }

    private static string TrackPath(MediaControls.Track t) => "/com/kirbohq/UltimateMP3Player/Track/" + t.Number;

    private static long Micro(TimeSpan t) => t.Ticks / 10;

    // ------------------------------------------------------------------ calls

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        try
        {
            Handle(context);
        }
        catch (Exception ex)
        {
            if (!context.ReplySent && !context.NoReplyExpected) context.ReplyError("org.freedesktop.DBus.Error.Failed", ex.Message);
        }
        return default;
    }

    private void Handle(MethodContext context)
    {
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([Introspection]);
            return;
        }
        var m = context.Request;
        var iface = m.InterfaceAsString;
        var member = m.MemberAsString;
        if (iface == PropertiesInterface)
        {
            HandleProperties(context, member);
            return;
        }
        if (iface == RootInterface)
        {
            switch (member)
            {
                case "Raise": _owner.Raise(); break;
                case "Quit": _owner.Quit(); break;
                default: ReplyUnknown(context); return;
            }
            ReplyEmpty(context);
            return;
        }
        if (iface != PlayerInterface)
        {
            ReplyUnknown(context);
            return;
        }
        switch (member)
        {
            case "Play": _owner.Play(); break;
            case "Pause":
            case "Stop": _owner.Pause(); break;
            case "PlayPause": _owner.PlayPause(); break;
            case "Next": _owner.Next(); break;
            case "Previous": _owner.Previous(); break;
            case "Seek":
            {
                var r = m.GetBodyReader();
                _owner.Seek(_owner.Position + TimeSpan.FromTicks(r.ReadInt64() * 10));
                break;
            }
            case "SetPosition":
            {
                var r = m.GetBodyReader();
                var track = r.ReadObjectPathAsString();
                var to = TimeSpan.FromTicks(r.ReadInt64() * 10);
                // Only for the song that is playing now (a late request for the previous one is stale).
                if (_owner.Current is { } t && track == TrackPath(t)) _owner.Seek(to);
                break;
            }
            case "OpenUri": break;
            default: ReplyUnknown(context); return;
        }
        ReplyEmpty(context);
    }

    private void HandleProperties(MethodContext context, string? member)
    {
        var r = context.Request.GetBodyReader();
        switch (member)
        {
            case "Get":
            {
                var iface = r.ReadString();
                var name = r.ReadString();
                if (Get(iface, name) is not { } v)
                {
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", $"No property {name} in {iface}");
                    return;
                }
                using var w = context.CreateReplyWriter("v");
                w.WriteVariant(v);
                context.Reply(w.CreateMessage());
                return;
            }
            case "GetAll":
            {
                var iface = r.ReadString();
                var names = iface == RootInterface ? RootNames : iface == PlayerInterface ? PlayerNames : Array.Empty<string>();
                using var w = context.CreateReplyWriter("a{sv}");
                var dict = w.WriteDictionaryStart();
                foreach (var n in names)
                {
                    if (Get(iface, n) is not { } v) continue;
                    w.WriteDictionaryEntryStart();
                    w.WriteString(n);
                    w.WriteVariant(v);
                }
                w.WriteDictionaryEnd(dict);
                context.Reply(w.CreateMessage());
                return;
            }
            case "Set":
            {
                var iface = r.ReadString();
                var name = r.ReadString();
                var value = r.ReadVariantValue();
                if (iface == PlayerInterface && name == "Volume") _owner.RequestVolume(value.GetDouble());
                else if (iface == PlayerInterface && name == "Rate") { } // the speed is set in the app
                else
                {
                    context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly", $"{name} is read-only");
                    return;
                }
                ReplyEmpty(context);
                return;
            }
            default:
                ReplyUnknown(context);
                return;
        }
    }

    private static void ReplyUnknown(MethodContext context)
        => context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod", $"Unknown method {context.Request.MemberAsString} in {context.Request.InterfaceAsString}");

    private static void ReplyEmpty(MethodContext context)
    {
        if (context.NoReplyExpected) return;
        using var w = context.CreateReplyWriter(null!);
        context.Reply(w.CreateMessage());
    }

    private static readonly ReadOnlyMemory<byte> Introspection = Encoding.UTF8.GetBytes("""
        <interface name="org.mpris.MediaPlayer2">
          <method name="Raise"/>
          <method name="Quit"/>
          <property name="CanQuit" type="b" access="read"/>
          <property name="CanRaise" type="b" access="read"/>
          <property name="HasTrackList" type="b" access="read"/>
          <property name="Identity" type="s" access="read"/>
          <property name="DesktopEntry" type="s" access="read"/>
          <property name="SupportedUriSchemes" type="as" access="read"/>
          <property name="SupportedMimeTypes" type="as" access="read"/>
        </interface>
        <interface name="org.mpris.MediaPlayer2.Player">
          <method name="Next"/>
          <method name="Previous"/>
          <method name="Pause"/>
          <method name="PlayPause"/>
          <method name="Stop"/>
          <method name="Play"/>
          <method name="Seek"><arg direction="in" name="Offset" type="x"/></method>
          <method name="SetPosition"><arg direction="in" name="TrackId" type="o"/><arg direction="in" name="Position" type="x"/></method>
          <method name="OpenUri"><arg direction="in" name="Uri" type="s"/></method>
          <signal name="Seeked"><arg name="Position" type="x"/></signal>
          <property name="PlaybackStatus" type="s" access="read"/>
          <property name="Rate" type="d" access="readwrite"/>
          <property name="Metadata" type="a{sv}" access="read"/>
          <property name="Volume" type="d" access="readwrite"/>
          <property name="Position" type="x" access="read"/>
          <property name="MinimumRate" type="d" access="read"/>
          <property name="MaximumRate" type="d" access="read"/>
          <property name="CanGoNext" type="b" access="read"/>
          <property name="CanGoPrevious" type="b" access="read"/>
          <property name="CanPlay" type="b" access="read"/>
          <property name="CanPause" type="b" access="read"/>
          <property name="CanSeek" type="b" access="read"/>
          <property name="CanControl" type="b" access="read"/>
        </interface>
        <interface name="org.freedesktop.DBus.Properties">
          <method name="Get"><arg direction="in" type="s"/><arg direction="in" type="s"/><arg direction="out" type="v"/></method>
          <method name="GetAll"><arg direction="in" type="s"/><arg direction="out" type="a{sv}"/></method>
          <method name="Set"><arg direction="in" type="s"/><arg direction="in" type="s"/><arg direction="in" type="v"/></method>
          <signal name="PropertiesChanged"><arg type="s"/><arg type="a{sv}"/><arg type="as"/></signal>
        </interface>
        """);

    public void Dispose()
    {
        _disposed = true;
        try { _bus?.Dispose(); } catch { }
        _bus = null;
    }
}
