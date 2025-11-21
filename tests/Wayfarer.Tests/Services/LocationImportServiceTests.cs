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
