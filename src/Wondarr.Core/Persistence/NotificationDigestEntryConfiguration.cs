using Wondarr.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Wondarr.Core.Persistence;

/// <summary>Maps the <c>notification_digest_entry</c> table.</summary>
internal sealed class NotificationDigestEntryConfiguration : IEntityTypeConfiguration<NotificationDigestEntry>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<NotificationDigestEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("notification_digest_entry");
        builder.Property(x => x.Event).IsRequired();
        builder.Property(x => x.Snapshot).IsRequired();
        builder.HasIndex(x => new { x.NotificationId, x.CreatedAt });
        builder.HasOne<Notification>().WithMany().HasForeignKey(x => x.NotificationId).OnDelete(DeleteBehavior.Cascade);
    }
}
