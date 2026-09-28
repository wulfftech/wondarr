using Compilarr.Core.Domain;

namespace Compilarr.Api.Profiles;

/// <summary>
/// A quality profile as the *arr clients and the Settings UI expect it. The shape follows Lidarr's
/// <c>QualityProfileResource</c> (<c>src/Lidarr.Api.V1/Profiles/Qualities/QualityProfileResource.cs</c>)
/// with Compilarr's names: <c>cutoff</c> is the cutoff quality's id and <c>items</c> run worst → best.
/// </summary>
/// <param name="Id">The <c>quality_profile</c> row id.</param>
/// <param name="Name">The profile name.</param>
/// <param name="UpgradeAllowed">Whether a better file replaces the current one.</param>
/// <param name="Cutoff">The id of the quality at or above which a song is done.</param>
/// <param name="MinScore">The minimum candidate score a grab must reach.</param>
/// <param name="DurationToleranceMs">How far a candidate's duration may differ, in milliseconds.</param>
/// <param name="Items">The item groups, ordered worst → best.</param>
public sealed record QualityProfileResource(
    long Id,
    string Name,
    bool UpgradeAllowed,
    long Cutoff,
    int MinScore,
    int DurationToleranceMs,
    List<QualityProfileItemResource> Items);

/// <summary>One item group: the qualities it holds are all allowed or all rejected.</summary>
/// <param name="Name">The group's display name, or <see langword="null"/> for an unnamed group.</param>
/// <param name="Qualities">The qualities in the group; only <see cref="QualityProfileQualityResource.Id"/> is read on input.</param>
/// <param name="Allowed">Whether a file matching any of <paramref name="Qualities"/> may be grabbed.</param>
public sealed record QualityProfileItemResource(
    string? Name,
    List<QualityProfileQualityResource> Qualities,
    bool Allowed);

/// <summary>
/// A quality inside an item group. <see cref="Name"/> is nullable because it is output only: on
/// input a client sends the ids alone and the names come from the seeded ladder.
/// </summary>
/// <param name="Id">The <c>quality</c> row id.</param>
/// <param name="Name">The quality's display name, for example <c>MP3-320</c>, as read.</param>
public sealed record QualityProfileQualityResource(long Id, string? Name);

/// <summary>Maps between <see cref="QualityProfile"/> and <see cref="QualityProfileResource"/>.</summary>
public static class QualityProfileResourceMapper
{
    /// <summary>Builds the resource for <paramref name="profile"/>.</summary>
    /// <param name="profile">The stored profile.</param>
    /// <param name="qualitiesById">Every quality, so the groups can carry display names.</param>
    public static QualityProfileResource ToResource(
        this QualityProfile profile,
        IReadOnlyDictionary<long, Quality> qualitiesById)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(qualitiesById);

        return new QualityProfileResource(
            profile.Id,
            profile.Name,
            profile.UpgradeAllowed,
            profile.CutoffQualityId,
            profile.MinScore,
            profile.DurationToleranceMs,
            [.. profile.Items.Select(item => new QualityProfileItemResource(
                item.Name,
                [.. item.QualityIds.Select(id => new QualityProfileQualityResource(
                    id,
                    qualitiesById.TryGetValue(id, out var quality) ? quality.Name : string.Empty))],
                item.Allowed))]);
    }

    /// <summary>
    /// Builds the profile <paramref name="resource"/> describes. Only the quality ids are read from
    /// the groups: names and display order come from the seeded ladder, so a client cannot rename a
    /// quality by posting to a profile.
    /// </summary>
    /// <param name="resource">The resource to read.</param>
    /// <param name="id">The id to store it under: the route id, or 0 to let the database assign one.</param>
    public static QualityProfile ToProfile(this QualityProfileResource resource, long id)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return new QualityProfile
        {
            Id = id,
            Name = resource.Name,
            UpgradeAllowed = resource.UpgradeAllowed,
            CutoffQualityId = resource.Cutoff,
            MinScore = resource.MinScore,
            DurationToleranceMs = resource.DurationToleranceMs,
            Items =
            [
                .. (resource.Items ?? []).Select(item => new QualityProfileItem(
                    item.Name,
                    [.. (item.Qualities ?? []).Select(quality => quality.Id)],
                    item.Allowed))
            ],
        };
    }
}
