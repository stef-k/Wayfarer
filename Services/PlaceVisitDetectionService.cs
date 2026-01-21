using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;

namespace Wayfarer.Services;

/// <summary>
/// Service interface for detecting and tracking place visits based on location pings.
/// </summary>
public interface IPlaceVisitDetectionService
{
    /// <summary>
    /// Process a location ping for visit detection.
    /// Called before timeline threshold checks in log-location.
    /// This is a side-effect-only operation that does not affect the response.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    /// <param name="location">The ping location as a PostGIS Point.</param>
    /// <param name="accuracyMeters">Optional GPS accuracy in meters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ProcessPingAsync(
        string userId,
        Point location,
        double? accuracyMeters,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implements place visit detection using PostGIS spatial queries.
/// Detects when users visit planned trip places and tracks visit events.
/// </summary>
public class PlaceVisitDetectionService : IPlaceVisitDetectionService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IApplicationSettingsService _settingsService;
    private readonly SseService _sseService;
    private readonly ILogger<PlaceVisitDetectionService> _logger;

    public PlaceVisitDetectionService(
        ApplicationDbContext dbContext,
        IApplicationSettingsService settingsService,
        SseService sseService,
        ILogger<PlaceVisitDetectionService> logger)
    {
        _dbContext = dbContext;
        _settingsService = settingsService;
        _sseService = sseService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ProcessPingAsync(
        string userId,
        Point location,
        double? accuracyMeters,
        CancellationToken cancellationToken = default)
    {
        var settings = _settingsService.GetSettings();
        var now = DateTime.UtcNow;

        // Check accuracy rejection threshold
        if (ShouldRejectForAccuracy(accuracyMeters, settings))
        {
            _logger.LogDebug(
                "Visit detection skipped for user {UserId}: accuracy {Accuracy}m exceeds threshold {Threshold}m",
                userId, accuracyMeters, settings.VisitedAccuracyRejectMeters);
            return;
        }

        // Calculate effective radius based on GPS accuracy
        var effectiveRadius = CalculateEffectiveRadius(accuracyMeters, settings);

        // Find the nearest place within search radius
        var nearestPlace = await FindNearestPlaceWithinRadiusAsync(
            userId, location, settings.VisitedMaxSearchRadiusMeters, cancellationToken);

        if (nearestPlace == null)
        {
            // No place nearby - close any stale visits and clean up candidates
            await CloseStaleVisitsAsync(userId, now, settings, cancellationToken);
            await CleanupStaleCandidatesAsync(userId, now, settings, cancellationToken);
            return;
        }

        // Check if the nearest place is within effective radius
        if (nearestPlace.Distance > effectiveRadius)
        {
            // Outside effective radius - close stale visits and clean up
            await CloseStaleVisitsAsync(userId, now, settings, cancellationToken);
            await CleanupStaleCandidatesAsync(userId, now, settings, cancellationToken);
            return;
        }

        // Place is within effective radius - process visit detection
        var openVisit = await FindOpenVisitAsync(userId, nearestPlace.Place.Id, cancellationToken);

        if (openVisit != null)
        {
            // Update existing open visit
            await HandleOpenVisitAsync(openVisit, now, cancellationToken);
        }
        else
        {
            // No open visit - use candidate confirmation
            await HandleCandidateConfirmationAsync(
                userId, nearestPlace.Place, nearestPlace.Region, nearestPlace.Trip,
                now, settings, cancellationToken);
        }

        // Close any other stale visits for this user
        await CloseStaleVisitsAsync(userId, now, settings, cancellationToken);
        await CleanupStaleCandidatesAsync(userId, now, settings, cancellationToken);
    }

    /// <summary>
    /// Determines if a ping should be rejected based on GPS accuracy.
    /// </summary>
    private static bool ShouldRejectForAccuracy(double? accuracyMeters, ApplicationSettings settings)
    {
        if (settings.VisitedAccuracyRejectMeters == 0)
            return false; // Rejection disabled

        return accuracyMeters.HasValue && accuracyMeters.Value > settings.VisitedAccuracyRejectMeters;
    }

    /// <summary>
    /// Calculates the effective detection radius based on GPS accuracy.
    /// </summary>
    private static double CalculateEffectiveRadius(double? accuracyMeters, ApplicationSettings settings)
    {
        var baseRadius = Math.Max(
            settings.VisitedMinRadiusMeters,
            (accuracyMeters ?? 0) * settings.VisitedAccuracyMultiplier);

        return Math.Clamp(baseRadius, settings.VisitedMinRadiusMeters, settings.VisitedMaxRadiusMeters);
    }

    /// <summary>
    /// Finds the nearest place within the search radius for the given user.
    /// Uses raw SQL with PostGIS ST_DWithin for efficient spatial filtering.
    /// The spatial index on Places.Location enables fast queries even with many places.
    /// </summary>
    /// <remarks>
    /// Query strategy:
    /// 1. ST_DWithin uses the GiST spatial index to efficiently filter places within radius
    /// 2. ST_Distance calculates exact distance for ordering (only on filtered subset)
    /// 3. LIMIT 1 returns only the nearest match
    ///
    /// Performance: O(log n) with spatial index vs O(n) without
    /// Works efficiently with 500K+ places per user
    /// </remarks>
    private async Task<NearestPlaceResult?> FindNearestPlaceWithinRadiusAsync(
        string userId,
        Point location,
        double searchRadiusMeters,
        CancellationToken cancellationToken)
    {
        // Step 1: Use raw SQL with PostGIS to find the nearest place ID within radius
        // ST_DWithin leverages the GiST spatial index for efficient filtering
        // ST_SetSRID + ST_MakePoint constructs the ping location as geography
        const string sql = """
            SELECT
                p."Id" AS "PlaceId",
                ST_Distance(
                    p."Location",
                    ST_SetSRID(ST_MakePoint(@lon, @lat), 4326)::geography
                ) AS "Distance"
            FROM "Places" p
            INNER JOIN "Regions" r ON p."RegionId" = r."Id"
            INNER JOIN "Trips" t ON r."TripId" = t."Id"
            WHERE t."UserId" = @userId
              AND p."Location" IS NOT NULL
              AND ST_DWithin(
                    p."Location",
                    ST_SetSRID(ST_MakePoint(@lon, @lat), 4326)::geography,
                    @radius
                  )
            ORDER BY "Distance"
            LIMIT 1
            """;

        Guid? nearestPlaceId = null;
        double distance = 0;

        try
        {
            var connection = _dbContext.Database.GetDbConnection();
            var connectionWasOpen = connection.State == System.Data.ConnectionState.Open;

            if (!connectionWasOpen)
            {
                await _dbContext.Database.OpenConnectionAsync(cancellationToken);
            }

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.Add(new NpgsqlParameter("@userId", userId));
                command.Parameters.Add(new NpgsqlParameter("@lon", location.X));
                command.Parameters.Add(new NpgsqlParameter("@lat", location.Y));
                command.Parameters.Add(new NpgsqlParameter("@radius", searchRadiusMeters));

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    nearestPlaceId = reader.GetGuid(0);
                    distance = reader.GetDouble(1);
                }
            }
            finally
            {
                // Only close if we opened it
                if (!connectionWasOpen)
                {
                    await _dbContext.Database.CloseConnectionAsync();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Spatial query failed for user {UserId} at ({Lat}, {Lon}). Falling back to no match.",
                userId, location.Y, location.X);
            return null;
        }

        if (nearestPlaceId == null)
        {
            return null;
        }

        // Step 2: Load the full Place entity with navigation properties using EF Core
        // This is a simple primary key lookup - very fast
        var place = await _dbContext.Places
            .Include(p => p.Region)
            .ThenInclude(r => r.Trip)
            .FirstOrDefaultAsync(p => p.Id == nearestPlaceId.Value, cancellationToken);

        if (place == null)
        {
            _logger.LogWarning(
                "Place {PlaceId} found by spatial query but not found by EF Core lookup",
                nearestPlaceId);
            return null;
        }

        return new NearestPlaceResult
        {
            Place = place,
            Region = place.Region,
            Trip = place.Region.Trip,
            Distance = distance
        };
    }

