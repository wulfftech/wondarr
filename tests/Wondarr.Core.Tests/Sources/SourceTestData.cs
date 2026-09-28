using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;
using Wondarr.Core.Sources;
using Wondarr.Core.Tests.Persistence;

namespace Wondarr.Core.Tests.Sources;

/// <summary>
/// Seeds the rows the search, queue and reputation services hang off: songs, and the run and candidate
/// a queue item must point at.
/// </summary>
internal static class SourceTestData
{
    /// <summary>Inserts one song and returns its id.</summary>
    public static async Task<long> SeedSongAsync(SqliteTestDatabase database, TimeProvider time, string title = "Alpha")
    {
        var songs = await SeedSongsAsync(database, time, title);

        return songs[0];
    }

    /// <summary>Inserts one song per title and returns their ids, in order.</summary>
    public static async Task<List<long>> SeedSongsAsync(
        SqliteTestDatabase database,
        TimeProvider time,
        params string[] titles)
    {
        await using var context = database.CreateContext(time);

        var artist = new Artist { Name = "Aphex Twin", SortName = "Aphex Twin" };
        context.Artists.Add(artist);
        await context.SaveChangesAsync();

        var songs = titles
            .Select(title => new Song
            {
                Title = title,
                ArtistCredit = artist.Name,
                PrimaryArtistId = artist.Id,
                QualityProfileId = SeedData.StandardProfileId,
                LibraryId = SeedData.DefaultLibraryId,
                AddedBy = "api",
            })
            .ToList();

        context.Songs.AddRange(songs);
        await context.SaveChangesAsync();

        return [.. songs.Select(song => song.Id)];
    }

    /// <summary>Inserts a song, a search run for it and one candidate; a queue item needs all three.</summary>
    public static async Task<(long SongId, long SearchRunId, long CandidateId)> SeedSongWithCandidateAsync(
        SqliteTestDatabase database,
        TimeProvider time,
        string title = "Alpha")
    {
        var songId = await SeedSongAsync(database, time, title);
        var (runId, candidateId) = await SeedRunAndCandidateAsync(database, time, songId);

        return (songId, runId, candidateId);
    }

    /// <summary>Inserts a search run with one candidate for the song; queue items need both.</summary>
    public static async Task<(long SearchRunId, long CandidateId)> SeedRunAndCandidateAsync(
        SqliteTestDatabase database,
        TimeProvider time,
        long songId)
    {
        await using var context = database.CreateContext(time);

        var run = new SearchRun
        {
            SongId = songId,
            Trigger = SearchTrigger.Automatic,
            StartedAt = time.GetUtcNow().UtcDateTime,
        };

        context.SearchRuns.Add(run);
        await context.SaveChangesAsync();

        var candidate = new CandidateRecord
        {
            SearchRunId = run.Id,
            SongId = songId,
            SourceType = SourceTypes.Soulseek,
            BlocklistKey = BlocklistKeys.Soulseek("peer", "Music\\one.flac"),
            DisplayName = "one.flac",
            RemotePath = "Music\\one.flac",
            Provider = "peer",
        };

        context.Candidates.Add(candidate);
        await context.SaveChangesAsync();

        return (run.Id, candidate.Id);
    }
}
