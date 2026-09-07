using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AirServerLite.Core;

public sealed record NetworkChoice(IPAddress Address, PhysicalAddress Mac, string InterfaceName);

public static class NetUtil
{
    private static readonly string[] VirtualHints =
    {
        "hyper-v", "vethernet", "virtualbox", "vmware", "loopback", "wsl",
        "tap-", "tailscale", "zerotier", "bluetooth", "docker", "npcap", "openvpn"
    };

    /// <summary>
    /// Pick the interface the iPhone will actually reach us on. Loopback, tunnels and
    /// Hyper-V/WSL virtual switches are pushed to the bottom: advertising an mDNS A record
    /// that points at a 172.x Hyper-V address is the single most common reason the receiver
    /// appears in Control Center but the RTSP connect then times out.
    /// </summary>
    public static NetworkChoice? PickInterface(string preferredName = "")
    {
        var candidates = new List<(NetworkInterface Nic, IPAddress Addr, int Score)>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); }
            catch { continue; }

            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;

                var bytes = ua.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) continue; // APIPA - no real link

                var score = 0;
                var desc = (nic.Description + " " + nic.Name).ToLowerInvariant();

                if (!string.IsNullOrWhiteSpace(preferredName) &&
                    (nic.Name.Equals(preferredName, StringComparison.OrdinalIgnoreCase) ||
                     nic.Description.Contains(preferredName, StringComparison.OrdinalIgnoreCase)))
                    score += 1000;

                if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) score += 50;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet) score += 40;

                if (props.GatewayAddresses.Any(g =>
                        g.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !g.Address.Equals(IPAddress.Any)))
                    score += 100;

                foreach (var bad in VirtualHints)
                    if (desc.Contains(bad)) score -= 500;

                candidates.Add((nic, ua.Address, score));
            }
        }

        if (candidates.Count == 0)
        {
            Log.Error("net", "No usable IPv4 interface found");
            return null;
        }

        foreach (var c in candidates.OrderByDescending(c => c.Score))
            Log.Debug("net", $"iface candidate: {c.Nic.Name} ({c.Nic.Description}) {c.Addr} score={c.Score}");

        var best = candidates.OrderByDescending(c => c.Score).First();

        var mac = best.Nic.GetPhysicalAddress();
        if (mac is null || mac.GetAddressBytes().Length != 6)
            mac = new PhysicalAddress(new byte[] { 0x02, 0x00, 0x4C, 0x49, 0x54, 0x45 });

        Log.Info("net", $"Using interface {best.Nic.Name} {best.Addr} mac={FormatMac(mac)}");
        return new NetworkChoice(best.Addr, mac, best.Nic.Name);
    }

    public static string FormatMac(PhysicalAddress mac) =>
        string.Join(":", mac.GetAddressBytes().Select(b => b.ToString("X2")));

    public static TcpListener BindEphemeralTcp(IPAddress bindAddr, out int port)
    {
        var l = new TcpListener(bindAddr, 0);
        l.Start();
        port = ((IPEndPoint)l.LocalEndpoint).Port;
        return l;
    }

    public static UdpClient BindEphemeralUdp(IPAddress bindAddr, out int port)
    {
        var u = new UdpClient(new IPEndPoint(bindAddr, 0));
        port = ((IPEndPoint)u.Client.LocalEndPoint!).Port;
        return u;
    }

    public static bool IsPortFree(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Any, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }

    /// <summary>
    /// Apple's Bonjour Service (mDNSResponder.exe, installed by iTunes) binds UDP 5353
    /// exclusively and will swallow our announcements. Detect it so the UI can warn
    /// instead of failing silently.
    /// </summary>
    public static bool IsAppleBonjourServiceRunning()
    {
        try { return System.Diagnostics.Process.GetProcessesByName("mDNSResponder").Length > 0; }
        catch { return false; }
    }
}
