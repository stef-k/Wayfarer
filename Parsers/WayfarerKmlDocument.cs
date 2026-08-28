using System.Xml.Linq;
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
    string? MarkerColor, string? Address, Point? Location,
    string? ResolvedFeatureName = null, string? ResolvedFeatureType = null,
    string? AddressEnrichmentProvider = null, string? AddressEnrichmentStorageMode = null,
    DateTimeOffset? AddressEnrichedAt = null);

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

/// <summary>Retains one hardened detached XML document with its structural native classification.</summary>
public sealed record WayfarerKmlClassification(
    WayfarerKmlKind Kind,
    WayfarerKmlDocument? Document,
    XDocument Source)
{
    /// <summary>Preserves callers that explicitly store the established two-value tuple.</summary>
    public static implicit operator (WayfarerKmlKind Kind, WayfarerKmlDocument? Document)(
        WayfarerKmlClassification classification) => (classification.Kind, classification.Document);

    /// <summary>Preserves the established two-value deconstruction contract.</summary>
    public void Deconstruct(out WayfarerKmlKind kind, out WayfarerKmlDocument? document)
    {
        kind = Kind;
        document = Document;
    }
}
