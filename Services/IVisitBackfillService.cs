using Wayfarer.Models.Dtos;

namespace Wayfarer.Services;

/// <summary>
/// Service for backfilling visit records by analyzing location history against trip places.
/// Supports preview (analysis without changes), apply (create/delete visits), and clear operations.
/// </summary>
public interface IVisitBackfillService
{
    /// <summary>
    /// Analyzes location history against trip places to find potential visits.
    /// Does not modify any data - preview only.
    /// Results are deduplicated by (PlaceId, Date) and grouped by region, place name, then date descending.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="tripId">The trip ID to analyze.</param>
    /// <param name="fromDate">Optional start date filter for location history.</param>
    /// <param name="toDate">Optional end date filter for location history.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Preview result with candidates and stale visits.</returns>
    Task<BackfillPreviewDto> PreviewAsync(
        string userId,
        Guid tripId,
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies backfill changes - creates new visits and deletes stale visits.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="tripId">The trip ID.</param>
    /// <param name="request">The apply request with visits to create and delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result with counts of created/deleted/skipped visits.</returns>
    Task<BackfillResultDto> ApplyAsync(
        string userId,
        Guid tripId,
        BackfillApplyRequestDto request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all visits for a trip.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="tripId">The trip ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result with count of deleted visits.</returns>
    Task<BackfillResultDto> ClearVisitsAsync(
        string userId,
        Guid tripId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets lightweight metadata for backfill analysis progress feedback.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="tripId">The trip ID.</param>
    /// <param name="fromDate">Optional start date filter.</param>
    /// <param name="toDate">Optional end date filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Metadata for progress feedback.</returns>
    Task<BackfillInfoDto> GetInfoAsync(
        string userId,
        Guid tripId,
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets location pings that contributed to a visit candidate or suggestion.
    /// Used for the map context modal to visualize location evidence.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="placeId">The place ID (for validation).</param>
    /// <param name="lat">Place latitude for spatial filtering.</param>
    /// <param name="lon">Place longitude for spatial filtering.</param>
    /// <param name="firstSeenUtc">Start of the time window.</param>
    /// <param name="lastSeenUtc">End of the time window.</param>
    /// <param name="searchRadiusMeters">Search radius in meters.</param>
    /// <param name="page">Page number (1-based).</param>
    /// <param name="pageSize">Number of locations per page.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Paginated list of candidate locations with total count.</returns>
    Task<(List<CandidateLocationDto> Locations, int TotalCount)> GetCandidateLocationsAsync(
        string userId,
        Guid placeId,
        double lat,
        double lon,
        DateTime firstSeenUtc,
        DateTime lastSeenUtc,
        int searchRadiusMeters,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default);
}
