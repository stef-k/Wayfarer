using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Public.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Tests.Services;

/// <summary>Exercises public privacy at the real PostgreSQL source and both controller endpoints.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class PublicTimelineLocationPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Excluded rows alter every summary field; private summaries retain the complete history.</summary>
    [PostgresTheory]
    [InlineData("1d", false, true, false)]
    [InlineData("now", true, true, false)]
    [InlineData("1d", true, true, false)]
    [InlineData("now", false, true, false)]
    [InlineData("1d", true, true, true)]
    [InlineData("now", false, false, false)]
    [InlineData("invalid", false, true, false)]
    [InlineData("1.5w", true, true, false)]
    public async Task PublicEndpoints_UseEligibleHistory(
        string threshold, bool hidden, bool isPublic, bool empty)
    {
        var user = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        user.IsTimelinePublic = isPublic;
        user.PublicTimelineTimeThreshold = threshold;
        db.Users.Update(user);
        var now = DateTime.UtcNow;
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
        var rows = new[]
        {
            Row(user.Id, now.AddDays(-30), 23, "Visible"),
            Row(user.Id, now.AddDays(-40), 25, "Hidden"),
            Row(user.Id, now.AddMinutes(-5), 27, "Recent")
        };
        db.Locations.AddRange(empty ? rows.Skip(1) : rows);
        if (hidden) db.HiddenAreas.Add(Area(user.Id));
        await db.SaveChangesAsync();
        var stats = new LocationStatsService(db);
        var controller = Controller(db);
        var projection = new PublicTimelineLocationProjection(user, now);
        var expected = (empty ? rows.Skip(1) : rows.AsEnumerable())
            .Where(r => (!hidden || r.Country != "Hidden") && (threshold == "now" || r.Country != "Recent"))
            .ToArray();

        var statsResult = await controller.GetPublicStats(user.UserName!);
        var pointResult = await controller.GetPublicTimeline(Request(user.UserName!));
        if (!isPublic || threshold == "invalid")
        {
            Assert.IsType<NotFoundObjectResult>(statsResult);
            Assert.IsType<NotFoundObjectResult>(pointResult);
            Assert.Empty(await projection.Query(db).ToListAsync());
            AssertSummary(await stats.GetPublicStatsAsync(projection), []);
        }
        else
        {
            AssertSummary(Assert.IsType<UserLocationStatsDto>(Assert.IsType<OkObjectResult>(statsResult).Value), expected);
            var points = Points(pointResult);
            Assert.Equal(expected.Select(r => r.Id).Order(), points.Select(r => r.Id).Order());
            Assert.Equal(expected.OrderByDescending(r => r.LocalTimestamp).Select(r => r.Id).Take(1),
                points.Where(r => r.IsLatestLocation).Select(r => r.Id));
        }
        AssertSummary(await stats.GetStatsForUserAsync(user.Id), empty ? rows[1..] : rows);
    }

    /// <summary>UTC event time is inclusive at the cutoff, independent of server receipt and display time.</summary>
    [PostgresFact]
    public async Task Cutoff_UsesPersistedUtcEventTime_AndPreservesPolygonBoundary()
    {
        var user = await fixture.CreateUserAsync();
        user.IsTimelinePublic = true;
        user.PublicTimelineTimeThreshold = "1d";
        var now = new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);
        var cutoff = now.AddDays(-1);
        await using var db = fixture.CreateContext();
        var boundary = Row(user.Id, cutoff, 24, "Boundary");
        boundary.Timestamp = now; // Receipt time deliberately disagrees with the event cutoff.
        var recent = Row(user.Id, cutoff.AddMilliseconds(1), 23, "Recent");
        recent.Timestamp = cutoff.AddDays(-2);
        db.Locations.AddRange(boundary, recent);
        db.HiddenAreas.Add(Area(user.Id));
        await db.SaveChangesAsync();
        var projection = new PublicTimelineLocationProjection(user, now);
        var (points, count) = await new LocationService(db).GetLocationsAsync(
            20, 35, 30, 45, 12, user.Id, CancellationToken.None, projection);

        var point = Assert.Single(points);
        Assert.Equal(boundary.Id, point.Id);
        Assert.Equal(1, count);
        Assert.True(point.LocalTimestamp > cutoff); // Athens display time must not be compared to UTC.
        AssertSummary(await new LocationStatsService(db).GetPublicStatsAsync(projection), [boundary]);
        // Existing private callers omit the projection and continue seeing both records.
        var (privatePoints, privateCount) = await new LocationService(db).GetLocationsAsync(
            20, 35, 30, 45, 12, user.Id, CancellationToken.None);
        Assert.Equal(2, privateCount);
        Assert.Equal(2, privatePoints.Count);
    }

    /// <summary>Private rows cannot win country/geohash ranking or the high-zoom latest selection.</summary>
    [PostgresTheory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(12)]
    public async Task Sampling_FiltersBeforeRanking_AndStatsIgnoreViewport(int zoom)
    {
        var user = await fixture.CreateUserAsync();
        user.IsTimelinePublic = true;
        user.PublicTimelineTimeThreshold = "1d";
        await using var db = fixture.CreateContext();
        db.Users.Update(user);
        var now = DateTime.UtcNow;
        now = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
        var eligible = Enumerable.Range(0, 401)
            .Select(i => Row(user.Id, now.AddDays(-3).AddSeconds(i), 23, "Visible")).ToArray();
        var hidden = Row(user.Id, now.AddDays(-2), 25, "Visible");
        var recent = Row(user.Id, now.AddMinutes(-1), 23, "Visible");
        var outside = Row(user.Id, now.AddDays(-4), 35, "Outside");
        db.Locations.AddRange(eligible);
        db.Locations.AddRange(hidden, recent, outside);
        db.HiddenAreas.Add(Area(user.Id));
        await db.SaveChangesAsync();
        var controller = Controller(db);
        var request = Request(user.UserName!);
        request.ZoomLevel = zoom;
        var points = Points(await controller.GetPublicTimeline(request));

        Assert.NotEmpty(points);
        Assert.All(points, p => Assert.Contains(p.Id, eligible.Select(r => r.Id)));
        Assert.Contains(points, p => p.Id == eligible[^1].Id && p.IsLatestLocation);
        var summary = Assert.IsType<UserLocationStatsDto>(
            Assert.IsType<OkObjectResult>(await controller.GetPublicStats(user.UserName!)).Value);
        Assert.Equal(402, summary.TotalLocations);
        Assert.Equal(2, summary.CountriesVisited);
        Assert.Equal(2, summary.RegionsVisited);
        Assert.Equal(2, summary.CitiesVisited);
        Assert.Equal(outside.Timestamp, summary.FromDate);
        Assert.Equal(eligible[^1].Timestamp, summary.ToDate);
    }

    /// <summary>Checks the success envelope before examining points (HTTP 200 alone is insufficient).</summary>
    private static PublicLocationDto[] Points(IActionResult result)
    {
        var envelope = Assert.IsType<OkObjectResult>(result).Value!;
        Assert.True((bool)envelope.GetType().GetProperty("Success")!.GetValue(envelope)!);
        return ((IEnumerable<PublicLocationDto>)envelope.GetType().GetProperty("Data")!.GetValue(envelope)!).ToArray();
    }

    /// <summary>Checks every public field, including null dates for empty summaries.</summary>
    private static void AssertSummary(UserLocationStatsDto actual, Location[] rows)
    {
        Assert.Equal(rows.Length, actual.TotalLocations);
        Assert.Equal(rows.Length, actual.CountriesVisited);
        Assert.Equal(rows.Length, actual.RegionsVisited);
        Assert.Equal(rows.Length, actual.CitiesVisited);
        Assert.Equal(rows.Select(r => (DateTime?)r.Timestamp).Min(), actual.FromDate);
        Assert.Equal(rows.Select(r => (DateTime?)r.Timestamp).Max(), actual.ToDate);
    }

    /// <summary>Uses real services with the fixture's isolated owner data.</summary>
    private static UsersTimelineController Controller(ApplicationDbContext db) =>
        new(NullLogger<BaseController>.Instance, db, new LocationService(db), new LocationStatsService(db));

    /// <summary>Viewport contains the visible and hidden examples, but not the outside-history row.</summary>
    private static LocationFilterRequest Request(string username) => new()
    {
        Username = username, MinLongitude = 20, MaxLongitude = 30,
        MinLatitude = 35, MaxLatitude = 45, ZoomLevel = 12
    };

    /// <summary>Distinct geographic labels make every excluded record observable in every count.</summary>
    private static Location Row(string userId, DateTime time, double longitude, string label) => new()
    {
        UserId = userId, Timestamp = time, LocalTimestamp = time, TimeZoneId = "Europe/Athens",
        Coordinates = new Point(longitude, 40) { SRID = 4326 }, Country = label, Region = label, Place = label
    };

    /// <summary>Interior contains longitude 25; longitude 24 lies exactly on its boundary.</summary>
    private static HiddenArea Area(string userId) => new()
    {
        UserId = userId, Name = "Private",
        Area = new Polygon(new LinearRing([
            new Coordinate(24, 39), new Coordinate(26, 39), new Coordinate(26, 41),
            new Coordinate(24, 41), new Coordinate(24, 39)])) { SRID = 4326 }
    };
}
