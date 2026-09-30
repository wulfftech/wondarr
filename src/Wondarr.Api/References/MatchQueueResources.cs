using System.Text.Json;
using Wondarr.Core.Domain;
using Wondarr.Core.Media;
using Wondarr.Core.References;
using Wondarr.Core.Tagging;

namespace Wondarr.Api.References;

/// <summary>
/// One file the Match queue is asking about (LIBRARY_OUTPUT §7.6): what the file is, what was found for
/// it, and the candidates ranked best first.
/// </summary>
/// <param name="Id">The reference file id.</param>
/// <param name="ReferenceLibraryId">The library the file belongs to.</param>
/// <param name="RelativePath">The path relative to the library root, separated by <c>/</c>.</param>
/// <param name="State"><c>ambiguous</c> or <c>unmatched</c>.</param>
/// <param name="File">What the user needs to decide: the file's tags and what the probe measured.</param>
/// <param name="Candidates">The ranked candidates, best first.</param>
/// <param name="Message">The line the last run left on the row, or <see langword="null"/>.</param>
public sealed record MatchQueueItemResource(
    long Id,
    long ReferenceLibraryId,
    string RelativePath,
    string State,
    MatchFileResource File,
    List<MatchCandidateResource> Candidates,
    string? Message);

/// <summary>
/// What the user needs to decide about one file: what its own tags say, and what the scan measured.
/// Every field is optional — a file with no readable tags, or one whose stored JSON cannot be read,
/// reports nulls rather than failing the page.
/// </summary>
/// <param name="Title">The title tag, or <see langword="null"/>.</param>
/// <param name="Artist">The artist tag, or <see langword="null"/>.</param>
/// <param name="Album">The album tag, or <see langword="null"/>.</param>
/// <param name="TrackNumber">The track number tag, or <see langword="null"/>.</param>
/// <param name="DurationMs">The probed length in milliseconds, or the tagged one, or <see langword="null"/>.</param>
/// <param name="Codec">The probed codec, for example <c>flac</c>, or <see langword="null"/>.</param>
/// <param name="Bitrate">The probed bitrate in kbps, or <see langword="null"/>.</param>
/// <param name="Isrc">The ISRC tag, or <see langword="null"/>.</param>
/// <param name="MbRecordingId">The recording MBID tag, or <see langword="null"/>.</param>
public sealed record MatchFileResource(
    string? Title,
    string? Artist,
    string? Album,
    int? TrackNumber,
    int? DurationMs,
    string? Codec,
    int? Bitrate,
    string? Isrc,
    string? MbRecordingId);

/// <summary>One ranked candidate the user may accept.</summary>
/// <param name="Rank">The rank, 1 being the best.</param>
/// <param name="Score">The score that ranked it.</param>
/// <param name="Reason">Why it scored what it did.</param>
/// <param name="Source">The provider it came from: <c>musicbrainz</c> or <c>deezer</c>.</param>
/// <param name="MbRecordingId">The recording MBID, when known.</param>
/// <param name="DeezerId">The Deezer track id, when known.</param>
/// <param name="Title">The title, including any version suffix.</param>
/// <param name="ArtistCredit">The display credit.</param>
/// <param name="DurationMs">The length the provider knows, or <see langword="null"/>.</param>
/// <param name="AlbumTitle">The album the candidate would be filed under, or <see langword="null"/>.</param>
public sealed record MatchCandidateResource(
    int Rank,
    double Score,
    string Reason,
    string Source,
    string? MbRecordingId,
    long? DeezerId,
    string Title,
    string ArtistCredit,
    int? DurationMs,
    string? AlbumTitle);

/// <summary>How one file's resolve ended.</summary>
/// <param name="State">The state the row is in now: <c>identified</c> or <c>skipped</c>.</param>
/// <param name="SongId">The song the file is, or <see langword="null"/> when it was released or skipped.</param>
/// <param name="Message">The line the row carries, for example the duplicate notice.</param>
public sealed record MatchResolveResource(string State, long? SongId, string? Message);

/// <summary>What accepting the best candidate of many files did.</summary>
/// <param name="Resolved">How many files became owned songs.</param>
/// <param name="Failed">How many could not be accepted.</param>
/// <param name="Errors">One line per failure, <c>"&lt;relativePath&gt;: &lt;reason&gt;"</c>.</param>
public sealed record MatchBulkResource(int Resolved, int Failed, List<string> Errors);

/// <summary>What a caller sends to settle one file: exactly one of the four choices.</summary>
/// <param name="CandidateRank">The rank of the candidate to accept, or <see langword="null"/>.</param>
/// <param name="MbRecordingId">The recording MBID the user named, or <see langword="null"/>.</param>
/// <param name="DeezerId">The Deezer track id the user named, or <see langword="null"/>.</param>
/// <param name="Skip">Whether the user asked Wondarr to leave the file alone.</param>
public sealed record MatchResolveRequestResource(int? CandidateRank, string? MbRecordingId, long? DeezerId, bool Skip);

/// <summary>What a caller sends to accept the best candidate of many files at once.</summary>
/// <param name="Ids">The reference file ids to accept.</param>
public sealed record MatchBulkRequestResource(List<long>? Ids);

/// <summary>Turns stored reference files into the Match queue's wire shapes.</summary>
public static class MatchQueueResourceMapper
{
    /// <summary>How much of a page the queue asks the file's own JSON for at most.</summary>
    private static readonly JsonSerializerOptions StoredJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Builds the queue item for one file and its candidates.</summary>
    /// <param name="row">The reference file's row.</param>
    /// <returns>The wire resource.</returns>
    public static MatchQueueItemResource ToResource(this ReferenceFile row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var tags = Read<FileTags>(row.Tags);
        var probe = Read<MediaInfo>(row.Probe);

        return new MatchQueueItemResource(
            row.Id,
            row.ReferenceLibraryId,
            row.RelativePath,
            WireName(row.State),
            new MatchFileResource(
                tags?.Title,
                tags?.Artist,
                tags?.Album,
                tags?.TrackNumber,
                probe?.DurationMs ?? tags?.DurationMs,
                probe?.Codec,
                probe?.BitrateKbps,
                tags?.Isrc,
                tags?.MbRecordingId),
            [.. row.Candidates.OrderBy(candidate => candidate.Rank).Select(ToResource)],
            row.Message);
    }

    /// <summary>The wire name of a state, for example <c>ambiguous</c>.</summary>
    /// <param name="state">The state.</param>
    /// <returns>The camelCase name.</returns>
    public static string WireName(ReferenceFileState state) =>
        JsonNamingPolicy.CamelCase.ConvertName(state.ToString());

    /// <summary>Builds the resource for one ranked candidate, whose identity is JSON.</summary>
    /// <param name="candidate">The stored candidate.</param>
    /// <returns>The wire resource; an unreadable identity reports empty names rather than failing.</returns>
    public static MatchCandidateResource ToResource(this MatchCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var identity = Read<MatchIdentity>(candidate.Identity);

        return new MatchCandidateResource(
            candidate.Rank,
            candidate.Score,
            candidate.Reason,
            identity?.Source ?? string.Empty,
            identity?.MbRecordingId,
            identity?.DeezerId,
            identity?.Title ?? string.Empty,
            identity?.ArtistCredit ?? string.Empty,
            identity?.DurationMs,
            identity?.AlbumTitle);
    }

    /// <summary>Reads one of the scan's stored JSON blobs; anything unreadable is "nothing is known".</summary>
    private static T? Read<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, StoredJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
