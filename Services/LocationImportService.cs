using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Parsers;

namespace Wayfarer.Parsers
{
    public interface ILocationImportService
    {
        Task ProcessImport(int importId, CancellationToken cancellationToken);
    }

    public class LocationImportService : ILocationImportService
    {
        private readonly ApplicationDbContext _context;
        private readonly ReverseGeocodingService _reverseGeocodingService;
        private readonly ILogger<LocationImportService> _logger;
        private readonly LocationDataParserFactory _parserFactory;
        private readonly SseService _sse;

        public LocationImportService(
            ApplicationDbContext context,
            ReverseGeocodingService reverseGeocodingService,
            ILogger<LocationImportService> logger,
            LocationDataParserFactory parserFactory,
            SseService sse)
        {
            _context = context;
            _reverseGeocodingService = reverseGeocodingService;
            _logger = logger;
            _parserFactory = parserFactory;
            _sse = sse;
        }

        public async Task ProcessImport(int importId, CancellationToken cancellationToken)
        {
            // 0) Load the import record
            var locationImport = await _context.LocationImports.FindAsync(importId);
            if (locationImport == null || locationImport.Status != ImportStatus.InProgress)
                return;

            // 1) Grab Mapbox key (if any) and the fileType
            var apiToken = await _context.ApiTokens
                .Where(t => t.UserId == locationImport.UserId
                            && t.Name.ToLower() == "mapbox")
                .Select(t => t.Token)
                .FirstOrDefaultAsync(cancellationToken);

            bool hasApiKey = !string.IsNullOrWhiteSpace(apiToken);
            var fileType  = locationImport.FileType;

            if (!hasApiKey)
            {
                _logger.LogWarning(
                    "User {UserId} has no Mapbox key—any missing addresses will remain blank for import {ImportId}.",
                    locationImport.UserId, importId);
            }

            try
            {
                var allLocations = await GetLocationsToProcess(locationImport, cancellationToken);
                int total      = allLocations.Count;
                int processed  = locationImport.LastProcessedIndex;
                locationImport.TotalRecords = total;
                const int batchSize = 50;

                while (processed < total)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Refresh status in case user clicked "stop"
                    locationImport = await _context.LocationImports.FindAsync(importId);
                    if (locationImport == null)
                    {
                        _logger.LogWarning("Import {ImportId} record disappeared during processing.", importId);
                        return;
                    }

                    if (locationImport.Status == ImportStatus.Stopping)
                    {
                        locationImport.Status = ImportStatus.Stopped;
                        await _context.SaveChangesAsync(cancellationToken);

                        await _sse.BroadcastAsync(
                            $"import-{locationImport.UserId}",
                            JsonSerializer.Serialize(new {
                                FilePath           = Path.GetFileName(locationImport.FilePath ?? string.Empty),
                                LastImportedRecord = locationImport.LastImportedRecord,
                                LastProcessedIndex = locationImport.LastProcessedIndex,
                                TotalRecords     = locationImport.TotalRecords,
                                SkippedDuplicates = locationImport.SkippedDuplicates,
                                Status             = ImportStatus.Stopped,
                                ErrorMessage = locationImport.ErrorMessage,
                            })
                        );

                        _logger.LogInformation(
                            "Import {ImportId} cancelled by user after {Processed} records.",
                            importId, processed);
                        return;
                    }

                    // Pull the next chunk
                    var batch = allLocations
                        .Skip(processed)
                        .Take(batchSize)
                        .ToList();

                    // Set Source field on locations that don't already have one (preserve from file if present)
                    foreach (var loc in batch)
                    {
                        loc.Source ??= "queue-import";
                    }

                    // 2) Filter duplicates BEFORE geocoding to avoid wasting API calls on duplicates
                    var (toInsert, skippedInBatch) = await FilterDuplicatesAsync(
                        batch,
                        locationImport.UserId,
                        cancellationToken);

                    locationImport.SkippedDuplicates += skippedInBatch;

                    // 3) Reverse‑geocode only non-duplicates that need it
                    if (hasApiKey && toInsert.Count > 0)
                    {
                        foreach (var loc in toInsert)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            // Only geocode points that lack an address
                            if (string.IsNullOrWhiteSpace(loc.FullAddress))
                            {
                                var rev = await _reverseGeocodingService
                                    .GetReverseGeocodingDataAsync(
                                        loc.Coordinates.Y,
                                        loc.Coordinates.X,
                                        apiToken!);

                                loc.FullAddress   = rev.FullAddress;
                                loc.Place         = rev.Place;
                                loc.AddressNumber = rev.AddressNumber;
                                loc.StreetName    = rev.StreetName;
                                loc.PostCode      = rev.PostCode;
                                loc.Region        = rev.Region;
                                loc.Country       = rev.Country;

                                await Task.Delay(200, cancellationToken);
                            }
                        }
                    }

                    // Insert only non-duplicates
                    if (toInsert.Count > 0)
                    {
                        await InsertLocationsToDb(toInsert, cancellationToken);
                    }

                    // 4) Update progress & SSE
                    processed += batch.Count;

                    var latest = batch.OrderByDescending(l => l.Timestamp).FirstOrDefault();
                    if (latest != null)
                    {
                        locationImport.LastImportedRecord =
                            $"Timestamp: {latest.Timestamp:u}"
                          + (!string.IsNullOrWhiteSpace(latest.FullAddress)
                              ? $", {latest.FullAddress}"
                              : "");
                    }
                    else
                    {
                        locationImport.LastImportedRecord = "N/A";
                    }

                    locationImport.LastProcessedIndex = processed;
                    await _context.SaveChangesAsync(cancellationToken);

                    await _sse.BroadcastAsync(
                        $"import-{locationImport?.UserId}",
                        JsonSerializer.Serialize(new {
                            FilePath             = Path.GetFileName(locationImport?.FilePath ?? string.Empty),
                            LastImportedRecord   = locationImport?.LastImportedRecord,
                            LastProcessedIndex   = locationImport?.LastProcessedIndex,
                            TotalRecords     = locationImport?.TotalRecords ?? 0,
                            SkippedDuplicates = locationImport?.SkippedDuplicates ?? 0,
                            Status = ImportStatus.InProgress,
                            ErrorMessage = locationImport?.ErrorMessage,
                        })
                    );

                    // brief pause between batches
                    await Task.Delay(1_000, cancellationToken);
                }

