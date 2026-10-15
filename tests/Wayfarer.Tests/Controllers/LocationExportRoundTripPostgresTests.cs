using System.Globalization;
using System.Text;
using System.Text.Json;
using CsvHelper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Areas.User.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Parsers;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Tests.Controllers;

/// <summary>Exercises owner exports through the production parsers, worker and PostgreSQL persistence.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationExportRoundTripPostgresTests(PostgresImportTestFixture fixture) : TestBase, IAsyncLifetime
{
    private readonly string activityPrefix = $"export-activity-{Guid.NewGuid():N}-";

    /// <summary>Fresh exports and destination imports retain names, independent types and metadata across all native formats.</summary>
    [PostgresTheory]
    [InlineData(LocationImportFileType.Csv)]
    [InlineData(LocationImportFileType.WayfarerGeoJson)]
    [InlineData(LocationImportFileType.Gpx)]
    [InlineData(LocationImportFileType.Kml)]
    public async Task NativeRoundTrip_ReconcilesDestinationActivitiesAndPreservesLocationType(LocationImportFileType format)
    {
        var owner = await fixture.CreateUserAsync();
        var destination = await fixture.CreateUserAsync();
        var other = await fixture.CreateUserAsync();
        var names = new[]
        {
            activityPrefix + "Eating  \tOut\u00a0& <\rHome",
            activityPrefix + "eBike  \tRide\u00a0& <\rHome"
        };
        var activities = names.Select(name => new ActivityType { Name = name }).ToArray();
        var originals = new[]
        {
            CreateLocation(owner.Id, Guid.NewGuid()),
            CreateLocation(owner.Id, Guid.NewGuid()),
            CreateLocation(owner.Id, null)
        };
        var types = new[] { "Manual", " MiXeD,\t\u00a0<&\"'\r\n ", " \t\r\n " };
        for (var i = 0; i < originals.Length; i++)
        {
            originals[i].Timestamp = originals[i].Timestamp.AddHours(i * 2);
            originals[i].LocalTimestamp = originals[i].LocalTimestamp.AddHours(i * 2);
            originals[i].LocationType = types[i];
            if (i < activities.Length) originals[i].ActivityType = activities[i];
        }
        var privateLocation = CreateLocation(other.Id, Guid.NewGuid());
        privateLocation.Notes = "Other owner's private native history";
        await using (var seed = fixture.CreateContext())
        {
            seed.Locations.AddRange(originals);
            seed.Locations.Add(privateLocation);
            await seed.SaveChangesAsync();
        }

        var exported = await ExportAsync(owner.Id, format);
        var parsed = await ParseAsync(destination.Id, format, exported);
        Assert.Equal(new string?[] { names[0], names[1], null }, parsed.Select(row => row.ImportedActivityName));
        Assert.Equal(types, parsed.Select(row => row.LocationType));
        Assert.DoesNotContain(privateLocation.Notes, Encoding.UTF8.GetString(exported));
        Assert.DoesNotContain(owner.Id, Encoding.UTF8.GetString(exported));
        Assert.DoesNotContain("ActivityTypeId", Encoding.UTF8.GetString(exported));
        Assert.DoesNotContain("NormalizedName", Encoding.UTF8.GetString(exported));

        // The fixture shares one catalog: retire source names to model a destination with different IDs.
        var existing = new ActivityType { Name = names[0].Replace("Eating", "EATING"), Description = "Destination description" };
        await using (var catalog = fixture.CreateContext())
        {
            foreach (var activity in activities)
            {
                var source = await catalog.ActivityTypes.SingleAsync(row => row.Id == activity.Id);
                source.Name += " retired";
            }
            catalog.ActivityTypes.Add(existing);
            await catalog.SaveChangesAsync();
        }

        var import = await ImportAsync(destination.Id, format, exported);
        await using (var verification = fixture.CreateContext())
        {
            var saved = await verification.Locations.Include(row => row.ActivityType)
                .Where(row => row.UserId == destination.Id).OrderBy(row => row.Timestamp).ToListAsync();
            Assert.Equal(3, saved.Count);
            Assert.Equal(0, import.UnclassifiedActivityCount);
            Assert.Equal(0, import.SkippedDuplicates);
            Assert.Equal(existing.Id, saved[0].ActivityTypeId);
            Assert.Equal("Destination description", saved[0].ActivityType!.Description);
            Assert.Equal(new string?[] { existing.Name, names[1], null }, saved.Select(row => row.ActivityType?.Name));
            Assert.Null(saved[1].ActivityType!.Description);
            for (var i = 0; i < saved.Count; i++)
            {
                AssertMetadata(originals[i], saved[i]);
                Assert.Equal(types[i], saved[i].LocationType);
                Assert.Equal(format is LocationImportFileType.Gpx or LocationImportFileType.Kml
                    ? null : originals[i].IdempotencyKey, saved[i].IdempotencyKey);
                if (i < activities.Length) Assert.NotEqual(activities[i].Id, saved[i].ActivityTypeId);
            }
        }
        var reexported = await ParseAsync(destination.Id, format, await ExportAsync(destination.Id, format));
        Assert.Equal(new string?[] { existing.Name, names[1], null }, reexported.Select(row => row.ImportedActivityName));
        Assert.Equal(types, reexported.Select(row => row.LocationType));
        for (var i = 0; i < originals.Length; i++) AssertMetadata(originals[i], reexported[i]);
        var replay = await ImportAsync(destination.Id, format, exported);
        Assert.Equal(3, replay.SkippedDuplicates);
    }

    /// <summary>Distinct keyed captures transfer independently with destination identity and supported metadata.</summary>
    [PostgresTheory]
    [InlineData(LocationImportFileType.Csv)]
    [InlineData(LocationImportFileType.WayfarerGeoJson)]
    public async Task RoundTrip_PreservesDistinctKeysNullAndMetadataForAnotherOwner(LocationImportFileType format)
    {
        var owner = await fixture.CreateUserAsync();
        var destination = await fixture.CreateUserAsync();
        var other = await fixture.CreateUserAsync();
        var originals = new[]
        {
            CreateLocation(owner.Id, Guid.NewGuid()),
            CreateLocation(owner.Id, Guid.NewGuid()),
            CreateLocation(owner.Id, null)
        };
        originals[2].Timestamp = originals[2].Timestamp.AddHours(2);
        originals[2].LocalTimestamp = originals[2].LocalTimestamp.AddHours(2);
        var privateLocation = CreateLocation(other.Id, Guid.NewGuid());
        privateLocation.Notes = "Other owner's private history";
        await using (var seed = fixture.CreateContext())
        {
            seed.Locations.AddRange(originals);
            seed.Locations.Add(privateLocation);
            await seed.SaveChangesAsync();
        }

        var exported = await ExportAsync(owner.Id, format);
        var import = await ImportAsync(destination.Id, format, exported);
        await using var verification = fixture.CreateContext();
        var transferred = await verification.Locations.Where(row => row.UserId == destination.Id).ToListAsync();
        Assert.Equal(3, transferred.Count);
        Assert.Equal(0, import.SkippedDuplicates);
        Assert.Equal(3, import.LastProcessedIndex);
        foreach (var original in originals)
        {
            var saved = Assert.Single(transferred, row => row.IdempotencyKey == original.IdempotencyKey);
            Assert.Equal(destination.Id, saved.UserId);
            Assert.True(saved.Id > 0);
            AssertMetadata(original, saved);
        }
        Assert.Equal(2, transferred.Count(row => row.Timestamp == originals[0].Timestamp));
        Assert.DoesNotContain(privateLocation.IdempotencyKey!.Value.ToString("D"), Encoding.UTF8.GetString(exported));
        Assert.DoesNotContain(privateLocation.Notes, Encoding.UTF8.GetString(exported));
        AssertExportIdentities(exported, format, originals);
        AssertExportIdentities(await ExportAsync(destination.Id, format), format, transferred);
        var retained = await verification.Locations.Where(row => row.UserId == owner.Id).OrderBy(row => row.Id).ToListAsync();
        Assert.Equal(originals.Select(row => row.IdempotencyKey), retained.Select(row => row.IdempotencyKey));
    }

    /// <summary>Replaying an old keyed export skips creation and preserves edits outside the keyless tolerance.</summary>
    [PostgresTheory]
    [InlineData(LocationImportFileType.Csv)]
    [InlineData(LocationImportFileType.WayfarerGeoJson)]
    public async Task EditedReplay_SkipsWithoutDuplicatingOrOverwriting(LocationImportFileType format)
    {
        var owner = await fixture.CreateUserAsync();
        var original = CreateLocation(owner.Id, Guid.NewGuid());
        original.LocationType = "Original type";
        await using (var seed = fixture.CreateContext())
        {
            seed.Locations.Add(original);
            await seed.SaveChangesAsync();
        }
        var oldExport = await ExportAsync(owner.Id, format);
        await using (var edit = fixture.CreateContext())
        {
            var saved = await edit.Locations.SingleAsync(row => row.Id == original.Id);
            saved.Coordinates = new Point(23.2, 41.1) { SRID = 4326 };
            saved.Timestamp = saved.Timestamp.AddDays(1);
            saved.LocalTimestamp = saved.LocalTimestamp.AddDays(1);
            saved.Notes = "<p>Edited after export</p>";
            saved.Address = "Edited address";
            saved.LocationType = "Edited type";
            await edit.SaveChangesAsync();
        }

        var import = await ImportAsync(owner.Id, format, oldExport);
        await using var verification = fixture.CreateContext();
        var retained = Assert.Single(await verification.Locations.Where(row => row.UserId == owner.Id).ToListAsync());
        Assert.Equal(original.Id, retained.Id);
        Assert.Equal(original.IdempotencyKey, retained.IdempotencyKey);
        Assert.Equal((23.2, 41.1), (retained.Coordinates.X, retained.Coordinates.Y));
        Assert.Equal(original.Timestamp.AddDays(1), retained.Timestamp);
        Assert.Equal(original.LocalTimestamp.AddDays(1), retained.LocalTimestamp);
        Assert.Equal("<p>Edited after export</p>", retained.Notes);
        Assert.Equal("Edited address", retained.Address);
        Assert.Equal("Edited type", retained.LocationType);
        Assert.Equal(1, import.SkippedDuplicates);
        Assert.Equal(1, import.LastProcessedIndex);
    }

    /// <summary>Old files without a UUID field remain accepted without assigning a synthetic UUID.</summary>
    [PostgresTheory]
    [InlineData(LocationImportFileType.Csv)]
    [InlineData(LocationImportFileType.WayfarerGeoJson)]
    [InlineData(LocationImportFileType.Gpx)]
    [InlineData(LocationImportFileType.Kml)]
    public async Task LegacyFileWithoutKey_RemainsAccepted(LocationImportFileType format)
    {
        var owner = await fixture.CreateUserAsync();
        var payload = format switch
        {
            LocationImportFileType.Csv => "Latitude,Longitude,TimestampUtc\r\n40.1,22.2,2026-08-25T12:00:00Z\r\n",
            LocationImportFileType.WayfarerGeoJson => """
              {"type":"FeatureCollection","features":[{"type":"Feature",
              "geometry":{"type":"Point","coordinates":[22.2,40.1]},
              "properties":{"TimestampUtc":"2026-08-25T12:00:00Z"}}]}
              """,
            LocationImportFileType.Gpx => """
              <gpx xmlns="http://www.topografix.com/GPX/1/1"><trk><trkseg>
              <trkpt lat="40.1" lon="22.2"><time>2026-08-25T12:00:00Z</time></trkpt>
              </trkseg></trk></gpx>
              """,
            _ => """
              <kml xmlns="http://www.opengis.net/kml/2.2"><Document><Placemark>
              <name>2026-08-25T12:00:00Z</name><Point><coordinates>22.2,40.1</coordinates></Point>
              </Placemark></Document></kml>
              """
        };
        var import = await ImportAsync(owner.Id, format, Encoding.UTF8.GetBytes(payload));
        await using var verification = fixture.CreateContext();
        var saved = Assert.Single(await verification.Locations.Where(row => row.UserId == owner.Id).ToListAsync());
        Assert.Null(saved.IdempotencyKey);
        Assert.Null(saved.ActivityTypeId);
        Assert.Null(saved.LocationType);
        Assert.Equal(0, import.SkippedDuplicates);
        Assert.Equal(1, import.LastProcessedIndex);
        var reexported = Assert.Single(await ParseAsync(owner.Id, format, await ExportAsync(owner.Id, format)));
        Assert.Null(reexported.IdempotencyKey);
        Assert.Null(reexported.ImportedActivityName);
        Assert.Null(reexported.LocationType);
    }

    /// <summary>Uses a fresh context and the established authenticated controller principal for each export.</summary>
    private async Task<byte[]> ExportAsync(string userId, LocationImportFileType format)
    {
        await using var db = fixture.CreateContext();
        var controller = ConfigureControllerWithUser(new LocationExportController(db), userId);
        var file = Assert.IsType<FileStreamResult>(format switch
        {
            LocationImportFileType.Csv => await controller.Csv(),
            LocationImportFileType.WayfarerGeoJson => await controller.GeoJson(),
            LocationImportFileType.Gpx => await controller.Gpx(),
            _ => await controller.Kml()
        });
        await using var stream = file.FileStream;
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

    /// <summary>Materializes the actual native parser without supplying catalog IDs or source-owner authority.</summary>
    private static async Task<List<Location>> ParseAsync(string userId, LocationImportFileType format, byte[] payload)
    {
        using var stream = new MemoryStream(payload);
        var rows = new List<Location>();
        var parser = new LocationDataParserFactory(NullLoggerFactory.Instance).GetParser(format);
        await foreach (var row in parser.ParseAsync(stream, userId)) rows.Add(row);
        return rows;
    }

    /// <summary>Stages unchanged exported bytes and runs the current production worker with no enrichment opt-in.</summary>
    private async Task<LocationImport> ImportAsync(string userId, LocationImportFileType format, byte[] payload)
    {
        var path = ImportStaging.TempFile();
        try
        {
            await File.WriteAllBytesAsync(path, payload);
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
            return import;
        }
        finally { File.Delete(path); }
    }

    /// <summary>Checks raw canonical UUID strings, format-specific nulls and the exporting store's numeric IDs.</summary>
    private static void AssertExportIdentities(byte[] payload, LocationImportFileType format, IEnumerable<Location> rows)
    {
        var actual = new List<(int Id, string? Key)>();
        using var stream = new MemoryStream(payload);
        if (format == LocationImportFileType.Csv)
        {
            using var reader = new StreamReader(stream);
            using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
            Assert.True(csv.Read());
            csv.ReadHeader();
            Assert.Contains("IdempotencyKey", csv.HeaderRecord!);
            while (csv.Read()) actual.Add((csv.GetField<int>("Id"), csv.GetField("IdempotencyKey")));
        }
        else
        {
            using var reader = new StreamReader(stream);
            using var json = JsonDocument.Parse(reader.ReadToEnd());
            foreach (var feature in json.RootElement.GetProperty("features").EnumerateArray())
            {
                var properties = feature.GetProperty("properties");
                actual.Add((properties.GetProperty("Id").GetInt32(), properties.GetProperty("IdempotencyKey").GetString()));
            }
        }
        var expected = rows.Select(row => (row.Id, row.IdempotencyKey?.ToString("D")
            ?? (format == LocationImportFileType.Csv ? string.Empty : null)));
        Assert.Equal(expected.OrderBy(row => row.Id), actual.OrderBy(row => row.Id));
    }

    /// <summary>Compares supported history metadata independently of format-specific identity fields.</summary>
    private static void AssertMetadata(Location expected, Location actual)
    {
        Assert.Equal((expected.Timestamp, expected.LocalTimestamp, expected.TimeZoneId,
                expected.Coordinates.X, expected.Coordinates.Y, expected.Accuracy, expected.Altitude, expected.Speed),
            (actual.Timestamp, actual.LocalTimestamp, actual.TimeZoneId,
                actual.Coordinates.X, actual.Coordinates.Y, actual.Accuracy, actual.Altitude, actual.Speed));
        Assert.Equal((expected.Address, expected.FullAddress, expected.AddressNumber, expected.StreetName,
                expected.PostCode, expected.Place, expected.Region, expected.Country, expected.Notes),
            (actual.Address, actual.FullAddress, actual.AddressNumber, actual.StreetName,
                actual.PostCode, actual.Place, actual.Region, actual.Country, actual.Notes));
        Assert.Equal((expected.ProviderAddressLine1, expected.ResolvedFeatureName, expected.ResolvedFeatureType,
                expected.ReverseGeocodingProvider, expected.ReverseGeocodingStorageMode, expected.ReverseGeocodedAt),
            (actual.ProviderAddressLine1, actual.ResolvedFeatureName, actual.ResolvedFeatureType,
                actual.ReverseGeocodingProvider, actual.ReverseGeocodingStorageMode, actual.ReverseGeocodedAt));
        Assert.Equal((expected.Source, expected.IsUserInvoked, expected.Provider, expected.Bearing,
                expected.AppVersion, expected.AppBuild, expected.DeviceModel, expected.OsVersion,
                expected.BatteryLevel, expected.IsCharging),
            (actual.Source, actual.IsUserInvoked, actual.Provider, actual.Bearing,
                actual.AppVersion, actual.AppBuild, actual.DeviceModel, actual.OsVersion,
                actual.BatteryLevel, actual.IsCharging));
    }

    /// <summary>Supplies synthetic captures with UTC instants and existing supported rich metadata.</summary>
    private static Location CreateLocation(string userId, Guid? key) => new()
    {
        UserId = userId, IdempotencyKey = key,
        Timestamp = new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc),
        LocalTimestamp = new DateTime(2026, 8, 25, 11, 59, 30, DateTimeKind.Utc),
        TimeZoneId = "Europe/Athens", Coordinates = new Point(22.2, 40.1) { SRID = 4326 },
        Accuracy = 4.5, Altitude = 42.25, Speed = 1.5,
        Address = "Main Street, 10", FullAddress = "Main Street, 10, City",
        AddressNumber = "10", StreetName = "Main Street", PostCode = "12345",
        Place = "City", Region = "Region", Country = "Country", Notes = "<p>Note &amp; detail</p>",
        ProviderAddressLine1 = "Feature\nMain Street, 10", ResolvedFeatureName = "Feature", ResolvedFeatureType = "amenity",
        ReverseGeocodingProvider = "geoapify", ReverseGeocodingStorageMode = "persistent",
        ReverseGeocodedAt = new DateTimeOffset(2026, 8, 25, 12, 1, 0, TimeSpan.Zero),
        Source = "api-log", IsUserInvoked = false, Provider = "gps", Bearing = 120.5,
        AppVersion = "1.3.0", AppBuild = "100", DeviceModel = "Synthetic device", OsVersion = "Synthetic OS",
        BatteryLevel = 75, IsCharging = true
    };

    /// <summary>Each worker context uses the existing guarded PostgreSQL fixture.</summary>
    private sealed class FixtureFactory(PostgresImportTestFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        /// <summary>Creates an independent context for production batch and checkpoint ownership.</summary>
        public ApplicationDbContext CreateDbContext() => fixture.CreateContext();
    }

    /// <summary>Relies on the shared fixture to initialize the guarded database.</summary>
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Removes only this case's Activity references and catalog rows before fixture-owned user cleanup.</summary>
    public async Task DisposeAsync()
    {
        if (!fixture.IsAvailable) return;
        await using var db = fixture.CreateContext();
        await db.Locations.Where(row => row.ActivityType != null && row.ActivityType.Name.StartsWith(activityPrefix)).ExecuteDeleteAsync();
        await db.ActivityTypes.Where(row => row.Name.StartsWith(activityPrefix)).ExecuteDeleteAsync();
    }
}
