namespace Wayfarer.Services;

/// <summary>Indicates an import input that cannot be safely persisted.</summary>
public sealed class TripImportValidationException(string message) : Exception(message);

public enum TripImportMode
{
    Auto,       // default: upsert if owned, else copy (status-quo)
    Upsert,     // force update an existing trip you own
    CreateNew   // always clone – ignore TripId inside the file
}

/// <summary>Bounded metadata describing one generic route simplification.</summary>
public sealed record TripImportNotice(
    string Code,
    string SegmentName,
    int? OriginalCoordinateCount,
    int? ResultingCoordinateCount,
    double? ToleranceMetres,
    double? MaximumDeviationMetres,
    int? AdditionalRouteCount = null);

/// <summary>Bounded successful import result returned without source geometry.</summary>
public sealed record TripImportResult(
    Guid TripId,
    IReadOnlyList<TripImportNotice> Notices,
    bool IsGenericWithRoutes = false)
{
    /// <summary>Supports existing internal consumers that require only the imported identity.</summary>
    public static implicit operator Guid(TripImportResult result) => result.TripId;

}

public interface ITripImportService
{
    /// <summary>Parses a Wayfarer-Extended-KML file and stores it.</summary>
    /// <returns>The imported Trip identity and bounded simplification notices.</returns>
    Task<TripImportResult> ImportWayfarerKmlAsync(
        Stream kmlStream,
        string currentUserId,
        TripImportMode mode = TripImportMode.Auto,
        CancellationToken cancellationToken = default);
}
