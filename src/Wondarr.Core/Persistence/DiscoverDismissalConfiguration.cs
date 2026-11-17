using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wondarr.Core.Discover;

namespace Wondarr.Core.Persistence;

/// <summary>Maps the <c>discover_dismissal</c> table.</summary>
internal sealed class DiscoverDismissalConfiguration : IEntityTypeConfiguration<DiscoverDismissal>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DiscoverDismissal> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("discover_dismissal");
        builder.Property(x => x.ArtistKey).IsRequired();
        builder.Property(x => x.TitleKey).IsRequired();
        builder.HasIndex(x => x.MbRecordingId);
        builder.HasIndex(x => new { x.ArtistKey, x.TitleKey });
    }
}
