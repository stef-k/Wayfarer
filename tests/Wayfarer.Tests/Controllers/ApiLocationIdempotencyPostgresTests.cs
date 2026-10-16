using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Point = NetTopologySuite.Geometries.Point;
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
        var afterKeyMiss = new KeyMissImportInterceptor(async () =>
            {
                // The initial SELECT has executed with no winner; import before the API evaluates filters.
                await using var before = fixture.CreateContext();
                Assert.False(await before.Locations.AnyAsync(row => row.UserId == user.Id));
                await ImportWinner(user.Id, key, dto.Timestamp);
                importCommitted = true;
            });
        var visits = new Mock<IPlaceVisitDetectionService>(MockBehavior.Strict);
        var sse = new Mock<SseService>(MockBehavior.Strict);
        await using var apiDb = fixture.CreateContext(afterKeyMiss);
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
        visits.VerifyNoOtherCalls();
        sse.VerifyNoOtherCalls();
    }

    /// <summary>Each terminal filter distinguishes a committed matching key from a genuine unpersisted skip.</summary>
    [PostgresTheory]
    [InlineData("accuracy", "winner")]
    [InlineData("accuracy", "absent")]
    [InlineData("time", "winner")]
    [InlineData("time", "absent")]
    [InlineData("distance", "winner")]
    [InlineData("distance", "absent")]
    [InlineData("duplicate", "winner")]
    [InlineData("duplicate", "absent")]
    [InlineData("time", "other-owner")]
    [InlineData("time", "keyless")]
    public async Task LogLocation_TerminalSkip_OnlyAcknowledgesPersistedOwnerKey(string filter, string outcome)
    {
        var user = await fixture.CreateUserAsync();
        await SeedToken(user);
        var winnerOwner = outcome == "other-owner" ? await fixture.CreateUserAsync() : user;
        var key = Guid.NewGuid();
        var dto = ApiLocationAdmissionTests.Dto();
        dto.Timestamp = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        dto.Accuracy = filter == "accuracy" ? 51 : 5;
        await using (var seed = fixture.CreateContext())
        {
            seed.Locations.Add(new Location
            {
                UserId = user.Id, IdempotencyKey = Guid.NewGuid(), TimeZoneId = "UTC",
                Timestamp = dto.Timestamp.AddMinutes(filter == "time" ? 0 : -10),
                LocalTimestamp = dto.Timestamp.AddMinutes(filter == "duplicate" ? 0 : -10),
                Coordinates = filter == "distance" ? new Point(dto.Longitude, dto.Latitude) { SRID = 4326 }
                    : new Point(22.2, 40.1) { SRID = 4326 }
            });
            await seed.SaveChangesAsync();
        }
        var winner = new Location
        {
            UserId = winnerOwner.Id, IdempotencyKey = key, TimeZoneId = "UTC",
            Timestamp = dto.Timestamp.AddMinutes(-20), LocalTimestamp = dto.Timestamp,
            Coordinates = new Point(23.3, 41.2) { SRID = 4326 }, Notes = "Concurrent winner"
        };
        var settings = new Mock<IApplicationSettingsService>();
        settings.Setup(service => service.GetSettings()).Returns(() =>
        {
            // Settings are read after the initial key miss, including before the early accuracy filter.
            using var concurrent = fixture.CreateContext();
            if (outcome != "absent")
            {
                concurrent.Locations.Add(winner);
                concurrent.SaveChanges();
            }
            return new ApplicationSettings { Id = 1 };
        });
        await using var apiDb = fixture.CreateContext();
        var visits = new Mock<IPlaceVisitDetectionService>(MockBehavior.Strict);
        visits.Setup(service => service.ProcessPingAsync(user.Id, It.IsAny<Point>(), dto.Accuracy,
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var sse = new Mock<SseService>(MockBehavior.Strict);
        var controller = ApiLocationAdmissionTests.Controller(apiDb, new ApiWorkAdmission(64, 8),
            visits.Object, sse.Object, settingsService: settings.Object);
        controller.Request.Headers.Authorization = $"Bearer {user.Id}";
        if (outcome != "keyless") controller.Request.Headers["Idempotency-Key"] = key.ToString("D");

        AssertLogResponse(await controller.LogLocation(dto), outcome == "winner" ? winner.Id : null);

        await using var verify = fixture.CreateContext();
        Assert.Equal(outcome == "absent" ? 1 : 2,
            await verify.Locations.CountAsync(row => row.UserId == user.Id || row.UserId == winnerOwner.Id));
        if (outcome != "absent")
            Assert.Equal("Concurrent winner", (await verify.Locations.AsNoTracking().SingleAsync(row => row.Id == winner.Id)).Notes);
        visits.Verify(service => service.ProcessPingAsync(user.Id, It.IsAny<Point>(), dto.Accuracy,
            It.IsAny<CancellationToken>()), outcome == "winner" || filter == "accuracy" ? Times.Never() : Times.Once());
        sse.VerifyNoOtherCalls();
    }

    /// <summary>A failed or cancelled persisted-winner lookup cannot turn into a successful skipped/null response.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LogLocation_WinnerLookupFailureOrCancellation_DoesNotAcknowledgeSkip(bool cancel)
    {
        var user = await fixture.CreateUserAsync();
        await SeedToken(user);
        using var cancellation = new CancellationTokenSource();
        var failure = new LookupFailureInterceptor();
        await using var apiDb = fixture.CreateContext(failure);
        var settings = new Mock<IApplicationSettingsService>();
        settings.Setup(service => service.GetSettings()).Returns(() =>
        {
            if (cancel) cancellation.Cancel();
            else failure.FailLookup = true;
            return new ApplicationSettings { Id = 1 };
        });
        var gate = new ApiWorkAdmission(64, 8);
        var controller = ApiLocationAdmissionTests.Controller(apiDb, gate, settingsService: settings.Object);
        controller.Request.Headers.Authorization = $"Bearer {user.Id}";
        controller.Request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString("D");
        controller.HttpContext.RequestAborted = cancellation.Token;
        var dto = ApiLocationAdmissionTests.Dto();
        dto.Accuracy = 51;

        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.LogLocation(dto));
        else Assert.Same(failure.Failure,
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.LogLocation(dto)));
        Assert.Equal(0, gate.IdentityCount);
        await using var verify = fixture.CreateContext();
        Assert.False(await verify.Locations.AnyAsync(row => row.UserId == user.Id));
    }

    /// <summary>Two automatic first uses converge with one visit hit; stored replay ignores saturation without a hit.</summary>
    [PostgresFact(Timeout = 30000)]
    public async Task ConcurrentFirstUseAndReplay()
    {
        var user = await fixture.CreateUserAsync();
        await SeedToken(user);
        var key = Guid.NewGuid();
        var gate = new ApiWorkAdmission(64, 8);
        var barrier = new SaveBarrier();
        var visits = new Mock<IPlaceVisitDetectionService>(MockBehavior.Strict);
        visits.Setup(service => service.ProcessPingAsync(user.Id, It.IsAny<Point>(), It.IsAny<double?>(),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        await using var firstDb = fixture.CreateContext(barrier);
        await using var secondDb = fixture.CreateContext(barrier);
        var first = ApiLocationAdmissionTests.Controller(firstDb, gate, visits.Object);
        var second = ApiLocationAdmissionTests.Controller(secondDb, gate, visits.Object);
        foreach (var controller in new[] { first, second })
        {
            controller.Request.Headers.Authorization = $"Bearer {user.Id}";
            controller.Request.Headers["Idempotency-Key"] = key.ToString();
        }
        var results = await Task.WhenAll(first.LogLocation(ApiLocationAdmissionTests.Dto()),
            second.LogLocation(ApiLocationAdmissionTests.Dto()));
        Assert.All(results, result => Assert.IsType<OkObjectResult>(result));
        await using var verify = fixture.CreateContext();
        Assert.Equal(1, await verify.Locations.CountAsync(l => l.UserId == user.Id && l.IdempotencyKey == key));
        visits.Verify(service => service.ProcessPingAsync(user.Id, It.IsAny<Point>(), It.IsAny<double?>(),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(0, gate.IdentityCount);

        var saturated = new ApiWorkAdmission(1, 1);
        using var held = saturated.TryAcquire(user.Id, out _);
        var replay = ApiLocationAdmissionTests.Controller(verify, saturated, visits.Object);
        replay.Request.Headers.Authorization = $"Bearer {user.Id}";
        replay.Request.Headers["Idempotency-Key"] = key.ToString();
        Assert.IsType<OkObjectResult>(await replay.CheckIn(ApiLocationAdmissionTests.Dto()));
        var winnerId = await verify.Locations.Where(l => l.UserId == user.Id && l.IdempotencyKey == key)
            .Select(l => l.Id).SingleAsync();
        AssertLogResponse(await replay.LogLocation(ApiLocationAdmissionTests.Dto()), winnerId);
        visits.Verify(service => service.ProcessPingAsync(user.Id, It.IsAny<Point>(), It.IsAny<double?>(),
            It.IsAny<CancellationToken>()), Times.Once);

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

    /// <summary>Commits the real import after the initial keyed SELECT has observed its statement snapshot.</summary>
    private sealed class KeyMissImportInterceptor(Func<Task> import) : DbCommandInterceptor
    {
        private bool _imported;

        /// <summary>Leaves the already-executed key lookup empty while making its winner visible to subsequent queries.</summary>
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!_imported && command.CommandText.Contains("\"IdempotencyKey\"", StringComparison.Ordinal))
            {
                _imported = true;
                await import();
            }
            return result;
        }
    }

    /// <summary>Injects a failure only after the request's initial lookup and before its terminal accuracy skip.</summary>
    private sealed class LookupFailureInterceptor : DbCommandInterceptor
    {
        /// <summary>Enables failure at the post-settings lookup seam.</summary>
        public bool FailLookup { get; set; }
        /// <summary>The original database-operation failure must reach the caller unchanged.</summary>
        public InvalidOperationException Failure { get; } = new("Synthetic persisted-winner lookup failure");

        /// <summary>Leaves earlier reads intact and fails the fresh persisted-winner read.</summary>
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (FailLookup) throw Failure;
            return ValueTask.FromResult(result);
        }
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
