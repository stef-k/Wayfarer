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
    private const string ShadowRegionName = "Unassigned Places";
    private static readonly GeoJsonWriter GeoJsonWriter = new();

    /// <summary>
    /// Builds the normalized editor state for an already ownership-filtered trip.
    /// </summary>
    public static EditorTripStateDto ToEditorState(
        Trip trip,
        IReadOnlyDictionary<Guid, IReadOnlyList<PlaceVisitEvent>> visitsByPlaceId,
        EditorOptionsDto options)
    {
        ArgumentNullException.ThrowIfNull(trip);

        var regions = trip.Regions.OrderBy(r => r.DisplayOrder).ThenBy(r => r.Name).ToList();
        var places = regions.SelectMany(r => r.Places.Select(p => (Region: r, Place: p))).ToList();
        var areas = regions.SelectMany(r => r.Areas.Select(a => (Region: r, Area: a))).ToList();
        var segments = trip.Segments.OrderBy(s => s.DisplayOrder).ThenBy(s => s.Id).ToList();
        var visitSummaries = BuildVisitSummaries(places.Select(p => p.Place), visitsByPlaceId);

        return new EditorTripStateDto
        {
            TripId = trip.Id,
            Metadata = MapMetadata(trip),
            RegionsById = regions.ToDictionary(r => r.Id, MapRegion),
            RegionOrder = regions.Select(r => r.Id).ToList(),
            PlacesById = places.ToDictionary(p => p.Place.Id, p => MapPlace(trip.Id, p.Region.Id, p.Place, visitSummaries[p.Place.Id])),
            PlaceOrderByRegionId = regions.ToDictionary(r => r.Id, r => (IReadOnlyList<Guid>)r.Places.OrderBy(p => p.DisplayOrder).ThenBy(p => p.Name).Select(p => p.Id).ToList()),
            AreasById = areas.ToDictionary(a => a.Area.Id, a => MapArea(trip.Id, a.Region.Id, a.Area)),
            AreaOrderByRegionId = regions.ToDictionary(r => r.Id, r => (IReadOnlyList<Guid>)r.Areas.OrderBy(a => a.DisplayOrder).ThenBy(a => a.Name).Select(a => a.Id).ToList()),
            SegmentsById = segments.ToDictionary(s => s.Id, s => MapSegment(trip.Id, s)),
            SegmentOrder = segments.Select(s => s.Id).ToList(),
            TagsBySlug = trip.Tags.OrderBy(t => t.Name).ToDictionary(t => t.Slug, t => new EditorTagDto(t.Id, t.Name, t.Slug)),
            TagOrder = trip.Tags.OrderBy(t => t.Name).Select(t => t.Slug).ToList(),
            VisitProgress = BuildVisitProgress(visitSummaries),
            Options = options,
            Permissions = new EditorPermissionsDto(true, true, true, true, true, true, true, true, true)
        };

        EditorRegionDto MapRegion(Region region)
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
    }

    private static EditorTripMetadataDto MapMetadata(Trip trip) =>
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
            trip.UpdatedAt);

    private static EditorPlaceDto MapPlace(
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

    private static EditorAreaDto MapArea(Guid tripId, Guid regionId, Area area) =>
        new(
            area.Id,
            tripId,
            regionId,
            area.Name,
            area.Notes ?? string.Empty,
            string.IsNullOrWhiteSpace(area.FillHex) ? "#ff6600" : area.FillHex,
            ToGeoJson(area.Geometry),
            area.DisplayOrder ?? 0,
            EditableLeafCapabilities());

    private static EditorSegmentDto MapSegment(Guid tripId, Segment segment) =>
        new(
            segment.Id,
            tripId,
            segment.FromPlaceId,
            segment.ToPlaceId,
            segment.Mode ?? string.Empty,
            segment.EstimatedDistanceKm,
            segment.EstimatedDuration?.TotalMinutes,
            segment.Notes ?? string.Empty,
            ToGeoJson(segment.RouteGeometry),
            segment.DisplayOrder,
            EditableLeafCapabilities());

    private static EditorVisitProgressDto BuildVisitProgress(IReadOnlyDictionary<Guid, EditorPlaceVisitSummaryDto> summaries)
    {
        var totalPlaces = summaries.Count;
        var visitedPlaces = summaries.Values.Count(s => s.IsVisited);
        var percentVisited = totalPlaces > 0 ? (int)Math.Round((double)visitedPlaces / totalPlaces * 100) : 0;
        return new EditorVisitProgressDto(totalPlaces, visitedPlaces, percentVisited, summaries, Array.Empty<object>());
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

    private static EditorImageReferenceDto? ToImageReference(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return null;
        }

        return new EditorImageReferenceDto(rawUrl, $"/Public/ProxyImage?url={Uri.EscapeDataString(rawUrl)}");
    }

    private static bool IsShadowRegion(Region region) =>
        region.DisplayOrder == 0 && string.Equals(region.Name, ShadowRegionName, StringComparison.Ordinal);

    private static EditorEntityCapabilitiesDto ShadowCapabilities() =>
        new(false, false, false, false, false, false, true);

    private static EditorEntityCapabilitiesDto EditableRegionCapabilities() =>
        new(true, true, true, true, false, true, true);

    private static EditorEntityCapabilitiesDto EditableLeafCapabilities() =>
        new(true, true, true, true, true, false, false);
}