    /// <summary>
    /// Finds an open (not ended) visit for the given user and place.
    /// </summary>
    private async Task<PlaceVisitEvent?> FindOpenVisitAsync(
        string userId,
        Guid placeId,
        CancellationToken cancellationToken)
    {
        return await _dbContext.PlaceVisitEvents
            .Where(e => e.UserId == userId)
            .Where(e => e.PlaceId == placeId)
            .Where(e => e.EndedAtUtc == null)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Updates an existing open visit with the latest ping timestamp.
    /// </summary>
    private async Task HandleOpenVisitAsync(
        PlaceVisitEvent visit,
        DateTime now,
        CancellationToken cancellationToken)
    {
        visit.LastSeenAtUtc = now;
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogDebug(
            "Updated open visit {VisitId} for place {PlaceName}, last seen at {LastSeen}",
            visit.Id, visit.PlaceNameSnapshot, now);
    }

    /// <summary>
    /// Handles the two-hit candidate confirmation logic.
    /// </summary>
    private async Task HandleCandidateConfirmationAsync(
        string userId,
        Place place,
        Region region,
        Trip trip,
        DateTime now,
        ApplicationSettings settings,
        CancellationToken cancellationToken)
    {
        // Find or create candidate
        var candidate = await _dbContext.PlaceVisitCandidates
            .Where(c => c.UserId == userId && c.PlaceId == place.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (candidate == null)
        {
            // First hit - create candidate
            candidate = new PlaceVisitCandidate
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PlaceId = place.Id,
                FirstHitUtc = now,
                LastHitUtc = now,
                ConsecutiveHits = 1
            };
            _dbContext.PlaceVisitCandidates.Add(candidate);
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogDebug(
                "Created visit candidate for user {UserId}, place {PlaceName} (hit 1)",
                userId, place.Name);
            return;
        }

        // Check if within hit window
        var timeSinceLastHit = (now - candidate.LastHitUtc).TotalMinutes;
        if (timeSinceLastHit > settings.VisitedHitWindowMinutes)
        {
            // Window expired - reset candidate
            candidate.FirstHitUtc = now;
            candidate.LastHitUtc = now;
            candidate.ConsecutiveHits = 1;
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogDebug(
                "Reset visit candidate for user {UserId}, place {PlaceName} (window expired after {Minutes} min)",
                userId, place.Name, timeSinceLastHit);
            return;
        }

        // Within window - increment hits
        candidate.ConsecutiveHits++;
        candidate.LastHitUtc = now;

        if (candidate.ConsecutiveHits >= settings.VisitedRequiredHits)
        {
            // Confirmed! Create visit event
            await CreateVisitEventAsync(candidate, place, region, trip, now, settings, cancellationToken);

            // Remove candidate
            _dbContext.PlaceVisitCandidates.Remove(candidate);
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogDebug(
                "Confirmed visit for user {UserId}, place {PlaceName} after {Hits} hits",
                userId, place.Name, candidate.ConsecutiveHits);
        }
        else
        {
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogDebug(
                "Updated visit candidate for user {UserId}, place {PlaceName} (hit {Hits}/{Required})",
                userId, place.Name, candidate.ConsecutiveHits, settings.VisitedRequiredHits);
        }
    }

    /// <summary>
    /// Creates a new PlaceVisitEvent with snapshot data.
    /// </summary>
    private async Task CreateVisitEventAsync(
        PlaceVisitCandidate candidate,
        Place place,
        Region region,
        Trip trip,
        DateTime now,
        ApplicationSettings settings,
        CancellationToken cancellationToken)
    {
        var visitEvent = new PlaceVisitEvent
        {
            Id = Guid.NewGuid(),
            UserId = candidate.UserId,
            PlaceId = place.Id,
            ArrivedAtUtc = candidate.FirstHitUtc,
            LastSeenAtUtc = now,
            EndedAtUtc = null,

            // Snapshots
            TripIdSnapshot = trip.Id,
            TripNameSnapshot = trip.Name,
            RegionNameSnapshot = region.Name,
            PlaceNameSnapshot = place.Name,
            PlaceLocationSnapshot = place.Location != null
                ? new Point(place.Location.X, place.Location.Y) { SRID = place.Location.SRID }
                : null,

            // Notes - truncate if needed
            NotesHtml = TruncateNotes(place.Notes, settings.VisitedPlaceNotesSnapshotMaxHtmlChars),

            // Optional UI snapshots
            IconNameSnapshot = place.IconName,
            MarkerColorSnapshot = place.MarkerColor
        };

        _dbContext.PlaceVisitEvents.Add(visitEvent);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Check notification cooldown before broadcasting SSE
        var shouldNotify = await ShouldBroadcastVisitNotificationAsync(
            candidate.UserId, place.Id, visitEvent.Id, settings, cancellationToken);

        if (shouldNotify)
        {
            var sseEvent = VisitSseEventDto.FromVisitEvent(visitEvent);
            await _sseService.BroadcastAsync(
                $"user-visits-{candidate.UserId}",
                JsonSerializer.Serialize(sseEvent));

            _logger.LogInformation(
                "Created visit event {VisitId} for user {UserId}, place {PlaceName} in trip {TripName} (notification sent)",
                visitEvent.Id, candidate.UserId, place.Name, trip.Name);
        }
        else
        {
            _logger.LogInformation(
                "Created visit event {VisitId} for user {UserId}, place {PlaceName} in trip {TripName} (notification skipped - within cooldown)",
                visitEvent.Id, candidate.UserId, place.Name, trip.Name);
        }
    }

    /// <summary>
    /// Determines if a visit notification should be broadcast based on cooldown settings.
    /// Returns false if notifications are disabled (-1).
    /// Returns true if cooldown is disabled (0) or no recent visit to the same place exists within the cooldown window.
    /// </summary>
    /// <param name="userId">The user's ID.</param>
    /// <param name="placeId">The place being visited.</param>
    /// <param name="currentVisitId">The ID of the visit just created (to exclude from the query).</param>
    /// <param name="settings">Application settings containing cooldown configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if notification should be sent, false if disabled or within cooldown period.</returns>
    private async Task<bool> ShouldBroadcastVisitNotificationAsync(
        string userId,
        Guid placeId,
        Guid currentVisitId,
        ApplicationSettings settings,
        CancellationToken cancellationToken)
    {
        // Notifications completely disabled
        if (settings.VisitNotificationCooldownHours < 0)
        {
            return false;
        }

        // Cooldown disabled - always notify
        if (settings.VisitNotificationCooldownHours == 0)
        {
            return true;
        }

        var cooldownCutoff = DateTime.UtcNow.AddHours(-settings.VisitNotificationCooldownHours);

        // Check for any previous visit to the same place within the cooldown window
        // Use LastSeenAtUtc (last activity) rather than ArrivedAtUtc to properly handle
        // long-running visits - we want to throttle based on when user was last present
        var hasRecentVisit = await _dbContext.PlaceVisitEvents
            .Where(v => v.UserId == userId)
            .Where(v => v.PlaceId == placeId)
            .Where(v => v.Id != currentVisitId) // Exclude the visit we just created
            .Where(v => v.LastSeenAtUtc >= cooldownCutoff)
            .AnyAsync(cancellationToken);

        return !hasRecentVisit;
    }

    /// <summary>
    /// Truncates notes to the maximum allowed length.
    /// </summary>
    private static string? TruncateNotes(string? notes, int maxChars)
    {
        if (string.IsNullOrEmpty(notes) || notes.Length <= maxChars)
            return notes;

        return notes[..(maxChars - 1)] + "…";
    }

    /// <summary>
    /// Closes visits that have been inactive for longer than the configured timeout.
    /// </summary>
    private async Task CloseStaleVisitsAsync(
        string userId,
        DateTime now,
        ApplicationSettings settings,
        CancellationToken cancellationToken)
    {
        var cutoff = now.AddMinutes(-settings.VisitedEndVisitAfterMinutes);

        var staleVisits = await _dbContext.PlaceVisitEvents
            .Where(e => e.UserId == userId)
            .Where(e => e.EndedAtUtc == null)
            .Where(e => e.LastSeenAtUtc < cutoff)
            .ToListAsync(cancellationToken);

        foreach (var visit in staleVisits)
        {
            visit.EndedAtUtc = visit.LastSeenAtUtc;

            _logger.LogDebug(
                "Closed stale visit {VisitId} for place {PlaceName}, ended at {EndedAt}",
                visit.Id, visit.PlaceNameSnapshot, visit.EndedAtUtc);
        }

        if (staleVisits.Count > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Removes candidate records that are older than the configured stale threshold.
    /// </summary>
    private async Task CleanupStaleCandidatesAsync(
        string userId,
        DateTime now,
        ApplicationSettings settings,
        CancellationToken cancellationToken)
    {
        var cutoff = now.AddMinutes(-settings.VisitedCandidateStaleMinutes);

        var staleCandidates = await _dbContext.PlaceVisitCandidates
            .Where(c => c.UserId == userId)
            .Where(c => c.LastHitUtc < cutoff)
            .ToListAsync(cancellationToken);

        if (staleCandidates.Count > 0)
        {
            _dbContext.PlaceVisitCandidates.RemoveRange(staleCandidates);
            await _dbContext.SaveChangesAsync(cancellationToken);

            _logger.LogDebug(
                "Cleaned up {Count} stale candidates for user {UserId}",
                staleCandidates.Count, userId);
        }
    }

    /// <summary>
    /// Result of finding the nearest place within search radius.
    /// </summary>
    private class NearestPlaceResult
    {
        public required Place Place { get; init; }
        public required Region Region { get; init; }
        public required Trip Trip { get; init; }
        public required double Distance { get; init; }
    }
}
