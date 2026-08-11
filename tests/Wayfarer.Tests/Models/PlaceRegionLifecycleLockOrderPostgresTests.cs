using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Observes the production lifecycle row-lock acquisition sequence on PostgreSQL.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class PlaceRegionLifecycleLockOrderPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Proves profile, Segment, Place, and Region locks use the global class and GUID order.</summary>
    [PostgresFact]
    public async Task PlaceMovement_AcquiresGlobalLockClassesAndSortedIdentities()
    {
        var seeded = await SeedAsync();
        var observer = new LockObservationInterceptor();
        await using var context = fixture.CreateContext(observer);

        var result = await Service(context).UpdatePlaceAsync(
            seeded.TripId,
            seeded.MovingPlaceId,
            seeded.UserId,
            new(seeded.TargetRegionId, "Moved", "", "", "marker", "bg-blue", Point(8, 8)),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(
            ["profile", "profile", "segment", "segment", "place", "region", "region"],
            observer.Locks.Select(item => item.Class));
        AssertClassSorted(observer.Locks, "profile", seeded.ProfileIds);
        AssertClassSorted(observer.Locks, "segment", seeded.SegmentIds);
        AssertClassSorted(observer.Locks, "place", [seeded.MovingPlaceId]);
        AssertClassSorted(observer.Locks, "region", [seeded.SourceRegionId, seeded.TargetRegionId]);
    }

    private async Task<LockSeed> SeedAsync()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        var trip = new Trip { Id = Guid.NewGuid(), UserId = user.Id, Name = "Lock order" };
        fixture.RegisterTrip(trip.Id);
        var source = Region(trip, user.Id, "Source", 1);
        var target = Region(trip, user.Id, "Target", 2);
        var from = Place(source, user.Id, "From", 1, 1);
        var moving = Place(source, user.Id, "Moving", 2, 2);
        var to = Place(source, user.Id, "To", 3, 3);
        var profileIds = new[] { Guid.NewGuid(), Guid.NewGuid() }.Order().ToArray();
        var profiles = profileIds.Select((id, index) => new TransportProfile
        {
            Id = id,
            Key = $"life-lock-{index}-{Guid.NewGuid():N}",
            Label = $"Lifecycle lock {index}",
            Category = "Test",
            PlanningSpeedKmh = 5 + index,
            IsActive = true
        }).ToArray();
        foreach (var profile in profiles) fixture.RegisterTransportProfile(profile.Id);
        var segmentIds = new[] { Guid.NewGuid(), Guid.NewGuid() }.Order().ToArray();
        for (var index = segmentIds.Length - 1; index >= 0; index--)
        {
            var segment = new Segment
            {
                Id = segmentIds[index], Trip = trip, TripId = trip.Id, UserId = user.Id,
                FromPlaceId = from.Id, ToPlaceId = to.Id, DisplayOrder = index + 1,
                Mode = profiles[index].Key, TransportProfile = profiles[index],
                TransportProfileId = profiles[index].Id,
                EstimatedDurationSource = EstimatedDurationSource.Automatic,
                RouteGeometry = new LineString([new(1, 1), new(2, 2), new(3, 3)]) { SRID = 4326 }
            };
            segment.Waypoints.Add(new SegmentWaypoint
            {
                Segment = segment, SegmentId = segment.Id, Place = moving, PlaceId = moving.Id,
                Position = 0, RouteVertexIndex = 1
            });
            trip.Segments.Add(segment);
        }
        await using var context = fixture.CreateContext();
        context.Trips.Add(trip);
        await context.SaveChangesAsync();
        return new(user.Id, trip.Id, source.Id, target.Id, moving.Id, profileIds, segmentIds);
    }

    private static void AssertClassSorted(
        IReadOnlyList<ObservedLock> locks,
        string lockClass,
        IReadOnlyList<Guid> expected) =>
        Assert.Equal(expected.Order(), locks.Where(item => item.Class == lockClass).Select(item => item.Id));

    private static PlaceRegionLifecycleService Service(ApplicationDbContext context) =>
        new(context, new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider()));

    private static Region Region(Trip trip, string userId, string name, int displayOrder)
    {
        var region = new Region
        {
            Id = Guid.NewGuid(), Trip = trip, TripId = trip.Id, UserId = userId,
            Name = name, DisplayOrder = displayOrder
        };
        trip.Regions.Add(region);
        return region;
    }

    private static Place Place(Region region, string userId, string name, double x, double y)
    {
        var place = new Place
        {
            Id = Guid.NewGuid(), Region = region, RegionId = region.Id, UserId = userId,
            Name = name, DisplayOrder = region.Places.Count + 1, Location = Point(x, y)
        };
        region.Places.Add(place);
        return place;
    }

    private static Point Point(double x, double y) => new(x, y) { SRID = 4326 };

    private sealed record LockSeed(
        string UserId,
        Guid TripId,
        Guid SourceRegionId,
        Guid TargetRegionId,
        Guid MovingPlaceId,
        Guid[] ProfileIds,
        Guid[] SegmentIds);

    private sealed record ObservedLock(string Class, Guid Id);

    /// <summary>Records only production row-lock commands and their bound GUID identities.</summary>
    private sealed class LockObservationInterceptor : DbCommandInterceptor
    {
        internal List<ObservedLock> Locks { get; } = [];

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var lockClass = command.CommandText switch
            {
                var text when text.Contains("\"TransportProfiles\"", StringComparison.Ordinal) => "profile",
                var text when text.Contains("\"Segments\"", StringComparison.Ordinal) => "segment",
                var text when text.Contains("\"Places\"", StringComparison.Ordinal) => "place",
                var text when text.Contains("\"Regions\"", StringComparison.Ordinal) => "region",
                _ => null
            };
            if (lockClass != null
                && command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.Parameters.Cast<DbParameter>().Select(item => item.Value).OfType<Guid>().FirstOrDefault() is var id
                && id != Guid.Empty)
            {
                Locks.Add(new(lockClass, id));
            }
            return ValueTask.FromResult(result);
        }
    }
}
