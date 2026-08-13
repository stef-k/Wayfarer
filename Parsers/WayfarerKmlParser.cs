using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;

namespace Wayfarer.Parsers;

/// <summary>Safely classifies and parses versioned Wayfarer-native KML into detached transport data.</summary>
public static class WayfarerKmlParser
{
    private static readonly XNamespace Kml = "http://www.opengis.net/kml/2.2";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly HashSet<string> V2OnlyFields =
        ["TransportProfileKey", "DurationSeconds", "DurationSource", "HasCustomRoute", "WaypointPlaceIds", "WaypointRouteVertexIndices"];

    /// <summary>Parses XML once and returns its structural kind plus native transport data when applicable.</summary>
    public static (WayfarerKmlKind Kind, WayfarerKmlDocument? Document) ClassifyAndParse(Stream xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 10 * 1024 * 1024,
            IgnoreComments = true
        };
        using var reader = XmlReader.Create(xml, settings);
        var source = XDocument.Load(reader, LoadOptions.SetLineInfo);
        var document = source.Root?.Element(Kml + "Document") ?? throw new FormatException("Missing KML Document.");
        var versionValues = Values(document, "WayfarerSchemaVersion");
        if (versionValues.Count > 1) throw new FormatException("Duplicate Wayfarer schema version metadata.");
        var hasV2Fields = document.Descendants(Kml + "Placemark").Any(owner =>
            DirectData(owner).Keys.Any(V2OnlyFields.Contains));

