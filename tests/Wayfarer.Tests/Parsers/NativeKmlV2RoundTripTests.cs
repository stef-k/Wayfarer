using System.Text;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>Compact native-v2 serializer, transport, fallback, and closed-loop fixture family.</summary>
public sealed class NativeKmlV2RoundTripTests
{
    [Fact]
    public void CustomWaypointRoute_EmitsIdentityAndIndices()
    {
        var (trip, segment, waypoint) = CreateWaypointTrip(true);
        var kml = TripWayfarerKmlExporter.BuildKml(trip);
        Assert.Contains("WayfarerSchemaVersion", kml);
        Assert.Contains("<value>2</value>", kml);
        Assert.Contains("<value>true</value>", kml);
        Assert.Contains(waypoint.PlaceId.ToString("D"), kml);
        Assert.Contains($"<value>{waypoint.RouteVertexIndex}</value>", kml);
        Assert.Contains(segment.Id.ToString("D"), kml);
    }

    [Fact]
    public void FallbackWaypointRoute_EmitsAnchorGeometryWithoutCustomState()
    {
        var (trip, segment, waypoint) = CreateWaypointTrip(false);
        var kml = TripWayfarerKmlExporter.BuildKml(trip);
        Assert.Contains(segment.Id.ToString("D"), kml);
        Assert.Contains("<value>false</value>", kml);
        Assert.Contains(waypoint.PlaceId.ToString("D"), kml);
        Assert.Contains("<value>null</value>", kml);
        Assert.Contains("1,1,0", kml);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, null)]
    public void RoundTrip_PreservesWaypointRouteState(bool hasCustomRoute, int? expectedIndex)
    {
        var (trip, _, waypoint) = CreateWaypointTrip(hasCustomRoute);
        using var stream = Stream(TripWayfarerKmlExporter.BuildKml(trip));
        var parsed = WayfarerKmlParser.ClassifyAndParse(stream);
        var segment = Assert.Single(parsed.Document!.Segments);
        Assert.Equal(WayfarerKmlKind.NativeV2, parsed.Kind);
        Assert.Equal(hasCustomRoute, segment.HasCustomRoute);
        Assert.Equal(waypoint.PlaceId, Assert.Single(segment.WaypointPlaceIds));
        Assert.Equal(expectedIndex, Assert.Single(segment.WaypointRouteVertexIndices));
        Assert.NotNull(segment.Geometry);
    }

    [Fact]
    public void RoundTrip_ClosedLoopUsesOneEndpointIdentity()
    {
        var (trip, segment, _) = CreateWaypointTrip(true);
        segment.ToPlaceId = segment.FromPlaceId;
        segment.ToPlace = segment.FromPlace;
        segment.RouteGeometry = Line([new(0, 0), new(1, 1), new(0, 0)]);
        segment.EstimatedDistanceKm = SegmentMeasurementCalculator.CalculateDistance(segment.RouteGeometry.Coordinates).RoundedKilometres;
        using var stream = Stream(TripWayfarerKmlExporter.BuildKml(trip));
        var imported = Assert.Single(WayfarerKmlParser.ClassifyAndParse(stream).Document!.Segments);
        Assert.Equal(imported.FromPlaceId, imported.ToPlaceId);
        Assert.Equal(imported.Geometry!.GetCoordinateN(0), imported.Geometry.GetCoordinateN(2));
    }

    [Fact]
    public void Detection_TripIdWithoutNativeChildren_RemainsGeneric()
    {
        const string kml = """
            <kml xmlns="http://www.opengis.net/kml/2.2"><Document><ExtendedData>
            <Data name="TripId"><value>00000000-0000-0000-0000-000000000001</value></Data>
            </ExtendedData><Placemark><LineString><coordinates>0,0 1,1</coordinates></LineString></Placemark></Document></kml>
            """;
        using var stream = Stream(kml);
        var parsed = WayfarerKmlParser.ClassifyAndParse(stream);
        Assert.Equal(WayfarerKmlKind.Generic, parsed.Kind);
        Assert.Null(parsed.Document);
    }

    [Fact]
    public void Detection_DuplicateVersion_Rejects()
    {
        const string kml = """
            <kml xmlns="http://www.opengis.net/kml/2.2"><Document><ExtendedData>
            <Data name="WayfarerSchemaVersion"><value>2</value></Data>
            <Data name="WayfarerSchemaVersion"><value>2</value></Data>
            </ExtendedData></Document></kml>
            """;
        using var stream = Stream(kml);
        Assert.Throws<FormatException>(() => WayfarerKmlParser.ClassifyAndParse(stream));
    }

    /// <summary>Rejects persisted duration provenance outside the two native values before serialization.</summary>
    [Fact]
    public void Export_InvalidDurationProvenance_RejectsCompleteDocument()
    {
        var (trip, segment, _) = CreateWaypointTrip(true);
        segment.EstimatedDurationSource = (EstimatedDurationSource)99;
        segment.EstimatedDuration = null;

        Assert.Throws<InvalidOperationException>(() => TripWayfarerKmlExporter.BuildKml(trip));
    }

    /// <summary>Preserves explicit and structurally versionless v1 omission of Segments without LineString coordinates.</summary>
    [Fact]
    public void V1_SegmentWithoutLineString_IsOmitted()
    {
        var tripId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        foreach (var (versionData, lineString) in new[]
        {
            ("<Data name=\"WayfarerSchemaVersion\"><value>1</value></Data>", ""),
            ("", "<LineString><coordinates> </coordinates></LineString>")
        })
        {
            var kml = $"""
                <kml xmlns="http://www.opengis.net/kml/2.2"><Document><ExtendedData>
                {versionData}<Data name="TripId"><value>{tripId:D}</value></Data></ExtendedData>
                <Folder><name>Segments</name><Placemark><ExtendedData>
                <Data name="SegmentId"><value>{segmentId:D}</value></Data>
                <Data name="Mode"><value>walk</value></Data>
                </ExtendedData>{lineString}</Placemark></Folder></Document></kml>
                """;

            using var stream = Stream(kml);
            var parsed = WayfarerKmlParser.ClassifyAndParse(stream);
            Assert.Equal(WayfarerKmlKind.NativeV1, parsed.Kind);
            Assert.Empty(parsed.Document!.Segments);
        }
    }

    /// <summary>Rejects one Area identity reused by different Regions during detached document validation.</summary>
    [Fact]
    public void NativeDocument_DuplicateAreaIdentityAcrossRegions_Rejects()
    {
        var tripId = Guid.NewGuid();
        var areaId = Guid.NewGuid();
        var kml = $"""
            <kml xmlns="http://www.opengis.net/kml/2.2"><Document><ExtendedData>
            <Data name="WayfarerSchemaVersion"><value>1</value></Data>
            <Data name="TripId"><value>{tripId:D}</value></Data></ExtendedData>
            <Folder><name>R1</name><ExtendedData><Data name="RegionId"><value>{Guid.NewGuid():D}</value></Data></ExtendedData>
            <Placemark><ExtendedData><Data name="AreaId"><value>{areaId:D}</value></Data></ExtendedData>
            <Polygon><outerBoundaryIs><LinearRing><coordinates>0,0 1,0 1,1 0,0</coordinates></LinearRing></outerBoundaryIs></Polygon></Placemark></Folder>
            <Folder><name>R2</name><ExtendedData><Data name="RegionId"><value>{Guid.NewGuid():D}</value></Data></ExtendedData>
            <Placemark><ExtendedData><Data name="AreaId"><value>{areaId:D}</value></Data></ExtendedData>
            <Polygon><outerBoundaryIs><LinearRing><coordinates>2,2 3,2 3,3 2,2</coordinates></LinearRing></outerBoundaryIs></Polygon></Placemark></Folder>
            </Document></kml>
            """;

        using var stream = Stream(kml);
        Assert.Throws<FormatException>(() => WayfarerKmlParser.ClassifyAndParse(stream));
    }

    /// <summary>Keeps unrelated duplicate generic metadata outside native classification and parsing rules.</summary>
    [Fact]
    public void Detection_DuplicateUnrelatedGenericMetadata_RemainsGeneric()
    {
        const string kml = """
            <kml xmlns="http://www.opengis.net/kml/2.2"><Document><name>Generic</name><Placemark><name>walk</name>
            <ExtendedData><Data name="foo"><value>one</value></Data><Data name="foo"><value>two</value></Data></ExtendedData>
            <LineString><coordinates>0,0 0.5,0.25 1,1</coordinates></LineString></Placemark></Document></kml>
            """;
        using var classificationStream = Stream(kml);
        var classified = WayfarerKmlParser.ClassifyAndParse(classificationStream);

        using var genericStream = Stream(kml);
        var trip = GoogleMyMapsKmlParser.Parse(genericStream, "user1");
        var segment = Assert.Single(trip.Segments);
        Assert.Equal(WayfarerKmlKind.Generic, classified.Kind);
        Assert.Null(classified.Document);
        Assert.Equal(3, Assert.IsType<LineString>(segment.RouteGeometry).NumPoints);
        Assert.Empty(segment.Waypoints);
    }

    private static (Trip Trip, Segment Segment, SegmentWaypoint Waypoint) CreateWaypointTrip(bool custom)
    {
        var tripId = Guid.NewGuid();
        var regionId = Guid.NewGuid();
        var from = Place(regionId, 0, 0);
        var via = Place(regionId, 1, 1);
        var to = Place(regionId, 2, 2);
        var segment = new Segment
        {
            Id = Guid.NewGuid(), TripId = tripId, FromPlaceId = from.Id, FromPlace = from,
            ToPlaceId = to.Id, ToPlace = to, Mode = "walk", DisplayOrder = 0,
            RouteGeometry = custom ? Line([new(0, 0), new(1, 1), new(2, 2)]) : null,
            EstimatedDuration = TimeSpan.FromMinutes(1), EstimatedDurationSource = EstimatedDurationSource.Manual
        };
        var waypoint = new SegmentWaypoint
        {
            SegmentId = segment.Id, Segment = segment, PlaceId = via.Id, Place = via,
            Position = 0, RouteVertexIndex = custom ? 1 : null
        };
        segment.Waypoints.Add(waypoint);
        segment.EstimatedDistanceKm = SegmentMeasurementCalculator.CalculateDistance(
            new[] { new Coordinate(0, 0), new Coordinate(1, 1), new Coordinate(2, 2) }).RoundedKilometres;
        var region = new Region { Id = regionId, TripId = tripId, Places = [from, via, to] };
        from.Region = region;
        via.Region = region;
        to.Region = region;
        return (new Trip { Id = tripId, Name = "A to B to C", UpdatedAt = DateTime.UtcNow, Regions = [region], Segments = [segment] }, segment, waypoint);
    }

    private static Place Place(Guid regionId, double longitude, double latitude) => new()
    {
        Id = Guid.NewGuid(), RegionId = regionId, Location = new Point(longitude, latitude) { SRID = 4326 }
    };
    private static LineString Line(Coordinate[] coordinates) => new(coordinates) { SRID = 4326 };
    private static MemoryStream Stream(string value) => new(Encoding.UTF8.GetBytes(value));
}
