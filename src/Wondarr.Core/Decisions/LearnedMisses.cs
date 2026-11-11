using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wondarr.Core.Domain;
using Wondarr.Core.Metadata;
using Wondarr.Core.Persistence;
using Wondarr.Core.Searching;
using Wondarr.Core.Songs;
using Wondarr.Core.Sources;

namespace Wondarr.Core.Decisions;

/// <summary>
/// The one copy of the compilation-miss rule (MATCHING_ENGINE.md §6.2) that the import, the file remover and
/// the learned-evidence backfill share: a candidate that passed the artist rule on a compilation path only on
/// its full title (<see cref="DecisionEngine.PassesArtistOnTitleAlone"/>) and that turned out not to be the
/// song counts one miss for the song, as one permanent blocklist row.
/// </summary>
internal static class LearnedMisses
{
    /// <summary>
    /// The candidate as the search judged it, read back from its snapshot; for an older Soulseek row without
    /// one, what its path says, parsed as the search would have. Another source's row without a snapshot has
    /// no title of its own to read (a torrent's path is its release name), so nothing is learned from it.
    /// </summary>
    /// <param name="record">The stored candidate.</param>
    internal static Candidate? StoredCandidate(CandidateRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            if (JsonSerializer.Deserialize<Candidate>(record.Normalised, SongSearchService.Json) is { } stored)
            {
                return stored;
            }
        }
        catch (JsonException)
        {
            // An empty snapshot ("{}") lacks the required fields; the row's own columns are enough here.
        }

        if (record.SourceType != SourceTypes.Soulseek
            || string.IsNullOrEmpty(record.RemotePath)
            || string.IsNullOrEmpty(record.BlocklistKey))
        {
            return null;
        }

        return new Candidate
        {
            SourceType = record.SourceType,
            BlocklistKey = record.BlocklistKey,
            DisplayName = record.DisplayName,
            RemotePath = record.RemotePath,
            Provider = record.Provider,
            QualityId = record.QualityId,
            Parsed = SoulseekFilenameParser.Parse(record.RemotePath).Parsed,
        };
    }

    /// <summary>Whether a stored candidate's snapshot deserialises (an empty "{}" one does not).</summary>
    /// <param name="record">The stored candidate.</param>
    internal static bool HasSnapshot(CandidateRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            return JsonSerializer.Deserialize<Candidate>(record.Normalised, SongSearchService.Json) is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the stored candidate passed the song's artist rule only on its title on a compilation path.
    /// </summary>
    /// <param name="song">The song, loaded with its credits and their artists.</param>
    /// <param name="record">The stored candidate.</param>
    internal static bool PassedOnTitleAlone(Song song, CandidateRecord record)
    {
        ArgumentNullException.ThrowIfNull(song);

        if (StoredCandidate(record) is not { } candidate)
        {
            return false;
        }

        var mainArtists = song.OrderedCredits()
            .Where(credit => credit.Role == ArtistRole.Main)
            .Select(credit => credit.Artist.Name)
            .ToList();

        return DecisionEngine.PassesArtistOnTitleAlone(mainArtists, song.Title, ParseFlags(song.VersionFlags), candidate);
    }

    /// <summary>
    /// The compilation-miss row for a stored candidate that did not turn out to be the song, or
    /// <see langword="null"/> when it did not pass on its title alone or its miss is already recorded.
    /// The caller adds the row to its own save.
    /// </summary>
    /// <param name="database">The database.</param>
    /// <param name="song">The song, loaded with its credits and their artists.</param>
    /// <param name="record">The stored candidate.</param>
    /// <param name="sourceType">The source the row is for.</param>
    /// <param name="reason">The reason the row carries.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    internal static async Task<BlocklistItem?> MissRowAsync(
        WondarrDbContext database,
        Song song,
        CandidateRecord record,
        string sourceType,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        if (!PassedOnTitleAlone(song, record))
        {
            return null;
        }

        var key = BlocklistKeys.CompilationMiss(record.BlocklistKey);

        if (await database.Blocklist
                .AnyAsync(entry => entry.SongId == song.Id && entry.BlocklistKey == key, cancellationToken)
                .ConfigureAwait(false))
        {
            return null;
        }

        return new BlocklistItem
        {
            SongId = song.Id,
            SourceType = sourceType,
            BlocklistKey = key,
            Reason = reason,
            ExpiresAt = null,
        };
    }

    /// <summary>The song's stored version flags as a mask; unknown names are ignored.</summary>
    /// <param name="names">The stored flag names.</param>
    internal static VersionFlags ParseFlags(IEnumerable<string> names)
    {
        var flags = VersionFlags.None;

        foreach (var name in names)
        {
            if (VersionFlagNames.TryParse(name, out var single))
            {
                flags |= single;
            }
        }

        return flags;
    }
}
