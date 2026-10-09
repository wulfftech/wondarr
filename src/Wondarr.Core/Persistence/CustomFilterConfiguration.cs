using Wondarr.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Wondarr.Core.Persistence;

/// <summary>Maps the <c>custom_filter</c> table, kept beside the other entity configurations.</summary>
internal sealed class CustomFilterConfiguration : IEntityTypeConfiguration<CustomFilter>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CustomFilter> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("custom_filter");
        builder.Property(x => x.Type).IsRequired();
        builder.Property(x => x.Label).IsRequired();
        builder.Property(x => x.Filters).IsRequired().HasDefaultValue("[]");
        builder.HasIndex(x => new { x.Type, x.Label }).IsUnique();
    }
}
