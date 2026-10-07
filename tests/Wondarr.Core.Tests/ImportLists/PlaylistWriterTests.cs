using System.Globalization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Domain;
using Wondarr.Core.ImportLists;
using Wondarr.Core.Persistence;
using Wondarr.Core.Plex;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.ImportLists;

/// <summary>
/// A list's playlists (DECISIONS build session 7 #10): the Plex playlist made once and then reconciled
/// in place — added, removed, re-ordered — and the <c>.m3u8</c> with relative paths.
/// </summary>
public sealed class PlaylistWriterTests : IDisposable
{
    private const string Machine = "machine-1";

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly IPlexConnectionService _connection = Substitute.For<IPlexConnectionService>();
    private readonly FakePlex _plex = new();
    private readonly string _root;

    public PlaylistWriterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wondarr-playlist-tests", Guid.NewGuid().ToString("N"), "music");
        Directory.CreateDirectory(_root);

        _connection.GetStateAsync(Arg.Any<CancellationToken>())
            .Returns(new PlexConnectionState(true, "http://plex:32400", "plex", Machine, "client"));
        _connection.GetServerContextAsync(Arg.Any<CancellationToken>())
            .Returns(((Uri, string)?)(new Uri("http://plex:32400"), "token"));
    }

    [Fact]
    public void The_diff_removes_strays_and_duplicates_and_appends_what_is_missing()
    {
        var current = new[]
        {
            new PlexPlaylistItem("a", "i1"),
            new PlexPlaylistItem("x", "i2"),
            new PlexPlaylistItem("b", "i3"),
            new PlexPlaylistItem("a", "i4"),
        };

        var (remove, add) = PlaylistWriter.Diff(current, ["a", "b", "c"]);

        remove.Should().Equal("i2", "i4");
        add.Should().Equal("c");
    }

    [Fact]
    public void The_moves_put_the_items_into_the_wanted_order()
    {
        var current = new[] { new PlexPlaylistItem("1", "i1"), new PlexPlaylistItem("2", "i2"), new PlexPlaylistItem("3", "i3") };

        var moves = PlaylistWriter.Moves(current, ["3", "1", "2"]);

        // 3 to the top; 1 and 2 then follow in order without moving.
        moves.Should().Equal(("i3", (string?)null));
    }

    [Fact]
    public async Task The_first_write_creates_the_playlist_in_order_and_later_writes_reconcile_it()
    {
        await using var context = await ContextAsync();
        var list = await SeedListAsync(context, plex: true, m3u: false, ["A", "B", "C"]);
        var writer = Writer(context);

        var first = await writer.WriteAsync(list.Id, CancellationToken.None);

        first.Should().Be("Plex playlist created with 3 tracks");
        _plex.Playlist.Select(item => item.RatingKey).Should().Equal("rk-A", "rk-B", "rk-C");
        (await context.ImportLists.AsNoTracking().SingleAsync()).PlexPlaylistKey.Should().Be("pl-1");

        // The source now reads C, A, D: B left, D is new, C moved to the top.
        await ReorderAsync(context, list.Id, ["C", "A"], removed: ["B"]);
        await AddItemAsync(context, list.Id, "D", position: 2);
        _plex.Searches = 0;

        var second = await writer.WriteAsync(list.Id, CancellationToken.None);

        second.Should().StartWith("Plex playlist updated (+1 -1");
        _plex.Playlist.Select(item => item.RatingKey).Should().Equal("rk-C", "rk-A", "rk-D");
        _plex.Searches.Should().Be(1, "the rating keys of files that did not move are cached on the items");
        _plex.Created.Should().Be(1, "the playlist is updated in place, never made again");
    }

    [Fact]
    public async Task A_playlist_deleted_in_plex_is_made_again()
    {
        await using var context = await ContextAsync();
        var list = await SeedListAsync(context, plex: true, m3u: false, ["A"]);
        var writer = Writer(context);
        await writer.WriteAsync(list.Id, CancellationToken.None);

        _plex.Deleted = true;

        (await writer.WriteAsync(list.Id, CancellationToken.None)).Should().Be("Plex playlist created with 1 tracks");
        _plex.Created.Should().Be(2);
    }

    [Fact]
    public async Task A_song_plex_has_not_scanned_yet_waits_for_the_next_write()
    {
        await using var context = await ContextAsync();
        var list = await SeedListAsync(context, plex: true, m3u: false, ["A", "B"]);
        _plex.Unknown.Add("B");

        (await Writer(context).WriteAsync(list.Id, CancellationToken.None))
            .Should().Be("Plex playlist created with 1 tracks (1 not in Plex yet)");
    }

    [Fact]
    public async Task The_m3u8_lists_the_files_in_order_relative_to_the_playlists_folder()
    {
        await using var context = await ContextAsync();
        var list = await SeedListAsync(context, plex: false, m3u: true, ["A", "B"]);

        var result = await Writer(context).WriteAsync(list.Id, CancellationToken.None);

        result.Should().Be(".m3u8 written with 2 tracks");
        var text = await File.ReadAllTextAsync(Path.Combine(_root, "Playlists", "Road trip.m3u8"));
        text.Should().Be("#EXTM3U\n#EXTINF:200,Artist A - A\n../Artist A/A.mp3\n#EXTINF:200,Artist B - B\n../Artist B/B.mp3\n");
        _plex.Created.Should().Be(0);
    }

    public void Dispose()
    {
        _database.Dispose();
    }

    private PlaylistWriter Writer(WondarrDbContext context) =>
        new(context, _connection, _plex, NullLogger<PlaylistWriter>.Instance);

    private async Task<WondarrDbContext> ContextAsync()
    {
        await _database.MigrateAsync(_timeProvider);

        var context = _database.CreateContext(_timeProvider);
        var library = await context.Libraries.SingleAsync(candidate => candidate.Id == SeedData.DefaultLibraryId);
        library.RootPath = _root;
        library.PlexSectionId = "1";
        library.PlexLibraryPath = "/music";
        await context.SaveChangesAsync();

        return context;
    }

    private async Task<ImportList> SeedListAsync(WondarrDbContext context, bool plex, bool m3u, IReadOnlyList<string> titles)
    {
        var list = new ImportList
        {
            Type = "fake",
            Name = "Road trip",
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            PlexPlaylist = plex,
            M3uExport = m3u,
        };
        context.ImportLists.Add(list);
        await context.SaveChangesAsync();

        for (var index = 0; index < titles.Count; index++)
        {
            await AddItemAsync(context, list.Id, titles[index], index);
        }

        return list;
    }

    private async Task AddItemAsync(WondarrDbContext context, long listId, string title, int position)
    {
        var artist = new Artist { Name = "Artist " + title, SortName = "Artist " + title };
        context.Artists.Add(artist);
        await context.SaveChangesAsync();

        var song = new Song
        {
            Title = title,
            ArtistCredit = artist.Name,
            PrimaryArtistId = artist.Id,
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "api",
        };
        context.Songs.Add(song);
        await context.SaveChangesAsync();

        context.SongFiles.Add(new SongFile
        {
            SongId = song.Id,
            Path = Path.Combine(_root, artist.Name, title + ".mp3"),
            Codec = "mp3",
            Container = "mp3",
            DurationMs = 200_000,
            QualityId = 23,
            SourceType = "soulseek",
            ImportedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        context.ImportListItems.Add(new ImportListItem
        {
            ImportListId = listId,
            ExternalId = "x-" + title,
            Position = position,
            SongId = song.Id,
            State = ImportListItemState.Added,
        });
        await context.SaveChangesAsync();
    }

    private static async Task ReorderAsync(WondarrDbContext context, long listId, IReadOnlyList<string> order, IReadOnlyList<string> removed)
    {
        var items = await context.ImportListItems.Include(item => item.Song).Where(item => item.ImportListId == listId).ToListAsync();

        foreach (var item in items)
        {
            var at = order.ToList().IndexOf(item.Song!.Title);

            if (at >= 0)
            {
                item.Position = at;
            }

            if (removed.Contains(item.Song.Title))
            {
                item.RemovedAt = new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);
            }
        }

        await context.SaveChangesAsync();
    }

    /// <summary>One Plex server with one playlist, behaving as the calls python-plexapi documents.</summary>
    private sealed class FakePlex : IPlexServerClient
    {
        private int _nextItemId = 100;

        public List<PlexPlaylistItem> Playlist { get; } = [];

        public HashSet<string> Unknown { get; } = new(StringComparer.Ordinal);

        public int Created { get; private set; }

        public int Searches { get; set; }

        public bool Deleted { get; set; }

        public Task<IReadOnlyList<PlexTrack>> FindTracksAsync(Uri server, string token, string sectionKey, string title, CancellationToken cancellationToken)
        {
            Searches++;

            IReadOnlyList<PlexTrack> found = Unknown.Contains(title)
                ? []
                : [new PlexTrack("rk-" + title, title, [$"/music/Artist {title}/{title}.mp3"]), new PlexTrack("other", title, ["/music/elsewhere.mp3"])];

            return Task.FromResult(found);
        }

        public Task<IReadOnlyList<PlexPlaylistItem>?> GetPlaylistItemsAsync(Uri server, string token, string playlistKey, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PlexPlaylistItem>?>(Deleted ? null : [.. Playlist]);

        public Task<string> CreatePlaylistAsync(Uri server, string token, string machineIdentifier, string title, IReadOnlyList<string> ratingKeys, CancellationToken cancellationToken)
        {
            machineIdentifier.Should().Be(Machine);
            Deleted = false;
            Playlist.Clear();
            Created++;
            Append(ratingKeys);

            return Task.FromResult("pl-" + Created.ToString(CultureInfo.InvariantCulture));
        }

        public Task AddPlaylistItemsAsync(Uri server, string token, string machineIdentifier, string playlistKey, IReadOnlyList<string> ratingKeys, CancellationToken cancellationToken)
        {
            Append(ratingKeys);

            return Task.CompletedTask;
        }

        public Task RemovePlaylistItemAsync(Uri server, string token, string playlistKey, string playlistItemId, CancellationToken cancellationToken)
        {
            Playlist.RemoveAll(item => item.PlaylistItemId == playlistItemId);

            return Task.CompletedTask;
        }

        public Task MovePlaylistItemAsync(Uri server, string token, string playlistKey, string playlistItemId, string? afterPlaylistItemId, CancellationToken cancellationToken)
        {
            var item = Playlist.Single(candidate => candidate.PlaylistItemId == playlistItemId);
            Playlist.Remove(item);
            var at = afterPlaylistItemId is null ? 0 : Playlist.FindIndex(candidate => candidate.PlaylistItemId == afterPlaylistItemId) + 1;
            Playlist.Insert(at, item);

            return Task.CompletedTask;
        }

        public Task<PlexIdentity> GetIdentityAsync(Uri server, string token, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PlexSection>> GetSectionsAsync(Uri server, string token, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RefreshPathAsync(Uri server, string token, string sectionKey, string serverPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task EmptyTrashAsync(Uri server, string token, string sectionKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsRefreshingAsync(Uri server, string token, string sectionKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private void Append(IReadOnlyList<string> ratingKeys)
        {
            foreach (var key in ratingKeys)
            {
                Playlist.Add(new PlexPlaylistItem(key, (_nextItemId++).ToString(CultureInfo.InvariantCulture)));
            }
        }
    }
}
