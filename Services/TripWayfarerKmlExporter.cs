using System.Globalization;
using System.Xml.Linq;
using NetTopologySuite.Geometries;
using Wayfarer.Models;
using Wayfarer.Services;

namespace Wayfarer.Parsers;

/// <summary>Serializes a completely loaded and validated Trip as deterministic Wayfarer-native KML v2.</summary>
public static class TripWayfarerKmlExporter
{
    private static readonly XNamespace Kml = "http://www.opengis.net/kml/2.2";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Builds one native v2 document or rejects the complete malformed aggregate.</summary>
    public static string BuildKml(Trip trip)
    {
        ArgumentNullException.ThrowIfNull(trip);
        ValidateAggregate(trip);

        var document = new XElement(Kml + "Document", new XElement(Kml + "name", trip.Name));
        AddData(document,
            ("WayfarerSchemaVersion", "2"),
            ("TripId", GuidText(trip.Id)),
            ("UpdatedAt", trip.UpdatedAt.ToString("O", Invariant)),
            ("CoverImageUrl", trip.CoverImageUrl ?? ""),
            ("NotesHtml", trip.Notes ?? ""),
            ("CenterLat", Number(trip.CenterLat)),
            ("CenterLon", Number(trip.CenterLon)),
            ("Zoom", Number(trip.Zoom)),
            ("Tags", string.Join(',', trip.Tags.OrderBy(tag => tag.Name).Select(tag => tag.Slug))));
        document.Add(trip.Regions.SelectMany(region => region.Places)
            .Select(place => (place.IconName, place.MarkerColor)).Distinct()
            .Select(icon => new XElement(Kml + "Style", new XAttribute("id", $"wf_{icon.IconName}_{icon.MarkerColor}"),
                new XElement(Kml + "IconStyle", new XElement(Kml + "Icon",
                    new XElement(Kml + "href", $"/icons/wayfarer-map-icons/dist/png/marker/{icon.MarkerColor}/{icon.IconName}.png"))))));

        foreach (var region in trip.Regions.OrderBy(region => region.DisplayOrder).ThenBy(region => region.Id))
            document.Add(BuildRegion(region, trip.Id));

        if (trip.Segments.Count > 0)
        {
            var folder = new XElement(Kml + "Folder", new XElement(Kml + "name", "Segments"));
            foreach (var segment in trip.Segments.OrderBy(segment => segment.DisplayOrder).ThenBy(segment => segment.Id))
                folder.Add(BuildSegment(segment, trip.Id));
            document.Add(folder);
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", "yes"),
            new XElement(Kml + "kml", document)).ToString();
    }

    private static XElement BuildRegion(Region region, Guid tripId)
    {
        var folder = new XElement(Kml + "Folder", new XElement(Kml + "name", region.Name));
        AddData(folder,
            ("RegionId", GuidText(region.Id)), ("TripId", GuidText(tripId)),
            ("DisplayOrder", Number(region.DisplayOrder)), ("NotesHtml", region.Notes ?? ""),
            ("CenterLat", Number(region.Center?.Y)), ("CenterLon", Number(region.Center?.X)));
        foreach (var place in region.Places.OrderBy(place => place.DisplayOrder).ThenBy(place => place.Id))
            folder.Add(BuildPlace(place, region.Id));
        foreach (var area in region.Areas.OrderBy(area => area.DisplayOrder).ThenBy(area => area.Id))
            folder.Add(BuildArea(area, region.Id));
        return folder;
    }

    private static XElement BuildPlace(Place place, Guid regionId)
    {
        var placemark = new XElement(Kml + "Placemark", new XElement(Kml + "name", place.Name),
            new XElement(Kml + "styleUrl", $"#wf_{place.IconName}_{place.MarkerColor}"));
        AddData(placemark,
            ("PlaceId", GuidText(place.Id)), ("RegionId", GuidText(regionId)),
            ("DisplayOrder", Number(place.DisplayOrder)), ("NotesHtml", place.Notes ?? ""),
            ("IconName", place.IconName ?? ""), ("MarkerColor", place.MarkerColor ?? ""),
            ("Address", place.Address ?? ""));
        if (place.Location is not null)
            placemark.Add(new XElement(Kml + "Point", new XElement(Kml + "coordinates", CoordinateText(place.Location.Coordinate))));
        return placemark;
    }

