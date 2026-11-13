using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Planning;

namespace Wayfarer.Services;

/// <summary>Decodes the stored format-1 representation, never consulting live sources or inventing missing route facts.</summary>
internal sealed record TripCompositionSource(Trip Trip, bool Own, bool MediaOmitted)
{
    internal static TripCompositionSource Read(string json)
    {
        var root = JsonDocument.Parse(json).RootElement;
        if (root.GetProperty("format").GetInt32() != 1) throw Invalid();
        var graphElement = root.GetProperty("graph");
        foreach (var segment in graphElement.GetProperty("segments").EnumerateArray())
            foreach (var required in new[] { "id", "mode", "displayOrder", "hasCustomRoute", "waypoints", "estimatedDurationSource" })
                if (!segment.TryGetProperty(required, out _)) throw Invalid();
        var graph = graphElement.Deserialize<ApiTripDto>(TripCompositionSnapshot.Json) ?? throw Invalid();
        var own = root.TryGetProperty("planning", out var planning);
        var plans = own ? planning.Deserialize<PlanningOwnerSnapshot>(TripCompositionSnapshot.Json) : null;
        var trip = new Trip
        {
            Id = graph.Id,
            Name = graph.Name,
            Notes = graph.Notes,
            CoverImageUrl = graph.CoverImageUrl,
            ContributionPolicy = graph.ContributionPolicy,
            Credits = graph.Credits,
            CenterLat = graph.CenterLat,
            CenterLon = graph.CenterLon,
            Zoom = graph.Zoom,
            Planning = plans?.Trip
        };
        foreach (var r in graph.Regions ?? [])
        {
            var region = new Region
            {
                Id = r.Id,
                TripId = trip.Id,
                Name = r.Name,
                Notes = r.Notes,
                CoverImageUrl = r.CoverImageUrl,
                Center = Point(r.Center),
                DisplayOrder = r.DisplayOrder,
                Planning = plans?.Regions.GetValueOrDefault(r.Id)
            };
            foreach (var p in r.Places ?? []) region.Places.Add(new Place
            {
                Id = p.Id,
                RegionId = r.Id,
                Name = p.Name,
                Notes = p.Notes,
                DisplayOrder = p.DisplayOrder,
                Location = Point(p.Location),
                IconName = p.IconName,
                MarkerColor = p.MarkerColor,
                Address = p.Address,
                ResolvedFeatureName = p.ResolvedFeatureName,
                ResolvedFeatureType = p.ResolvedFeatureType,
                AddressEnrichmentProvider = p.AddressEnrichmentProvider,
                AddressEnrichmentStorageMode = p.AddressEnrichmentStorageMode,
                AddressEnrichedAt = p.AddressEnrichedAt,
                Planning = plans?.Places.GetValueOrDefault(p.Id)
            });
            foreach (var a in r.Areas ?? []) region.Areas.Add(new Area
            {
                Id = a.Id,
                RegionId = r.Id,
                Name = a.Name,
                Notes = a.Notes,
                DisplayOrder = a.DisplayOrder,
                FillHex = a.FillHex,
                Geometry = a.GeometryGeoJson == null ? throw Invalid() : Geometry<Polygon>(a.GeometryGeoJson),
                Planning = plans?.Areas.GetValueOrDefault(a.Id)
            });
            foreach (var place in region.Places) place.Region = region;
            foreach (var area in region.Areas) area.Region = region;
            trip.Regions.Add(region);
        }
        var places = trip.Regions.SelectMany(r => r.Places).ToDictionary(p => p.Id);
        foreach (var s in graph.Segments ?? [])
        {
            // Stored nullable ticks and source are authoritative; minutes cannot reconstruct missing historical precision.
            if (s.EstimatedDurationMinutes.HasValue && !s.EstimatedDurationTicks.HasValue
                || !Enum.TryParse<EstimatedDurationSource>(s.EstimatedDurationSource, out var durationSource)
                || s.HasCustomRoute != (s.RouteJson != null)) throw Invalid();
            var segment = new Segment
            {
                Id = s.Id,
                TripId = trip.Id,
                Title = s.Title,
                Notes = s.Notes,
                Mode = s.Mode,
                DisplayOrder = s.DisplayOrder,
                FromPlaceId = s.FromPlaceId,
                ToPlaceId = s.ToPlaceId,
                FromPlace = Anchor(s.FromPlaceId),
                ToPlace = Anchor(s.ToPlaceId),
                TransportProfileId = s.TransportProfileId,
                RouteGeometry = s.RouteJson == null ? null : Geometry<LineString>(s.RouteJson),
                EstimatedDuration = s.EstimatedDurationTicks.HasValue ? TimeSpan.FromTicks(s.EstimatedDurationTicks.Value) : null,
                EstimatedDurationSource = durationSource,
                EstimatedDistanceKm = s.EstimatedDistanceKm,
                RouteInstructionsJson = s.RouteInstructionsJson,
                RouteProvider = s.RouteProvider,
                RouteProviderConfigurationId = s.RouteProviderConfigurationId,
                RouteProviderConfigurationVersion = s.RouteProviderConfigurationVersion,
                RouteTransportProfileId = s.RouteTransportProfileId,
                RouteGeneratedAt = s.RouteGeneratedAt,
                RouteAttribution = s.RouteAttribution,
                RouteStorageMode = s.RouteStorageMode,
                RouteMappingMode = s.RouteMappingMode,
                Planning = plans?.Segments.GetValueOrDefault(s.Id)
            };
            foreach (var w in s.Waypoints) segment.Waypoints.Add(new SegmentWaypoint
            {
                SegmentId = s.Id,
                PlaceId = w.PlaceId,
                Place = Anchor(w.PlaceId)!,
                Position = w.Position,
                RouteVertexIndex = w.RouteVertexIndex
            });
            trip.Segments.Add(segment);
        }
        foreach (var t in graph.Tags ?? []) trip.Tags.Add(new Tag { Id = t.Id, Name = t.Name, Slug = t.Slug });
        return new(trip, own, root.GetProperty("mediaOmitted").GetBoolean());

        Place? Anchor(Guid? id) => id == null ? null : places.GetValueOrDefault(id.Value) ?? throw Invalid();
    }

    /// <summary>Looks up only type-scoped captured identities.</summary>
    internal static object Target(Trip trip, string kind, Guid id) => kind switch
    {
        "trip" when id == trip.Id => trip,
        "region" => trip.Regions.SingleOrDefault(r => r.Id == id) ?? throw Invalid(),
        "place" => trip.Regions.SelectMany(r => r.Places).SingleOrDefault(p => p.Id == id) ?? throw Invalid(),
        "area" => trip.Regions.SelectMany(r => r.Areas).SingleOrDefault(a => a.Id == id) ?? throw Invalid(),
        "segment" => trip.Segments.SingleOrDefault(s => s.Id == id) ?? throw Invalid(),
        _ => throw Invalid()
    };

    internal static Point? Point(double[]? pair) => pair == null ? null : pair.Length == 2 && pair.All(double.IsFinite)
        ? new Point(pair[0], pair[1]) { SRID = 4326 } : throw Invalid();
    internal static T Geometry<T>(string json) where T : Geometry
    {
        var geometry = new GeoJsonReader().Read<Geometry>(json);
        if (geometry is not T typed || !typed.IsValid || typed.IsEmpty) throw Invalid();
        typed.SRID = 4326;
        return typed;
    }
    internal static TripMetadataMutationRejected Invalid() => new(400, "composition-source-invalid",
        "The captured object lacks required saved facts. Select another object or explicitly repair a separate route through ordinary controls; no live source was reacquired.");
}
