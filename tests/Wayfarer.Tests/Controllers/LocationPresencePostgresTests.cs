using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Tests.Controllers;

/// <summary>Exercises observation history, current ordering, visits and privacy through routed PostgreSQL seams.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationPresencePostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Fresh observations retain two-hit visits; historical arrivals retain history without current effects.</summary>
    [PostgresTheory]
    [InlineData("fresh", true)]
    [InlineData("fresh-automatic", true)]
    [InlineData("old-fix", false)]
    [InlineData("delayed", false)]
    [InlineData("queued-automatic", false)]
    [InlineData("unknown", false)]
    [InlineData("manual-map", false)]
    [InlineData("uncertain-age", false)]
    public async Task RoutedCheckIn_PreservesHistoryAndGatesArrivalEffects(string sample, bool live)
    {
        var user = await SeedAsync();
        await using var db = fixture.CreateContext();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sse = new Recorder();
        var controller = Controller(db, user.Id, cache, sse);
        var root = Path.Combine(Path.GetTempPath(), $"wayfarer-855-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var app = await IdentityRouteHost.StartAsync(db, root, services =>
            {
                services.AddSingleton(controller);
                services.AddControllers().AddControllersAsServices();
            });
            using var client = app.GetTestClient();
            client.BaseAddress = new Uri("https://wayfarer.test");
            client.DefaultRequestHeaders.Authorization = new("Bearer", user.Id);
            var delayed = sample is "delayed" or "queued-automatic";
            int? newerId = null;
            if (delayed)
            {
                var newer = Dto(DateTime.UtcNow.AddSeconds(-5));
                newer.Latitude = 11;
                newer.Longitude = 21;
                newerId = await SendAsync(client, newer, Guid.NewGuid());
                sse.Events.Clear();
            }
            var eventTime = DateTime.UtcNow.AddSeconds(-2).AddDays(delayed ? -1 : 0);
            var dto = Dto(eventTime);
            dto.IsUserInvoked = sample is not ("queued-automatic" or "fresh-automatic");
            if (sample == "old-fix") { dto.ObservedAtUtc = eventTime.AddDays(-1); dto.ObservedAgeAtEventMs = 86400000; }
            if (sample == "manual-map") { dto.CoordinateOrigin = "manual-map"; dto.ObservedAtUtc = null; }
            if (sample == "unknown") { dto.CoordinateOrigin = null; dto.ObservedAtUtc = null; }
            if (sample == "uncertain-age") dto.ObservedAgeAtEventMs = null;
            var key = Guid.NewGuid();
            var firstId = await SendAsync(client, dto, key);
            dto.Timestamp = dto.Timestamp.AddMilliseconds(1);
            if (dto.ObservedAtUtc.HasValue) dto.ObservedAtUtc = dto.ObservedAtUtc.Value.AddMilliseconds(1);
            var lastId = await SendAsync(client, dto, Guid.NewGuid());
            await using var verify = fixture.CreateContext();
            var rows = await verify.Locations.AsNoTracking().Where(row => row.UserId == user.Id).ToListAsync();
            var historical = rows.Where(row => row.Id != newerId).ToArray();
            Assert.Equal(2, historical.Length);
            Assert.All(historical, row =>
            {
                Assert.Equal(dto.CoordinateOrigin, row.CoordinateOrigin);
                Assert.Equal(dto.IsUserInvoked, row.IsUserInvoked);
                Assert.Equal(live, row.PresenceObservedAtUtc.HasValue);
                Assert.Equal(eventTime.Date, row.LocalTimestamp.Date);
                Assert.Equal(DateTime.UtcNow.Date, row.Timestamp.Date);
            });
            Assert.Equal(live ? 1 : 0, await verify.PlaceVisitEvents.CountAsync(row => row.UserId == user.Id));
            Assert.False(await verify.PlaceVisitCandidates.AnyAsync(row => row.UserId == user.Id));
            Assert.DoesNotContain(sse.Events, item => !live && item.Data.Contains("visit_"));
            Assert.All(sse.Events.Where(item => item.Data.Contains("isLive")), item =>
                Assert.Equal(live, JsonDocument.Parse(item.Data).RootElement.GetProperty("isLive").GetBoolean()));
            Assert.DoesNotContain(sse.Events, item => !live && item.Channel.StartsWith("group-"));
            cache.TryGetValue($"lastLocation_{user.Id}", out Location? cached);
            Assert.Equal(live ? lastId : newerId, cached?.Id);
            var beforeReplay = sse.Events.Count;
            dto.Longitude = 30;
            Assert.Equal(firstId, await SendAsync(client, dto, key));
            Assert.Equal(beforeReplay, sse.Events.Count);
            Assert.Equal(rows.Count, await verify.Locations.CountAsync(row => row.UserId == user.Id));
            using var history = await client.GetAsync($"/api/location/chronological?dateType=day&year={eventTime.Year}&month={eventTime.Month}&day={eventTime.Day}");
            Assert.Equal(HttpStatusCode.OK, history.StatusCode);
            Assert.Contains(firstId.ToString(), await history.Content.ReadAsStringAsync());
            await AssertGroupPrivacyAsync(db, client, user, live ? lastId : newerId);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>A committed older request finishing last cannot overtake a newer fix, including equal-fix Id ties.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingWrites_KeepCurrentOrderAfterLateCompletionAndCacheMiss(bool tie)
    {
        var user = await SeedAsync();
        var pause = new PauseAfterInsert();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        await using var oldDb = fixture.CreateContext(pause);
        await using var newDb = fixture.CreateContext();
        var now = DateTime.UtcNow.AddSeconds(-2);
        var old = Controller(oldDb, user.Id, cache, new Recorder());
        var newer = Controller(newDb, user.Id, cache, new Recorder());
        old.Request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString("D");
        newer.Request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString("D");
        var first = old.CheckIn(Dto(now.AddSeconds(tie ? 0 : -1)));
        await pause.Inserted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try { Assert.IsType<OkObjectResult>(await newer.CheckIn(Dto(now))); }
        finally { pause.Release.TrySetResult(); }
        Assert.IsType<OkObjectResult>(await first);
        await using var verify = fixture.CreateContext();
        var rows = await verify.Locations.AsNoTracking().Where(row => row.UserId == user.Id).OrderBy(row => row.Id).ToListAsync();
        Assert.Null(rows[0].PresenceObservedAtUtc);
        Assert.NotNull(rows[1].PresenceObservedAtUtc);
        Assert.True(cache.TryGetValue($"lastLocation_{user.Id}", out Location? cached));
        Assert.Equal(rows[1].Id, cached!.Id);
        cache.Remove($"lastLocation_{user.Id}");
        Assert.Equal(rows[1].Id, (await LocationPresence.Current(verify.Locations.Where(row => row.UserId == user.Id)).FirstAsync()).Id);
        Assert.Single(await verify.PlaceVisitCandidates.Where(row => row.UserId == user.Id).ToListAsync());
    }

    /// <summary>Historical arrivals neither extend an open visit nor become its supporting locations/counts.</summary>
    [PostgresFact]
    public async Task HistoricalArrival_PreservesOpenVisitAndArrivalAssociationBoundary()
    {
        var user = await SeedAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        await using var db = fixture.CreateContext();
        var sse = new Recorder();
        var controller = Controller(db, user.Id, cache, sse);
        var now = DateTime.UtcNow.AddSeconds(-2);
        Assert.IsType<OkObjectResult>(await controller.CheckIn(Dto(now.AddSeconds(-1))));
        Assert.IsType<OkObjectResult>(await controller.CheckIn(Dto(now)));
        var visit = await db.PlaceVisitEvents.AsNoTracking().SingleAsync(row => row.UserId == user.Id);
        var before = sse.Events.Count;
        var history = Dto(now.AddSeconds(1));
        history.ObservedAtUtc = history.ObservedAtUtc!.Value.AddDays(-1);
        history.ObservedAgeAtEventMs += 86400000;
        Assert.IsType<OkObjectResult>(await controller.CheckIn(history));
        await using var verify = fixture.CreateContext();
        var after = await verify.PlaceVisitEvents.SingleAsync(row => row.UserId == user.Id);
        Assert.Equal((visit.ArrivedAtUtc, visit.LastSeenAtUtc, visit.EndedAtUtc),
            (after.ArrivedAtUtc, after.LastSeenAtUtc, after.EndedAtUtc));
        Assert.False(await verify.PlaceVisitCandidates.AnyAsync(row => row.UserId == user.Id));
        Assert.DoesNotContain(sse.Events.Skip(before), item => item.Data.Contains("visit_"));
        // Expand the synthetic association interval to include the late receipt, without advancing detector state.
        after.LastSeenAtUtc = DateTime.UtcNow.AddSeconds(1);
        await verify.SaveChangesAsync();
        var settings = new Mock<IApplicationSettingsService>();
        settings.Setup(service => service.GetSettings()).Returns(new ApplicationSettings { Id = 1 });
        var reads = new VisitController(verify, NullLogger<BaseApiController>.Instance, settings.Object);
        reads.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            { User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, user.Id)], "cookie")) } };
        var recent = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await reads.GetVisitLocations(visit.Id)).Value);
        // Only the second live Location was received after the candidate-based arrival clock.
        Assert.Equal(1, recent.GetProperty("totalItems").GetInt32());
        after.Source = "backfill";
        await verify.SaveChangesAsync();
        var backfill = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await reads.GetVisitLocations(visit.Id)).Value);
        Assert.Equal(2, backfill.GetProperty("totalItems").GetInt32());
    }

    /// <summary>A real PostgreSQL visit failure rolls back noncritical visit work without losing history or presence.</summary>
    [PostgresFact]
    public async Task VisitDatabaseFailure_StillAcknowledgesDurableWinner()
    {
        var user = await SeedAsync();
        await using var db = fixture.CreateContext();
        var visits = new Mock<IPlaceVisitDetectionService>();
        visits.Setup(service => service.ProcessPingAsync(user.Id, It.IsAny<NetTopologySuite.Geometries.Point>(),
            It.IsAny<double?>(), It.IsAny<CancellationToken>())).Returns(async () =>
                await db.Database.ExecuteSqlRawAsync("SELECT 1 / 0"));
        var controller = ApiLocationAdmissionTests.Controller(db, new ApiWorkAdmission(64, 8), visits.Object);
        controller.Request.Headers.Authorization = $"Bearer {user.Id}";
        controller.Request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString("D");
        Assert.IsType<OkObjectResult>(await controller.CheckIn(Dto(DateTime.UtcNow.AddSeconds(-1))));
        await using var verify = fixture.CreateContext();
        var winner = await verify.Locations.SingleAsync(row => row.UserId == user.Id);
        Assert.NotNull(winner.PresenceObservedAtUtc);
        visits.VerifyAll();
    }

    /// <summary>Owner coordinate edits invalidate sensor evidence without changing UUID identity or retained event time.</summary>
    [PostgresFact]
    public async Task CoordinateEdit_ClearsCoordinateBoundEvidence()
    {
        var user = await SeedAsync();
        await using var db = fixture.CreateContext();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = Controller(db, user.Id, cache, new Recorder());
        var key = Guid.NewGuid();
        controller.Request.Headers["Idempotency-Key"] = key.ToString("D");
        Assert.IsType<OkObjectResult>(await controller.CheckIn(Dto(DateTime.UtcNow.AddSeconds(-1))));
        var row = await db.Locations.SingleAsync(location => location.UserId == user.Id);
        var eventTime = row.LocalTimestamp;
        // HTTP edits load their persisted snapshot in a fresh request scope, including PostgreSQL timestamp precision.
        await using var editDb = fixture.CreateContext();
        var edit = Controller(editDb, user.Id, cache, new Recorder());
        Assert.Equal(200, await ApplyProtectedPositionAsync(editDb, row, cache));
        await using var verify = fixture.CreateContext();
        var edited = await verify.Locations.SingleAsync(location => location.UserId == user.Id);
        Assert.Null(edited.CoordinateOrigin);
        Assert.Null(edited.ObservedAtUtc);
        Assert.Null(edited.ObservedAgeAtEventMs);
        Assert.Null(edited.PresenceObservedAtUtc);
        Assert.Equal(key, edited.IdempotencyKey);
        Assert.Equal(eventTime, edited.LocalTimestamp);
    }

    /// <summary>An edit loaded before admission must still clear a presence decision committed before its write.</summary>
    [PostgresFact]
    public async Task CoordinateEdit_RacingAdmission_CannotRetainPresence()
    {
        var user = await SeedAsync();
        var pause = new PauseAfterInsert();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        await using var intakeDb = fixture.CreateContext(pause);
        var intake = Controller(intakeDb, user.Id, cache, new Recorder());
        intake.Request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString("D");
        var first = intake.CheckIn(Dto(DateTime.UtcNow.AddSeconds(-1)));
        await pause.Inserted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            await using var editDb = fixture.CreateContext(new BeforeEditFence(async () =>
            {
                pause.Release.TrySetResult();
                Assert.IsType<OkObjectResult>(await first);
            }));
            var row = await editDb.Locations.SingleAsync(location => location.UserId == user.Id);
            Assert.Null(row.PresenceObservedAtUtc);
            var edit = Controller(editDb, user.Id, cache, new Recorder());
            Assert.Equal(200, await ApplyProtectedPositionAsync(editDb, row, cache));
            await using var verify = fixture.CreateContext();
            var edited = await verify.Locations.SingleAsync(location => location.Id == row.Id);
            Assert.Null(edited.CoordinateOrigin);
            Assert.Null(edited.PresenceObservedAtUtc);
        }
        finally { pause.Release.TrySetResult(); await first; }
    }

    /// <summary>Failed automatic visit tracking must not be retried by the later presence admission save.</summary>
    [PostgresFact]
    public async Task AutomaticVisitWriteFailure_DoesNotRetryDirtyEntriesDuringAdmission()
    {
        var user = await SeedAsync();
        await using var db = fixture.CreateContext(new FailCandidateWrite());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var controller = Controller(db, user.Id, cache, new Recorder());
        controller.Request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString("D");
        var response = Assert.IsType<OkObjectResult>(await controller.LogLocation(Dto(DateTime.UtcNow.AddSeconds(-1))));
        Assert.False(JsonSerializer.SerializeToElement(response.Value).GetProperty("skipped").GetBoolean());
        await using var verify = fixture.CreateContext();
        Assert.NotNull((await verify.Locations.SingleAsync(row => row.UserId == user.Id)).PresenceObservedAtUtc);
        Assert.False(await verify.PlaceVisitCandidates.AnyAsync(row => row.UserId == user.Id));
    }

    /// <summary>Real Group latest projections and protected-frame authorization share the persisted audience.</summary>
    private async Task AssertGroupPrivacyAsync(ApplicationDbContext db, HttpClient client, ApplicationUser owner, int? currentId)
    {
        var peer = await fixture.CreateUserAsync();
        var outsider = await fixture.CreateUserAsync();
        var group = TestDataFixtures.CreateGroup(owner, "Observation presence", "Friends");
        group.Owner = null;
        db.Groups.Add(group);
        db.GroupMembers.AddRange(new GroupMember { GroupId = group.Id, UserId = owner.Id, Role = "Owner", OrgPeerVisibilityAccessDisabled = true },
            new GroupMember { GroupId = group.Id, UserId = peer.Id });
        db.ApiTokens.AddRange(new ApiToken { User = null!, UserId = peer.Id, Name = "peer", Token = peer.Id },
            new ApiToken { User = null!, UserId = outsider.Id, Name = "outsider", Token = outsider.Id });
        await db.SaveChangesAsync();
        try
        {
            foreach (var caller in new[] { owner, peer, outsider })
            {
                client.DefaultRequestHeaders.Authorization = new("Bearer", caller.Id);
                using var response = await client.PostAsJsonAsync($"/api/mobile/groups/{group.Id}/locations/latest", new { includeUserIds = new[] { owner.Id } });
                Assert.Equal(caller == outsider ? HttpStatusCode.Forbidden : HttpStatusCode.OK, response.StatusCode);
                if (caller == outsider) continue;
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(caller == owner && currentId.HasValue ? 1 : 0, body.RootElement.GetArrayLength());
                if (body.RootElement.GetArrayLength() != 0) Assert.Equal(currentId, body.RootElement[0].GetProperty("id").GetInt32());
            }
            var timeline = new GroupTimelineService(db, new LocationService(db), new ConfigurationBuilder().Build());
            var context = await timeline.BuildAccessContextAsync(group.Id, owner.Id);
            var cookie = new GroupsController(db, Mock.Of<IGroupService>(), NullLogger<GroupsController>.Instance,
                new LocationService(db), new SseService(), timeline);
            cookie.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                { User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, owner.Id)], "cookie")) } };
            var result = Assert.IsType<OkObjectResult>(await cookie.Latest(group.Id, new GroupLocationsLatestRequest(), default));
            Assert.Equal(currentId.HasValue ? 1 : 0, Assert.IsAssignableFrom<IReadOnlyList<PublicLocationDto>>(result.Value).Count);
            using var provider = new ServiceCollection().AddScoped(_ => fixture.CreateContext()).BuildServiceProvider();
            var delivery = new GroupSseDeliveryLease(provider.GetRequiredService<IServiceScopeFactory>());
            var frame = JsonSerializer.Serialize(GroupSseEventDto.Location(currentId ?? 1, DateTime.UtcNow, owner.Id, owner.UserName!, true, null));
            foreach (var caller in new[] { owner, peer, outsider })
            {
                var decision = await delivery.AcquireEventAsync(group.Id, caller.Id, frame, default);
                await using var lease = decision.Lease;
                Assert.Equal(caller == owner, decision.Allowed);
                Assert.Equal(caller != outsider, lease != null);
            }
        }
        finally { await db.Groups.Where(row => row.Id == group.Id).ExecuteDeleteAsync(); }
    }

    /// <summary>Seeds fixture-owned users, bearer token and a spatial Place for the real two-hit detector.</summary>
    private async Task<ApplicationUser> SeedAsync()
    {
        var user = await fixture.CreateUserAsync();
        var trip = TestDataFixtures.CreateTrip(user.Id, "Observation presence");
        fixture.RegisterTrip(trip.Id);
        var region = TestDataFixtures.CreateRegion(trip);
        var place = TestDataFixtures.CreatePlace(region, latitude: 10, longitude: 20);
        await using var db = fixture.CreateContext();
        db.AddRange(trip, region, place);
        db.ApiTokens.Add(new ApiToken { User = null!, UserId = user.Id, Name = "presence", Token = user.Id });
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>Uses real visit persistence, shared cache and captured broadcast calls without provider contact.</summary>
    private static LocationController Controller(ApplicationDbContext db, string owner, IMemoryCache cache, SseService sse)
    {
        var settings = new Mock<IApplicationSettingsService>();
        settings.Setup(service => service.GetSettings()).Returns(new ApplicationSettings { Id = 1 });
        var locations = new LocationService(db);
        var visits = new PlaceVisitDetectionService(db, settings.Object, sse, NullLogger<PlaceVisitDetectionService>.Instance);
        var controller = new LocationController(db, NullLogger<BaseApiController>.Instance, cache, settings.Object,
            new ReverseGeocodingService(new HttpClient(), NullLogger<BaseApiController>.Instance), locations, sse,
            new LocationStatsService(db), locations, visits, new ApiWorkAdmission(64, 8));
        controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };
        controller.Request.Headers.Authorization = $"Bearer {owner}";
        return controller;
    }

    /// <summary>Freezes a self-consistent sensor/event snapshot at millisecond precision.</summary>
    private static GpsLoggerLocationDto Dto(DateTime eventTime)
    {
        eventTime = new DateTime(eventTime.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
        return new GpsLoggerLocationDto { Latitude = 10, Longitude = 20, Timestamp = eventTime, Accuracy = 5,
            CoordinateOrigin = "sensor", ObservedAtUtc = eventTime.AddSeconds(-1), ObservedAgeAtEventMs = 1000, IsUserInvoked = true };
    }

    /// <summary>Asserts the existing routed check-in success envelope and returns its immutable UUID winner.</summary>
    private static async Task<int> SendAsync(HttpClient client, GpsLoggerLocationDto dto, Guid key)
    {
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", key.ToString("D"));
        using var response = await client.PostAsJsonAsync("/api/location/check-in", dto);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("location").GetProperty("id").GetInt32();
    }

    /// <summary>Records production broadcast calls; this is not evidence of native notification delivery.</summary>
    private sealed class Recorder : SseService
    {
        internal List<(string Channel, string Data)> Events { get; } = [];
        public override Task BroadcastAsync(string channel, string data) { Events.Add((channel, data)); return Task.CompletedTask; }
    }

    /// <summary>Models an actual best-effort candidate write failure without failing Location persistence.</summary>
    private sealed class FailCandidateWrite : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (data.Context!.ChangeTracker.Entries<PlaceVisitCandidate>().Any(entry => entry.State == EntityState.Added))
                throw new DbUpdateException("Synthetic visit candidate write failure");
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Lets actual admission complete after the edit snapshot, before the editor acquires its account fence.</summary>
    private sealed class BeforeEditFence(Func<Task> admit) : DbCommandInterceptor
    {
        private bool _admitted;

        /// <summary>Models a committed competing admission without waiting while holding the editor's SQL locks.</summary>
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(System.Data.Common.DbCommand command,
            CommandEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (!_admitted && command.CommandText.Contains(", 838)", StringComparison.Ordinal))
            {
                _admitted = true;
                await admit();
            }
            return result;
        }
    }

    /// <summary>Pauses a committed keyed insert before optional enrichment and postwrite effects.</summary>
    private sealed class PauseAfterInsert : DbTransactionInterceptor
    {
        internal TaskCompletionSource Inserted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction, TransactionEndEventData data, CancellationToken ct = default)
        {
            if (data.Context!.ChangeTracker.Entries<Location>().Any() && !Inserted.Task.IsCompleted)
            { Inserted.TrySetResult(); await Release.Task.WaitAsync(ct); }
        }
    }
    /// <summary>Production protected effect seam; routed owner/receipt authority is covered by LocationMutationPostgresTests.</summary>
    private static async Task<int> ApplyProtectedPositionAsync(ApplicationDbContext db, Location row, IMemoryCache cache)
    {
        var owner = row.UserId; var id = row.Id;
        db.ChangeTracker.Clear();
        var protection = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
        object? snapshot;
        await using (var read = await db.Database.BeginTransactionAsync())
        {
            snapshot = await HistoryDetail.ReadAsync(db, owner, id, NodaTime.DateTimeZone.Utc, default, protection);
            await read.RollbackAsync();
        }
        var request = await ProtectedLocationRequest.CreateAsync(null!, id,
            new { position = new { op = "set", latitude = 11.0, longitude = 21.0 } }, detail: JsonSerializer.SerializeToElement(snapshot));
        using var http = new HttpClient();
        var reverse = new ReverseGeocodingService(http, NullLogger<BaseApiController>.Instance);
        var service = new Wayfarer.Services.LocationMutations.LocationMutationService(db, protection, reverse, cache)
        { Authorized = _ => Task.FromResult(true) };
        return (await service.ExecuteAsync(owner, id, request.BaseId, request.ActionId,
            Wayfarer.Services.LocationMutations.LocationMutationCommand.Parse(request.Bytes), request.Bytes, default)).Status;
    }

}
