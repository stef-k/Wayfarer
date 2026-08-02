using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>
/// Focused validation tests for Trip Editor segment mutation requests.
/// </summary>
public sealed class TripEditorSegmentValidationControllerTests : TestBase
{
    [Fact]
    public async Task SaveRejectsForbiddenFieldsAndInvalidEditableFields()
    {
        using var db = CreateDbContext();
        var graph = TripEditorSegmentControllerTests.SeedTripGraph(db, "owner-user");
        var controller = TripEditorSegmentControllerTests.BuildController(db);
        TripEditorSegmentControllerTests.ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await TripEditorSegmentControllerTests.SendJson(controller, c => c.UpdateSegment(graph.Trip.Id, graph.FirstSegment.Id, CancellationToken.None), $$"""
        {
          "id": "00000000-0000-0000-0000-000000000001",
          "tripId": "00000000-0000-0000-0000-000000000002",
          "displayOrder": 99,
          "capabilities": {},
          "fromPlaceId": null,
          "toPlaceId": null,
          "mode": "hoverboard",
          "estimatedDistanceKm": -1,
          "estimatedDurationMinutes": -2,
          "estimatedDurationSource": "Manual",
          "notesHtml": "<img src=\"   data:image/png;base64,abc\">",
          "route": { "type": "Point", "coordinates": [0, 0] }
        }
        """);

        var keys = TripEditorSegmentControllerTests.AssertValidationProblem(result).Errors.Keys;
        foreach (var key in new[] { "id", "tripId", "displayOrder", "capabilities", "estimatedDurationMinutes", "notesHtml", "route" })
        {
            Assert.Contains(key, keys);
        }
    }

    /// <summary>Proves semantic mode validation uses the database catalog.</summary>
    [Fact]
    public async Task SaveRejectsModeMissingFromDatabaseCatalog()
    {
        using var db = CreateDbContext();
        var graph = TripEditorSegmentControllerTests.SeedTripGraph(db, "owner-user");
        var controller = TripEditorSegmentControllerTests.BuildController(db);
        TripEditorSegmentControllerTests.ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await TripEditorSegmentControllerTests.SendJson(
            controller,
            c => c.UpdateSegment(graph.Trip.Id, graph.FirstSegment.Id, CancellationToken.None),
            TripEditorSegmentControllerTests.ValidBody(graph.FirstPlace.Id, graph.SecondPlace.Id, "hoverboard", "null"));

        Assert.Contains("mode", TripEditorSegmentControllerTests.AssertValidationProblem(result).Errors.Keys);
    }

    [Fact]
    public async Task SaveRejectsInvalidPlaceReferences()
    {
        using var db = CreateDbContext();
        var graph = TripEditorSegmentControllerTests.SeedTripGraph(db, "owner-user");
        var otherGraph = TripEditorSegmentControllerTests.SeedTripGraph(db, "other-user");
        var controller = TripEditorSegmentControllerTests.BuildController(db);
        TripEditorSegmentControllerTests.ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await TripEditorSegmentControllerTests.SendJson(
            controller,
            c => c.UpdateSegment(graph.Trip.Id, graph.FirstSegment.Id, CancellationToken.None),
            TripEditorSegmentControllerTests.ValidBody(otherGraph.FirstPlace.Id, otherGraph.SecondPlace.Id, "walk", TripEditorSegmentControllerTests.RouteJson(1)));

        var keys = TripEditorSegmentControllerTests.AssertValidationProblem(result).Errors.Keys;
        Assert.Contains("fromPlaceId", keys);
        Assert.Contains("toPlaceId", keys);
    }

