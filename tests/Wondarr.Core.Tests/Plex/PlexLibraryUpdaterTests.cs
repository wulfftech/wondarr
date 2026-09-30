using FluentAssertions;
using Wondarr.Core.Domain;
using Wondarr.Core.HealthCheck;
using Wondarr.Core.Importing;
using Wondarr.Core.Persistence;
using Wondarr.Core.Plex;
using Wondarr.Core.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Wondarr.Core.Tests.Plex;

public sealed class PlexLibraryUpdaterTests : IAsyncDisposable
{
    private static readonly (Uri Server, string Token) SelectedServer =
        (new Uri("http://plex.local:32400"), "a-plex-token");

    // Wondarr's own paths are the host's (the folder comes from Path.GetDirectoryName of a file
    // path); the Plex server keeps the library somewhere else, which is what the mapping translates.
    private static readonly string MusicRoot = Path.Combine(Path.GetTempPath(), "wondarr-plex-tests", "music");
    private static readonly string BooksRoot = Path.Combine(Path.GetTempPath(), "wondarr-plex-tests", "books");

    private const string MusicPlexRoot = "/plex/music";
    private const string BooksPlexRoot = "/plex/books";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteTestDatabase _database = new();
    private readonly IPlexServerClient _client = Substitute.For<IPlexServerClient>();
    private readonly IPlexConnectionService _connection = Substitute.For<IPlexConnectionService>();
    private readonly ServiceProvider _provider;
    private readonly PlexLibraryUpdater _updater;
    private readonly CancellationTokenSource _stopping = new();

    public PlexLibraryUpdaterTests()
    {
        _database.MigrateAsync(_time).GetAwaiter().GetResult();

        _connection.GetServerContextAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(Uri Server, string Token)?>(SelectedServer));

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddDbContext<WondarrDbContext>(builder => builder
            .UseSqlite($"Data Source={_database.FilePath}")
            .UseSnakeCaseNamingConvention());
        services.AddScoped(_ => _client);
        services.AddScoped(_ => _connection);

        _provider = services.BuildServiceProvider();

