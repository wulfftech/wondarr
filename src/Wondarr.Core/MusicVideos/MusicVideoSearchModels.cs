using Wondarr.Core.Domain;

namespace Wondarr.Core.MusicVideos;

/// <summary>What kind of upload a music video candidate is, as far as the source says.</summary>
public enum MusicVideoCandidateKind
{
    /// <summary>The source does not say. A video MusicBrainz links to a recording is one: MusicBrainz does not classify it.</summary>
    Unknown,

    /// <summary>YouTube Music's own label for an official music video.</summary>
    OfficialVideo,

    /// <summary>An Art Track: the song's audio over a static picture. Not a music video.</summary>
    ArtTrack,

    /// <summary>A user upload, and everything unlisted.</summary>
    UserUpload,

    /// <summary>
    /// An upload whose real YouTube title says it is the audio or a lyric video ("(Official Audio)", "// Lyrics"), even
    /// when YouTube Music labels it an official video. Not a music video.
    /// </summary>
    AudioOnly,
}

/// <summary>What the finder needs to know about a song.</summary>
/// <param name="Title">The song's title.</param>
/// <param name="Artists">The song's credited artists, main first.</param>
/// <param name="MbRecordingId">The song's MusicBrainz recording id, or <see langword="null"/> when it has none.</param>
/// <param name="DurationMs">The song's length in milliseconds, or <see langword="null"/> when unknown.</param>
/// <param name="Blocklisted">
/// The YouTube video ids rejected for this song (<c>music_video_blocklist</c>); the finder never returns one.
/// </param>
public sealed record MusicVideoQuery(
    string Title,
    IReadOnlyList<string> Artists,
    string? MbRecordingId,
    int? DurationMs,
    IReadOnlySet<string> Blocklisted);

/// <summary>One YouTube video that may be the song's music video.</summary>
/// <param name="VideoId">The 11-character YouTube video id.</param>
/// <param name="Title">The title the source shows, or the song's title for a video MusicBrainz links.</param>
/// <param name="Artists">The artists the source credits; empty when it names none.</param>
/// <param name="Kind">What the source says the upload is.</param>
/// <param name="DurationMs">The length the source shows, in milliseconds, or <see langword="null"/>.</param>
/// <param name="ThumbnailUrl">YouTube's thumbnail for the video.</param>
/// <param name="Confident">
/// Whether Wondarr may download it without asking: MusicBrainz links it to the song's recording, or it is an
/// official video of one of the song's artists titled as the song, uploaded by the artist's own channel and not audio
/// only. Anything less is left to the user.
/// </param>
/// <param name="FoundBy">Where the candidate came from.</param>
/// <param name="Score">The ranking score: higher is better. Only the order is meant to matter.</param>
/// <param name="ArtistNamed">
/// Whether one of the song's artists is credited on the video or named in its title ("Artist - Title"). A user upload
/// can be artist-named and still never confident.
/// </param>
/// <param name="TitledAsSong">
/// Whether its title is the song's: at or above the decision engine's title floor, in the studio version, and not a
/// lyric video. A video MusicBrainz links is curated, so it is. Without it a candidate may be another song of the artist.
/// </param>
public sealed record MusicVideoCandidate(
    string VideoId,
    string Title,
    IReadOnlyList<string> Artists,
    MusicVideoCandidateKind Kind,
    int? DurationMs,
    string ThumbnailUrl,
    bool Confident,
    MusicVideoFoundBy FoundBy,
    int Score,
    bool ArtistNamed = false,
    bool TitledAsSong = false)
{
    /// <summary>The thumbnail URL YouTube serves for a video id.</summary>
    /// <param name="videoId">The video id.</param>
    public static string ThumbnailFor(string videoId) => $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";
}

/// <summary>
/// What a finder found: the candidates (this is the list), and whether MusicBrainz failed to answer, so the list may
/// lack the video it links. A partial result is never taken for "this song has no video".
/// </summary>
public sealed class MusicVideoFindResult : IReadOnlyList<MusicVideoCandidate>
{
    private readonly IReadOnlyList<MusicVideoCandidate> _candidates;

