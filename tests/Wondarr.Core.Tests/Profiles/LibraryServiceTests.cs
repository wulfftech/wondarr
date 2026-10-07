using Wondarr.Core.Domain;
using Wondarr.Core.Persistence;
using Wondarr.Core.Profiles;
using Wondarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Core.Tests.Profiles;

/// <summary>
/// The library rules against a real migrated SQLite database: the seeded Plexamp library, the
/// validation of every editable field, and the single-default rule.
/// </summary>
public class LibraryServiceTests
{
    [Fact]
    public async Task Listing_returns_the_seeded_plexamp_library_and_it_is_the_default()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var service = new LibraryService(context);

        var libraries = await service.GetAllAsync(CancellationToken.None);

        libraries.Should().ContainSingle();
        libraries[0].Name.Should().Be("Music");
        libraries[0].Layout.Should().Be(LibraryLayout.Plexamp);
        libraries[0].AlbumPolicy.Should().Be(AlbumPolicy.FewestAlbums);

        var byId = await service.GetAsync(SeedData.DefaultLibraryId, CancellationToken.None);
        byId.Should().NotBeNull();
        byId!.RootPath.Should().Be("/data/music");

        var byDefault = await service.GetDefaultAsync(CancellationToken.None);
        byDefault.Should().NotBeNull();
        byDefault!.Id.Should().Be(SeedData.DefaultLibraryId);
    }

    [Fact]
    public async Task Reading_an_unknown_library_returns_null()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var service = new LibraryService(context);

        (await service.GetAsync(987_654, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Updating_a_library_stores_every_editable_field()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using (var context = database.CreateContext(timeProvider))
        {
            var service = new LibraryService(context);

            var update = SeededLibrary();
            update.MinTracksPerRealAlbum = 3;
            update.AlbumPolicy = AlbumPolicy.SinglesOnly;
            update.Layout = LibraryLayout.ArtistAlbum;
            update.PlexSectionId = "3";
            update.SidecarOptions = "{\"cover\":true}";

            await service.UpdateAsync(update, CancellationToken.None);
        }

        await using var readBack = database.CreateContext(timeProvider);
        var stored = await readBack.Libraries.SingleAsync(x => x.Id == SeedData.DefaultLibraryId);

        stored.MinTracksPerRealAlbum.Should().Be(3);
        stored.AlbumPolicy.Should().Be(AlbumPolicy.SinglesOnly);
        stored.Layout.Should().Be(LibraryLayout.ArtistAlbum);
        stored.PlexSectionId.Should().Be("3");
        stored.SidecarOptions.Should().Be("{\"cover\":true}");
        stored.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task A_version_1_output_policy_is_stored_as_version_2()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using (var context = database.CreateContext(timeProvider))
        {
            var service = new LibraryService(context);

            var update = SeededLibrary();
            update.OutputPolicy = """{"codec":"mp3","mode":"vbr","vbrQuality":2}""";

            await service.UpdateAsync(update, CancellationToken.None);
        }

        await using var readBack = database.CreateContext(timeProvider);
        var stored = await readBack.Libraries.SingleAsync(x => x.Id == SeedData.DefaultLibraryId);

        // The version-1 shape is the youtube rule; the other two rules are the default keep.
        stored.OutputPolicy.Should().Be(
            """{"version":2,"youtube":{"codec":"mp3","mode":"vbr","bitrateKbps":256,"vbrQuality":2,"sampleRate":"keep"},"lossy":{"codec":"keep","mode":"cbr","bitrateKbps":256,"vbrQuality":0,"sampleRate":"keep","opusContainer":"opus"},"lossless":{"codec":"keep","mode":"cbr","bitrateKbps":256,"vbrQuality":0,"sampleRate":"keep","opusContainer":"opus"}}""");
    }

    [Fact]
    public async Task An_empty_name_is_rejected()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var service = new LibraryService(context);

        var update = SeededLibrary();
        update.Name = "   ";

        var act = async () => await service.UpdateAsync(update, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ProfileValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.Property.Should().Be("name");
    }

    [Fact]
    public async Task A_relative_root_path_is_rejected_but_a_windows_drive_is_accepted()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var service = new LibraryService(context);

        var relative = SeededLibrary();
        relative.RootPath = "music/library";

        var rejected = async () => await service.UpdateAsync(relative, CancellationToken.None);
        var exception = await rejected.Should().ThrowAsync<ProfileValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.Property.Should().Be("rootPath");

        var windows = SeededLibrary();
        windows.RootPath = @"D:\Music";

        (await service.UpdateAsync(windows, CancellationToken.None)).RootPath.Should().Be(@"D:\Music");
    }

    [Fact]
    public async Task A_naming_template_without_the_track_title_is_rejected()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var service = new LibraryService(context);

        var update = SeededLibrary();
        update.NamingTemplate = "{Album Artist Name}/{Album Title}";

        var act = async () => await service.UpdateAsync(update, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ProfileValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.Property.Should().Be("namingTemplate");
    }

    [Fact]
    public async Task A_track_threshold_outside_one_to_ten_is_rejected()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var service = new LibraryService(context);

        var update = SeededLibrary();
        update.MinTracksPerRealAlbum = 11;

        var act = async () => await service.UpdateAsync(update, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ProfileValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.Property.Should().Be("minTracksPerRealAlbum");
    }

    [Fact]
    public async Task Sidecar_options_that_are_not_a_json_object_are_rejected()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var service = new LibraryService(context);

        var update = SeededLibrary();
        update.SidecarOptions = "not json";

        var act = async () => await service.UpdateAsync(update, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ProfileValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.Property.Should().Be("sidecarOptions");
    }

    [Fact]
    public async Task Clearing_the_default_flag_on_the_only_library_is_rejected()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var service = new LibraryService(context);

        var update = SeededLibrary();
        update.IsDefault = false;

        var act = async () => await service.UpdateAsync(update, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ProfileValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.Property.Should().Be("isDefault");
    }

    [Fact]
    public async Task Making_another_library_the_default_clears_the_previous_one()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using (var context = database.CreateContext(timeProvider))
        {
            context.Libraries.Add(new Library
            {
                Name = "Archive",
                RootPath = "/data/archive",
                NamingTemplate = "{Album Artist Name}/{Album Title}/{track:00} - {Track Title}",
                Layout = LibraryLayout.ArtistAlbum,
                AlbumPolicy = AlbumPolicy.OriginalAlbum,
            });
            await context.SaveChangesAsync();
        }

        await using (var context = database.CreateContext(timeProvider))
        {
            var service = new LibraryService(context);

            var music = SeededLibrary();
            music.IsDefault = false;

            var act = async () => await service.UpdateAsync(music, CancellationToken.None);
            await act.Should().ThrowAsync<ProfileValidationException>();

            var archive = SeededLibrary();
            archive.Id = 2;
            archive.Name = "Archive";
            archive.RootPath = "/data/archive";
            archive.IsDefault = true;

            await service.UpdateAsync(archive, CancellationToken.None);
        }

        await using var readBack = database.CreateContext(timeProvider);
        var libraries = await readBack.Libraries.OrderBy(x => x.Id).ToListAsync();

        libraries.Select(library => library.IsDefault).Should().Equal(false, true);
        (await new LibraryService(readBack).GetDefaultAsync(CancellationToken.None))!.Name.Should().Be("Archive");
    }

    /// <summary>The seeded library with its stored values, ready to be edited.</summary>
    private static Library SeededLibrary()
    {
        var seed = SeedData.Libraries[0];

        return new Library
        {
            Id = seed.Id,
            Name = seed.Name,
            RootPath = seed.RootPath,
            Layout = seed.Layout,
            NamingTemplate = seed.NamingTemplate,
            SidecarOptions = seed.SidecarOptions,
            AlbumPolicy = seed.AlbumPolicy,
            MinTracksPerRealAlbum = seed.MinTracksPerRealAlbum,
            PlexSectionId = seed.PlexSectionId,
            IsDefault = seed.IsDefault,
        };
    }
}
