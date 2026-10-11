using System.Net;
using System.Net.Sockets;
using Wondarr.Api.Extensions;
using Wondarr.Core.Configuration;
using Microsoft.AspNetCore.Http;

namespace Wondarr.Api.Authentication;

/// <summary>
/// The one definition of "a local caller", shared by the <c>disabledForLocalAddresses</c> bypass
/// (<see cref="UiAuthorizationHandler"/>) and first-login setup. A request is local only when:
/// <list type="number">
/// <item>it carries none of the proxy headers (R-008);</item>
/// <item>its socket address is local (<see cref="IpAddressExtensions.IsLocalAddress"/>); and</item>
/// <item>its <c>Host</c> is a LAN-style name (<see cref="IsLocalHost"/>), so a page reaching the app
/// through a rebound public name is not trusted (R-009).</item>
/// </list>
/// A request that fails the rule is not refused: it simply needs a login.
/// </summary>
public static class LocalCallerRules
{
    // Any of these means a proxy is in the path, so the socket address is the proxy's, not the visitor's.
    // X-Original-For is what the ForwardedHeaders middleware leaves behind when it rewrites the address.
    private static readonly string[] ProxyHeaders =
    [
        "X-Forwarded-For",
        "Forwarded",
        "X-Real-IP",
        "X-Forwarded-Host",
        "X-Forwarded-Proto",
        "Via",
        "X-Original-For",
        "X-Original-Proto",
        "X-Original-Host",
        "X-Forwarded-Prefix",
        "CF-Connecting-IP",
        "True-Client-IP",
    ];

    private static readonly string[] LanSuffixes = [".local", ".lan", ".home.arpa", ".internal"];

    /// <summary>Whether the request comes from the local network, by all three rules.</summary>
    public static bool IsLocalCaller(HttpContext context, ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        return !HasProxyHeader(context.Request) &&
            context.GetRemoteIp() is { } remoteIp &&
            remoteIp.IsLocalAddress() &&
            IsLocalHost(context.Request.Host.Host, options.AllowedHosts);
    }

    /// <summary>Whether the request carries any header a reverse proxy adds.</summary>
    public static bool HasProxyHeader(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ProxyHeaders.Any(request.Headers.ContainsKey);
    }

    /// <summary>
    /// Whether a <c>Host</c> header value (port already stripped, IPv6 in brackets) names a local
    /// machine: a local IP literal, <c>localhost</c> or <c>*.localhost</c>, a single-label name, a
    /// name under <c>.local</c>, <c>.lan</c>, <c>.home.arpa</c> or <c>.internal</c>, or a name in
    /// <paramref name="allowedHosts"/> (exact, or <c>*.suffix</c>). A missing host is not local.
    /// A single-label name that starts with a digit is not local either, which excludes numeric
    /// spellings of an address such as <c>0x08080808</c>. A trailing dot (<c>nas.lan.</c>) fails
    /// closed: the name no longer matches a suffix or counts as single-label.
    /// </summary>
    public static bool IsLocalHost(string? host, IEnumerable<string>? allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var name = host.Trim();

        if (name.StartsWith('[') && name.EndsWith(']'))
        {
            return IPAddress.TryParse(name[1..^1], out var v6) &&
                v6.AddressFamily == AddressFamily.InterNetworkV6 &&
                v6.IsLocalAddress();
        }

        if (IsDottedQuad(name))
        {
            return IPAddress.TryParse(name, out var v4) && v4.IsLocalAddress();
        }

        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // A leading digit is a number in some spelling, not a machine name; browsers read it as an address.
        if (!name.Contains('.', StringComparison.Ordinal) && !char.IsAsciiDigit(name[0]))
        {
            return true;
        }

        if (LanSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return allowedHosts is not null && allowedHosts.Any(entry => MatchesAllowedHost(name, entry));
    }

    private static bool MatchesAllowedHost(string name, string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        // "*.example.lan" matches any name under it, but not the bare suffix.
        return entry.StartsWith("*.", StringComparison.Ordinal)
            ? name.Length > entry.Length - 1 && name.EndsWith(entry[1..], StringComparison.OrdinalIgnoreCase)
            : name.Equals(entry, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDottedQuad(string name) =>
        name.Count(character => character == '.') == 3 && name.All(character => char.IsAsciiDigit(character) || character == '.');
}
