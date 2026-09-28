using Wondarr.Core.Domain;
using Wondarr.Core.Profiles;
using Microsoft.AspNetCore.Mvc;

namespace Wondarr.Api.Profiles;

/// <summary>
/// The seeded quality ladder, read-only: the ids are a public contract (profiles, files and history
/// rows store them), so nothing here creates or edits one. The shape follows Lidarr's
/// <c>QualityDefinitionController</c> (<c>src/Lidarr.Api.V1/Profiles/Qualities/QualityDefinitionController.cs</c>)
/// and <c>GET /api/v1/qualitydefinition</c> is what the profile editor in P1-12 reads.
/// </summary>
[ApiController]
[Route("api/v1/qualitydefinition")]
public sealed class QualityDefinitionController : ControllerBase
{
    private readonly IQualityDefinitionService _qualities;

    /// <summary>Initialises a new instance of the <see cref="QualityDefinitionController"/> class.</summary>
    /// <param name="qualities">The quality ladder.</param>
    public QualityDefinitionController(IQualityDefinitionService qualities)
    {
        ArgumentNullException.ThrowIfNull(qualities);

        _qualities = qualities;
    }

    /// <summary>Lists every quality, ordered by id.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    [HttpGet]
    [Produces("application/json")]
    public async Task<ActionResult<List<QualityDefinitionResource>>> GetQualities(CancellationToken cancellationToken)
    {
        var qualities = await _qualities.GetAllAsync(cancellationToken).ConfigureAwait(false);

        return Ok(qualities.Select(QualityDefinitionResource.From).ToList());
    }
}

/// <summary>One rung of the quality ladder.</summary>
/// <param name="Id">The <c>quality</c> row id.</param>
/// <param name="Name">The display name, for example <c>MP3-320</c>.</param>
/// <param name="Group">The group the quality is shown under, for example "High lossy".</param>
/// <param name="Rank">The group rank, 1 (Unknown) to 9 (Uncompressed).</param>
/// <param name="Codec">The codec family.</param>
/// <param name="Lossless">Whether the codec is lossless.</param>
/// <param name="MinBitrate">The lowest bitrate the quality covers, or <see langword="null"/> when open.</param>
/// <param name="MaxBitrate">The highest bitrate the quality covers, or <see langword="null"/> when open.</param>
/// <param name="BitDepth">The bit depth the quality requires, or <see langword="null"/> when it does not constrain one.</param>
public sealed record QualityDefinitionResource(
    long Id,
    string Name,
    string Group,
    int Rank,
    string Codec,
    bool Lossless,
    int? MinBitrate,
    int? MaxBitrate,
    int? BitDepth)
{
    /// <summary>Builds the resource for a stored <see cref="Quality"/>.</summary>
    /// <param name="quality">The stored quality.</param>
    public static QualityDefinitionResource From(Quality quality)
    {
        ArgumentNullException.ThrowIfNull(quality);

        return new QualityDefinitionResource(
            quality.Id,
            quality.Name,
            quality.Group,
            quality.Rank,
            quality.Codec,
            quality.Lossless,
            quality.MinBitrate,
            quality.MaxBitrate,
            quality.BitDepth);
    }
}
