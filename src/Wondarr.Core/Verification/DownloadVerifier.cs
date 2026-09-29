using System.Globalization;
using Wondarr.Core.Identity;
using Wondarr.Core.Media;
using Wondarr.Core.Metadata;
using Wondarr.Core.Metadata.AcoustId;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wondarr.Core.Verification;

/// <summary>What the import pipeline should do with a downloaded file (MATCHING_ENGINE.md §6.5 step 4).</summary>
public enum VerificationOutcome
{
    /// <summary>Import it.</summary>
    Passed,

    /// <summary>Import it with a low-confidence badge, or not, depending on the profile.</summary>
    NeedsReview,

    /// <summary>Reject it and try the next candidate.</summary>
    Failed,

    /// <summary>The verdict could not be reached — AcoustID is down or throttling; retry later.</summary>
    Deferred,
}

/// <summary>Everything verification needs to know about the file and the song it is supposed to be.</summary>
/// <param name="Path">The downloaded file to verify.</param>
/// <param name="SongMbRecordingId">The MusicBrainz recording the song wants, or <c>null</c> for a Deezer-only song.</param>
/// <param name="SongTitle">The song's title, as the user's source gave it.</param>
/// <param name="MainArtists">The song's main artists.</param>
/// <param name="SongDurationMs">The song's known length in milliseconds, or <c>null</c> when unknown.</param>
/// <param name="SongFlags">The version flags the song carries.</param>
/// <param name="DurationToleranceMs">How far the file may be from the song's length before it is the wrong file.</param>
public sealed record VerificationRequest(
    string Path,
    string? SongMbRecordingId,
    string SongTitle,
    IReadOnlyList<string> MainArtists,
    int? SongDurationMs,
    VersionFlags SongFlags,
    int DurationToleranceMs);

/// <summary>The verdict on one downloaded file, with the reason the history shows.</summary>
/// <param name="Outcome">What to do with the file.</param>
/// <param name="Reason">A human-readable line for the history; never a key or a fingerprint.</param>
/// <param name="Media">What ffprobe measured, or <c>null</c> when the file does not decode.</param>
/// <param name="MeasuredQualityId">The quality the file actually is, or <c>null</c> when it does not decode.</param>
/// <param name="AcoustId">The best matching AcoustID, or <c>null</c>.</param>
/// <param name="FingerprintScore">That AcoustID's score, or <c>null</c>.</param>
/// <param name="MatchedRecordingId">The recording the song's own MBID was confirmed against, or <c>null</c>.</param>
/// <param name="LearnedMbRecordingId">The recording a Deezer-only song learned its MBID from, or <c>null</c>.</param>
/// <param name="FingerprintVerified">Whether AcoustID recognised the file as the wanted recording.</param>
public sealed record VerificationResult(
    VerificationOutcome Outcome,
    string Reason,
    MediaInfo? Media,
    long? MeasuredQualityId,
    string? AcoustId,
    double? FingerprintScore,
    string? MatchedRecordingId,
    string? LearnedMbRecordingId,
    bool FingerprintVerified);

