using System.Text.Json;
using System.Text.Json.Serialization;
using Wondarr.Core.Decisions;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata;
using Wondarr.Core.Sources;

namespace Wondarr.Api.Release;

/// <summary>
/// One interactive-search answer: the run that was recorded, how it ended and every candidate the
/// decision engine saw. The releases are in the engine's order — accepted first, best first — so the
/// UI can render them top-down without sorting.
/// </summary>
/// <param name="SearchRunId">The run that was recorded, or 0 when no run was opened.</param>
/// <param name="Outcome">How the run ended, as a camel-case string.</param>
/// <param name="Message">Why nothing was accepted or why no source answered, or <see langword="null"/>.</param>
/// <param name="Releases">Every candidate the run saw, in the engine's order.</param>
public sealed record InteractiveSearchResource(
    long SearchRunId,
    SearchOutcome Outcome,
    string? Message,
    IReadOnlyList<ReleaseResource> Releases);

/// <summary>
/// One candidate of an interactive search: everything the UI shows and the id the manual grab needs.
/// The score and the parse are the engine's own objects, read back from the run's stored evidence.
/// </summary>
/// <param name="CandidateId">The stored candidate record's id — the handle <c>POST /api/v1/release</c> takes.</param>
/// <param name="SourceType">One of <c>soulseek</c>, <c>youtube</c>, <c>torznab</c> or <c>newznab</c>.</param>
/// <param name="Provider">The Soulseek username, YouTube channel or indexer, or <see langword="null"/>.</param>
/// <param name="DisplayName">What the source calls the file.</param>
/// <param name="RemotePath">The source's own path or id for the file.</param>
/// <param name="Extension">The file's extension, or <see langword="null"/> when the source gave none.</param>
/// <param name="QualityId">The inferred quality id.</param>
/// <param name="QualityName">The quality's display name, or <see langword="null"/> when the id is unknown.</param>
/// <param name="SizeBytes">The size in bytes, or <see langword="null"/> when the source did not report one.</param>
/// <param name="DurationMs">The duration in milliseconds, or <see langword="null"/>.</param>
/// <param name="BitrateKbps">The bit rate in kbps, or <see langword="null"/>.</param>
/// <param name="SampleRate">The sample rate in Hz, or <see langword="null"/>.</param>
/// <param name="BitDepth">The bit depth, or <see langword="null"/>.</param>
/// <param name="FreeUploadSlot">Soulseek: whether the peer has a free upload slot, or <see langword="null"/>.</param>
/// <param name="QueueLength">Soulseek: the peer's upload queue length, or <see langword="null"/>.</param>
/// <param name="UploadSpeed">Soulseek: the peer's advertised upload speed in bytes per second, or <see langword="null"/>.</param>
/// <param name="Score">The 0–1000 total the engine gave the candidate.</param>
/// <param name="ScoreBreakdown">The components and named adjustments behind <paramref name="Score"/>.</param>
/// <param name="Rejections">Every rule the candidate failed; empty when the engine accepted it.</param>
/// <param name="Accepted">Whether the engine would grab this candidate on its own.</param>
/// <param name="Parsed">What the filename parser made of the candidate.</param>
/// <param name="Query">The query the candidate answered, or <see langword="null"/>.</param>
public sealed record ReleaseResource(
    long CandidateId,
    string SourceType,
    string? Provider,
    string DisplayName,
    string RemotePath,
    string? Extension,
    long QualityId,
    string? QualityName,
    long? SizeBytes,
    int? DurationMs,
    int? BitrateKbps,
    int? SampleRate,
    int? BitDepth,
    bool? FreeUploadSlot,
    int? QueueLength,
    long? UploadSpeed,
    int Score,
    ScoreBreakdown ScoreBreakdown,
    IReadOnlyList<ReleaseRejectionResource> Rejections,
    bool Accepted,
    ReleaseParseResource Parsed,
    string? Query);

