using Microsoft.AspNetCore.Mvc;
using Wondarr.Core.Searching;

namespace Wondarr.Api.Release;

/// <summary>
/// <c>POST /api/v1/release/push</c>: what autobrr's "push to Lidarr" action calls (ARCHITECTURE §5.6,
/// DECISIONS build session 6 #7, build session 8 #11). The answer keeps Lidarr's shape — one object
/// with <c>approved</c>/<c>rejected</c>/<c>temporarilyRejected</c>/<c>rejections</c>, and a 400 with
/// the validation array when the title is missing — so autobrr understands it. A pushed torrent or NZB
/// is matched against the wanted songs, judged by the engine and grabbed when approved; nothing about
/// the push but its title, protocol and outcome is logged (download URLs can carry tracker passkeys).
/// </summary>
[ApiController]
[Route("api/v1/release/push")]
public sealed class ReleasePushController : ControllerBase
{
    private readonly IReleasePushHandler _handler;

    /// <summary>Initialises a new instance of the <see cref="ReleasePushController"/> class.</summary>
    /// <param name="handler">Decides and grabs the pushed release.</param>
    public ReleasePushController(IReleasePushHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        _handler = handler;
    }

    /// <summary>Decides a pushed release, and grabs it when approved.</summary>
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

        var outcome = await _handler.PushAsync(
            new PushedRelease(
                release.Title,
                protocol,
                release.DownloadUrl,
                release.MagnetUrl,
                release.Size,
                release.Indexer,
                release.PublishDate is { } published ? new DateTimeOffset(DateTime.SpecifyKind(published, DateTimeKind.Utc)) : null),
            cancellationToken).ConfigureAwait(false);

        return Ok(new ReleasePushDecisionResource(
            Approved: outcome.Approved,
            Rejected: !outcome.Approved,
            TemporarilyRejected: false,
            Rejections: outcome.Rejections,
            SongId: outcome.SongId,
            SongTitle: outcome.SongTitle));
    }
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
