using System.Globalization;
using System.Text;
using System.Xml.Linq;
using NetTopologySuite.Geometries;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>Proves generic KML route caps, pre-entity budgeting, and bounded reporting.</summary>
public sealed class GenericKmlGeometryBudgetTests
{
    /// <summary>Proves oversized source geometry is budgeted before the resulting Segment is constructed.</summary>
    [Fact]
    public void Parse_OversizedRoute_ConstructsOnlyAcceptedFinalGeometry()
    {
        var source = Route(2_001, index => (index * 0.0001d, 40d));

        var result = Parse(Kml(source, "Budgeted route"));

        var segment = Assert.Single(result.Trip.Segments);
        var geometry = Assert.IsType<LineString>(segment.RouteGeometry);
        Assert.InRange(geometry.NumPoints, 2, 500);
        Assert.Equal((0d, 40d), Pair(geometry.GetCoordinateN(0)));
        Assert.Equal((0.2d, 40d), Pair(geometry.GetCoordinateN(geometry.NumPoints - 1)));
        Assert.Empty(segment.Waypoints);
        var notice = Assert.Single(result.Notices);
        Assert.Equal("generic_route_simplified", notice.Code);
        Assert.Equal(2_001, notice.OriginalCoordinateCount);
        Assert.Equal(geometry.NumPoints, notice.ResultingCoordinateCount);
    }

    /// <summary>Proves a generic route at the trigger remains coordinate-for-coordinate unchanged.</summary>
    [Fact]
    public void Parse_SmallGenericRoute_RemainsExactWithoutNotice()
    {
        var source = Enumerable.Range(0, 1_000).Select(index => (index * 0.001d, 10d)).ToArray();

        var result = Parse(Kml(CoordinateText(source), "Exact route"));

        var geometry = Assert.IsType<LineString>(Assert.Single(result.Trip.Segments).RouteGeometry);
        Assert.Equal(source, geometry.Coordinates.Select(Pair));
        Assert.Empty(result.Notices);
    }

    /// <summary>Proves every fixed generic document and route input cap returns its stable code.</summary>
    [Theory]
    [InlineData(101, 2, "generic_kml_segment_limit")]
    [InlineData(1, 100_001, "generic_kml_linestring_input_limit")]
    [InlineData(3, 83_334, "generic_kml_document_input_limit")]
    public void Parse_InputCapExceeded_RejectsWithStableCode(int routes, int coordinates, string code)
    {
        var route = Route(coordinates, index => (index * 0.000001d, 1d));
        var document = KmlDocument(Enumerable.Range(0, routes).Select(index => (route, $"Route {index}")));

        var error = Assert.Throws<RouteGeometryBudgetException>(() => Parse(document));

        Assert.Equal(code, error.Code);
    }

    /// <summary>Proves accepted small routes cannot exceed the aggregate persisted Trip limit.</summary>
    [Fact]
    public void Parse_AggregatePersistedCapExceeded_Rejects()
    {
        var route = Route(1_000, index => (index * 0.0001d, 5d));
        var document = KmlDocument(Enumerable.Range(0, 11).Select(index => (route, $"Route {index}")));

        var error = Assert.Throws<RouteGeometryBudgetException>(() => Parse(document));

        Assert.Equal("generic_kml_persisted_limit", error.Code);
    }

    /// <summary>Proves reporting is bounded to twenty route notices plus one aggregate notice.</summary>
    [Fact]
    public void Parse_ManySimplifiedRoutes_BoundsNoticesAndNames()
    {
        var route = Route(1_001, index => (index * 0.0001d, 15d));
        var longName = new string('R', 200);
        var document = KmlDocument(Enumerable.Range(0, 21).Select(index => (route, $"{longName}{index}")));

        var result = Parse(document);

        Assert.Equal(21, result.Notices.Count);
        Assert.All(result.Notices.Take(20), notice =>
        {
            Assert.Equal("generic_route_simplified", notice.Code);
            Assert.InRange(notice.SegmentName.Length, 1, 120);
        });
        Assert.Equal("generic_routes_simplified_additional", result.Notices[^1].Code);
        Assert.Equal(1, result.Notices[^1].AdditionalRouteCount);
    }

    /// <summary>Proves generic route coordinates reject malformed, non-finite, range, and antipodal values.</summary>
    [Theory]
    [InlineData("0,0 broken")]
    [InlineData("0,0 NaN,1")]
    [InlineData("0,0 181,1")]
    [InlineData("0,0 180,0")]
    public void Parse_InvalidRouteCoordinate_Rejects(string coordinates)
    {
        var error = Assert.Throws<RouteGeometryBudgetException>(() => Parse(Kml(coordinates, "Invalid")));

        Assert.Equal("generic_kml_invalid_coordinate", error.Code);
    }

    /// <summary>Proves cancellation is propagated at the generic route boundary.</summary>
    [Fact]
    public void Parse_CancelledRequest_StopsBeforeRouteWork()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => GoogleMyMapsKmlParser.Parse(
            XDocument.Parse(Kml(Route(2, index => (index, index)), "Route")),
            "user", cancellation.Token));
    }

    private static GenericKmlParseResult Parse(string source) => GoogleMyMapsKmlParser.Parse(
        XDocument.Parse(source), "user", CancellationToken.None);
    private static string Kml(string coordinates, string name) => KmlDocument([(coordinates, name)]);
    private static string KmlDocument(IEnumerable<(string Coordinates, string Name)> routes) =>
        $"<kml xmlns=\"http://www.opengis.net/kml/2.2\"><Document><name>Generic</name>{string.Concat(routes.Select(route => $"<Placemark><name>{route.Name}</name><LineString><coordinates>{route.Coordinates}</coordinates></LineString></Placemark>"))}</Document></kml>";
    private static string Route(int count, Func<int, (double Longitude, double Latitude)> coordinate) =>
        CoordinateText(Enumerable.Range(0, count).Select(coordinate));
    private static string CoordinateText(IEnumerable<(double Longitude, double Latitude)> coordinates) =>
        string.Join(' ', coordinates.Select(coordinate => string.Create(
            CultureInfo.InvariantCulture, $"{coordinate.Longitude:R},{coordinate.Latitude:R}")));
    private static (double Longitude, double Latitude) Pair(Coordinate coordinate) => (coordinate.X, coordinate.Y);
}
