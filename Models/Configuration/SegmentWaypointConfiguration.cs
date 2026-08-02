using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Wayfarer.Models.Configuration;

/// <summary>Configures ordered waypoint persistence and database-enforceable row invariants.</summary>
public sealed class SegmentWaypointConfiguration : IEntityTypeConfiguration<SegmentWaypoint>
{
    /// <summary>Configures keys, relationships, checks, and deterministic lookup indexes.</summary>
    public void Configure(EntityTypeBuilder<SegmentWaypoint> waypoint)
    {
        waypoint.ToTable("SegmentWaypoints", table =>
        {
            table.HasCheckConstraint("CK_SegmentWaypoint_Position", "\"Position\" >= 0");
            table.HasCheckConstraint(
                "CK_SegmentWaypoint_RouteVertexIndex",
                "\"RouteVertexIndex\" IS NULL OR \"RouteVertexIndex\" > 0");
        });

        waypoint.HasKey(item => new { item.SegmentId, item.PlaceId });

        waypoint.HasOne(item => item.Segment)
            .WithMany(segment => segment.Waypoints)
            .HasForeignKey(item => item.SegmentId)
            .OnDelete(DeleteBehavior.Cascade);

        // A waypoint association cannot own or delete its canonical saved place.
        waypoint.HasOne(item => item.Place)
            .WithMany()
            .HasForeignKey(item => item.PlaceId)
            .OnDelete(DeleteBehavior.Restrict);

        waypoint.HasIndex(item => new { item.SegmentId, item.Position })
            .IsUnique()
            .HasDatabaseName("IX_SegmentWaypoints_SegmentId_Position");

        waypoint.HasIndex(item => new { item.SegmentId, item.RouteVertexIndex })
            .IsUnique()
            .HasFilter("\"RouteVertexIndex\" IS NOT NULL")
            .HasDatabaseName("IX_SegmentWaypoints_SegmentId_RouteVertexIndex");
    }
}
