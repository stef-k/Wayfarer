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
                var allLocations = await GetLocationsToProcess(locationImport);
                int total      = allLocations.Count;
                int processed  = locationImport.LastProcessedIndex;
                locationImport.TotalRecords = total;
                const int batchSize = 50;

                while (processed < total)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Refresh status in case user clicked “stop”
                    locationImport = await _context.LocationImports.FindAsync(importId);
                    if (locationImport.Status == ImportStatus.Stopping)
                    {
                        locationImport.Status = ImportStatus.Stopped;
                        await _context.SaveChangesAsync(cancellationToken);
                        
                        await _sse.BroadcastAsync(
                            $"import-{locationImport.UserId}",
                            JsonSerializer.Serialize(new {
                                FilePath           = Path.GetFileName(locationImport.FilePath),
                                LastImportedRecord = locationImport.LastImportedRecord,
                                LastProcessedIndex = locationImport.LastProcessedIndex,
                                TotalRecords     = locationImport.TotalRecords,
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
                                        apiToken);

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

                    // 3) Insert & save this batch
                    await InsertLocationsToDb(batch, cancellationToken);

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
                        $"import-{locationImport.UserId}",
                        JsonSerializer.Serialize(new {
                            FilePath             = Path.GetFileName(locationImport.FilePath),
                            LastImportedRecord   = locationImport.LastImportedRecord,
                            LastProcessedIndex   = locationImport.LastProcessedIndex,
                            TotalRecords     = locationImport.TotalRecords,
                            Status = ImportStatus.InProgress,
                            ErrorMessage = locationImport.ErrorMessage,
                        })
                    );

                    // brief pause between batches
                    await Task.Delay(1_000, cancellationToken);
                }

                // 5) All done
                locationImport.Status = ImportStatus.Completed;
                await _context.SaveChangesAsync(cancellationToken);
                await _sse.BroadcastAsync(
                    $"import-{locationImport.UserId}",
                    JsonSerializer.Serialize(new {
                        FilePath             = Path.GetFileName(locationImport.FilePath),
                        LastImportedRecord   = locationImport.LastImportedRecord,
                        LastProcessedIndex   = locationImport.LastProcessedIndex,
                        Status = ImportStatus.Completed,
                        ErrorMessage = locationImport.ErrorMessage,
                    })
                );
                _logger.LogInformation(
                    "Import {ImportId} completed successfully: {Total} records processed.",
                    importId, total);
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
                        $"import-{locationImport.UserId}",
                        JsonSerializer.Serialize(new {
                            FilePath             = Path.GetFileName(locationImport.FilePath),
                            LastImportedRecord   = locationImport.LastImportedRecord,
                            LastProcessedIndex   = locationImport.LastProcessedIndex,
                            TotalRecords     = locationImport.TotalRecords,
                            Status = ImportStatus.Stopped,
                            ErrorMessage = locationImport.ErrorMessage,
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
                        $"import-{locationImport.UserId}",
                        JsonSerializer.Serialize(new {
                            FilePath             = Path.GetFileName(locationImport.FilePath),
                            LastImportedRecord   = locationImport.LastImportedRecord,
                            LastProcessedIndex   = locationImport.LastProcessedIndex,
                            TotalRecords     = locationImport.TotalRecords,
                            Status = ImportStatus.Failed,
                            ErrorMessage = locationImport.ErrorMessage,
                        })
                    );
                }
            }
        }

        private async Task<List<Location>> GetLocationsToProcess(LocationImport locationImport)
        {
            var filePath = locationImport.FilePath;
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Import file not found at: {filePath}");

            var parser = _parserFactory.GetParser(locationImport.FileType);
            using var stream = File.OpenRead(filePath);
            return await parser.ParseAsync(stream, locationImport.UserId);
        }

        private async Task InsertLocationsToDb(List<Location> locations, CancellationToken cancellationToken)
        {
            _context.Locations.AddRange(locations);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}