                // 5) All done
                if (locationImport != null)
                {
                    locationImport.Status = ImportStatus.Completed;
                    await _context.SaveChangesAsync(cancellationToken);
                    await _sse.BroadcastAsync(
                        $"import-{locationImport.UserId}",
                        JsonSerializer.Serialize(new {
                            FilePath             = Path.GetFileName(locationImport.FilePath ?? string.Empty),
                            LastImportedRecord   = locationImport.LastImportedRecord,
                            LastProcessedIndex   = locationImport.LastProcessedIndex,
                            TotalRecords         = locationImport.TotalRecords,
                            SkippedDuplicates    = locationImport.SkippedDuplicates,
                            Status = ImportStatus.Completed,
                            ErrorMessage = locationImport.ErrorMessage,
                        })
                    );
                    _logger.LogInformation(
                        "Import {ImportId} completed successfully: {Total} records processed, {Skipped} duplicates skipped.",
                        importId, total, locationImport.SkippedDuplicates);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Import {ImportId} was cancelled mid-process.", importId);
                var li = await _context.LocationImports.FindAsync(importId);
                if (li != null)
                {
                    li.Status = ImportStatus.Stopped;
                    await _context.SaveChangesAsync(CancellationToken.None);
                    await _sse.BroadcastAsync(
                        $"import-{locationImport?.UserId}",
                        JsonSerializer.Serialize(new {
                            FilePath             = Path.GetFileName(locationImport?.FilePath ?? string.Empty),
                            LastImportedRecord   = locationImport?.LastImportedRecord,
                            LastProcessedIndex   = locationImport?.LastProcessedIndex,
                            TotalRecords     = locationImport?.TotalRecords ?? 0,
                            SkippedDuplicates = locationImport?.SkippedDuplicates ?? 0,
                            Status = ImportStatus.Stopped,
                            ErrorMessage = locationImport?.ErrorMessage,
                        })
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while processing import {ImportId}.", importId);
                var li = await _context.LocationImports.FindAsync(importId);
                if (li != null)
                {
                    li.Status = ImportStatus.Failed;
                    li.ErrorMessage = ex.ToString().Length > 2000
                        ? ex.ToString().Substring(0, 2000)
                        : ex.ToString();
                    await _context.SaveChangesAsync(CancellationToken.None);
                    await _sse.BroadcastAsync(
                        $"import-{li.UserId}",
                        JsonSerializer.Serialize(new {
                            FilePath             = Path.GetFileName(li.FilePath ?? string.Empty),
                            LastImportedRecord   = li.LastImportedRecord,
                            LastProcessedIndex   = li.LastProcessedIndex,
                            TotalRecords         = li.TotalRecords,
                            SkippedDuplicates    = li.SkippedDuplicates,
                            Status               = ImportStatus.Failed,
                            ErrorMessage         = li.ErrorMessage,
                        })
                    );
                }
            }
        }

        private async Task<List<Location>> GetLocationsToProcess(LocationImport locationImport, CancellationToken cancellationToken)
        {
            var filePath = locationImport.FilePath;
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Import file not found at: {filePath}");

            var parser = _parserFactory.GetParser(locationImport.FileType);
            using var stream = File.OpenRead(filePath);
            var locations = await parser.ParseAsync(stream, locationImport.UserId);
            await ResolveActivityTypesAsync(locations, cancellationToken);
            return locations;
        }

        private async Task InsertLocationsToDb(List<Location> locations, CancellationToken cancellationToken)
        {
            _context.Locations.AddRange(locations);
            await _context.SaveChangesAsync(cancellationToken);
        }

        /// <summary>
        /// Filters out duplicate locations from a batch using timestamp and coordinate matching.
        /// </summary>
        /// <param name="batch">The batch of locations to filter.</param>
        /// <param name="userId">The user ID for the locations.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>A tuple containing the list of non-duplicate locations to insert and the count of skipped duplicates.</returns>
        private async Task<(List<Location> toInsert, int skipped)> FilterDuplicatesAsync(
            List<Location> batch,
            string userId,
            CancellationToken ct)
        {
            if (batch.Count == 0)
            {
                return (batch, 0);
            }

            var timeTolerance = TimeSpan.FromSeconds(1);
            var distanceMeters = 10.0;

            // Get timestamp range with buffer
            var minTs = batch.Min(l => l.Timestamp).AddSeconds(-2);
            var maxTs = batch.Max(l => l.Timestamp).AddSeconds(2);

            // Pre-fetch existing locations in range (avoids N+1 queries)
            var existing = await _context.Locations
                .Where(l => l.UserId == userId && l.Timestamp >= minTs && l.Timestamp <= maxTs)
                .Select(l => new { l.Timestamp, l.Coordinates })
                .ToListAsync(ct);

            if (existing.Count == 0)
            {
                return (batch, 0);
            }

            var toInsert = new List<Location>();
            int skipped = 0;

            foreach (var loc in batch)
            {
                bool isDuplicate = existing.Any(e =>
                    Math.Abs((e.Timestamp - loc.Timestamp).TotalSeconds) <= timeTolerance.TotalSeconds &&
                    HaversineDistanceMeters(e.Coordinates.X, e.Coordinates.Y, loc.Coordinates.X, loc.Coordinates.Y) <= distanceMeters);

                if (isDuplicate)
                {
                    skipped++;
                    _logger.LogDebug(
                        "Skipping duplicate location: Timestamp={Timestamp}, Lat={Lat}, Lon={Lon}",
                        loc.Timestamp, loc.Coordinates.Y, loc.Coordinates.X);
                }
                else
                {
                    toInsert.Add(loc);
                }
            }

            if (skipped > 0)
            {
                _logger.LogInformation(
                    "Deduplication: {Skipped} duplicates found in batch of {Total}",
                    skipped, batch.Count);
            }

            return (toInsert, skipped);
        }

        private async Task ResolveActivityTypesAsync(List<Location> locations, CancellationToken cancellationToken)
        {
            var distinctNames = locations
                .Select(l => l.ImportedActivityName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (distinctNames.Count == 0)
            {
                return;
            }

            var activities = await _context.ActivityTypes
                .AsNoTracking()
                .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                .ToListAsync(cancellationToken);

            var lookup = activities
                .ToDictionary(a => a.Name!, a => a.Id, StringComparer.OrdinalIgnoreCase);

            foreach (var location in locations)
            {
                if (string.IsNullOrWhiteSpace(location.ImportedActivityName))
                {
                    continue;
                }

                var key = location.ImportedActivityName.Trim();
                if (!lookup.TryGetValue(key, out var activityId))
                {
                    continue;
                }

                location.ActivityTypeId = activityId;
                location.ActivityType = null;
                location.ImportedActivityName = null;
            }
        }

        /// <summary>
        /// Calculates the Haversine (great-circle) distance between two points in meters.
        /// </summary>
        /// <param name="lon1">Longitude of first point in degrees.</param>
        /// <param name="lat1">Latitude of first point in degrees.</param>
        /// <param name="lon2">Longitude of second point in degrees.</param>
        /// <param name="lat2">Latitude of second point in degrees.</param>
        /// <returns>Distance in meters.</returns>
        private static double HaversineDistanceMeters(double lon1, double lat1, double lon2, double lat2)
        {
            const double EarthRadiusMeters = 6_371_000.0;

            var dLat = DegreesToRadians(lat2 - lat1);
            var dLon = DegreesToRadians(lon2 - lon1);

            var lat1Rad = DegreesToRadians(lat1);
            var lat2Rad = DegreesToRadians(lat2);

            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(lat1Rad) * Math.Cos(lat2Rad) *
                    Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

            return EarthRadiusMeters * c;
        }

        private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;
    }
}
