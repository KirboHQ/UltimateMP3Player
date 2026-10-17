using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using UltimateMP3Player.Core.Together;

namespace UltimateMP3Player.Core.Link;

// The phones in "Connect to computer" are found by the computer: it asks every address of its networks one by one (an
// iPhone hears no broadcast without a permission Apple gives to few apps) plus a broadcast, and the phones answer.
// The answers are replies to the computer's own question, so no firewall stands in the way. iPhones plugged in by cable
// are found through Apple's usbmuxd (Usbmux).
public static class LinkDiscovery
{
    public const int Port = 47797;
    private static readonly byte[] Question = "UMP-LINK?"u8.ToArray();
    // Addresses asked per network at most (a big office network: the ones next to this computer's).
    private const int MaxHosts = 1024;

    // ------------------------------------------------------------------ phone side

    public sealed class Responder : IDisposable
    {
        private readonly UdpClient _udp;
        private readonly Func<IPAddress, PhoneAd?> _ad;
        private readonly CancellationTokenSource _cts = new();

        // ad: what to answer to whoever asks from that address (null: nothing).
        public Responder(Func<IPAddress, PhoneAd?> ad)
        {
            _ad = ad;
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            Discovery.NoResetErrors(_udp);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Port));
            _ = Loop();
        }

        private async Task Loop()
        {
            while (!_cts.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await _udp.ReceiveAsync(_cts.Token); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { continue; }
                if (!r.Buffer.AsSpan().SequenceEqual(Question) || _ad(r.RemoteEndPoint.Address) is not { } ad) continue;
                try { await _udp.SendAsync(JsonSerializer.SerializeToUtf8Bytes(ad, Msg.Json), r.RemoteEndPoint, _cts.Token); }
                catch { }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _udp.Dispose(); } catch { }
        }
    }

    // The other side is at the end of a cable: an iPhone's usbmuxd (it comes from the phone itself) or a computer on the
    // phone's USB tethering. Nobody else can be there, so no need to ask the user.
    public static bool ViaCable(IPAddress remote)
    {
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (IPAddress.IsLoopback(remote)) return true;
        if (remote.AddressFamily != AddressFamily.InterNetwork) return false;
        try
        {
            var r = remote.GetAddressBytes();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var name = nic.Name.ToLowerInvariant();
                if (!name.StartsWith("rndis") && !name.StartsWith("usb") && !name.StartsWith("ncm")) continue;
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask == null) continue;
                    var a = u.Address.GetAddressBytes();
                    var m = u.IPv4Mask.GetAddressBytes();
                    bool same = true;
                    for (int i = 0; i < 4; i++) same &= (a[i] & m[i]) == (r[i] & m[i]);
                    if (same) return true;
                }
            }
        }
        catch { }
        return false;
    }

    // ------------------------------------------------------------------ computer side

    public static async Task<List<PhoneAd>> ScanAsync(int waitMs, CancellationToken ct = default)
    {
        var found = new Dictionary<string, PhoneAd>();
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        Discovery.NoResetErrors(udp);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        foreach (var target in Targets())
        {
            try { await udp.SendAsync(Question, new IPEndPoint(target, Port), ct); }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        long until = Environment.TickCount64 + waitMs;
        while (true)
        {
            long left = until - Environment.TickCount64;
            if (left <= 0) break;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter((int)left);
            try
            {
                var r = await udp.ReceiveAsync(cts.Token);
                var ad = JsonSerializer.Deserialize<PhoneAd>(r.Buffer, Msg.Json);
                if (ad == null || ad.Id.Length == 0 || ad.Proto <= 0 || ad.Port <= 0) continue;
                ad.Ip = r.RemoteEndPoint.Address.ToString();
                // The same phone on Wi-Fi and on the cable: the cable wins.
                if (!found.TryGetValue(ad.Id, out var had) || ad.Cable && !had.Cable) found[ad.Id] = ad;
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                break;
            }
            catch { }
        }
        return found.Values.ToList();
    }

    // Every address of every network this device is on (and their broadcasts): iPhones can't hear broadcasts.
    internal static List<IPAddress> Targets()
    {
        var list = new List<IPAddress> { IPAddress.Broadcast };
        foreach (var (ip, mask) in Discovery.Networks())
        {
            uint a = ToUint(ip), m = ToUint(mask);
            uint net = a & m, count = ~m;
            if (count is 0 or uint.MaxValue) continue;
            list.Add(ToIp(net | count));
            // Too many: the 256 around this computer's address.
            if (count > MaxHosts)
            {
                net = a & 0xFFFFFF00;
                count = 255;
            }
            for (uint h = 1; h < count; h++)
                if ((net | h) != a) list.Add(ToIp(net | h));
        }
        return list.Distinct().ToList();
    }

    private static uint ToUint(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3];
    }

    private static IPAddress ToIp(uint v) => new(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
}
