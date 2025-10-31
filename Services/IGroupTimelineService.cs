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
    Task<(IReadOnlyList<PublicLocationDto> Results, int TotalItems)> QueryLocationsAsync(GroupTimelineAccessContext context, GroupLocationsQueryRequest request, CancellationToken cancellationToken = default);
}