    [Theory]
    [InlineData("""{ "type": "Point", "coordinates": [0, 0] }""", "route")]
    [InlineData("\"not-geometry\"", "route")]
    [InlineData("""{ "type": "MultiLineString", "coordinates": [] }""", "route")]
    [InlineData("""{ "type": "LineString", "coordinates": [] }""", "route.coordinates")]
    [InlineData("""{ "type": "LineString", "coordinates": [[0,0]] }""", "route.coordinates")]
    [InlineData("""{ "type": "LineString", "coordinates": [[181,0],[1,1]] }""", "route.coordinates")]
    [InlineData("""{ "type": "LineString", "coordinates": [[0,91],[1,1]] }""", "route.coordinates")]
    [InlineData("""{ "type": "LineString", "coordinates": [[0,0,1],[1,1,1]] }""", "route.coordinates")]
    public async Task RouteValidationRejectsInvalidGeometryCases(string routeJson, string expectedKey)
    {
        using var db = CreateDbContext();
        var graph = TripEditorSegmentControllerTests.SeedTripGraph(db, "owner-user");
        var controller = TripEditorSegmentControllerTests.BuildController(db);
        TripEditorSegmentControllerTests.ConfigureControllerWithUserRole(controller, "owner-user");

        var result = await TripEditorSegmentControllerTests.SendJson(
            controller,
            c => c.UpdateSegment(graph.Trip.Id, graph.FirstSegment.Id, CancellationToken.None),
            TripEditorSegmentControllerTests.ValidBody(graph.FirstPlace.Id, graph.SecondPlace.Id, "walk", routeJson));

        Assert.Contains(expectedKey, TripEditorSegmentControllerTests.AssertValidationProblem(result).Errors.Keys);
    }

    [Fact]
    public async Task SaveRequiresExplicitRoutePropertyButAcceptsNullClear()
    {
        using var db = CreateDbContext();
        var graph = TripEditorSegmentControllerTests.SeedTripGraph(db, "owner-user");
        var controller = TripEditorSegmentControllerTests.BuildController(db);
        TripEditorSegmentControllerTests.ConfigureControllerWithUserRole(controller, "owner-user");

        var missing = await TripEditorSegmentControllerTests.SendJson(controller, c => c.UpdateSegment(graph.Trip.Id, graph.FirstSegment.Id, CancellationToken.None), $$"""
        {
          "fromPlaceId": "{{graph.FirstPlace.Id}}",
          "toPlaceId": "{{graph.SecondPlace.Id}}",
          "mode": "walk",
          "estimatedDistanceKm": null,
          "estimatedDurationMinutes": null,
          "estimatedDurationSource": "Automatic",
          "notesHtml": null
        }
        """);
        var clear = await TripEditorSegmentControllerTests.SendJson(controller, c => c.UpdateSegment(graph.Trip.Id, graph.FirstSegment.Id, CancellationToken.None), TripEditorSegmentControllerTests.ValidBody(graph.FirstPlace.Id, graph.SecondPlace.Id, "walk", "null"));

        Assert.Contains("route", TripEditorSegmentControllerTests.AssertValidationProblem(missing).Errors.Keys);
        Assert.Null(TripEditorSegmentControllerTests.AssertMutation<Wayfarer.Models.Dtos.Editor.EditorSegmentDto>(clear).Data.Route);
    }

    [Fact]
    public async Task OrderRejectsMissingDuplicateUnknownAndCrossTripIds()
    {
        using var db = CreateDbContext();
        var graph = TripEditorSegmentControllerTests.SeedTripGraph(db, "owner-user");
        var otherGraph = TripEditorSegmentControllerTests.SeedTripGraph(db, "other-user");
        var controller = TripEditorSegmentControllerTests.BuildController(db);
        TripEditorSegmentControllerTests.ConfigureControllerWithUserRole(controller, "owner-user");

        var bodies = new[]
        {
            """{ "segmentIds": [] }""",
            $$"""{ "segmentIds": [ "{{graph.FirstSegment.Id}}", "{{graph.FirstSegment.Id}}" ] }""",
            """{ "segmentIds": [ "00000000-0000-0000-0000-000000000001" ] }""",
            $$"""{ "segmentIds": [ "{{otherGraph.FirstSegment.Id}}" ] }"""
        };

        foreach (var body in bodies)
        {
            var result = await TripEditorSegmentControllerTests.SendJson(controller, c => c.OrderSegments(graph.Trip.Id, CancellationToken.None), body);
            Assert.Contains("segmentIds", TripEditorSegmentControllerTests.AssertValidationProblem(result).Errors.Keys);
        }
    }
}
