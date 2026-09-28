using Compilarr.Core.Domain;
using Compilarr.Core.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Compilarr.Core.Profiles;

/// <summary>Reads and writes the quality profiles a song is judged against.</summary>
public interface IQualityProfileService
{
    /// <summary>Lists every profile, ordered by id.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<IReadOnlyList<QualityProfile>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>Reads one profile, or <see langword="null"/> when the id is unknown.</summary>
    /// <param name="id">The profile id.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    Task<QualityProfile?> GetAsync(long id, CancellationToken cancellationToken);

    /// <summary>Validates and stores a new profile.</summary>
    /// <param name="profile">The profile to add; its id is assigned by the database.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ProfileValidationException">The profile is not valid.</exception>
    Task<QualityProfile> AddAsync(QualityProfile profile, CancellationToken cancellationToken);

    /// <summary>Validates and replaces the stored profile with the same id.</summary>
    /// <param name="profile">The new values; <see cref="EntityBase.Id"/> selects the row.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="ProfileValidationException">The profile is not valid, or its id is unknown.</exception>
    Task<QualityProfile> UpdateAsync(QualityProfile profile, CancellationToken cancellationToken);

    /// <summary>Deletes a profile.</summary>
    /// <param name="id">The profile id.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true"/> when a row was deleted, <see langword="false"/> when the id is unknown.</returns>
    /// <exception cref="ProfileInUseException">A song still uses the profile, or it is the last one.</exception>
    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken);
}

/// <summary>
/// The business rules of a quality profile: the name is unique, every seeded quality appears exactly
/// once across the items, the cutoff sits in an allowed item and the numbers stay in range. A profile
/// that a song uses — or the last remaining one — cannot be deleted.
/// </summary>
public sealed class QualityProfileService : IQualityProfileService
{
    /// <summary>The longest accepted profile name.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The highest accepted minimum candidate score.</summary>
    public const int MaxMinScore = 1000;

    /// <summary>The widest accepted duration tolerance, in milliseconds.</summary>
    public const int MaxDurationToleranceMs = 60_000;

    /// <summary>How many missing quality ids the message names before it summarises.</summary>
    private const int MissingIdSampleSize = 8;

    private readonly CompilarrDbContext _context;

