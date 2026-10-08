using Wondarr.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Wondarr.Core.Persistence;

/// <summary>Maps the <c>indexer</c> table, kept beside the other entity configurations.</summary>
internal sealed class IndexerConfiguration : IEntityTypeConfiguration<Indexer>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Indexer> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("indexer");
        builder.Property(x => x.Name).IsRequired();
        builder.Property(x => x.Type).IsRequired();

        // Stored as its name so the rows stay readable and a renumbered enum cannot rewrite them.
        builder.Property(x => x.Protocol).HasConversion<string>().IsRequired();
        builder.Property(x => x.Settings).IsRequired().HasDefaultValue("{}");
        builder.Property(x => x.Enabled).HasDefaultValue(true);
        builder.Property(x => x.Priority).HasDefaultValue(25);

        // An indexer may name the client its grabs go to. Restrict, not Cascade: deleting a client
        // an indexer names is the user's decision, never a side effect.
        builder.HasOne<DownloadClient>()
            .WithMany()
            .HasForeignKey(x => x.DownloadClientId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
