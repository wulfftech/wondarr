using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Wondarr.Core.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build a context with Wondarr.Core as both project and startup project.
/// The connection string is only used for commands that talk to a database; <c>migrations add</c> never opens it.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<WondarrDbContext>
{
    /// <inheritdoc />
    public WondarrDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<WondarrDbContext>()
            .UseSqlite("Data Source=design.db")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new WondarrDbContext(options, TimeProvider.System);
    }
}