    /// <summary>Initialises a new instance of the <see cref="QualityProfileService"/> class.</summary>
    /// <param name="context">The database context.</param>
    public QualityProfileService(CompilarrDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<QualityProfile>> GetAllAsync(CancellationToken cancellationToken) =>
        await _context.QualityProfiles
            .AsNoTracking()
            .OrderBy(profile => profile.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<QualityProfile?> GetAsync(long id, CancellationToken cancellationToken) =>
        await _context.QualityProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(profile => profile.Id == id, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<QualityProfile> AddAsync(QualityProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var errors = await ValidateAsync(profile, cancellationToken).ConfigureAwait(false);
        if (errors.Count > 0)
        {
            throw new ProfileValidationException(errors);
        }

        profile.Id = 0;
        _context.QualityProfiles.Add(profile);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return profile;
    }

    /// <inheritdoc />
    public async Task<QualityProfile> UpdateAsync(QualityProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var stored = await _context.QualityProfiles
            .FirstOrDefaultAsync(candidate => candidate.Id == profile.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ProfileValidationException(
                [("id", $"Quality profile {profile.Id} does not exist.")]);

        var errors = await ValidateAsync(profile, cancellationToken).ConfigureAwait(false);
        if (errors.Count > 0)
        {
            throw new ProfileValidationException(errors);
        }

        stored.Name = profile.Name;
        stored.UpgradeAllowed = profile.UpgradeAllowed;
        stored.CutoffQualityId = profile.CutoffQualityId;
        stored.MinScore = profile.MinScore;
        stored.DurationToleranceMs = profile.DurationToleranceMs;
        stored.Items = profile.Items;

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return stored;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var profile = await _context.QualityProfiles
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (profile is null)
        {
            return false;
        }

        if (await _context.Songs.AnyAsync(song => song.QualityProfileId == id, cancellationToken).ConfigureAwait(false))
        {
            throw new ProfileInUseException($"Quality profile {id} is still used by a song.");
        }

        // Counted after the in-use check so a song's profile reports the more specific reason.
        if (await _context.QualityProfiles.CountAsync(cancellationToken).ConfigureAwait(false) <= 1)
        {
            throw new ProfileInUseException("The last quality profile cannot be deleted.");
        }

        _context.QualityProfiles.Remove(profile);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>Checks <paramref name="profile"/> against the rules and returns every problem found.</summary>
    private async Task<List<(string Property, string Message)>> ValidateAsync(
        QualityProfile profile,
        CancellationToken cancellationToken)
    {
        var errors = new List<(string Property, string Message)>();

        var name = profile.Name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add(("name", "name must not be empty."));
        }
        else if (name.Length > MaxNameLength)
        {
            errors.Add(("name", $"name must be at most {MaxNameLength} characters."));
        }
        else if (await _context.QualityProfiles
            .AnyAsync(
                // NOCASE is SQLite's ASCII case-insensitive collation: "Test" and "test" collide.
                candidate => candidate.Id != profile.Id && EF.Functions.Collate(candidate.Name, "NOCASE") == name,
                cancellationToken)
            .ConfigureAwait(false))
        {
            errors.Add(("name", $"A quality profile named '{name}' already exists."));
        }

        errors.AddRange(await ValidateItemsAsync(profile, cancellationToken).ConfigureAwait(false));

        if (profile.MinScore is < 0 or > MaxMinScore)
        {
            errors.Add(("minScore", $"minScore must be between 0 and {MaxMinScore}."));
        }

        if (profile.DurationToleranceMs is < 0 or > MaxDurationToleranceMs)
        {
            errors.Add(("durationToleranceMs", $"durationToleranceMs must be between 0 and {MaxDurationToleranceMs}."));
        }

        return errors;
    }

    /// <summary>
    /// Checks the item groups: every seeded quality exactly once, no unknown id, at least one allowed
    /// item and a cutoff that sits in one of them.
    /// </summary>
    private async Task<List<(string Property, string Message)>> ValidateItemsAsync(
        QualityProfile profile,
        CancellationToken cancellationToken)
    {
        var errors = new List<(string Property, string Message)>();

        var knownIds = await _context.Qualities
            .Select(quality => quality.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var occurrences = new Dictionary<long, int>();
        foreach (var item in profile.Items)
        {
            foreach (var qualityId in item.QualityIds)
            {
                occurrences[qualityId] = occurrences.GetValueOrDefault(qualityId) + 1;
            }
        }

        foreach (var (qualityId, count) in occurrences.Where(pair => pair.Value > 1).OrderBy(pair => pair.Key))
        {
            errors.Add(("items", $"Quality id {qualityId} is listed {count} times; every quality must appear exactly once."));
        }

        foreach (var qualityId in occurrences.Keys.Where(id => !knownIds.Contains(id)).OrderBy(id => id))
        {
            errors.Add(("items", $"Unknown quality id {qualityId}."));
        }

        var missing = knownIds.Where(id => !occurrences.ContainsKey(id)).OrderBy(id => id).ToList();
        if (missing.Count > 0)
        {
            var sample = string.Join(", ", missing.Take(MissingIdSampleSize));
            var suffix = missing.Count > MissingIdSampleSize ? ", …" : string.Empty;

            errors.Add((
                "items",
                $"{missing.Count} qualities are missing from the profile ({sample}{suffix}); every quality must appear exactly once."));
        }

        if (!profile.Items.Any(item => item.Allowed))
        {
            errors.Add(("items", "At least one item must be allowed."));
        }
        else if (!profile.Items.Any(item => item.Allowed && item.QualityIds.Contains(profile.CutoffQualityId)))
        {
            errors.Add(("cutoff", $"Cutoff quality id {profile.CutoffQualityId} must sit in an allowed item."));
        }

        return errors;
    }
}
