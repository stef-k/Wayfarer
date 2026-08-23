using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Npgsql;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Tests.Services;

/// <summary>Proves one durable per-user backfill owner spans selection, admission, contact, and persistence.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class GeoapifyBackfillConcurrencyPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Proves cancellation before durable ownership/admission has no provider cost.</summary>
    [PostgresFact]
    public async Task CancellationBeforeAdmissionCostsNothing()
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, null, protection);
        var handler = new CoordinatedHandler(user.Id, null);
        await using var db = fixture.CreateContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service(db, protection, handler).RunAsync(user.Id, cancellation.Token));

        await using var verify = fixture.CreateContext();
        Assert.Equal(0, handler.RequestsFor(user.Id));
        Assert.Equal(0, await verify.Set<GeoapifyUsageAdmission>().CountAsync(item => item.UserId == user.Id));
    }

    /// <summary>Proves admitted timeout and provider failure remain charged and release ownership for retry.</summary>
    [PostgresFact]
    public async Task AdmittedFailuresRetainAdmissionAndAllowRetry()
    {
        foreach (var outcome in new[] { ContactOutcome.Timeout, ContactOutcome.ProviderFailure })
        {
            var user = await fixture.CreateUserAsync();
            var protection = new EphemeralDataProtectionProvider();
            await SeedAsync(user.Id, null, protection);
            var failedHandler = new CoordinatedHandler(user.Id, null, outcome);
            await using var failedDb = fixture.CreateContext();
            var failedRun = Service(failedDb, protection, failedHandler).RunAsync(user.Id);
            await failedHandler.FirstUserRequestEntered;
            failedHandler.Release();
            var failure = await failedRun;
            Assert.Equal(1, failure.Unavailable);

            var retryHandler = new CoordinatedHandler(user.Id, null);
            await using var retryDb = fixture.CreateContext();
            var retry = Service(retryDb, protection, retryHandler).RunAsync(user.Id);
            await retryHandler.FirstUserRequestEntered;
            retryHandler.Release();
            await retry;

            await using var verify = fixture.CreateContext();
            Assert.Equal(2, await verify.Set<GeoapifyUsageAdmission>().CountAsync(item => item.UserId == user.Id));
            Assert.Equal("geoapify", (await verify.Locations.SingleAsync(item => item.UserId == user.Id)).ReverseGeocodingProvider);
        }
    }

    /// <summary>Proves cancellation after contact retains admission and releases durable ownership.</summary>
    [PostgresFact]
    public async Task CancellationAfterContactRetainsAdmissionAndAllowsRetry()
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, null, protection);
        var cancelledHandler = new CoordinatedHandler(user.Id, null);
        await using var cancelledDb = fixture.CreateContext();
        var cancelledService = Service(cancelledDb, protection, cancelledHandler);
        using var cancellation = new CancellationTokenSource();

        var cancelledRun = cancelledService.RunAsync(user.Id, cancellation.Token);
        await cancelledHandler.FirstUserRequestEntered;
        await using (var duringContact = fixture.CreateContext())
            Assert.Equal(1, await duringContact.Set<GeoapifyUsageAdmission>()
                .CountAsync(item => item.UserId == user.Id));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRun);

        await using (var verify = fixture.CreateContext())
        {
            Assert.Equal(1, await verify.Set<GeoapifyUsageAdmission>()
                .CountAsync(item => item.UserId == user.Id));
            var location = await verify.Locations.SingleAsync(item => item.UserId == user.Id);
            Assert.True(GeoapifyLocationBackfillService.IsWhollyUnenriched(location));
            Assert.Equal(1, handlerRequests(cancelledHandler, user.Id));
        }

        var retryHandler = new CoordinatedHandler(user.Id, null);
        await using var retryDb = fixture.CreateContext();
        var retry = Service(retryDb, protection, retryHandler).RunAsync(user.Id);
        await retryHandler.FirstUserRequestEntered;
        retryHandler.Release();
        await retry;

        await using var final = fixture.CreateContext();
        Assert.Equal(2, await final.Set<GeoapifyUsageAdmission>().CountAsync(item => item.UserId == user.Id));
        Assert.Equal(1, retryHandler.RequestsFor(user.Id));
        Assert.Equal("geoapify", (await final.Locations.SingleAsync(item => item.UserId == user.Id)).ReverseGeocodingProvider);

        static int handlerRequests(CoordinatedHandler handler, string userId) => handler.RequestsFor(userId);
    }

    [PostgresFact]
    public async Task ConcurrentSameUserInvocationsContactOnceWhileAnotherUserRemainsIndependent()
    {
        var user = await fixture.CreateUserAsync();
        var other = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, other.Id, protection);
        var handler = new CoordinatedHandler(user.Id, other.Id);
        await using var firstDb = fixture.CreateContext();
        await using var secondDb = fixture.CreateContext();
        await using var otherDb = fixture.CreateContext();
        var first = Service(firstDb, protection, handler);
        var second = Service(secondDb, protection, handler);
        var independent = Service(otherDb, protection, handler);

        var firstRun = first.RunAsync(user.Id);
        Task<GeoapifyBackfillResult>? secondRun = null;
        Task<GeoapifyBackfillResult>? otherRun = null;
        try
        {
            await handler.FirstUserRequestEntered;
            await using (var duringContact = fixture.CreateContext())
                Assert.Equal(1, await duringContact.Set<GeoapifyUsageAdmission>()
                    .CountAsync(item => item.UserId == user.Id));
            secondRun = second.RunAsync(user.Id);
            otherRun = independent.RunAsync(other.Id);
            await handler.OtherUserRequestEntered;
            await ObserveLockOrDuplicateContactAsync(handler, user.Id);
        }
        finally
        {
            handler.Release();
        }
        await Task.WhenAll(firstRun, secondRun!, otherRun!);

        await using var verify = fixture.CreateContext();
        var sameUserAdmissions = await verify.GeoapifyUsageAdmissions
            .Where(item => item.UserId == user.Id).ToListAsync();
        var admission = Assert.Single(sameUserAdmissions);
        Assert.Equal(1, admission.Credits);
        Assert.Equal(PersonalProviderProduct.Geocoding, admission.Product);
        Assert.Equal(1, handler.RequestsFor(user.Id));
        Assert.Equal(1, await verify.Locations.CountAsync(item => item.UserId == user.Id && item.ReverseGeocodingProvider == "geoapify"));
        Assert.Equal(1, handler.RequestsFor(other.Id));
    }

    private async Task ObserveLockOrDuplicateContactAsync(CoordinatedHandler handler, string userId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var connection = fixture.CreateConnection();
        try
        {
            await connection.OpenAsync(timeout.Token);
            while (handler.RequestsFor(userId) < 2)
            {
                await using var command = new NpgsqlCommand("""
                    SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                    WHERE wait_event_type = 'Lock'
                    AND (query LIKE '%pg_advisory_xact_lock%' OR query LIKE '%AspNetUsers%FOR UPDATE%'))
                    """, connection);
                if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!) return;
                await Task.Yield();
                timeout.Token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Assert.Fail("The competing same-user backfill did not enter the expected PostgreSQL lock wait within 10 seconds.");
        }
    }

    private async Task SeedAsync(string userId, string? otherUserId, IDataProtectionProvider protection)
    {
        await using var db = fixture.CreateContext();
        foreach (var id in new[] { userId, otherUserId }.OfType<string>())
        {
            var profile = PersonalLocationProviderProfile.Create(id, PersonalLocationProvider.Geoapify);
            new PersonalProviderCredentialService(protection).Replace(profile, $"key-{id}");
            profile.GeocodingAuthorized = true;
            profile.GeocodingVerification = PersonalProviderVerification.Verified;
            profile.GeocodingVerifiedCredentialGeneration = profile.CredentialGeneration;
            profile.GeocodingVerifiedConfigurationGeneration = profile.GeocodingGeneration;
            db.Add(profile);
            db.Add(new PersonalLocationProviderSelection { UserId = id, GeocodingProviderKey = "geoapify" });
            db.Add(new GeoapifyUsageGuard { UserId = id });
            db.Locations.Add(new Location
            {
                UserId = id, Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow, TimeZoneId = "UTC",
                Coordinates = new Point(20, 10) { SRID = 4326 }
            });
        }
        await db.SaveChangesAsync();
    }

    private GeoapifyLocationBackfillService Service(ApplicationDbContext db,
        IDataProtectionProvider protection, CoordinatedHandler handler)
    {
        var credentials = new PersonalProviderCredentialService(protection);
        var gate = new PersonalProviderContactGate(db, credentials,
            new LegacyMapboxMigrationService(db, credentials), new ConfigurationBuilder().Build());
        var reverse = new ReverseGeocodingService(new HttpClient(handler), NullLogger<BaseApiController>.Instance, gate, db);
        return new GeoapifyLocationBackfillService(db, reverse, new FixtureDbContextFactory(fixture));
    }

    private sealed class FixtureDbContextFactory(PostgresImportTestFixture fixture)
        : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateContext();
    }

    private sealed class CoordinatedHandler(
        string primaryUserId, string? otherUserId, ContactOutcome outcome = ContactOutcome.Success) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _other = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<string, int> _requests = [];
        public Task FirstUserRequestEntered => _first.Task;
        public Task OtherUserRequestEntered => _other.Task;
        public int RequestsFor(string userId) { lock (_requests) return _requests.GetValueOrDefault(userId); }
        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.RequestUri!.Query.Split("apiKey=", StringSplitOptions.None)[1];
            var userId = Uri.UnescapeDataString(key)[4..];
            lock (_requests) _requests[userId] = _requests.GetValueOrDefault(userId) + 1;
            if (userId == primaryUserId) _first.TrySetResult();
            if (otherUserId != null && userId == otherUserId) _other.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            if (outcome == ContactOutcome.Timeout) throw new TaskCanceledException();
            if (outcome == ContactOutcome.ProviderFailure)
                return new(System.Net.HttpStatusCode.ServiceUnavailable);
            return new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"type":"FeatureCollection","features":[{"properties":{"formatted":"Address","address_line1":"Address"}}]}
                    """)
            };
        }
    }

    private enum ContactOutcome { Success, Timeout, ProviderFailure }
}
