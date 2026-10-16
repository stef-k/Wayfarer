using System.Net;
using System.Text;
using System.Text.Json;
using System.Data.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Services.LocationEnrichment;
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
        var sse = new Mock<SseService>();
        sse.Setup(service => service.BroadcastAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(async (string _, string payload) =>
            {
                await using var db = fixture.CreateContext();
                var published = await db.Locations.AsNoTracking().SingleAsync(row => row.UserId == userId);
                using var json = JsonDocument.Parse(payload);
                Assert.Equal(published.Id, json.RootElement.GetProperty("locationId").GetInt32());
                Assert.Equal("Controlled address", published.FullAddress);
            });
        await WithHostAsync(protection, handler, [barrier], async client =>
        {
            var key = Guid.NewGuid();
            using var first = Request(route, token, key);
            using var second = Request(route, token, key);
            var responses = await Task.WhenAll(client.SendAsync(first), client.SendAsync(second));
            await using var db = fixture.CreateContext();
            var winner = Assert.Single(await db.Locations.AsNoTracking().Where(row => row.UserId == userId).ToListAsync());
            var addresses = new List<string?>();
            foreach (var response in responses)
            {
                using (response)
                {
                    await AssertResponseAsync(response, route, winner.Id);
                    if (route == "check-in")
                    {
                        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                        addresses.Add(json.RootElement.GetProperty("location").GetProperty("fullAddress").GetString());
                    }
                }
            }
            if (route == "check-in") Assert.Contains("Controlled address", addresses);
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
        }, sse.Object);
        sse.Verify(service => service.BroadcastAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    /// <summary>Provider failure retains an eligible blank row; ordinary replay never substitutes a recovery contact.</summary>
    [PostgresTheory(Timeout = 30000)]
    [InlineData("check-in")]
    [InlineData("log-location")]
    public async Task ProviderFailure_PreservesWinnerAndExplicitRecoveryEligibility(string route)
    {
        var protection = new EphemeralDataProtectionProvider();
        var (userId, token) = await SeedAsync("geoapify", protection);
        using var handler = new ControlledProvider
        {
            BeforeResponse = _ => throw new HttpRequestException("Synthetic provider failure")
        };
        await WithHostAsync(protection, handler, [], async client =>
        {
            var key = Guid.NewGuid();
            using var request = Request(route, token, key);
            using var response = await client.SendAsync(request);
            await using var db = fixture.CreateContext();
            var saved = await LocationEnrichmentProgressQuery.WhollyUnenriched(
                db.Locations.Where(row => row.UserId == userId)).SingleAsync();
            await AssertResponseAsync(response, route, saved.Id);
            using var replayRequest = Request(route, token, key);
            using var replay = await client.SendAsync(replayRequest);
            await AssertResponseAsync(replay, route, saved.Id);
            Assert.Equal(1, handler.Calls);
            Assert.Equal(1, await AdmissionCountAsync(userId, "geoapify"));
            Assert.False(await db.LocationEnrichmentAttempts.AnyAsync(row => row.UserId == userId));
            Assert.False(await db.LocationEnrichmentWorkflows.AnyAsync(row => row.UserId == userId));
        });
    }

    /// <summary>Request cancellation after transport entry propagates while its committed row remains replayable.</summary>
    [PostgresFact(Timeout = 30000)]
    public async Task ProviderCancellation_PreservesWinnerWithoutSuccessProvenance()
    {
        var protection = new EphemeralDataProtectionProvider();
        var (userId, token) = await SeedAsync("geoapify", protection);
        using var cancellation = new CancellationTokenSource();
        using var handler = new ControlledProvider { BeforeResponse = ct =>
        {
            cancellation.Cancel();
            return Task.FromCanceled(ct);
        } };
        await using var db = fixture.CreateContext();
        var credentials = CredentialTestFactory.Create(protection);
        var gate = new PersonalProviderContactGate(db, credentials, new LegacyMapboxMigrationService(db, credentials),
            new ConfigurationBuilder().Build());
        var reverse = new ReverseGeocodingService(new HttpClient(handler), NullLogger<BaseApiController>.Instance, gate, db);
        var controller = ApiLocationAdmissionTests.Controller(db, new ApiWorkAdmission(64, 8), reverse: reverse);
        var key = Guid.NewGuid();
        controller.Request.Headers.Authorization = $"Bearer {token}";
        controller.Request.Headers["Idempotency-Key"] = key.ToString("D");
        controller.HttpContext.RequestAborted = cancellation.Token;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => controller.CheckIn(ApiLocationAdmissionTests.Dto()));
        var saved = await LocationEnrichmentProgressQuery.WhollyUnenriched(
            db.Locations.AsNoTracking().Where(row => row.UserId == userId)).SingleAsync();
        await WithHostAsync(protection, handler, [], async client =>
        {
            using var request = Request("log-location", token, key);
            using var replay = await client.SendAsync(request);
            await AssertResponseAsync(replay, "log-location", saved.Id);
        });
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, await AdmissionCountAsync(userId, "geoapify"));
    }

    /// <summary>Owner edits during HTTP and authority changes after service revalidation supersede a stale result.</summary>
    [PostgresTheory(Timeout = 30000)]
    [InlineData("owner-edit", "geoapify")]
    [InlineData("provider-result", "geoapify")]
    [InlineData("credential", "geoapify")]
    [InlineData("selection", "geoapify")]
    [InlineData("consent", "mapbox")]
    public async Task StaleResult_DoesNotOverwriteCurrentRowOrAuthority(string mutation, string provider)
    {
        var protection = new EphemeralDataProtectionProvider();
        var (userId, token) = await SeedAsync(provider, protection);
        async Task MutateAsync()
        {
            await using var edit = fixture.CreateContext();
            var row = await edit.Locations.SingleAsync(item => item.UserId == userId);
            if (mutation is "owner-edit" or "provider-result")
            {
                if (mutation == "owner-edit") row.Coordinates = new(21, 11) { SRID = 4326 };
                row.FullAddress = "Owner address";
                row.ReverseGeocodingProvider = mutation == "owner-edit" ? "manual" : "geoapify";
                row.ReverseGeocodingStorageMode = mutation == "owner-edit" ? null : "persistent";
                row.ReverseGeocodedAt = mutation == "owner-edit" ? null : new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
                row.Notes = "Owner notes";
            }
            else if (mutation == "selection")
                (await edit.PersonalLocationProviderSelections.SingleAsync(item => item.UserId == userId))
                    .Select(PersonalProviderCapability.Geocoding, null);
            else
            {
                var profile = await edit.PersonalLocationProviderProfiles.SingleAsync(item => item.UserId == userId);
                if (mutation == "consent") profile.ClearPermanentGeocodingConsent();
                else CredentialTestFactory.Create(protection).Replace(profile, "replacement-synthetic");
            }
            await edit.SaveChangesAsync();
        }
        using var handler = new ControlledProvider();
        var rowEdited = mutation is "owner-edit" or "provider-result";
        if (rowEdited) handler.BeforeResponse = _ => MutateAsync();
        var lateMutation = new BeforeAddressPersistence(MutateAsync);
        await WithHostAsync(protection, handler, rowEdited ? [] : [lateMutation], async client =>
        {
            using var request = Request("check-in", token, Guid.NewGuid());
            using var response = await client.SendAsync(request);
            await using var db = fixture.CreateContext();
            var saved = await db.Locations.AsNoTracking().SingleAsync(row => row.UserId == userId);
            await AssertResponseAsync(response, "check-in", saved.Id);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(saved.FullAddress, json.RootElement.GetProperty("location").GetProperty("fullAddress").GetString());
            Assert.Equal(rowEdited ? "Owner address" : null, saved.FullAddress);
            Assert.Equal(rowEdited ? mutation == "owner-edit" ? "manual" : "geoapify" : null, saved.ReverseGeocodingProvider);
            Assert.Equal(mutation == "provider-result" ? new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero) : (DateTimeOffset?)null,
                saved.ReverseGeocodedAt);
            if (mutation == "owner-edit")
            {
                Assert.Equal((21d, 11d), (saved.Coordinates.X, saved.Coordinates.Y));
                Assert.Equal("Owner notes", saved.Notes);
            }
            else if (!rowEdited) Assert.True(lateMutation.Applied);
            Assert.Equal(1, handler.Calls);
            Assert.Equal(1, await AdmissionCountAsync(userId, provider));
        });
    }

    /// <summary>Distinct UUIDs, another owner using the same UUID and keyless legacy creates charge independently.</summary>
    [PostgresFact(Timeout = 30000)]
    public async Task IndependentCreates_KeepOwnerIsolationAndKeylessBehavior()
    {
        var protection = new EphemeralDataProtectionProvider();
        var first = await SeedAsync("geoapify", protection);
        var other = await SeedAsync("geoapify", protection);
        using var handler = new ControlledProvider();
        await WithHostAsync(protection, handler, [], async client =>
        {
            var key = Guid.NewGuid();
            foreach (var sample in new[] { (first.Token, (Guid?)key), (first.Token, (Guid?)Guid.NewGuid()),
                (other.Token, (Guid?)key), (first.Token, (Guid?)null) })
            {
                using var request = Request("check-in", sample.Token, sample.Item2);
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            using var deniedRequest = Request("check-in", "invalid-token", Guid.NewGuid());
            using var denied = await client.SendAsync(deniedRequest);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            await using var db = fixture.CreateContext();
            Assert.Equal(3, await db.Locations.CountAsync(row => row.UserId == first.UserId));
            Assert.Equal(1, await db.Locations.CountAsync(row => row.UserId == other.UserId));
            Assert.Equal(3, await AdmissionCountAsync(first.UserId, "geoapify"));
            Assert.Equal(1, await AdmissionCountAsync(other.UserId, "geoapify"));
            Assert.Equal(4, handler.Calls);
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
        IInterceptor[] interceptors, Func<HttpClient, Task> run, SseService? sse = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"wayfarer-822-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var seed = fixture.CreateContext();
            var workAdmission = new ApiWorkAdmission(64, 8);
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
                    workAdmission, sse: sse, reverse: sp.GetRequiredService<ReverseGeocodingService>()));
                services.AddControllers().AddControllersAsServices();
            });
            using var client = app.GetTestClient();
            client.BaseAddress = new Uri("https://wayfarer.test");
            await run(client);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Mutates authority after reverse-service revalidation but before final persistence locks are acquired.</summary>
    private sealed class BeforeAddressPersistence(Func<Task> mutate) : DbCommandInterceptor
    {
        public bool Applied { get; private set; }

        /// <summary>Uses a deterministic command boundary, leaving provider admission and HTTP untouched.</summary>
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Applied && command.CommandText.Contains("FOR SHARE", StringComparison.Ordinal))
            {
                Applied = true;
                await mutate();
            }
            return result;
        }
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
        /// <summary>Optional coordinated failure or owner edit at the actual transport boundary.</summary>
        public Func<CancellationToken, Task>? BeforeResponse { get; set; }
        /// <summary>Counts actual HTTP-handler entries independently of durable usage records.</summary>
        public int Calls => Volatile.Read(ref _calls);

        /// <summary>Checks the selected provider product and supplies a deterministic complete address result.</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (BeforeResponse != null) await BeforeResponse(cancellationToken);
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
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
}
