using System.Linq;
using System.Net;
using System.Net.Http;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// LocationImportService behaviors without touching production code.
/// </summary>
public class LocationImportServiceTests : TestBase
{
    [Fact]
    public async Task ProcessImport_Skips_WhenNotInProgress()
    {
        var db = CreateDbContext();
        var import = new LocationImport
        {
            Id = 1,
            UserId = "u1",
            FileType = LocationImportFileType.Csv,
            FilePath = "missing.csv",
            LastProcessedIndex = 0,
            TotalRecords = 0,
            Status = ImportStatus.Completed
        };
        db.LocationImports.Add(import);
        await db.SaveChangesAsync();

        var service = CreateService(db, sse: out var sse);

        await service.ProcessImport(import.Id, CancellationToken.None);

        Assert.Empty(db.Locations);
        Assert.Empty(sse.Messages);
        Assert.Equal(ImportStatus.Completed, db.LocationImports.Single().Status);
    }

    [Fact]
    public async Task ProcessImport_ImportsCsvWithoutMapboxKey()
    {
        var db = CreateDbContext();
        db.ActivityTypes.Add(new ActivityType { Id = 5, Name = "Walking" });
        await db.SaveChangesAsync();

        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(
            tempFile,
            "Latitude,Longitude,TimestampUtc,LocalTimestamp,TimeZoneId,Activity,Accuracy,Altitude,Speed,Address,FullAddress,Notes,AddressNumber,StreetName,PostCode,Place,Region,Country\r\n" +
            "37.1,-122.2,2025-01-01T00:00:00Z,2025-01-01T00:00:00Z,UTC,Walking,5,10,1,Addr,FullAddr,Note,10,Street,12345,City,Region,Country");

        var import = new LocationImport
        {
            Id = 2,
            UserId = "u-import",
            FileType = LocationImportFileType.Csv,
            FilePath = tempFile,
            LastProcessedIndex = 0,
            TotalRecords = 0,
            Status = ImportStatus.InProgress
        };
        db.LocationImports.Add(import);
        await db.SaveChangesAsync();

        var service = CreateService(db, sse: out var sse);

        try
        {
            await service.ProcessImport(import.Id, CancellationToken.None);
        }
        finally
        {
            File.Delete(tempFile);
        }

        var stored = Assert.Single(db.Locations);
        Assert.Equal(5, stored.ActivityTypeId);
        Assert.Null(stored.ImportedActivityName);

        var updatedImport = db.LocationImports.Single(li => li.Id == import.Id);
        Assert.Equal(ImportStatus.Completed, updatedImport.Status);
        Assert.Equal(1, updatedImport.LastProcessedIndex);
        Assert.NotNull(updatedImport.LastImportedRecord);
        Assert.NotEmpty(sse.Messages);
    }

    private LocationImportService CreateService(ApplicationDbContext db, out FakeSseService sse)
    {
        var loggerFactory = NullLoggerFactory.Instance;
        sse = new FakeSseService();
        var parserFactory = new LocationDataParserFactory(loggerFactory);
        var httpClient = new HttpClient(new FakeHttpHandler());
        var reverse = new ReverseGeocodingService(httpClient, NullLogger<BaseApiController>.Instance);
        return new LocationImportService(
            db,
            reverse,
            NullLogger<LocationImportService>.Instance,
            parserFactory,
            sse);
    }

    private sealed class FakeSseService : SseService
    {
        public List<string> Messages { get; } = new();

