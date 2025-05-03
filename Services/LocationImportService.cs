using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Parsers;
using System.Threading;
using System.Threading.Tasks;

namespace Wayfarer.Services
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
            var locationImport = await _context.LocationImports.FindAsync(importId);
            if (locationImport == null || locationImport.Status != ImportStatus.InProgress)
                return;

            // Load Mapbox key if available
            var apiToken = await _context.ApiTokens
                .Where(t => t.UserId == locationImport.UserId
                            && t.Name.ToLower() == "mapbox")
                .Select(t => t.Token)
                .FirstOrDefaultAsync(cancellationToken);
            
            bool doGeocoding = !string.IsNullOrWhiteSpace(apiToken);
            if (!doGeocoding)
            {
                _logger.LogWarning(
                    "User {UserId} has no Mapbox key—skipping reverse-geocoding for import {ImportId}.",
                    locationImport.UserId, importId);
            }

            try
            {
                var allLocations = await GetLocationsToProcess(locationImport);
                int total = allLocations.Count;
                int processed = locationImport.LastProcessedIndex;
                locationImport.TotalRecords = total;
                const int batchSize = 50;

                while (processed < total)
                {
                    // 1) Honor cancellation before each batch
                    cancellationToken.ThrowIfCancellationRequested();

                    // 2) Check user‐requested status change
                    locationImport = await _context.LocationImports.FindAsync(importId);
                    if (locationImport.Status == ImportStatus.Stopping)
                    {
                        locationImport.Status = ImportStatus.Stopped;
                        await _context.SaveChangesAsync(cancellationToken);
                        _logger.LogInformation(
                            "Import {ImportId} cancelled by user after {Processed} records.",
                            importId, processed);
                        return;
                    }

                    // 3) Grab next batch
                    var batch = allLocations
                        .Skip(processed)
                        .Take(batchSize)
                        .ToList();

                    // 4) Process each record (and optionally geocode)
                    if (doGeocoding)
                    {
                        foreach (var loc in batch)
                        {
                            // Check cancellation between individual items
                            cancellationToken.ThrowIfCancellationRequested();

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

                            // throttle between calls
                            await Task.Delay(200, cancellationToken);
                        }
                    }

                    // 5) Insert & save batch
                    await InsertLocationsToDb(batch, cancellationToken);

                    // 6) Update progress
                    processed += batch.Count;
                    
                    var latest = batch
                        .OrderByDescending(l => l.Timestamp)
                        .FirstOrDefault();

                    if (latest != null)
                    {
                        locationImport.LastImportedRecord = $"Timestamp: {latest.Timestamp:u}" + 
                                                            (!string.IsNullOrWhiteSpace(latest.FullAddress) ? $", {latest.FullAddress}" : "");
                    }
                    else
                    {
                        locationImport.LastImportedRecord = "N/A";
                    }
                    
                    locationImport.LastProcessedIndex = processed;
                    await _context.SaveChangesAsync(cancellationToken);
                    await  _sse.BroadcastAsync($"import-{locationImport.UserId}", JsonSerializer.Serialize(new
                    {
                        FilePath = System.IO.Path.GetFileName(locationImport.FilePath),
                        LastImportedRecord = locationImport.LastImportedRecord,
                        LastProcessedIndex = locationImport.LastProcessedIndex
                    }));

                    // Optional pacing between batches
                    await Task.Delay(1_000, cancellationToken);
                }

                // 7) Mark completed
                locationImport.Status = ImportStatus.Completed;
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation(
                    "Import {ImportId} completed successfully: {Total} records processed.",
                    importId, total);
            }
            catch (OperationCanceledException)
            {
                // Quartz or user requested cancellation
                _logger.LogInformation("Import {ImportId} was cancelled mid-process.", importId);

                // Ensure we mark it stopped if it wasn’t already
                var li = await _context.LocationImports.FindAsync(importId);
                if (li != null)
                {
                    li.Status = ImportStatus.Stopped;
                    await _context.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while processing location import {ImportId}.", importId);
                var li = await _context.LocationImports.FindAsync(importId);
                if (li != null)
                {
                    li.Status = ImportStatus.Failed;
                    await _context.SaveChangesAsync(CancellationToken.None);
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
