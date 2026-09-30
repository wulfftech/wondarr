using ATL;
using Microsoft.Extensions.Logging;

namespace Wondarr.Core.Tagging;

/// <summary>
/// What a file's own tags say, as far as the §7.5 mapping goes. Every field is <see langword="null"/>
/// when the file does not carry it; nothing is inferred from the file name.
/// </summary>
/// <param name="Title">The title tag, or <see langword="null"/>.</param>
/// <param name="Artist">The artist tag, or <see langword="null"/>.</param>
/// <param name="AlbumArtist">The album-artist tag, or <see langword="null"/>.</param>
/// <param name="Album">The album tag, or <see langword="null"/>.</param>
/// <param name="Date">The date as <c>yyyy-MM-dd</c>, <c>yyyy-MM</c> or <c>yyyy</c>, or <see langword="null"/>.</param>
/// <param name="Isrc">The ISRC, or <see langword="null"/>.</param>
/// <param name="MbRecordingId">The MusicBrainz recording id, or <see langword="null"/>.</param>
/// <param name="MbReleaseId">The MusicBrainz release id, or <see langword="null"/>.</param>
/// <param name="MbReleaseGroupId">The MusicBrainz release-group id, or <see langword="null"/>.</param>
/// <param name="MbArtistId">The MusicBrainz artist id, or <see langword="null"/>.</param>
/// <param name="AcoustId">The AcoustID, or <see langword="null"/>.</param>
/// <param name="TrackNumber">The track number, or <see langword="null"/>.</param>
/// <param name="TrackTotal">The track total, or <see langword="null"/>.</param>
/// <param name="DiscNumber">The disc number, or <see langword="null"/>.</param>
/// <param name="DurationMs">The duration in milliseconds, or <see langword="null"/>.</param>
public sealed record FileTags(
    string? Title,
    string? Artist,
    string? AlbumArtist,
    string? Album,
    string? Date,
    string? Isrc,
    string? MbRecordingId,
    string? MbReleaseId,
    string? MbReleaseGroupId,
    string? MbArtistId,
    string? AcoustId,
    int? TrackNumber,
    int? TrackTotal,
    int? DiscNumber,
    int? DurationMs);

/// <summary>Reads the tags of a file that may not be ours, for identification (LIBRARY_OUTPUT §7.6).</summary>
public interface ITagReader
{
    /// <summary>
    /// Reads the tags of <paramref name="path"/>. A file ATL cannot read, and a file that carries none
    /// of the mapped fields, both yield <see langword="null"/> rather than an exception.
    /// </summary>
    /// <param name="path">Absolute path of the file to read.</param>
    /// <returns>The tags found, or <see langword="null"/>.</returns>
    FileTags? Read(string path);
}

/// <summary>
/// Reads tags through the same ATL field keys <see cref="TagWriter"/> writes, so a file Wondarr tagged
/// and a file Picard tagged are read the same way. Only the reading is shared: nothing here writes.
/// </summary>
public sealed partial class TagReader(ILogger<TagReader> logger) : ITagReader
{
    /// <summary>
    /// ATL reports a <c>UFID</c> frame as one <c>AdditionalFields</c> entry under this key, holding the
    /// owner, a NUL and the identifier — Picard's recording-id frame (LIBRARY_OUTPUT §7.5).
    /// </summary>
    private const string UfidKey = "UFID";

    /// <summary>The owner MusicBrainz's own tagger writes; only that owner's UFID is a recording id.</summary>
    private const string MusicBrainzUfidOwner = "http://musicbrainz.org";

    /// <summary>MP4 and Vorbis carry the ISRC in their own freeform/comment field rather than in an ATL property.</summary>
    private const string IsrcKey = "ISRC";

    /// <inheritdoc />
    public FileTags? Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Track track;

        try
        {
            track = new Track(path);
        }
        catch (Exception exception)
        {
            // A file ATL cannot parse is the normal case here: a reference library holds whatever the
            // user has, so this is a Debug line rather than a failure.
            LogUnreadable(logger, exception);

            return null;
        }

        var keys = KeysOrNull(path);

