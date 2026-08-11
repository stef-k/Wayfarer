using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Captures the pre-#407 editor aggregate defects before production remediation.
/// </summary>
public sealed class TripEditorWaypointAggregateDefectTests : TestBase
{
    /// <summary>Complete editor updates must not erase persisted hidden waypoint state.</summary>
    [Fact]
    public async Task CompleteEditorUpdatePreservesSubmittedHiddenWaypointAggregate()
    {
        using var db = CreateDbContext();
        var graph = SeedWaypointSegment(db);
        var controller = TripEditorSegmentControllerTests.BuildController(db);
        TripEditorSegmentControllerTests.ConfigureControllerWithUserRole(controller, "owner-user");

        await TripEditorSegmentControllerTests.SendJson(
            controller,
            item => item.UpdateSegment(graph.Trip.Id, graph.Segment.Id, CancellationToken.None),
            TripEditorSegmentControllerTests.ValidBody(graph.From.Id, graph.To.Id, "walk", "null"));

        Assert.Equal(
            [graph.Waypoint.Id],
            await db.Set<SegmentWaypoint>().Where(item => item.SegmentId == graph.Segment.Id)
                .OrderBy(item => item.Position).Select(item => item.PlaceId).ToListAsync());
    }

    /// <summary>Authoritative editor mapping must expose ordered waypoint children.</summary>
    [Fact]
    public void EditorSegmentMappingCannotRepresentUnloadedWaypointsAsAuthoritativeEmpty()
    {
        var property = typeof(EditorSegmentDto).GetProperty("WaypointPlaceIds", BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(property);
        Assert.Equal(typeof(IReadOnlyList<Guid>), property!.PropertyType);
    }

    /// <summary>Segment aggregate edits require a provider-backed optimistic concurrency value.</summary>
    [Fact]
    public void SegmentModelHasPostgresRowVersionConcurrencyToken()
    {
        using var db = CreateDbContext();
        var property = db.Model.FindEntityType(typeof(Segment))?.FindProperty("RowVersion");

        Assert.NotNull(property);
        Assert.True(property!.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, property.ValueGenerated);
    }

    /// <summary>Anchor replacement on custom waypoint geometry must warn before clearing state.</summary>
    [Fact]
    public async Task WaypointBearingAnchorReplacementRequiresRouteClearConfirmation()
    {
        using var db = CreateDbContext();
        var graph = SeedWaypointSegment(db, customRoute: true);
        var controller = TripEditorSegmentControllerTests.BuildController(db);
        TripEditorSegmentControllerTests.ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await TripEditorSegmentControllerTests.SendJson(
            controller,
            item => item.UpdateSegment(graph.Trip.Id, graph.Segment.Id, CancellationToken.None),
            TripEditorSegmentControllerTests.ValidBody(graph.To.Id, graph.From.Id, "walk", "null"));

        var conflict = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.True(controller.Response.Headers.ContainsKey("X-Wayfarer-Clear-Route-Confirmation"));
    }

    /// <summary>The narrow notes writer needs row-version protection without aggregate attachment.</summary>
    [Fact]
    public void NotesOnlyConcurrencyCanBeImplementedWithoutAggregateFieldsInItsRequest()
    {
        var rowVersion = typeof(Segment).GetProperty("RowVersion", BindingFlags.Public | BindingFlags.Instance);
        var notesRequestProperties = typeof(Wayfarer.Models.Dtos.SegmentUpdateRequestDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(item => item.Name)
            .ToArray();

        Assert.NotNull(rowVersion);
        Assert.Equal(["Notes"], notesRequestProperties);
    }

    private static WaypointGraph SeedWaypointSegment(ApplicationDbContext db, bool customRoute = false)
    {
        var graph = TripEditorSegmentControllerTests.SeedTripGraph(db, "owner-user");
        var waypoint = new Place
        {
            Id = Guid.NewGuid(), UserId = "owner-user", RegionId = graph.FirstPlace.RegionId,
            Name = "Via", DisplayOrder = 3, Location = new Point(23.5, 37.5) { SRID = 4326 }
        };
        db.Places.Add(waypoint);
        graph.FirstSegment.RouteGeometry = customRoute
            ? new LineString([graph.FirstPlace.Location!.Coordinate, waypoint.Location.Coordinate, graph.SecondPlace.Location!.Coordinate]) { SRID = 4326 }
            : null;
        graph.FirstSegment.Waypoints.Add(new SegmentWaypoint
        {
            SegmentId = graph.FirstSegment.Id, PlaceId = waypoint.Id, Place = waypoint,
            Position = 0, RouteVertexIndex = customRoute ? 1 : null
        });
        db.SaveChanges();
        return new WaypointGraph(graph.Trip, graph.FirstPlace, graph.SecondPlace, waypoint, graph.FirstSegment);
    }

    private sealed record WaypointGraph(Trip Trip, Place From, Place To, Place Waypoint, Segment Segment);
}
