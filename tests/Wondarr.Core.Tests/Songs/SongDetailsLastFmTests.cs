using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wondarr.Core.Identity;
using Wondarr.Core.Metadata.Deezer;
using Wondarr.Core.Metadata.LastFm;
using Wondarr.Core.Persistence;
using Wondarr.Core.Songs;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.Songs;

/// <summary>
/// How the song page pairs Last.fm's similar tracks with the library: the answer, and that finding it
/// reads a handful of candidate rows by title instead of the whole Songs table.
/// </summary>
public sealed class SongDetailsLastFmTests
{
    [Fact]
    public async Task Similar_tracks_are_matched_by_title_candidates_and_the_library_is_never_read_whole()
    {
        using var database = new SqliteTestDatabase();
        var time = TimeProvider.System;
        await database.MigrateAsync(time);

        var recorder = new SqlRecorder();
        await using var context = new WondarrDbContext(
            new DbContextOptionsBuilder<WondarrDbContext>()
                .UseSqlite($"Data Source={database.FilePath}")
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(recorder)
                .Options,
            time);

        var song = await SongSeed.AddAsync(context, "Get Lucky");

        for (var index = 0; index < 60; index++)
        {
            await SongSeed.AddAsync(context, $"Unrelated {index}", "Somebody");
        }

        var wanted = await SongSeed.AddAsync(context, "Lose Yourself To Dance");
        var accented = await SongSeed.AddAsync(context, "Don't Stop", "Daft Punk");

        var lastFm = Substitute.For<ILastFmClient>();
        lastFm.IsConfigured.Returns(true);
        lastFm.GetTrackAsync(null, "Daft Punk", "Get Lucky", Arg.Any<CancellationToken>())
            .Returns(new LastFmResult<LastFmTrackInfo>(LastFmStatus.Ok, new LastFmTrackInfo(null, 1, 2, [], null, null), null));
        lastFm.GetArtistAsync("Daft Punk", Arg.Any<CancellationToken>())
            .Returns(new LastFmResult<LastFmArtistInfo>(LastFmStatus.NotFound, null, null));
        lastFm.GetSimilarAsync(null, "Daft Punk", "Get Lucky", Arg.Any<CancellationToken>())
            .Returns(new LastFmResult<IReadOnlyList<LastFmSimilarTrack>>(
                LastFmStatus.Ok,
                [
                    new LastFmSimilarTrack("Daft Punk", "Lose Yourself to Dance", null, 0.9, null),
                    new LastFmSimilarTrack("Daft Punk", "Dont Stop", null, 0.8, null),
                    new LastFmSimilarTrack("Nobody", "Never Heard Of It", null, 0.1, null),
                ],
                null));

        var service = new SongDetailsService(
            context,
            Substitute.For<IIdentityResolver>(),
            Substitute.For<IDeezerClient>(),
            lastFm,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<SongDetailsService>.Instance);

        var details = await service.GetAsync(song.Id, CancellationToken.None);

        var similar = details!.LastFm!.Similar;
        similar.Select(item => item.SongId).Should().Equal(wanted.Id, accented.Id, null);

        var lookups = recorder.Texts
            .Where(text => text.Contains("artist_credit", StringComparison.OrdinalIgnoreCase)
                && text.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase))
            .ToList();

        lookups.Should().NotBeEmpty("the name lookups ran");
        lookups.Should().OnlyContain(
            text => text.Contains("LIKE", StringComparison.Ordinal) && text.Contains("LIMIT", StringComparison.Ordinal),
            "every name lookup is a LIKE on the title with a row cap, never the whole table");
        lookups.Should().HaveCountLessThanOrEqualTo(6, "two lookups per similar track at most");
    }

    private sealed class SqlRecorder : DbCommandInterceptor
    {
        private readonly object _sync = new();
        private readonly List<string> _texts = [];

        public IReadOnlyList<string> Texts
        {
            get
            {
                lock (_sync)
                {
                    return [.. _texts];
                }
            }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Record(command);

            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);

            return ValueTask.FromResult(result);
        }

        private void Record(DbCommand command)
        {
            lock (_sync)
            {
                _texts.Add(command.CommandText);
            }
        }
    }
}
