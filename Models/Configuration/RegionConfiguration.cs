using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Wayfarer.Models.Configuration;

/// <summary>Enforces exact canonical fallback identity within each Trip, regardless of order or owner.</summary>
public sealed class RegionConfiguration : IEntityTypeConfiguration<Region>
{
    /// <summary>The sole constraint eligible for missing-fallback Place recovery.</summary>
    public const string UnassignedIndex = "UX_Regions_Trip_UnassignedPlaces";

    /// <summary>Leaves ordinary and near-name Regions unrestricted.</summary>
    public void Configure(EntityTypeBuilder<Region> builder) => builder
        .HasIndex(region => region.TripId, UnassignedIndex)
        .HasDatabaseName(UnassignedIndex)
        .IsUnique()
        .HasFilter("\"Name\" = 'Unassigned Places'");
}