    private static XElement BuildArea(Area area, Guid regionId)
    {
        var placemark = new XElement(Kml + "Placemark", new XElement(Kml + "name", area.Name));
        AddData(placemark,
            ("AreaId", GuidText(area.Id)), ("RegionId", GuidText(regionId)),
            ("DisplayOrder", Number(area.DisplayOrder)), ("FillHex", area.FillHex ?? ""),
            ("NotesHtml", area.Notes ?? ""));
        if (area.Geometry is Polygon polygon)
            placemark.Add(new XElement(Kml + "Polygon", new XElement(Kml + "outerBoundaryIs",
                new XElement(Kml + "LinearRing", new XElement(Kml + "coordinates",
                    string.Join(' ', polygon.Coordinates.Select(CoordinateText)))))));
        return placemark;
    }

    private static XElement BuildSegment(Segment segment, Guid tripId)
    {
        var waypoints = segment.Waypoints.OrderBy(waypoint => waypoint.Position).ToArray();
        var hasCustomRoute = segment.RouteGeometry is not null;
        var line = segment.RouteGeometry ?? BuildFallback(segment, waypoints);
        var placemark = new XElement(Kml + "Placemark", new XElement(Kml + "name", segment.Mode));
        AddData(placemark,
            ("SegmentId", GuidText(segment.Id)), ("FromPlaceId", GuidText(segment.FromPlaceId)),
            ("ToPlaceId", GuidText(segment.ToPlaceId)), ("Mode", segment.Mode),
            ("TransportProfileKey", segment.TransportProfile?.Key ?? ""),
            ("DistanceKm", segment.EstimatedDistanceKm?.ToString("0.###", Invariant) ?? ""),
            ("DurationSeconds", segment.EstimatedDuration?.TotalSeconds.ToString("0", Invariant) ?? ""),
            ("DurationSource", segment.EstimatedDurationSource.ToString()),
            ("DisplayOrder", Number(segment.DisplayOrder)), ("NotesHtml", segment.Notes ?? ""),
            ("HasCustomRoute", hasCustomRoute ? "true" : "false"),
            ("WaypointPlaceIds", string.Join(',', waypoints.Select(waypoint => GuidText(waypoint.PlaceId)))),
            ("WaypointRouteVertexIndices", string.Join(',', waypoints.Select(waypoint => waypoint.RouteVertexIndex?.ToString(Invariant) ?? "null"))));
        AddData(placemark, ("TripId", GuidText(tripId)));
        if (line is not null)
            placemark.Add(new XElement(Kml + "LineString", new XElement(Kml + "tessellate", "1"),
                new XElement(Kml + "coordinates", string.Join(' ', line.Coordinates.Select(CoordinateText)))));
        return placemark;
    }

    private static LineString? BuildFallback(Segment segment, IReadOnlyList<SegmentWaypoint> waypoints)
    {
        var places = new[] { segment.FromPlace }.Concat(waypoints.Select(waypoint => waypoint.Place)).Append(segment.ToPlace).ToArray();
        if (places.Any(place => place?.Location is null)) return null;
        return new LineString(places.Select(place => new Coordinate(place!.Location!.X, place.Location.Y)).ToArray()) { SRID = 4326 };
    }

