using System.Net;
using System.Net.Sockets;
using System.Text;
using static NetSynq.Core.I18n;

namespace NetSynq.Core;

/// <summary>
/// Ricerca automatica del master in LAN via UDP broadcast. Il master risponde solo a chi sta nella
/// sua stessa subnet LAN (quindi mai a richieste arrivate dalla VPN).
/// </summary>
public static class Discovery
{
    public const int UdpPort = 48766;
    private const string Query = "NETSYNQ?1";

    public sealed class Found
    {
        public string Name { get; set; } = "";
        public IPAddress Ip { get; set; } = IPAddress.None;
        public int Port { get; set; }
        public override string ToString() => $"{Name}  ({Ip}:{Port})";
    }

    public sealed class Responder : IDisposable
    {
        private readonly UdpClient _udp;
        private readonly CancellationTokenSource _cts = new();

        public Responder(NicInfo lan, int tcpPort, string name, Action<string> log, int udpPort = UdpPort)
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort));
            var reply = Encoding.UTF8.GetBytes($"NETSYNQ!1|{name}|{tcpPort}");
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        var r = await _udp.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                        if (Encoding.UTF8.GetString(r.Buffer) != Query) continue;
                        if (!NetworkHelper.SameSubnet(lan.Ip, r.RemoteEndPoint.Address, lan.Mask)) continue;
                        await _udp.SendAsync(reply, r.RemoteEndPoint, _cts.Token).ConfigureAwait(false);
                        log(Tr("net.disc.log_reply", r.RemoteEndPoint.Address));
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch { /* ignora datagrammi strani */ }
                }
            });
        }

        public void Dispose() { _cts.Cancel(); _udp.Dispose(); }
    }

    /// <summary>Invia il broadcast da ogni interfaccia LAN (e verso tutti i broadcast) e raccoglie le risposte.</summary>
    public static async Task<List<Found>> FindAsync(TimeSpan wait, int udpPort = UdpPort, IEnumerable<NicInfo>? nics = null)
    {
        var found = new Dictionary<string, Found>();
        var list = (nics ?? NetworkHelper.GetCandidates()).ToList();
        var tasks = new List<Task>();
        foreach (var nic in list)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var u = new UdpClient(new IPEndPoint(nic.Ip, 0)) { EnableBroadcast = true };
                    var data = Encoding.UTF8.GetBytes(Query);
                    await u.SendAsync(data, new IPEndPoint(NetworkHelper.Broadcast(nic.Ip, nic.Mask), udpPort)).ConfigureAwait(false);
                    await u.SendAsync(data, new IPEndPoint(IPAddress.Broadcast, udpPort)).ConfigureAwait(false);
                    using var cts = new CancellationTokenSource(wait);
                    while (!cts.IsCancellationRequested)
                    {
                        var r = await u.ReceiveAsync(cts.Token).ConfigureAwait(false);
                        var s = Encoding.UTF8.GetString(r.Buffer).Split('|');
                        if (s.Length == 3 && s[0] == "NETSYNQ!1" && int.TryParse(s[2], out var port))
                            lock (found) found[r.RemoteEndPoint.Address + ":" + port] =
                                new Found { Name = s[1], Ip = r.RemoteEndPoint.Address, Port = port };
                    }
                }
                catch { /* timeout o interfaccia non utilizzabile */ }
            }));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return found.Values.ToList();
    }
}
