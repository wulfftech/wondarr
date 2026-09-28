// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/NzbDrone.Common/Instrumentation/CleanseLogMessage.cs, GPL-3.0.
// Adaptation: tracker-specific rules (TorrentLeech, IPTorrents, HD.me, Avistaz, BroadcastheNet, Notifiarr)
// dropped, because Compilarr talks to torrent/usenet indexers rather than private trackers; added rules for the
// X-Api-Key and Authorization: Bearer request headers; the IP helper is local instead of NzbDrone.Common.Extensions.

using System.Net;
using System.Text.RegularExpressions;

namespace Compilarr.Core.Logging;

/// <summary>
/// Removes credentials from a log message before it reaches a sink. Every rule keeps the rest of
/// the match and replaces only the <c>secret</c> capture with <c>(removed)</c>.
/// </summary>
public static class CleanseLogMessage
{
    private const string Removed = "(removed)";

    private static readonly Regex[] CleansingRules =
    {
        // Url
        new(@"(?<=[?&: ;])((?:api|auth|pass)?key|(?:access[-_]?|refresh_)?token|auth|user|u?id|api|[a-z_]*apikey|account|passwd|pwd)=(?<secret>[^&=""]+?)(?=[ ""&=]|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"(?<=[?& ])[^=]*?(username|passwo?rd)=(?<secret>[^&=]+?)(?= |&|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),

        // Request headers
        new(@"X-Api-Key:\s*(?<secret>[^\s,;""'}]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"Authorization:\s*Bearer\s+(?<secret>[^\s,;""'}]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),

        // Path
        new(@"C:\\Users\\(?<secret>[^\""]+?)(\\|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"/(home|Users)/(?<secret>[^/""]+?)(/|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),

        // Trackers Announce Keys; Designed for Qbit Json; should work for all in theory
        new(@"announce(\.php)?(/|%2f|%3fpasskey%3d)(?<secret>[a-z0-9]{16,})|(?<secret>[a-z0-9]{16,})(/|%2f)announce", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"announce(\.php)?(/|%2f|%3fpasskey%3d)(?<secret>[a-z0-9]{16,})|(?<secret>[a-z0-9]{16,})(/|%2f)announce"),

        // NzbGet
        new(@"""Name""\s*:\s*""[^""]*(username|password)""\s*,\s*""Value""\s*:\s*""(?<secret>[^""]+?)""", RegexOptions.Compiled | RegexOptions.IgnoreCase),

        // Sabnzbd
        new(@"""[^""]*(username|password|api_?key|nzb_key)""\s*:\s*""(?<secret>[^""]+?)""", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"""email_(account|to|from|pwd)""\s*:\s*""(?<secret>[^""]+?)""", RegexOptions.Compiled | RegexOptions.IgnoreCase),

        // uTorrent
        new(@"\[""[a-z._]*(username|password)"",\d,""(?<secret>[^""]+?)""", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\[""(boss_key|boss_key_salt|proxy\.proxy)"",\d,""(?<secret>[^""]+?)""", RegexOptions.Compiled | RegexOptions.IgnoreCase),

        // Deluge
        new(@"auth.login\(""(?<secret>[^""]+?)""", RegexOptions.Compiled | RegexOptions.IgnoreCase),

        // Plex
        new(@"(?<=\?|&)(X-Plex-Client-Identifier|X-Plex-Token)=(?<secret>[^&=]+?)(?= |&|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase),

        // Indexer Responses
        new(@",""info_hash"":""(?<secret>[^&=]+?)"",", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@",""pass[- _]?key"":""(?<secret>[^&=]+?)"",", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@",""rss[- _]?key"":""(?<secret>[^&=]+?)"",", RegexOptions.Compiled | RegexOptions.IgnoreCase),

        // Discord
        new(@"discord.com/api/webhooks/((?<secret>[\w-]+)/)?(?<secret>[\w-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase),

        // Telegram
        new(@"api.telegram.org/bot(?<id>[\d]+):(?<secret>[\w-]+)/", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };

    private static readonly Regex CleanseRemoteIpRegex = new(@"(?:Auth-\w+(?<!Failure|Unauthorized) ip|from) (\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})", RegexOptions.Compiled);

    /// <summary>
    /// Returns <paramref name="message"/> with every recognised secret replaced by <c>(removed)</c>.
    /// </summary>
    public static string Cleanse(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return message;
        }

        foreach (var regex in CleansingRules)
        {
            message = regex.Replace(message, match =>
            {
                var value = match.Value;

                // Right to left, so the offsets of the captures still to come stay valid.
                foreach (var capture in match.Groups["secret"].Captures.OfType<Capture>().Reverse())
                {
                    var start = capture.Index - match.Index;
                    value = value.Remove(start, capture.Length).Insert(start, Removed);
                }

                return value;
            });
        }

        return CleanseRemoteIpRegex.Replace(message, CleanseRemoteIp);
    }

    private static string CleanseRemoteIp(Match match)
    {
        var group = match.Groups[1];
        var value = group.Value;

        if (IPAddress.TryParse(value, out var address) && !IsLocalAddress(address))
        {
            var prefix = match.Value[..(group.Index - match.Index)];
            var postfix = match.Value[(group.Index + group.Length - match.Index)..];
            var items = value.Split('.');

            return $"{prefix}{items[0]}.*.*.{items[3]}{postfix}";
        }

        return match.Value;
    }

    /// <summary>Loopback, link-local and private ranges — mirrors the Lidarr helper of the same name.</summary>
    private static bool IsLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();

        if (bytes.Length == 4)
        {
            return bytes[0] == 10
                   || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || (bytes[0] == 169 && bytes[1] == 254);
        }

        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
    }
}