    private static void ValidateAggregate(Trip trip)
    {
        var regionIds = new HashSet<Guid>();
        var placeIds = new HashSet<Guid>();
        foreach (var region in trip.Regions)
        {
            if (!regionIds.Add(region.Id) || region.TripId != Guid.Empty && region.TripId != trip.Id)
                throw new InvalidOperationException("The Trip contains malformed Region identity state.");
            foreach (var place in region.Places)
                if (!placeIds.Add(place.Id) || place.RegionId != Guid.Empty && place.RegionId != region.Id)
                    throw new InvalidOperationException("The Trip contains malformed Place identity state.");
            if (region.Areas.Any(area => area.Id == Guid.Empty || area.RegionId != Guid.Empty && area.RegionId != region.Id
                || area.Geometry is not { SRID: 4326, IsValid: true }))
                throw new InvalidOperationException("The Trip contains malformed Area state.");
        }
        var segmentIds = new HashSet<Guid>();
        foreach (var segment in trip.Segments)
        {
            if (!segmentIds.Add(segment.Id) || segment.TripId != trip.Id)
                throw new InvalidOperationException("The Trip contains malformed Segment identity state.");
            var errors = SegmentRouteReconciler.ValidateProjectedAggregate(segment);
            if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
            if (segment.EstimatedDuration is { Ticks: var ticks } && ticks % TimeSpan.TicksPerSecond != 0)
                throw new InvalidOperationException("Segment duration must use whole seconds.");
            if (segment.TransportProfileId.HasValue && segment.TransportProfile is null)
                throw new InvalidOperationException("The Segment transport profile was not loaded.");
            if (segment.TransportProfile is { } profile
                && (profile.Key != TransportProfile.NormalizeKey(profile.Key)
                    || TransportProfile.NormalizeKey(segment.Mode) != profile.Key))
                throw new InvalidOperationException("Segment mode and transport profile key do not match.");
            ValidateMeasurements(segment);
        }
    }

    private static void ValidateMeasurements(Segment segment)
    {
        if (segment.EstimatedDurationSource is not EstimatedDurationSource.Automatic
            and not EstimatedDurationSource.Manual)
            throw new InvalidOperationException("Segment duration provenance is invalid.");

        var waypoints = segment.Waypoints.OrderBy(waypoint => waypoint.Position).ToArray();
        var route = segment.RouteGeometry ?? BuildFallback(segment, waypoints);
        if (route is null)
        {
            if (segment.EstimatedDistanceKm.HasValue || segment.EstimatedDurationSource == EstimatedDurationSource.Automatic
                && segment.EstimatedDuration.HasValue)
                throw new InvalidOperationException("Unavailable route measurements must be empty.");
            return;
        }
        var measurement = SegmentMeasurementCalculator.CalculateDistance(route.Coordinates);
        if (segment.EstimatedDistanceKm != measurement.RoundedKilometres)
            throw new InvalidOperationException("Segment distance is not canonical.");
        if (segment.EstimatedDurationSource == EstimatedDurationSource.Manual)
        {
            if (!segment.EstimatedDuration.HasValue) throw new InvalidOperationException("Manual duration is required.");
            return;
        }
        var speed = segment.TransportProfile?.PlanningSpeedKmh;
        var expected = speed is > 0 ? SegmentMeasurementCalculator.CalculateAutomaticDuration(measurement.UnroundedMetres, speed.Value) : (TimeSpan?)null;
        if (segment.EstimatedDuration != expected)
            throw new InvalidOperationException("Automatic duration is not canonical.");
    }

    private static void AddData(XElement owner, params (string Name, string Value)[] values) =>
        owner.Add(new XElement(Kml + "ExtendedData", values.Select(value =>
            new XElement(Kml + "Data", new XAttribute("name", value.Name), new XElement(Kml + "value", value.Value)))));

    private static string GuidText(Guid value) => value.ToString("D", Invariant).ToLowerInvariant();
    private static string GuidText(Guid? value) => value.HasValue ? GuidText(value.Value) : "";
    private static string Number(IFormattable? value) => value?.ToString(null, Invariant) ?? "";
    private static string CoordinateText(Coordinate coordinate) =>
        $"{coordinate.X.ToString("R", Invariant)},{coordinate.Y.ToString("R", Invariant)},0";
}
