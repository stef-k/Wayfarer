using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Owns observation eligibility and deterministic current ordering, independently of history clocks.</summary>
public static class LocationPresence
{
    /// <summary>No existing time contract grants clock drift; allow zero future or clock-disagreement tolerance.</summary>
    public static readonly TimeSpan ClockSkewTolerance = TimeSpan.Zero;

    /// <summary>Fails closed without affirmative fix evidence, consistent elapsed age, and recent event/fix clocks.</summary>
    public static DateTime? CheckInObservation(Location location, int windowMinutes)
    {
        if (windowMinutes <= 0 || location.CoordinateOrigin != "sensor"
            || location.ObservedAtUtc is not { } fix || location.ObservedAgeAtEventMs is not >= 0)
            return null;
        var observed = fix.UtcDateTime;
        var eventTime = location.LocalTimestamp;
        var receipt = location.Timestamp;
        var wallAgeMs = (eventTime - observed).TotalMilliseconds;
        // Millisecond age is compared at its declared precision; sub-millisecond serialization is immaterial.
        if (observed > eventTime + ClockSkewTolerance || eventTime > receipt + ClockSkewTolerance
            || Math.Abs(wallAgeMs - location.ObservedAgeAtEventMs.Value) >= 1
            || receipt - observed > TimeSpan.FromMinutes(windowMinutes))
            return null;
        return observed;
    }

    /// <summary>Ordinary passive logging retains capture-time semantics, without inventing raw sensor evidence.</summary>
    public static DateTime? AutomaticObservation(DateTime capturedUtc, DateTime receiptUtc, int windowMinutes) =>
        windowMinutes > 0 && capturedUtc <= receiptUtc + ClockSkewTolerance
        && receiptUtc - capturedUtc <= TimeSpan.FromMinutes(windowMinutes) ? capturedUtc : null;

    /// <summary>Only previously admitted observations can be last-known; Id breaks equal-fix ties consistently.</summary>
    public static IOrderedQueryable<Location> Current(IQueryable<Location> locations) => locations
        .Where(location => location.PresenceObservedAtUtc != null)
        .OrderByDescending(location => location.PresenceObservedAtUtc).ThenByDescending(location => location.Id);

    /// <summary>Freshness expires; invalid windows never make last-known or historical data indefinitely live.</summary>
    public static bool IsLive(Location location, double windowMinutes, DateTime nowUtc) =>
        location.PresenceObservedAtUtc is { } observed && windowMinutes > 0 && double.IsFinite(windowMinutes)
        && observed <= nowUtc + ClockSkewTolerance && nowUtc - observed <= TimeSpan.FromMinutes(windowMinutes);

    /// <summary>Coordinate or event edits invalidate coordinate-bound evidence and server admission together.</summary>
    public static void Invalidate(Location location)
    {
        location.CoordinateOrigin = null;
        location.ObservedAtUtc = null;
        location.ObservedAgeAtEventMs = null;
        location.PresenceObservedAtUtc = null;
    }

    /// <summary>Serializes postcommit decisions across processes without enclosing provider contact or the UUID insert.</summary>
    public static async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> LockAsync(
        ApplicationDbContext db, string userId, CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational()) return null;
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({'p' + userId}, 855))", cancellationToken);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }
}
