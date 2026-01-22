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
    /// <summary>
    /// Minimum number of places to use batched query. Below this, individual queries are fine.
    /// </summary>
    private const int BatchedQueryThreshold = 10;

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
        DateOnly? toDate,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var settings = _settingsService.GetSettings();

        // Validate trip ownership and load with places
        var trip = await _dbContext.Trips
            .Include(t => t.Regions)
            .ThenInclude(r => r.Places)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);

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
            .ToListAsync(cancellationToken);

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

        // Build region lookup for candidate mapping
        var regionsById = trip.Regions.ToDictionary(r => r.Id);

        // Analyze places - use batched query for efficiency on larger trips
        List<BackfillCandidateDto> allCandidates;
        int totalLocationsScanned;

        if (placesWithCoords.Count >= BatchedQueryThreshold)
        {
            // Use efficient batched query
            allCandidates = await FindVisitCandidatesBatchedAsync(
                userId, placesWithCoords, regionsById, settings, fromDate, toDate, cancellationToken);

            // Estimate locations scanned (batched query doesn't return this directly)
            totalLocationsScanned = allCandidates.Sum(c => c.LocationCount);
        }
        else
        {
            // Use individual queries for small trips (simpler, easier to debug)
            allCandidates = await FindVisitCandidatesIndividuallyAsync(
                userId, placesWithCoords, regionsById, settings, fromDate, toDate, cancellationToken);

            totalLocationsScanned = allCandidates.Sum(c => c.LocationCount);
        }

        // Filter out candidates that already have visits
        // Check both by PlaceId and by PlaceName (for cases where place was deleted/recreated)
        // IMPORTANT: Use UTC date from FirstSeenUtc (not VisitDate which is local) to match ArrivedAtUtc storage
        var candidates = new List<BackfillCandidateDto>();
        foreach (var candidate in allCandidates)
        {
            var candidateDateUtc = DateOnly.FromDateTime(candidate.FirstSeenUtc);
            var hasByPlaceId = existingVisitKeysByPlaceId.Contains((candidate.PlaceId, candidateDateUtc));
            var hasByName = existingVisitKeysByName.Contains((candidate.PlaceName, candidateDateUtc));

            if (!hasByPlaceId && !hasByName)
            {
                candidates.Add(candidate);
            }
        }

        // Build sets for suggestion filtering (exclude strict matches and existing visits)
        var strictMatchKeys = candidates
            .Select(c => (c.PlaceId, DateOnly.FromDateTime(c.FirstSeenUtc)))
            .ToHashSet();

        // Find visit suggestions (cross-tier evidence, "Consider Also" feature)
        var suggestions = await FindVisitSuggestionsAsync(
            userId, placesWithCoords, regionsById, settings,
            strictMatchKeys, existingVisitKeysByPlaceId,
            fromDate, toDate, cancellationToken);

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
            "Backfill preview for trip {TripId}: {Candidates} candidates, {Suggestions} suggestions, {Stale} stale, {Unchanged} unchanged in {Ms}ms",
            tripId, candidates.Count, suggestions.Count, staleVisits.Count, unchangedVisits.Count, stopwatch.ElapsedMilliseconds);

        return new BackfillPreviewDto
        {
            TripId = tripId,
            TripName = trip.Name,
            LocationsScanned = totalLocationsScanned,
            PlacesAnalyzed = placesWithCoords.Count,
            AnalysisDurationMs = stopwatch.ElapsedMilliseconds,
            NewVisits = candidates.OrderByDescending(c => c.VisitDate).ThenBy(c => c.PlaceName).ToList(),
            StaleVisits = staleVisits,
            ExistingVisits = MapToExistingVisitDtos(unchangedVisits),
            SuggestedVisits = suggestions.OrderByDescending(s => s.VisitDate).ThenBy(s => s.PlaceName).ToList()
        };
    }

    /// <inheritdoc />
    public async Task<BackfillResultDto> ApplyAsync(
        string userId,
        Guid tripId,
        BackfillApplyRequestDto request,
        CancellationToken cancellationToken = default)
    {
        // Validate trip ownership
        var trip = await _dbContext.Trips
            .Include(t => t.Regions)
            .ThenInclude(r => r.Places)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);

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
            .ToListAsync(cancellationToken);

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
        var suggestionsConfirmed = 0;
        var skipped = 0;

        // Create new visits from strict matching
        foreach (var item in request.CreateVisits)
        {
            var result = CreateVisitFromBackfill(
                item, userId, trip, placesById, settings, existingKeysByPlaceId, existingKeysByName, "backfill");

            if (result == BackfillCreateResult.Created)
            {
                created++;
            }
            else
            {
                skipped++;
            }
        }

        // Create visits from user-confirmed suggestions ("Consider Also")
        foreach (var item in request.ConfirmedSuggestions)
        {
            var result = CreateVisitFromBackfill(
                item, userId, trip, placesById, settings, existingKeysByPlaceId, existingKeysByName, "backfill-user-confirmed");

            if (result == BackfillCreateResult.Created)
            {
                suggestionsConfirmed++;
            }
            else
            {
                skipped++;
            }
        }

        // Delete selected visits
        var deleted = 0;
        if (request.DeleteVisitIds.Count > 0)
        {
            // Scope deletions to match preview scope: (PlaceId in trip's places) OR TripIdSnapshot == tripId
            // This ensures visits shown in preview can actually be deleted
            var allPlaceIds = placesById.Keys.ToHashSet();
            var visitsToDelete = await _dbContext.PlaceVisitEvents
                .Where(v => v.UserId == userId
                            && request.DeleteVisitIds.Contains(v.Id)
                            && ((v.PlaceId.HasValue && allPlaceIds.Contains(v.PlaceId.Value))
                                || v.TripIdSnapshot == tripId))
                .ToListAsync(cancellationToken);

            _dbContext.PlaceVisitEvents.RemoveRange(visitsToDelete);
            deleted = visitsToDelete.Count;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Backfill applied for trip {TripId}: {Created} created, {SuggestionsConfirmed} confirmed suggestions, {Deleted} deleted, {Skipped} skipped",
            tripId, created, suggestionsConfirmed, deleted, skipped);

        var totalCreated = created + suggestionsConfirmed;
        return new BackfillResultDto
        {
            Success = true,
            VisitsCreated = created,
            SuggestionsConfirmed = suggestionsConfirmed,
            VisitsDeleted = deleted,
            Skipped = skipped,
            Message = $"Created {totalCreated} visits ({created} matched, {suggestionsConfirmed} confirmed), deleted {deleted} stale visits."
        };
    }

    /// <inheritdoc />
    public async Task<BackfillResultDto> ClearVisitsAsync(
        string userId,
        Guid tripId,
        CancellationToken cancellationToken = default)
    {
        // Validate trip ownership
        var tripExists = await _dbContext.Trips
            .AnyAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);

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
            .ToListAsync(cancellationToken);

        var count = visits.Count;
        _dbContext.PlaceVisitEvents.RemoveRange(visits);
        await _dbContext.SaveChangesAsync(cancellationToken);

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

    /// <inheritdoc />
    public async Task<BackfillInfoDto> GetInfoAsync(
        string userId,
        Guid tripId,
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken = default)
    {
        // Validate trip ownership
        var trip = await _dbContext.Trips
            .Include(t => t.Regions)
            .ThenInclude(r => r.Places)
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId, cancellationToken);

        if (trip == null)
        {
            throw new InvalidOperationException("Trip not found or access denied.");
        }

        var allPlaces = trip.Regions.SelectMany(r => r.Places).ToList();
        var placesWithCoords = allPlaces.Count(p => p.Location != null);

        // Count locations in date range (fast count query)
        var locationQuery = _dbContext.Locations.Where(l => l.UserId == userId);

        if (fromDate.HasValue)
            locationQuery = locationQuery.Where(l => l.LocalTimestamp >= fromDate.Value.ToDateTime(TimeOnly.MinValue));
        if (toDate.HasValue)
            locationQuery = locationQuery.Where(l => l.LocalTimestamp <= toDate.Value.ToDateTime(TimeOnly.MaxValue));

        var locationCount = await locationQuery.CountAsync(cancellationToken);

        // Count existing visits for this trip
        var existingVisitCount = await _dbContext.PlaceVisitEvents
            .CountAsync(v => v.UserId == userId && v.TripIdSnapshot == tripId, cancellationToken);

        // Estimate analysis time based on batched query performance
        // Batched query: ~50ms base + ~2ms per place + ~0.01ms per 1000 locations
        var estimatedMs = 50 + (placesWithCoords * 2) + (locationCount / 100);
        var estimatedSeconds = Math.Max(1, (int)Math.Ceiling(estimatedMs / 1000.0));

        return new BackfillInfoDto
        {
            TripId = tripId,
            TripName = trip.Name,
            TotalPlaces = allPlaces.Count,
            PlacesWithCoordinates = placesWithCoords,
            EstimatedLocations = locationCount,
            EstimatedSeconds = estimatedSeconds,
            ExistingVisits = existingVisitCount
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
        DateOnly? toDate,
        CancellationToken cancellationToken = default)
    {
        if (place.Location == null)
        {
            return new PlaceAnalysisResult { LocationsScanned = 0, Candidates = new List<BackfillCandidateDto>() };
        }

        var radiusMeters = settings.VisitedMaxSearchRadiusMeters;
        var minHits = settings.VisitedRequiredHits;

        // Build dynamic SQL date filters using DATE() to filter on local date portion
        // This avoids timezone boundary issues when user selects date ranges
        var dateFilters = "";
        if (fromDate.HasValue)
            dateFilters += " AND DATE(l.\"LocalTimestamp\") >= @fromDate";
        if (toDate.HasValue)
            dateFilters += " AND DATE(l.\"LocalTimestamp\") <= @toDate";

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
                await _dbContext.Database.OpenConnectionAsync(cancellationToken);
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
                // Pass DateOnly directly - PostgreSQL DATE type comparison
                if (fromDate.HasValue)
                    command.Parameters.Add(new NpgsqlParameter("@fromDate", fromDate.Value));
                if (toDate.HasValue)
                    command.Parameters.Add(new NpgsqlParameter("@toDate", toDate.Value));

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
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
            // Log error but continue - one failed place shouldn't block the entire analysis
            _logger.LogError(ex,
                "Spatial query failed for place {PlaceId} ({PlaceName}) at ({Lat}, {Lon}). " +
                "This place will be skipped in the analysis.",
                place.Id, place.Name, place.Location.Y, place.Location.X);
        }

        return new PlaceAnalysisResult
        {
            LocationsScanned = totalLocations,
            Candidates = candidates
        };
    }

    /// <summary>
    /// Finds visit candidates for multiple places in a single batched query.
    /// Uses PostgreSQL LATERAL JOIN for efficient spatial matching.
    /// Chunks large place lists to stay within PostgreSQL's ~32,767 parameter limit.
    /// </summary>
    private async Task<List<BackfillCandidateDto>> FindVisitCandidatesBatchedAsync(
        string userId,
        List<Place> places,
        Dictionary<Guid, Region> regionsById,
        ApplicationSettings settings,
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken)
    {
        if (places.Count == 0)
            return new List<BackfillCandidateDto>();

        // PostgreSQL has ~32,767 parameter limit. Each place uses 3 params (id, lon, lat).
        // Use 10,000 places per chunk (30,000 params) to leave room for common params.
        const int maxPlacesPerChunk = 10000;

        if (places.Count > maxPlacesPerChunk)
        {
            _logger.LogInformation(
                "Chunking batched query: {TotalPlaces} places into {ChunkCount} chunks of {ChunkSize}",
                places.Count, (places.Count + maxPlacesPerChunk - 1) / maxPlacesPerChunk, maxPlacesPerChunk);

            var allCandidates = new List<BackfillCandidateDto>();
            foreach (var chunk in places.Chunk(maxPlacesPerChunk))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunkResults = await FindVisitCandidatesBatchedCoreAsync(
                    userId, chunk.ToList(), regionsById, settings, fromDate, toDate, cancellationToken);
                allCandidates.AddRange(chunkResults);
            }
            return allCandidates;
        }

        return await FindVisitCandidatesBatchedCoreAsync(
            userId, places, regionsById, settings, fromDate, toDate, cancellationToken);
    }

    /// <summary>
    /// Core implementation of batched query for a single chunk of places.
    /// </summary>
    private async Task<List<BackfillCandidateDto>> FindVisitCandidatesBatchedCoreAsync(
        string userId,
        List<Place> places,
        Dictionary<Guid, Region> regionsById,
        ApplicationSettings settings,
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken)
    {
        if (places.Count == 0)
            return new List<BackfillCandidateDto>();

        var radiusMeters = settings.VisitedMaxSearchRadiusMeters;
        var minHits = settings.VisitedRequiredHits;

        // Build VALUES clause for all places
        var placeParams = new List<NpgsqlParameter>();
        var valuesRows = new List<string>();

        for (int i = 0; i < places.Count; i++)
        {
            var place = places[i];
            if (place.Location == null) continue;

            var idParam = $"@p{i}_id";
            var lonParam = $"@p{i}_lon";
            var latParam = $"@p{i}_lat";

            valuesRows.Add($"({idParam}::uuid, {lonParam}, {latParam})");
            placeParams.Add(new NpgsqlParameter(idParam, place.Id));
            placeParams.Add(new NpgsqlParameter(lonParam, place.Location.X));
            placeParams.Add(new NpgsqlParameter(latParam, place.Location.Y));
        }

        if (valuesRows.Count == 0)
            return new List<BackfillCandidateDto>();

        // Build date filter clause
        var dateFilters = "";
        if (fromDate.HasValue)
            dateFilters += " AND DATE(l.\"LocalTimestamp\") >= @fromDate";
        if (toDate.HasValue)
            dateFilters += " AND DATE(l.\"LocalTimestamp\") <= @toDate";

        var sql = $"""
            WITH place_coords AS (
                SELECT * FROM (VALUES
                    {string.Join(",\n                ", valuesRows)}
                ) AS t(place_id, lon, lat)
            )
            SELECT
                pc.place_id,
                DATE(l."LocalTimestamp") as visit_date,
                MIN(l."LocalTimestamp") as first_seen,
                MAX(l."LocalTimestamp") as last_seen,
                COUNT(*) as location_count,
                AVG(ST_Distance(
                    l."Coordinates",
                    ST_SetSRID(ST_MakePoint(pc.lon, pc.lat), 4326)::geography
                )) as avg_distance
            FROM place_coords pc
            CROSS JOIN LATERAL (
                SELECT l."LocalTimestamp", l."Coordinates"
                FROM "Locations" l
                WHERE l."UserId" = @userId
                  AND ST_DWithin(
                        l."Coordinates",
                        ST_SetSRID(ST_MakePoint(pc.lon, pc.lat), 4326)::geography,
                        @radius
                      ){dateFilters}
            ) l
            GROUP BY pc.place_id, DATE(l."LocalTimestamp")
            HAVING COUNT(*) >= @minHits
            ORDER BY pc.place_id, visit_date DESC
            """;

        var candidates = new List<BackfillCandidateDto>();
        var placesById = places.Where(p => p.Location != null).ToDictionary(p => p.Id);

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
                command.CommandTimeout = 120; // 2 minute timeout for large queries

                // Add place parameters
                foreach (var param in placeParams)
                {
                    command.Parameters.Add(param);
                }

                // Add common parameters
                command.Parameters.Add(new NpgsqlParameter("@userId", userId));
                command.Parameters.Add(new NpgsqlParameter("@radius", radiusMeters));
                command.Parameters.Add(new NpgsqlParameter("@minHits", minHits));

                if (fromDate.HasValue)
                    command.Parameters.Add(new NpgsqlParameter("@fromDate", fromDate.Value));
                if (toDate.HasValue)
                    command.Parameters.Add(new NpgsqlParameter("@toDate", toDate.Value));

                var sw = Stopwatch.StartNew();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);

                while (await reader.ReadAsync(cancellationToken))
                {
                    var placeId = reader.GetGuid(0);
                    var visitDate = DateOnly.FromDateTime(reader.GetDateTime(1));
                    var firstSeen = DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc);
                    var lastSeen = DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc);
                    var locationCount = reader.GetInt32(4);
                    var avgDistance = reader.GetDouble(5);

                    if (!placesById.TryGetValue(placeId, out var place))
                        continue;

                    if (!regionsById.TryGetValue(place.RegionId, out var region))
                    {
                        _logger.LogWarning("Region {RegionId} not found for place {PlaceId}, skipping candidate", place.RegionId, place.Id);
                        continue;
                    }

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
                        Latitude = place.Location!.Y,
                        Longitude = place.Location!.X,
                        IconName = place.IconName,
                        MarkerColor = place.MarkerColor
                    });
                }

                sw.Stop();
                _logger.LogInformation(
                    "Batched spatial query for {PlaceCount} places completed in {ElapsedMs}ms, found {CandidateCount} candidates",
                    places.Count, sw.ElapsedMilliseconds, candidates.Count);
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
            _logger.LogError(ex,
                "Batched spatial query failed for {PlaceCount} places. Falling back to individual queries.",
                places.Count);

            // Fallback to individual queries on error
            return await FindVisitCandidatesIndividuallyAsync(
                userId, places, regionsById, settings, fromDate, toDate, cancellationToken);
        }

        return candidates;
    }

    /// <summary>
    /// Fallback method that uses individual queries per place (original N+1 pattern).
    /// Used for small trips or when batched query fails.
    /// </summary>
    private async Task<List<BackfillCandidateDto>> FindVisitCandidatesIndividuallyAsync(
        string userId,
        List<Place> places,
        Dictionary<Guid, Region> regionsById,
        ApplicationSettings settings,
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken)
    {
        var candidates = new List<BackfillCandidateDto>();

        foreach (var place in places)
        {
            if (place.Location == null) continue;

            if (!regionsById.TryGetValue(place.RegionId, out var region))
            {
                _logger.LogWarning("Region {RegionId} not found for place {PlaceId}, skipping", place.RegionId, place.Id);
                continue;
            }

            var placeResults = await FindVisitCandidatesForPlaceAsync(
                userId, place, region, settings, fromDate, toDate, cancellationToken);

            candidates.AddRange(placeResults.Candidates);
        }

        return candidates;
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
            if (range > 0)
            {
                var excess = avgDistanceMeters - settings.VisitedMinRadiusMeters;
                distancePenalty = Math.Min(20, (excess / range) * 20);
            }
            // If range is 0, no distance penalty is applied (settings are equal)
        }

        return (int)Math.Max(0, Math.Min(100, hitScore - distancePenalty));
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

    /// <summary>
    /// Result of attempting to create a visit from backfill.
    /// </summary>
    private enum BackfillCreateResult
    {
        Created,
        Skipped
    }

    /// <summary>
    /// Creates a visit from a backfill request item.
    /// Handles duplicate detection and adds to the database context.
    /// </summary>
    private BackfillCreateResult CreateVisitFromBackfill(
        BackfillCreateVisitDto item,
        string userId,
        Trip trip,
        Dictionary<Guid, Place> placesById,
        ApplicationSettings settings,
        HashSet<(Guid PlaceId, DateOnly Date)> existingKeysByPlaceId,
        HashSet<(string PlaceName, DateOnly Date)> existingKeysByName,
        string source)
    {
        // Check if place still exists
        if (!placesById.TryGetValue(item.PlaceId, out var place))
        {
            return BackfillCreateResult.Skipped;
        }

        // Check for duplicate by PlaceId or PlaceName
        // IMPORTANT: Use UTC date from FirstSeenUtc (not VisitDate which is local) to match ArrivedAtUtc storage
        var itemDateUtc = DateOnly.FromDateTime(item.FirstSeenUtc);
        var hasByPlaceId = existingKeysByPlaceId.Contains((item.PlaceId, itemDateUtc));
        var hasByName = existingKeysByName.Contains((place.Name, itemDateUtc));

        if (hasByPlaceId || hasByName)
        {
            return BackfillCreateResult.Skipped;
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
            Source = source,

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

        return BackfillCreateResult.Created;
    }

    /// <summary>
    /// Finds visit suggestions using cross-tier logic.
    /// These are potential visits that didn't meet strict matching criteria
    /// but have evidence across multiple radius tiers or user check-ins.
    /// </summary>
    private async Task<List<SuggestedVisitDto>> FindVisitSuggestionsAsync(
        string userId,
        List<Place> places,
        Dictionary<Guid, Region> regionsById,
        ApplicationSettings settings,
        HashSet<(Guid PlaceId, DateOnly Date)> strictMatchKeys,
        HashSet<(Guid PlaceId, DateOnly Date)> existingVisitKeys,
        DateOnly? fromDate,
        DateOnly? toDate,
        CancellationToken cancellationToken)
    {
        if (places.Count == 0)
            return new List<SuggestedVisitDto>();

        // Get tier radii and hit thresholds from settings
        var tier1Radius = settings.SuggestionTier1Radius;
        var tier2Radius = settings.SuggestionTier2Radius;
        var tier3Radius = settings.SuggestionTier3Radius;
        var maxRadius = settings.SuggestionMaxRadius;

        var tier1Hits = settings.SuggestionTier1Hits;
        var tier2Hits = settings.SuggestionTier2Hits;
        var tier3Hits = settings.SuggestionTier3Hits;
        var maxHits = settings.SuggestionMaxHits;

        // Build VALUES clause for all places
        var placeParams = new List<NpgsqlParameter>();
        var valuesRows = new List<string>();

        for (int i = 0; i < places.Count; i++)
        {
            var place = places[i];
            if (place.Location == null) continue;

            var idParam = $"@s{i}_id";
            var lonParam = $"@s{i}_lon";
            var latParam = $"@s{i}_lat";

            valuesRows.Add($"({idParam}::uuid, {lonParam}, {latParam})");
            placeParams.Add(new NpgsqlParameter(idParam, place.Id));
            placeParams.Add(new NpgsqlParameter(lonParam, place.Location.X));
            placeParams.Add(new NpgsqlParameter(latParam, place.Location.Y));
        }

        if (valuesRows.Count == 0)
            return new List<SuggestedVisitDto>();

        // Build date filter clause
        var dateFilters = "";
        if (fromDate.HasValue)
            dateFilters += " AND DATE(l.\"LocalTimestamp\") >= @fromDate";
        if (toDate.HasValue)
            dateFilters += " AND DATE(l.\"LocalTimestamp\") <= @toDate";

        // Cross-tier suggestion query:
        // - Searches within max suggestion radius
        // - Counts hits in each tier
        // - Checks for user-invoked check-ins
        // - Applies cross-tier logic to filter GPS noise
        var sql = $"""
            WITH place_coords AS (
                SELECT * FROM (VALUES
                    {string.Join(",\n                ", valuesRows)}
                ) AS t(place_id, lon, lat)
            ),
            location_hits AS (
                SELECT
                    pc.place_id,
                    DATE(l."LocalTimestamp") as visit_date,
                    MIN(l."LocalTimestamp") as first_seen,
                    MAX(l."LocalTimestamp") as last_seen,
                    MIN(ST_Distance(
                        l."Coordinates",
                        ST_SetSRID(ST_MakePoint(pc.lon, pc.lat), 4326)::geography
                    )) as min_distance,
                    COUNT(*) FILTER (WHERE ST_DWithin(
                        l."Coordinates",
                        ST_SetSRID(ST_MakePoint(pc.lon, pc.lat), 4326)::geography,
                        @tier1Radius
                    )) as hits_tier1,
                    COUNT(*) FILTER (WHERE ST_DWithin(
                        l."Coordinates",
                        ST_SetSRID(ST_MakePoint(pc.lon, pc.lat), 4326)::geography,
                        @tier2Radius
                    )) as hits_tier2,
                    COUNT(*) FILTER (WHERE ST_DWithin(
                        l."Coordinates",
                        ST_SetSRID(ST_MakePoint(pc.lon, pc.lat), 4326)::geography,
                        @tier3Radius
                    )) as hits_tier3,
                    COUNT(*) as hits_total,
                    COUNT(*) FILTER (WHERE l."IsUserInvoked" = true) as checkin_count
                FROM place_coords pc
                CROSS JOIN LATERAL (
                    SELECT l."LocalTimestamp", l."Coordinates", l."IsUserInvoked"
                    FROM "Locations" l
                    WHERE l."UserId" = @userId
                      AND ST_DWithin(
                            l."Coordinates",
                            ST_SetSRID(ST_MakePoint(pc.lon, pc.lat), 4326)::geography,
                            @maxRadius
                          ){dateFilters}
                ) l
                GROUP BY pc.place_id, DATE(l."LocalTimestamp")
            )
            SELECT
                place_id, visit_date, first_seen, last_seen, min_distance,
                hits_tier1, hits_tier2, hits_tier3, hits_total, checkin_count
            FROM location_hits
            WHERE (
                -- Cross-tier evidence logic
                checkin_count >= 1
                OR hits_tier1 >= @tier1Hits
                OR hits_tier2 >= @tier2Hits
                OR hits_tier3 >= @tier3Hits
                OR hits_total >= @maxHits
                OR (hits_tier1 >= 1 AND hits_tier2 >= 2)
                OR (hits_tier2 >= 1 AND hits_tier3 >= 3)
            )
            ORDER BY place_id, visit_date DESC
            """;

        var suggestions = new List<SuggestedVisitDto>();
        var placesById = places.Where(p => p.Location != null).ToDictionary(p => p.Id);

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
                command.CommandTimeout = 120; // 2 minute timeout

                // Add place parameters
                foreach (var param in placeParams)
                {
                    command.Parameters.Add(param);
                }

                // Add tier radius parameters
                command.Parameters.Add(new NpgsqlParameter("@tier1Radius", tier1Radius));
                command.Parameters.Add(new NpgsqlParameter("@tier2Radius", tier2Radius));
                command.Parameters.Add(new NpgsqlParameter("@tier3Radius", tier3Radius));
                command.Parameters.Add(new NpgsqlParameter("@maxRadius", maxRadius));

                // Add tier hit threshold parameters
                command.Parameters.Add(new NpgsqlParameter("@tier1Hits", tier1Hits));
                command.Parameters.Add(new NpgsqlParameter("@tier2Hits", tier2Hits));
                command.Parameters.Add(new NpgsqlParameter("@tier3Hits", tier3Hits));
                command.Parameters.Add(new NpgsqlParameter("@maxHits", maxHits));

                // Add common parameters
                command.Parameters.Add(new NpgsqlParameter("@userId", userId));

                if (fromDate.HasValue)
                    command.Parameters.Add(new NpgsqlParameter("@fromDate", fromDate.Value));
                if (toDate.HasValue)
                    command.Parameters.Add(new NpgsqlParameter("@toDate", toDate.Value));

                var sw = Stopwatch.StartNew();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);

                while (await reader.ReadAsync(cancellationToken))
                {
                    var placeId = reader.GetGuid(0);
                    var visitDate = DateOnly.FromDateTime(reader.GetDateTime(1));
                    var firstSeen = DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc);
                    var lastSeen = DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc);
                    var minDistance = reader.GetDouble(4);
                    var hitsTier1 = reader.GetInt32(5);
                    var hitsTier2 = reader.GetInt32(6);
                    var hitsTier3 = reader.GetInt32(7);
                    var hitsTotal = reader.GetInt32(8);
                    var checkinCount = reader.GetInt32(9);

                    // Skip if this place+date combo already has a strict match or existing visit
                    var visitDateUtc = DateOnly.FromDateTime(firstSeen);
                    if (strictMatchKeys.Contains((placeId, visitDateUtc)) ||
                        existingVisitKeys.Contains((placeId, visitDateUtc)))
                    {
                        continue;
                    }

                    if (!placesById.TryGetValue(placeId, out var place))
                        continue;

                    if (!regionsById.TryGetValue(place.RegionId, out var region))
                    {
                        _logger.LogWarning("Region {RegionId} not found for place {PlaceId}, skipping suggestion", place.RegionId, place.Id);
                        continue;
                    }

                    var hasUserCheckin = checkinCount >= 1;

                    // Generate suggestion reason
                    var reason = GenerateSuggestionReason(
                        hasUserCheckin, hitsTier1, hitsTier2, hitsTier3, hitsTotal,
                        tier1Radius, tier2Radius, tier3Radius);

                    suggestions.Add(new SuggestedVisitDto
                    {
                        PlaceId = place.Id,
                        PlaceName = place.Name,
                        RegionName = region.Name,
                        VisitDate = visitDate,
                        MinDistanceMeters = Math.Round(minDistance, 1),
                        HitsTier1 = hitsTier1,
                        HitsTier2 = hitsTier2,
                        HitsTier3 = hitsTier3,
                        HitsTotal = hitsTotal,
                        HasUserCheckin = hasUserCheckin,
                        SuggestionReason = reason,
                        FirstSeenUtc = firstSeen,
                        LastSeenUtc = lastSeen,
                        Latitude = place.Location!.Y,
                        Longitude = place.Location!.X,
                        IconName = place.IconName,
                        MarkerColor = place.MarkerColor
                    });
                }

                sw.Stop();
                _logger.LogInformation(
                    "Suggestion query for {PlaceCount} places completed in {ElapsedMs}ms, found {SuggestionCount} suggestions",
                    places.Count, sw.ElapsedMilliseconds, suggestions.Count);
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
            _logger.LogError(ex,
                "Suggestion query failed for {PlaceCount} places. Suggestions will be empty.",
                places.Count);
            return new List<SuggestedVisitDto>();
        }

        return suggestions;
    }

    /// <summary>
    /// Generates a human-readable reason for why a place is being suggested.
    /// </summary>
    private static string GenerateSuggestionReason(
        bool hasUserCheckin,
        int hitsTier1,
        int hitsTier2,
        int hitsTier3,
        int hitsTotal,
        int tier1Radius,
        int tier2Radius,
        int tier3Radius)
    {
        if (hasUserCheckin)
        {
            return "User checked in nearby";
        }

        if (hitsTier1 >= 1 && hitsTier2 >= 2)
        {
            return $"Cross-tier: {hitsTier1} within {tier1Radius}m + {hitsTier2} within {tier2Radius}m";
        }

        if (hitsTier2 >= 1 && hitsTier3 >= 3)
        {
            return $"Cross-tier: {hitsTier2} within {tier2Radius}m + {hitsTier3} within {tier3Radius}m";
        }

        if (hitsTier1 >= 1)
        {
            return $"{hitsTier1} ping{(hitsTier1 > 1 ? "s" : "")} within {tier1Radius}m";
        }

        if (hitsTier2 >= 2)
        {
            return $"{hitsTier2} pings within {tier2Radius}m";
        }

        if (hitsTier3 >= 3)
        {
            return $"{hitsTier3} pings within {tier3Radius}m";
        }

        return $"{hitsTotal} pings within extended range";
    }
}
