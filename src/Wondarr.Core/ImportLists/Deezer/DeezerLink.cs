using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wondarr.Core.ImportLists.Deezer;

/// <summary>
/// Reads a Deezer id out of what the user typed: a bare number, or a link of the site's own shapes —
/// <c>https://www.deezer.com/playlist/908622995</c>, with or without a language segment, a query or a
/// scheme. Deezer's short links (<c>deezer.page.link/…</c>) hide the id behind a redirect, so they are
/// recognised and refused rather than guessed at.
/// </summary>
internal static class DeezerLink
{
    /// <summary>The id inside a <c>/playlist/…</c> or <c>/artist/…</c> path.</summary>
    private static readonly Regex IdInPath = new(
        @"(?:^|/)(?<kind>playlist|artist)/(?<id>\d+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>Deezer's short-link host.</summary>
    public const string ShortLinkHost = "deezer.page.link";

    /// <summary>Whether the value is a short link, which Wondarr cannot follow.</summary>
    public static bool IsShortLink(string value) =>
        value.Contains(ShortLinkHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the playlist id out of a bare number or a playlist link.</summary>
    public static long? PlaylistId(string? value) => Id(value, "playlist");

    /// <summary>Reads the artist id out of a bare number or an artist link.</summary>
    public static long? ArtistId(string? value) => Id(value, "artist");

    private static long? Id(string? value, string kind)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var bare) && bare > 0)
        {
            return bare;
        }

        var match = IdInPath.Match(text);

        return match.Success
            && match.Groups["kind"].Value.Equals(kind, StringComparison.OrdinalIgnoreCase)
            && long.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && id > 0
                ? id
                : null;
    }
}

/// <summary>What both Deezer providers read out of a list's settings.</summary>
internal static class DeezerListSettings
{
    /// <summary>The trimmed value of one settings property, when it is a non-empty string.</summary>
    public static string? Setting(JsonElement settings, string name) =>
        settings.ValueKind == JsonValueKind.Object
            && settings.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text
            && text.Trim().Length > 0
                ? text.Trim()
                : null;
}
