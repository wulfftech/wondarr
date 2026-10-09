using System.Text.Json;
using Wondarr.Core.CustomFilters;
using Wondarr.Core.Persistence;
using Wondarr.Core.Tests.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Wondarr.Core.Tests.CustomFilters;

/// <summary>Saved views against a real migrated SQLite database.</summary>
public sealed class CustomFilterServiceTests : IDisposable
{
    private const string OneFilter = """[{"key":"monitored","value":[true],"type":"equal"}]""";

    private readonly SqliteTestDatabase _database = new();
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public async Task The_migration_creates_the_table_on_a_fresh_database()
    {
        await _database.MigrateAsync(_time);

        var tables = await _database.ReadStringsAsync("SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'custom_filter'");
        var indexes = await _database.ReadStringsAsync("SELECT name FROM sqlite_master WHERE type = 'index' AND name = 'ix_custom_filter_type_label'");

        tables.Should().Equal("custom_filter");
        indexes.Should().Equal("ix_custom_filter_type_label");
    }

    [Fact]
    public async Task A_view_is_created_read_listed_updated_and_deleted()
    {
        await using var context = await ContextAsync();
        var service = new CustomFilterService(context);

        var created = await service.CreateAsync(Draft("library", " Unmonitored ", OneFilter), CancellationToken.None);
        created.Label.Should().Be("Unmonitored");
        created.Filters.Should().Be(OneFilter);

        (await service.GetAsync(created.Id, CancellationToken.None))!.Label.Should().Be("Unmonitored");
        await service.CreateAsync(Draft("other", "Z", "[]"), CancellationToken.None);
        (await service.ListAsync(null, CancellationToken.None)).Should().HaveCount(2);
        (await service.ListAsync("library", CancellationToken.None)).Should().ContainSingle();

        var updated = await service.UpdateAsync(created.Id, Draft("library", "Unmonitored", "[]"), CancellationToken.None);
        updated!.Filters.Should().Be("[]");

        (await service.UpdateAsync(999, Draft("library", "x", "[]"), CancellationToken.None)).Should().BeNull();
        (await service.DeleteAsync(created.Id, CancellationToken.None)).Should().BeTrue();
        (await service.DeleteAsync(created.Id, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task A_duplicate_label_for_the_same_type_conflicts_but_another_type_does_not()
    {
        await using var context = await ContextAsync();
        var service = new CustomFilterService(context);

        await service.CreateAsync(Draft("library", "Mine", "[]"), CancellationToken.None);
        await service.CreateAsync(Draft("other", "Mine", "[]"), CancellationToken.None);
        var second = await service.CreateAsync(Draft("library", "Yours", "[]"), CancellationToken.None);

        var dup = () => service.CreateAsync(Draft("library", "Mine", "[]"), CancellationToken.None);
        await dup.Should().ThrowAsync<CustomFilterConflictException>();

        var rename = () => service.UpdateAsync(second.Id, Draft("library", "Mine", "[]"), CancellationToken.None);
        await rename.Should().ThrowAsync<CustomFilterConflictException>();

        // Saving a view under its own label is not a conflict.
        (await service.UpdateAsync(second.Id, Draft("library", "Yours", OneFilter), CancellationToken.None)).Should().NotBeNull();
    }

    [Theory]
    [InlineData("""{"key":"a"}""")]
    [InlineData("""[1]""")]
    [InlineData("""[{"value":1}]""")]
    [InlineData("""[{"key":1}]""")]
    [InlineData("\"text\"")]
    public async Task Filters_that_are_not_an_array_of_keyed_objects_are_refused(string filters)
    {
        await using var context = await ContextAsync();
        var service = new CustomFilterService(context);

        var act = () => service.CreateAsync(Draft("library", "Bad", filters), CancellationToken.None);

        await act.Should().ThrowAsync<CustomFilterValidationException>();
    }

    [Fact]
    public async Task Too_many_entries_too_large_or_missing_fields_are_refused()
    {
        await using var context = await ContextAsync();
        var service = new CustomFilterService(context);

        var many = "[" + string.Join(',', Enumerable.Repeat("""{"key":"a"}""", 21)) + "]";
        var large = """[{"key":"a","value":"%s"}]""".Replace("%s", new string('x', 9000), StringComparison.Ordinal);

        foreach (var filters in new[] { many, large })
        {
            var act = () => service.CreateAsync(Draft("library", "Bad", filters), CancellationToken.None);
            await act.Should().ThrowAsync<CustomFilterValidationException>();
        }

        var noLabel = () => service.CreateAsync(Draft("library", "  ", "[]"), CancellationToken.None);
        await noLabel.Should().ThrowAsync<CustomFilterValidationException>();

        var longLabel = () => service.CreateAsync(Draft("library", new string('x', 101), "[]"), CancellationToken.None);
        await longLabel.Should().ThrowAsync<CustomFilterValidationException>();

        var noType = () => service.CreateAsync(Draft(null, "Label", "[]"), CancellationToken.None);
        await noType.Should().ThrowAsync<CustomFilterValidationException>();

        var undefined = () => service.CreateAsync(new CustomFilterDraft("library", "Label", default), CancellationToken.None);
        await undefined.Should().ThrowAsync<CustomFilterValidationException>();
    }

    public void Dispose()
    {
        _database.Dispose();
        GC.SuppressFinalize(this);
    }

    private static CustomFilterDraft Draft(string? type, string label, string filters) =>
        new(type, label, JsonDocument.Parse(filters).RootElement.Clone());

    private async Task<WondarrDbContext> ContextAsync()
    {
        await _database.MigrateAsync(_time);

        return _database.CreateContext(_time);
    }
}
