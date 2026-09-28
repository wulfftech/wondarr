using Compilarr.Core.Blocklisting;
using Compilarr.Core.Domain;
using Compilarr.Core.History;
using Compilarr.Core.Paging;
using Compilarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Compilarr.Core.Tests.History;

public sealed class HistoryAndBlocklistServiceTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task History_lists_newest_first_and_filters_by_song_and_event()
    {
        using var database = new SqliteTestDatabase();
        var time = new FakeTimeProvider(Now);
        await database.MigrateAsync(time);
        var songs = await SeedSongsAsync(database, time, "Alpha", "Bravo");

        var service = new HistoryService(database.CreateContext(time));

        await service.AddAsync(Event(songs[0], HistoryEventType.Grabbed, """{"candidate":1}"""), CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        await service.AddAsync(Event(songs[1], HistoryEventType.Imported, """{"path":"/data/music/b.mp3"}"""), CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(1));
        await service.AddAsync(Event(songs[0], HistoryEventType.Imported, "{}"), CancellationToken.None);

        var all = await service.GetPageAsync(new PagingSpec(1, 20, null, true), null, null, CancellationToken.None);
        all.TotalRecords.Should().Be(3);
        all.Records.Select(item => item.EventType)
            .Should().ContainInOrder(HistoryEventType.Imported, HistoryEventType.Imported, HistoryEventType.Grabbed);
        all.Records[0].Song.Title.Should().Be("Alpha");
        all.Records[0].Song.PrimaryArtist.Name.Should().Be("Aphex Twin");

        var bySong = await service.GetPageAsync(new PagingSpec(1, 20, "date", false), songs[0], null, CancellationToken.None);
        bySong.TotalRecords.Should().Be(2);
        bySong.Records.Select(item => item.EventType)
            .Should().ContainInOrder(HistoryEventType.Grabbed, HistoryEventType.Imported);

        var byType = await service.GetPageAsync(new PagingSpec(1, 20, null, true), null, HistoryEventType.Grabbed, CancellationToken.None);
        byType.TotalRecords.Should().Be(1);
        byType.Records[0].SongId.Should().Be(songs[0]);
        byType.Records[0].Data.Should().Be("""{"candidate":1}""");
    }

    [Fact]
    public async Task Blocklist_entries_block_until_they_expire()
    {
        using var database = new SqliteTestDatabase();
        var time = new FakeTimeProvider(Now);
        await database.MigrateAsync(time);
        var songs = await SeedSongsAsync(database, time, "Alpha");

        var service = new BlocklistService(database.CreateContext(time), time);
        var token = CancellationToken.None;

        await service.AddAsync(Entry(songs[0], "slskd", "peer:path", expiresAt: null), token);
        await service.AddAsync(Entry(songs[0], "slskd", "peer:tomorrow", expiresAt: Now.AddDays(1)), token);
        await service.AddAsync(Entry(songs[0], "slskd", "peer:yesterday", expiresAt: Now.AddDays(-1)), token);

        (await service.IsBlocklistedAsync("slskd", "peer:path", token)).Should().BeTrue();
        (await service.IsBlocklistedAsync("slskd", "peer:tomorrow", token)).Should().BeTrue();
        (await service.IsBlocklistedAsync("slskd", "peer:yesterday", token)).Should().BeFalse();
        (await service.IsBlocklistedAsync("slskd", "peer:unknown", token)).Should().BeFalse();
        (await service.IsBlocklistedAsync("youtube", "peer:path", token)).Should().BeFalse();

        // Two days later the dated entry has expired too and only the permanent one still blocks.
        time.Advance(TimeSpan.FromDays(2));
        (await service.IsBlocklistedAsync("slskd", "peer:tomorrow", token)).Should().BeFalse();
        (await service.IsBlocklistedAsync("slskd", "peer:path", token)).Should().BeTrue();
    }

    [Fact]
    public async Task Blocklist_pages_deletes_and_survives_a_song_delete()
    {
        using var database = new SqliteTestDatabase();
        var time = new FakeTimeProvider(Now);
        await database.MigrateAsync(time);
        var songs = await SeedSongsAsync(database, time, "Alpha", "Bravo");

        var service = new BlocklistService(database.CreateContext(time), time);
        var token = CancellationToken.None;

        var first = await service.AddAsync(Entry(songs[0], "slskd", "peer:one", expiresAt: null), token);
        time.Advance(TimeSpan.FromMinutes(1));
        var second = await service.AddAsync(Entry(songs[1], "slskd", "peer:two", expiresAt: null), token);

        var page = await service.GetPageAsync(new PagingSpec(1, 1, null, true), token);
        page.TotalRecords.Should().Be(2);
        page.Records.Should().ContainSingle().Which.Id.Should().Be(second.Id);

        (await service.DeleteAsync(second.Id, token)).Should().BeTrue();
        (await service.DeleteAsync(second.Id, token)).Should().BeFalse();
        (await service.GetPageAsync(new PagingSpec(1, 20, null, true), token)).TotalRecords.Should().Be(1);

        // Deleting the song must not take the blocklist row with it: the key stays blocked.
        await using (var context = database.CreateContext(time))
        {
            await context.Songs.Where(song => song.Id == songs[0]).ExecuteDeleteAsync(token);
        }

        await using var reread = database.CreateContext(time);
        var survivor = await reread.Blocklist.AsNoTracking().SingleAsync(token);
        survivor.Id.Should().Be(first.Id);
        survivor.SongId.Should().BeNull();
    }

    private static async Task<List<long>> SeedSongsAsync(SqliteTestDatabase database, FakeTimeProvider time, params string[] titles)
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

    private static HistoryItem Event(long songId, HistoryEventType eventType, string data) => new()
    {
        SongId = songId,
        EventType = eventType,
        Data = data,
    };

    private static BlocklistItem Entry(long songId, string sourceType, string key, DateTime? expiresAt) => new()
    {
        SongId = songId,
        SourceType = sourceType,
        BlocklistKey = key,
        Reason = "Verification failed",
        ExpiresAt = expiresAt,
    };
}
