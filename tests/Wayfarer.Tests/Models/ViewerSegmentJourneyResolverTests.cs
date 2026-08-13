using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Verifies the presentation-only ordered-anchor and neutral route projection.</summary>
public sealed class ViewerSegmentJourneyResolverTests
{
    [Theory]
    [InlineData("from")]
    [InlineData("to")]
    [InlineData("waypoint")]
    public void Resolve_RejectsForeignTripAnchorsWithoutDisclosingThem(string foreignAnchor)
    {
        var segment = SegmentWithWaypoints(1);
        var foreignTripId = Guid.NewGuid();
        var foreignPlace = foreignAnchor switch
        {
            "from" => segment.FromPlace!,
            "to" => segment.ToPlace!,
            _ => Assert.Single(segment.Waypoints).Place
        };
        foreignPlace.Region!.TripId = foreignTripId;

        var result = ViewerSegmentJourneyResolver.Resolve(segment, waypointsLoaded: true);

        var foreign = result.Anchors.Single(anchor => anchor.Role == (foreignAnchor switch
        {
            "from" => "Start",
            "to" => "End",
            _ => "Via 1"
        }));
        Assert.Null(foreign.PlaceId);
        Assert.DoesNotContain(foreignPlace.Name, foreign.DisplayName, StringComparison.Ordinal);
        Assert.Null(foreign.Location);
        Assert.Null(result.RouteWkt);
    }

    [Theory]
    [InlineData(0, "A → C", 2)]
    [InlineData(1, "A → B1 → C", 3)]
    [InlineData(3, "A → B1 → B2 → B3 → C", 5)]
    public void Resolve_ProjectsZeroOneAndMultipleWaypointFallbacks(int waypointCount, string trail, int routePointCount)
    {
        var segment = SegmentWithWaypoints(waypointCount);

        var result = ViewerSegmentJourneyResolver.Resolve(segment, waypointsLoaded: true);

        Assert.Equal(trail, result.TrailText);
        Assert.Equal(waypointCount, result.WaypointCount);
        Assert.Equal(routePointCount, result.RoutePointCount);
        Assert.NotNull(result.RouteWkt);
        Assert.Null(result.DegradationMessage);
    }

    [Fact]
    public void Resolve_PreservesClosedLoopPositionalRolesAndCanonicalIdentity()
    {
        var segment = SegmentWithWaypoints(1);
        segment.ToPlaceId = segment.FromPlaceId;
        segment.ToPlace = segment.FromPlace;

        var result = ViewerSegmentJourneyResolver.Resolve(segment, waypointsLoaded: true);

        Assert.Equal("A → B1 → A", result.TrailText);
        Assert.Equal("Start", result.Anchors[0].Role);
        Assert.Equal("End", result.Anchors[^1].Role);
        Assert.Equal(result.Anchors[0].PlaceId, result.Anchors[^1].PlaceId);
        Assert.Contains("1 1", result.RouteWkt);
    }

    [Fact]
    public void Resolve_KeepsValidCustomGeometryAuthoritative()
    {
        var segment = SegmentWithWaypoints(1);
        segment.RouteGeometry = Line((1, 1), (1.5, 1.5), (2, 2), (2.5, 2.5), (3, 3));
        Assert.Single(segment.Waypoints).RouteVertexIndex = 2;

        var result = ViewerSegmentJourneyResolver.Resolve(segment, waypointsLoaded: true);

        Assert.Equal(5, result.RoutePointCount);
        Assert.Contains("1.5 1.5", result.RouteWkt);
    }

    [Fact]
    public void Resolve_RejectsMalformedPositionAndCustomIndexWithoutRepair()
    {
        var malformedPosition = SegmentWithWaypoints(1);
        Assert.Single(malformedPosition.Waypoints).Position = 2;
        var malformedIndex = SegmentWithWaypoints(1);
        malformedIndex.RouteGeometry = Line((1, 1), (2, 2), (3, 3));
        Assert.Single(malformedIndex.Waypoints).RouteVertexIndex = 0;

        var positionResult = ViewerSegmentJourneyResolver.Resolve(malformedPosition, waypointsLoaded: true);
        var indexResult = ViewerSegmentJourneyResolver.Resolve(malformedIndex, waypointsLoaded: true);

        Assert.Empty(positionResult.Anchors);
        Assert.Null(positionResult.RouteWkt);
        Assert.Equal("Journey order is unavailable.", positionResult.DegradationMessage);
        Assert.Equal("A → B1 → C", indexResult.TrailText);
        Assert.Null(indexResult.RouteWkt);
        Assert.Equal("Route line is unavailable.", indexResult.DegradationMessage);
    }

