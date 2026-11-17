using System.Text;
using System.Text.Json.Serialization;

namespace Wondarr.Core.Metadata.MusicBrainz;

/// <summary>A MusicBrainz artist, as embedded in an artist credit.</summary>
public sealed record MbArtist
{
    /// <summary>Gets the artist MBID.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the artist's name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets the name used for sorting.</summary>
    public string? SortName { get; init; }

    /// <summary>Gets the disambiguation comment that separates same-named artists.</summary>
    public string? Disambiguation { get; init; }
}

/// <summary>
/// One "credited as" entry: the name as it appears on the release, the join phrase that binds it to
/// the next entry, and the artist it points at.
/// </summary>
public sealed record MbArtistCredit
{
    /// <summary>Gets the credited name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the join phrase (" &amp; ", " feat. ", …). MusicBrainz writes the key without the inner
    /// capital, so it needs naming explicitly.
    /// </summary>
    [JsonPropertyName("joinphrase")]
    public string? JoinPhrase { get; init; }

    /// <summary>Gets the artist this credit resolves to.</summary>
    public MbArtist Artist { get; init; } = new();

    /// <summary>Renders a credit list the way MusicBrainz shows it: name + joinphrase, repeated.</summary>
    /// <param name="credits">The credit list, in order.</param>
    /// <returns>For example <c>Daft Punk feat. Pharrell Williams</c>.</returns>
    public static string Format(IReadOnlyList<MbArtistCredit> credits)
    {
        ArgumentNullException.ThrowIfNull(credits);

        var builder = new StringBuilder();

        foreach (var credit in credits)
        {
            builder.Append(credit.Name);
            builder.Append(credit.JoinPhrase);
        }

        return builder.ToString();
    }
}

/// <summary>A recording: the app's unit of "one song".</summary>
public sealed record MbRecording
{
    /// <summary>Gets the recording MBID.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the recording title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the length in milliseconds; often missing outside a lookup.</summary>
    public int? Length { get; init; }

    /// <summary>Gets the disambiguation comment (the canonical-vs-remix signal).</summary>
    public string? Disambiguation { get; init; }

    /// <summary>
    /// Gets a value indicating whether this is a video recording. Search results carry a null here —
    /// only a lookup answers it — so it stays nullable.
    /// </summary>
    public bool? Video { get; init; }

    /// <summary>Gets the first release date (usually year-only).</summary>
    public string? FirstReleaseDate { get; init; }

    /// <summary>Gets the search score; present on search results only.</summary>
    public int? Score { get; init; }

    /// <summary>Gets the artist credit.</summary>
    public IReadOnlyList<MbArtistCredit> ArtistCredit { get; init; } = [];

    /// <summary>Gets the ISRCs; present on a lookup with <c>inc=isrcs</c> only.</summary>
    public IReadOnlyList<string> Isrcs { get; init; } = [];

    /// <summary>Gets the releases the recording appears on.</summary>
    public IReadOnlyList<MbRelease> Releases { get; init; } = [];
}

/// <summary>A release group: "the overall concept of an album", with its types.</summary>
public sealed record MbReleaseGroup
{
    /// <summary>Gets the release group MBID.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the release group title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the primary type: Album, Single, EP, Broadcast or Other.</summary>
    public string? PrimaryType { get; init; }

    /// <summary>Gets the secondary types: Compilation, Live, Remix, Soundtrack, Demo …</summary>
    public IReadOnlyList<string> SecondaryTypes { get; init; } = [];

    /// <summary>Gets the first release date of the group.</summary>
    public string? FirstReleaseDate { get; init; }

    /// <summary>Gets the credited artist, as returned by a release-group search.</summary>
    public IReadOnlyList<MbArtistCredit> ArtistCredit { get; init; } = [];

    /// <summary>Gets the search score, from 0 to 100; a browse does not return one.</summary>
    public int? Score { get; init; }
}

/// <summary>A release: a concrete product a recording appears on.</summary>
public sealed record MbRelease
{
    /// <summary>Gets the release MBID.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the release title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets the release status, for example Official.</summary>
    public string? Status { get; init; }

    /// <summary>Gets the release date.</summary>
    public string? Date { get; init; }

    /// <summary>Gets the release country, as an ISO-3166 code.</summary>
    public string? Country { get; init; }

    /// <summary>Gets the MusicBrainz disambiguation comment.</summary>
    public string? Disambiguation { get; init; }

    /// <summary>Gets the artist credit for the release as a whole.</summary>
    public IReadOnlyList<MbArtistCredit> ArtistCredit { get; init; } = [];

    /// <summary>Gets the release group this release belongs to.</summary>
    public MbReleaseGroup? ReleaseGroup { get; init; }

    /// <summary>Gets the media (discs) of the release.</summary>
    public IReadOnlyList<MbMedium> Media { get; init; } = [];
}

/// <summary>One medium (disc, side, file) of a release.</summary>
public sealed record MbMedium
{
    /// <summary>Gets the medium's position within the release, starting at 1.</summary>
    public int Position { get; init; }

    /// <summary>Gets the medium format, for example <c>CD</c> or <c>12" Vinyl</c>.</summary>
    public string? Format { get; init; }

    /// <summary>Gets the number of tracks the medium carries.</summary>
    public int TrackCount { get; init; }

    /// <summary>Gets the tracks; empty in a release browse, which does not return them.</summary>
    public IReadOnlyList<MbTrack> Tracks { get; init; } = [];
}

/// <summary>A track: how a recording is placed on one medium of one release.</summary>
public sealed record MbTrack
{
    /// <summary>Gets the release track MBID (not the recording MBID).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets the track's position on its medium, starting at 1.</summary>
    public int Position { get; init; }

