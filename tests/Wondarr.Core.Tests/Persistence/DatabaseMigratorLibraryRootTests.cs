using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Wondarr.Core.Domain;
using Wondarr.Core.Organizer;
using Wondarr.Core.Persistence;
using Wondarr.Core.Tests.Organizer;
using Wondarr.Core.Tests.Support;
using Xunit;

namespace Wondarr.Core.Tests.Persistence;

/// <summary>A new database's seeded library root is created and marked once; an existing database's is never touched.</summary>
public sealed class DatabaseMigratorLibraryRootTests : IDisposable
{
    private readonly TempRoot _temp = new();
    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new();

    public void Dispose()
    {
        _temp.Dispose();
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private static DatabaseMigrator Migrator(WondarrDbContext context, string root, Action<string>? prepare) =>
        new(context, NullLogger<DatabaseMigrator>.Instance, () => null, newLibraryRoot: root, prepareNewLibraryRoot: prepare);

    [Fact]
    public async Task A_new_database_creates_and_marks_the_seeded_libraries_final_root()
    {
        var root = _temp.Full("media/music");
        await using var context = _database.CreateContext(_time);

        await Migrator(context, root, TestLibraryRoots.Real.Mark).MigrateAsync(CancellationToken.None);

        (await context.Libraries.AsNoTracking().SingleAsync(library => library.Id == SeedData.DefaultLibraryId)).RootPath.Should().Be(root);
        TestLibraryRoots.Real.Check(root).Should().Be(LibraryRootStatus.Ready);
    }

    [Fact]
    public async Task An_existing_database_is_never_touched_by_the_migrator()
    {
        var root = _temp.Full("media/music");
        await using (var first = _database.CreateContext(_time))
        {
            await Migrator(first, root, prepare: null).MigrateAsync(CancellationToken.None);
        }

        Directory.Exists(root).Should().BeFalse();

        await using var context = _database.CreateContext(_time);
        await Migrator(context, root, TestLibraryRoots.Real.Mark).MigrateAsync(CancellationToken.None);

        Directory.Exists(root).Should().BeFalse("the root of an existing library may be an unmounted share");
    }

    [Fact]
    public async Task A_root_that_cannot_be_prepared_is_logged_and_never_thrown()
    {
        await using var context = _database.CreateContext(_time);

        var act = async () => await Migrator(context, _temp.Full("media/music"), _ => throw new UnauthorizedAccessException("read-only"))
            .MigrateAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
