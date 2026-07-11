using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Wayfarer.Models;

namespace Wayfarer.Models.Dtos.TripViewer;

/// <summary>
/// Maps loaded trip entities into the read-only Trip Viewer state contract.
/// </summary>
public static class TripViewerStateMapper
{
    private const string PrivateMode = "private";
    private const string PublicMode = "public";
    private const string EmbedMode = "embed";
    private static readonly GeoJsonWriter GeoJsonWriter = new();
    private static readonly Regex FillHexRegex = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);
    private static readonly HashSet<string> ViewerIconNames = new(StringComparer.Ordinal)
    {
        "anchor", "atm", "barbecue", "beach", "bike", "boat", "camera", "camping", "car", "charging-point",
        "checkmark", "clouds", "construction", "danger", "drink", "eat", "ev-station", "fitness", "flag",
        "flight", "gas", "help", "hike", "hospital", "hotel", "info", "kayak", "latest", "luggage", "map",
        "marker", "museum", "no-wheelchair", "no-wifi", "park", "parking", "pet", "pharmacy", "phishing",
        "police", "run", "sail", "scuba-dive", "sea", "shopping", "ski", "smoke", "smoke-free", "sos",
        "star", "subway", "surf", "swim", "taxi", "telephone", "thunderstorm", "tool", "train", "walk",
        "water", "wc", "wheelchair", "wifi"
    };
    private static readonly HashSet<string> ViewerMarkerColors = new(StringComparer.Ordinal)
    {
        "bg-blue", "bg-purple", "bg-black", "bg-green", "bg-red"
    };

    /// <summary>Builds private owner viewer state for an already ownership-filtered trip.</summary>
    public static TripViewerStateDto ToPrivateState(
        Trip trip,
        IReadOnlyList<PlaceVisitEvent> visitEvents,
        IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(trip);
        var context = new ViewerContext(PrivateMode, IsOwner: true, IsAuthenticated: true, CanReadCounts: true, CanReadHistory: true);
        return BuildState(trip, visitEvents, context, query);
    }

    /// <summary>Builds public or embed viewer state for an already public-filtered trip.</summary>
    public static TripViewerStateDto ToPublicState(
        Trip trip,
        IReadOnlyList<PlaceVisitEvent> visitEvents,
        bool isOwner,
        bool isAuthenticated,
        bool embed,
        IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(trip);
        var canReadCounts = trip.ShareProgressEnabled || (!embed && isOwner);
        var canReadHistory = !embed && isOwner;
        var context = new ViewerContext(embed ? EmbedMode : PublicMode, isOwner, isAuthenticated, canReadCounts, canReadHistory);
        return BuildState(trip, visitEvents, context, query);
    }

    private static TripViewerStateDto BuildState(
        Trip trip,
        IReadOnlyList<PlaceVisitEvent> visitEvents,
        ViewerContext context,
        IQueryCollection query)
    {
        var regions = trip.Regions.OrderBy(r => r.DisplayOrder).ThenBy(r => r.Name).ThenBy(r => r.Id).ToList();
        var places = regions.SelectMany(r => r.Places.OrderBy(p => p.DisplayOrder ?? 0).ThenBy(p => p.Name).ThenBy(p => p.Id)
            .Select(p => (Region: r, Place: p))).ToList();
        var areas = regions.SelectMany(r => r.Areas.OrderBy(a => a.DisplayOrder ?? 0).ThenBy(a => a.Name).ThenBy(a => a.Id)
            .Select(a => (Region: r, Area: a))).ToList();
        var segments = trip.Segments.OrderBy(s => s.DisplayOrder).ThenBy(s => s.Id).ToList();
        var visitProgress = BuildVisitProgress(places, visitEvents, context);
        var placesById = places.ToDictionary(p => p.Place.Id, p => p.Place);

        return new TripViewerStateDto
        {
            ViewerMode = context.ViewerMode,
            Trip = ToTrip(trip, context),
            RegionsById = regions.ToDictionary(r => r.Id, r => ToRegion(r, context)),
            RegionOrder = regions.Select(r => r.Id).ToList(),
            PlacesById = places.ToDictionary(p => p.Place.Id, p => ToPlace(trip.Id, p.Region.Id, p.Place, visitProgress.PlaceSummariesByPlaceId[p.Place.Id])),
            PlaceOrderByRegionId = regions.ToDictionary(r => r.Id, r => (IReadOnlyList<Guid>)r.Places.OrderBy(p => p.DisplayOrder ?? 0).ThenBy(p => p.Name).ThenBy(p => p.Id).Select(p => p.Id).ToList()),
            AreasById = areas.ToDictionary(a => a.Area.Id, a => ToArea(trip.Id, a.Region.Id, a.Area)),
            AreaOrderByRegionId = regions.ToDictionary(r => r.Id, r => (IReadOnlyList<Guid>)r.Areas.OrderBy(a => a.DisplayOrder ?? 0).ThenBy(a => a.Name).ThenBy(a => a.Id).Select(a => a.Id).ToList()),
            SegmentsById = segments.ToDictionary(s => s.Id, s => ToSegment(trip.Id, s, placesById)),
            SegmentOrder = segments.Select(s => s.Id).ToList(),
            TagsBySlug = trip.Tags.OrderBy(t => t.Name).ThenBy(t => t.Slug).ToDictionary(t => t.Slug, t => new TripViewerTagDto(t.Id, t.Name, t.Slug)),
            TagOrder = trip.Tags.OrderBy(t => t.Name).ThenBy(t => t.Slug).Select(t => t.Slug).ToList(),
            VisitProgress = visitProgress,
            Permissions = BuildPermissions(context),
            Actions = BuildActions(trip, context),
            Map = BuildMap(trip, regions, query)
        };
    }

    private static TripViewerTripDto ToTrip(Trip trip, ViewerContext context) =>
        new(
            trip.Id,
            trip.Name,
            TripViewerNotesFormatter.Format(trip.Notes),
            trip.IsPublic,
            trip.ShareProgressEnabled,
            context.ViewerMode == PrivateMode ? null : trip.User?.DisplayName,
            ToTripCoverImage(trip, context),
            trip.CenterLat.HasValue && trip.CenterLon.HasValue ? new TripViewerCoordinateDto(trip.CenterLat.Value, trip.CenterLon.Value) : null,
            trip.Zoom,
            trip.UpdatedAt,
            context.ViewerMode == PrivateMode ? $"/User/Trip/ViewNext/{trip.Id}" : null,
            $"/Public/TripsNext/{trip.Id}",
            $"/Public/TripsNext/{trip.Id}?embed=true");

    private static TripViewerRegionDto ToRegion(Region region, ViewerContext context)
    {
        var placeIds = region.Places.OrderBy(p => p.DisplayOrder ?? 0).ThenBy(p => p.Name).ThenBy(p => p.Id).Select(p => p.Id).ToList();
        var areaIds = region.Areas.OrderBy(a => a.DisplayOrder ?? 0).ThenBy(a => a.Name).ThenBy(a => a.Id).Select(a => a.Id).ToList();

        return new TripViewerRegionDto(
            region.Id,
            region.TripId,
            region.Name,
            TripViewerNotesFormatter.Format(region.Notes),
            ToRegionCoverImage(region, context),
            ToCoordinate(region.Center),
            region.DisplayOrder,
            placeIds,
            areaIds);
    }

    private static TripViewerPlaceDto ToPlace(
        Guid tripId,
        Guid regionId,
        Place place,
        TripViewerPlaceVisitSummaryDto visitSummary) =>
        new(
            place.Id,
            tripId,
            regionId,
            place.Name,
            TripViewerNotesFormatter.Format(place.Notes),
            place.Address ?? string.Empty,
            ToCoordinate(place.Location),
            SafeIconName(place.IconName),
            SafeMarkerColor(place.MarkerColor),
            place.DisplayOrder ?? 0,
            visitSummary);

    private static TripViewerAreaDto ToArea(Guid tripId, Guid regionId, Area area) =>
        new(
            area.Id,
            tripId,
            regionId,
            area.Name,
            TripViewerNotesFormatter.Format(area.Notes),
            NormalizeFillHex(area.FillHex),
            ToGeoJson(area.Geometry),
            area.DisplayOrder ?? 0);

    /// <summary>Returns only persisted six-digit colors that are safe decorative viewer facts.</summary>
    private static string? NormalizeFillHex(string? fillHex) =>
        !string.IsNullOrWhiteSpace(fillHex) && FillHexRegex.IsMatch(fillHex.Trim())
            ? fillHex.Trim()
            : null;

    private static TripViewerSegmentDto ToSegment(Guid tripId, Segment segment, IReadOnlyDictionary<Guid, Place> placesById)
    {
        var route = ToGeoJson(segment.RouteGeometry);
        var fallbackStart = route == null ? LinkedPlaceCoordinate(segment.FromPlaceId, placesById) : RouteEndpoint(segment.RouteGeometry, first: true);
        var fallbackEnd = route == null ? LinkedPlaceCoordinate(segment.ToPlaceId, placesById) : RouteEndpoint(segment.RouteGeometry, first: false);

        return new TripViewerSegmentDto(
            segment.Id,
            tripId,
            segment.FromPlaceId,
            segment.ToPlaceId,
            segment.Mode ?? string.Empty,
            segment.EstimatedDistanceKm,
            segment.EstimatedDuration?.TotalMinutes,
            TripViewerNotesFormatter.Format(segment.Notes),
            route,
            fallbackStart,
            fallbackEnd,
            segment.DisplayOrder);
    }

    private static TripViewerVisitProgressDto BuildVisitProgress(
        IReadOnlyList<(Region Region, Place Place)> places,
        IReadOnlyList<PlaceVisitEvent> visitEvents,
        ViewerContext context)
    {
        var visitsByPlaceId = visitEvents
            .Where(v => v.PlaceId.HasValue)
            .GroupBy(v => v.PlaceId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(v => v.ArrivedAtUtc).ToList());

        var summaries = places.ToDictionary(
            p => p.Place.Id,
            p =>
            {
                var visits = context.CanReadCounts && visitsByPlaceId.TryGetValue(p.Place.Id, out var rows)
                    ? rows
                    : new List<PlaceVisitEvent>();
                return new TripViewerPlaceVisitSummaryDto(
                    p.Place.Id,
                    context.CanReadCounts ? visits.Count : 0,
                    context.CanReadCounts && visits.Count > 0,
                    context.CanReadHistory ? visits.FirstOrDefault()?.ArrivedAtUtc : null,
                    context.CanReadHistory ? visits.LastOrDefault()?.ArrivedAtUtc : null);
            });

        var totalPlaces = summaries.Count;
        var visitedPlaces = context.CanReadCounts ? summaries.Values.Count(s => s.IsVisited) : 0;
        var percentVisited = context.CanReadCounts && totalPlaces > 0 ? Math.Round((double)visitedPlaces / totalPlaces * 100, 1) : 0;
        var regionByPlaceId = places.ToDictionary(p => p.Place.Id, p => p.Region.Id);
        var historyRows = context.CanReadHistory
            ? visitsByPlaceId
                .SelectMany(pair => pair.Value.Select(visit => MapHistoryRow(pair.Key, regionByPlaceId[pair.Key], visit)))
                .OrderByDescending(row => row.StartedAt)
                .ThenBy(row => row.VisitId)
                .ToList()
            : new List<TripViewerVisitHistoryRowDto>();

        return new TripViewerVisitProgressDto(
            context.CanReadCounts,
            context.CanReadCounts,
            context.CanReadHistory,
            totalPlaces,
            visitedPlaces,
            percentVisited,
            summaries,
            historyRows);
    }

    private static TripViewerPermissionsDto BuildPermissions(ViewerContext context) =>
        new(
            context.ViewerMode == PrivateMode,
            context.ViewerMode == PublicMode,
            context.ViewerMode == EmbedMode,
            context.IsOwner && context.ViewerMode != EmbedMode,
            true,
            context.CanReadCounts,
            context.CanReadHistory,
            context.ViewerMode != EmbedMode && context.IsOwner,
            context.ViewerMode != EmbedMode,
            context.ViewerMode != EmbedMode);

    private static TripViewerActionsDto BuildActions(Trip trip, ViewerContext context)
    {
        var isEmbed = context.ViewerMode == EmbedMode;
        var isPrivate = context.ViewerMode == PrivateMode;
        var isPublic = context.ViewerMode == PublicMode;
        var canExport = !isEmbed && (isPrivate || trip.IsPublic);
        var canOwnerPublicActions = isPublic && context.IsOwner;
        var publicUrl = $"/Public/TripsNext/{trip.Id}";
        var loginUrl = $"/Identity/Account/Login?ReturnUrl={Uri.EscapeDataString(publicUrl)}";

        return new TripViewerActionsDto
        {
            Edit = new TripViewerActionDto(!isEmbed && context.IsOwner, $"/User/Trip/Edit/{trip.Id}"),
            Clone = BuildCloneAction(trip, context, loginUrl),
            ExportWayfarerKml = new TripViewerActionDto(canExport, canExport ? $"/Trip/ExportWayfarerKml/{trip.Id}" : null),
            ExportGoogleMyMapsKml = new TripViewerActionDto(canExport, canExport ? $"/Trip/ExportGoogleMyMapsKml/{trip.Id}" : null),
            ExportPdf = new TripViewerActionDto(canExport, canExport ? $"/Trip/ExportPdf/{trip.Id}" : null),
            Share = new TripViewerActionDto(!isEmbed && trip.IsPublic, trip.IsPublic ? publicUrl : null),
            CopyPublicUrl = new TripViewerActionDto(!isEmbed && trip.IsPublic, trip.IsPublic ? publicUrl : null),
            CopyCoverUrl = new TripViewerActionDto(canOwnerPublicActions && !string.IsNullOrWhiteSpace(trip.CoverImageUrl), canOwnerPublicActions ? $"/Public/Trips/{trip.Id}/CoverImage?v={trip.UpdatedAt.Ticks}" : null),
            CopyMapSnapshotUrl = new TripViewerActionDto(canOwnerPublicActions && trip.CenterLat.HasValue && trip.CenterLon.HasValue && trip.Zoom.HasValue, canOwnerPublicActions ? $"/Public/Trips/{trip.Id}/MapSnapshot" : null),
            Fullscreen = new TripViewerActionDto(isEmbed, isEmbed ? publicUrl : null),
            OpenCanonical = new TripViewerActionDto(isEmbed, isEmbed ? publicUrl : null),
            Readable = new TripViewerActionDto(!isEmbed),
            Print = new TripViewerActionDto(!isEmbed)
        };
    }

    private static TripViewerActionDto BuildCloneAction(Trip trip, ViewerContext context, string loginUrl)
    {
        if (context.ViewerMode == EmbedMode || !trip.IsPublic || context.IsOwner)
        {
            return new TripViewerActionDto(false);
        }

        return context.IsAuthenticated
            ? new TripViewerActionDto(true, $"/User/Trip/Clone/{trip.Id}", "POST")
            : new TripViewerActionDto(false, loginUrl, "GET", RequiresAuthentication: true);
    }

    private static TripViewerMapDto BuildMap(Trip trip, IReadOnlyList<Region> regions, IQueryCollection query)
    {
        var initialView = ResolveInitialView(trip, regions, query);
        return new TripViewerMapDto(
            initialView,
            new[] { "lat", "lon", "lng", "zoom" },
            new[] { "lat", "lon", "zoom" },
            "/Public/tiles/{z}/{x}/{y}.png",
            "(c) OpenStreetMap contributors");
    }

    private static TripViewerMapInitialViewDto ResolveInitialView(Trip trip, IReadOnlyList<Region> regions, IQueryCollection query)
    {
        if (TryQueryView(query, out var queryLat, out var queryLon, out var queryZoom))
        {
            return NewInitialView(queryLat, queryLon, queryZoom, "query");
        }

        if (trip.CenterLat.HasValue && trip.CenterLon.HasValue && trip.Zoom.HasValue)
        {
            return NewInitialView(trip.CenterLat.Value, trip.CenterLon.Value, trip.Zoom.Value, "trip");
        }

        var firstRegionCenter = regions.Select(r => r.Center).FirstOrDefault(p => p != null);
        if (firstRegionCenter != null)
        {
            return NewInitialView(firstRegionCenter.Y, firstRegionCenter.X, 8, "region");
        }

        return NewInitialView(20, 0, 2, "world");
    }

    private static bool TryQueryView(IQueryCollection query, out double lat, out double lon, out int zoom)
    {
        lat = 0;
        lon = 0;
        zoom = 0;

        var longitudeValue = query.TryGetValue("lon", out var lonValues) && !string.IsNullOrWhiteSpace(lonValues.FirstOrDefault())
            ? lonValues.FirstOrDefault()
            : query.TryGetValue("lng", out var lngValues)
                ? lngValues.FirstOrDefault()
                : null;

        return double.TryParse(query["lat"].FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out lat)
            && double.TryParse(longitudeValue, NumberStyles.Float, CultureInfo.InvariantCulture, out lon)
            && int.TryParse(query["zoom"].FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out zoom)
            && double.IsFinite(lat)
            && double.IsFinite(lon);
    }

    private static TripViewerMapInitialViewDto NewInitialView(double latitude, double longitude, int zoom, string source) =>
        new(latitude, longitude, zoom, source, FormattableString.Invariant($"lat={latitude}&lon={longitude}&zoom={zoom}"));

    private static TripViewerCoverImageDto? ToTripCoverImage(Trip trip, ViewerContext context)
    {
        if (string.IsNullOrWhiteSpace(trip.CoverImageUrl))
        {
            return null;
        }

        if (context.ViewerMode == PrivateMode)
        {
            return new TripViewerCoverImageDto(ToProxyUrl(trip.CoverImageUrl), trip.IsPublic ? $"/Public/Trips/{trip.Id}/CoverImage?v={trip.UpdatedAt.Ticks}" : null, trip.CoverImageUrl);
        }

        return new TripViewerCoverImageDto($"/Public/Trips/{trip.Id}/CoverImage?v={trip.UpdatedAt.Ticks}", context.IsOwner && context.ViewerMode == PublicMode ? $"/Public/Trips/{trip.Id}/CoverImage?v={trip.UpdatedAt.Ticks}" : null, null);
    }

    private static TripViewerCoverImageDto? ToRegionCoverImage(Region region, ViewerContext context)
    {
        if (string.IsNullOrWhiteSpace(region.CoverImageUrl))
        {
            return null;
        }

        return new TripViewerCoverImageDto(ToProxyUrl(region.CoverImageUrl), null, context.ViewerMode == PrivateMode ? region.CoverImageUrl : null);
    }

    private static TripViewerCoordinateDto? ToCoordinate(Point? point) =>
        point == null ? null : new TripViewerCoordinateDto(point.Y, point.X);

    private static TripViewerCoordinateDto? LinkedPlaceCoordinate(Guid? placeId, IReadOnlyDictionary<Guid, Place> placesById) =>
        placeId.HasValue && placesById.TryGetValue(placeId.Value, out var place) ? ToCoordinate(place.Location) : null;

    private static TripViewerCoordinateDto? RouteEndpoint(LineString? route, bool first)
    {
        if (route == null || route.IsEmpty || route.NumPoints == 0)
        {
            return null;
        }

        var point = first ? route.GetPointN(0) : route.GetPointN(route.NumPoints - 1);
        return ToCoordinate(point);
    }

    private static JsonElement? ToGeoJson(Geometry? geometry)
    {
        if (geometry == null || geometry.IsEmpty)
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

    private static TripViewerVisitHistoryRowDto MapHistoryRow(Guid placeId, Guid regionId, PlaceVisitEvent visit)
    {
        var durationMinutes = visit.EndedAtUtc.HasValue
            ? (int)Math.Floor((visit.EndedAtUtc.Value - visit.ArrivedAtUtc).TotalMinutes)
            : (int?)null;

        return new TripViewerVisitHistoryRowDto(visit.Id, placeId, regionId, visit.ArrivedAtUtc, visit.EndedAtUtc, durationMinutes);
    }

    private static string ToProxyUrl(string rawUrl) => $"/Public/ProxyImage?url={Uri.EscapeDataString(rawUrl)}";

    private static string SafeIconName(string? iconName) =>
        !string.IsNullOrWhiteSpace(iconName) && ViewerIconNames.Contains(iconName) ? iconName : "marker";

    private static string SafeMarkerColor(string? markerColor) =>
        !string.IsNullOrWhiteSpace(markerColor) && ViewerMarkerColors.Contains(markerColor) ? markerColor : "bg-blue";

    private sealed record ViewerContext(
        string ViewerMode,
        bool IsOwner,
        bool IsAuthenticated,
        bool CanReadCounts,
        bool CanReadHistory);
}
