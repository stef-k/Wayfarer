using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Verifies the persisted waypoint entity and its relational model contract.</summary>
public sealed class SegmentWaypointModelTests : TestBase
{
    /// <summary>Proves the runtime model exposes the required keys, checks, indexes, and delete behavior.</summary>
    [Fact]
    public void RuntimeModel_DefinesWaypointPersistenceContract()
    {
        using var context = CreateDbContext();
        var entity = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(SegmentWaypoint));

        Assert.NotNull(entity);
        Assert.Equal("SegmentWaypoints", entity!.GetTableName());
        Assert.Equal([nameof(SegmentWaypoint.SegmentId), nameof(SegmentWaypoint.PlaceId)],
            entity.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Contains(entity.GetIndexes(), index =>
            index.IsUnique && index.Properties.Select(property => property.Name).SequenceEqual([nameof(SegmentWaypoint.SegmentId), nameof(SegmentWaypoint.Position)]));
        Assert.Contains(entity.GetIndexes(), index =>
            index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual([nameof(SegmentWaypoint.SegmentId), nameof(SegmentWaypoint.RouteVertexIndex)])
            && index.GetFilter() == "\"RouteVertexIndex\" IS NOT NULL");
        Assert.Contains(entity.GetCheckConstraints(), constraint => constraint.Name == "CK_SegmentWaypoint_Position");
        Assert.Contains(entity.GetCheckConstraints(), constraint => constraint.Name == "CK_SegmentWaypoint_RouteVertexIndex");

        var segmentForeignKey = entity.GetForeignKeys().Single(key => key.PrincipalEntityType.ClrType == typeof(Segment));
        var placeForeignKey = entity.GetForeignKeys().Single(key => key.PrincipalEntityType.ClrType == typeof(Place));
        Assert.Equal(DeleteBehavior.Cascade, segmentForeignKey.DeleteBehavior);
        Assert.Equal(DeleteBehavior.Restrict, placeForeignKey.DeleteBehavior);
    }
}
