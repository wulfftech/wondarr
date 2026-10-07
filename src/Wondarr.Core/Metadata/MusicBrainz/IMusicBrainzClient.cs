namespace Wondarr.Core.Metadata.MusicBrainz;

/// <summary>
/// Reads recordings, ISRCs and releases from MusicBrainz WS/2. Every response is cached, so repeat
/// lookups cost nothing.
/// </summary>
public interface IMusicBrainzClient
{
    /// <summary>Searches for recordings with a Lucene query.</summary>
    /// <param name="luceneQuery">A query built with <see cref="MusicBrainzQuery"/> or by hand.</param>
    /// <param name="limit">How many results to ask for; MusicBrainz serves 1 to 100.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<MbRecordingSearchResult> SearchRecordingsAsync(
        string luceneQuery,
        int limit = 25,
        CancellationToken cancellationToken = default);

    /// <summary>Looks a recording up by MBID.</summary>
    /// <param name="recordingId">The recording MBID, in either case.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The recording, or <see langword="null"/> when MusicBrainz does not know the id.</returns>
    Task<MbRecording?> GetRecordingAsync(string recordingId, CancellationToken cancellationToken = default);

    /// <summary>Looks up every recording carrying an ISRC.</summary>
    /// <param name="isrc">The 12-character ISRC, in either case.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The recordings, empty when MusicBrainz does not know the ISRC.</returns>
    Task<IReadOnlyList<MbRecording>> GetRecordingsByIsrcAsync(string isrc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Browses every official release a recording appears on. A recording lookup caps
    /// <c>inc=releases</c> at 25 releases, so the complete list comes from the release browse.
    /// </summary>
    /// <param name="recordingId">The recording MBID.</param>
    /// <param name="maxPages">How many 100-release pages to fetch at most.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<IReadOnlyList<MbRelease>> GetReleasesForRecordingAsync(
        string recordingId,
        int maxPages = 3,
        CancellationToken cancellationToken = default);

    /// <summary>Looks a release up by MBID, with its tracklist.</summary>
    /// <param name="releaseId">The release MBID, in either case.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The release, or <see langword="null"/> when MusicBrainz does not know the id.</returns>
    Task<MbRelease?> GetReleaseAsync(string releaseId, CancellationToken cancellationToken = default);

    /// <summary>Searches for release groups with a Lucene query.</summary>
    /// <param name="luceneQuery">A query built with <see cref="MusicBrainzQuery"/> or by hand.</param>
    /// <param name="limit">How many results to ask for; MusicBrainz serves 1 to 100.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<MbReleaseGroupSearchResult> SearchReleaseGroupsAsync(
        string luceneQuery,
        int limit = 25,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Browses every official release of a release group, with its media. A browse does not return
    /// tracks; the tracklist comes from <see cref="GetReleaseAsync"/>.
    /// </summary>
    /// <param name="releaseGroupId">The release-group MBID, in either case.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<IReadOnlyList<MbRelease>> GetReleasesForReleaseGroupAsync(
        string releaseGroupId,
        CancellationToken cancellationToken = default);
}
