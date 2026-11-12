using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Wayfarer.Models.Configuration;

/// <summary>Only C owns comparison/origin rows; source deletion never cascades into independent content.</summary>
public sealed class TripCompositionConfiguration : IEntityTypeConfiguration<TripComposition>, IEntityTypeConfiguration<TripOrigin>
{
    public void Configure(EntityTypeBuilder<TripComposition> row)
    {
        row.ToTable("TripCompositions");
        row.HasKey(x => x.Id);
        row.HasOne<Trip>().WithMany().HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.Cascade);
        row.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        row.HasIndex(x => x.TripId).IsUnique();
        row.HasIndex(x => new { x.SourceAId, x.SourceBId }).HasFilter("\"FinishedAt\" IS NULL");
        row.Property(x => x.SnapshotA).HasColumnType("jsonb");
        row.Property(x => x.SnapshotB).HasColumnType("jsonb");
        row.Property(x => x.Mappings).HasColumnType("jsonb");
        row.ToTable(t => t.HasCheckConstraint("CK_TripComposition_Lifecycle",
            "(\"FinishedAt\" IS NULL AND \"SnapshotA\" IS NOT NULL AND \"SnapshotB\" IS NOT NULL AND \"Mappings\" IS NOT NULL) OR " +
            "(\"FinishedAt\" IS NOT NULL AND \"SnapshotA\" IS NULL AND \"SnapshotB\" IS NULL AND \"Mappings\" IS NULL AND \"SourceAId\" IS NULL AND \"SourceBId\" IS NULL)"));
    }

    public void Configure(EntityTypeBuilder<TripOrigin> row)
    {
        row.ToTable("TripOrigins");
        row.HasKey(x => x.Id);
        row.HasOne<Trip>().WithMany().HasForeignKey(x => x.TripId).OnDelete(DeleteBehavior.Cascade);
        row.Property(x => x.ChangeNote).HasMaxLength(2000);
    }
}