        var tags = new FileTags(
            Title: Text(track.Title),
            Artist: Text(track.Artist),
            AlbumArtist: Text(track.AlbumArtist),
            Album: Text(track.Album),
            Date: Date(track),
            Isrc: Text(track.ISRC) ?? RawAdditional(track, IsrcKey),
            MbRecordingId: RecordingId(track, keys),
            MbReleaseId: Guid1(RawAdditional(track, keys?.ReleaseId)),
            MbReleaseGroupId: Guid1(RawAdditional(track, keys?.ReleaseGroupId)),
            MbArtistId: Guid1(RawAdditional(track, keys?.ArtistId)),
            AcoustId: RawAdditional(track, keys?.AcoustId),
            TrackNumber: Positive(track.TrackNumber),
            TrackTotal: Positive(track.TrackTotal),
            DiscNumber: Positive(track.DiscNumber),
            DurationMs: track.DurationMs > 0 ? (int)Math.Round(track.DurationMs) : null);

        // ATL falls back to the file name when the format carries no title at all, and that fallback is
        // not a tag: a file whose only "title" is its own name is treated as carrying nothing. A file
        // ATL cannot parse at all (random bytes named .mp3) gets exactly that treatment too.
        if (tags.Title is not null
            && string.Equals(tags.Title, Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)
            && IsEmptyApartFromTitle(tags))
        {
            tags = tags with { Title = null };
        }

        return IsEmpty(tags) ? null : tags;
    }

    /// <summary>
    /// The recording id: Picard's <c>UFID</c> frame first, then the <c>TXXX</c>/freeform field Wondarr's
    /// own writer produces (ATL 7.17 cannot write UFID).
    /// </summary>
    private static string? RecordingId(Track track, TagWriter.FormatKeys? keys)
    {
        var ufid = TagWriter.GetAdditional(track, UfidKey);

        if (ufid is not null)
        {
            // "<owner>\0<identifier>"; anything that is not MusicBrainz's own owner is not a recording id.
            var separator = ufid.IndexOf('\0', StringComparison.Ordinal);

            if (separator >= 0
                && string.Equals(ufid[..separator], MusicBrainzUfidOwner, StringComparison.OrdinalIgnoreCase))
            {
                var identifier = Guid1(ufid[(separator + 1)..]);

                if (identifier is not null)
                {
                    return identifier;
                }
            }
        }

        return Guid1(RawAdditional(track, keys?.RecordingId));
    }

    /// <summary>One of the format's own freeform fields, trimmed, or <see langword="null"/>.</summary>
    private static string? RawAdditional(Track track, string? key) =>
        key is null ? null : Text(TagWriter.GetAdditional(track, key));

    /// <summary>
    /// A MusicBrainz identifier, lower-cased, or <see langword="null"/> when the field holds something
    /// that is not one — tags in the wild hold album names and URLs in these fields too.
    /// </summary>
    private static string? Guid1(string? value)
    {
        var text = Text(value);

        return text is not null && Guid.TryParse(text, out var parsed) ? parsed.ToString("D") : null;
    }

    /// <summary>
    /// The date at the precision the file carries it: a year-only tag stays a year. ATL reports a file
    /// with no date at all as year 1, which is not a date a tag could have carried.
    /// </summary>
    private static string? Date(Track track) =>
        track.Date is { Year: >= 1000 } date ? TagWriter.FormatDate(date) : TagWriter.FormatYear(track.Year);

    /// <summary>The writer's field keys for the format, or <see langword="null"/> for a format it does not know.</summary>
    private static TagWriter.FormatKeys? KeysOrNull(string path)
    {
        try
        {
            return TagWriter.KeysFor(path);
        }
        catch (NotSupportedException)
        {
            // An unknown extension still has the plain fields (title, artist, album, …); only the
            // format-specific freeform keys are missing.
            return null;
        }
    }

    /// <summary>Whether the file carried none of the mapped fields.</summary>
    private static bool IsEmpty(FileTags tags) =>
        IsEmptyApartFromTitle(tags) && tags.Title is null && tags.Date is null && tags.DurationMs is null;

    /// <summary>Whether nothing but the title (and the duration) was read.</summary>
    private static bool IsEmptyApartFromTitle(FileTags tags) =>
        tags.Artist is null
        && tags.AlbumArtist is null
        && tags.Album is null
        && tags.Date is null
        && tags.Isrc is null
        && tags.MbRecordingId is null
        && tags.MbReleaseId is null
        && tags.MbReleaseGroupId is null
        && tags.MbArtistId is null
        && tags.AcoustId is null
        && tags.TrackNumber is null
        && tags.TrackTotal is null
        && tags.DiscNumber is null;

    private static int? Positive(int? value) => value is > 0 ? value : null;

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [LoggerMessage(Level = LogLevel.Debug, Message = "ATL could not read the tags of a file.")]
    private static partial void LogUnreadable(ILogger logger, Exception exception);
}