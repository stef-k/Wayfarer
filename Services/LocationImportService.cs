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

                    // 2) Reverse‑geocode only if we have a key AND the record truly needs it (not having a full address data)
                    if (hasApiKey)
                    {
                        foreach (var loc in batch)
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

                    // 3) Filter duplicates before inserting
                    var (toInsert, skippedInBatch) = await FilterDuplicatesAsync(
                        batch,
                        locationImport.UserId,
                        cancellationToken);

                    locationImport.SkippedDuplicates += skippedInBatch;

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
                        $"import-{locationImport?.UserId}",
                        JsonSerializer.Serialize(new {
                            FilePath             = Path.GetFileName(locationImport?.FilePath ?? string.Empty),
                            LastImportedRecord   = locationImport?.LastImportedRecord,
                            LastProcessedIndex   = locationImport?.LastProcessedIndex,
                            TotalRecords     = locationImport?.TotalRecords ?? 0,
                            SkippedDuplicates = locationImport?.SkippedDuplicates ?? 0,
                            Status = ImportStatus.Failed,
                            ErrorMessage = locationImport?.ErrorMessage,
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
                    e.Coordinates.Distance(loc.Coordinates) <= distanceMeters);

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
    }
}
