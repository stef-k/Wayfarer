using NetTopologySuite.Geometries;
using Wayfarer.Models;

namespace Wayfarer.Parsers;

/// <summary>Detached, untrusted transport data parsed from one Wayfarer-native KML document.</summary>
public sealed record WayfarerKmlDocument(
    int Version, Guid TripId, string Name, string? CoverImageUrl, string? Notes,
    double? CenterLat, double? CenterLon, int? Zoom, IReadOnlyList<string> Tags,
    IReadOnlyList<WayfarerKmlRegion> Regions, IReadOnlyList<WayfarerKmlSegment> Segments);

/// <summary>Detached Region transport data.</summary>
public sealed record WayfarerKmlRegion(
    Guid Id, string Name, int DisplayOrder, string? Notes, Point? Center,
    IReadOnlyList<WayfarerKmlPlace> Places, IReadOnlyList<WayfarerKmlArea> Areas);

/// <summary>Detached Place transport data.</summary>
public sealed record WayfarerKmlPlace(
    Guid Id, string Name, int DisplayOrder, string? Notes, string? IconName,
    string? MarkerColor, string? Address, Point? Location);

/// <summary>Detached Area transport data.</summary>
public sealed record WayfarerKmlArea(
    Guid Id, string Name, int DisplayOrder, string? Notes, string? FillHex, Polygon? Geometry);

/// <summary>Detached Segment transport data with explicit route and measurement provenance.</summary>
public sealed record WayfarerKmlSegment(
    Guid Id, Guid? FromPlaceId, Guid? ToPlaceId, string Mode, string TransportProfileKey,
    double? DistanceKm, long? DurationSeconds, EstimatedDurationSource DurationSource,
    int DisplayOrder, string? Notes, bool HasCustomRoute, IReadOnlyList<Guid> WaypointPlaceIds,
    IReadOnlyList<int?> WaypointRouteVertexIndices, LineString? Geometry);

/// <summary>Structural classification for one parsed KML document.</summary>
public enum WayfarerKmlKind { Generic, NativeV1, NativeV2 }
