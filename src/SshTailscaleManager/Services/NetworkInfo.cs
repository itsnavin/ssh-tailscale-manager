using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SshTailscaleManager.Services;

public static class NetworkInfo
{
    /// <summary>Returns LAN IPv4 addresses, excluding loopback, link-local and Tailscale (100.64.0.0/10).</summary>
    public static List<string> GetLanIPv4Addresses()
    {
        var results = new List<(string Ip, bool HasGateway)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (ni.Description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ||
                ni.Name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase)) continue;

            var props = ni.GetIPProperties();
            var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                                             !g.Address.Equals(System.Net.IPAddress.Any));
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var b = ua.Address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue;
                if (b[0] == 100 && (b[1] & 0xC0) == 64) continue;
                results.Add((ua.Address.ToString(), hasGateway));
            }
        }

        return results.OrderByDescending(r => r.HasGateway).Select(r => r.Ip).Distinct().ToList();
    }
}
