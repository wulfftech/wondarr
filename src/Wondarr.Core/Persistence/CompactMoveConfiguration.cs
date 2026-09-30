using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Wondarr.Core.Domain;

namespace Wondarr.Core.Persistence;

/// <summary>Maps the <c>compact_move</c> table.</summary>
public sealed class CompactMoveConfiguration : IEntityTypeConfiguration<CompactMoveRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CompactMoveRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("compact_move");

        // Stored as text so the rows stay readable and a renumbered enum cannot rewrite history.
        builder.Property(x => x.State).HasConversion<string>();
        builder.Property(x => x.Proposed).IsRequired().HasDefaultValue("{}");

        // Every run reads the library's unfinished moves; the resume check and the plan both do.
        builder.HasIndex(x => new { x.LibraryId, x.State });
    }
}
