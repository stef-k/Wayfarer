using System;
using System.Diagnostics.CodeAnalysis;
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
using Wayfarer.Services.LocationProviders;
using Wayfarer.Services.LocationImports;

namespace Wayfarer.Parsers
{
    public interface ILocationImportService
    {
        Task ProcessImport(int importId, CancellationToken cancellationToken);
    }

    /// <summary>Exposes a bounded execution outcome to the Quartz job/listener seam.</summary>
    public interface ILocationImportExecutionService
    {
        Task<LocationImportExecutionOutcome> ProcessImportExecution(int importId, int epoch, CancellationToken cancellationToken);
    }

    public class LocationImportService : ILocationImportService, ILocationImportExecutionService
    {
        private const string SafeProgressEvent = """{"type":"import-state"}""";
        private readonly ApplicationDbContext _context;
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
            _logger = logger;
            _parserFactory = parserFactory;
            _sse = sse;
            _enrichmentHandoff = enrichmentHandoff;
        }

        public async Task ProcessImport(int importId, CancellationToken cancellationToken)
        {
            var outcome = await ProcessImportExecution(importId, 0, cancellationToken);
            if (outcome == LocationImportExecutionOutcome.Stale) return;
            var import = await _context.LocationImports.FindAsync([importId], CancellationToken.None);
            if (import is null) return;
            import.Status = outcome switch
            {
                LocationImportExecutionOutcome.Completed => ImportStatus.Completed,
                LocationImportExecutionOutcome.Failed => ImportStatus.Failed,
                _ => ImportStatus.Stopped
            };
            if (outcome == LocationImportExecutionOutcome.Failed) import.ErrorMessage = "Import processing failed.";
            await _context.SaveChangesAsync(CancellationToken.None);
        }

        public async Task<LocationImportExecutionOutcome> ProcessImportExecution(
            int importId, int epoch, CancellationToken cancellationToken)
        {
            // 0) Load the import record
            var locationImport = await _context.LocationImports.FindAsync(importId);
            if (!HasExecutionAuthority(locationImport, epoch))
                return LocationImportExecutionOutcome.Stale;

            var fileType  = locationImport.FileType;

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
                    await _context.Entry(locationImport!).ReloadAsync(cancellationToken);
                    if (!HasExecutionAuthority(locationImport, epoch))
                    {
                        return LocationImportExecutionOutcome.Stale;
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

                    // Enrichment is deliberately not performed inline. Persisted Locations are handed to
                    // the same leased workflow used by manual and scheduled executions after this commit.
                    if (toInsert.Count > 0)
                    {
                        if (locationImport.EnrichmentRequested)
                            locationImport.RemainingEnrichmentCount += toInsert.Count(IsMissingAddress);
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
                    if (!await HasCurrentExecutionAuthorityAsync(importId, epoch, cancellationToken))
                    {
                        return LocationImportExecutionOutcome.Stale;
                    }
                    await _context.SaveChangesAsync(cancellationToken);
                    await ReconcileEnrichmentAsync(locationImport, cancellationToken);

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
                    if (!await HasCurrentExecutionAuthorityAsync(importId, epoch, cancellationToken))
                        return LocationImportExecutionOutcome.Stale;
                    await ReconcileEnrichmentAsync(locationImport, cancellationToken);
                    await _sse.BroadcastAsync(
                        $"import-{locationImport.UserId}",
                        SafeProgressEvent
                    );
                    _logger.LogInformation(
                        "Import {ImportId} completed successfully: {Total} records processed, {Skipped} duplicates skipped.",
                        importId, total, locationImport.SkippedDuplicates);
                }
                return LocationImportExecutionOutcome.Completed;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Import {ImportId} was cancelled mid-process.", importId);
                var li = await _context.LocationImports.FindAsync(importId);
                if (li != null)
                {
                    await ReconcileEnrichmentAsync(li, CancellationToken.None);
                    await _sse.BroadcastAsync(
                        $"import-{locationImport?.UserId}",
                        SafeProgressEvent
                    );
                }
                return LocationImportExecutionOutcome.Cancelled;
            }
            catch (DbUpdateConcurrencyException)
            {
                _context.ChangeTracker.Clear();
                return LocationImportExecutionOutcome.Stale;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while processing import {ImportId}.", importId);
                var li = await _context.LocationImports.FindAsync(importId);
                if (li != null)
                {
                    await ReconcileEnrichmentAsync(li, CancellationToken.None);
                    await _sse.BroadcastAsync(
                        $"import-{li.UserId}",
                        SafeProgressEvent
                    );
                }
                return LocationImportExecutionOutcome.Failed;
            }
        }

        private static bool IsMissingAddress(Location location) =>
            GeoapifyLocationBackfillService.IsWhollyUnenriched(location);

        private static bool HasExecutionAuthority(
            [NotNullWhen(true)] LocationImport? import, int epoch) =>
            import is not null && import.Status == ImportStatus.InProgress
            && import.DeletionRequestedAtUtc is null && import.StopRequestedAtUtc is null
            && (epoch == 0 || import.ExecutionEpoch == epoch);

        private Task<bool> HasCurrentExecutionAuthorityAsync(
            int importId, int epoch, CancellationToken cancellationToken) =>
            _context.LocationImports.AsNoTracking().AnyAsync(import => import.Id == importId
                && import.Status == ImportStatus.InProgress && import.DeletionRequestedAtUtc == null
                && import.StopRequestedAtUtc == null && (epoch == 0 || import.ExecutionEpoch == epoch),
                cancellationToken);

        /// <summary>Identifies run-wide authority outcomes after which inline retries cannot succeed.</summary>
        public static bool IsRunWideNoContact(ReverseGeocodingCategory category) => category is
            ReverseGeocodingCategory.Exhausted or ReverseGeocodingCategory.NoProviderSelected
            or ReverseGeocodingCategory.CredentialRequired or ReverseGeocodingCategory.ConsentRequired
            or ReverseGeocodingCategory.Unauthorized or ReverseGeocodingCategory.VerificationRequired
            or ReverseGeocodingCategory.StaleAuthority;

        private async Task ReconcileEnrichmentAsync(LocationImport import, CancellationToken cancellationToken)
        {
            if (!import.EnrichmentRequested || _enrichmentHandoff is null) return;
            if (!await HasCurrentExecutionAuthorityAsync(import.Id, import.ExecutionEpoch, cancellationToken)) return;
            try { await _enrichmentHandoff.EnsureAsync(import.UserId, cancellationToken); }
            catch (Exception exception)
            {
                _logger.LogWarning(exception,
                    "Enrichment scheduling requires reconciliation after import {ImportId}.", import.Id);
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
