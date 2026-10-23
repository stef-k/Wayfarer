using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Tests.Services;

/// <summary>Qualifies native event timestamps through real parsers, import checkpoints and PostgreSQL.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationImportTimestampPostgresTests(PostgresImportTestFixture fixture, ITestOutputHelper output)
{
    /// <summary>Equivalent explicit offsets persist one instant while UUID replay preserves existing owner history.</summary>
    [PostgresFact]
    public async Task ExplicitOffsetCsvAndGeoJson_PersistSameInstantAndPreserveExistingHistory()
    {
        var owner = await fixture.CreateUserAsync();
        var retained = new Location
        {
            UserId = owner.Id, IdempotencyKey = Guid.NewGuid(),
            Timestamp = new DateTime(2026, 8, 21, 8, 0, 0, DateTimeKind.Utc),
            LocalTimestamp = new DateTime(2026, 8, 21, 7, 30, 0, DateTimeKind.Utc),
            TimeZoneId = "UTC", Coordinates = new Point(23.2, 41.1) { SRID = 4326 },
            Notes = "<p>Existing owner history</p>", Address = "Retained address", DeviceModel = "Retained device"
        };
        await using (var seed = fixture.CreateContext())
        {
            seed.Locations.Add(retained);
            await seed.SaveChangesAsync();
        }
        var csvKey = Guid.NewGuid();
        var geoJsonKey = Guid.NewGuid();
        var csv = "Latitude,Longitude,TimestampUtc,LocalTimestamp,TimeZoneId,IdempotencyKey,Notes,DeviceModel\r\n" +
                  $"40.1,22.2,2026-08-22T08:00:00Z,2026-08-22T10:00:00+02:00,Europe/Athens,{csvKey:D},Imported capture,Synthetic device\r\n";
        var csvImport = await ImportAsync(owner.Id, LocationImportFileType.Csv, csv);
        var geoJsonImport = await ImportAsync(owner.Id, LocationImportFileType.WayfarerGeoJson,
            GeoJson("2026-08-22T10:00:00+02:00", geoJsonKey, retained.IdempotencyKey!.Value));

        await using var verification = fixture.CreateContext();
        var rows = await verification.Locations.AsNoTracking().Where(row => row.UserId == owner.Id).ToListAsync();
        Assert.Equal(3, rows.Count);
        var csvRow = Assert.Single(rows, row => row.IdempotencyKey == csvKey);
        var geoJsonRow = Assert.Single(rows, row => row.IdempotencyKey == geoJsonKey);
        output.WriteLine($"Persisted event instants: CSV={csvRow.LocalTimestamp:O}; GeoJSON={geoJsonRow.LocalTimestamp:O}");
        AssertImported(csvRow, csvKey, 8);
        AssertImported(geoJsonRow, geoJsonKey, 8);
        Assert.Equal(csvRow.LocalTimestamp, geoJsonRow.LocalTimestamp);
        Assert.Equal(owner.Id, geoJsonRow.UserId);
        Assert.Equal(1, csvImport.TotalRecords);
        Assert.Equal(0, csvImport.SkippedDuplicates);
        Assert.Equal(2, geoJsonImport.TotalRecords);
        Assert.Equal(1, geoJsonImport.SkippedDuplicates);
        var existing = Assert.Single(rows, row => row.Id == retained.Id);
        Assert.Equal((retained.UserId, retained.IdempotencyKey, retained.Timestamp, retained.LocalTimestamp, retained.TimeZoneId),
            (existing.UserId, existing.IdempotencyKey, existing.Timestamp, existing.LocalTimestamp, existing.TimeZoneId));
        Assert.Equal((retained.Notes, retained.Address, retained.DeviceModel, retained.Coordinates.X, retained.Coordinates.Y),
            (existing.Notes, existing.Address, existing.DeviceModel, existing.Coordinates.X, existing.Coordinates.Y));
    }

    /// <summary>Explicit Z and legacy unzoned values preserve their approved persisted components.</summary>
    [PostgresTheory]
    [InlineData("2026-08-22T10:00:00Z")]
    [InlineData("2026-08-22T10:00:00")]
    public async Task GeoJsonControls_PreserveEventInstantAndCheckpoint(string localTimestamp)
    {
        var owner = await fixture.CreateUserAsync();
        var key = Guid.NewGuid();
        var import = await ImportAsync(owner.Id, LocationImportFileType.WayfarerGeoJson, GeoJson(localTimestamp, key));

        await using var verification = fixture.CreateContext();
        var saved = Assert.Single(await verification.Locations.AsNoTracking()
            .Where(row => row.UserId == owner.Id).ToListAsync());
        AssertImported(saved, key, 10);
        Assert.Equal(1, import.TotalRecords);
        Assert.Equal(0, import.SkippedDuplicates);
        output.WriteLine($"Persisted GeoJSON control {localTimestamp}: {saved.LocalTimestamp:O}");
    }

    /// <summary>Checks persisted UTC values, stored UUIDs and existing native metadata.</summary>
    private static void AssertImported(Location saved, Guid key, int eventHour)
    {
        Assert.True(saved.Id > 0);
        Assert.Equal(key, saved.IdempotencyKey);
        Assert.Equal(new DateTime(2026, 8, 22, 8, 0, 0, DateTimeKind.Utc), saved.Timestamp);
        Assert.Equal(new DateTime(2026, 8, 22, eventHour, 0, 0, DateTimeKind.Utc), saved.LocalTimestamp);
        Assert.Equal(DateTimeKind.Utc, saved.Timestamp.Kind);
        Assert.Equal(DateTimeKind.Utc, saved.LocalTimestamp.Kind);
        Assert.Equal("Europe/Athens", saved.TimeZoneId);
        Assert.Equal((22.2, 40.1), (saved.Coordinates.X, saved.Coordinates.Y));
        Assert.Equal("Imported capture", saved.Notes);
        Assert.Equal("Synthetic device", saved.DeviceModel);
        Assert.Equal(Wayfarer.Util.LocationHistoryDerivation.TimeVersion, saved.HistoryTimeVersion);
        Assert.Equal(Wayfarer.Util.LocationHistoryDerivation.TextVersion, saved.HistoryTextVersion);
        Assert.Equal("IMPORTED CAPTURE", saved.HistorySearchText);
        Assert.Equal(saved.LocalTimestamp.AddHours(3).Ticks, saved.CaptureLocalTimestamp!.Value.Ticks);
    }

    /// <summary>Builds native features with distinct keys, including an optional replay of existing history.</summary>
    private static string GeoJson(string localTimestamp, params Guid[] keys) => JsonSerializer.Serialize(new
    {
        type = "FeatureCollection",
        features = keys.Select(key => new
        {
            type = "Feature", geometry = new { type = "Point", coordinates = new[] { 22.2, 40.1 } },
            properties = new
            {
                TimestampUtc = "2026-08-22T08:00:00Z", LocalTimestamp = localTimestamp,
                TimeZoneId = "Europe/Athens", IdempotencyKey = key.ToString("D"),
                Notes = "Imported capture", DeviceModel = "Synthetic device"
            }
        })
    });

    /// <summary>Uses established staging and factory-based production execution with a durable generation.</summary>
    private async Task<LocationImport> ImportAsync(string userId, LocationImportFileType format, string payload)
    {
        var path = ImportStaging.TempFile();
        try
        {
            await File.WriteAllTextAsync(path, payload);
            await using var db = fixture.CreateContext();
            var import = new LocationImport
            {
                UserId = userId, FilePath = path, FileType = format,
                Status = ImportStatus.InProgress, ExecutionEpoch = 7,
                TotalRecords = 0, LastProcessedIndex = 0
            };
            db.LocationImports.Add(import);
            await db.SaveChangesAsync();
            using var client = new HttpClient();
            var worker = new LocationImportService(ImportStaging.Files, new FixtureFactory(fixture),
                new ReverseGeocodingService(client, NullLogger<BaseApiController>.Instance),
                NullLogger<LocationImportService>.Instance,
                new LocationDataParserFactory(NullLoggerFactory.Instance), new SseService());
            await worker.ProcessImport(import.Id, import.ExecutionEpoch, CancellationToken.None);
            await db.Entry(import).ReloadAsync();
            Assert.Equal(ImportStatus.Completed, import.Status);
            Assert.Equal(import.TotalRecords, import.LastProcessedIndex);
            Assert.Equal(7, import.ExecutionEpoch);
            Assert.Null(import.ErrorMessage);
            Assert.False(import.ProjectionPending);
            return import;
        }
        finally { File.Delete(path); }
    }

    /// <summary>Reuses the guarded database for independently owned production worker contexts.</summary>
    private sealed class FixtureFactory(PostgresImportTestFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        /// <summary>Creates a fresh relational context for each batch and checkpoint.</summary>
        public ApplicationDbContext CreateDbContext() => fixture.CreateContext();
    }
}
