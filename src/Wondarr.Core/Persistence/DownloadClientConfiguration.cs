using Wondarr.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Wondarr.Core.Persistence;

/// <summary>Maps the <c>download_client</c> table, kept beside the other entity configurations.</summary>
internal sealed class DownloadClientConfiguration : IEntityTypeConfiguration<DownloadClient>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DownloadClient> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("download_client");
        builder.Property(x => x.Name).IsRequired();
        builder.Property(x => x.Type).IsRequired();

        // Stored as its name so the rows stay readable and a renumbered enum cannot rewrite them.
        builder.Property(x => x.Protocol).HasConversion<string>().IsRequired();
        builder.Property(x => x.Settings).IsRequired().HasDefaultValue("{}");
        builder.Property(x => x.Enabled).HasDefaultValue(true);
        builder.Property(x => x.Priority).HasDefaultValue(1);
    }
}