    /// <summary>Initialises a new instance of the <see cref="MusicVideoFindResult"/> class.</summary>
    /// <param name="candidates">The candidates, best first.</param>
    /// <param name="partial">Whether MusicBrainz failed to answer.</param>
    public MusicVideoFindResult(IReadOnlyList<MusicVideoCandidate> candidates, bool partial = false)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        _candidates = candidates;
        Partial = partial;
    }

    /// <summary>Gets a value indicating whether MusicBrainz failed to answer.</summary>
    public bool Partial { get; }

    /// <inheritdoc />
    public int Count => _candidates.Count;

    /// <inheritdoc />
    public MusicVideoCandidate this[int index] => _candidates[index];

    /// <inheritdoc />
    public IEnumerator<MusicVideoCandidate> GetEnumerator() => _candidates.GetEnumerator();

    /// <inheritdoc />
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Finds the YouTube videos that may be a song's music video, ranked best first: a video MusicBrainz links
/// comes before a confident search result, and that before the rest. A blocklisted id is never returned.
/// </summary>
public interface IMusicVideoFinder
{
    /// <summary>Finds and ranks the candidates for a song.</summary>
    /// <param name="query">The song.</param>
    /// <param name="cancellationToken">Cancels the lookups.</param>
    Task<MusicVideoFindResult> FindAsync(MusicVideoQuery query, CancellationToken cancellationToken);
}

/// <summary>A video downloaded into a work folder, not yet checked or filed.</summary>
/// <param name="VideoPath">The merged <c>.mkv</c>.</param>
/// <param name="ThumbnailPath">The thumbnail converted to <c>.jpg</c>, or <see langword="null"/> when there is none.</param>
/// <param name="ReportedDurationMs">The length the downloader reported for the video, or <see langword="null"/> when it did not.</param>
public sealed record MusicVideoDownload(string VideoPath, string? ThumbnailPath, int? ReportedDurationMs);

/// <summary>Downloads a YouTube video as an MKV into a work folder of its own.</summary>
public interface IMusicVideoDownloader
{
    /// <summary>Downloads the video, merged to MKV, with its thumbnail as <c>.jpg</c>.</summary>
    /// <param name="videoId">The YouTube video id.</param>
    /// <param name="maxHeight">The tallest picture to take, or <see langword="null"/> for no cap.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <param name="started">
    /// Called once the download really begins, after it has waited its turn for the shared yt-dlp gate; until then the
    /// video is only queued. May be <see langword="null"/>.
    /// </param>
    /// <returns>The files, in a work folder that <see cref="Discard"/> removes.</returns>
    /// <exception cref="Exception">The download failed; the message says why and holds no secret.</exception>
    Task<MusicVideoDownload> DownloadAsync(string videoId, int? maxHeight, CancellationToken cancellationToken, Func<Task>? started = null);

    /// <summary>Deletes the work folder of a download and everything left in it. Never throws.</summary>
    /// <param name="download">What <see cref="DownloadAsync"/> returned.</param>
    void Discard(MusicVideoDownload download);
}

/// <summary>The finder to use when no YouTube source is registered: it finds nothing.</summary>
internal sealed class NoMusicVideoFinder : IMusicVideoFinder
{
    /// <inheritdoc />
    public Task<MusicVideoFindResult> FindAsync(MusicVideoQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(new MusicVideoFindResult([]));
}

/// <summary>The downloader to use when no YouTube source is registered: it refuses.</summary>
internal sealed class NoMusicVideoDownloader : IMusicVideoDownloader
{
    /// <inheritdoc />
    public Task<MusicVideoDownload> DownloadAsync(string videoId, int? maxHeight, CancellationToken cancellationToken, Func<Task>? started = null) =>
        throw new InvalidOperationException("No YouTube source is registered, so a music video cannot be downloaded.");

    /// <inheritdoc />
    public void Discard(MusicVideoDownload download)
    {
    }
}
