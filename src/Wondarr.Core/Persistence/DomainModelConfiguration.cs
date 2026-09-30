using System.Text.Json;
using System.Text.Json.Serialization;
using Wondarr.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Wondarr.Core.Persistence;

/// <summary>
/// Maps the Phase 1 domain tables: artists, songs and their credits, album contexts, files, the
/// seeded quality ladder, quality profiles and libraries. Kept apart from <see cref="WondarrDbContext"/>
/// so the context stays a readable list of tables.
/// </summary>
internal static class DomainModelConfiguration
{
    /// <summary>camelCase, matching the profile JSON the API and the UI exchange.</summary>
    private static readonly JsonSerializerOptions ProfileItemsJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// <c>Items</c> is a JSON text column, so the default reference comparer would miss an edit that
    /// swaps one group's qualities for another. Compare the serialized form instead.
    /// </summary>
    private static readonly ValueComparer<List<QualityProfileItem>> ProfileItemsComparer = new(
        (left, right) => SerializeItems(left) == SerializeItems(right),
        items => SerializeItems(items).GetHashCode(StringComparison.Ordinal),
        items => DeserializeItems(SerializeItems(items)));

    /// <summary>
    /// The search columns hold plain lists (<c>sources</c>, <c>queries</c>, <c>recent_failures</c>).
    /// UTC is forced on the way in: the column is text, so a timestamp must come back as the same instant.
    /// </summary>
    private static readonly JsonSerializerOptions CollectionJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new UtcDateTimeJsonConverter() },
    };

    private static readonly ValueComparer<List<string>> StringListComparer = new(
        (left, right) => SerializeStrings(left) == SerializeStrings(right),
        list => SerializeStrings(list).GetHashCode(StringComparison.Ordinal),
        list => DeserializeStrings(SerializeStrings(list)));

    private static readonly ValueComparer<List<DateTime>> DateTimeListComparer = new(
        (left, right) => SerializeInstants(left) == SerializeInstants(right),
        list => SerializeInstants(list).GetHashCode(StringComparison.Ordinal),
        list => DeserializeInstants(SerializeInstants(list)));

    /// <summary>Adds every domain entity to the model.</summary>
    public static void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Artist>(entity =>
        {
            entity.ToTable("artist");
            entity.Property(x => x.Name).IsRequired();
            entity.Property(x => x.SortName).IsRequired();
            entity.HasIndex(x => x.MbArtistId).IsUnique();
        });

        modelBuilder.Entity<Song>(entity =>
        {
            entity.ToTable("song");
            entity.Property(x => x.Title).IsRequired();
            entity.Property(x => x.ArtistCredit).IsRequired();
            entity.Property(x => x.AddedBy).IsRequired();

            entity.HasIndex(x => x.MbRecordingId).IsUnique();
            entity.HasIndex(x => x.DeezerId).IsUnique();

            entity.HasOne(x => x.PrimaryArtist)
                .WithMany()
                .HasForeignKey(x => x.PrimaryArtistId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<QualityProfile>()
                .WithMany()
                .HasForeignKey(x => x.QualityProfileId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<Library>()
                .WithMany()
                .HasForeignKey(x => x.LibraryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SongArtist>(entity =>
        {
            entity.ToTable("song_artist");
            entity.Property(x => x.Role).HasConversion<string>();
            entity.HasIndex(x => new { x.SongId, x.Position }).IsUnique();

            entity.HasOne(x => x.Song)
                .WithMany(x => x.Artists)
                .HasForeignKey(x => x.SongId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.Artist)
                .WithMany()
                .HasForeignKey(x => x.ArtistId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AlbumContext>(entity =>
        {
            entity.ToTable("album_context");
            entity.Property(x => x.Kind).HasConversion<string>();
            entity.Property(x => x.AlbumTitle).IsRequired();
            entity.Property(x => x.AlbumArtist).IsRequired();
            entity.Property(x => x.AlbumKey).IsRequired();

            // Only an explicit album choice pins a song; the Compact task re-plans everything else.
            entity.Property(x => x.Pinned).IsRequired().HasDefaultValue(false);

            // One album context per song, and every song in a folder shares the album key.
            entity.HasIndex(x => x.SongId).IsUnique();
            entity.HasIndex(x => x.AlbumKey);

            entity.HasOne(x => x.Song)
                .WithOne(x => x.AlbumContext)
                .HasForeignKey<AlbumContext>(x => x.SongId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SongFile>(entity =>
        {
            entity.ToTable("song_file");
            entity.Property(x => x.Path).IsRequired();
            entity.Property(x => x.Codec).IsRequired();
            entity.Property(x => x.Container).IsRequired();
            entity.Property(x => x.SourceType).IsRequired();

            // At most one file per song: "has a file" is "a song_file row exists".
            entity.HasIndex(x => x.SongId).IsUnique();

            entity.HasOne(x => x.Song)
                .WithOne(x => x.File)
                .HasForeignKey<SongFile>(x => x.SongId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.Quality)
                .WithMany()
                .HasForeignKey(x => x.QualityId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Quality>(entity =>
        {
            entity.ToTable("quality");
            entity.Property(x => x.Name).IsRequired();
            entity.Property(x => x.Group).IsRequired();
            entity.Property(x => x.Codec).IsRequired();
            entity.HasIndex(x => x.Name).IsUnique();

            entity.HasData(SeedData.Qualities);
        });

        modelBuilder.Entity<QualityProfile>(entity =>
        {
            entity.ToTable("quality_profile");
            entity.Property(x => x.Name).IsRequired();
            entity.HasIndex(x => x.Name).IsUnique();

            entity.Property(x => x.Items)
                .HasConversion(
                    items => JsonSerializer.Serialize(items, ProfileItemsJson),
                    json => JsonSerializer.Deserialize<List<QualityProfileItem>>(json, ProfileItemsJson)
                        ?? new List<QualityProfileItem>())
                .Metadata.SetValueComparer(ProfileItemsComparer);

            entity.HasOne(x => x.CutoffQuality)
                .WithMany()
                .HasForeignKey(x => x.CutoffQualityId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasData(SeedData.QualityProfiles);
        });

        modelBuilder.Entity<HistoryItem>(entity =>
        {
            entity.ToTable("history");
            entity.Property(x => x.EventType).HasConversion<string>();
            entity.Property(x => x.Data).IsRequired().HasDefaultValue("{}");

            // The wanted/history queries filter and order on these.
            entity.HasIndex(x => x.SongId);
            entity.HasIndex(x => x.CreatedAt);

            entity.HasOne(x => x.Song)
                .WithMany()
                .HasForeignKey(x => x.SongId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.Quality)
                .WithMany()
                .HasForeignKey(x => x.QualityId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BlocklistItem>(entity =>
        {
            entity.ToTable("blocklist");
            entity.Property(x => x.SourceType).IsRequired();
            entity.Property(x => x.BlocklistKey).IsRequired();
            entity.Property(x => x.Reason).IsRequired();

            // The grab path asks "is this source key blocked" on every candidate.
            entity.HasIndex(x => new { x.SourceType, x.BlocklistKey });

            // Deleting a song must not take its blocklist rows with it: the key stays blocked.
            entity.HasOne(x => x.Song)
                .WithMany()
                .HasForeignKey(x => x.SongId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ImportList>(entity =>
        {
            entity.ToTable("import_list");
            entity.Property(x => x.Type).IsRequired();
            entity.Property(x => x.Name).IsRequired();
            entity.Property(x => x.Settings).IsRequired().HasDefaultValue("{}");
            entity.Property(x => x.Policy).IsRequired();

            entity.HasOne<QualityProfile>()
                .WithMany()
                .HasForeignKey(x => x.QualityProfileId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<Library>()
                .WithMany()
                .HasForeignKey(x => x.LibraryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ImportListItem>(entity =>
        {
            entity.ToTable("import_list_item");
            entity.Property(x => x.ExternalId).IsRequired();
            entity.Property(x => x.Raw).IsRequired().HasDefaultValue("{}");
            entity.Property(x => x.State).HasConversion<string>();
            entity.Property(x => x.Candidates).IsRequired().HasDefaultValue("[]");

            // The review screen asks for one list's lines, and for the unresolved ones across lists.
            entity.HasIndex(x => x.ImportListId);
            entity.HasIndex(x => x.State);

            entity.HasOne(x => x.ImportList)
                .WithMany(x => x.Items)
                .HasForeignKey(x => x.ImportListId)
                .OnDelete(DeleteBehavior.Cascade);

            // Deleting a song must not delete the line that produced it: the line stays, skippable.
            entity.HasOne(x => x.Song)
                .WithMany()
                .HasForeignKey(x => x.SongId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<SearchRun>(entity =>
        {
            entity.ToTable("search_run");
            entity.Property(x => x.Trigger).HasConversion<string>();
            entity.Property(x => x.Outcome).HasConversion<string>();
            ConfigureJsonList(entity.Property(x => x.Sources).IsRequired(), StringListComparer);
            ConfigureJsonList(entity.Property(x => x.Queries).IsRequired(), StringListComparer);

            // Backoff asks for a song's recent runs; the history screen lists the latest ones.
            entity.HasIndex(x => x.SongId);
            entity.HasIndex(x => x.StartedAt);

            entity.HasOne(x => x.Song)
                .WithMany()
                .HasForeignKey(x => x.SongId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CandidateRecord>(entity =>
        {
            entity.ToTable("candidate");
            entity.Property(x => x.SourceType).IsRequired();
            entity.Property(x => x.BlocklistKey).IsRequired();
            entity.Property(x => x.DisplayName).IsRequired();
            entity.Property(x => x.RemotePath).IsRequired();
            entity.Property(x => x.Normalised).IsRequired().HasDefaultValue("{}");
            entity.Property(x => x.ScoreBreakdown).IsRequired().HasDefaultValue("{}");
            entity.Property(x => x.Rejections).IsRequired().HasDefaultValue("[]");

            // The run's own list, the song's history, and "have we already refused this exact file".
            entity.HasIndex(x => x.SearchRunId);
            entity.HasIndex(x => x.SongId);
            entity.HasIndex(x => new { x.SongId, x.BlocklistKey });

            entity.HasOne(x => x.SearchRun)
                .WithMany()
                .HasForeignKey(x => x.SearchRunId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.Song)
                .WithMany()
                .HasForeignKey(x => x.SongId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne<Quality>()
                .WithMany()
                .HasForeignKey(x => x.QualityId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<QueueItem>(entity =>
        {
            entity.ToTable("queue_item");
            entity.Property(x => x.SourceType).IsRequired();
            entity.Property(x => x.Destination).IsRequired();
            entity.Property(x => x.State).HasConversion<string>();

            // A song may have at most one download in flight. The pre-checks in the search service are a
            // fast path; this partial unique index is what actually enforces it, so two searches racing
            // for the same song cannot both queue a grab. The filter names the active states exactly as
            // EF stores the enum — as text, one name per state.
            entity.HasIndex(x => x.SongId)
                .IsUnique()
                .HasFilter("state IN ('Queued', 'RemotelyQueued', 'Downloading', 'Completed', 'Importing')");

            // The queue screen filters by state and the poll resolves a source row by its handle.
            entity.HasIndex(x => x.State);
            entity.HasIndex(x => new { x.SourceType, x.Handle });

            entity.HasOne(x => x.Song)
                .WithMany()
                .HasForeignKey(x => x.SongId)
                .OnDelete(DeleteBehavior.Cascade);

            // A queue item is evidence of a candidate: deleting the candidate must not delete history.
            entity.HasOne(x => x.Candidate)
                .WithMany()
                .HasForeignKey(x => x.CandidateId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<SearchRun>()
                .WithMany()
                .HasForeignKey(x => x.SearchRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SoulseekUser>(entity =>
        {
            entity.ToTable("soulseek_user");
            entity.Property(x => x.Username).IsRequired().UseCollation("NOCASE");
            ConfigureJsonList(entity.Property(x => x.RecentFailures).IsRequired(), DateTimeListComparer);
            entity.HasIndex(x => x.Username).IsUnique();
        });

        modelBuilder.Entity<Library>(entity =>
        {
            entity.ToTable("library");
            entity.Property(x => x.Name).IsRequired();
            entity.Property(x => x.RootPath).IsRequired();
            entity.Property(x => x.NamingTemplate).IsRequired();
            entity.Property(x => x.Layout).HasConversion<string>();
            entity.Property(x => x.AlbumPolicy).HasConversion<string>();
            entity.Property(x => x.SidecarOptions).HasDefaultValue("{}");
            entity.HasIndex(x => x.Name).IsUnique();

            entity.HasData(SeedData.Libraries);
        });

        modelBuilder.Entity<ReferenceLibrary>(entity =>
        {
            entity.ToTable("reference_library");
            entity.Property(x => x.Name).IsRequired().UseCollation("NOCASE");
            entity.Property(x => x.RootPath).IsRequired();
            entity.Property(x => x.Mode).HasConversion<string>().HasDefaultValue(ReferenceLibraryMode.Reference);
            entity.Property(x => x.Enabled).HasDefaultValue(true);
            entity.HasIndex(x => x.Name).IsUnique();

            entity.HasOne(x => x.Library)
                .WithMany()
                .HasForeignKey(x => x.LibraryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ReferenceFile>(entity =>
        {
            entity.ToTable("reference_file");
            entity.Property(x => x.RelativePath).IsRequired();
            entity.Property(x => x.State).HasConversion<string>();

            // One row per file: the pair is what makes the walk incremental.
            entity.HasIndex(x => new { x.ReferenceLibraryId, x.RelativePath }).IsUnique();
            entity.HasIndex(x => x.State);
            entity.HasIndex(x => x.SongId);

            entity.HasOne(x => x.ReferenceLibrary)
                .WithMany()
                .HasForeignKey(x => x.ReferenceLibraryId)
                .OnDelete(DeleteBehavior.Cascade);

            // Deleting a song must not delete the file that is on disk: the row stays, unidentified.
            entity.HasOne(x => x.Song)
                .WithMany()
                .HasForeignKey(x => x.SongId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<MatchCandidate>(entity =>
        {
            entity.ToTable("match_candidate");
            entity.Property(x => x.Identity).IsRequired().HasDefaultValue("{}");
            entity.Property(x => x.Reason).IsRequired();
            entity.HasIndex(x => x.ReferenceFileId);

            entity.HasOne(x => x.ReferenceFile)
                .WithMany(x => x.Candidates)
                .HasForeignKey(x => x.ReferenceFileId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static string SerializeItems(List<QualityProfileItem>? items) =>
        JsonSerializer.Serialize(items, ProfileItemsJson);

    private static List<QualityProfileItem> DeserializeItems(string json) =>
        JsonSerializer.Deserialize<List<QualityProfileItem>>(json, ProfileItemsJson) ?? [];

    /// <summary>
    /// Stores a list as JSON text. The default reference comparer would miss an edit that swaps one
    /// list for another of equal length, so the serialized form is compared instead.
    /// </summary>
    private static void ConfigureJsonList<T>(PropertyBuilder<T> property, ValueComparer<T> comparer)
        where T : class
    {
        property.HasConversion(
            value => JsonSerializer.Serialize(value, CollectionJson),
            json => JsonSerializer.Deserialize<T>(json, CollectionJson)!);

        property.Metadata.SetValueComparer(comparer);
    }

    private static string SerializeStrings(List<string>? list) =>
        JsonSerializer.Serialize(list, CollectionJson);

    private static List<string> DeserializeStrings(string json) =>
        JsonSerializer.Deserialize<List<string>>(json, CollectionJson) ?? [];

    private static string SerializeInstants(List<DateTime>? list) =>
        JsonSerializer.Serialize(list, CollectionJson);

    private static List<DateTime> DeserializeInstants(string json) =>
        JsonSerializer.Deserialize<List<DateTime>>(json, CollectionJson) ?? [];
}

/// <summary>
/// Reads a JSON timestamp back as UTC. SQLite stores the column as text, so without this the instants
/// in a JSON list would come back <see cref="DateTimeKind.Unspecified"/> and stop comparing correctly
/// with <see cref="TimeProvider"/> values.
/// </summary>
internal sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
{
    /// <inheritdoc />
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTime.SpecifyKind(reader.GetDateTime(), DateTimeKind.Utc);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime());
    }
}
