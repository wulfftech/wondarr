using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Configuration;
using Wondarr.Core.Domain;
using Wondarr.Core.HealthCheck;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Profiles;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.Organizer;

/// <summary>
/// The recycle bin and the library roots (code review 2026-10-10, R-002): the bin's cleanup deletes
/// every old file under it, so it must never walk a library or reference library, and a library
/// cannot be created or moved onto the bin.
/// </summary>
public sealed class RecycleBinLibraryRootTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly TempRoot _temp = new();
    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly ImportOptions _options = new();
    private ServiceProvider? _services;

    public RecycleBinLibraryRootTests()
    {
        _options.RecycleBinCleanupDays = 7;
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        _database.Dispose();
        _temp.Dispose();
    }

    private async Task<RecycleBin> BinAsync()
    {
        await _database.MigrateAsync(_time);

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddDbContext<WondarrDbContext>(builder => builder
            .UseSqlite($"Data Source={_database.FilePath}")
            .UseSnakeCaseNamingConvention());
        _services = services.BuildServiceProvider();

        return new RecycleBin(
            new DiskOperations(),
            new TestOptionsMonitor<ImportOptions>(_options),
            new WondarrPaths(_temp.Full("config")),
            _time,
            NullLogger<RecycleBin>.Instance,
            _services.GetRequiredService<IServiceScopeFactory>());
    }

    private string MarkedOldFile(string relative)
    {
        var path = _temp.CreateFile(relative, "old");
        File.SetLastWriteTimeUtc(path, Now.UtcDateTime.AddDays(-30));

        return path;
    }

    [Fact]
    public async Task Cleanup_skips_a_reference_library_that_sits_inside_the_bin()
    {
        // import.recycle_bin_path: /data/media, with the user's own collection under it.
        _options.RecycleBinPath = _temp.Full("media");
        _temp.CreateFile("media/.wondarr-recycle-bin", string.Empty);

        var recycled = MarkedOldFile("media/Artist/recycled.mp3");
        var collection = MarkedOldFile("media/collection/Artist/Album/song.flac");

        var bin = await BinAsync();

        await using (var context = _database.CreateContext(_time))
        {
            context.ReferenceLibraries.Add(new ReferenceLibrary
            {
                Name = "Collection",
                RootPath = _temp.Full("media/collection"),
            });
            await context.SaveChangesAsync();
        }

        (await bin.CleanupAsync(CancellationToken.None)).Should().Be(1);

        File.Exists(recycled).Should().BeFalse("a recycled file past the cleanup age goes");
        File.Exists(collection).Should().BeTrue("another library's file is not the bin's to delete");
    }

    [Fact]
    public async Task Cleanup_skips_a_managed_library_that_sits_inside_the_bin()
    {
        _options.RecycleBinPath = _temp.Full("media");
        _temp.CreateFile("media/.wondarr-recycle-bin", string.Empty);

        var collection = MarkedOldFile("media/music/Artist/Album/song.flac");

        var bin = await BinAsync();

        await using (var context = _database.CreateContext(_time))
        {
            var library = await context.Libraries.SingleAsync(entry => entry.Id == SeedData.DefaultLibraryId);
            library.RootPath = _temp.Full("media/music");
            await context.SaveChangesAsync();
        }

        (await bin.CleanupAsync(CancellationToken.None)).Should().Be(0);

        File.Exists(collection).Should().BeTrue();
    }

    [Fact]
    public async Task Cleanup_does_nothing_when_the_bin_itself_sits_inside_a_library()
    {
        // A bin that was marked while the configuration was different, and is now inside a library.
        _options.RecycleBinPath = _temp.Full("library/recycle");
        _temp.CreateFile("library/recycle/.wondarr-recycle-bin", string.Empty);

        var inLibrary = MarkedOldFile("library/recycle/Artist/Album/song.flac");

        var bin = await BinAsync();

        await using (var context = _database.CreateContext(_time))
        {
            var library = await context.Libraries.SingleAsync(entry => entry.Id == SeedData.DefaultLibraryId);
            library.RootPath = _temp.Full("library");
            await context.SaveChangesAsync();
        }

        (await bin.CleanupAsync(CancellationToken.None)).Should().Be(0);

        File.Exists(inLibrary).Should().BeTrue();
    }

    [Fact]
    public async Task Creating_a_library_inside_or_around_the_bin_is_refused()
    {
        await _database.MigrateAsync(_time);

        _options.RecycleBinPath = "/srv/bin";

        foreach (var root in new[] { "/srv/bin/music", "/srv", "/srv/bin" })
        {
            await using var context = _database.CreateContext(_time);
            var service = Service(context);

            var act = async () => await service.CreateAsync(
                new Library { Name = "Second", RootPath = root, Layout = LibraryLayout.Flat },
                CancellationToken.None);

            var exception = await act.Should().ThrowAsync<ProfileValidationException>();
            exception.Which.Errors.Should().Contain(
                error => error.Property == "rootPath" && error.Message.Contains("recycle bin", StringComparison.Ordinal),
                $"'{root}' overlaps the bin");
        }

        await using (var context = _database.CreateContext(_time))
        {
            var beside = await Service(context).CreateAsync(
                new Library { Name = "Second", RootPath = "/srv/binary", Layout = LibraryLayout.Flat },
                CancellationToken.None);

            beside.RootPath.Should().Be("/srv/binary", "a sibling that shares a prefix is not inside the bin");
        }
    }

    [Fact]
    public async Task Moving_a_library_root_onto_the_bin_is_refused()
    {
        await _database.MigrateAsync(_time);

        _options.RecycleBinPath = "/srv/bin";

        await using var context = _database.CreateContext(_time);
        var service = Service(context);

        var created = await service.CreateAsync(
            new Library { Name = "Second", RootPath = "/srv/elsewhere", Layout = LibraryLayout.Flat },
            CancellationToken.None);

        // As the API sends it: a separate object carrying the new values.
        var edited = new Library
        {
            Id = created.Id,
            Name = created.Name,
            RootPath = "/srv/bin/music",
            Layout = created.Layout,
            NamingTemplate = created.NamingTemplate,
        };

        var act = async () => await service.UpdateAsync(edited, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ProfileValidationException>();
        exception.Which.Errors.Should().Contain(
            error => error.Property == "rootPath" && error.Message.Contains("recycle bin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_existing_library_that_overlaps_the_bin_can_still_be_renamed()
    {
        await _database.MigrateAsync(_time);

        _options.RecycleBinPath = "/srv/bin";

        await using (var seed = _database.CreateContext(_time))
        {
            seed.Libraries.Add(new Library { Name = "Old", RootPath = "/srv/bin/music", Layout = LibraryLayout.Flat, NamingTemplate = "{Track Title}" });
            await seed.SaveChangesAsync();
        }

        await using var context = _database.CreateContext(_time);
        var service = Service(context);

        var existing = (await service.GetAllAsync(CancellationToken.None)).Single(library => library.Name == "Old");
        existing.Name = "Renamed";

        var updated = await service.UpdateAsync(existing, CancellationToken.None);

        updated.Name.Should().Be("Renamed");
    }

    [Fact]
    public async Task The_health_check_reports_a_bin_that_overlaps_a_reference_library()
    {
        await _database.MigrateAsync(_time);

        _options.RecycleBinPath = _temp.Full("media");

        await using var context = _database.CreateContext(_time);
        var check = new RecycleBinHealthCheck(
            context,
            new TestOptionsMonitor<ImportOptions>(_options),
            new WondarrPaths(_temp.Full("config")));

        (await check.CheckAsync(CancellationToken.None)).Type.Should().Be(HealthCheckResult.Ok);

        context.ReferenceLibraries.Add(new ReferenceLibrary { Name = "Collection", RootPath = _temp.Full("media/collection") });
        await context.SaveChangesAsync();

        var report = await check.CheckAsync(CancellationToken.None);
        report.Type.Should().Be(HealthCheckResult.Error);
        report.Message.Should().Contain("Collection").And.Contain("recycle_bin_path");
    }

    private LibraryService Service(WondarrDbContext context) =>
        new(context, new TestOptionsMonitor<ImportOptions>(_options), new WondarrPaths(_temp.Full("config")));
}
