using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Shared global lock-order composition for Segment aggregate writers.</summary>
public static partial class SegmentRouteReconciler
{
    /// <summary>Locks canonical Place and Region dependencies after profile and Segment lock classes.</summary>
    internal static async Task LockPlacesAndRegionsAsync(
        ApplicationDbContext dbContext,
        IReadOnlyList<Guid> placeIds,
        CancellationToken cancellationToken)
    {
        var orderedPlaces = placeIds.Distinct().Order().ToArray();
        var regionIds = await dbContext.Places.AsNoTracking()
            .Where(place => orderedPlaces.Contains(place.Id))
            .Select(place => place.RegionId).Distinct().Order().ToArrayAsync(cancellationToken);
        foreach (var placeId in orderedPlaces)
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM public.\"Places\" WHERE \"Id\" = {placeId} FOR UPDATE", cancellationToken);
        foreach (var regionId in regionIds)
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM public.\"Regions\" WHERE \"Id\" = {regionId} FOR UPDATE", cancellationToken);
    }
}
