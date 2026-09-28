using Wondarr.Core.Persistence;

namespace Wondarr.Core.Domain;

/// <summary>
/// One rung on the quality ladder, seeded from <see cref="SeedData"/> with fixed ids that are a
/// public contract: they are what profiles, files and history rows store. Stored in the <c>quality</c>
/// table.
/// </summary>
public sealed class Quality : EntityBase
{
    /// <summary>Gets or sets the display name, for example <c>MP3-320</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the group the quality is shown under, for example "High lossy".</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>Gets or sets the group rank, 1 (Unknown) to 9 (Uncompressed).</summary>
    public int Rank { get; set; }

    /// <summary>Gets or sets the codec family: <c>unknown</c>, <c>mp3</c>, <c>aac</c>, <c>vorbis</c>, <c>opus</c>, <c>wma</c>, <c>flac</c>, <c>alac</c>, <c>ape</c>, <c>wavpack</c>, <c>wav</c> or <c>aiff</c>.</summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the codec is lossless.</summary>
    public bool Lossless { get; set; }

    /// <summary>Gets or sets the lowest bitrate in kbps this quality covers, or <see langword="null"/> when open.</summary>
    public int? MinBitrate { get; set; }

    /// <summary>Gets or sets the highest bitrate in kbps this quality covers, or <see langword="null"/> when open.</summary>
    public int? MaxBitrate { get; set; }

    /// <summary>Gets or sets the bit depth this quality requires, or <see langword="null"/> when it does not constrain one.</summary>
    public int? BitDepth { get; set; }
}

/// <summary>
/// One entry in a quality profile: a named group of qualities that are all allowed or all rejected.
/// </summary>
/// <param name="Name">The group's display name, or <see langword="null"/> for an unnamed (rejected) group.</param>
/// <param name="QualityIds">The quality ids in this group.</param>
/// <param name="Allowed">Whether a file matching any of <paramref name="QualityIds"/> may be grabbed.</param>
public sealed record QualityProfileItem(string? Name, List<long> QualityIds, bool Allowed);

/// <summary>
/// The rules a song is judged against: which qualities are acceptable, where the cutoff sits and
/// whether a better file is worth replacing the current one. Stored in the <c>quality_profile</c>
/// table, with the groups held as one JSON text column.
/// </summary>
public sealed class QualityProfile : EntityBase
{
    /// <summary>Gets or sets the profile name, for example "Standard 320".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the profile's groups, ordered <em>worst → best</em>: an item's position is its
    /// group index, which is how the cutoff and upgrade rules compare qualities.
    /// </summary>
    public List<QualityProfileItem> Items { get; set; } = [];

    /// <summary>Gets or sets the quality at or above which a song is considered done.</summary>
    public long CutoffQualityId { get; set; }

    /// <summary>Gets or sets a value indicating whether a better file should replace the current one.</summary>
    public bool UpgradeAllowed { get; set; }

    /// <summary>Gets or sets the minimum candidate score (0–100) a grab must reach.</summary>
    public int MinScore { get; set; }

    /// <summary>Gets or sets how far a candidate's duration may differ from the expected one, in milliseconds.</summary>
    public int DurationToleranceMs { get; set; }

    /// <summary>Gets or sets the quality the cutoff sits at.</summary>
    public Quality CutoffQuality { get; set; } = null!;

    /// <summary>
    /// Gets the index of the group containing <paramref name="qualityId"/>, or <see langword="null"/>
    /// when the profile does not mention that quality.
    /// </summary>
    public int? GroupIndexOf(long qualityId)
    {
        for (var index = 0; index < Items.Count; index++)
        {
            if (Items[index].QualityIds.Contains(qualityId))
            {
                return index;
            }
        }

        return null;
    }

    /// <summary>Gets a value indicating whether a file of <paramref name="qualityId"/> may be grabbed.</summary>
    public bool IsAllowed(long qualityId) =>
        GroupIndexOf(qualityId) is { } index && Items[index].Allowed;

    /// <summary>
    /// Gets a value indicating whether <paramref name="qualityId"/> is at or above the cutoff. A
    /// quality the profile does not list never meets it.
    /// </summary>
    public bool MeetsCutoff(long qualityId)
    {
        if (GroupIndexOf(CutoffQualityId) is not { } cutoffIndex)
        {
            return false;
        }

        return GroupIndexOf(qualityId) is { } index && index >= cutoffIndex;
    }

    /// <summary>
    /// Gets a value indicating whether a candidate at <paramref name="candidateQualityId"/> is a
    /// strictly better, allowed quality than the file already held at <paramref name="currentQualityId"/>.
    /// A current quality the profile does not list counts as worse than everything in it.
    /// </summary>
    public bool IsUpgrade(long currentQualityId, long candidateQualityId)
    {
        if (!UpgradeAllowed || !IsAllowed(candidateQualityId))
        {
            return false;
        }

        var currentIndex = GroupIndexOf(currentQualityId) ?? -1;

        return GroupIndexOf(candidateQualityId) is { } candidateIndex && candidateIndex > currentIndex;
    }
}
