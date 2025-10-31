using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Util;
using LocationEntity = Wayfarer.Models.Location;

namespace Wayfarer.Services;

/// <summary>
/// Provides member filtering and location retrieval for group timelines.
/// </summary>
public class GroupTimelineService : IGroupTimelineService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly LocationService _locationService;

    public GroupTimelineService(ApplicationDbContext dbContext, LocationService locationService)
    {
        _dbContext = dbContext;
        _locationService = locationService;
    }

    public async Task<GroupTimelineAccessContext?> BuildAccessContextAsync(Guid groupId, string callerUserId, CancellationToken cancellationToken = default)
    {
        var group = await _dbContext.Groups
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.Id == groupId && !g.IsArchived, cancellationToken);
        if (group == null) return null;

        var activeMembers = await _dbContext.GroupMembers
            .Where(m => m.GroupId == groupId && m.Status == GroupMember.MembershipStatuses.Active)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var callerMembership = activeMembers.FirstOrDefault(m => string.Equals(m.UserId, callerUserId, StringComparison.Ordinal));
        var isFriends = string.Equals(group.GroupType, "Friends", StringComparison.OrdinalIgnoreCase);

        var allowed = new HashSet<string>(StringComparer.Ordinal);
        if (callerMembership != null)
        {
            foreach (var member in activeMembers)
            {
                if (string.Equals(member.UserId, callerMembership.UserId, StringComparison.Ordinal))
                {
                    allowed.Add(member.UserId);
                    continue;
                }

                if (!isFriends)
                {
                    allowed.Add(member.UserId);
                    continue;
                }

                if (!member.OrgPeerVisibilityAccessDisabled)
                {
                    allowed.Add(member.UserId);
                }
            }
        }

        var settings = await _dbContext.ApplicationSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);
        var threshold = settings?.LocationTimeThresholdMinutes ?? 10;

        return new GroupTimelineAccessContext(group, callerMembership, activeMembers, allowed, isFriends, threshold);
    }

    public async Task<IReadOnlyList<PublicLocationDto>> GetLatestLocationsAsync(GroupTimelineAccessContext context, IEnumerable<string>? includeUserIds, CancellationToken cancellationToken = default)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        if (!context.IsMember) throw new InvalidOperationException("Caller is not an active member of the group.");

        var activeIds = context.ActiveMembers.Select(m => m.UserId).ToList();
        var requested = includeUserIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var userIds = (requested != null && requested.Count > 0 ? requested : activeIds)
            .Where(id => context.AllowedUserIds.Contains(id))
            .ToList();

        if (userIds.Count == 0) return Array.Empty<PublicLocationDto>();

        var results = new List<PublicLocationDto>(userIds.Count);
        foreach (var uid in userIds)
        {
            var latest = await _dbContext.Locations
                .Where(l => l.UserId == uid)
                .OrderByDescending(l => l.LocalTimestamp)
                .Include(l => l.ActivityType)
                .AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);

            if (latest == null) continue;
            results.Add(MapLatest(latest, uid, context.LocationTimeThresholdMinutes));
        }

        return results;
    }

    public async Task<(IReadOnlyList<PublicLocationDto> Results, int TotalItems)> QueryLocationsAsync(GroupTimelineAccessContext context, GroupLocationsQueryRequest request, CancellationToken cancellationToken = default)
    {
        if (context == null) throw new ArgumentNullException(nameof(context));
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (!context.IsMember) throw new InvalidOperationException("Caller is not an active member of the group.");

        var activeIds = context.ActiveMembers.Select(m => m.UserId).ToList();
        var requested = request.UserIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var userIds = (requested != null && requested.Count > 0 ? requested : activeIds)
            .Where(id => context.AllowedUserIds.Contains(id))
            .ToList();

        if (userIds.Count == 0)
        {
            return (Array.Empty<PublicLocationDto>(), 0);
        }

        bool multipleUsers = userIds.Count > 1;
        string dateType = request.DateType ?? string.Empty;
        int? year = request.Year;
        int? month = request.Month;
        int? day = request.Day;

        if (multipleUsers)
        {
            dateType = "day";
            if (!year.HasValue || !month.HasValue || !day.HasValue)
            {
                var today = DateTime.UtcNow;
                year ??= today.Year;
                month ??= today.Month;
                day ??= today.Day;
            }
        }

        if (string.IsNullOrWhiteSpace(dateType))
        {
            var today = DateTime.UtcNow;
            dateType = "day";
            year = today.Year;
            month = today.Month;
            day = today.Day;
        }

        var combined = new List<PublicLocationDto>();
        int totalItems = 0;

        foreach (var uid in userIds)
        {
            if (!string.IsNullOrWhiteSpace(dateType) && year.HasValue)
            {
                var (locations, _) = await _locationService.GetLocationsByDateAsync(
                    uid,
                    dateType!,
                    year.Value,
                    month,
                    day,
                    cancellationToken);

                var filtered = locations.Where(dto =>
                    dto.Coordinates.X >= request.MinLng && dto.Coordinates.X <= request.MaxLng &&
                    dto.Coordinates.Y >= request.MinLat && dto.Coordinates.Y <= request.MaxLat).ToList();

                foreach (var dto in filtered) dto.UserId = uid;
                combined.AddRange(filtered);
                totalItems += filtered.Count;
            }
            else
            {
                var (locations, userTotal) = await _locationService.GetLocationsAsync(
                    request.MinLng,
                    request.MinLat,
                    request.MaxLng,
                    request.MaxLat,
                    request.ZoomLevel,
                    uid,
                    cancellationToken);

                foreach (var dto in locations) dto.UserId = uid;
                combined.AddRange(locations);
                totalItems += userTotal;
            }
        }

        return (combined, totalItems);
    }

    private static PublicLocationDto MapLatest(LocationEntity location, string userId, int threshold)
    {
        return new PublicLocationDto
        {
            Id = location.Id,
            UserId = userId,
            Timestamp = location.Timestamp,
            LocalTimestamp = CoordinateTimeZoneConverter.ConvertUtcToLocal(
                location.Coordinates.Y,
                location.Coordinates.X,
                DateTime.SpecifyKind(location.LocalTimestamp, DateTimeKind.Utc)),
            Timezone = location.TimeZoneId ?? string.Empty,
            Coordinates = location.Coordinates,
            Accuracy = location.Accuracy,
            Altitude = location.Altitude,
            Speed = location.Speed,
            LocationType = location.LocationType,
            ActivityType = location.ActivityType?.Name,
            Address = location.Address,
            FullAddress = location.FullAddress,
            StreetName = location.StreetName,
            PostCode = location.PostCode,
            Place = location.Place,
            Region = location.Region,
            Country = location.Country,
            Notes = location.Notes,
            VehicleId = location.VehicleId,
            IsLatestLocation = true,
            LocationTimeThresholdMinutes = threshold
        };
    }
}
