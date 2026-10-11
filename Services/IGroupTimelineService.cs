using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wayfarer.Models.Dtos;

namespace Wayfarer.Services;

public interface IGroupTimelineService
{
    Task<GroupTimelineAccessContext?> BuildAccessContextAsync(Guid groupId, string callerUserId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PublicLocationDto>> GetLatestLocationsAsync(GroupTimelineAccessContext context, IEnumerable<string>? includeUserIds, CancellationToken cancellationToken = default);
    /// <summary>Normalizes Mobile subjects, selects history once, then globally orders and pages selected markers.</summary>
    Task<GroupLocationsQueryResult> QueryLocationsAsync(GroupTimelineAccessContext context, GroupLocationsQueryRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Selects unpaged history for supplied subjects allowed by the access context, preserving their order.
    /// Applies effective dates and the multi-subject day restriction to that authorized selection, then
    /// uses the existing LocationService readers with post-date bbox filtering and attribution.
    /// Date totals count filtered selected markers; viewport totals retain the expanded-box row count.
    /// Empty or unauthorized-only selections return no markers and zero total without an audience fallback.
    /// </summary>
    /// <param name="context">The current request's persisted, active-member access context.</param>
    /// <param name="userIds">Adapter-selected subjects matched exactly against context.AllowedUserIds.</param>
    /// <param name="request">Existing date/viewport inputs; paging and subject normalization remain with adapters.</param>
    /// <param name="cancellationToken">Cancellation forwarded to each underlying location read.</param>
    /// <returns>The unpaged marker selection and its path-specific total, before Mobile sorting or paging.</returns>
    Task<(IReadOnlyList<PublicLocationDto> Results, int TotalItems)> SelectHistoryLocationsAsync(
        GroupTimelineAccessContext context, IReadOnlyList<string> userIds, GroupLocationsQueryRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record GroupLocationsQueryResult(
    IReadOnlyList<PublicLocationDto> Results,
    int TotalItems,
    int PageSize,
    bool HasMore,
    bool IsTruncated,
    string? NextPageToken);
