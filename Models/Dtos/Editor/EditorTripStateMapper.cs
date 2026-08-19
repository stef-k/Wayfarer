using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Wayfarer.Models;

namespace Wayfarer.Models.Dtos.Editor;

/// <summary>
/// Maps trip domain entities into the dedicated normalized Trip Editor read contract.
/// </summary>
public static class EditorTripStateMapper
{
    private static readonly GeoJsonWriter GeoJsonWriter = new();

    /// <summary>
    /// Builds the normalized editor state for an already ownership-filtered trip.
    /// </summary>
    public static EditorTripStateDto ToEditorState(
        Trip trip,
        IReadOnlyDictionary<Guid, IReadOnlyList<PlaceVisitEvent>> visitsByPlaceId,
        EditorOptionsDto options,
        string? publicUrl,
        string? progressPublicUrl,
        Func<Segment, string>? aggregateTokenFactory = null,
        Func<Segment, EditorExternalRoutingCapabilityDto?>? externalRoutingFactory = null)
    {
        ArgumentNullException.ThrowIfNull(trip);

        var regions = trip.Regions.OrderBy(r => r.DisplayOrder).ThenBy(r => r.Id).ToList();
        var places = regions.SelectMany(r => r.Places.Select(p => (Region: r, Place: p))).ToList();
        var areas = regions.SelectMany(r => r.Areas.Select(a => (Region: r, Area: a))).ToList();
        var segments = trip.Segments.OrderBy(s => s.DisplayOrder).ThenBy(s => s.Id).ToList();
        var visitSummaries = BuildVisitSummaries(places.Select(p => p.Place), visitsByPlaceId);

        return new EditorTripStateDto
        {
            TripId = trip.Id,
            Metadata = ToMetadata(trip, publicUrl, progressPublicUrl),
            RegionsById = regions.ToDictionary(r => r.Id, ToRegion),
            RegionOrder = regions.Select(r => r.Id).ToList(),
            PlacesById = places.ToDictionary(p => p.Place.Id, p => ToPlace(trip.Id, p.Region.Id, p.Place, visitSummaries[p.Place.Id])),
            PlaceOrderByRegionId = regions.ToDictionary(r => r.Id, r => (IReadOnlyList<Guid>)r.Places
                .OrderBy(p => p.DisplayOrder.HasValue ? 0 : 1)
                .ThenBy(p => p.DisplayOrder)
                .ThenBy(p => p.Id)
                .Select(p => p.Id)
                .ToList()),
            AreasById = areas.ToDictionary(a => a.Area.Id, a => ToArea(trip.Id, a.Region.Id, a.Area)),
            AreaOrderByRegionId = regions.ToDictionary(r => r.Id, r => (IReadOnlyList<Guid>)r.Areas.OrderBy(a => a.DisplayOrder).ThenBy(a => a.Name).Select(a => a.Id).ToList()),
            SegmentsById = segments.ToDictionary(s => s.Id, s => ToSegment(trip.Id, s,
                aggregateTokenFactory?.Invoke(s) ?? string.Empty, true, externalRoutingFactory?.Invoke(s))),
            SegmentOrder = segments.Select(s => s.Id).ToList(),
            TagsBySlug = trip.Tags.OrderBy(t => t.Name).ToDictionary(t => t.Slug, t => new EditorTagDto(t.Id, t.Name, t.Slug)),
            TagOrder = trip.Tags.OrderBy(t => t.Name).Select(t => t.Slug).ToList(),
            VisitProgress = ToVisitProgress(places, visitSummaries, visitsByPlaceId),
            Options = options,
            Permissions = new EditorPermissionsDto(true, true, true, true, true, true, true, true, true)
        };
    }

    /// <summary>
    /// Maps only the top-level trip metadata slice used by read and mutation responses.
    /// </summary>
    public static EditorTripMetadataDto ToMetadata(Trip trip, string? publicUrl, string? progressPublicUrl) =>
        new(
            trip.Id,
            trip.Name,
            trip.Notes ?? string.Empty,
            trip.IsPublic,
            trip.ShareProgressEnabled,
            trip.CenterLat.HasValue && trip.CenterLon.HasValue
                ? new EditorCoordinateDto(trip.CenterLat.Value, trip.CenterLon.Value)
                : null,
            trip.Zoom,
            ToImageReference(trip.CoverImageUrl),
            trip.UpdatedAt,
            publicUrl,
            progressPublicUrl);

    /// <summary>
    /// Maps a region into the editor region contract.
    /// </summary>
    public static EditorRegionDto ToRegion(Region region)
    {
        var isShadow = IsShadowRegion(region);
        return new EditorRegionDto(
            region.Id,
            region.TripId,
            region.Name,
            region.Notes ?? string.Empty,
            ToImageReference(region.CoverImageUrl),
            ToCoordinate(region.Center),
            region.DisplayOrder,
            isShadow,
            isShadow ? ShadowCapabilities() : EditableRegionCapabilities());
    }

