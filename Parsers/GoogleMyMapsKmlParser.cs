using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;

namespace Wayfarer.Parsers;

/// <summary>Parses the supported direct generic KML shapes after route geometry has been budgeted.</summary>
public static class GoogleMyMapsKmlParser
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly XNamespace Kml = "http://www.opengis.net/kml/2.2";
    private const int MaximumSegments = 100;
    private const int MaximumDocumentCoordinates = 250_000;
    private const int MaximumPersistedCoordinates = 10_000;
    private const int MaximumNotices = 20;
    private const int MaximumNoticeNameLength = 120;

    /// <summary>Compatibility entry point that performs one hardened parse for direct parser callers.</summary>
    public static Trip Parse(Stream stream, string userId)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 10 * 1024 * 1024
        };
        using var reader = XmlReader.Create(stream, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        return Parse(document, userId, CancellationToken.None).Trip;
    }

    /// <summary>Budgets every supported direct LineString before constructing any Segment entity.</summary>
    public static GenericKmlParseResult Parse(
        XDocument document,
        string userId,
        CancellationToken cancellationToken)
    {
        var root = document.Root?.Element(Kml + "Document")
            ?? throw new FormatException("Missing KML Document.");
        var lineOwners = root.Elements(Kml + "Folder").SelectMany(folder => folder.Elements(Kml + "Placemark"))
            .Concat(root.Elements(Kml + "Placemark"))
            .Where(placemark => placemark.Element(Kml + "LineString") is not null)
            .ToArray();
        if (lineOwners.Length > MaximumSegments)
            throw new RouteGeometryBudgetException(
                "generic_kml_segment_limit", "The KML contains more than 100 routes.");

        var rawRoutes = new List<RawRoute>(lineOwners.Length);
        var aggregateInput = 0;
        foreach (var placemark in lineOwners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var coordinates = ParseRouteCoordinates(placemark);
            aggregateInput = checked(aggregateInput + coordinates.Count);
            if (aggregateInput > MaximumDocumentCoordinates)
                throw new RouteGeometryBudgetException(
                    "generic_kml_document_input_limit",
                    "The KML contains more than 250,000 route coordinates.");
            rawRoutes.Add(new(placemark, coordinates));
        }

        var work = new RouteGeometryBudgetWork();
        var acceptedRoutes = new List<AcceptedRoute>(rawRoutes.Count);
        var aggregatePersisted = 0;
        foreach (var route in rawRoutes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var budget = RouteGeometryBudgeter.Budget(
                route.Coordinates, [0, route.Coordinates.Count - 1], work, cancellationToken);
            aggregatePersisted = checked(aggregatePersisted + budget.Coordinates.Count);
            if (aggregatePersisted > MaximumPersistedCoordinates)
                throw new RouteGeometryBudgetException(
                    "generic_kml_persisted_limit", "The imported routes exceed the Trip geometry limit.");
            acceptedRoutes.Add(new(route.Owner, budget));
        }

        var trip = BuildTrip(root, acceptedRoutes, userId);
        return new(trip, BuildNotices(acceptedRoutes));
    }

    private static IReadOnlyList<Coordinate> ParseRouteCoordinates(XElement placemark)
    {
        var text = placemark.Element(Kml + "LineString")?.Element(Kml + "coordinates")?.Value;
        if (string.IsNullOrWhiteSpace(text)) throw InvalidCoordinate();
        var coordinates = new List<Coordinate>();
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (coordinates.Count == RouteGeometryBudgeter.MaximumInputCoordinates)
                throw new RouteGeometryBudgetException(
                    "generic_kml_linestring_input_limit",
                    "A route contains more than 100,000 coordinates.");
            var parts = token.Split(',');
            if (parts.Length < 2
                || !double.TryParse(parts[0], NumberStyles.Float, Invariant, out var longitude)
                || !double.TryParse(parts[1], NumberStyles.Float, Invariant, out var latitude))
                throw InvalidCoordinate();
            coordinates.Add(new(longitude, latitude));
        }
        if (coordinates.Count < 2) throw InvalidCoordinate();
        return coordinates;
    }

    private static Trip BuildTrip(
        XElement root,
        IReadOnlyList<AcceptedRoute> acceptedRoutes,
        string userId)
    {
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            Name = root.Element(Kml + "name")?.Value ?? "Imported My Maps",
            UserId = userId,
            Regions = [],
            Segments = [],
            UpdatedAt = DateTime.UtcNow
        };
        XNamespace wayfarer = "https://wayfarer.stefk.me/kml";
        var tags = root.Element(Kml + "ExtendedData")?.Element(wayfarer + "Tags")?.Value;
        if (!string.IsNullOrWhiteSpace(tags))
            trip.Tags = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(slug => new Tag { Slug = slug, Name = slug }).ToList();

        foreach (var folder in root.Elements(Kml + "Folder"))
        {
            var region = new Region
            {
                Id = Guid.NewGuid(),
                TripId = trip.Id,
                UserId = userId,
                Name = StripPrefix(folder.Element(Kml + "name")?.Value),
                Places = []
            };
            foreach (var placemark in folder.Elements(Kml + "Placemark"))
            {
                if (placemark.Element(Kml + "Point") is not null)
                    region.Places.Add(ParsePlace(placemark, region.Id, userId));
                else if (placemark.Element(Kml + "Polygon") is not null)
                {
                    region.Areas ??= [];
                    region.Areas.Add(ParseArea(placemark, region.Id));
                }
            }
            trip.Regions.Add(region);
        }
        foreach (var route in acceptedRoutes)
            trip.Segments.Add(BuildSegment(route, trip.Id, userId));
        LinkSegmentsToPlaces(trip);
        SetCenterFromPlaces(trip);
        return trip;
    }

    private static Segment BuildSegment(AcceptedRoute route, Guid tripId, string userId) => new()
    {
        Id = Guid.NewGuid(),
        TripId = tripId,
        UserId = userId,
        Mode = route.Owner.Element(Kml + "name")?.Value ?? "drive",
        RouteGeometry = new LineString(route.Budget.Coordinates.ToArray()) { SRID = 4326 },
        EstimatedDistanceKm = null,
        EstimatedDuration = null,
        EstimatedDurationSource = EstimatedDurationSource.Automatic,
        Waypoints = []
    };

    private static IReadOnlyList<TripImportNotice> BuildNotices(IReadOnlyList<AcceptedRoute> routes)
    {
        var simplified = routes.Where(route => route.Budget.WasSimplified).ToArray();
        var notices = simplified.Take(MaximumNotices).Select(route => new TripImportNotice(
            "generic_route_simplified",
            BoundedName(route.Owner.Element(Kml + "name")?.Value),
            route.Budget.OriginalCoordinateCount,
            route.Budget.Coordinates.Count,
            route.Budget.ToleranceMetres,
            route.Budget.MaximumDeviationMetres)).ToList();
        if (simplified.Length > MaximumNotices)
            notices.Add(new(
                "generic_routes_simplified_additional", "Additional routes simplified",
                null, null, null, null, simplified.Length - MaximumNotices));
        return notices;
    }

    private static Place ParsePlace(XElement placemark, Guid regionId, string userId)
    {
        var token = placemark.Element(Kml + "Point")?.Element(Kml + "coordinates")?.Value;
        var parts = token?.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (parts is null || parts.Length < 2
            || !double.TryParse(parts[0], NumberStyles.Float, Invariant, out var longitude)
            || !double.TryParse(parts[1], NumberStyles.Float, Invariant, out var latitude))
            throw new FormatException("Point has an invalid coordinate.");
        return new()
        {
            Id = Guid.NewGuid(), RegionId = regionId, UserId = userId,
            Name = placemark.Element(Kml + "name")?.Value ?? "Place",
            Notes = placemark.Element(Kml + "description")?.Value,
            Location = new Point(longitude, latitude) { SRID = 4326 }
        };
    }

    private static Area ParseArea(XElement placemark, Guid regionId)
    {
        var text = placemark.Element(Kml + "Polygon")?.Element(Kml + "outerBoundaryIs")?
            .Element(Kml + "LinearRing")?.Element(Kml + "coordinates")?.Value;
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("Polygon has no coordinates.");
        var coordinates = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(token =>
        {
            var parts = token.Split(',');
            if (parts.Length < 2
                || !double.TryParse(parts[0], NumberStyles.Float, Invariant, out var longitude)
                || !double.TryParse(parts[1], NumberStyles.Float, Invariant, out var latitude))
                throw new FormatException("Polygon has an invalid coordinate.");
            return new Coordinate(longitude, latitude);
        }).ToArray();
        return new()
        {
            Id = Guid.NewGuid(), RegionId = regionId,
            Name = placemark.Element(Kml + "name")?.Value ?? "Area",
            Notes = placemark.Element(Kml + "description")?.Value,
            Geometry = new Polygon(new LinearRing(coordinates)) { SRID = 4326 }
        };
    }

    private static void LinkSegmentsToPlaces(Trip trip)
    {
        const double maximumDistanceKm = 0.2;
        var places = trip.Regions.SelectMany(region => region.Places).ToArray();
        foreach (var segment in trip.Segments)
        {
            if (segment.RouteGeometry is not LineString line) continue;
            segment.FromPlaceId = FindNearest(line.StartPoint, places, maximumDistanceKm);
            segment.ToPlaceId = FindNearest(line.EndPoint, places, maximumDistanceKm);
        }
        static Guid? FindNearest(Point point, IEnumerable<Place> places, double limitKm)
        {
            var nearest = places.Select(place => new { place.Id, Distance = point.Distance(place.Location) * 111d })
                .OrderBy(item => item.Distance).FirstOrDefault();
            return nearest is not null && nearest.Distance <= limitKm ? nearest.Id : null;
        }
    }

    private static void SetCenterFromPlaces(Trip trip)
    {
        var points = trip.Regions.SelectMany(region => region.Places).Select(place => place.Location)
            .Where(point => point is not null).Cast<Point>().ToArray();
        if (points.Length == 0) return;
        trip.CenterLat = points.Average(point => point.Y);
        trip.CenterLon = points.Average(point => point.X);
        trip.Zoom = 5;
    }

    private static string StripPrefix(string? value) => string.IsNullOrWhiteSpace(value)
        ? "Unnamed layer" : Regex.Replace(value, @"^\d+\s*[-–]\s*", "");
    private static string BoundedName(string? value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "Unnamed route" : value.Trim();
        return name.Length <= MaximumNoticeNameLength ? name : name[..MaximumNoticeNameLength];
    }
    private static RouteGeometryBudgetException InvalidCoordinate() => new(
        "generic_kml_invalid_coordinate", "A route contains an invalid coordinate.");

    private sealed record RawRoute(XElement Owner, IReadOnlyList<Coordinate> Coordinates);
    private sealed record AcceptedRoute(XElement Owner, RouteGeometryBudgetResult Budget);
}

/// <summary>Detached generic import graph plus bounded route simplification notices.</summary>
public sealed record GenericKmlParseResult(Trip Trip, IReadOnlyList<TripImportNotice> Notices);
