// rgas-source booth discovery — SPEC C-34.
//
// Sweeps the local /24 subnets for a host listening on the snapcast stream
// port (TCP :4953). A plain bounded TCP connect per candidate — no broadcast
// or mDNS dependency, and nothing else plausibly listens on that port.
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RgasSoundboard.Audio;

public static class BoothDiscovery
{
    /// <summary>All hosts on the local /24 subnets with <paramref name="port"/> open.</summary>
    public static async Task<List<string>> ScanAsync(int port, TimeSpan perHostTimeout)
    {
        var candidates = new HashSet<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var bytes = addr.Address.GetAddressBytes();
                if (bytes[0] == 169 && bytes[1] == 254) continue; // link-local: no DHCP, no booth
                string self = addr.Address.ToString();
                for (int host = 1; host <= 254; host++)
                {
                    var ip = $"{bytes[0]}.{bytes[1]}.{bytes[2]}.{host}";
                    if (ip != self) candidates.Add(ip);
                }
            }
        }

        var probes = candidates.Select(ip => ProbeAsync(ip, port, perHostTimeout)).ToList();
        var results = await Task.WhenAll(probes).ConfigureAwait(false);
        return results.Where(r => r is not null).Select(r => r!).OrderBy(IPSortKey).ToList();
    }

    /// <summary>Bounded check that a single host accepts TCP on <paramref name="port"/>.</summary>
    public static async Task<string?> ProbeAsync(string ip, int port, TimeSpan timeout)
    {
        using var client = new TcpClient();
        try
        {
            var connect = client.ConnectAsync(ip, port);
            var done = await Task.WhenAny(connect, Task.Delay(timeout)).ConfigureAwait(false);
            if (done == connect && client.Connected)
            {
                await connect.ConfigureAwait(false); // surface any stored exception
                return ip;
            }
        }
        catch { }
        return null;
    }

    private static uint IPSortKey(string ip) =>
        BitConverter.ToUInt32(IPAddress.Parse(ip).GetAddressBytes().Reverse().ToArray(), 0);
}
