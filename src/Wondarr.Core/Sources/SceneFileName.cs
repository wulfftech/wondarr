using System.Text.RegularExpressions;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata;

namespace Wondarr.Core.Sources;

/// <summary>What <see cref="SceneFileName"/> read out of one file name.</summary>
/// <param name="Title">The song title left once the track number, the artist and the group tag are gone, version hints removed.</param>
/// <param name="Flags">The version flags the title carried.</param>
/// <param name="Hints">The raw hint segments that produced <paramref name="Flags"/>.</param>
internal sealed record SceneReading(string Title, VersionFlags Flags, IReadOnlyList<string> Hints);

/// <summary>
/// Reads a scene-style file name — <c>NN-artist_name-track_title[-group].ext</c>, underscores for spaces and
/// bare hyphens between the fields — which <see cref="SoulseekFilenameParser"/> cannot split: it splits on
/// a hyphen only when spaces surround it, so it reads <c>01-norah_jones-dont_know_why-ph</c> as one title.
/// Only <see cref="ContainerMatcher"/> uses it, and only for a file its ordinary reading did not match: the
/// release name tells which artist and group to expect, so the fields can be told apart here.
/// </summary>
internal static partial class SceneFileName
{
    /// <summary>
    /// The leading track token and the separator after it: <c>01-</c>, <c>101-</c> (disc and track), <c>1-05-</c>,
    /// vinyl <c>A1-</c>. An underscore has become a space by then, so a space separates as well.
    /// </summary>
    [GeneratedRegex(@"^(?:\d-\d{2}|\d{2,3}|[a-d]\d{1,2})(?:\s*-\s*|\s+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex LeadingTrackToken();

    /// <summary>Reads the file name of <paramref name="path"/>; null when nothing is left of the title.</summary>
    /// <param name="path">The file's path inside its container, either separator.</param>
    /// <param name="releaseArtist">The artist the release name gives, or null when it names none.</param>
    /// <param name="releaseGroup">The scene group tag the release name ends with, or null when it has none.</param>
    public static SceneReading? Read(string path, string? releaseArtist, string? releaseGroup)
    {
        var separator = path.LastIndexOfAny(['/', '\\']);
        var name = separator < 0 ? path : path[(separator + 1)..];
        var dot = name.LastIndexOf('.');

        // The extension is up to five letters or digits, as the ordinary parser reads it.
        if (dot >= 0 && name.Length - dot - 1 is >= 1 and <= 5 && name[(dot + 1)..].All(char.IsAsciiLetterOrDigit))
        {
            name = name[..dot];
        }

        // Underscores are the spaces of a scene name; the bare hyphens stay, they separate its fields.
        var text = name.Replace('_', ' ').Trim();
        var track = LeadingTrackToken().Match(text);

        if (track.Success)
        {
            text = text[track.Length..];
        }

        text = DropArtist(text, releaseArtist);
        text = DropGroup(text, releaseGroup);

        // The fields are dash segments to the version parser, so a "-live" or "-remix" field is a version
        // hint like " - Live" is, and the title that is left keeps its own hyphens.
        var info = VersionFlagParser.Parse(text.Replace("-", " - ", StringComparison.Ordinal));
        var title = info.BaseTitle.Replace(" - ", "-", StringComparison.Ordinal);

        return string.IsNullOrWhiteSpace(title) ? null : new SceneReading(title, info.Flags, info.Hints);
    }

    /// <summary>
    /// Drops the leading field when it is the release's artist. Every hyphen is tried as the end of the
    /// field, so an artist that has hyphens of its own (<c>a-ha</c>, <c>Jay-Z</c>) is still recognised.
    /// </summary>
    private static string DropArtist(string text, string? artist)
    {
        var wanted = string.IsNullOrWhiteSpace(artist) ? string.Empty : TextMatching.NormalizeArtist(artist);

        if (wanted.Length == 0)
        {
            return text;
        }

        for (var index = text.IndexOf('-', StringComparison.Ordinal); index > 0; index = text.IndexOf('-', index + 1))
        {
            if (string.Equals(TextMatching.NormalizeArtist(text[..index]), wanted, StringComparison.Ordinal))
            {
                var rest = text[(index + 1)..].Trim(' ', '-');

                return rest.Length == 0 ? text : rest;
            }
        }

        return text;
    }

    /// <summary>Drops the trailing field when it is the release's group tag, whatever its case.</summary>
    private static string DropGroup(string text, string? group)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            return text;
        }

        var cut = text.LastIndexOf('-');

        if (cut > 0 && string.Equals(text[(cut + 1)..].Trim(), group.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            var rest = text[..cut].Trim(' ', '-');

            return rest.Length == 0 ? text : rest;
        }

        return text;
    }
}
