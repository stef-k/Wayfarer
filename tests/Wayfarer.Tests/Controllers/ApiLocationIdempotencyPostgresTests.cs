using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Services.LocationImports;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Real unique-index arbitration and admission ordering using the established guarded database.</summary>
[Collection(PostgresImportTestCollection.Name)]
public class ApiLocationIdempotencyPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>A real import committed after the initial key miss supplies the filtered API response's ID.</summary>
    [PostgresFact(Timeout = 30000)]
    public async Task LogLocation_ImportWinsAfterKeyMiss_ReturnsWinnerWithoutOverwrite()
    {
        var user = await fixture.CreateUserAsync();
        await SeedToken(user);
        var key = Guid.NewGuid();
        var dto = ApiLocationAdmissionTests.Dto();
        dto.Timestamp = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        dto.Notes = "API retry must not overwrite the import";
        var importCommitted = false;
        var visits = new Mock<IPlaceVisitDetectionService>(MockBehavior.Strict);
        visits.Setup(service => service.ProcessPingAsync(user.Id, It.IsAny<Point>(), dto.Accuracy,
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                // The API is suspended at its existing visit seam, after the initial keyed lookup missed.
                await using var before = fixture.CreateContext();
                Assert.False(await before.Locations.AnyAsync(row => row.UserId == user.Id));
                await ImportWinner(user.Id, key, dto.Timestamp);
                importCommitted = true;
            });
        var sse = new Mock<SseService>(MockBehavior.Strict);
        await using var apiDb = fixture.CreateContext();
        var controller = ApiLocationAdmissionTests.Controller(apiDb, new ApiWorkAdmission(64, 8), visits.Object, sse.Object);
        controller.Request.Headers.Authorization = $"Bearer {user.Id}";
        controller.Request.Headers["Idempotency-Key"] = key.ToString("B");

        var result = await controller.LogLocation(dto);

        Assert.True(importCommitted);
        await using var verify = fixture.CreateContext();
        var winner = Assert.Single(await verify.Locations.AsNoTracking().Where(row => row.UserId == user.Id).ToListAsync());
        AssertLogResponse(result, winner.Id);
        Assert.Equal(key, winner.IdempotencyKey);
        Assert.Equal((22.2, 40.1), (winner.Coordinates.X, winner.Coordinates.Y));
        Assert.Equal(dto.Timestamp, winner.Timestamp);
        Assert.Equal(dto.Timestamp, winner.LocalTimestamp);
        Assert.Equal("Imported winner", winner.Notes);
        Assert.Equal("Imported device", winner.DeviceModel);
        visits.Verify(service => service.ProcessPingAsync(user.Id, It.IsAny<Point>(), dto.Accuracy,
            It.IsAny<CancellationToken>()), Times.Once);
        sse.VerifyNoOtherCalls();
    }

    /// <summary>Two first uses converge through save-conflict reread; stored replay ignores saturation.</summary>
    [PostgresFact(Timeout = 30000)]
    public async Task ConcurrentFirstUseAndReplay()
    {
        var user = await fixture.CreateUserAsync();
        await SeedToken(user);
        var key = Guid.NewGuid();
        var gate = new ApiWorkAdmission(64, 8);
        var barrier = new SaveBarrier();
        await using var firstDb = fixture.CreateContext(barrier);
        await using var secondDb = fixture.CreateContext(barrier);
        var first = ApiLocationAdmissionTests.Controller(firstDb, gate);
        var second = ApiLocationAdmissionTests.Controller(secondDb, gate);
        foreach (var controller in new[] { first, second })
        {
            controller.Request.Headers.Authorization = $"Bearer {user.Id}";
            controller.Request.Headers["Idempotency-Key"] = key.ToString();
        }
        var results = await Task.WhenAll(first.CheckIn(ApiLocationAdmissionTests.Dto()),
            second.LogLocation(ApiLocationAdmissionTests.Dto()));
        Assert.All(results, result => Assert.IsType<OkObjectResult>(result));
        await using var verify = fixture.CreateContext();
        Assert.Equal(1, await verify.Locations.CountAsync(l => l.UserId == user.Id && l.IdempotencyKey == key));
        Assert.Equal(0, gate.IdentityCount);

        var saturated = new ApiWorkAdmission(1, 1);
        using var held = saturated.TryAcquire(user.Id, out _);
        var replay = ApiLocationAdmissionTests.Controller(verify, saturated);
        replay.Request.Headers.Authorization = $"Bearer {user.Id}";
        replay.Request.Headers["Idempotency-Key"] = key.ToString();
        Assert.IsType<OkObjectResult>(await replay.CheckIn(ApiLocationAdmissionTests.Dto()));
        Assert.IsType<OkObjectResult>(await replay.LogLocation(ApiLocationAdmissionTests.Dto()));

        var other = await fixture.CreateUserAsync();
        await SeedToken(other);
        await using var otherDb = fixture.CreateContext();
        var independent = ApiLocationAdmissionTests.Controller(otherDb, gate);
        independent.Request.Headers.Authorization = $"Bearer {other.Id}";
        independent.Request.Headers["Idempotency-Key"] = key.ToString();
        Assert.IsType<OkObjectResult>(await independent.CheckIn(ApiLocationAdmissionTests.Dto()));
        Assert.Equal(1, await verify.Locations.CountAsync(l => l.UserId == other.Id && l.IdempotencyKey == key));
    }

    /// <summary>Seeds a fixture-owned token; user cleanup cascades to tokens and locations.</summary>
    private async Task SeedToken(ApplicationUser user)
    {
        await using var db = fixture.CreateContext();
        db.ApiTokens.Add(new ApiToken { User = null!, UserId = user.Id, Name = "phone", Token = user.Id });
        await db.SaveChangesAsync();
    }

    /// <summary>Commits one keyed CSV row through the established staging authority and production worker.</summary>
    private async Task ImportWinner(string userId, Guid key, DateTime timestamp)
    {
        var path = ImportStaging.TempFile();
        try
        {
            await File.WriteAllTextAsync(path,
                "Latitude,Longitude,TimestampUtc,LocalTimestamp,TimeZoneId,IdempotencyKey,Notes,DeviceModel\r\n" +
                $"40.1,22.2,{timestamp:O},{timestamp:O},UTC,{key:D},Imported winner,Imported device\r\n");
            await using var db = fixture.CreateContext();
            var import = new LocationImport
            {
                UserId = userId, FilePath = path, FileType = LocationImportFileType.Csv,
                Status = ImportStatus.InProgress, ExecutionEpoch = 7, TotalRecords = 0, LastProcessedIndex = 0
            };
            db.LocationImports.Add(import);
            await db.SaveChangesAsync();
            using var client = new HttpClient();
            var worker = new LocationImportService(ImportStaging.Files, db,
                new ReverseGeocodingService(client, NullLogger<BaseApiController>.Instance),
                NullLogger<LocationImportService>.Instance,
                new LocationDataParserFactory(NullLoggerFactory.Instance), new SseService());
            Assert.Equal(LocationImportExecutionOutcome.Completed,
                await worker.ProcessImportExecution(import.Id, import.ExecutionEpoch, CancellationToken.None));
            await db.Entry(import).ReloadAsync();
            Assert.Equal(1, import.LastProcessedIndex);
            Assert.Equal(0, import.SkippedDuplicates);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Checks the published automatic-ingestion response fields and numeric/null identity.</summary>
    private static void AssertLogResponse(IActionResult result, int? winnerId)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(200, ok.StatusCode);
        var response = JsonSerializer.SerializeToElement(ok.Value);
        Assert.True(response.GetProperty("success").GetBoolean());
        Assert.Equal(!winnerId.HasValue, response.GetProperty("skipped").GetBoolean());
        if (winnerId.HasValue) Assert.Equal(winnerId.Value, response.GetProperty("locationId").GetInt32());
        else Assert.Equal(JsonValueKind.Null, response.GetProperty("locationId").ValueKind);
    }

    /// <summary>Holds both real first inserts until both actions have passed their replay lookup.</summary>
    private sealed class SaveBarrier : SaveChangesInterceptor
    {
        private int _arrived;
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _arrived) == 2) _both.TrySetResult();
            await _both.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }
    }
}