        WayfarerKmlKind kind;
        if (versionValues.Count == 1)
        {
            kind = versionValues[0].Trim() switch
            {
                "1" => WayfarerKmlKind.NativeV1,
                "2" => WayfarerKmlKind.NativeV2,
                _ => throw new FormatException("Unsupported Wayfarer schema version.")
            };
            if (kind == WayfarerKmlKind.NativeV1 && hasV2Fields)
                throw new FormatException("Version 1 KML cannot contain version 2 Segment metadata.");
        }
        else
        {
            if (hasV2Fields) throw new FormatException("Version 2 Segment metadata requires an explicit schema version.");
            var tripIds = Values(document, "TripId");
            var nativeChild = document.Elements(Kml + "Folder").Any(folder =>
                Values(folder, "RegionId").Count > 0 || folder.Elements(Kml + "Placemark").Any(pm => Values(pm, "SegmentId").Count > 0));
            kind = tripIds.Count == 1 && nativeChild ? WayfarerKmlKind.NativeV1 : WayfarerKmlKind.Generic;
            if (tripIds.Count > 1) throw new FormatException("Duplicate Trip identity metadata.");
        }
        return kind == WayfarerKmlKind.Generic ? (kind, null) : (kind, ParseDocument(document, kind == WayfarerKmlKind.NativeV2 ? 2 : 1));
    }

    /// <summary>Compatibility wrapper for existing v1 parser callers.</summary>
    public static Trip Parse(Stream xml)
    {
        var (_, document) = ClassifyAndParse(xml);
        if (document is null) throw new FormatException("The document is not Wayfarer-native KML.");
        return ToCompatibilityTrip(document);
    }

    private static WayfarerKmlDocument ParseDocument(XElement owner, int version)
    {
        var tripId = RequiredGuid(owner, "TripId");
        var regions = owner.Elements(Kml + "Folder").Where(folder => folder.Element(Kml + "name")?.Value != "Segments")
            .Select(folder => ParseRegion(folder, version)).ToArray();
        EnsureUnique(regions.Select(region => region.Id), "Region");
        var segmentsFolder = owner.Elements(Kml + "Folder").SingleOrDefault(folder => folder.Element(Kml + "name")?.Value == "Segments");
        var segments = segmentsFolder?.Elements(Kml + "Placemark").Select(pm => ParseSegment(pm, version)).ToArray() ?? [];
        EnsureUnique(segments.Select(segment => segment.Id), "Segment");
        return new(version, tripId, owner.Element(Kml + "name")?.Value ?? "Imported trip",
            Scalar(owner, "CoverImageUrl"), Scalar(owner, "NotesHtml"), OptionalDouble(owner, "CenterLat"),
            OptionalDouble(owner, "CenterLon"), OptionalInt(owner, "Zoom"), Tokens(Scalar(owner, "Tags")), regions, segments);
    }

    private static WayfarerKmlRegion ParseRegion(XElement owner, int version)
    {
        var id = RequiredGuid(owner, "RegionId");
        var places = owner.Elements(Kml + "Placemark").Where(pm => pm.Element(Kml + "Polygon") is null)
            .Select(pm => ParsePlace(pm, version)).ToArray();
        var areas = owner.Elements(Kml + "Placemark").Where(pm => pm.Element(Kml + "Polygon") is not null)
            .Select(pm => ParseArea(pm, version)).ToArray();
        EnsureUnique(places.Select(place => place.Id), "Place");
        EnsureUnique(areas.Select(area => area.Id), "Area");
        var lat = OptionalDouble(owner, "CenterLat");
        var lon = OptionalDouble(owner, "CenterLon");
        if (lat.HasValue != lon.HasValue) throw new FormatException("Region center coordinates must be complete.");
        return new(id, owner.Element(Kml + "name")?.Value ?? "Region", OptionalInt(owner, "DisplayOrder") ?? 0,
            Scalar(owner, "NotesHtml"), lat.HasValue ? Point(lon!.Value, lat.Value) : null, places, areas);
    }

    private static WayfarerKmlPlace ParsePlace(XElement owner, int version)
    {
        var coordinate = owner.Element(Kml + "Point")?.Element(Kml + "coordinates")?.Value;
        return new(RequiredGuid(owner, "PlaceId"), owner.Element(Kml + "name")?.Value ?? "Place",
            OptionalInt(owner, "DisplayOrder") ?? 0, Scalar(owner, "NotesHtml"), Scalar(owner, "IconName"),
            Scalar(owner, "MarkerColor"), Scalar(owner, "Address"), string.IsNullOrWhiteSpace(coordinate) ? null : ParsePoint(coordinate));
    }

    private static WayfarerKmlArea ParseArea(XElement owner, int version)
    {
        var coordinates = owner.Element(Kml + "Polygon")?.Element(Kml + "outerBoundaryIs")?
            .Element(Kml + "LinearRing")?.Element(Kml + "coordinates")?.Value;
        Polygon? polygon = null;
        if (!string.IsNullOrWhiteSpace(coordinates))
            polygon = new Polygon(new LinearRing(Coordinates(coordinates).ToArray())) { SRID = 4326 };
        return new(RequiredGuid(owner, "AreaId"), owner.Element(Kml + "name")?.Value ?? "Area",
            OptionalInt(owner, "DisplayOrder") ?? 0, Scalar(owner, "NotesHtml"), Scalar(owner, "FillHex"), polygon);
    }

    private static WayfarerKmlSegment ParseSegment(XElement owner, int version)
    {
        var geometryText = owner.Element(Kml + "LineString")?.Element(Kml + "coordinates")?.Value;
        var geometry = string.IsNullOrWhiteSpace(geometryText) ? null : new LineString(Coordinates(geometryText).ToArray()) { SRID = 4326 };
        if (version == 1)
        {
            var minutes = OptionalDouble(owner, "DurationMin");
            return new(RequiredGuid(owner, "SegmentId"), OptionalGuid(owner, "FromPlaceId"), OptionalGuid(owner, "ToPlaceId"),
                Scalar(owner, "Mode") ?? "", "", null,
                minutes.HasValue ? (long?)SegmentMeasurementCalculator.NormalizeManualDuration(minutes.Value).TotalSeconds : null,
                minutes.HasValue ? EstimatedDurationSource.Manual : EstimatedDurationSource.Automatic,
                OptionalInt(owner, "DisplayOrder") ?? 0, Scalar(owner, "NotesHtml"), geometry is not null, [], [], geometry);
        }
        var waypointIds = GuidTokens(RequiredScalar(owner, "WaypointPlaceIds"));
        var indices = IndexTokens(RequiredScalar(owner, "WaypointRouteVertexIndices"));
        if (waypointIds.Count != indices.Count) throw new FormatException("Waypoint collection lengths do not match.");
        var source = RequiredScalar(owner, "DurationSource") switch
        {
            "Automatic" => EstimatedDurationSource.Automatic,
            "Manual" => EstimatedDurationSource.Manual,
            _ => throw new FormatException("Invalid duration provenance.")
        };
        return new(RequiredGuid(owner, "SegmentId"), OptionalGuid(owner, "FromPlaceId"), OptionalGuid(owner, "ToPlaceId"),
            RequiredScalar(owner, "Mode"), RequiredScalar(owner, "TransportProfileKey"), OptionalDouble(owner, "DistanceKm"),
            OptionalLong(owner, "DurationSeconds"), source, RequiredInt(owner, "DisplayOrder"), Scalar(owner, "NotesHtml"),
            RequiredBool(owner, "HasCustomRoute"), waypointIds, indices, geometry);
    }

    private static Trip ToCompatibilityTrip(WayfarerKmlDocument source)
    {
        var trip = new Trip { Id = source.TripId, Name = source.Name, CoverImageUrl = source.CoverImageUrl, Notes = source.Notes,
            CenterLat = source.CenterLat, CenterLon = source.CenterLon, Zoom = source.Zoom, UpdatedAt = DateTime.UtcNow, IsPublic = false,
            Tags = source.Tags.Select(token => new Tag { Slug = token, Name = token }).ToList() };
        trip.Regions = source.Regions.Select(region => new Region { Id = region.Id, TripId = source.TripId, Name = region.Name,
            DisplayOrder = region.DisplayOrder, Notes = region.Notes, Center = region.Center,
            Places = region.Places.Select(place => new Place { Id = place.Id, RegionId = region.Id, Name = place.Name,
                DisplayOrder = place.DisplayOrder, Notes = place.Notes, IconName = place.IconName, MarkerColor = place.MarkerColor,
                Address = place.Address, Location = place.Location }).ToList(),
            Areas = region.Areas.Select(area => new Area { Id = area.Id, RegionId = region.Id, Name = area.Name,
                DisplayOrder = area.DisplayOrder, Notes = area.Notes, FillHex = area.FillHex,
                Geometry = area.Geometry ?? throw new FormatException("Area geometry is required.") }).ToList() }).ToList();
        var places = trip.Regions.SelectMany(region => region.Places.Select(place => (region, place))).ToDictionary(item => item.place.Id);
        foreach (var (region, place) in places.Values) place.Region = region;
        trip.Segments = source.Segments.Select(segment => new Segment { Id = segment.Id, TripId = source.TripId,
            FromPlaceId = segment.FromPlaceId, ToPlaceId = segment.ToPlaceId, Mode = segment.Mode, EstimatedDistanceKm = null,
            EstimatedDuration = segment.DurationSeconds.HasValue ? TimeSpan.FromSeconds(segment.DurationSeconds.Value) : null,
            EstimatedDurationSource = segment.DurationSource, DisplayOrder = segment.DisplayOrder, Notes = segment.Notes,
            RouteGeometry = segment.HasCustomRoute ? segment.Geometry : null }).ToList();
        return trip;
    }

    private static Dictionary<string, string> DirectData(XElement owner)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var data in owner.Elements(Kml + "ExtendedData").Elements(Kml + "Data"))
        {
            var name = (string?)data.Attribute("name");
            if (string.IsNullOrEmpty(name)) continue;
            if (!result.TryAdd(name, data.Element(Kml + "value")?.Value ?? "")) throw new FormatException($"Duplicate {name} metadata.");
        }
        return result;
    }

    private static IReadOnlyList<string> Values(XElement owner, string name) => owner.Elements(Kml + "ExtendedData")
        .Elements(Kml + "Data").Where(data => (string?)data.Attribute("name") == name)
        .Select(data => data.Element(Kml + "value")?.Value ?? "").ToArray();
    private static string? Scalar(XElement owner, string name) => DirectData(owner).TryGetValue(name, out var value) ? value.Trim() : null;
    private static string RequiredScalar(XElement owner, string name) => Scalar(owner, name) ?? throw new FormatException($"Missing {name} metadata.");
    private static Guid RequiredGuid(XElement owner, string name) => Guid.TryParseExact(RequiredScalar(owner, name), "D", out var value) ? value : throw new FormatException($"Invalid {name} identity.");
    private static Guid? OptionalGuid(XElement owner, string name) => RequiredOrEmpty(owner, name) is { Length: > 0 } value && Guid.TryParseExact(value, "D", out var guid) ? guid : string.IsNullOrEmpty(RequiredOrEmpty(owner, name)) ? null : throw new FormatException($"Invalid {name} identity.");
    private static string RequiredOrEmpty(XElement owner, string name) => Scalar(owner, name) ?? "";
    private static int RequiredInt(XElement owner, string name) => int.TryParse(RequiredScalar(owner, name), NumberStyles.None, Invariant, out var value) && value >= 0 ? value : throw new FormatException($"Invalid {name} integer.");
    private static int? OptionalInt(XElement owner, string name) => Scalar(owner, name) is { Length: > 0 } value ? int.TryParse(value, NumberStyles.Integer, Invariant, out var parsed) ? parsed : throw new FormatException($"Invalid {name} integer.") : null;
    private static long? OptionalLong(XElement owner, string name) => Scalar(owner, name) is { Length: > 0 } value ? long.TryParse(value, NumberStyles.None, Invariant, out var parsed) && parsed >= 0 ? parsed : throw new FormatException($"Invalid {name} integer.") : null;
    private static double? OptionalDouble(XElement owner, string name) => Scalar(owner, name) is { Length: > 0 } value ? double.TryParse(value, NumberStyles.Float, Invariant, out var parsed) && double.IsFinite(parsed) ? parsed : throw new FormatException($"Invalid {name} number.") : null;
    private static bool RequiredBool(XElement owner, string name) => RequiredScalar(owner, name) switch { "true" => true, "false" => false, _ => throw new FormatException($"Invalid {name} Boolean.") };
    private static IReadOnlyList<string> Tokens(string? value) => string.IsNullOrEmpty(value) ? [] : value.Split(',', StringSplitOptions.None);
    private static IReadOnlyList<Guid> GuidTokens(string value) => Tokens(value).Select(token => Guid.TryParseExact(token, "D", out var id) && token == token.Trim() ? id : throw new FormatException("Invalid waypoint identity token.")).ToArray();
    private static IReadOnlyList<int?> IndexTokens(string value) => Tokens(value).Select(token => token == "null" ? (int?)null : int.TryParse(token, NumberStyles.None, Invariant, out var index) && index >= 0 && token == token.Trim() ? index : throw new FormatException("Invalid waypoint index token.")).ToArray();
    private static IEnumerable<Coordinate> Coordinates(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(ParseCoordinate);
    private static Point ParsePoint(string text) { var coordinate = ParseCoordinate(text); return Point(coordinate.X, coordinate.Y); }
    private static Point Point(double longitude, double latitude) => new(longitude, latitude) { SRID = 4326 };
    private static Coordinate ParseCoordinate(string token)
    {
        var parts = token.Split(',');
        if (parts.Length < 2 || !double.TryParse(parts[0], NumberStyles.Float, Invariant, out var longitude)
            || !double.TryParse(parts[1], NumberStyles.Float, Invariant, out var latitude) || !double.IsFinite(longitude)
            || !double.IsFinite(latitude) || longitude is < -180 or > 180 || latitude is < -90 or > 90)
            throw new FormatException("Invalid KML coordinate.");
        return new(longitude, latitude);
    }
    private static void EnsureUnique(IEnumerable<Guid> ids, string label) { var seen = new HashSet<Guid>(); if (ids.Any(id => id == Guid.Empty || !seen.Add(id))) throw new FormatException($"Duplicate or empty {label} identity."); }
}
