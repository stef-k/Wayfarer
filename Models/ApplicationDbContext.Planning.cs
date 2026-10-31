using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Wayfarer.Models.Planning;

namespace Wayfarer.Models;

public partial class ApplicationDbContext
{
    /// <summary>Maps one typed JSONB scalar per entity; no separate lifecycle or speculative indexing.</summary>
    private static void ConfigurePlanning(ModelBuilder builder)
    {
        MapPlanning<Trip>(builder);
        MapPlanning<Region>(builder);
        MapPlanning<Place>(builder);
        MapPlanning<Area>(builder);
        MapPlanning<Segment>(builder);
    }

    /// <summary>Snapshots nested values for correct change tracking while preserving literal authored fields.</summary>
    private static void MapPlanning<T>(ModelBuilder builder) where T : class, IPlanningTarget
    {
        var property = builder.Entity<T>().Property(entity => entity.Planning).HasColumnType("jsonb")
            .HasConversion(value => value == null ? null : PlanningCodec.Write(value),
                json => json == null ? null : PlanningCodec.Read(json));
        property.Metadata.SetValueComparer(new ValueComparer<PlanningAggregate?>(
            (left, right) => PlanningCodec.Equal(left, right),
            value => value == null ? 0 : PlanningCodec.Write(value).GetHashCode(),
            value => value == null ? null : PlanningCodec.Read(PlanningCodec.Write(value))));
    }

    /// <summary>Owner-private planning alone never changes public Trip freshness, including tracked writers.</summary>
    private static bool HasReusableChanges(EntityEntry entry) => entry.State is EntityState.Added or EntityState.Deleted
        || entry.State == EntityState.Modified && entry.Properties.Any(property => property.IsModified
            && property.Metadata.Name != nameof(IPlanningTarget.Planning));
}
