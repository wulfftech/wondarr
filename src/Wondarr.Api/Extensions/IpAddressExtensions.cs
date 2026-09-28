// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Common/Extensions/IpAddressExtensions.cs, GPL-3.0.
// Adapted for Wondarr: CGNAT handling dropped; private and loopback ranges are treated as local.

using System.Net;
using System.Net.Sockets;

namespace Wondarr.Api.Extensions;

/// <summary>Address checks used by the "authentication disabled for local addresses" bypass.</summary>
public static class IpAddressExtensions
{
    /// <summary>
    /// Whether the address is on the machine or on the local network: loopback, link local, or an
    /// RFC 1918 private range, in IPv4 or IPv6.
    /// </summary>
    public static bool IsLocalAddress(this IPAddress ipAddress)
    {
        ArgumentNullException.ThrowIfNull(ipAddress);

        // Map back to IPv4 if mapped to IPv6, for example "::ffff:1.2.3.4" to "1.2.3.4".
        if (ipAddress.IsIPv4MappedToIPv6)
        {
            ipAddress = ipAddress.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ipAddress))
        {
            return true;
        }

        if (ipAddress.AddressFamily == AddressFamily.InterNetwork)
        {
            return IsLocalIPv4(ipAddress.GetAddressBytes());
        }

        if (ipAddress.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return ipAddress.IsIPv6LinkLocal || ipAddress.IsIPv6UniqueLocal || ipAddress.IsIPv6SiteLocal;
        }

        return false;
    }

    private static bool IsLocalIPv4(byte[] ipv4Bytes)
    {
        // Link local (no address from DHCP): 169.254.0.0/16.
        var isLinkLocal = ipv4Bytes[0] == 169 && ipv4Bytes[1] == 254;

        // 10.0.0.0/8.
        var isClassA = ipv4Bytes[0] == 10;

        // 172.16.0.0/12.
        var isClassB = ipv4Bytes[0] == 172 && ipv4Bytes[1] >= 16 && ipv4Bytes[1] <= 31;

        // 192.168.0.0/16.
        var isClassC = ipv4Bytes[0] == 192 && ipv4Bytes[1] == 168;

        return isLinkLocal || isClassA || isClassB || isClassC;
    }
}
