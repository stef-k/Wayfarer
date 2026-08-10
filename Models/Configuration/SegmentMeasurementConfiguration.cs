using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Wayfarer.Models.Configuration;

/// <summary>Maps the persisted segment measurement-provenance contract.</summary>
public sealed class SegmentMeasurementConfiguration : IEntityTypeConfiguration<Segment>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Segment> builder)
    {
        builder.Property(segment => segment.EstimatedDurationSource)
            .HasConversion<int>()
            .HasDefaultValue(EstimatedDurationSource.Automatic)
            .IsRequired();

        builder.ToTable(table => table.HasCheckConstraint(
            "CK_Segments_EstimatedDurationSource",
            "\"EstimatedDurationSource\" IN (0, 1)"));
    }
}