    /// <summary>Gets the printed track number, for example <c>B4</c>.</summary>
    public string? Number { get; init; }

    /// <summary>Gets the track title, which may differ from the recording title.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the track length in milliseconds.</summary>
    public int? Length { get; init; }

    /// <summary>Gets the track's own artist credit, which may differ from the release's.</summary>
    public IReadOnlyList<MbArtistCredit> ArtistCredit { get; init; } = [];

    /// <summary>Gets the recording the track plays; a release lookup fills in id and title.</summary>
    public MbRecording? Recording { get; init; }
}

/// <summary>The release-group search response envelope.</summary>
public sealed record MbReleaseGroupSearchResult
{
    /// <summary>Gets the total number of matches, not the number returned.</summary>
    public int Count { get; init; }

    /// <summary>Gets the offset the page starts at.</summary>
    public int Offset { get; init; }

    /// <summary>Gets the page of release groups.</summary>
    public IReadOnlyList<MbReleaseGroup> ReleaseGroups { get; init; } = [];
}

/// <summary>The recording search response envelope.</summary>
public sealed record MbRecordingSearchResult
{
    /// <summary>Gets the total number of matches, not the number returned.</summary>
    public int Count { get; init; }

    /// <summary>Gets the offset the page starts at.</summary>
    public int Offset { get; init; }

    /// <summary>Gets the page of recordings.</summary>
    public IReadOnlyList<MbRecording> Recordings { get; init; } = [];
}

/// <summary>The release browse envelope: <c>{ release-count, release-offset, releases }</c>.</summary>
internal sealed record MbReleaseBrowsePage
{
    /// <summary>Gets the total number of releases the recording appears on.</summary>
    public int ReleaseCount { get; init; }

    /// <summary>Gets the offset this page starts at.</summary>
    public int ReleaseOffset { get; init; }

    /// <summary>Gets the page of releases.</summary>
    public IReadOnlyList<MbRelease> Releases { get; init; } = [];
}

/// <summary>The ISRC lookup envelope: <c>{ isrc, recordings }</c>.</summary>
internal sealed record MbIsrcResult
{
    /// <summary>Gets the ISRC that was looked up.</summary>
    public string Isrc { get; init; } = string.Empty;

    /// <summary>Gets the recordings carrying that ISRC.</summary>
    public IReadOnlyList<MbRecording> Recordings { get; init; } = [];
}

/// <summary>One <c>music video</c> relation of a recording: the recording it points to, and which way it points.</summary>
/// <param name="TargetRecordingId">The MBID of the related recording.</param>
/// <param name="TargetIsVideo">Whether the related recording is a video recording (<c>video: true</c>).</param>
/// <param name="Direction">
/// <c>forward</c> when the looked-up recording is the audio and the target its video; <c>backward</c> when the
/// looked-up recording is the video and the target its audio.
/// </param>
public sealed record MbMusicVideoRelation(string TargetRecordingId, bool TargetIsVideo, string Direction)
{
    /// <summary>The direction MusicBrainz uses when the looked-up recording is the audio and the target its video.</summary>
    public const string Forward = "forward";
}

/// <summary>One URL relation of a recording, for example <c>free streaming</c> to a YouTube watch page.</summary>
/// <param name="Type">The relationship type, for example <c>free streaming</c>.</param>
/// <param name="Resource">The URL.</param>
public sealed record MbUrlRelation(string Type, string Resource);

/// <summary>
/// What a recording's relations say, from <c>recording/{mbid}?inc=recording-rels+url-rels</c>: whether it is a
/// video recording, its <c>music video</c> links to other recordings, and its URL links.
/// </summary>
/// <param name="Id">The recording MBID.</param>
/// <param name="Video">Whether the recording is a video recording.</param>
/// <param name="LengthMs">The recording's length in milliseconds, or <see langword="null"/> when MusicBrainz has none.</param>
/// <param name="MusicVideos">The <c>music video</c> relations, in the order MusicBrainz lists them.</param>
/// <param name="Urls">The URL relations, in the order MusicBrainz lists them.</param>
public sealed record MbRecordingRelations(
    string Id,
    bool Video,
    int? LengthMs,
    IReadOnlyList<MbMusicVideoRelation> MusicVideos,
    IReadOnlyList<MbUrlRelation> Urls);

/// <summary>The wire shape of a recording lookup with relations; <see cref="MbRecordingRelations"/> is what callers see.</summary>
internal sealed record MbRecordingRelationsDocument
{
    /// <summary>Gets the recording MBID.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the recording is a video.</summary>
    public bool? Video { get; init; }

    /// <summary>Gets the length in milliseconds.</summary>
    public int? Length { get; init; }

    /// <summary>Gets the relations.</summary>
    public IReadOnlyList<MbRelationDocument> Relations { get; init; } = [];
}

/// <summary>One relation on the wire: a recording link, a URL link or another kind this client ignores.</summary>
internal sealed record MbRelationDocument
{
    /// <summary>Gets the relationship type, for example <c>music video</c> or <c>free streaming</c>.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Gets the target kind: <c>recording</c>, <c>url</c> and so on.</summary>
    public string TargetType { get; init; } = string.Empty;

    /// <summary>Gets the direction: <c>forward</c> or <c>backward</c>.</summary>
    public string Direction { get; init; } = string.Empty;

    /// <summary>Gets the related recording, on a recording link.</summary>
    public MbRecording? Recording { get; init; }

    /// <summary>Gets the URL, on a URL link.</summary>
    public MbUrlDocument? Url { get; init; }
}

/// <summary>The URL of a URL relation, on the wire.</summary>
internal sealed record MbUrlDocument
{
    /// <summary>Gets the URL text.</summary>
    public string Resource { get; init; } = string.Empty;
}
