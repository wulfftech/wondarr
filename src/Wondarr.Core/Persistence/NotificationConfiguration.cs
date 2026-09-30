using Wondarr.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Wondarr.Core.Persistence;

/// <summary>Maps the <c>notification</c> table, kept beside the other entity configurations.</summary>
internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("notification");
        builder.Property(x => x.Name).IsRequired();
        builder.Property(x => x.Type).IsRequired();
        builder.Property(x => x.Settings).IsRequired().HasDefaultValue("{}");
        builder.Property(x => x.Events).IsRequired().HasDefaultValue("[]");
        builder.Property(x => x.Enabled).HasDefaultValue(true);
        builder.HasIndex(x => x.Name).IsUnique();
    }
}