        public override Task BroadcastAsync(string channel, string data)
        {
            Messages.Add($"{channel}:{data}");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ProcessImport_SkipsDuplicates_WhenLocationsAlreadyExist()
    {
        var db = CreateDbContext();

        // Pre-existing location
        var existingLocation = new Location
        {
            UserId = "u-dedup",
            Timestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            LocalTimestamp = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            TimeZoneId = "UTC",
            Coordinates = new NetTopologySuite.Geometries.Point(-122.2, 37.1) { SRID = 4326 },
            Accuracy = 5,
            Source = "api-log"
        };
        db.Locations.Add(existingLocation);
        await db.SaveChangesAsync();

        // CSV with same location (should be skipped) + one new location
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(
            tempFile,
            "Latitude,Longitude,TimestampUtc,LocalTimestamp,TimeZoneId,Accuracy\r\n" +
            "37.1,-122.2,2025-01-01T00:00:00Z,2025-01-01T00:00:00Z,UTC,5\r\n" +      // Duplicate
            "37.2,-122.3,2025-01-02T00:00:00Z,2025-01-02T00:00:00Z,UTC,10");          // New

        var import = new LocationImport
        {
            Id = 10,
            UserId = "u-dedup",
            FileType = LocationImportFileType.Csv,
            FilePath = tempFile,
            LastProcessedIndex = 0,
            TotalRecords = 0,
            Status = ImportStatus.InProgress
        };
        db.LocationImports.Add(import);
        await db.SaveChangesAsync();

        var service = CreateService(db, sse: out var sse);

        try
        {
            await service.ProcessImport(import.Id, CancellationToken.None);
        }
        finally
        {
            File.Delete(tempFile);
        }

        // Should have 2 locations total: 1 existing + 1 new (1 skipped)
        Assert.Equal(2, db.Locations.Count());

        var updatedImport = db.LocationImports.Single(li => li.Id == import.Id);
        Assert.Equal(ImportStatus.Completed, updatedImport.Status);
        Assert.Equal(1, updatedImport.SkippedDuplicates);
        Assert.Equal(2, updatedImport.LastProcessedIndex); // Processed 2 records

        // Verify SSE broadcast includes SkippedDuplicates
        Assert.Contains(sse.Messages, m => m.Contains("SkippedDuplicates"));
    }

    [Fact]
    public async Task ProcessImport_SetsSourceField_OnImportedLocations()
    {
        var db = CreateDbContext();

        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(
            tempFile,
            "Latitude,Longitude,TimestampUtc,LocalTimestamp,TimeZoneId,Accuracy\r\n" +
            "37.1,-122.2,2025-01-01T00:00:00Z,2025-01-01T00:00:00Z,UTC,5");

        var import = new LocationImport
        {
            Id = 11,
            UserId = "u-source",
            FileType = LocationImportFileType.Csv,
            FilePath = tempFile,
            LastProcessedIndex = 0,
            TotalRecords = 0,
            Status = ImportStatus.InProgress
        };
        db.LocationImports.Add(import);
        await db.SaveChangesAsync();

        var service = CreateService(db, sse: out _);

        try
        {
            await service.ProcessImport(import.Id, CancellationToken.None);
        }
        finally
        {
            File.Delete(tempFile);
        }

        var stored = Assert.Single(db.Locations);
        Assert.Equal("queue-import", stored.Source);
    }

    [Fact]
    public async Task ProcessImport_ImportSameFileTwice_SkipsAllOnSecondRun()
    {
        var db = CreateDbContext();

        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(
            tempFile,
            "Latitude,Longitude,TimestampUtc,LocalTimestamp,TimeZoneId,Accuracy\r\n" +
            "37.1,-122.2,2025-01-01T00:00:00Z,2025-01-01T00:00:00Z,UTC,5\r\n" +
            "37.2,-122.3,2025-01-02T00:00:00Z,2025-01-02T00:00:00Z,UTC,10");

        // First import
        var import1 = new LocationImport
        {
            Id = 20,
            UserId = "u-twice",
            FileType = LocationImportFileType.Csv,
            FilePath = tempFile,
            LastProcessedIndex = 0,
            TotalRecords = 0,
            Status = ImportStatus.InProgress
        };
        db.LocationImports.Add(import1);
        await db.SaveChangesAsync();

        var service1 = CreateService(db, sse: out _);
        await service1.ProcessImport(import1.Id, CancellationToken.None);

        Assert.Equal(2, db.Locations.Count());
        Assert.Equal(0, db.LocationImports.Single(li => li.Id == 20).SkippedDuplicates);

        // Second import of same file
        var import2 = new LocationImport
        {
            Id = 21,
            UserId = "u-twice",
            FileType = LocationImportFileType.Csv,
            FilePath = tempFile,
            LastProcessedIndex = 0,
            TotalRecords = 0,
            Status = ImportStatus.InProgress
        };
        db.LocationImports.Add(import2);
        await db.SaveChangesAsync();

        var service2 = CreateService(db, sse: out _);

        try
        {
            await service2.ProcessImport(import2.Id, CancellationToken.None);
        }
        finally
        {
            File.Delete(tempFile);
        }

        // Should still have 2 locations (no new ones added)
        Assert.Equal(2, db.Locations.Count());

        var updatedImport2 = db.LocationImports.Single(li => li.Id == import2.Id);
        Assert.Equal(ImportStatus.Completed, updatedImport2.Status);
        Assert.Equal(2, updatedImport2.SkippedDuplicates); // Both records skipped
    }

    [Fact]
    public async Task ProcessImport_BoundaryTime_ExactlyOneSecondApart_IsDuplicate()
    {
        var db = CreateDbContext();

        // Pre-existing location
        var existingLocation = new Location
        {
            UserId = "u-boundary-time",
            Timestamp = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            LocalTimestamp = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            TimeZoneId = "UTC",
            Coordinates = new NetTopologySuite.Geometries.Point(-122.2, 37.1) { SRID = 4326 },
            Source = "api-log"
        };
        db.Locations.Add(existingLocation);
        await db.SaveChangesAsync();

        // CSV with location exactly 1 second later at same coordinates (should be duplicate)
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(
            tempFile,
            "Latitude,Longitude,TimestampUtc,LocalTimestamp,TimeZoneId\r\n" +
            "37.1,-122.2,2025-01-01T12:00:01Z,2025-01-01T12:00:01Z,UTC");

        var import = new LocationImport
        {
            Id = 30,
            UserId = "u-boundary-time",
            FileType = LocationImportFileType.Csv,
            FilePath = tempFile,
            LastProcessedIndex = 0,
            TotalRecords = 0,
            Status = ImportStatus.InProgress
        };
        db.LocationImports.Add(import);
        await db.SaveChangesAsync();

        var service = CreateService(db, sse: out _);

        try
        {
            await service.ProcessImport(import.Id, CancellationToken.None);
        }
        finally
        {
            File.Delete(tempFile);
        }

        // Should still have only 1 location (new one skipped as duplicate)
        Assert.Equal(1, db.Locations.Count());
        Assert.Equal(1, db.LocationImports.Single(li => li.Id == import.Id).SkippedDuplicates);
    }

    [Fact]
    public async Task ProcessImport_BoundaryTime_JustOverOneSecondApart_IsNotDuplicate()
    {
        var db = CreateDbContext();

        // Pre-existing location
        var existingLocation = new Location
        {
            UserId = "u-boundary-time2",
            Timestamp = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            LocalTimestamp = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            TimeZoneId = "UTC",
            Coordinates = new NetTopologySuite.Geometries.Point(-122.2, 37.1) { SRID = 4326 },
            Source = "api-log"
        };
        db.Locations.Add(existingLocation);
        await db.SaveChangesAsync();

        // CSV with location 2 seconds later at same coordinates (should NOT be duplicate)
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(
            tempFile,
            "Latitude,Longitude,TimestampUtc,LocalTimestamp,TimeZoneId\r\n" +
            "37.1,-122.2,2025-01-01T12:00:02Z,2025-01-01T12:00:02Z,UTC");

        var import = new LocationImport
        {
            Id = 31,
            UserId = "u-boundary-time2",
            FileType = LocationImportFileType.Csv,
            FilePath = tempFile,
            LastProcessedIndex = 0,
            TotalRecords = 0,
            Status = ImportStatus.InProgress
        };
        db.LocationImports.Add(import);
        await db.SaveChangesAsync();

        var service = CreateService(db, sse: out _);

        try
        {
            await service.ProcessImport(import.Id, CancellationToken.None);
        }
        finally
        {
            File.Delete(tempFile);
        }

        // Should have 2 locations (new one is NOT a duplicate)
        Assert.Equal(2, db.Locations.Count());
        Assert.Equal(0, db.LocationImports.Single(li => li.Id == import.Id).SkippedDuplicates);
    }

    [Fact]
    public async Task ProcessImport_BoundaryDistance_Within10Meters_IsDuplicate()
    {
        var db = CreateDbContext();

        // Pre-existing location at 37.1, -122.2
        var existingLocation = new Location
        {
            UserId = "u-boundary-dist",
            Timestamp = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            LocalTimestamp = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            TimeZoneId = "UTC",
            Coordinates = new NetTopologySuite.Geometries.Point(-122.2, 37.1) { SRID = 4326 },
            Source = "api-log"
        };
        db.Locations.Add(existingLocation);
        await db.SaveChangesAsync();

        // CSV with location ~5 meters away at same timestamp (should be duplicate)
        // At latitude 37.1°: ~5m north ≈ 0.000045 degrees latitude
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(
            tempFile,
            "Latitude,Longitude,TimestampUtc,LocalTimestamp,TimeZoneId\r\n" +
            "37.100045,-122.2,2025-01-01T12:00:00Z,2025-01-01T12:00:00Z,UTC");

        var import = new LocationImport
        {
            Id = 32,
            UserId = "u-boundary-dist",
            FileType = LocationImportFileType.Csv,
            FilePath = tempFile,
            LastProcessedIndex = 0,
            TotalRecords = 0,
            Status = ImportStatus.InProgress
        };
        db.LocationImports.Add(import);
        await db.SaveChangesAsync();

        var service = CreateService(db, sse: out _);

        try
        {
            await service.ProcessImport(import.Id, CancellationToken.None);
        }
        finally
        {
            File.Delete(tempFile);
        }

        // Should still have only 1 location (new one skipped as duplicate)
        Assert.Equal(1, db.Locations.Count());
        Assert.Equal(1, db.LocationImports.Single(li => li.Id == import.Id).SkippedDuplicates);
    }

    [Fact]
    public async Task ProcessImport_BoundaryDistance_Over10Meters_IsNotDuplicate()
    {
        var db = CreateDbContext();

        // Pre-existing location at 37.1, -122.2
        var existingLocation = new Location
        {
            UserId = "u-boundary-dist2",
            Timestamp = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            LocalTimestamp = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            TimeZoneId = "UTC",
            Coordinates = new NetTopologySuite.Geometries.Point(-122.2, 37.1) { SRID = 4326 },
            Source = "api-log"
        };
        db.Locations.Add(existingLocation);
        await db.SaveChangesAsync();

        // CSV with location ~15 meters away at same timestamp (should NOT be duplicate)
        // At latitude 37.1°: ~15m north ≈ 0.000135 degrees latitude
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(
            tempFile,
            "Latitude,Longitude,TimestampUtc,LocalTimestamp,TimeZoneId\r\n" +
            "37.100135,-122.2,2025-01-01T12:00:00Z,2025-01-01T12:00:00Z,UTC");

        var import = new LocationImport
        {
            Id = 33,
            UserId = "u-boundary-dist2",
            FileType = LocationImportFileType.Csv,
            FilePath = tempFile,
            LastProcessedIndex = 0,
            TotalRecords = 0,
            Status = ImportStatus.InProgress
        };
        db.LocationImports.Add(import);
        await db.SaveChangesAsync();

        var service = CreateService(db, sse: out _);

        try
        {
            await service.ProcessImport(import.Id, CancellationToken.None);
        }
        finally
        {
            File.Delete(tempFile);
        }

        // Should have 2 locations (new one is NOT a duplicate)
        Assert.Equal(2, db.Locations.Count());
        Assert.Equal(0, db.LocationImports.Single(li => li.Id == import.Id).SkippedDuplicates);
    }

    [Fact]
    public async Task ProcessImport_BothBoundaries_TimeAndDistanceWithinTolerance_IsDuplicate()
    {
        var db = CreateDbContext();

        // Pre-existing location
        var existingLocation = new Location
        {
            UserId = "u-boundary-both",
            Timestamp = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            LocalTimestamp = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            TimeZoneId = "UTC",
            Coordinates = new NetTopologySuite.Geometries.Point(-122.2, 37.1) { SRID = 4326 },
            Source = "api-log"
        };
        db.Locations.Add(existingLocation);
        await db.SaveChangesAsync();

        // CSV with location ~8m away and 1 second later (both within tolerance = duplicate)
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(
            tempFile,
            "Latitude,Longitude,TimestampUtc,LocalTimestamp,TimeZoneId\r\n" +
            "37.100072,-122.2,2025-01-01T12:00:01Z,2025-01-01T12:00:01Z,UTC");

        var import = new LocationImport
        {
            Id = 34,
            UserId = "u-boundary-both",
            FileType = LocationImportFileType.Csv,
            FilePath = tempFile,
            LastProcessedIndex = 0,
            TotalRecords = 0,
            Status = ImportStatus.InProgress
        };
        db.LocationImports.Add(import);
        await db.SaveChangesAsync();

        var service = CreateService(db, sse: out _);

        try
        {
            await service.ProcessImport(import.Id, CancellationToken.None);
        }
        finally
        {
            File.Delete(tempFile);
        }

        // Should have only 1 location (new one is a duplicate)
        Assert.Equal(1, db.Locations.Count());
        Assert.Equal(1, db.LocationImports.Single(li => li.Id == import.Id).SkippedDuplicates);
    }

    [Fact]
    public async Task ProcessImport_Boundary_TimeWithinButDistanceOutside_IsNotDuplicate()
    {
        var db = CreateDbContext();

        // Pre-existing location
        var existingLocation = new Location
        {
            UserId = "u-boundary-mix1",
            Timestamp = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            LocalTimestamp = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            TimeZoneId = "UTC",
            Coordinates = new NetTopologySuite.Geometries.Point(-122.2, 37.1) { SRID = 4326 },
            Source = "api-log"
        };
        db.Locations.Add(existingLocation);
        await db.SaveChangesAsync();

        // CSV with location ~20m away but only 0.5 seconds later (distance outside, time within)
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(
            tempFile,
            "Latitude,Longitude,TimestampUtc,LocalTimestamp,TimeZoneId\r\n" +
            "37.10018,-122.2,2025-01-01T12:00:00Z,2025-01-01T12:00:00Z,UTC");

        var import = new LocationImport
        {
            Id = 35,
            UserId = "u-boundary-mix1",
            FileType = LocationImportFileType.Csv,
            FilePath = tempFile,
            LastProcessedIndex = 0,
            TotalRecords = 0,
            Status = ImportStatus.InProgress
        };
        db.LocationImports.Add(import);
        await db.SaveChangesAsync();

        var service = CreateService(db, sse: out _);

        try
        {
            await service.ProcessImport(import.Id, CancellationToken.None);
        }
        finally
        {
            File.Delete(tempFile);
        }

        // Should have 2 locations (new one is NOT a duplicate - distance too far)
        Assert.Equal(2, db.Locations.Count());
        Assert.Equal(0, db.LocationImports.Single(li => li.Id == import.Id).SkippedDuplicates);
    }

    [Fact]
    public async Task ProcessImport_Boundary_DistanceWithinButTimeOutside_IsNotDuplicate()
    {
        var db = CreateDbContext();

        // Pre-existing location
        var existingLocation = new Location
        {
            UserId = "u-boundary-mix2",
            Timestamp = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            LocalTimestamp = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            TimeZoneId = "UTC",
            Coordinates = new NetTopologySuite.Geometries.Point(-122.2, 37.1) { SRID = 4326 },
            Source = "api-log"
        };
        db.Locations.Add(existingLocation);
        await db.SaveChangesAsync();

        // CSV with location at same coordinates but 5 seconds later (distance within, time outside)
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(
            tempFile,
            "Latitude,Longitude,TimestampUtc,LocalTimestamp,TimeZoneId\r\n" +
            "37.1,-122.2,2025-01-01T12:00:05Z,2025-01-01T12:00:05Z,UTC");

        var import = new LocationImport
        {
            Id = 36,
            UserId = "u-boundary-mix2",
            FileType = LocationImportFileType.Csv,
            FilePath = tempFile,
            LastProcessedIndex = 0,
            TotalRecords = 0,
            Status = ImportStatus.InProgress
        };
        db.LocationImports.Add(import);
        await db.SaveChangesAsync();

        var service = CreateService(db, sse: out _);

        try
        {
            await service.ProcessImport(import.Id, CancellationToken.None);
        }
        finally
        {
            File.Delete(tempFile);
        }

        // Should have 2 locations (new one is NOT a duplicate - time too far)
        Assert.Equal(2, db.Locations.Count());
        Assert.Equal(0, db.LocationImports.Single(li => li.Id == import.Id).SkippedDuplicates);
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"features":[]}""")
            });
        }
    }
}