/// <summary>Decides whether a downloaded file is the wanted recording.</summary>
public interface IDownloadVerifier
{
    /// <summary>Verifies one downloaded file.</summary>
    /// <param name="request">The file and the song it is supposed to be.</param>
    /// <param name="cancellationToken">Cancels the probe, the fingerprints and the lookups.</param>
    /// <returns>The verdict and the reason for it.</returns>
    Task<VerificationResult> VerifyAsync(VerificationRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The verification step of the import pipeline (MATCHING_ENGINE.md §6.5): probe the file, check its
/// length, fingerprint it and ask AcoustID what it is. Every branch stops at the first failure and
/// explains itself, because the reason is what the history and the UI show.
/// </summary>
/// <remarks>
/// An AcoustID outage defers the file rather than importing it unverified: "we could not ask" is not
/// evidence that the file is right. A file AcoustID simply does not know is a different thing, and is
/// imported on probe and duration alone with <see cref="VerificationResult.FingerprintVerified"/> false.
/// </remarks>
public sealed partial class DownloadVerifier : IDownloadVerifier
{
    private readonly IMediaProbe _probe;
    private readonly IFingerprinter _fingerprinter;
    private readonly IAcoustIdClient _client;
    private readonly IOptionsMonitor<AcoustIdOptions> _options;
    private readonly ILogger<DownloadVerifier> _logger;

    /// <summary>Initialises a new instance of the <see cref="DownloadVerifier"/> class.</summary>
    /// <param name="probe">Measures the file and checks that it decodes.</param>
    /// <param name="fingerprinter">Produces the Chromaprint fingerprints.</param>
    /// <param name="client">Answers what recording a fingerprint belongs to.</param>
    /// <param name="options">The score thresholds and the strict switch.</param>
    /// <param name="logger">Logs one line per file at Information.</param>
    public DownloadVerifier(
        IMediaProbe probe,
        IFingerprinter fingerprinter,
        IAcoustIdClient client,
        IOptionsMonitor<AcoustIdOptions> options,
        ILogger<DownloadVerifier> logger)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(fingerprinter);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _probe = probe;
        _fingerprinter = fingerprinter;
        _client = client;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<VerificationResult> VerifyAsync(
        VerificationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var probed = await _probe.ProbeAsync(request.Path, cancellationToken).ConfigureAwait(false);

        // 1. It has to decode: real format and real bitrate are what the import runs on.
        if (!probed.Decodable || probed.Info is null)
        {
            return Failed($"Not decodable: {probed.Error ?? "the file has no audio stream"}");
        }

        var media = probed.Info;
        var quality = MeasuredQuality.FromMediaInfo(media);

        // 2. It has to be as long as the song (a YouTube rip of a video with an intro fails here).
        if (request.SongDurationMs is { } songMs
            && media.DurationMs > 0
            && Math.Abs(media.DurationMs - songMs) > request.DurationToleranceMs)
        {
            var difference = Math.Abs(media.DurationMs - songMs);

            return Failed(
                $"Duration {Seconds(media.DurationMs)} s is {Seconds(difference)} s off the song's "
                    + $"{Seconds(songMs)} s (tolerance {Seconds(request.DurationToleranceMs)} s)",
                media,
                quality);
        }

        // 3. Fingerprint the start of the file.
        var fingerprint = await _fingerprinter
            .FingerprintAsync(request.Path, FingerprintWindow.Start, media.DurationMs, cancellationToken)
            .ConfigureAwait(false);

        if (!fingerprint.Success || fingerprint.Fingerprint is null)
        {
            return Failed(
                $"Fingerprint failed: {fingerprint.Error ?? "fpcalc returned no fingerprint"}",
                media,
                quality);
        }

        // 4. Ask AcoustID what it is.
        var lookup = await _client
            .LookupAsync(fingerprint.Fingerprint, fingerprint.DurationSeconds, cancellationToken)
            .ConfigureAwait(false);

        var options = _options.CurrentValue;
        var verdict = WithoutEvaluation(lookup);

        if (verdict is null)
        {
            verdict = Evaluate(request, lookup, options);

            // 5. The start of the file names a different recording. DJ talk-over and long intros do
            // that, so the middle of the file gets one more chance before the download is rejected.
            if (verdict.Outcome == VerificationOutcome.Failed && HasRecordings(lookup))
            {
                var second = await FingerprintMiddleAsync(request, media, cancellationToken).ConfigureAwait(false);

                if (second is not null)
                {
                    var secondLookup = await _client
                        .LookupAsync(second.Fingerprint!, second.DurationSeconds, cancellationToken)
                        .ConfigureAwait(false);

                    if (secondLookup.Status == AcoustIdStatus.Ok)
                    {
                        var secondVerdict = Evaluate(request, secondLookup, options);

                        // Only a real hit replaces the first window's verdict: "AcoustID does not know
                        // the middle either" is not evidence that the start was misread, and would turn
                        // a positively identified wrong recording into an unverified import.
                        if (Worth(secondVerdict) > Worth(verdict))
                        {
                            verdict = secondVerdict;
                            lookup = secondLookup;
                        }
                    }
                }
            }
        }

        LogVerified(_logger, request.Path, verdict.Outcome, verdict.Reason);

        return Finish(verdict, media, quality, lookup);
    }

    /// <summary>Fingerprints the middle window, or <c>null</c> when fpcalc fails there too.</summary>
    private async Task<FingerprintResult?> FingerprintMiddleAsync(
        VerificationRequest request,
        MediaInfo media,
        CancellationToken cancellationToken)
    {
        var middle = await _fingerprinter
            .FingerprintAsync(request.Path, FingerprintWindow.Middle, media.DurationMs, cancellationToken)
            .ConfigureAwait(false);

        return middle.Success && middle.Fingerprint is not null ? middle : null;
    }

    /// <summary>The verdicts that need no evaluation, or <c>null</c> when the lookup has to be evaluated.</summary>
    private static Verdict? WithoutEvaluation(AcoustIdLookupResult lookup) => lookup.Status switch
    {
        AcoustIdStatus.NotConfigured => new Verdict(
            VerificationOutcome.Passed,
            "AcoustID not configured; verified by probe and duration only"),

        AcoustIdStatus.Unavailable or AcoustIdStatus.RateLimited => new Verdict(
            VerificationOutcome.Deferred,
            "AcoustID unavailable; will retry"),

        AcoustIdStatus.InvalidKey => new Verdict(
            VerificationOutcome.Deferred,
            "AcoustID rejected the client key"),

        AcoustIdStatus.InvalidFingerprint or AcoustIdStatus.Error => new Verdict(
            VerificationOutcome.Failed,
            lookup.Error ?? "AcoustID could not read the fingerprint"),

        _ => null,
    };

    /// <summary>Applies the matching rules of MATCHING_ENGINE.md §6.5 to one lookup.</summary>
    private static Verdict Evaluate(
        VerificationRequest request,
        AcoustIdLookupResult lookup,
        AcoustIdOptions options)
    {
        // Best first: the service sorts by score, but nothing promises that.
        var results = lookup.Results.OrderByDescending(result => result.Score).ToList();

        var wanted = request.SongMbRecordingId;
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            var match = results.FirstOrDefault(result => result.Recordings.Any(
                recording => string.Equals(recording.Id, wanted, StringComparison.OrdinalIgnoreCase)));

            if (match is not null)
            {
                if (match.Score >= options.AcceptScore)
                {
                    return new Verdict(
                        VerificationOutcome.Passed,
                        $"Fingerprint matches the song's MusicBrainz recording {wanted} "
                            + $"(score {Score(match.Score)})",
                        FingerprintVerified: true,
                        MatchedRecordingId: wanted);
                }

                if (match.Score >= options.ReviewScore)
                {
                    var reason = $"Fingerprint score {Score(match.Score)} is below {Score(options.AcceptScore)}";

                    return options.Strict
                        ? new Verdict(VerificationOutcome.Failed, reason)
                        : new Verdict(VerificationOutcome.NeedsReview, reason);
                }
            }
        }

        // Either the song has no MBID yet (Deezer-only), or the wanted recording is not among the
        // results: the same song under another MusicBrainz id is still the song.
        var sameTitle = SameTitleMatch(request, results, options.AcceptScore);
        if (sameTitle is { } duplicate)
        {
            var recording = duplicate.Recording;

            return string.IsNullOrWhiteSpace(wanted)
                ? new Verdict(
                    VerificationOutcome.Passed,
                    $"Fingerprint matched \"{recording.Title}\" ({recording.Id}); "
                        + "learned the MusicBrainz recording",
                    FingerprintVerified: true,
                    LearnedMbRecordingId: recording.Id)
                : new Verdict(
                    VerificationOutcome.Passed,
                    $"Matched a same-titled recording {recording.Id} (MusicBrainz duplicate)",
                    FingerprintVerified: true,
                    MatchedRecordingId: recording.Id);
        }

        // AcoustID has no fingerprint for this recording at all — common for new or obscure music.
        return !HasRecordings(lookup)
            ? new Verdict(VerificationOutcome.Passed, "Not in AcoustID; verified by probe and duration only")
            : DifferentRecording(results);
    }

    /// <summary>The failure that names what the fingerprint actually is.</summary>
    private static Verdict DifferentRecording(List<AcoustIdResult> results)
    {
        var best = results.First(result => result.Recordings.Count > 0);
        var identified = best.Recordings[0];

        return new Verdict(
            VerificationOutcome.Failed,
            $"Fingerprint belongs to a different recording: {identified.Title ?? "unknown title"} "
                + $"({identified.Id}), score {Score(best.Score)}");
    }

    /// <summary>
    /// The best result holding a recording that is the same song: same bare title, an overlapping
    /// artist, the same hard version flags, and a length inside the tolerance.
    /// </summary>
    /// <remarks>
    /// The hard flags are what keep a live take from passing for the studio recording the song wants
    /// ("Get Lucky (live)" is not "Get Lucky"), which is most of what the fingerprint step is for.
    /// </remarks>
    private static (AcoustIdResult Result, AcoustIdRecording Recording)? SameTitleMatch(
        VerificationRequest request,
        IReadOnlyList<AcoustIdResult> results,
        double acceptScore)
    {
        // The song's own flags, not the ones its title would parse to: the song is the authority.
        var songTitle = TextMatching.Normalize(VersionFlagParser.Parse(request.SongTitle).BaseTitle);
        var songHardFlags = request.SongFlags & VersionFlagNames.HardFlags;
        var songArtists = request.MainArtists
            .Select(TextMatching.NormalizeArtist)
            .Where(artist => artist.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var result in results)
        {
            if (result.Score < acceptScore)
            {
                continue;
            }

            foreach (var recording in result.Recordings)
            {
                if (IsSameSong(recording, songTitle, songHardFlags, songArtists, request))
                {
                    return (result, recording);
                }
            }
        }

        return null;
    }

    private static bool IsSameSong(
        AcoustIdRecording recording,
        string songTitle,
        VersionFlags songHardFlags,
        HashSet<string> songArtists,
        VerificationRequest request)
    {
        if (string.IsNullOrWhiteSpace(recording.Title))
        {
            return false;
        }

        var candidate = VersionFlagParser.Parse(recording.Title);

        if (!string.Equals(TextMatching.Normalize(candidate.BaseTitle), songTitle, StringComparison.Ordinal))
        {
            return false;
        }

        if ((candidate.Flags & VersionFlagNames.HardFlags) != songHardFlags)
        {
            return false;
        }

        if (!recording.ArtistNames.Any(artist => songArtists.Contains(TextMatching.NormalizeArtist(artist))))
        {
            return false;
        }

        if (recording.DurationSeconds is not { } durationSeconds || request.SongDurationMs is not { } songMs)
        {
            // A length the service did not give cannot confirm the length rule, so it is no match.
            return false;
        }

        return Math.Abs((durationSeconds * 1000) - songMs) <= request.DurationToleranceMs;
    }

    private static bool HasRecordings(AcoustIdLookupResult lookup) =>
        lookup.Results.Any(result => result.Recordings.Count > 0);

    /// <summary>How much a verdict is worth when two fingerprint windows disagree; only a hit wins.</summary>
    private static int Worth(Verdict verdict) => verdict switch
    {
        { Outcome: VerificationOutcome.Passed, FingerprintVerified: true } => 2,
        { Outcome: VerificationOutcome.NeedsReview } => 1,
        _ => 0,
    };

    /// <summary>A failing verdict with nothing measured to report.</summary>
    private static VerificationResult Failed(string reason, MediaInfo? media = null, long? quality = null) =>
        new(VerificationOutcome.Failed, reason, media, quality, null, null, null, null, false);

    /// <summary>Folds the verdict, what was measured and the winning lookup into the result.</summary>
    private static VerificationResult Finish(
        Verdict verdict,
        MediaInfo media,
        long quality,
        AcoustIdLookupResult lookup)
    {
        var best = lookup.Results.Count > 0 ? lookup.Results.MaxBy(result => result.Score) : null;

        return new VerificationResult(
            verdict.Outcome,
            verdict.Reason,
            media,
            quality,
            best?.Id,
            best?.Score,
            verdict.MatchedRecordingId,
            verdict.LearnedMbRecordingId,
            verdict.FingerprintVerified);
    }

    /// <summary>Whole seconds, for the lines a human reads.</summary>
    private static string Seconds(int milliseconds) =>
        (milliseconds / 1000).ToString(CultureInfo.InvariantCulture);

    /// <summary>A score with at most two decimals, so "0.61" and "0.7" read as the service sent them.</summary>
    private static string Score(double score) => score.ToString("0.##", CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Information, Message = "Verified {File}: {Outcome} — {Reason}")]
    private static partial void LogVerified(
        ILogger logger,
        string file,
        VerificationOutcome outcome,
        string reason);

    /// <summary>A verdict and the ids it may carry.</summary>
    private sealed record Verdict(
        VerificationOutcome Outcome,
        string Reason,
        bool FingerprintVerified = false,
        string? MatchedRecordingId = null,
        string? LearnedMbRecordingId = null);
}