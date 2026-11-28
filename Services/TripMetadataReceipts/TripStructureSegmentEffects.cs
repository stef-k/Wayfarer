using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services.TripMetadataReceipts;

/// <summary>Captures precisely the saved Segment and waypoint effects under the lifecycle owner's locks.</summary>
internal static class TripStructureSegmentEffects
{
    /// <summary>Projects deleted waypoint identities, route changes and any independent order changes.</summary>
    internal static async Task<object> ReadAsync(ApplicationDbContext db, Guid tripId, Segment[] originals,
        Segment[] originalOrder, IReadOnlyCollection<Guid> deletedIds, IReadOnlyCollection<Guid> updatedIds, CancellationToken token)
    {
        var order = deletedIds.Count != 0
            ? await db.Segments.AsNoTracking().Where(segment => segment.TripId == tripId)
                .OrderBy(segment => segment.DisplayOrder).ThenBy(segment => segment.Id)
                .Select(segment => new SegmentPosition(segment.Id, segment.DisplayOrder)).ToArrayAsync(token)
            : null;
        var oldOrder = originalOrder.ToDictionary(segment => segment.Id, segment => segment.DisplayOrder);
        var changed = updatedIds.Concat(order?.Where(row => oldOrder[row.Id] != row.DisplayOrder).Select(row => row.Id) ?? [])
            .Distinct().ToArray();
        var saved = changed.Length == 0 ? [] : await db.Segments.AsNoTracking().AsSplitQuery()
            .Where(segment => segment.TripId == tripId && changed.Contains(segment.Id))
            .Include(segment => segment.FromPlace).Include(segment => segment.ToPlace)
            .Include(segment => segment.Waypoints).ThenInclude(waypoint => waypoint.Place)
            .OrderBy(segment => segment.Id).ToArrayAsync(token);
        if (saved.Length != changed.Length) throw new InvalidOperationException("An affected Segment disappeared from the locked Trip.");
        var savedWaypoints = saved.SelectMany(segment => segment.Waypoints).Select(Association).ToHashSet();
        var removed = originals.Where(segment => deletedIds.Contains(segment.Id) || changed.Contains(segment.Id))
            .SelectMany(segment => segment.Waypoints).Select(Association).Where(item => !savedWaypoints.Contains(item))
            .OrderBy(item => item.SegmentId).ThenBy(item => item.PlaceId).ToArray();
        return new
        {
            deletedSegmentIds = deletedIds.Order().ToArray(), removedWaypoints = removed,
            updatedSegments = saved.Select(Project).ToArray(), segmentOrder = order
        };
    }

    /// <summary>Captures committed endpoint, route, measurement and provenance values in the receipt grammar.</summary>
    private static object Project(Segment segment)
    {
        var effective = segment.RouteGeometry ?? EditorTripStateMapper.BuildEffectiveRoute(segment);
        return new
        {
            segment.Id, segment.DisplayOrder, segment.FromPlaceId, segment.ToPlaceId,
            waypoints = segment.Waypoints.OrderBy(row => row.Position).ThenBy(row => row.PlaceId)
                .Select(row => new { row.PlaceId, row.Position, row.RouteVertexIndex }).ToArray(),
            route = new { storedDigest = Geometry(segment.RouteGeometry), effectiveDigest = Geometry(effective) },
            segment.EstimatedDistanceKm,
            estimatedDurationTicks = segment.EstimatedDuration?.Ticks.ToString(CultureInfo.InvariantCulture),
            estimatedDurationSource = segment.EstimatedDurationSource.ToString(), segment.TransportProfileId,
            provenanceDigests = Provenance(segment)
        };
    }

    /// <summary>Retains the nine provenance comparison values solely as typed field digests.</summary>
    private static object Provenance(Segment segment)
    {
        static string Hash(string field, string? value) => TripStructureDigests.Text("segment", field, value);
        return new
        {
            routeInstructionsJson = Hash("routeInstructionsJson", segment.RouteInstructionsJson),
            routeProvider = Hash("routeProvider", segment.RouteProvider),
            routeProviderConfigurationId = Hash("routeProviderConfigurationId", segment.RouteProviderConfigurationId?.ToString("D")),
            routeProviderConfigurationVersion = Hash("routeProviderConfigurationVersion", segment.RouteProviderConfigurationVersion?.ToString(CultureInfo.InvariantCulture)),
            routeTransportProfileId = Hash("routeTransportProfileId", segment.RouteTransportProfileId?.ToString("D")),
            routeMappingMode = Hash("routeMappingMode", segment.RouteMappingMode),
            routeGeneratedAt = Hash("routeGeneratedAt", segment.RouteGeneratedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture)),
            routeAttribution = Hash("routeAttribution", segment.RouteAttribution),
            routeStorageMode = Hash("routeStorageMode", segment.RouteStorageMode)
        };
    }

    /// <summary>Produces the fixed little-endian SRID-aware digest used by the historical route projection.</summary>
    private static string? Geometry(LineString? geometry) => geometry == null ? null
        : "sha256:" + Convert.ToHexStringLower(SHA256.HashData(new WKBWriter(ByteOrder.LittleEndian, true).Write(geometry)));

    /// <summary>Uses the durable composite row identity, independent of any internal replacement.</summary>
    private static EditorLifecycleWaypointAssociationDto Association(SegmentWaypoint row) => new(row.SegmentId, row.PlaceId);

    private sealed record SegmentPosition(Guid Id, int DisplayOrder);
}
