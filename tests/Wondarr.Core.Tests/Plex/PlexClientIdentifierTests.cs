using Wondarr.Core.Persistence;
using Wondarr.Core.Plex;
using Wondarr.Core.Tests.Persistence;
using FluentAssertions;
using Xunit;

namespace Wondarr.Core.Tests.Plex;

/// <summary>
/// The identifier is cached process-wide, so these tests and the connection service's share one
/// piece of static state and must not run beside one another.
/// </summary>
[Collection("plex-client-identifier")]
public class PlexClientIdentifierTests
{
    [Fact]
    public async Task Eight_first_uses_at_once_generate_one_identifier_and_store_it_once()
    {
        using var database = new SqliteTestDatabase();
        await database.MigrateAsync(TimeProvider.System);

        var contexts = Enumerable
            .Range(0, 8)
            .Select(_ => database.CreateContext(TimeProvider.System))
            .ToList();

        try
        {
            // Eight scopes, each with its own settings repository over the one database, sharing the
            // app's one state as the container gives it to them.
            var state = new PlexClientIdentifierState();
            var identifiers = contexts
                .Select(context => new PlexClientIdentifier(new SettingsRepository(context), state))
                .ToArray();

            var values = await Task.WhenAll(identifiers.Select(
                identifier => Task.Run(() => identifier.GetAsync(CancellationToken.None))));

            values.Should().HaveCount(8);
            values.Distinct(StringComparer.Ordinal).Should().ContainSingle();
            values[0].Should().HaveLength(32).And.MatchRegex("^[0-9a-f]{32}$");

            var rows = await database.ReadStringsAsync("SELECT value FROM setting WHERE key = 'plex'");

            rows.Should().ContainSingle();
            rows[0].Should().Contain(values[0]);
        }
        finally
        {
            foreach (var context in contexts)
            {
                context.Dispose();
            }
        }
    }

    [Fact]
    public async Task An_identifier_already_stored_is_kept_rather_than_replaced()
    {
        using var database = new SqliteTestDatabase();
        await database.MigrateAsync(TimeProvider.System);

        using (var context = database.CreateContext(TimeProvider.System))
        {
            await new SettingsRepository(context).SetAsync(
                PlexConnectionService.SettingKey,
                new PlexConnectionSettings { ClientIdentifier = "stored-identifier" },
                CancellationToken.None);
        }

        using var reader = database.CreateContext(TimeProvider.System);

        var value = await new PlexClientIdentifier(new SettingsRepository(reader), new PlexClientIdentifierState())
            .GetAsync(CancellationToken.None);

        value.Should().Be("stored-identifier");
    }
}
