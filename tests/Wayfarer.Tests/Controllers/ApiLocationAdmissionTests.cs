using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Point = NetTopologySuite.Geometries.Point;
using Wayfarer.Tests.Mocks;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Wayfarer.Util;
using System.Text;
using System.Text.Json;

namespace Wayfarer.Tests.Controllers;

/// <summary>Exercises admission at real ingestion actions, including post-write failure and replay ordering.</summary>
public class ApiLocationAdmissionTests : TestBase
{
    /// <summary>The documented GPSLogger substitutions bind as real JSON and save a point through production MVC.</summary>
    [Fact]
    public async Task GpsLoggerDocumentedBody_SavesThroughHttp()
    {
        var db = CreateDbContext();
        var user = Seed(db);
        var token = new ApiTokenService(db, null!).GenerateToken();
        var row = db.ApiTokens.First(item => item.UserId == user.Id);
        row.Token = null;
        row.TokenHash = ApiTokenService.HashToken(token);
        await db.SaveChangesAsync();
        var controller = Controller(db, new ApiWorkAdmission(64, 8));
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddSingleton(controller);
            services.AddControllers().AddControllersAsServices();
        });
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://wayfarer.test");
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var body = """
            { "latitude": %LAT, "longitude": %LON, "timestamp": "%TIME" }
            """.Replace("%LAT", "37.98").Replace("%LON", "23.73").Replace("%TIME", "2026-10-07T12:00:00.123Z");
        using var response = await client.PostAsync("/api/location/log-location", new StringContent(body, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(result.RootElement.GetProperty("skipped").GetBoolean());
        var saved = Assert.Single(db.Locations);
        Assert.Equal(user.Id, saved.UserId);
        Assert.Equal(37.98, saved.Coordinates.Y);
        Assert.Equal(23.73, saved.Coordinates.X);
        Assert.Equal(new DateTime(2026, 10, 7, 12, 0, 0, 123, DateTimeKind.Utc), saved.LocalTimestamp);
        Assert.Equal(saved.Id, result.RootElement.GetProperty("locationId").GetInt32());
    }

    /// <summary>Both actions reject new work but replay stored keys before a full user gate.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ReplayAndMalformedKeysPrecedeOverload(bool checkIn, bool global)
    {
        var db = CreateDbContext();
        var user = Seed(db);
        var key = Guid.NewGuid();
        db.Locations.Add(new Location { UserId = user.Id, IdempotencyKey = key,
            Coordinates = new Point(20, 10) { SRID = 4326 }, TimeZoneId = "UTC",
            Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var gate = new ApiWorkAdmission(global ? 1 : 64, global ? 8 : 1);
        using var held = gate.TryAcquire(user.Id, out _);
        var visits = new Mock<IPlaceVisitDetectionService>(MockBehavior.Strict);
        var sse = new Mock<SseService>(MockBehavior.Strict);
        var controller = Controller(db, gate, visits.Object, sse.Object);
        var invoke = () => checkIn ? controller.CheckIn(Dto()) : controller.LogLocation(Dto());
        controller.Request.Headers["Idempotency-Key"] = key.ToString();
        Assert.IsType<OkObjectResult>(await invoke());
        controller.Request.Headers["Idempotency-Key"] = "malformed";
        Assert.IsType<BadRequestObjectResult>(await invoke());
        controller.Request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString();
        var denied = Assert.IsType<ObjectResult>(await invoke());
        Assert.Equal(global ? 503 : 429, denied.StatusCode);
        Assert.Equal("12", controller.Response.Headers.RetryAfter);
        Assert.Single(db.Locations);
        visits.VerifyNoOtherCalls();
        sse.VerifyNoOtherCalls();
    }

    /// <summary>Eight mixed device/manual/queue operations share one user gate through SSE completion.</summary>
    [Fact]
    public async Task MixedEndpointsAndTokensShareActiveWork()
    {
        var gate = new ApiWorkAdmission(64, 8);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sse = new Mock<SseService>();
        sse.Setup(x => x.BroadcastAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(release.Task);
        var requests = new List<Task<IActionResult>>();
        try
        {
            for (var i = 0; i < 8; i++)
            {
                var db = CreateDbContext();
                Seed(db);
                var controller = Controller(db, gate, sse: sse.Object);
                controller.Request.Headers.Authorization = i % 2 == 0 ? "Bearer phone" : "Bearer tablet";
                requests.Add(i % 3 == 0 ? controller.LogLocation(Dto()) : controller.CheckIn(Dto()));
            }
            Assert.All(requests, request => Assert.False(request.IsCompleted));
            var deniedDb = CreateDbContext();
            Seed(deniedDb);
            var deniedController = Controller(deniedDb, gate);
            Assert.Equal(429, Assert.IsType<ObjectResult>(await deniedController.CheckIn(Dto())).StatusCode);
            Assert.Empty(deniedDb.Locations);
        }
        finally { release.TrySetResult(); }
        Assert.All(await Task.WhenAll(requests), result => Assert.IsType<OkObjectResult>(result));
        Assert.Equal(0, gate.IdentityCount);
    }

    /// <summary>Validation, cancelled post-write work and SSE failures release both endpoint reservations.</summary>
    [Theory]
    [InlineData(false, "validation")]
    [InlineData(true, "validation")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "sse")]
    [InlineData(true, "sse")]
    public async Task EarlyReturnAndPostWriteFailureRelease(bool checkIn, string outcome)
    {
        var db = CreateDbContext();
        Seed(db);
        var gate = new ApiWorkAdmission(64, 8);
        var sse = new Mock<SseService>();
        sse.Setup(x => x.BroadcastAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(outcome == "cancel" ? new OperationCanceledException() : new InvalidOperationException());
        var controller = Controller(db, gate, sse: sse.Object);
        var dto = outcome == "validation" ? new GpsLoggerLocationDto() : Dto();
        var invoke = () => checkIn ? controller.CheckIn(dto) : controller.LogLocation(dto);
        if (outcome == "validation") Assert.IsType<BadRequestObjectResult>(await invoke());
        else if (outcome == "cancel") await Assert.ThrowsAsync<OperationCanceledException>(invoke);
        else await Assert.ThrowsAsync<InvalidOperationException>(invoke);
        Assert.Equal(0, gate.IdentityCount);
    }

    /// <summary>Denied ingestion never contacts a configured provider; provider failure releases accepted work.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderFailureAndDeniedContact(bool checkIn)
    {
        var db = CreateDbContext();
        var user = Seed(db);
        var credentials = CredentialTestFactory.Create(new EphemeralDataProtectionProvider());
        var profile = PersonalLocationProviderProfile.Create(user.Id, PersonalLocationProvider.Geoapify);
        credentials.Replace(profile, "synthetic");
        profile.SetAuthorization(PersonalProviderCapability.Geocoding, true);
        credentials.RecordVerification(profile, PersonalProviderCapability.Geocoding, PersonalProviderVerification.Verified);
        db.Add(profile);
        db.Add(new PersonalLocationProviderSelection { UserId = user.Id, GeocodingProviderKey = "geoapify" });
        await db.SaveChangesAsync();
        var providerGate = new PersonalProviderContactGate(db, credentials,
            new LegacyMapboxMigrationService(db, credentials), new ConfigurationBuilder().Build());
        var handler = new FailingProvider();
        var reverse = new ReverseGeocodingService(new HttpClient(handler), NullLogger<BaseApiController>.Instance, providerGate, db);
        var gate = new ApiWorkAdmission(64, 1);
        var controller = Controller(db, gate, reverse: reverse);
        using (gate.TryAcquire(user.Id, out _))
        {
            var denied = checkIn ? await controller.CheckIn(Dto()) : await controller.LogLocation(Dto());
            Assert.Equal(429, Assert.IsType<ObjectResult>(denied).StatusCode);
            Assert.Equal(0, handler.Calls);
        }
        // Existing enrichment failures fall back to an unenriched persisted location.
        Assert.IsType<OkObjectResult>(checkIn ? await controller.CheckIn(Dto()) : await controller.LogLocation(Dto()));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, gate.IdentityCount);
    }

    /// <summary>Location mutation bearer branches gain the same inactive-user 401 before ownership lookup.</summary>
    [Fact]
    public async Task InactivePutAndDeleteAreUnauthorized()
    {
        var db = CreateDbContext();
        var user = Seed(db);
        user.IsActive = false;
        await db.SaveChangesAsync();
        var controller = Controller(db, new ApiWorkAdmission(64, 8));
        Assert.IsType<UnauthorizedObjectResult>(await controller.Delete(999));
        Assert.IsType<UnauthorizedObjectResult>(await controller.Update(999, new LocationUpdateRequestDto()));
    }

    /// <summary>Simulates a transient upstream failure without contacting a real provider.</summary>
    private sealed class FailingProvider : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new HttpRequestException("Synthetic upstream failure");
        }
    }

    /// <summary>Builds the existing product controller with isolated admission and optional side-effect seams.</summary>
    internal static LocationController Controller(ApplicationDbContext db, ApiWorkAdmission gate,
        IPlaceVisitDetectionService? visits = null, SseService? sse = null, ReverseGeocodingService? reverse = null)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var locations = new LocationService(db);
        var settings = new Mock<IApplicationSettingsService>();
        settings.Setup(x => x.GetSettings()).Returns(new ApplicationSettings { Id = 1 });
        var controller = new LocationController(db, NullLogger<BaseApiController>.Instance, cache,
            settings.Object,
            reverse ?? new ReverseGeocodingService(new HttpClient(), NullLogger<BaseApiController>.Instance),
            locations, sse ?? new SseService(), new LocationStatsService(db), locations,
            visits ?? new NullPlaceVisitDetectionService(), gate);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.Request.Headers.Authorization = "Bearer phone";
        return controller;
    }

    /// <summary>Seeds two named tokens owned by one active account.</summary>
    private static ApplicationUser Seed(ApplicationDbContext db)
    {
        var user = TestDataFixtures.CreateUser(id: "shared-user");
        db.Users.Add(user);
        db.ApplicationSettings.Add(new ApplicationSettings { Id = 1 });
        db.ApiTokens.AddRange(new ApiToken { User = user, UserId = user.Id, Name = "phone", Token = "phone" },
            new ApiToken { User = user, UserId = user.Id, Name = "tablet", Token = "tablet" });
        db.SaveChanges();
        return user;
    }

    /// <summary>Creates a valid timestamped GPS sample without changing production thresholds.</summary>
    internal static GpsLoggerLocationDto Dto() => new()
        { Latitude = 10, Longitude = 20, Accuracy = 5, Timestamp = DateTime.UtcNow };
}
