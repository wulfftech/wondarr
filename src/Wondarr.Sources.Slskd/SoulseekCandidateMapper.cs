using Wondarr.Core.Sources;

namespace Wondarr.Sources.Slskd;

/// <summary>
/// Turns the peer responses of one Soulseek search into <see cref="Candidate"/>s (MATCHING_ENGINE.md
/// §6.1). Pure code: slskd's own attributes go to <see cref="SoulseekQuality"/>, the path goes to
/// <see cref="SoulseekFilenameParser"/>, and nothing here scores or rejects anything.
/// </summary>
public static class SoulseekCandidateMapper
{
    /// <summary>One slash of either kind separates the segments of a remote path.</summary>
    private static readonly char[] PathSeparators = ['\\', '/'];

    /// <summary>
    /// Maps every audio file every peer offered, in the order slskd reported it, de-duplicated by
    /// <see cref="Candidate.BlocklistKey"/> — the same file offered by two peers is two candidates,
    /// the same file offered twice by one peer is one.
    /// </summary>
    /// <param name="responses">The search's responses.</param>
    /// <param name="query">The search text that found them, recorded on every candidate.</param>
    public static IReadOnlyList<Candidate> Map(IReadOnlyList<SlskdSearchResponse> responses, string query)
    {
        ArgumentNullException.ThrowIfNull(responses);
        ArgumentNullException.ThrowIfNull(query);

        var candidates = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var response in responses)
        {
            // lockedFiles is deliberately ignored: a locked file cannot be downloaded without the
            // peer granting access, so it is not a candidate.
            foreach (var file in response.Files)
            {
                var path = SoulseekFilenameParser.Parse(file.Filename);

                if (!path.IsAudio)
                {
                    continue;
                }

                var key = BlocklistKeys.Soulseek(response.Username, file.Filename);

                if (!seen.Add(key))
                {
                    continue;
                }

                candidates.Add(new Candidate
                {
                    SourceType = SourceTypes.Soulseek,
                    BlocklistKey = key,
                    DisplayName = LastSegment(file.Filename),
                    RemotePath = file.Filename,
                    Provider = response.Username,
                    Parsed = path.Parsed,
                    DurationMs = file.Length is null ? null : file.Length.Value * 1000,
                    Extension = path.Extension,
                    BitrateKbps = file.BitRate,
                    SampleRate = file.SampleRate,
                    BitDepth = file.BitDepth,
                    IsVariableBitrate = file.IsVariableBitRate,
                    SizeBytes = file.Size,
                    QualityId = SoulseekQuality.Infer(
                        path.Extension,
                        file.BitRate,
                        file.IsVariableBitRate,
                        file.SampleRate,
                        file.BitDepth,
                        file.Length,
                        file.Size),
                    Availability = new CandidateAvailability(
                        response.HasFreeUploadSlot,
                        response.QueueLength,
                        response.UploadSpeed),
                    IsLocked = file.IsLocked,
                    Query = query,
                });
            }
        }

        return candidates;
    }

    /// <summary>The file's own name without the peer's directory path, however the peer separated it.</summary>
    private static string LastSegment(string filename)
    {
        var separator = filename.LastIndexOfAny(PathSeparators);

        return separator >= 0 ? filename[(separator + 1)..] : filename;
    }
}
