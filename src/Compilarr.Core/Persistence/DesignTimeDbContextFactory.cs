using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Compilarr.Core.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build a context with Compilarr.Core as both project and startup project.
/// The connection string is only used for commands that talk to a database; <c>migrations add</c> never opens it.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CompilarrDbContext>
{
    /// <inheritdoc />
    public CompilarrDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CompilarrDbContext>()
            .UseSqlite("Data Source=design.db")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new CompilarrDbContext(options, TimeProvider.System);
    }
}
