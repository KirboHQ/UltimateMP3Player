using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace UltimateMP3Player.Core.Link;

// iPhones plugged in by cable, through Apple's usbmuxd (on Windows the "Apple Mobile Device Service" that comes with
// iTunes or Apple Devices, on a Mac always there, on Linux the usbmuxd package): it lists them and opens a connection to
// a port of the phone through the cable, as if it were on the network. The phone must trust this computer (Apple's own
// "Trust this computer?"). Messages: a 16-byte header (length, version 1, type 8 = plist, tag) and an XML plist.
public static class Usbmux
{
    private const int WindowsPort = 27015;
    private const string UnixPath = "/var/run/usbmuxd";

    private static async Task<Socket> OpenAsync(CancellationToken ct)
    {
        Socket s;
        if (OperatingSystem.IsWindows())
        {
            s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try { await s.ConnectAsync(new IPEndPoint(IPAddress.Loopback, WindowsPort), ct); }
            catch { s.Dispose(); throw; }
        }
        else
        {
            s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try { await s.ConnectAsync(new UnixDomainSocketEndPoint(UnixPath), ct); }
            catch { s.Dispose(); throw; }
        }
        return s;
    }

    // usbmuxd's numbers of the iPhones on a cable (empty when there's no usbmuxd).
    public static async Task<List<int>> DevicesAsync(CancellationToken ct)
    {
        var list = new List<int>();
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()) return list;
        try
        {
            using var s = await OpenAsync(ct);
            await SendAsync(s, Request("ListDevices"), ct);
            var reply = await ReceiveAsync(s, ct);
            if (Get(reply, "DeviceList") is not { } devices) return list;
            foreach (var d in devices.Elements("dict"))
            {
                var props = Get(d, "Properties");
                if ((string?)Get(props, "ConnectionType") is { } kind && kind != "USB") continue;
                if (int.TryParse((string?)Get(d, "DeviceID"), out var id)) list.Add(id);
            }
        }
        catch { }
        return list;
    }

    // A connection to that port of the phone, through the cable.
    public static async Task<TcpClient> ConnectAsync(int device, int port, CancellationToken ct)
    {
        var s = await OpenAsync(ct);
        try
        {
            var req = Request("Connect");
            req.Add(new XElement("key", "DeviceID"), new XElement("integer", device));
            // (the port in network order, as usbmuxd wants it)
            req.Add(new XElement("key", "PortNumber"), new XElement("integer", (port & 0xFF) << 8 | port >> 8 & 0xFF));
            await SendAsync(s, req, ct);
            var reply = await ReceiveAsync(s, ct);
            if ((string?)Get(reply, "Number") != "0") throw new IOException("usbmuxd: " + ((string?)Get(reply, "Number") ?? "?"));
            return new TcpClient { Client = s };
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    private static XElement Request(string type)
    {
        var d = new XElement("dict");
        void Add(string key, XElement value) => d.Add(new XElement("key", key), value);
        Add("MessageType", new XElement("string", type));
        Add("ClientVersionString", new XElement("string", "UltimateMP3Player"));
        Add("ProgName", new XElement("string", "UltimateMP3Player"));
        Add("kLibUSBMuxVersion", new XElement("integer", 3));
        return d;
    }

    // The value after a key of a plist dictionary.
    private static XElement? Get(XElement? dict, string key)
        => dict?.Elements("key").FirstOrDefault(k => k.Value == key)?.ElementsAfterSelf().FirstOrDefault();

    private static async Task SendAsync(Socket s, XElement dict, CancellationToken ct)
    {
        var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                  new XElement("plist", new XAttribute("version", "1.0"), dict).ToString(SaveOptions.DisableFormatting);
        var body = Encoding.UTF8.GetBytes(xml);
        var msg = new byte[16 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(msg, msg.Length);
        BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(8), 8);
        BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(12), 1);
        body.CopyTo(msg, 16);
        await s.SendAsync(msg, SocketFlags.None, ct);
    }

    private static async Task<XElement?> ReceiveAsync(Socket s, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(3000);
        var head = new byte[16];
        await ReadExactly(s, head, timeout.Token);
        int len = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (len < 16 || len > 1 << 20) throw new IOException("usbmuxd");
        var body = new byte[len - 16];
        await ReadExactly(s, body, timeout.Token);
        using var reader = XmlReader.Create(new MemoryStream(body), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        return XDocument.Load(reader).Root?.Element("dict");
    }

    private static async Task ReadExactly(Socket s, byte[] buf, CancellationToken ct)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = await s.ReceiveAsync(buf.AsMemory(got), SocketFlags.None, ct);
            if (n <= 0) throw new IOException("usbmuxd closed");
            got += n;
        }
    }
}
