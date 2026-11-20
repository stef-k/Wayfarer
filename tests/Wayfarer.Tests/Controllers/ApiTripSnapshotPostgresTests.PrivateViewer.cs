using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class ApiTripSnapshotPostgresTests
{
    /// <summary>Reads a private Trip graph and its owner's visits from one explicit split-query snapshot.</summary>
    [PostgresFact]
    public async Task PrivateViewer_PreservesGraphAndOwnerVisitsInSplitSnapshot()
    {
        var trip = await SeedAsync(false);
        var ownerPlace = Place(trip);
        var other = await fixture.CreateUserAsync();
        var regions = trip.Regions.OrderBy(r => r.DisplayOrder).ToArray();
        var stops = Enumerable.Range(1, 2).Select(i => new Place
        {
            Id = Guid.NewGuid(), Name = $"Stop {i}", UserId = trip.UserId, RegionId = regions[i - 1].Id
        }).ToArray();
        var tag = new Tag { Id = Guid.NewGuid(), Name = "Private graph", Slug = $"private-{trip.Id}" };
        fixture.RegisterTag(tag);
        var ownerVisit = Visit(trip.UserId);
        await using (var setup = fixture.CreateContext())
        {
            setup.Places.AddRange(stops);
            setup.Areas.Add(new Area { Id = Guid.NewGuid(), RegionId = regions[0].Id, Name = "Private area",
                Geometry = new Polygon(new LinearRing([new(20, 30), new(21, 30), new(21, 31), new(20, 30)])) { SRID = 4326 } });
            setup.PlaceVisitEvents.AddRange(ownerVisit, Visit(other.Id));
            setup.Tags.Add(tag);
            (await setup.Trips.SingleAsync(t => t.Id == trip.Id)).Tags.Add(tag);
            var segment = await setup.Segments.SingleAsync(s => s.TripId == trip.Id);
            segment.Waypoints = [new() { PlaceId = stops[0].Id, Position = 1 }, new() { PlaceId = stops[1].Id, Position = 0 }];
            await setup.SaveChangesAsync();
        }

        var gate = new ReadGate();
        await using var db = fixture.CreateContext((options, provider) => new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>(options)
                .ConfigureWarnings(w => w.Throw(RelationalEventId.MultipleCollectionIncludeWarning)).Options, provider), gate);
        var viewer = CreateViewer(db, trip.UserId);
        var viewing = viewer.View(trip.Id);
        try
        {
            await Task.WhenAny(gate.Reached.Task, viewing).WaitAsync(TimeSpan.FromSeconds(15));
            if (viewing.IsCompleted) await viewing;
            Assert.Equal(System.Data.IsolationLevel.RepeatableRead, gate.Isolation);
            await using var writer = fixture.CreateContext();
            await using var change = await writer.Database.BeginTransactionAsync();
            await writer.Places.Where(p => p.Id == ownerPlace.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, "After snapshot"));
            await writer.PlaceVisitEvents.Where(v => v.Id == ownerVisit.Id).ExecuteDeleteAsync();
            await change.CommitAsync();
        }
        finally { gate.Release.TrySetResult(); }

        var result = Assert.IsType<ViewResult>(await viewing);
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Equal("~/Views/Trip/Viewer.cshtml", result.ViewName);
        Assert.True((bool)result.ViewData["IsOwner"]!);
        Assert.Equal(3, result.ViewData["TotalPlaces"]);
        Assert.Equal(1, result.ViewData["VisitedPlaces"]);
        Assert.Equal(1, Assert.IsType<Dictionary<Guid, int>>(result.ViewData["PlaceVisitCounts"])[ownerPlace.Id]);
        Assert.Equal(ownerVisit.Id, Assert.Single(Assert.IsType<List<PlaceVisitEvent>>(result.ViewData["VisitEvents"])).Id);
        var graph = Assert.IsType<Trip>(result.Model);
        Assert.False(graph.IsPublic);
        Assert.Equal(tag.Id, Assert.Single(graph.Tags).Id);
        Assert.Equal(2, graph.Regions.Count);
        Assert.Equal("Private area", Assert.Single(graph.Regions.SelectMany(r => r.Areas)).Name);
        var route = Assert.Single(graph.Segments);
        Assert.Equal(ownerPlace.Name, route.FromPlace!.Name);
        Assert.Equal(ownerPlace.Id, route.ToPlace!.Id);
        Assert.Equal(new[] { stops[1].Id, stops[0].Id }, route.Waypoints.Select(w => w.Place.Id));

        await using var outsiderDb = fixture.CreateContext();
        Assert.IsType<NotFoundResult>(await CreateViewer(outsiderDb, other.Id).View(trip.Id));

        PlaceVisitEvent Visit(string userId) => new()
        {
            Id = Guid.NewGuid(), UserId = userId, PlaceId = ownerPlace.Id, TripIdSnapshot = trip.Id,
            ArrivedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>Uses the real Viewer action with a named principal and no unrelated services.</summary>
    private static TripController CreateViewer(ApplicationDbContext db, string userId) =>
        ConfigureControllerWithUser(new TripController(NullLogger<TripController>.Instance, db,
            Mock.Of<ITripMapThumbnailGenerator>(), Mock.Of<ITripTagService>(), Mock.Of<ICacheWarmupScheduler>()), userId);
}
