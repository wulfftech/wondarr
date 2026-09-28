// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Http/Extensions/RequestExtensions.cs, GPL-3.0.
// Adapted for Compilarr: only the remote address helper is kept, and it returns an IPAddress?

using System.Net;
using Microsoft.AspNetCore.Http;

namespace Compilarr.Api.Extensions;

/// <summary>Request helpers used by the authentication middleware.</summary>
public static class RequestExtensions
{
    /// <summary>
    /// The address the request came from, with IPv4-mapped IPv6 addresses mapped back to IPv4.
    /// Returns <see langword="null"/> when the transport did not report one.
    /// </summary>
    public static IPAddress? GetRemoteIp(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var remoteIp = context.Connection.RemoteIpAddress;

        return remoteIp is { IsIPv4MappedToIPv6: true } ? remoteIp.MapToIPv4() : remoteIp;
    }
}