    [Fact]
    public void Resolve_DoesNotTreatUnloadedWaypointsAsEmpty()
    {
        var result = ViewerSegmentJourneyResolver.Resolve(SegmentWithWaypoints(0), waypointsLoaded: false);

        Assert.Empty(result.Anchors);
        Assert.Null(result.TrailText);
        Assert.Equal("Journey details are unavailable.", result.DegradationMessage);
    }

    [Fact]
    public void Resolve_RejectsDuplicateSemanticIdentityExceptClosedLoopEndpoints()
    {
        var segment = SegmentWithWaypoints(1);
        var waypoint = Assert.Single(segment.Waypoints);
        waypoint.Place = segment.FromPlace!;
        waypoint.PlaceId = segment.FromPlaceId!.Value;

        var result = ViewerSegmentJourneyResolver.Resolve(segment, waypointsLoaded: true);

        Assert.Empty(result.Anchors);
        Assert.Null(result.RouteWkt);
        Assert.Equal("Journey details are unavailable.", result.DegradationMessage);
    }

    [Fact]
    public void Resolve_UsesBoundedTextForMissingUnnamedAndLocationlessPlaces()
    {
        var segment = SegmentWithWaypoints(3);
        var waypoints = segment.Waypoints.OrderBy(item => item.Position).ToArray();
        waypoints[0].Place = null!;
        waypoints[1].Place.Name = " ";
        waypoints[2].Place.Location = null;

        var result = ViewerSegmentJourneyResolver.Resolve(segment, waypointsLoaded: true);

        Assert.Equal("Unavailable intermediate place", result.Anchors[1].DisplayName);
        Assert.Equal("Unnamed place", result.Anchors[2].DisplayName);
        Assert.Null(result.Anchors[3].Location);
        Assert.Null(result.RouteWkt);
        Assert.Equal("Route line is unavailable.", result.DegradationMessage);
    }

    /// <summary>Builds canonical anchors whose coordinates make the fallback order observable.</summary>
    private static Segment SegmentWithWaypoints(int waypointCount)
    {
        var segment = new Segment { Id = Guid.NewGuid(), UserId = "owner", TripId = Guid.NewGuid() };
        segment.FromPlace = Place("A", 1, 1);
        segment.FromPlace.Region!.TripId = segment.TripId;
        segment.FromPlaceId = segment.FromPlace.Id;
        segment.ToPlace = Place("C", 3, 3);
        segment.ToPlace.Region!.TripId = segment.TripId;
        segment.ToPlaceId = segment.ToPlace.Id;
        for (var position = 0; position < waypointCount; position++)
        {
            var place = Place($"B{position + 1}", 2 + position * 0.1, 2 + position * 0.1);
            place.Region!.TripId = segment.TripId;
            segment.Waypoints.Add(new SegmentWaypoint
            {
                Segment = segment, SegmentId = segment.Id, Place = place, PlaceId = place.Id, Position = position
            });
        }

        return segment;
    }

    /// <summary>Builds one saved Place with a valid WGS84 map location.</summary>
    private static Place Place(string name, double x, double y)
    {
        var region = new Region { Id = Guid.NewGuid(), TripId = Guid.Empty, UserId = "owner", Name = $"{name} region" };
        return new Place
        {
            Id = Guid.NewGuid(), UserId = "owner", Region = region, RegionId = region.Id, Name = name,
            Location = new Point(x, y) { SRID = 4326 }
        };
    }

    /// <summary>Builds custom route geometry without invoking mutation responsibilities.</summary>
    private static LineString Line(params (double X, double Y)[] points) =>
        new(points.Select(point => new Coordinate(point.X, point.Y)).ToArray()) { SRID = 4326 };
}
