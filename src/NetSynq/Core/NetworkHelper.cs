using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using static NetSynq.Core.I18n;

namespace NetSynq.Core;

public sealed class NicInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public IPAddress Ip { get; set; } = IPAddress.None;
    public IPAddress Mask { get; set; } = IPAddress.None;
    public bool Suspect { get; set; }        // sembra VPN / virtuale
    public bool HasGateway { get; set; }
    public bool IsPrivate { get; set; }

    public string Display => $"{Ip}  —  {Name}{(Suspect ? Tr("net.nic.suspect") : "")}";
    public override string ToString() => Display;
}

public static class NetworkHelper
{
    private static readonly Regex SuspectRx = new(
        "vpn|tap-|tap |tun|wireguard|wintun|openvpn|cisco|anyconnect|fortinet|forticlient|pulse|juniper|zerotier|tailscale|" +
        "nordlynx|globalprotect|palo alto|sonicwall|check ?point|virtual|vmware|vbox|hyper-v|vethernet|wsl|docker|loopback|" +
        "bluetooth|ppp|l2tp|sstp|ikev2|pptp",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Tutte le interfacce attive con un IPv4 utilizzabile (no loopback, no 169.254.x.x).</summary>
    public static List<NicInfo> GetCandidates(bool includeLoopback = false)
    {
        var list = new List<NicInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (!includeLoopback && nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                bool gw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                           && !g.Address.Equals(IPAddress.Any));
                bool typeSuspect = nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp
                                   or NetworkInterfaceType.Slip;
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = ua.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue;
                    list.Add(new NicInfo
                    {
                        Name = nic.Name,
                        Description = nic.Description,
                        Ip = ua.Address,
                        Mask = ua.IPv4Mask ?? IPAddress.Parse("255.255.255.0"),
                        HasGateway = gw,
                        IsPrivate = IsPrivate(ua.Address),
                        Suspect = typeSuspect || SuspectRx.IsMatch(nic.Name + " " + nic.Description)
                    });
                }
            }
            catch { /* interfaccia problematica: ignorata */ }
        }
        return list;
    }

    /// <summary>
    /// Sceglie l'interfaccia migliore per parlare con la LAN: se è noto l'IP dell'altro PC vince l'interfaccia
    /// che sta nella sua stessa subnet; poi si scartano quelle che sembrano VPN/virtuali.
    /// </summary>
    public static NicInfo? Pick(IEnumerable<NicInfo> nics, IPAddress? peer)
    {
        NicInfo? best = null; int bestScore = int.MinValue;
        foreach (var n in nics)
        {
            int s = 0;
            if (peer != null && SameSubnet(n.Ip, peer, n.Mask)) s += 100;
            if (!n.Suspect) s += 50;
            if (n.IsPrivate) s += 20;
            if (!n.HasGateway) s += 1; // una VPN spesso ha il gateway; la LAN "sola" può non averlo
            if (s > bestScore) { bestScore = s; best = n; }
        }
        return best;
    }

    public static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        var x = a.GetAddressBytes(); var y = b.GetAddressBytes(); var m = mask.GetAddressBytes();
        if (x.Length != 4 || y.Length != 4 || m.Length != 4) return false;
        for (int i = 0; i < 4; i++) if ((x[i] & m[i]) != (y[i] & m[i])) return false;
        return true;
    }

    public static IPAddress Broadcast(IPAddress ip, IPAddress mask)
    {
        var x = ip.GetAddressBytes(); var m = mask.GetAddressBytes();
        var r = new byte[4];
        for (int i = 0; i < 4; i++) r[i] = (byte)(x[i] | ~m[i]);
        return new IPAddress(r);
    }

    public static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b.Length != 4) return false;
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }
}
