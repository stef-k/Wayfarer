using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;
using Xunit.Abstractions;

namespace Wayfarer.Tests.Controllers;

/// <summary>Exercises keyed creation through authenticated MVC, PostgreSQL and real provider admission.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class ApiLocationProviderCreatePostgresTests(PostgresImportTestFixture fixture, ITestOutputHelper output)
{
    /// <summary>Only the inserted winner may charge or contact; persisted cross-endpoint replay does neither.</summary>
    [PostgresTheory(Timeout = 30000)]
    [InlineData("log-location", "geoapify")]
    [InlineData("check-in", "mapbox")]
    public async Task ConcurrentFirstUse_ContactsOnlyForPersistedWinner(string route, string provider)
    {
        var protection = new EphemeralDataProtectionProvider();
        var (userId, token) = await SeedAsync(provider, protection);
        var barrier = new InsertControl(barrier: true);
        using var handler = new ControlledProvider();
        await WithHostAsync(protection, handler, [barrier], async client =>
        {
            var key = Guid.NewGuid();
            using var first = Request(route, token, key);
            using var second = Request(route, token, key);
            var responses = await Task.WhenAll(client.SendAsync(first), client.SendAsync(second));
            await using var db = fixture.CreateContext();
            var winner = Assert.Single(await db.Locations.AsNoTracking().Where(row => row.UserId == userId).ToListAsync());
            foreach (var response in responses)
            {
                using (response) await AssertResponseAsync(response, route, winner.Id);
            }
            var admissions = await AdmissionCountAsync(userId, provider);
            output.WriteLine("{0}: Locations=1, admissions={1}, HTTP contacts={2}", route, admissions, handler.Calls);
            Assert.Equal(1, admissions);
            Assert.Equal(1, handler.Calls);
            Assert.Equal(key, winner.IdempotencyKey);
            Assert.Equal("Controlled address", winner.FullAddress);
            Assert.Equal(provider, winner.ReverseGeocodingProvider);
            Assert.Equal(provider == "geoapify" ? "persistent" : "permanent", winner.ReverseGeocodingStorageMode);
            Assert.NotNull(winner.ReverseGeocodedAt);
            using var replayRequest = Request(route == "check-in" ? "log-location" : "check-in", token, key);
            using var replay = await client.SendAsync(replayRequest);
            await AssertResponseAsync(replay, route == "check-in" ? "log-location" : "check-in", winner.Id);
            Assert.Equal(1, handler.Calls);
            Assert.Equal(1, await AdmissionCountAsync(userId, provider));
            Assert.Equal(1, await db.Locations.CountAsync(row => row.UserId == userId));
        });
    }

    /// <summary>A failed initial insert must leave no charged contact; the identical retry may enrich once.</summary>
    [PostgresTheory(Timeout = 30000)]
    [InlineData("check-in", "geoapify")]
    [InlineData("log-location", "mapbox")]
    public async Task InitialInsertFailure_DoesNotContactBeforeRetry(string route, string provider)
    {
        var protection = new EphemeralDataProtectionProvider();
        var (userId, token) = await SeedAsync(provider, protection);
        using var handler = new ControlledProvider();
        await WithHostAsync(protection, handler, [new InsertControl(failFirst: true)], async client =>
        {
            var key = Guid.NewGuid();
            using var failedRequest = Request(route, token, key);
            using var failed = await client.SendAsync(failedRequest);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            await using var db = fixture.CreateContext();
            Assert.False(await db.Locations.AnyAsync(row => row.UserId == userId));
            var admissions = await AdmissionCountAsync(userId, provider);
            output.WriteLine("Failed insert: Locations=0, admissions={0}, HTTP contacts={1}", admissions, handler.Calls);
            Assert.Equal(0, admissions);
            Assert.Equal(0, handler.Calls);
            using var retryRequest = Request(route, token, key);
            using var retry = await client.SendAsync(retryRequest);
            var winner = await db.Locations.AsNoTracking().SingleAsync(row => row.UserId == userId);
            await AssertResponseAsync(retry, route, winner.Id);
            Assert.Equal("Controlled address", winner.FullAddress);
            Assert.Equal(1, await AdmissionCountAsync(userId, provider));
            Assert.Equal(1, handler.Calls);
        });
    }

    /// <summary>Creates only fixture-owned identities, hash-only API tokens and synthetic verified credentials.</summary>
    private async Task<(string UserId, string Token)> SeedAsync(string provider, IDataProtectionProvider protection)
    {
        var user = await fixture.CreateUserAsync();
        var token = new ApiTokenService(null!, null!).GenerateToken();
        await using var db = fixture.CreateContext();
        db.ApiTokens.Add(new ApiToken { User = null!, UserId = user.Id, Name = "822-test",
            TokenHash = ApiTokenService.HashToken(token) });
        var profile = PersonalLocationProviderProfile.Create(user.Id, provider == "geoapify"
            ? PersonalLocationProvider.Geoapify : PersonalLocationProvider.Mapbox);
        var credentials = CredentialTestFactory.Create(protection);
        credentials.Replace(profile, "synthetic-822");
        profile.SetAuthorization(PersonalProviderCapability.Geocoding, true);
        if (provider == "mapbox") profile.GrantPermanentGeocodingConsent(DateTimeOffset.UtcNow);
        credentials.RecordVerification(profile, PersonalProviderCapability.Geocoding, PersonalProviderVerification.Verified);
        db.PersonalLocationProviderProfiles.Add(profile);
        db.PersonalLocationProviderSelections.Add(new() { UserId = user.Id, GeocodingProviderKey = provider });
        await db.SaveChangesAsync();
        return (user.Id, token);
    }

    /// <summary>Uses real request-scoped contexts/controllers and substitutes only external transport and noncritical work.</summary>
    private async Task WithHostAsync(IDataProtectionProvider protection, ControlledProvider handler,
        IInterceptor[] interceptors, Func<HttpClient, Task> run)
    {
        var root = Path.Combine(Path.GetTempPath(), $"wayfarer-822-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var seed = fixture.CreateContext();
            await using var app = await IdentityRouteHost.StartAsync(seed, root, services =>
            {
                services.RemoveAll<ApplicationDbContext>();
                services.AddScoped(_ => fixture.CreateContext(interceptors));
                services.AddSingleton(CredentialTestFactory.Create(protection));
                services.AddScoped(sp =>
                {
                    var db = sp.GetRequiredService<ApplicationDbContext>();
                    var credentials = sp.GetRequiredService<PersonalProviderCredentialService>();
                    var gate = new PersonalProviderContactGate(db, credentials,
                        new LegacyMapboxMigrationService(db, credentials), new ConfigurationBuilder().Build());
                    return new ReverseGeocodingService(new HttpClient(handler, disposeHandler: false),
                        NullLogger<BaseApiController>.Instance, gate, db);
                });
                services.AddScoped(sp => ApiLocationAdmissionTests.Controller(sp.GetRequiredService<ApplicationDbContext>(),
                    new ApiWorkAdmission(64, 8), reverse: sp.GetRequiredService<ReverseGeocodingService>()));
                services.AddControllers().AddControllersAsServices();
            });
            using var client = app.GetTestClient();
            client.BaseAddress = new Uri("https://wayfarer.test");
            await run(client);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Counts durable provider-native charges independently of synthetic transport calls.</summary>
    private async Task<int> AdmissionCountAsync(string userId, string provider)
    {
        await using var db = fixture.CreateContext();
        return provider == "geoapify"
            ? await db.GeoapifyUsageAdmissions.Where(row => row.UserId == userId).SumAsync(row => row.Credits)
            : await db.MapboxProductMeters.Where(row => row.UserId == userId).SumAsync(row => row.AdmittedCount);
    }

    /// <summary>Creates the published JSON request with an owner token and a stable logical capture key.</summary>
    private static HttpRequestMessage Request(string route, string token, Guid? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/location/{route}");
        request.Headers.Authorization = new("Bearer", token);
        if (key.HasValue) request.Headers.Add("Idempotency-Key", key.Value.ToString("D"));
        request.Content = new StringContent("""
            {"latitude":10,"longitude":20,"accuracy":5,"timestamp":"2026-12-01T12:00:00Z"}
            """, Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>Asserts endpoint-specific field names and the same durable destination identity.</summary>
    private static async Task AssertResponseAsync(HttpResponseMessage response, string route, int id)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = json.RootElement;
        if (route == "check-in")
        {
            Assert.Equal(new[] { "message", "location" }, body.EnumerateObject().Select(item => item.Name));
            Assert.Equal("Check-in logged successfully", body.GetProperty("message").GetString());
            Assert.Equal(id, body.GetProperty("location").GetProperty("id").GetInt32());
        }
        else
        {
            Assert.Equal(new[] { "success", "skipped", "locationId" }, body.EnumerateObject().Select(item => item.Name));
            Assert.True(body.GetProperty("success").GetBoolean());
            Assert.False(body.GetProperty("skipped").GetBoolean());
            Assert.Equal(id, body.GetProperty("locationId").GetInt32());
        }
    }

    /// <summary>Coordinates or fails only initial Location saves, never provider admission saves.</summary>
    private sealed class InsertControl(bool barrier = false, bool failFirst = false) : SaveChangesInterceptor
    {
        private int _arrived;
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Releases the two first inserts together or injects exactly one pre-insert failure.</summary>
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!eventData.Context!.ChangeTracker.Entries<Location>().Any(row => row.State == EntityState.Added))
                return result;
            var count = Interlocked.Increment(ref _arrived);
            if (failFirst && count == 1) throw new DbUpdateException("Synthetic initial Location insert failure");
            if (barrier)
            {
                if (count == 2) _both.TrySetResult();
                await _both.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            return result;
        }
    }

    /// <summary>Counts real transport entry and returns valid provider payloads without any network access.</summary>
    private sealed class ControlledProvider : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        /// <summary>Checks the selected provider product and supplies a deterministic complete address result.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var geoapify = request.RequestUri!.Host == "api.geoapify.com";
            Assert.True(geoapify || request.RequestUri.Host == "api.mapbox.com");
            if (!geoapify) Assert.Contains("permanent=true", request.RequestUri.Query);
            var json = geoapify ? """
                {"type":"FeatureCollection","features":[{"type":"Feature","properties":{
                "formatted":"Controlled address","address_line1":"12 Main Street","housenumber":"12",
                "street":"Main Street","postcode":"12345","city":"Town","state":"Region","country":"Country"}}]}
                """ : """
                {"type":"FeatureCollection","features":[{"type":"Feature","id":"controlled","properties":{
                "feature_type":"street","full_address":"Controlled address","name":"Main Street",
                "context":{"address":{"address_number":"12","street_name":"Main Street"},
                "postcode":{"name":"12345"},"place":{"name":"Town"},"region":{"name":"Region"},"country":{"name":"Country"}}}}]}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
