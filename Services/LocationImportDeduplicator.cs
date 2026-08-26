using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wayfarer.Models;

namespace Wayfarer.Parsers;

/// <summary>Owns legacy proximity and portable-key duplicate handling for location imports.</summary>
internal static class LocationImportDeduplicator
{
    public static async Task<(List<Location> ToInsert, int Skipped)> FilterAsync(
        ApplicationDbContext context,
        List<Location> batch,
        IReadOnlySet<Guid> batchKeys,
        string userId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
            return (batch, 0);

        var keys = batchKeys.ToArray();
        var seenKeys = (await context.Locations.AsNoTracking()
            .Where(location => location.UserId == userId && location.IdempotencyKey.HasValue &&
                keys.Contains(location.IdempotencyKey.Value))
            .Select(location => location.IdempotencyKey!.Value)
            .ToListAsync(cancellationToken)).ToHashSet();

        var toInsert = new List<Location>();
        var acceptedLegacy = new List<Location>();
        var skipped = 0;
        foreach (var location in batch)
        {
            var duplicate = location.IdempotencyKey.HasValue
                ? !seenKeys.Add(location.IdempotencyKey.Value)
                : acceptedLegacy.Any(candidate => IsLegacyDuplicate(candidate, location)) ||
                  await IsPersistedLegacyDuplicateAsync(context, userId, location, cancellationToken);
            if (duplicate)
            {
                skipped++;
                logger.LogDebug("Skipping duplicate imported location at {Timestamp}", location.Timestamp);
            }
            else
            {
                toInsert.Add(location);
                if (!location.IdempotencyKey.HasValue) acceptedLegacy.Add(location);
            }
        }

        return (toInsert, skipped);
    }

    private static bool IsLegacyDuplicate(Location candidate, Location location) =>
        Math.Abs((candidate.Timestamp - location.Timestamp).TotalSeconds) <= 1 &&
        DistanceMeters(candidate.Coordinates.X, candidate.Coordinates.Y,
            location.Coordinates.X, location.Coordinates.Y) <= 10;

    private static async Task<bool> IsPersistedLegacyDuplicateAsync(
        ApplicationDbContext context, string userId, Location location, CancellationToken cancellationToken)
    {
        var candidates = context.Locations.AsNoTracking().Where(candidate =>
            candidate.UserId == userId &&
            candidate.Timestamp >= location.Timestamp.AddSeconds(-1) &&
            candidate.Timestamp <= location.Timestamp.AddSeconds(1));
        if (context.Database.IsRelational())
            return await candidates.AnyAsync(candidate =>
                candidate.Coordinates.IsWithinDistance(location.Coordinates, 10), cancellationToken);
        return (await candidates.ToListAsync(cancellationToken)).Any(candidate =>
            IsLegacyDuplicate(candidate, location));
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

        var keyed = locations.Where(location => location.IdempotencyKey.HasValue).ToList();
        if (keyed.Count == 0) return 0;
        var transaction = context.Database.CurrentTransaction;
        if (transaction is null || !context.Database.IsNpgsql())
        {
            context.Locations.AddRange(keyed);
            await context.SaveChangesAsync(cancellationToken);
            return 0;
        }

        const string savepoint = "before_keyed_location_insert";
        var remaining = keyed;
        var reused = 0;
        while (remaining.Count > 0)
        {
            await transaction.CreateSavepointAsync(savepoint, cancellationToken);
            context.Locations.AddRange(remaining);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                await transaction.ReleaseSavepointAsync(savepoint, cancellationToken);
                break;
            }
            catch (DbUpdateException exception) when (IsKeyedLocationConflict(exception))
            {
                await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
                foreach (var location in remaining)
                    context.Entry(location).State = EntityState.Detached;

                var keys = remaining.Select(location => location.IdempotencyKey!.Value).ToArray();
                var winners = (await context.Locations.AsNoTracking()
                    .Where(location => location.UserId == userId && location.IdempotencyKey.HasValue &&
                        keys.Contains(location.IdempotencyKey.Value))
                    .Select(location => location.IdempotencyKey!.Value)
                    .ToListAsync(cancellationToken)).ToHashSet();
                if (winners.Count == 0) throw;
                reused += winners.Count;
                remaining = remaining.Where(location => !winners.Contains(location.IdempotencyKey!.Value)).ToList();
                await transaction.ReleaseSavepointAsync(savepoint, cancellationToken);
            }
        }

        return reused;
    }

    private static bool IsKeyedLocationConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres &&
        postgres.SqlState == PostgresErrorCodes.UniqueViolation &&
        postgres.ConstraintName == "IX_Location_UserId_IdempotencyKey";

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
