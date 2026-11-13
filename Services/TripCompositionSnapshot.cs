using System.Text.Json;
using System.Text.Json.Serialization;
using NetTopologySuite.IO;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;

namespace Wayfarer.Services;

/// <summary>Explicit reusable graph plus an optional separate own-planning projection. Never serializes account entities or origins.</summary>
internal static class TripCompositionSnapshot
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Captures complete stored routes, without a public fallback resolver replacing custom geometry or measurements.</summary>
    internal static object Project(Trip source, bool own, bool mediaOmitted = false)
    {
        var places = source.Regions.SelectMany(r => r.Places).Select(p => p.Id).ToHashSet();
        foreach (var segment in source.Segments)
        {
            SegmentMeasurementState.Validate(segment);
            if (segment.Waypoints.Select(w => (Guid?)w.PlaceId).Append(segment.FromPlaceId).Append(segment.ToPlaceId)
                .Any(id => id.HasValue && !places.Contains(id.Value)))
                throw new InvalidOperationException("A captured route references a Place outside the authorized source.");
        }
        var writer = new GeoJsonWriter();
        var graph = source.ToApiDto(source.Segments.Select(s => new ApiTripSegmentDto
        {
            Id = s.Id, Title = s.Title, Mode = s.Mode, Notes = s.Notes, DisplayOrder = s.DisplayOrder,
            FromPlaceId = s.FromPlaceId, ToPlaceId = s.ToPlaceId, TransportProfileId = s.TransportProfileId,
            HasCustomRoute = s.RouteGeometry != null, RouteJson = s.RouteGeometry == null ? null : writer.Write(s.RouteGeometry),
            EstimatedDistanceKm = s.EstimatedDistanceKm, EstimatedDurationMinutes = s.EstimatedDuration?.TotalMinutes,
            EstimatedDurationTicks = s.EstimatedDuration?.Ticks, EstimatedDurationSource = s.EstimatedDurationSource.ToString(),
            RouteInstructionsJson = s.RouteInstructionsJson, RouteProvider = s.RouteProvider,
            RouteProviderConfigurationId = s.RouteProviderConfigurationId, RouteProviderConfigurationVersion = s.RouteProviderConfigurationVersion,
            RouteTransportProfileId = s.RouteTransportProfileId, RouteGeneratedAt = s.RouteGeneratedAt,
            RouteAttribution = s.RouteAttribution, RouteStorageMode = s.RouteStorageMode, RouteMappingMode = s.RouteMappingMode,
            Waypoints = s.Waypoints.OrderBy(w => w.Position).Select(w => new ApiTripSegmentWaypointDto
                { PlaceId = w.PlaceId, Position = w.Position, RouteVertexIndex = w.RouteVertexIndex }).ToArray()
        }).ToArray());
        // Record the visible editor sequence explicitly; raw nullable values remain in the graph.
        var order = source.Regions.OrderBy(r => r.DisplayOrder).ThenBy(r => r.Id).ToArray();
        return new
        {
            format = 1, graph, mediaOmitted,
            regionOrder = order.Select(r => r.Id),
            placeOrder = order.ToDictionary(r => r.Id, r => r.Places.OrderBy(p => p.DisplayOrder.HasValue ? 0 : 1).ThenBy(p => p.DisplayOrder).ThenBy(p => p.Id).Select(p => p.Id).ToArray()),
            areaOrder = order.ToDictionary(r => r.Id, r => r.Areas.OrderBy(a => a.DisplayOrder).ThenBy(a => a.Name).ThenBy(a => a.Id).Select(a => a.Id).ToArray()),
            planning = own ? PlanningOwnerSnapshot.FromTrip(source) : null
        };
    }

    internal static string Capture(Trip source, bool own, bool mediaOmitted = false) => JsonSerializer.Serialize(Project(source, own, mediaOmitted), Json);
}

/// <summary>Type-scoped identities avoid conflating valid equal UUIDs in different source tables.</summary>
internal sealed record TripCompositionMapping(string Kind, Guid SourceId, Guid DestinationId, string Source = "a", string Decision = "included");
