using System.Text.Json;
using Wondarr.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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
    }

    private static string SerializeItems(List<QualityProfileItem>? items) =>
        JsonSerializer.Serialize(items, ProfileItemsJson);

    private static List<QualityProfileItem> DeserializeItems(string json) =>
        JsonSerializer.Deserialize<List<QualityProfileItem>>(json, ProfileItemsJson) ?? [];
}
