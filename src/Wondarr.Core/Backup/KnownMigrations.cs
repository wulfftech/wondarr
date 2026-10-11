using System.Reflection;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Wondarr.Core.Backup;

/// <summary>
/// The ids of every EF Core migration this build carries, read from the assembly so that a backup
/// can be checked without a database context (R-039).
/// </summary>
public static class KnownMigrations
{
    /// <summary>Every migration id this build knows, such as <c>20260928040822_InitialCreate</c>.</summary>
    public static IReadOnlySet<string> Ids { get; } = typeof(KnownMigrations).Assembly
        .GetTypes()
        .Select(type => type.GetCustomAttribute<MigrationAttribute>()?.Id)
        .OfType<string>()
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>The newest of <paramref name="applied"/> that this build does not know, or <see langword="null"/>.</summary>
    /// <param name="applied">Migration ids recorded in a database's history.</param>
    /// <returns>The unknown id with the highest timestamp, or <see langword="null"/> when every id is known.</returns>
    public static string? NewestUnknown(IEnumerable<string> applied)
    {
        ArgumentNullException.ThrowIfNull(applied);

        // Ids start with a timestamp, so the ordinal maximum is the newest.
        return applied
            .Where(id => !Ids.Contains(id))
            .Max(StringComparer.Ordinal);
    }
}
