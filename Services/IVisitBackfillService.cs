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
}