    /// <summary>
    /// Maps remaining places and visits into the editor visit progress contract.
    /// </summary>
    public static EditorVisitProgressDto ToVisitProgress(
        IReadOnlyList<(Region Region, Place Place)> places,
        IReadOnlyDictionary<Guid, EditorPlaceVisitSummaryDto> summaries,
        IReadOnlyDictionary<Guid, IReadOnlyList<PlaceVisitEvent>> visitsByPlaceId) =>
        BuildVisitProgress(places, summaries, visitsByPlaceId);

    /// <summary>
    /// Builds editor visit summaries for the supplied places.
    /// </summary>
    public static IReadOnlyDictionary<Guid, EditorPlaceVisitSummaryDto> ToVisitSummaries(
        IEnumerable<Place> places,
        IReadOnlyDictionary<Guid, IReadOnlyList<PlaceVisitEvent>> visitsByPlaceId) =>
        BuildVisitSummaries(places, visitsByPlaceId);

    /// <summary>
    /// Maps a segment into the editor segment contract.
    /// </summary>
    public static EditorSegmentDto ToSegment(
        Guid tripId, Segment segment, string aggregateConcurrencyToken = "", bool waypointsAuthoritative = false,
        EditorExternalRoutingCapabilityDto? externalRouting = null)
    {
        if (!waypointsAuthoritative)
            throw new InvalidOperationException("Authoritative Segment mapping requires explicitly loaded waypoint children.");
        return new(
            segment.Id,
            tripId,
            segment.FromPlaceId,
            segment.ToPlaceId,
            segment.Waypoints.OrderBy(item => item.Position).Select(item => item.PlaceId).ToArray(),
            segment.Waypoints.OrderBy(item => item.Position).Select(item => item.RouteVertexIndex).ToArray(),
            segment.Mode ?? string.Empty,
            segment.TransportProfileId,
            segment.RouteGeometry != null,
            segment.EstimatedDistanceKm,
            segment.EstimatedDuration?.TotalMinutes,
            segment.EstimatedDurationSource.ToString(),
            segment.Notes ?? string.Empty,
            ToGeoJson(segment.RouteGeometry),
            ToGeoJson(segment.RouteGeometry ?? BuildEffectiveRoute(segment)),
            aggregateConcurrencyToken,
            segment.DisplayOrder,
            EditableLeafCapabilities(),
            externalRouting);
    }

    private static LineString? BuildEffectiveRoute(Segment segment)
    {
        var coordinates = new List<Coordinate>();
        if (segment.FromPlace?.Location is Point from) coordinates.Add(from.Coordinate);
        coordinates.AddRange(segment.Waypoints.OrderBy(item => item.Position)
            .Where(item => item.Place.Location != null).Select(item => item.Place.Location!.Coordinate));
        if (segment.ToPlace?.Location is Point to) coordinates.Add(to.Coordinate);
        return coordinates.Count >= 2 ? new LineString(coordinates.ToArray()) { SRID = 4326 } : null;
    }

    /// <summary>
    /// Maps a place into the editor place contract.
    /// </summary>
    public static EditorPlaceDto ToPlace(
        Guid tripId,
        Guid regionId,
        Place place,
        EditorPlaceVisitSummaryDto visitSummary) =>
        new(
            place.Id,
            tripId,
            regionId,
            place.Name,
            place.Notes ?? string.Empty,
            place.Address ?? string.Empty,
            ToCoordinate(place.Location),
            string.IsNullOrWhiteSpace(place.IconName) ? "marker" : place.IconName,
            string.IsNullOrWhiteSpace(place.MarkerColor) ? "bg-blue" : place.MarkerColor,
            place.DisplayOrder ?? 0,
            visitSummary,
            EditableLeafCapabilities());

    /// <summary>
    /// Maps an area into the editor area contract.
    /// </summary>
    public static EditorAreaDto ToArea(Guid tripId, Guid regionId, Area area) =>
        new(
            area.Id,
            tripId,
            regionId,
            area.Name,
            area.Notes ?? string.Empty,
            string.IsNullOrWhiteSpace(area.FillHex) ? "#ff6600" : area.FillHex,
            ToAreaPolygonGeoJson(tripId, area),
            area.DisplayOrder ?? 0,
            EditableLeafCapabilities());