        _updater = new PlexLibraryUpdater(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _time,
            NullLogger<PlexLibraryUpdater>.Instance);
    }

    [Fact]
    public async Task An_import_scans_the_mapped_album_folder_once_the_library_is_quiet()
    {
        var libraryId = await SeedLibraryAsync("Music", MusicRoot, section: "7", plexRoot: MusicPlexRoot);
        var fileId = await SeedFileAsync(
            libraryId,
            Path.Combine(MusicRoot, "Daft Punk", "Discovery", "01 - One More Time.flac"));

        await _updater.StartAsync(_stopping.Token);
        await _updater.HandleAsync(new SongImportedEvent(1, fileId, Upgraded: false), CancellationToken.None);

        await AdvanceAsync(PlexLibraryUpdater.QuietPeriod - TimeSpan.FromSeconds(2));
        Refreshes().Should().BeEmpty("the library has not been quiet for long enough");

        await AdvanceUntilAsync(() => Refreshes().Count == 1, "the scan after the quiet period");

        var refresh = Refreshes().Single();
        refresh.Server.Should().Be("http://plex.local:32400/");
        refresh.Token.Should().Be("a-plex-token");
        refresh.Section.Should().Be("7");
        refresh.Path.Should().Be("/plex/music/Daft Punk/Discovery");
        _updater.IsPending.Should().BeFalse();
    }

    [Fact]
    public async Task A_burst_of_imports_in_one_folder_is_one_scan()
    {
        var libraryId = await SeedLibraryAsync("Music", MusicRoot, section: "7", plexRoot: MusicPlexRoot);
        var first = await SeedFileAsync(
            libraryId,
            Path.Combine(MusicRoot, "Daft Punk", "Discovery", "01 - One More Time.flac"));
        var second = await SeedFileAsync(
            libraryId,
            Path.Combine(MusicRoot, "Daft Punk", "Discovery", "02 - Aerodynamic.flac"));

        await _updater.StartAsync(_stopping.Token);

        for (var index = 0; index < 4; index++)
        {
            await _updater.HandleAsync(
                new SongImportedEvent(index + 1, index % 2 == 0 ? first : second, Upgraded: false),
                CancellationToken.None);
            await AdvanceAsync(TimeSpan.FromSeconds(2));
        }

        await AdvanceAsync(PlexLibraryUpdater.QuietPeriod + TimeSpan.FromSeconds(2));

        Refreshes().Should().ContainSingle();
    }

    [Fact]
    public async Task Imports_that_keep_arriving_are_still_scanned_by_MaxDelay()
    {
        var libraryId = await SeedLibraryAsync("Music", MusicRoot, section: "7", plexRoot: MusicPlexRoot);
        var fileId = await SeedFileAsync(
            libraryId,
            Path.Combine(MusicRoot, "Daft Punk", "Discovery", "01 - One More Time.flac"));

        await _updater.StartAsync(_stopping.Token);

        // An import every 5 s never leaves the library quiet for 10 s.
        for (var elapsed = TimeSpan.Zero;
             elapsed < PlexLibraryUpdater.MaxDelay + TimeSpan.FromSeconds(5);
             elapsed += TimeSpan.FromSeconds(5))
        {
            await _updater.HandleAsync(new SongImportedEvent(1, fileId, Upgraded: false), CancellationToken.None);
            await AdvanceAsync(TimeSpan.FromSeconds(5));
        }

        Refreshes().Should().ContainSingle("the first pending import is scanned no later than MaxDelay");
    }

    [Fact]
    public async Task Each_library_is_scanned_with_its_own_section_and_mapping()
    {
        var music = await SeedLibraryAsync("Music", MusicRoot, section: "7", plexRoot: MusicPlexRoot);
        var books = await SeedLibraryAsync("Audiobooks", BooksRoot, section: "9", plexRoot: BooksPlexRoot);
        var first = await SeedFileAsync(
            music,
            Path.Combine(MusicRoot, "Daft Punk", "Discovery", "01 - One More Time.flac"));
        var second = await SeedFileAsync(
            books,
            Path.Combine(BooksRoot, "Adams", "Hitchhiker", "01 - Chapter One.flac"));

        await _updater.StartAsync(_stopping.Token);
        await _updater.HandleAsync(new SongImportedEvent(1, first, Upgraded: false), CancellationToken.None);
        await _updater.HandleAsync(new SongImportedEvent(2, second, Upgraded: false), CancellationToken.None);

        await AdvanceUntilAsync(() => Refreshes().Count == 2, "one scan per library");

        Refreshes().Should().BeEquivalentTo(
        [
            ("http://plex.local:32400/", "a-plex-token", "7", "/plex/music/Daft Punk/Discovery"),
            ("http://plex.local:32400/", "a-plex-token", "9", "/plex/books/Adams/Hitchhiker"),
        ], "each library is asked for the folder as its own Plex server sees it");
    }

    [Fact]
    public async Task More_than_twenty_five_folders_scans_the_library_root_once()
    {
        var libraryId = await SeedLibraryAsync("Music", MusicRoot, section: "7", plexRoot: MusicPlexRoot);

        await _updater.StartAsync(_stopping.Token);

        for (var index = 0; index <= PlexLibraryUpdater.MaxFoldersPerLibrary; index++)
        {
            var fileId = await SeedFileAsync(
                libraryId,
                Path.Combine(MusicRoot, "Artist", $"Album {index}", "01 - Track.flac"));

            await _updater.HandleAsync(new SongImportedEvent(index + 1, fileId, Upgraded: false), CancellationToken.None);
        }

        await AdvanceUntilAsync(() => Refreshes().Count == 1, "the single root scan");

        Refreshes().Single().Path.Should().Be("/plex/music");
    }

    [Fact]
    public async Task A_library_with_no_section_is_never_scanned()
    {
        var libraryId = await SeedLibraryAsync("Music", MusicRoot, section: null);
        var fileId = await SeedFileAsync(
            libraryId,
            Path.Combine(MusicRoot, "Daft Punk", "Discovery", "01 - One More Time.flac"));

        await _updater.StartAsync(_stopping.Token);
        await _updater.HandleAsync(new SongImportedEvent(1, fileId, Upgraded: false), CancellationToken.None);

        await AdvanceUntilAsync(() => !_updater.IsPending, "the request to be dropped");
        await AdvanceAsync(PlexLibraryUpdater.RetryDelay * 2);

        Refreshes().Should().BeEmpty();
        _updater.LastError.Should().BeNull("a library with no section is not a failure");
    }

    [Fact]
    public async Task Nothing_is_scanned_when_no_server_is_connected()
    {
        _connection.GetServerContextAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(Uri Server, string Token)?>(null));

        var libraryId = await SeedLibraryAsync("Music", MusicRoot, section: "7", plexRoot: MusicPlexRoot);
        var fileId = await SeedFileAsync(
            libraryId,
            Path.Combine(MusicRoot, "Daft Punk", "Discovery", "01 - One More Time.flac"));

        await _updater.StartAsync(_stopping.Token);
        await _updater.HandleAsync(new SongImportedEvent(1, fileId, Upgraded: false), CancellationToken.None);

        await AdvanceUntilAsync(() => !_updater.IsPending, "the request to be dropped");
        await AdvanceAsync(PlexLibraryUpdater.RetryDelay * 2);

        Refreshes().Should().BeEmpty();
        _updater.LastError.Should().BeNull("not being signed in is not a failure");
    }

    [Fact]
    public async Task A_failed_scan_is_retried_after_RetryDelay_and_a_success_clears_the_error()
    {
        var libraryId = await SeedLibraryAsync("Music", MusicRoot, section: "7", plexRoot: MusicPlexRoot);
        var fileId = await SeedFileAsync(
            libraryId,
            Path.Combine(MusicRoot, "Daft Punk", "Discovery", "01 - One More Time.flac"));

        var attempts = new RefCounter();
        RefuseFirst(attempts, 1, new PlexException("Plex is not answering"));

        await _updater.StartAsync(_stopping.Token);
        await _updater.HandleAsync(new SongImportedEvent(1, fileId, Upgraded: false), CancellationToken.None);

        await AdvanceUntilAsync(() => attempts.Value == 1, "the first attempt");
        _updater.LastError.Should().Be("Plex is not answering");
        _updater.IsPending.Should().BeTrue();

        await AdvanceAsync(PlexLibraryUpdater.RetryDelay - TimeSpan.FromSeconds(5));
        attempts.Value.Should().Be(1, "the retry waits RetryDelay");

        await AdvanceUntilAsync(() => attempts.Value == 2, "the retry");

        _updater.LastError.Should().BeNull("the next successful scan clears it");
        _updater.IsPending.Should().BeFalse();
    }

    [Fact]
    public async Task A_folder_that_failed_three_times_is_dropped()
    {
        var libraryId = await SeedLibraryAsync("Music", MusicRoot, section: "7", plexRoot: MusicPlexRoot);
        var fileId = await SeedFileAsync(
            libraryId,
            Path.Combine(MusicRoot, "Daft Punk", "Discovery", "01 - One More Time.flac"));

        var attempts = new RefCounter();
        RefuseFirst(attempts, int.MaxValue, new HttpRequestException("Plex is not answering"));

        await _updater.StartAsync(_stopping.Token);
        await _updater.HandleAsync(new SongImportedEvent(1, fileId, Upgraded: false), CancellationToken.None);

        await AdvanceUntilAsync(
            () => attempts.Value == PlexLibraryUpdater.MaxAttemptsPerFolder,
            "the third attempt");

        _updater.IsPending.Should().BeFalse("the folder is dropped after three failures");
        _updater.LastError.Should().Be("Plex is not answering");

        await AdvanceAsync(PlexLibraryUpdater.RetryDelay * 3);

        attempts.Value.Should().Be(PlexLibraryUpdater.MaxAttemptsPerFolder);
    }

    [Fact]
    public async Task One_folder_s_success_does_not_clear_another_folder_s_failure()
    {
        var libraryId = await SeedLibraryAsync("Music", MusicRoot, section: "7", plexRoot: MusicPlexRoot);
        var failing = await SeedFileAsync(
            libraryId,
            Path.Combine(MusicRoot, "Daft Punk", "Discovery", "01 - One More Time.flac"));
        var fine = await SeedFileAsync(
            libraryId,
            Path.Combine(MusicRoot, "Daft Punk", "Homework", "01 - Da Funk.flac"));

        _client.RefreshPathAsync(
                Arg.Any<Uri>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Is<string>(path => path.Contains("Discovery", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new PlexException("Plex is not answering")));

        await _updater.StartAsync(_stopping.Token);
        await _updater.HandleAsync(new SongImportedEvent(1, failing, Upgraded: false), CancellationToken.None);
        await _updater.HandleAsync(new SongImportedEvent(2, fine, Upgraded: false), CancellationToken.None);

        await AdvanceUntilAsync(() => Refreshes().Count == 2, "both folders to be tried");

        _updater.LastError.Should().Be("Plex is not answering", "the Discovery folder is still waiting for its retry");
        _updater.IsPending.Should().BeTrue();
    }

    [Fact]
    public async Task A_batch_whose_connection_cannot_be_read_is_dropped_after_three_tries()
    {
        var libraryId = await SeedLibraryAsync("Music", MusicRoot, section: "7", plexRoot: MusicPlexRoot);
        var fileId = await SeedFileAsync(
            libraryId,
            Path.Combine(MusicRoot, "Daft Punk", "Discovery", "01 - One More Time.flac"));

        var reads = new RefCounter();
        _connection.GetServerContextAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                reads.Next();

                return Task.FromException<(Uri Server, string Token)?>(new InvalidOperationException("settings unreadable"));
            });

        await _updater.StartAsync(_stopping.Token);
        await _updater.HandleAsync(new SongImportedEvent(1, fileId, Upgraded: false), CancellationToken.None);

        await AdvanceUntilAsync(() => reads.Value == PlexLibraryUpdater.MaxAttemptsPerFolder, "the third try");

        _updater.IsPending.Should().BeFalse("the batch is dropped after three failed tries");

        await AdvanceAsync(PlexLibraryUpdater.RetryDelay * 3);

        reads.Value.Should().Be(PlexLibraryUpdater.MaxAttemptsPerFolder);
    }

    [Fact]
    public async Task A_refused_token_drops_the_whole_batch_at_once()
    {
        var music = await SeedLibraryAsync("Music", MusicRoot, section: "7", plexRoot: MusicPlexRoot);
        var books = await SeedLibraryAsync("Audiobooks", BooksRoot, section: "9", plexRoot: BooksPlexRoot);
        var first = await SeedFileAsync(
            music,
            Path.Combine(MusicRoot, "Daft Punk", "Discovery", "01 - One More Time.flac"));
        var second = await SeedFileAsync(
            books,
            Path.Combine(BooksRoot, "Adams", "Hitchhiker", "01 - Chapter One.flac"));

        var attempts = new RefCounter();
        RefuseFirst(attempts, int.MaxValue, new PlexUnauthorizedException("Plex refused the token"));

        await _updater.StartAsync(_stopping.Token);
        await _updater.HandleAsync(new SongImportedEvent(1, first, Upgraded: false), CancellationToken.None);
        await _updater.HandleAsync(new SongImportedEvent(2, second, Upgraded: false), CancellationToken.None);

        await AdvanceUntilAsync(() => !_updater.IsPending, "the batch to be dropped");

        attempts.Value.Should().Be(1, "the second library is not asked with the same token");
        _updater.LastError.Should().Be("Plex refused the token");

        await AdvanceAsync(PlexLibraryUpdater.RetryDelay * 2);

        attempts.Value.Should().Be(1, "a refused token is never retried");
    }

    [Fact]
    public async Task RequestFolder_scans_a_folder_without_an_import_event()
    {
        var libraryId = await SeedLibraryAsync("Music", MusicRoot, section: "7", plexRoot: MusicPlexRoot);

        await _updater.StartAsync(_stopping.Token);
        _updater.RequestFolder(libraryId, Path.Combine(MusicRoot, "Daft Punk", "Random Access Memories"));

        await AdvanceUntilAsync(() => Refreshes().Count == 1, "the scan the request asked for");

        Refreshes().Single().Path.Should().Be("/plex/music/Daft Punk/Random Access Memories");
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        await _updater.StopAsync(CancellationToken.None);
        _updater.Dispose();
        _stopping.Dispose();
        await _provider.DisposeAsync();
        _database.Dispose();
    }

    /// <summary>The calls the updater made, as plain strings.</summary>
    private List<(string Server, string Token, string Section, string Path)> Refreshes() =>
    [
        .. _client.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IPlexServerClient.RefreshPathAsync))
            .Select(call => (
                Server: ((Uri)call.GetArguments()[0]!).ToString(),
                Token: (string)call.GetArguments()[1]!,
                Section: (string)call.GetArguments()[2]!,
                Path: (string)call.GetArguments()[3]!)),
    ];

    /// <summary>Makes the client fail its first <paramref name="failures"/> calls and answer afterwards.</summary>
    private void RefuseFirst(RefCounter attempts, int failures, Exception exception)
    {
        _client.RefreshPathAsync(
                Arg.Any<Uri>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => attempts.Next() <= failures
                ? Task.FromException(exception)
                : Task.CompletedTask);
    }

    private async Task<long> SeedLibraryAsync(string name, string root, string? section, string? plexRoot = null)
    {
        await using var context = _database.CreateContext(_time);

        var library = new Library
        {
            // The seeded "Music" library already holds this name, and names are unique.
            Name = $"{name} {Guid.NewGuid():N}",
            RootPath = root,
            NamingTemplate = "{Album Artist Name}/{Album Title}/{track:00} - {Track Title}",
            PlexSectionId = section,
            PlexLibraryPath = plexRoot,
        };

        context.Libraries.Add(library);
        await context.SaveChangesAsync();

        return library.Id;
    }

    private async Task<long> SeedFileAsync(long libraryId, string path)
    {
        await using var context = _database.CreateContext(_time);

        var artist = new Artist { Name = $"Artist {Guid.NewGuid():N}", SortName = "Artist" };
        context.Artists.Add(artist);
        await context.SaveChangesAsync();

        var song = new Song
        {
            Title = "A Song",
            ArtistCredit = artist.Name,
            PrimaryArtistId = artist.Id,
            QualityProfileId = SeedData.StandardProfileId,
            LibraryId = libraryId,
            AddedBy = "ui",
        };

        context.Songs.Add(song);
        await context.SaveChangesAsync();

        var file = new SongFile
        {
            SongId = song.Id,
            Path = path,
            Size = 1_000_000,
            Codec = "flac",
            Container = "flac",
            QualityId = 1,
            SourceType = "soulseek",
            ImportedAt = _time.GetUtcNow().UtcDateTime,
        };

        context.SongFiles.Add(file);
        await context.SaveChangesAsync();

        return file.Id;
    }

    /// <summary>Advances fake time one second at a time, letting the updater's loop run in between.</summary>
    private async Task AdvanceAsync(TimeSpan span)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < span; elapsed += TimeSpan.FromSeconds(1))
        {
            await Settle();
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        await Settle();
    }

    private async Task AdvanceUntilAsync(Func<bool> condition, string because)
    {
        for (var step = 0; step < 900; step++)
        {
            if (condition())
            {
                return;
            }

            await Settle();
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        await Settle();
        condition().Should().BeTrue($"expected {because} within 900 fake seconds");
    }

    // Gives the updater's continuations a turn on the thread pool.
    private static Task Settle() => Task.Delay(5);

    /// <summary>A counter the substitute's callback can advance from whichever thread it runs on.</summary>
    private sealed class RefCounter
    {
        private int _value;

        public int Value => Volatile.Read(ref _value);

        public int Next() => Interlocked.Increment(ref _value);
    }
}

