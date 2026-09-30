using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace UltimateMP3Player.Core.Together;

// Rooms are found with a broadcast on the local network (also works over Radmin VPN and similar "virtual LANs"):
// the searcher shouts a question, every host answers with its room.
public static class Discovery
{
    public const int Port = 47799;
    private static readonly byte[] Question = "UMP-LT?"u8.ToArray();

    // Windows reports an "ICMP port unreachable" as an error on the next receive: switched off.
    private static void NoResetErrors(UdpClient udp)
    {
        try { udp.Client.IOControl(unchecked((int)0x9800000C), new byte[] { 0 }, null); } catch { }
    }

    // ------------------------------------------------------------------ host side

    public sealed class Responder : IDisposable
    {
        private readonly UdpClient _udp;
        private readonly Func<RoomAd?> _ad;
        private readonly CancellationTokenSource _cts = new();

        public Responder(Func<RoomAd?> ad)
        {
            _ad = ad;
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            NoResetErrors(_udp);
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
                if (!r.Buffer.AsSpan().SequenceEqual(Question) || _ad() is not { } ad) continue;
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

    // ------------------------------------------------------------------ searcher

    public static async Task<List<RoomAd>> ScanAsync(int waitMs, CancellationToken ct = default)
    {
        var found = new Dictionary<string, RoomAd>();
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        NoResetErrors(udp);
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
                var ad = JsonSerializer.Deserialize<RoomAd>(r.Buffer, Msg.Json);
                if (ad == null || ad.RoomId.Length == 0 || ad.Proto <= 0) continue;
                ad.Ip = r.RemoteEndPoint.Address.ToString();
                // The same room answering on several networks: the first answer wins, except loopback over a real address.
                if (!found.TryGetValue(ad.RoomId, out var had) || had.Ip == "127.0.0.1") found[ad.RoomId] = ad;
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                break;
            }
            catch { }
        }
        return found.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // Everyone on every network this PC is on (Wi-Fi, cable, VPNs), plus this PC itself.
    private static List<IPAddress> Targets()
    {
        var list = new List<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
        foreach (var (ip, mask) in Networks())
        {
            var a = ip.GetAddressBytes();
            var m = mask.GetAddressBytes();
            var b = new byte[4];
            for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
            var bc = new IPAddress(b);
            if (!list.Contains(bc)) list.Add(bc);
        }
        return list;
    }

    private static IEnumerable<(IPAddress Ip, IPAddress Mask)> Networks()
    {
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { yield break; }
        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); }
            catch { continue; }
            foreach (var u in props.UnicastAddresses)
                if (u.Address.AddressFamily == AddressFamily.InterNetwork && u.IPv4Mask != null && !u.Address.Equals(IPAddress.Any))
                    yield return (u.Address, u.IPv4Mask);
        }
    }

    // This PC's addresses others can use (to join by address): networks with a gateway (the home network) first.
    public static List<string> LocalAddresses()
    {
        var list = new List<(string Ip, int Rank)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                bool gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var u in props.UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var s = u.Address.ToString();
                    if (s.StartsWith("169.254.")) continue;
                    list.Add((s, gateway ? 0 : 1));
                }
            }
        }
        catch { }
        return list.OrderBy(x => x.Rank).Select(x => x.Ip).Distinct().ToList();
    }
}
