using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;

namespace Wayfarer.Services;

/// <summary>
/// Service for backfilling visit records by analyzing location history against trip places.
/// Uses PostGIS spatial queries for efficient location-to-place matching.
/// </summary>
public class VisitBackfillService : IVisitBackfillService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IApplicationSettingsService _settingsService;
    private readonly ILogger<VisitBackfillService> _logger;

    public VisitBackfillService(
        ApplicationDbContext dbContext,
        IApplicationSettingsService settingsService,
        ILogger<VisitBackfillService> logger)
    {
        _dbContext = dbContext;
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<BackfillPreviewDto> PreviewAsync(
        string userId,
        Guid tripId,
        DateOnly? fromDate,
        DateOnly? toDate)
    {
        var stopwatch = Stopwatch.StartNew();
        var settings = _settingsService.GetSettings();

        // Validate trip ownership and load with places
        var trip = await _dbContext.Trips
            .Include(t => t.Regions)
            .ThenInclude(r => r.Places)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId);

        if (trip == null)
        {
            throw new InvalidOperationException("Trip not found or access denied.");
        }

        // Get all places from this trip
        var allPlaces = trip.Regions
            .SelectMany(r => r.Places)
            .ToList();

        // Get places with coordinates (for spatial analysis)
        var placesWithCoords = allPlaces
            .Where(p => p.Location != null)
            .ToList();

        // Get all place IDs from this trip
        var allPlaceIds = allPlaces.Select(p => p.Id).ToHashSet();

        // Get existing visits for this trip - query by PlaceId OR TripIdSnapshot
        // PlaceId: matches visits to places currently in the trip
        // TripIdSnapshot: matches visits created for this trip (even if place was recreated with new ID)
        var existingVisits = await _dbContext.PlaceVisitEvents
            .Where(v => v.UserId == userId)
            .Where(v => (v.PlaceId.HasValue && allPlaceIds.Contains(v.PlaceId.Value))
                        || v.TripIdSnapshot == tripId)
            .ToListAsync();

        _logger.LogInformation(
            "Backfill analysis for trip {TripId} ({TripName}): {PlaceCount} places, {PlacesWithCoords} with coords, {ExistingVisits} existing visits",
            tripId, trip.Name, allPlaces.Count, placesWithCoords.Count, existingVisits.Count);

        if (placesWithCoords.Count == 0)
        {
            // No places with coordinates - skip candidate analysis but still detect stale visits
            var staleVisitsNoCoords = FindStaleVisitsFromList(existingVisits, allPlaces, settings);
            var staleVisitIdsNoCoords = staleVisitsNoCoords.Select(s => s.VisitId).ToHashSet();
            var unchangedVisitsNoCoords = existingVisits
                .Where(v => !staleVisitIdsNoCoords.Contains(v.Id))
                .ToList();

            stopwatch.Stop();
            return new BackfillPreviewDto
            {
                TripId = tripId,
                TripName = trip.Name,
                LocationsScanned = 0,
                PlacesAnalyzed = 0,
                AnalysisDurationMs = stopwatch.ElapsedMilliseconds,
                NewVisits = new List<BackfillCandidateDto>(),
                StaleVisits = staleVisitsNoCoords,
                ExistingVisits = MapToExistingVisitDtos(unchangedVisitsNoCoords)
            };
        }

        // Build sets for duplicate detection:
        // 1. By (PlaceId, Date) - for visits with valid PlaceId
        // 2. By (PlaceNameSnapshot, Date) - ONLY for visits where place was deleted (PlaceId = null)
        //    This prevents false positives when two different places have the same name
        var existingVisitKeysByPlaceId = existingVisits
            .Where(v => v.PlaceId.HasValue)
            .Select(v => (PlaceId: v.PlaceId!.Value, Date: DateOnly.FromDateTime(v.ArrivedAtUtc)))
            .ToHashSet();

        var existingVisitKeysByName = existingVisits
            .Where(v => !v.PlaceId.HasValue) // Only for deleted places (PlaceId = null)
            .Select(v => (PlaceName: v.PlaceNameSnapshot, Date: DateOnly.FromDateTime(v.ArrivedAtUtc)))
            .ToHashSet();

        // Analyze each place
        var candidates = new List<BackfillCandidateDto>();
        var totalLocationsScanned = 0;

        foreach (var place in placesWithCoords)
        {
            var region = trip.Regions.First(r => r.Id == place.RegionId);
            var placeResults = await FindVisitCandidatesForPlaceAsync(
                userId, place, region, settings, fromDate, toDate);

            totalLocationsScanned += placeResults.LocationsScanned;

            // Filter out candidates that already have visits
            // Check both by PlaceId and by PlaceName (for cases where place was deleted/recreated)
            // IMPORTANT: Use UTC date from FirstSeenUtc (not VisitDate which is local) to match ArrivedAtUtc storage
            foreach (var candidate in placeResults.Candidates)
            {
                var candidateDateUtc = DateOnly.FromDateTime(candidate.FirstSeenUtc);
                var hasByPlaceId = existingVisitKeysByPlaceId.Contains((place.Id, candidateDateUtc));
                var hasByName = existingVisitKeysByName.Contains((place.Name, candidateDateUtc));

                if (!hasByPlaceId && !hasByName)
                {
                    candidates.Add(candidate);
                }
            }
        }

        // Find stale visits (place deleted or moved)
        // Use allPlaces so visits to places without coordinates aren't incorrectly marked as stale
        var staleVisits = FindStaleVisitsFromList(existingVisits, allPlaces, settings);

        // Get unchanged visits (existing visits that aren't stale)
        var staleVisitIds = staleVisits.Select(s => s.VisitId).ToHashSet();
        var unchangedVisits = existingVisits
            .Where(v => !staleVisitIds.Contains(v.Id))
            .ToList();

        stopwatch.Stop();

        _logger.LogInformation(
            "Backfill preview for trip {TripId}: {Candidates} candidates, {Stale} stale, {Unchanged} unchanged in {Ms}ms",
            tripId, candidates.Count, staleVisits.Count, unchangedVisits.Count, stopwatch.ElapsedMilliseconds);

        return new BackfillPreviewDto
        {
            TripId = tripId,
            TripName = trip.Name,
            LocationsScanned = totalLocationsScanned,
            PlacesAnalyzed = placesWithCoords.Count,
            AnalysisDurationMs = stopwatch.ElapsedMilliseconds,
            NewVisits = candidates.OrderByDescending(c => c.VisitDate).ThenBy(c => c.PlaceName).ToList(),
            StaleVisits = staleVisits,
            ExistingVisits = MapToExistingVisitDtos(unchangedVisits)
        };
    }

    /// <inheritdoc />
    public async Task<BackfillResultDto> ApplyAsync(
        string userId,
        Guid tripId,
        BackfillApplyRequestDto request)
    {
        // Validate trip ownership
        var trip = await _dbContext.Trips
            .Include(t => t.Regions)
            .ThenInclude(r => r.Places)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId);

        if (trip == null)
        {
            return new BackfillResultDto
            {
                Success = false,
                Message = "Trip not found or access denied."
            };
        }

        var settings = _settingsService.GetSettings();
        var placesById = trip.Regions
            .SelectMany(r => r.Places)
            .ToDictionary(p => p.Id);

        // Get existing visits to prevent duplicates - fetch PlaceNameSnapshot for name-based check
        var existingVisitRecords = await _dbContext.PlaceVisitEvents
            .Where(v => v.UserId == userId && v.TripIdSnapshot == tripId)
            .Select(v => new { v.PlaceId, v.PlaceNameSnapshot, Date = v.ArrivedAtUtc.Date })
            .ToListAsync();

        // Build sets for duplicate detection (by PlaceId and by PlaceNameSnapshot)
        var existingKeysByPlaceId = existingVisitRecords
            .Where(v => v.PlaceId.HasValue)
            .Select(v => (PlaceId: v.PlaceId!.Value, Date: DateOnly.FromDateTime(v.Date)))
            .ToHashSet();

        // Only use name-based dedupe for visits where PlaceId is null (deleted places)
        // This prevents false positives when two different places have the same name
        var existingKeysByName = existingVisitRecords
            .Where(v => !v.PlaceId.HasValue)
            .Select(v => (PlaceName: v.PlaceNameSnapshot, Date: DateOnly.FromDateTime(v.Date)))
            .ToHashSet();

        var created = 0;
        var skipped = 0;

        // Create new visits
        foreach (var item in request.CreateVisits)
        {
            // Check if place still exists
            if (!placesById.TryGetValue(item.PlaceId, out var place))
            {
                skipped++;
                continue;
            }

            // Check for duplicate by PlaceId or PlaceName
            // IMPORTANT: Use UTC date from FirstSeenUtc (not VisitDate which is local) to match ArrivedAtUtc storage
            var itemDateUtc = DateOnly.FromDateTime(item.FirstSeenUtc);
            var hasByPlaceId = existingKeysByPlaceId.Contains((item.PlaceId, itemDateUtc));
            var hasByName = existingKeysByName.Contains((place.Name, itemDateUtc));

            if (hasByPlaceId || hasByName)
            {
                skipped++;
                continue;
            }

            var region = trip.Regions.First(r => r.Id == place.RegionId);

            var visitEvent = new PlaceVisitEvent
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PlaceId = place.Id,
                ArrivedAtUtc = item.FirstSeenUtc,
                LastSeenAtUtc = item.LastSeenUtc,
                EndedAtUtc = item.LastSeenUtc, // Backfilled visits are closed

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
            // Add to PlaceId set to prevent duplicates in same batch (use UTC date)
            // Note: Don't add to existingKeysByName - that's only for visits with null PlaceId
            existingKeysByPlaceId.Add((item.PlaceId, itemDateUtc));
            created++;
        }

        // Delete stale visits
        var deleted = 0;
        if (request.DeleteVisitIds.Count > 0)
        {
            // Scope deletions to this trip to prevent accidental deletion of visits from other trips
            var visitsToDelete = await _dbContext.PlaceVisitEvents
                .Where(v => v.UserId == userId
                            && v.TripIdSnapshot == tripId
                            && request.DeleteVisitIds.Contains(v.Id))
                .ToListAsync();

            _dbContext.PlaceVisitEvents.RemoveRange(visitsToDelete);
            deleted = visitsToDelete.Count;
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Backfill applied for trip {TripId}: {Created} created, {Deleted} deleted, {Skipped} skipped",
            tripId, created, deleted, skipped);

        return new BackfillResultDto
        {
            Success = true,
            VisitsCreated = created,
            VisitsDeleted = deleted,
            Skipped = skipped,
            Message = $"Created {created} visits, deleted {deleted} stale visits."
        };
    }

    /// <inheritdoc />
    public async Task<BackfillResultDto> ClearVisitsAsync(string userId, Guid tripId)
    {
        // Validate trip ownership
        var tripExists = await _dbContext.Trips
            .AnyAsync(t => t.Id == tripId && t.UserId == userId);

        if (!tripExists)
        {
            return new BackfillResultDto
            {
                Success = false,
                Message = "Trip not found or access denied."
            };
        }

        var visits = await _dbContext.PlaceVisitEvents
            .Where(v => v.UserId == userId && v.TripIdSnapshot == tripId)
            .ToListAsync();

        var count = visits.Count;
        _dbContext.PlaceVisitEvents.RemoveRange(visits);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Cleared {Count} visits for trip {TripId} by user {UserId}",
            count, tripId, userId);

        return new BackfillResultDto
        {
            Success = true,
            VisitsDeleted = count,
            Message = $"Deleted {count} visits."
        };
    }

    /// <summary>
    /// Finds visit candidates for a specific place by querying location history.
    /// Uses PostGIS ST_DWithin for efficient spatial matching.
    /// </summary>
    private async Task<PlaceAnalysisResult> FindVisitCandidatesForPlaceAsync(
        string userId,
        Place place,
        Region region,
        ApplicationSettings settings,
        DateOnly? fromDate,
        DateOnly? toDate)
    {
        if (place.Location == null)
        {
            return new PlaceAnalysisResult { LocationsScanned = 0, Candidates = new List<BackfillCandidateDto>() };
        }

        var radiusMeters = settings.VisitedMaxSearchRadiusMeters;
        var minHits = settings.VisitedRequiredHits;

        // Build date filters
        DateTime? fromUtc = fromDate.HasValue
            ? new DateTime(fromDate.Value.Year, fromDate.Value.Month, fromDate.Value.Day, 0, 0, 0, DateTimeKind.Utc)
            : null;
        DateTime? toUtc = toDate.HasValue
            ? new DateTime(toDate.Value.Year, toDate.Value.Month, toDate.Value.Day, 23, 59, 59, DateTimeKind.Utc)
            : null;

        // Build dynamic SQL to avoid nullable parameter type inference issues
        var dateFilters = "";
        if (fromUtc.HasValue)
            dateFilters += " AND l.\"LocalTimestamp\" >= @fromDate";
        if (toUtc.HasValue)
            dateFilters += " AND l.\"LocalTimestamp\" <= @toDate";

        // PostGIS query to find location matches grouped by date
        // Use LocalTimestamp consistently for visit times (not Timestamp which is server ingestion time)
        var sql = $"""
            SELECT
                DATE(l."LocalTimestamp") as visit_date,
                MIN(l."LocalTimestamp") as first_seen,
                MAX(l."LocalTimestamp") as last_seen,
                COUNT(*) as location_count,
                AVG(ST_Distance(
                    l."Coordinates",
                    ST_SetSRID(ST_MakePoint(@lon, @lat), 4326)::geography
                )) as avg_distance
            FROM "Locations" l
            WHERE l."UserId" = @userId
              AND ST_DWithin(
                    l."Coordinates",
                    ST_SetSRID(ST_MakePoint(@lon, @lat), 4326)::geography,
                    @radius
                  ){dateFilters}
            GROUP BY DATE(l."LocalTimestamp")
            HAVING COUNT(*) >= @minHits
            ORDER BY visit_date DESC
            """;

        var candidates = new List<BackfillCandidateDto>();
        var totalLocations = 0;

        try
        {
            var connection = _dbContext.Database.GetDbConnection();
            var connectionWasOpen = connection.State == System.Data.ConnectionState.Open;

            if (!connectionWasOpen)
            {
                await _dbContext.Database.OpenConnectionAsync();
            }

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.Add(new NpgsqlParameter("@userId", userId));
                command.Parameters.Add(new NpgsqlParameter("@lon", place.Location.X));
                command.Parameters.Add(new NpgsqlParameter("@lat", place.Location.Y));
                command.Parameters.Add(new NpgsqlParameter("@radius", radiusMeters));
                command.Parameters.Add(new NpgsqlParameter("@minHits", minHits));
                if (fromUtc.HasValue)
                    command.Parameters.Add(new NpgsqlParameter("@fromDate", fromUtc.Value));
                if (toUtc.HasValue)
                    command.Parameters.Add(new NpgsqlParameter("@toDate", toUtc.Value));

                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var visitDate = DateOnly.FromDateTime(reader.GetDateTime(0));
                    // Note: LocalTimestamp is user's local time, not UTC. We store it as UTC for
                    // consistency with ArrivedAtUtc field, but it represents the actual event time.
                    var firstSeen = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
                    var lastSeen = DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc);
                    var locationCount = reader.GetInt32(3);
                    var avgDistance = reader.GetDouble(4);

                    totalLocations += locationCount;

                    // Calculate confidence score (0-100)
                    var confidence = CalculateConfidence(locationCount, avgDistance, settings);

                    candidates.Add(new BackfillCandidateDto
                    {
                        PlaceId = place.Id,
                        PlaceName = place.Name,
                        RegionName = region.Name,
                        VisitDate = visitDate,
                        FirstSeenUtc = firstSeen,
                        LastSeenUtc = lastSeen,
                        LocationCount = locationCount,
                        AvgDistanceMeters = Math.Round(avgDistance, 1),
                        Confidence = confidence,
                        Latitude = place.Location.Y,
                        Longitude = place.Location.X,
                        IconName = place.IconName,
                        MarkerColor = place.MarkerColor
                    });
                }
            }
            finally
            {
                if (!connectionWasOpen)
                {
                    await _dbContext.Database.CloseConnectionAsync();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Spatial query failed for place {PlaceId} at ({Lat}, {Lon})",
                place.Id, place.Location.Y, place.Location.X);
        }

        return new PlaceAnalysisResult
        {
            LocationsScanned = totalLocations,
            Candidates = candidates
        };
    }

    /// <summary>
    /// Calculates a confidence score (0-100) based on hit count and average distance.
    /// Higher hits and lower distance = higher confidence.
    /// </summary>
    private static int CalculateConfidence(int locationCount, double avgDistanceMeters, ApplicationSettings settings)
    {
        // Hit score: 2 hits = 50%, scales up to ~95% at 10+ hits
        var hitScore = Math.Min(95, 40 + (locationCount * 5.5));

        // Distance penalty: closer is better
        // At minRadius (e.g., 35m) = no penalty
        // At maxSearchRadius (e.g., 150m) = 20% penalty
        var distancePenalty = 0.0;
        if (avgDistanceMeters > settings.VisitedMinRadiusMeters)
        {
            var range = settings.VisitedMaxSearchRadiusMeters - settings.VisitedMinRadiusMeters;
            var excess = avgDistanceMeters - settings.VisitedMinRadiusMeters;
            distancePenalty = Math.Min(20, (excess / range) * 20);
        }

        return (int)Math.Max(0, Math.Min(100, hitScore - distancePenalty));
    }

    /// <summary>
    /// Finds stale visits for a trip. A visit is stale if:
    /// - The place was deleted (PlaceId is null)
    /// - The place was moved beyond the search radius
    /// </summary>
    private async Task<List<StaleVisitDto>> FindStaleVisitsAsync(
        string userId,
        Guid tripId,
        ApplicationSettings settings)
    {
        var visits = await _dbContext.PlaceVisitEvents
            .Where(v => v.UserId == userId && v.TripIdSnapshot == tripId)
            .ToListAsync();

        // Get current place locations
        var placeIds = visits
            .Where(v => v.PlaceId.HasValue)
            .Select(v => v.PlaceId!.Value)
            .Distinct()
            .ToList();

        var places = await _dbContext.Places
            .Where(p => placeIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);

        var staleVisits = new List<StaleVisitDto>();

        foreach (var visit in visits)
        {
            string? reason = null;
            double? distanceMeters = null;

            if (!visit.PlaceId.HasValue)
            {
                // Place was deleted
                reason = "Place was deleted";
            }
            else if (!places.TryGetValue(visit.PlaceId.Value, out var place))
            {
                // Place no longer exists
                reason = "Place no longer exists";
            }
            else if (visit.PlaceLocationSnapshot != null && place.Location != null)
            {
                // Check if place has moved significantly
                var distance = CalculateHaversineDistance(
                    visit.PlaceLocationSnapshot.Y, visit.PlaceLocationSnapshot.X,
                    place.Location.Y, place.Location.X);

                if (distance > settings.VisitedMaxSearchRadiusMeters)
                {
                    reason = "Place was moved";
                    distanceMeters = Math.Round(distance, 1);
                }
            }

            if (reason != null)
            {
                staleVisits.Add(new StaleVisitDto
                {
                    VisitId = visit.Id,
                    PlaceId = visit.PlaceId,
                    PlaceName = visit.PlaceNameSnapshot,
                    RegionName = visit.RegionNameSnapshot,
                    VisitDate = DateOnly.FromDateTime(visit.ArrivedAtUtc),
                    Reason = reason,
                    DistanceMeters = distanceMeters
                });
            }
        }

        return staleVisits;
    }

    /// <summary>
    /// Calculates distance between two coordinates using the Haversine formula.
    /// </summary>
    private static double CalculateHaversineDistance(double lat1, double lon1, double lat2, double lon2)
    {
        const double EarthRadiusMeters = 6371000;

        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return EarthRadiusMeters * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;

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
    /// Finds stale visits from an already-fetched list.
    /// A visit is stale if the place was deleted or moved beyond the search radius.
    /// </summary>
    private List<StaleVisitDto> FindStaleVisitsFromList(
        List<PlaceVisitEvent> visits,
        List<Place> currentPlaces,
        ApplicationSettings settings)
    {
        var placesById = currentPlaces.ToDictionary(p => p.Id);
        var staleVisits = new List<StaleVisitDto>();

        foreach (var visit in visits)
        {
            string? reason = null;
            double? distanceMeters = null;

            if (!visit.PlaceId.HasValue)
            {
                // Place was deleted (PlaceId set to null)
                reason = "Place was deleted";
            }
            else if (!placesById.TryGetValue(visit.PlaceId.Value, out var place))
            {
                // Place no longer exists in trip (might have been moved to different trip or deleted)
                reason = "Place no longer exists";
            }
            else if (visit.PlaceLocationSnapshot != null && place.Location != null)
            {
                // Check if place has moved significantly
                var distance = CalculateHaversineDistance(
                    visit.PlaceLocationSnapshot.Y, visit.PlaceLocationSnapshot.X,
                    place.Location.Y, place.Location.X);

                if (distance > settings.VisitedMaxSearchRadiusMeters)
                {
                    reason = "Place was moved";
                    distanceMeters = Math.Round(distance, 1);
                }
            }

            if (reason != null)
            {
                staleVisits.Add(new StaleVisitDto
                {
                    VisitId = visit.Id,
                    PlaceId = visit.PlaceId,
                    PlaceName = visit.PlaceNameSnapshot,
                    RegionName = visit.RegionNameSnapshot,
                    VisitDate = DateOnly.FromDateTime(visit.ArrivedAtUtc),
                    Reason = reason,
                    DistanceMeters = distanceMeters
                });
            }
        }

        return staleVisits;
    }

    /// <summary>
    /// Maps PlaceVisitEvent entities to ExistingVisitDto objects.
    /// </summary>
    private static List<ExistingVisitDto> MapToExistingVisitDtos(List<PlaceVisitEvent> visits)
    {
        return visits
            .OrderByDescending(v => v.ArrivedAtUtc)
            .Select(v => new ExistingVisitDto
            {
                VisitId = v.Id,
                PlaceId = v.PlaceId,
                PlaceName = v.PlaceNameSnapshot,
                RegionName = v.RegionNameSnapshot,
                VisitDate = DateOnly.FromDateTime(v.ArrivedAtUtc),
                ArrivedAtUtc = v.ArrivedAtUtc,
                IsOpen = v.EndedAtUtc == null
            })
            .ToList();
    }

    /// <summary>
    /// Result of analyzing a single place.
    /// </summary>
    private class PlaceAnalysisResult
    {
        public int LocationsScanned { get; init; }
        public List<BackfillCandidateDto> Candidates { get; init; } = new();
    }
}
