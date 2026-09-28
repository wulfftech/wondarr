using Compilarr.Core.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;

namespace Compilarr.Core.Metadata;

/// <summary>
/// One cached provider response: the raw body, keyed by the provider and the request it answers.
/// </summary>
public sealed class MetadataCacheEntry : EntityBase
{
    /// <summary>Gets or sets the provider key, for example <c>musicbrainz</c>.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Gets or sets the provider-specific cache key (the relative request URI).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the stored body, byte for byte as the provider sent it.</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>Gets or sets the UTC instant the body was fetched.</summary>
    public DateTime FetchedAt { get; set; }

    /// <summary>Gets or sets the UTC instant after which the entry reads as a miss.</summary>
    public DateTime ExpiresAt { get; set; }
}

/// <summary>Maps the <c>metadata_cache</c> table.</summary>
public sealed class MetadataCacheEntryConfiguration : IEntityTypeConfiguration<MetadataCacheEntry>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<MetadataCacheEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("metadata_cache");

        builder.Property(x => x.Provider).IsRequired();
        builder.Property(x => x.Key).IsRequired();
        builder.Property(x => x.Payload).IsRequired();

        // The cache is only ever read by (provider, key); the unique index is also what lets the
        // upsert use ON CONFLICT.
        builder.HasIndex(x => new { x.Provider, x.Key }).IsUnique();

        // Housekeeping sweeps on expiry.
        builder.HasIndex(x => x.ExpiresAt);
    }
}

/// <summary>Reads and writes cached metadata-provider responses.</summary>
public interface IMetadataCache
{
    /// <summary>Reads a cached body.</summary>
    /// <returns>The payload, or <see langword="null"/> when the entry is missing or expired.</returns>
    Task<string?> GetAsync(string provider, string key, CancellationToken cancellationToken);

    /// <summary>Stores a body, replacing any entry already held for the same key.</summary>
    Task SetAsync(string provider, string key, string payload, TimeSpan ttl, CancellationToken cancellationToken);

    /// <summary>Deletes every expired entry.</summary>
    /// <returns>The number of rows deleted.</returns>
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The SQLite-backed implementation. Registered as a singleton because the clients that use it are:
/// each call opens its own scope and <see cref="CompilarrDbContext"/>, and writes go through one
/// upsert statement so two callers racing on the same key cannot collide.
/// </summary>
public sealed class MetadataCache : IMetadataCache
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initialises a new instance of the <see cref="MetadataCache"/> class.</summary>
    public MetadataCache(IServiceScopeFactory scopeFactory, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<string?> GetAsync(string provider, string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(provider);
        ArgumentException.ThrowIfNullOrEmpty(key);

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CompilarrDbContext>();

        return await context.MetadataCache
            .AsNoTracking()
            .Where(entry => entry.Provider == provider && entry.Key == key && entry.ExpiresAt > now)
            .Select(entry => entry.Payload)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetAsync(
        string provider,
        string key,
        string payload,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(provider);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(payload);

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var expiresAt = now + ttl;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CompilarrDbContext>();

        // One statement, so concurrent writers of the same key serialise in SQLite instead of
        // reading-then-writing and losing one of the payloads. The audit columns are stamped here
        // because SaveChanges never sees these rows.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO metadata_cache (provider, key, payload, fetched_at, expires_at, created_at, updated_at)
             VALUES ({provider}, {key}, {payload}, {now}, {expiresAt}, {now}, {now})
             ON CONFLICT(provider, key) DO UPDATE SET
                 payload = excluded.payload,
                 fetched_at = excluded.fetched_at,
                 expires_at = excluded.expires_at,
                 updated_at = excluded.updated_at
             """,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CompilarrDbContext>();

        return await context.Database
            .ExecuteSqlInterpolatedAsync(
                $"DELETE FROM metadata_cache WHERE expires_at <= {now}",
                cancellationToken)
            .ConfigureAwait(false);
    }
}
