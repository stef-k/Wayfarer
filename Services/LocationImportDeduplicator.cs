using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Parsers;

/// <summary>Owns legacy proximity and portable-key duplicate handling for location imports.</summary>
internal static class LocationImportDeduplicator
{
    public static async Task<(List<Location> ToInsert, int Skipped)> FilterAsync(
        ApplicationDbContext context,
        List<Location> batch,
        string userId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
            return (batch, 0);

        var minTimestamp = batch.Min(location => location.Timestamp).AddSeconds(-2);
        var maxTimestamp = batch.Max(location => location.Timestamp).AddSeconds(2);
        var existing = await context.Locations
            .Where(location => location.UserId == userId &&
                location.Timestamp >= minTimestamp && location.Timestamp <= maxTimestamp)
            .Select(location => new { location.Timestamp, location.Coordinates })
            .ToListAsync(cancellationToken);
        var seenKeys = (await context.Locations
            .Where(location => location.UserId == userId && location.IdempotencyKey.HasValue)
            .Select(location => location.IdempotencyKey!.Value)
            .ToListAsync(cancellationToken)).ToHashSet();

        var toInsert = new List<Location>();
        var skipped = 0;
        foreach (var location in batch)
        {
            var duplicate = location.IdempotencyKey.HasValue
                ? !seenKeys.Add(location.IdempotencyKey.Value)
                : existing.Any(candidate =>
                    Math.Abs((candidate.Timestamp - location.Timestamp).TotalSeconds) <= 1 &&
                    DistanceMeters(candidate.Coordinates.X, candidate.Coordinates.Y,
                        location.Coordinates.X, location.Coordinates.Y) <= 10);
            if (duplicate)
            {
                skipped++;
                logger.LogDebug("Skipping duplicate imported location at {Timestamp}", location.Timestamp);
            }
            else
            {
                toInsert.Add(location);
            }
        }

        return (toInsert, skipped);
    }

    public static async Task<int> InsertAsync(
        ApplicationDbContext context,
        List<Location> locations,
        string userId,
        CancellationToken cancellationToken)
    {
        var legacy = locations.Where(location => !location.IdempotencyKey.HasValue).ToList();
        if (legacy.Count > 0)
        {
            context.Locations.AddRange(legacy);
            await context.SaveChangesAsync(cancellationToken);
        }

        var reused = 0;
        foreach (var location in locations.Where(location => location.IdempotencyKey.HasValue))
        {
            context.Locations.Add(location);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                context.Entry(location).State = EntityState.Detached;
                if (!await context.Locations.AsNoTracking().AnyAsync(candidate =>
                    candidate.UserId == userId && candidate.IdempotencyKey == location.IdempotencyKey,
                    cancellationToken))
                {
                    throw;
                }
                reused++;
            }
        }

        return reused;
    }

    private static double DistanceMeters(double lon1, double lat1, double lon2, double lat2)
    {
        const double radius = 6_371_000;
        var latitude = Radians(lat2 - lat1);
        var longitude = Radians(lon2 - lon1);
        var value = Math.Sin(latitude / 2) * Math.Sin(latitude / 2) +
            Math.Cos(Radians(lat1)) * Math.Cos(Radians(lat2)) *
            Math.Sin(longitude / 2) * Math.Sin(longitude / 2);
        return radius * 2 * Math.Atan2(Math.Sqrt(value), Math.Sqrt(1 - value));
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180;
}
