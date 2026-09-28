using Compilarr.Core.Domain;
using Compilarr.Core.Persistence;
using Compilarr.Core.Profiles;
using Compilarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Compilarr.Core.Tests.Profiles;

/// <summary>
/// The quality profile rules against a real migrated SQLite database, so the seeded ladder the
/// validation reads is the one an install has.
/// </summary>
public class QualityProfileServiceTests
{
    [Fact]
    public async Task Listing_profiles_returns_the_seeded_two_ordered_by_id()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var service = new QualityProfileService(context);

        var profiles = await service.GetAllAsync(CancellationToken.None);

        profiles.Select(profile => profile.Name).Should().Equal("Standard 320", "Lossless");
        profiles[0].CutoffQualityId.Should().Be(29);
        profiles[1].CutoffQualityId.Should().Be(36);
    }

    [Fact]
    public async Task Reading_an_unknown_profile_returns_null()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var service = new QualityProfileService(context);

        (await service.GetAsync(987_654, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Adding_a_profile_stores_its_items_as_json()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        long id;
        await using (var context = database.CreateContext(timeProvider))
        {
            var service = new QualityProfileService(context);

            var added = await service.AddAsync(CopyOfStandard("Test"), CancellationToken.None);
            id = added.Id;
            id.Should().Be(3);
        }

        await using var readBack = database.CreateContext(timeProvider);
        var stored = await readBack.QualityProfiles.SingleAsync(x => x.Id == id);

        stored.Name.Should().Be("Test");
        stored.Items.Should().HaveCount(13);
        stored.Items[7].QualityIds.Should().Equal(29L, 30L, 25L, 31L, 32L);
    }

    [Fact]
    public async Task Adding_a_profile_whose_name_differs_only_in_case_is_rejected()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var service = new QualityProfileService(context);

        var act = async () => await service.AddAsync(CopyOfStandard("standard 320"), CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ProfileValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.Property.Should().Be("name");
    }

    [Fact]
    public async Task Adding_a_profile_that_repeats_a_quality_is_rejected()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var service = new QualityProfileService(context);

        var profile = CopyOfStandard("Test");
        profile.Items[0].QualityIds.Add(29);

        var act = async () => await service.AddAsync(profile, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ProfileValidationException>();
        exception.Which.Errors.Should().OnlyContain(error => error.Property == "items");
        exception.Which.Errors.Should().Contain(error => error.Message.Contains("listed 2 times"));
    }

    [Fact]
    public async Task Adding_a_profile_that_omits_a_quality_is_rejected()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var service = new QualityProfileService(context);

        var profile = CopyOfStandard("Test");
        profile.Items[^1].QualityIds.Remove(43);

        var act = async () => await service.AddAsync(profile, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ProfileValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.Message.Should().Contain("1 qualities are missing from the profile (43)");
    }

    [Fact]
    public async Task Adding_a_profile_that_names_an_unknown_quality_is_rejected()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var service = new QualityProfileService(context);

        var profile = CopyOfStandard("Test");
        profile.Items[^1].QualityIds.Remove(43);
        profile.Items[^1].QualityIds.Add(99);

        var act = async () => await service.AddAsync(profile, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ProfileValidationException>();
        exception.Which.Errors.Should().Contain(error => error.Message == "Unknown quality id 99.");
    }

    [Fact]
    public async Task Adding_a_profile_whose_cutoff_is_not_allowed_is_rejected()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var service = new QualityProfileService(context);

        var profile = CopyOfStandard("Test");
        profile.CutoffQualityId = 26;

        var act = async () => await service.AddAsync(profile, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ProfileValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.Property.Should().Be("cutoff");
    }

    [Fact]
    public async Task Adding_a_profile_without_an_allowed_item_is_rejected()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var service = new QualityProfileService(context);

        var profile = CopyOfStandard("Test");
        profile.Items = [.. profile.Items.Select(item => item with { Allowed = false })];

        var act = async () => await service.AddAsync(profile, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ProfileValidationException>();
        exception.Which.Errors.Should().Contain(error => error.Message == "At least one item must be allowed.");
    }

    [Fact]
    public async Task Adding_a_profile_with_a_number_out_of_range_is_rejected()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var service = new QualityProfileService(context);

        var profile = CopyOfStandard("Test");
        profile.MinScore = 1001;
        profile.DurationToleranceMs = 60_001;

        var act = async () => await service.AddAsync(profile, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ProfileValidationException>();
        exception.Which.Errors.Select(error => error.Property)
            .Should().Equal("minScore", "durationToleranceMs");
    }

    [Fact]
    public async Task Updating_replaces_the_stored_values()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using (var context = database.CreateContext(timeProvider))
        {
            var service = new QualityProfileService(context);

            var seeded = await service.GetAsync(SeedData.LosslessProfileId, CancellationToken.None);
            seeded.Should().NotBeNull();

            var updated = CopyOfStandard(seeded!.Name);
            updated.Id = seeded.Id;
            updated.MinScore = 42;

            await service.UpdateAsync(updated, CancellationToken.None);
        }

        await using var readBack = database.CreateContext(timeProvider);
        var stored = await readBack.QualityProfiles.SingleAsync(x => x.Id == SeedData.LosslessProfileId);

        stored.MinScore.Should().Be(42);
        stored.CutoffQualityId.Should().Be(29);
        stored.Items.Should().HaveCount(13);
    }

    [Fact]
    public async Task Updating_a_profile_onto_another_name_is_rejected()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var service = new QualityProfileService(context);

        var profile = CopyOfStandard("Standard 320");
        profile.Id = SeedData.LosslessProfileId;

        var act = async () => await service.UpdateAsync(profile, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ProfileValidationException>();
        exception.Which.Errors.Should().ContainSingle()
            .Which.Property.Should().Be("name");
    }

    [Fact]
    public async Task Deleting_a_profile_a_song_uses_is_rejected()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await AddSongAsync(database, timeProvider, SeedData.StandardProfileId);

        await using var context = database.CreateContext(timeProvider);
        var service = new QualityProfileService(context);

        var act = async () => await service.DeleteAsync(SeedData.StandardProfileId, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ProfileInUseException>();
        exception.Which.Message.Should().Contain("still used by a song");
    }

    [Fact]
    public async Task Deleting_the_last_profile_is_rejected()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var service = new QualityProfileService(context);

        (await service.DeleteAsync(SeedData.LosslessProfileId, CancellationToken.None)).Should().BeTrue();

        var act = async () => await service.DeleteAsync(SeedData.StandardProfileId, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<ProfileInUseException>();
        exception.Which.Message.Should().Contain("last quality profile");
    }

    [Fact]
    public async Task Deleting_an_unknown_profile_returns_false()
    {
        using var database = new SqliteTestDatabase();
        var timeProvider = new FakeTimeProvider();
        await database.MigrateAsync(timeProvider);

        await using var context = database.CreateContext(timeProvider);
        var service = new QualityProfileService(context);

        (await service.DeleteAsync(987_654, CancellationToken.None)).Should().BeFalse();
    }

    /// <summary>The seeded "Standard 320" under another name: valid, and complete over the ladder.</summary>
    private static QualityProfile CopyOfStandard(string name)
    {
        var seed = SeedData.QualityProfiles[0];

        return new QualityProfile
        {
            Name = name,
            UpgradeAllowed = seed.UpgradeAllowed,
            CutoffQualityId = seed.CutoffQualityId,
            MinScore = seed.MinScore,
            DurationToleranceMs = seed.DurationToleranceMs,
            Items =
            [
                .. seed.Items.Select(item => new QualityProfileItem(item.Name, [.. item.QualityIds], item.Allowed))
            ],
        };
    }

    /// <summary>Inserts a song that uses <paramref name="profileId"/>, the way the add path does.</summary>
    private static async Task AddSongAsync(SqliteTestDatabase database, TimeProvider timeProvider, long profileId)
    {
        await using var context = database.CreateContext(timeProvider);

        context.Songs.Add(new Song
        {
            Title = "Get Lucky",
            ArtistCredit = "Daft Punk",
            PrimaryArtist = new Artist { Name = "Daft Punk", SortName = "Daft Punk" },
            QualityProfileId = profileId,
            LibraryId = SeedData.DefaultLibraryId,
            AddedBy = "api",
        });

        await context.SaveChangesAsync();
    }
}