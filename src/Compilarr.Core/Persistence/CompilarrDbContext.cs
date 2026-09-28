using Microsoft.EntityFrameworkCore;

namespace Compilarr.Core.Persistence;

/// <summary>
/// The Compilarr SQLite database. Table and column names are snake_case: build the options with
/// <c>UseSnakeCaseNamingConvention()</c> (see <c>AddCompilarrPersistence</c>).
/// </summary>
public sealed class CompilarrDbContext : DbContext
{
    private readonly TimeProvider _timeProvider;

    /// <summary>Initialises a new instance of the <see cref="CompilarrDbContext"/> class.</summary>
    public CompilarrDbContext(DbContextOptions<CompilarrDbContext> options, TimeProvider timeProvider)
        : base(options)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>Gets the key/value settings table.</summary>
    public DbSet<Setting> Settings => Set<Setting>();

    /// <summary>Gets the scheduler job table.</summary>
    public DbSet<Job> Jobs => Set<Job>();

    /// <summary>
    /// SQLite has no date type, so values come back with <see cref="DateTimeKind.Unspecified"/>.
    /// Everything is stored as UTC; mark it so on the way out to keep arithmetic with
    /// <see cref="TimeProvider"/> values correct.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcDateTimeConverter>();
    }

    /// <summary>Maps the Phase 0 tables.</summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Setting>(entity =>
        {
            entity.ToTable("setting");
            entity.Property(x => x.Key).IsRequired();
            entity.Property(x => x.Value).IsRequired();
            entity.HasIndex(x => x.Key).IsUnique();
        });

        modelBuilder.Entity<Job>(entity =>
        {
            entity.ToTable("job");
            entity.Property(x => x.Name).IsRequired();
            entity.HasIndex(x => x.Name).IsUnique();
        });
    }

    /// <inheritdoc />
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTimestamps();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc />
    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        StampTimestamps();
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);
    }

    private void StampTimestamps()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        foreach (var entry in ChangeTracker.Entries<EntityBase>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedAt = now;
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    entry.Property(nameof(EntityBase.CreatedAt)).IsModified = false;
                    break;
                default:
                    break;
            }
        }
    }
}

/// <summary>Stores <see cref="DateTime"/> unchanged and reads it back as UTC.</summary>
internal sealed class UtcDateTimeConverter : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter()
        : base(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc))
    {
    }
}