    private static EditorVisitProgressDto BuildVisitProgress(
        IReadOnlyList<(Region Region, Place Place)> places,
        IReadOnlyDictionary<Guid, EditorPlaceVisitSummaryDto> summaries,
        IReadOnlyDictionary<Guid, IReadOnlyList<PlaceVisitEvent>> visitsByPlaceId)
    {
        var totalPlaces = summaries.Count;
        var visitedPlaces = summaries.Values.Count(s => s.IsVisited);
        var percentVisited = totalPlaces > 0 ? Math.Round((double)visitedPlaces / totalPlaces * 100, 1) : 0;
        var regionByPlaceId = places.ToDictionary(p => p.Place.Id, p => p.Region.Id);
        var historyRows = visitsByPlaceId
            .SelectMany(pair => pair.Value.Select(visit => MapHistoryRow(pair.Key, regionByPlaceId[pair.Key], visit)))
            .OrderByDescending(row => row.StartedAt)
            .ThenBy(row => row.VisitId)
            .ToList();

        return new EditorVisitProgressDto(totalPlaces, visitedPlaces, percentVisited, summaries, historyRows);
    }

    private static IReadOnlyDictionary<Guid, EditorPlaceVisitSummaryDto> BuildVisitSummaries(
        IEnumerable<Place> places,
        IReadOnlyDictionary<Guid, IReadOnlyList<PlaceVisitEvent>> visitsByPlaceId) =>
        places.ToDictionary(
            p => p.Id,
            p =>
            {
                var visits = visitsByPlaceId.TryGetValue(p.Id, out var rows) ? rows : Array.Empty<PlaceVisitEvent>();
                var ordered = visits.OrderBy(v => v.ArrivedAtUtc).ToList();
                return new EditorPlaceVisitSummaryDto(
                    p.Id,
                    ordered.Count,
                    ordered.Count > 0,
                    ordered.FirstOrDefault()?.ArrivedAtUtc,
                    ordered.LastOrDefault()?.ArrivedAtUtc);
            });

    private static EditorCoordinateDto? ToCoordinate(Point? point) =>
        point == null ? null : new EditorCoordinateDto(point.Y, point.X);

    private static JsonElement? ToGeoJson(Geometry? geometry)
    {
        if (geometry == null)
        {
            return null;
        }

        if (geometry.SRID != 4326)
        {
            geometry.SRID = 4326;
        }

        using var document = JsonDocument.Parse(GeoJsonWriter.Write(geometry));
        return document.RootElement.Clone();
    }

    private static JsonElement ToAreaPolygonGeoJson(Guid tripId, Area area)
    {
        if (area.Geometry == null || area.Geometry.IsEmpty || area.Geometry.GeometryType != "Polygon")
        {
            throw new EditorInvalidAreaGeometryException(tripId, area.Id);
        }

        return ToGeoJson(area.Geometry)!.Value;
    }

    private static EditorVisitHistoryRowDto MapHistoryRow(Guid placeId, Guid regionId, PlaceVisitEvent visit)
    {
        var durationMinutes = visit.EndedAtUtc.HasValue
            ? (int)Math.Floor((visit.EndedAtUtc.Value - visit.ArrivedAtUtc).TotalMinutes)
            : (int?)null;

        return new EditorVisitHistoryRowDto(
            visit.Id,
            placeId,
            regionId,
            visit.ArrivedAtUtc,
            visit.EndedAtUtc,
            durationMinutes);
    }

    private static EditorImageReferenceDto? ToImageReference(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return null;
        }

        return new EditorImageReferenceDto(rawUrl, $"/Public/ProxyImage?url={Uri.EscapeDataString(rawUrl)}");
    }

    private static bool IsShadowRegion(Region region) =>
        region.DisplayOrder == 0 && string.Equals(region.Name, EditorRegionRequestParser.ShadowRegionName, StringComparison.Ordinal);

    private static EditorEntityCapabilitiesDto ShadowCapabilities() =>
        new(false, false, false, false, false, false, true);

    private static EditorEntityCapabilitiesDto EditableRegionCapabilities() =>
        new(true, true, true, true, false, true, true);

    private static EditorEntityCapabilitiesDto EditableLeafCapabilities() =>
        new(true, true, true, true, true, false, false);
}

/// <summary>Exception raised when persisted area geometry cannot satisfy the editor read contract.</summary>
public sealed class EditorInvalidAreaGeometryException : Exception
{
    /// <summary>Initializes a new invalid area geometry exception.</summary>
    public EditorInvalidAreaGeometryException(Guid tripId, Guid areaId)
        : base($"Area {areaId} on trip {tripId} has invalid editor geometry.")
    {
        TripId = tripId;
        AreaId = areaId;
    }

    /// <summary>The trip containing the invalid area.</summary>
    public Guid TripId { get; }

    /// <summary>The invalid area identifier.</summary>
    public Guid AreaId { get; }
}