public sealed class PlexHealthCheckTests
{
    private readonly IPlexLibraryUpdater _updater = Substitute.For<IPlexLibraryUpdater>();
    private readonly IPlexConnectionService _connection = Substitute.For<IPlexConnectionService>();

    [Fact]
    public async Task Without_a_failure_it_is_healthy()
    {
        _updater.LastError.Returns((string?)null);

        var result = await new PlexHealthCheck(_updater, _connection).CheckAsync(CancellationToken.None);

        result.Type.Should().Be(HealthCheckResult.Ok);
        result.Source.Should().Be(nameof(PlexHealthCheck));
    }

    [Fact]
    public async Task A_failure_with_no_server_connected_is_healthy()
    {
        _updater.LastError.Returns("Plex is not answering");
        _connection.GetServerContextAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(Uri Server, string Token)?>(null));

        var result = await new PlexHealthCheck(_updater, _connection).CheckAsync(CancellationToken.None);

        result.Type.Should().Be(HealthCheckResult.Ok);
    }

    [Fact]
    public async Task A_failure_with_a_server_connected_is_a_warning()
    {
        _updater.LastError.Returns("Plex is not answering");
        _connection.GetServerContextAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<(Uri Server, string Token)?>(
                (new Uri("http://plex.local:32400"), "a-plex-token")));

        var result = await new PlexHealthCheck(_updater, _connection).CheckAsync(CancellationToken.None);

        result.Type.Should().Be(HealthCheckResult.Warning);
        result.Message.Should().Be("Plex partial scan failed: Plex is not answering");
        result.Message.Should().NotContain("a-plex-token");
    }
}
