using System.Text.RegularExpressions;
using Wondarr.Core.Domain;
using Wondarr.Core.Identity;
using Wondarr.Core.Paging;
using Wondarr.Core.Wanted;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.Release;

/// <summary>
/// <c>POST /api/v1/release/push</c>: what autobrr's "push to Lidarr" action calls (ARCHITECTURE §5.6,
/// DECISIONS build session 6 #7). The answer keeps Lidarr's shape — one object with
/// <c>approved</c>/<c>rejected</c>/<c>temporarilyRejected</c>/<c>rejections</c>, and a 400 with the
/// validation array when the title is missing — so autobrr understands it. Until a source can take a
/// pushed torrent or NZB (Phase 7), every push is rejected with the reason; nothing is grabbed or stored,
/// and nothing about the push but its title, protocol and outcome is logged (download URLs can carry
/// tracker passkeys).
/// </summary>
[ApiController]
[Route("api/v1/release/push")]
public sealed partial class ReleasePushController : ControllerBase
{
    private readonly IWantedService _wanted;
    private readonly ILogger<ReleasePushController> _logger;

    /// <summary>Initialises a new instance of the <see cref="ReleasePushController"/> class.</summary>
    /// <param name="wanted">The wanted list a push is matched against.</param>
    /// <param name="logger">The logger.</param>
    public ReleasePushController(IWantedService wanted, ILogger<ReleasePushController> logger)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(logger);

        _wanted = wanted;
        _logger = logger;
    }

    /// <summary>Decides a pushed release.</summary>
    /// <param name="release">The release, as autobrr sends it.</param>
    /// <param name="cancellationToken">Cancels the decision.</param>
    [HttpPost]
    [Consumes("application/json")]
    [Produces("application/json")]
    [ProducesResponseType<ReleasePushDecisionResource>(StatusCodes.Status200OK)]
    [ProducesResponseType<IReadOnlyList<ReleasePushValidationFailure>>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Push([FromBody] ReleasePushResource? release, CancellationToken cancellationToken)
    {
        if (release is null || string.IsNullOrWhiteSpace(release.Title))
        {
            // autobrr decodes a 400 as this array, not as problem details.
            return BadRequest(new[]
            {
                new ReleasePushValidationFailure("Title", "Title is required", "NotEmptyValidator", release?.Title, "error"),
            });
        }

        var protocol = string.IsNullOrWhiteSpace(release.Protocol)
            ? (release.DownloadProtocol ?? "unknown")
            : release.Protocol;
        var song = await MatchAsync(release.Title, cancellationToken).ConfigureAwait(false);

        var rejections = new List<string>();

        if (song is null)
        {
            rejections.Add($"No wanted song matches '{release.Title}'");
        }

        rejections.Add($"No download client for protocol '{protocol}' (torrent and usenet sources arrive in a later release)");

        LogPushed(_logger, release.Title, protocol, song?.Id);

        return Ok(new ReleasePushDecisionResource(
            Approved: false,
            Rejected: true,
            TemporarilyRejected: false,
            Rejections: rejections,
            SongId: song?.Id,
            SongTitle: song?.Title));
    }

    /// <summary>
    /// The wanted song (missing, or below its cutoff) whose main artist and title the release name
    /// carries as <c>Artist - Title</c>, compared the way the identity matcher compares them.
    /// </summary>
    private async Task<Song?> MatchAsync(string title, CancellationToken cancellationToken)
    {
        var dash = title.IndexOf(" - ", StringComparison.Ordinal);

        if (dash <= 0)
        {
            return null;
        }

        var artist = TextMatching.NormalizeArtist(title[..dash]);
        var name = TextMatching.Normalize(Bracketed().Replace(title[(dash + 3)..], " "));

        if (artist.Length == 0 || name.Length == 0)
        {
            return null;
        }

        var paging = new PagingSpec(1, PagingSpec.MaxPageSize, null, descending: false);
        var missing = await _wanted.GetMissingAsync(paging, cancellationToken).ConfigureAwait(false);
        var cutoff = await _wanted.GetCutoffUnmetAsync(paging, cancellationToken).ConfigureAwait(false);

        return missing.Records
            .Concat(cutoff.Records)
            .FirstOrDefault(song =>
                TextMatching.Normalize(song.Title) == name
                && TextMatching.NormalizeArtist(song.PrimaryArtist?.Name ?? song.ArtistCredit) == artist);
    }

    /// <summary>Release-name decorations: <c>[FLAC]</c>, <c>(320)</c>, <c>{WEB}</c>.</summary>
    [GeneratedRegex(@"[\[\(\{][^\]\)\}]*[\]\)\}]")]
    private static partial Regex Bracketed();

    [LoggerMessage(Level = LogLevel.Information, Message = "Release push '{Title}' ({Protocol}) rejected; wanted song {SongId}")]
    private static partial void LogPushed(ILogger logger, string title, string protocol, long? songId);
}

/// <summary>A pushed release, as autobrr's Lidarr action sends it (unknown fields are ignored).</summary>
/// <param name="Title">The release name.</param>
/// <param name="InfoUrl">The indexer's page for it.</param>
/// <param name="DownloadUrl">The .torrent or .nzb URL (never logged: it can carry a passkey).</param>
/// <param name="MagnetUrl">The magnet link.</param>
/// <param name="Size">The size in bytes.</param>
/// <param name="Indexer">The indexer's name.</param>
/// <param name="DownloadProtocol">The protocol (Lidarr's older field).</param>
/// <param name="Protocol">The protocol: <c>torrent</c> or <c>usenet</c>.</param>
/// <param name="PublishDate">When the release was published.</param>
/// <param name="DownloadClientId">The download client the push asks for.</param>
/// <param name="DownloadClient">The download client by name.</param>
public sealed record ReleasePushResource(
    string? Title,
    string? InfoUrl,
    string? DownloadUrl,
    string? MagnetUrl,
    long? Size,
    string? Indexer,
    string? DownloadProtocol,
    string? Protocol,
    DateTime? PublishDate,
    int? DownloadClientId,
    string? DownloadClient);

/// <summary>The decision on a pushed release, in Lidarr's shape plus the matched song.</summary>
/// <param name="Approved">Whether it would be grabbed.</param>
/// <param name="Rejected">Whether it was refused.</param>
/// <param name="TemporarilyRejected">Whether it was refused for now only.</param>
/// <param name="Rejections">Why, one sentence per reason.</param>
/// <param name="SongId">The wanted song it matched, if any.</param>
/// <param name="SongTitle">That song's title.</param>
public sealed record ReleasePushDecisionResource(
    bool Approved,
    bool Rejected,
    bool TemporarilyRejected,
    IReadOnlyList<string> Rejections,
    long? SongId,
    string? SongTitle);

/// <summary>One validation failure, in the array shape autobrr reads from a 400.</summary>
/// <param name="PropertyName">The field.</param>
/// <param name="ErrorMessage">What is wrong with it.</param>
/// <param name="ErrorCode">The validator's code.</param>
/// <param name="AttemptedValue">The value sent.</param>
/// <param name="Severity"><c>error</c>.</param>
public sealed record ReleasePushValidationFailure(
    string PropertyName,
    string ErrorMessage,
    string ErrorCode,
    string? AttemptedValue,
    string Severity);