/// <summary>One reason a candidate was rejected, as the run stored it.</summary>
/// <param name="Reason">The rule that fired, as a camel-case wire name.</param>
/// <param name="Message">The human-readable explanation.</param>
public sealed record ReleaseRejectionResource(string Reason, string Message);

/// <summary>What the filename parser made of a candidate (MATCHING_ENGINE §6.1).</summary>
/// <param name="Artist">The artist the filename names, or <see langword="null"/>.</param>
/// <param name="Title">The title the filename names, or <see langword="null"/>.</param>
/// <param name="Album">The album the filename names, or <see langword="null"/>.</param>
/// <param name="TrackNo">The track number the filename names, or <see langword="null"/>.</param>
/// <param name="Flags">The version flags read from the filename or the tags.</param>
/// <param name="PathFlags">The version flags read from the path alone.</param>
public sealed record ReleaseParseResource(
    string? Artist,
    string? Title,
    string? Album,
    int? TrackNo,
    IReadOnlyList<string> Flags,
    IReadOnlyList<string> PathFlags);

/// <summary>The manual grab's request body.</summary>
/// <param name="CandidateId">The stored candidate to grab, as the search returned it.</param>
public sealed record GrabRequestResource(long CandidateId);

/// <summary>What a successful manual grab answers with.</summary>
/// <param name="QueueItemId">The queue item the grab created.</param>
public sealed record GrabResource(long QueueItemId);

/// <summary>
/// Reads the run's stored evidence back into release resources. The candidate's normalised JSON and
/// the score breakdown were written with the search service's own camelCase, string-enum shape, so
/// the same options read them back exactly as they were judged.
/// </summary>
public static class ReleaseResourceExtensions
{
    /// <summary>The JSON shape the run's evidence columns were written in.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>
    /// Shapes one stored candidate. <paramref name="qualityNames"/> is the id → name lookup the caller
    /// read once per request.
    /// </summary>
    /// <param name="record">The candidate record the run stored.</param>
    /// <param name="qualityNames">Quality id → display name.</param>
    public static ReleaseResource ToResource(this CandidateRecord record, IReadOnlyDictionary<long, string> qualityNames)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(qualityNames);

        var candidate = JsonSerializer.Deserialize<Candidate>(record.Normalised, Json) ?? new Candidate
        {
            SourceType = record.SourceType,
            BlocklistKey = record.BlocklistKey,
            DisplayName = record.DisplayName,
            RemotePath = record.RemotePath,
            QualityId = record.QualityId,
        };

        var breakdown = JsonSerializer.Deserialize<ScoreBreakdown>(record.ScoreBreakdown, Json)
            ?? new ScoreBreakdown(0, 0, 0, 0, 0, 0, 0, [], 0, record.Score, false);

        var rejections = JsonSerializer.Deserialize<List<ReleaseRejectionResource>>(record.Rejections, Json) ?? [];

        return new ReleaseResource(
            record.Id,
            record.SourceType,
            record.Provider,
            record.DisplayName,
            record.RemotePath,
            candidate.Extension,
            record.QualityId,
            qualityNames.TryGetValue(record.QualityId, out var name) ? name : null,
            record.SizeBytes,
            record.DurationMs,
            candidate.BitrateKbps,
            candidate.SampleRate,
            candidate.BitDepth,
            candidate.Availability.FreeUploadSlot,
            candidate.Availability.QueueLength,
            candidate.Availability.UploadSpeedBytesPerSecond,
            record.Score,
            breakdown,
            rejections,
            record.Accepted,
            new ReleaseParseResource(
                candidate.Parsed.Artist,
                candidate.Parsed.Title,
                candidate.Parsed.Album,
                candidate.Parsed.TrackNo,
                VersionFlagNames.ToWireNames(candidate.Parsed.VersionFlags),
                VersionFlagNames.ToWireNames(candidate.Parsed.PathVersionFlags)),
            candidate.Query);
    }
}
