using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.Persistence;
using Wondarr.Core.References;
using Wondarr.Core.Tests.Persistence;
using Xunit;

namespace Wondarr.Core.Tests.References;

/// <summary>
/// The scan command: one library or every enabled one, and one library that cannot be reached never
/// stops the ones behind it.
/// </summary>
public sealed class ReferenceLibraryScanCommandHandlerTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly IReferenceScanner _scanner = Substitute.For<IReferenceScanner>();
    private readonly ServiceProvider _provider;

    public ReferenceLibraryScanCommandHandlerTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(_time);
        services.AddDbContext<WondarrDbContext>(options => options
            .UseSqlite($"Data Source={_database.FilePath}")
            .UseSnakeCaseNamingConvention());
        services.AddSingleton(_scanner);

        _provider = services.BuildServiceProvider();

        _scanner
            .ScanAsync(Arg.Any<long>(), Arg.Any<Func<string, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ReferenceScanResult(Seen: 1, Added: 1, Changed: 0, Unchanged: 0, Missing: 0, Unreadable: 0)));
    }

    [Fact]
    public async Task An_empty_body_scans_every_enabled_library_in_id_order()
    {
        var (first, _, last) = await AddLibrariesAsync(enabled: [true, false, true]);

        var summary = await Handler.ExecuteAsync(Context(null), CancellationToken.None);

        ScannedIds().Should().Equal(first, last);
        summary.Should().Contain("2 libraries").And.Contain("2 files");
    }

    [Fact]
    public async Task A_body_naming_a_library_scans_only_that_one()
    {
        var (first, second, _) = await AddLibrariesAsync(enabled: [true, true, true]);

        var summary = await Handler.ExecuteAsync(Context($"{{\"referenceLibraryId\": {second}}}"), CancellationToken.None);

        ScannedIds().Should().Equal(second);
        summary.Should().Contain("1 library");
        _ = first;
    }

    [Fact]
    public async Task A_library_that_cannot_be_scanned_does_not_stop_the_next_one()
    {
        var (first, second, _) = await AddLibrariesAsync(enabled: [true, true, false]);
        _scanner
            .ScanAsync(first, Arg.Any<Func<string, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ReferenceScanResult>(
                new ReferenceLibraryUnavailableException("'M' cannot be scanned: no files found; is the share mounted?")));

        var summary = await Handler.ExecuteAsync(Context(null), CancellationToken.None);

        ScannedIds().Should().Equal(first, second);
        summary.Should().Contain("2 libraries").And.Contain("1 unavailable").And.Contain("1 files");
    }

    [Fact]
    public async Task A_library_whose_scan_fails_unexpectedly_does_not_stop_the_next_one()
    {
        var (first, second, _) = await AddLibrariesAsync(enabled: [true, true, false]);
        _scanner
            .ScanAsync(first, Arg.Any<Func<string, Task>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ReferenceScanResult>(new InvalidOperationException("database is locked")));

        var summary = await Handler.ExecuteAsync(Context(null), CancellationToken.None);

        ScannedIds().Should().Equal(first, second);
        summary.Should().Contain("1 unavailable");
    }

    [Fact]
    public async Task A_body_naming_a_library_that_does_not_exist_says_so()
    {
        await AddLibrariesAsync(enabled: [true]);

        var summary = await Handler.ExecuteAsync(Context("{\"referenceLibraryId\": 999}"), CancellationToken.None);

        summary.Should().Contain("999");
        ScannedIds().Should().BeEmpty();
    }

    /// <inheritdoc />
    public void Dispose() => _provider.Dispose();

    private ReferenceLibraryScanCommandHandler Handler =>
        new(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReferenceLibraryScanCommandHandler>.Instance);

    private static CommandContext Context(string? body) =>
        new(1, body, CommandTrigger.Scheduled, _ => Task.CompletedTask);

    private List<long> ScannedIds() =>
        [.. _scanner.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IReferenceScanner.ScanAsync))
            .Select(call => (long)call.GetArguments()[0]!)];

    private async Task<(long First, long Second, long Third)> AddLibrariesAsync(bool[] enabled)
    {
        await _database.MigrateAsync(_time);

        await using var database = _database.CreateContext(_time);

        var libraries = new List<ReferenceLibrary>();

        // Always three rows, so a test that only cares about the first one still gets a fixed shape.
        for (var index = 0; index < 3; index++)
        {
            var library = new ReferenceLibrary
            {
                Name = $"Library {index}",
                RootPath = Path.Combine(Path.GetTempPath(), $"wondarr-reference-{index}"),
                Enabled = enabled.ElementAtOrDefault(index),
            };

            libraries.Add(library);
            database.ReferenceLibraries.Add(library);
        }

        await database.SaveChangesAsync();

        return (libraries[0].Id, libraries[1].Id, libraries[2].Id);
    }
}