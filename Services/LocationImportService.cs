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
using Wayfarer.Services.LocationEnrichment;

namespace Wayfarer.Parsers
{
    public interface ILocationImportService
    {
        Task ProcessImport(int importId, CancellationToken cancellationToken);
    }

    public class LocationImportService : ILocationImportService
    {
        private const string SafeProgressEvent = """{"type":"import-state"}""";
        private readonly ApplicationDbContext _context;
        private readonly ReverseGeocodingService _reverseGeocodingService;
        private readonly ILogger<LocationImportService> _logger;
        private readonly LocationDataParserFactory _parserFactory;
        private readonly SseService _sse;
        private readonly IImportEnrichmentHandoff? _enrichmentHandoff;

        public LocationImportService(
            ApplicationDbContext context,
            ReverseGeocodingService reverseGeocodingService,
            ILogger<LocationImportService> logger,
            LocationDataParserFactory parserFactory,
            SseService sse,
            IImportEnrichmentHandoff? enrichmentHandoff = null)
        {
            _context = context;
            _reverseGeocodingService = reverseGeocodingService;
            _logger = logger;
            _parserFactory = parserFactory;
            _sse = sse;
            _enrichmentHandoff = enrichmentHandoff;
        }

        public async Task ProcessImport(int importId, CancellationToken cancellationToken)
        {
            // 0) Load the import record
            var locationImport = await _context.LocationImports.FindAsync(importId);
            if (locationImport == null || locationImport.Status != ImportStatus.InProgress)
                return;

            var fileType  = locationImport.FileType;

            try
            {
                var allLocations = await GetLocationsToProcess(locationImport, cancellationToken);
                int total      = allLocations.Count;
                int processed  = locationImport.LastProcessedIndex;
                locationImport.TotalRecords = total;
                const int batchSize = 50;
                var inlineEnrichmentEnabled = locationImport.EnrichmentRequested;

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
                            SafeProgressEvent
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
                    var (toInsert, skippedInBatch) = await LocationImportDeduplicator.FilterAsync(
                        _context, batch, locationImport.UserId, _logger, cancellationToken);

                    locationImport.SkippedDuplicates += skippedInBatch;

                    // 3) Reverse‑geocode only non-duplicates that need it
                    if (toInsert.Count > 0)
                    {
                        foreach (var loc in toInsert)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            // Only geocode points that lack an address
                            if (inlineEnrichmentEnabled && string.IsNullOrWhiteSpace(loc.FullAddress))
                            {
                                var enrichment = await _reverseGeocodingService.EnrichAsync(locationImport.UserId,
                                    loc.Coordinates.Y, loc.Coordinates.X, ReverseGeocodingIntent.ImportMissingAddress,
                                    cancellationToken);
                                enrichment.ApplyTo(loc, DateTimeOffset.UtcNow);
                                if (IsRunWideNoContact(enrichment.Category))
                                {
                                    inlineEnrichmentEnabled = false;
                                    locationImport.EnrichmentPauseReason = enrichment.Category.ToString();
                                }
                                else
                                {
                                    await Task.Delay(200, cancellationToken);
                                }
                            }
                        }
                    }

                    // Insert only non-duplicates
                    if (toInsert.Count > 0)
                    {
                        locationImport.SkippedDuplicates += await LocationImportDeduplicator.InsertAsync(
                            _context, toInsert, locationImport.UserId, cancellationToken);
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
                    if (locationImport.EnrichmentRequested && _enrichmentHandoff is not null)
                        await _enrichmentHandoff.EnsureAsync(locationImport.UserId, cancellationToken);

                    await _sse.BroadcastAsync(
                        $"import-{locationImport?.UserId}",
                        SafeProgressEvent
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
                        SafeProgressEvent
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
                        SafeProgressEvent
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
                    li.ErrorMessage = "Import processing failed.";
                    await _context.SaveChangesAsync(CancellationToken.None);
                    await _sse.BroadcastAsync(
                        $"import-{li.UserId}",
                        SafeProgressEvent
                    );
                }
            }
        }

        /// <summary>Identifies run-wide authority outcomes after which inline retries cannot succeed.</summary>
        public static bool IsRunWideNoContact(ReverseGeocodingCategory category) => category is
            ReverseGeocodingCategory.Exhausted or ReverseGeocodingCategory.NoProviderSelected
            or ReverseGeocodingCategory.CredentialRequired or ReverseGeocodingCategory.ConsentRequired
            or ReverseGeocodingCategory.Unauthorized or ReverseGeocodingCategory.VerificationRequired
            or ReverseGeocodingCategory.StaleAuthority;

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
