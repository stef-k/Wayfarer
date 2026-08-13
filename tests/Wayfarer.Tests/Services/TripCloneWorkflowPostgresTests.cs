using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using System.Security.Claims;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Executes the complete shared clone workflow against guarded PostgreSQL/PostGIS.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class TripCloneWorkflowPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Proves both entry points, shared tags, reconciliation, and rollback in one provider workflow.</summary>
    [PostgresFact]
    public async Task BothEntryPoints_PersistCompleteCloneAndMalformedAggregateRollsBack()
    {
        fixture.RequireAvailable();
        var owner = await fixture.CreateUserAsync();
        var mvcUser = await fixture.CreateUserAsync();
        var apiUser = await fixture.CreateUserAsync();
        var sourceId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        fixture.RegisterTrip(sourceId);
        fixture.RegisterTransportProfile(profileId);
        fixture.RegisterTag(new Tag { Id = tagId, Name = "clone-workflow", Slug = "clone-workflow" });
        await SeedSourceAsync(owner, apiUser, sourceId, profileId, tagId);

        Guid mvcCloneId;
        await using (var context = fixture.CreateContext())
        {
            var controller = BuildMvcController(context, mvcUser.Id);
            var result = await controller.Clone(sourceId);
            var redirect = Assert.IsType<RedirectToActionResult>(result);
            mvcCloneId = Assert.IsType<Guid>(redirect.RouteValues!["id"]);
            Assert.Equal("Edit", redirect.ActionName);
        }
        fixture.RegisterTrip(mvcCloneId);

        Guid apiCloneId;
        await using (var context = fixture.CreateContext())
        {
            var controller = BuildApiController(context, "clone-token");
            var result = await controller.CloneTrip(sourceId);
            var ok = Assert.IsType<OkObjectResult>(result);
            apiCloneId = Assert.IsType<Guid>(ok.Value!.GetType().GetProperty("clonedTripId")!.GetValue(ok.Value));
        }
        fixture.RegisterTrip(apiCloneId);

        await using (var verify = fixture.CreateContext())
        {
            foreach (var cloneId in new[] { mvcCloneId, apiCloneId })
            {
                var clone = await verify.Trips.AsNoTracking().Include(trip => trip.Tags)
                    .Include(trip => trip.Regions).ThenInclude(region => region.Places)
                    .Include(trip => trip.Segments).ThenInclude(segment => segment.Waypoints)
                    .SingleAsync(trip => trip.Id == cloneId);
                Assert.False(clone.IsPublic);
                Assert.Equal(tagId, Assert.Single(clone.Tags).Id);
                var places = clone.Regions.SelectMany(region => region.Places).ToDictionary(place => place.Name);
                var segment = Assert.Single(clone.Segments);
                Assert.Equal(places["A"].Id, segment.FromPlaceId);
                Assert.Equal(places["B"].Id, Assert.Single(segment.Waypoints).PlaceId);
                Assert.Equal(places["C"].Id, segment.ToPlaceId);
                Assert.NotNull(segment.EstimatedDistanceKm);
                Assert.NotNull(segment.EstimatedDuration);
                Assert.Equal(EstimatedDurationSource.Automatic, segment.EstimatedDurationSource);
            }
            Assert.Equal(1, await verify.Tags.CountAsync(tag => tag.Id == tagId));
        }

        await MakeSourceRouteMalformedAsync(sourceId);
        await using (var context = fixture.CreateContext())
        {
            var controller = BuildApiController(context, "clone-token");
            Assert.IsType<ObjectResult>(await controller.CloneTrip(sourceId));
        }
        await using (var verify = fixture.CreateContext())
        {
            var destinationTrips = await verify.Trips.Where(trip => trip.UserId == apiUser.Id).Select(trip => trip.Id).ToArrayAsync();
            Assert.Equal([apiCloneId], destinationTrips);
        }
    }

    /// <summary>Seeds one tagged custom A to B to C source with an Automatic profile.</summary>
    private async Task SeedSourceAsync(
        ApplicationUser owner, ApplicationUser apiUser, Guid tripId, Guid profileId, Guid tagId)
    {
        await using var context = fixture.CreateContext();
        var trip = new Trip { Id = tripId, UserId = owner.Id, Name = "Clone workflow", IsPublic = true };
        var region = new Region { Id = Guid.NewGuid(), TripId = trip.Id, UserId = owner.Id, Name = "Route" };
        var a = Place(region, owner.Id, "A", 0, 0);
        var b = Place(region, owner.Id, "B", 0.1, 0);
        var c = Place(region, owner.Id, "C", 0.2, 0);
        var profile = new TransportProfile
        {
            Id = profileId, Key = $"clone-{Guid.NewGuid():N}"[..30], Label = "Clone workflow",
            Category = "Test", PlanningSpeedKmh = 10, IsActive = false
        };
        var tag = new Tag { Id = tagId, Name = "clone-workflow", Slug = "clone-workflow" };
        var segment = new Segment
        {
            Id = Guid.NewGuid(), TripId = trip.Id, UserId = owner.Id,
            FromPlaceId = a.Id, ToPlaceId = c.Id, Mode = profile.Key, TransportProfileId = profile.Id,
            RouteGeometry = Line((0, 0), (0.1, 0), (0.2, 0)),
            EstimatedDuration = TimeSpan.FromDays(1), EstimatedDurationSource = EstimatedDurationSource.Automatic,
            EstimatedDistanceKm = 999,
            Waypoints = [new SegmentWaypoint { PlaceId = b.Id, Position = 0, RouteVertexIndex = 1 }]
        };
        trip.Regions.Add(region);
        region.Places = [a, b, c];
        trip.Segments.Add(segment);
        trip.Tags.Add(tag);
        context.ApiTokens.Add(new ApiToken { Token = "clone-token", Name = "clone", UserId = apiUser.Id, User = apiUser });
        context.AddRange(trip, profile);
        await context.SaveChangesAsync();
    }

    /// <summary>Makes the stored semantic waypoint coordinate contradict its custom route index.</summary>
    private async Task MakeSourceRouteMalformedAsync(Guid sourceId)
    {
        await using var context = fixture.CreateContext();
        var segment = await context.Segments.SingleAsync(item => item.TripId == sourceId);
        segment.RouteGeometry = Line((0, 0), (0.15, 0), (0.2, 0));
        await context.SaveChangesAsync();
    }

    /// <summary>Builds the MVC entry point with claims and TempData.</summary>
    private static TripController BuildMvcController(ApplicationDbContext context, string userId)
    {
        var controller = new TripController(
            NullLogger<TripController>.Instance, context, Mock.Of<ITripMapThumbnailGenerator>(),
            Mock.Of<ITripTagService>(), Mock.Of<ICacheWarmupScheduler>());
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "Test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        controller.TempData = new TempDataDictionary(http, Mock.Of<ITempDataProvider>());
        return controller;
    }

    /// <summary>Builds the API entry point with its existing bearer-token mechanism.</summary>
    private static TripsController BuildApiController(ApplicationDbContext context, string token)
    {
        var controller = new TripsController(
            context, NullLogger<BaseApiController>.Instance, Mock.Of<ITripTagService>(),
            Mock.Of<IApplicationSettingsService>(), Mock.Of<ICacheWarmupScheduler>());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.ControllerContext.HttpContext.Request.Headers.Authorization = $"Bearer {token}";
        return controller;
    }

    /// <summary>Creates one located Place.</summary>
    private static Place Place(Region region, string userId, string name, double x, double y) => new()
    {
        Id = Guid.NewGuid(), RegionId = region.Id, UserId = userId, Name = name,
        Location = new Point(x, y) { SRID = 4326 }
    };

    /// <summary>Creates an SRID 4326 LineString.</summary>
    private static LineString Line(params (double X, double Y)[] coordinates) =>
        new(coordinates.Select(item => new Coordinate(item.X, item.Y)).ToArray()) { SRID = 4326 };
}
