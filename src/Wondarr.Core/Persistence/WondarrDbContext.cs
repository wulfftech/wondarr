using Wondarr.Core.Domain;
using Wondarr.Core.Jobs;
using Wondarr.Core.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Wondarr.Core.Persistence;

/// <summary>
/// The Wondarr SQLite database. Table and column names are snake_case: build the options with
/// <c>UseSnakeCaseNamingConvention()</c> (see <c>AddWondarrPersistence</c>).
/// </summary>
public sealed class WondarrDbContext : DbContext
{
    private readonly TimeProvider _timeProvider;

    /// <summary>Initialises a new instance of the <see cref="WondarrDbContext"/> class.</summary>
    public WondarrDbContext(DbContextOptions<WondarrDbContext> options, TimeProvider timeProvider)
        : base(options)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>Gets the key/value settings table.</summary>
    public DbSet<Setting> Settings => Set<Setting>();

    /// <summary>Gets the scheduler job table.</summary>
    public DbSet<Job> Jobs => Set<Job>();

    /// <summary>Gets the command queue table.</summary>
    public DbSet<CommandRecord> Commands => Set<CommandRecord>();

    /// <summary>Gets the artist table.</summary>
    public DbSet<Artist> Artists => Set<Artist>();

    /// <summary>Gets the song table.</summary>
    public DbSet<Song> Songs => Set<Song>();

    /// <summary>Gets the song-to-artist credit table.</summary>
    public DbSet<SongArtist> SongArtists => Set<SongArtist>();

    /// <summary>Gets the album context table.</summary>
    public DbSet<AlbumContext> AlbumContexts => Set<AlbumContext>();

    /// <summary>Gets the imported file table.</summary>
    public DbSet<SongFile> SongFiles => Set<SongFile>();

    /// <summary>Gets the seeded quality ladder.</summary>
    public DbSet<Quality> Qualities => Set<Quality>();

    /// <summary>Gets the quality profile table.</summary>
    public DbSet<QualityProfile> QualityProfiles => Set<QualityProfile>();

    /// <summary>Gets the library table.</summary>
    public DbSet<Library> Libraries => Set<Library>();

    /// <summary>Gets the metadata provider response cache.</summary>
    public DbSet<MetadataCacheEntry> MetadataCache => Set<MetadataCacheEntry>();

    /// <summary>Gets the song lifecycle log.</summary>
    public DbSet<HistoryItem> History => Set<HistoryItem>();

    /// <summary>Gets the blocklist.</summary>
    public DbSet<BlocklistItem> Blocklist => Set<BlocklistItem>();

    /// <summary>Gets the import lists.</summary>
    public DbSet<ImportList> ImportLists => Set<ImportList>();

    /// <summary>Gets the lines of the import lists.</summary>
    public DbSet<ImportListItem> ImportListItems => Set<ImportListItem>();

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

        modelBuilder.ApplyConfiguration(new MetadataCacheEntryConfiguration());

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

        modelBuilder.Entity<CommandRecord>(entity =>
        {
            entity.ToTable("command");
            entity.Property(x => x.Name).IsRequired();

            // Stored as text so the rows stay readable and a renumbered enum cannot rewrite history.
            entity.Property(x => x.Status).HasConversion<string>();
            entity.Property(x => x.Result).HasConversion<string>();
            entity.Property(x => x.Trigger).HasConversion<string>();

            // The executor's startup sweep and the queue's duplicate check both filter on these.
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.Name);
        });

        DomainModelConfiguration.Configure(modelBuilder);
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
