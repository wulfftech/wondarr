using System.Globalization;
using Wondarr.Core.Domain;
using Wondarr.Core.Media;
using Wondarr.Core.Metadata;
using Wondarr.Core.Organizer;
using Wondarr.Core.Sources;

namespace Wondarr.Core.Importing;

/// <summary>
/// Fills a <see cref="NamingValues"/> from a song, its album context and what the file measured, so
/// the naming template can render a path for it (LIBRARY_OUTPUT §7.1). Pure and deterministic.
/// </summary>
public static class NamingValuesBuilder
{
    /// <summary>
    /// The readable labels of the version flags, in the order they are joined. <c>remaster</c> is not
    /// in the list: a remaster is the same recording, so it never becomes part of a file name.
    /// </summary>
    private static readonly (VersionFlags Flag, string Label)[] VersionLabels =
    [
        (VersionFlags.Live, "Live"),
        (VersionFlags.Remix, "Remix"),
        (VersionFlags.Acoustic, "Acoustic"),
        (VersionFlags.Instrumental, "Instrumental"),
        (VersionFlags.Acapella, "Acapella"),
        (VersionFlags.RadioEdit, "Radio Edit"),
        (VersionFlags.Edit, "Edit"),
        (VersionFlags.Extended, "Extended"),
        (VersionFlags.Explicit, "Explicit"),
        (VersionFlags.Clean, "Clean"),
        (VersionFlags.Cover, "Cover"),
        (VersionFlags.Karaoke, "Karaoke"),
        (VersionFlags.Demo, "Demo"),
        (VersionFlags.Slowed, "Slowed"),
        (VersionFlags.EightD, "8D"),
        (VersionFlags.BassBoosted, "Bass Boosted"),
    ];

    /// <summary>Builds the naming values for one imported file.</summary>
    /// <param name="song">The song the file satisfies.</param>
    /// <param name="album">The album context the song is filed under.</param>
    /// <param name="primaryArtist">The song's primary artist.</param>
    /// <param name="media">What the probe measured.</param>
    /// <param name="quality">The quality the file was matched to.</param>
    /// <param name="sourceType">The source the file came from, one of <see cref="SourceTypes"/>.</param>
    /// <returns>The values the template renders from.</returns>
    public static NamingValues Build(
        Song song,
        AlbumContext album,
        Artist primaryArtist,
        MediaInfo media,
        Quality quality,
        string sourceType)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(album);
        ArgumentNullException.ThrowIfNull(primaryArtist);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(quality);

        return new NamingValues
        {
            ArtistName = primaryArtist.Name,
            AlbumArtistName = album.AlbumArtist,
            AlbumTitle = album.AlbumTitle,
            AlbumType = AlbumType(album.Kind),
            ReleaseYear = Year(album.Date),
            OriginalYear = Year(album.OriginalDate),
            TrackTitle = song.Title,
            TrackArtistName = song.ArtistCredit,
            TrackNo = album.TrackNo,
            DiscNo = album.DiscNo,

            // A context with no disc number is a one-disc release, and the medium token stays empty
            // for anything the template is told has one disc ("{medium:0}{track:00}").
            DiscCount = 1,

            ArtistMbId = primaryArtist.MbArtistId,
            AlbumMbId = album.AlbumKey,
            RecordingMbId = song.MbRecordingId,
            ReleaseMbId = album.AlbumKey,
            Isrc = song.Isrcs.Count > 0 ? song.Isrcs[0] : null,
            QualityTitle = quality.Name,
            QualityFull = quality.Name,
            AudioCodec = media.Codec,
            AudioBitRate = media.BitrateKbps,
            AudioSampleRate = media.SampleRate,
            AudioBitsPerSample = media.BitDepth,
            Source = SourceName(sourceType),
            Version = Version(song.VersionFlags),
        };
    }

    /// <summary>
    /// The album shape as a readable word. A pseudo-album of loose singles is shown as an album: that
    /// is what it is on disk, and the folder name is not the place to explain the policy.
    /// </summary>
    private static string AlbumType(AlbumContextKind kind) => kind switch
    {
        AlbumContextKind.Single => "Single",
        AlbumContextKind.Ep => "EP",
        AlbumContextKind.Compilation => "Compilation",
        _ => "Album",
    };

    /// <summary>The first four digits of a <c>YYYY</c>, <c>YYYY-MM</c> or <c>YYYY-MM-DD</c> date.</summary>
    private static int? Year(string? date)
    {
        if (date is null || date.Length < 4)
        {
            return null;
        }

        return int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            ? year
            : null;
    }

    /// <summary>The display name of a source type, as LIBRARY_OUTPUT §7.1's <c>{Source}</c> reads.</summary>
    private static string? SourceName(string? sourceType) => sourceType switch
    {
        SourceTypes.Soulseek => "Soulseek",
        SourceTypes.YouTube => "YouTube",
        SourceTypes.Torznab => "Torznab",
        SourceTypes.Newznab => "Newznab",
        _ => null,
    };

    /// <summary>
    /// The song's version flags as a readable phrase ("Live, Acoustic"), or <see langword="null"/>
    /// when it has none. A remaster does not count: it is the same recording.
    /// </summary>
    private static string? Version(IEnumerable<string> wireNames)
    {
        var flags = VersionFlags.None;

        foreach (var name in wireNames)
        {
            if (VersionFlagNames.TryParse(name, out var flag))
            {
                flags |= flag;
            }
        }

        var labels = VersionLabels
            .Where(entry => (flags & entry.Flag) == entry.Flag)
            .Select(entry => entry.Label)
            .ToList();

        return labels.Count == 0 ? null : string.Join(", ", labels);
    }
}