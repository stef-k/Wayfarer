using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using Npgsql;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Location = Wayfarer.Models.Location;

namespace Wayfarer.Tests.Services;

/// <summary>Proves one durable per-user backfill owner spans selection, admission, contact, and persistence.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class GeoapifyBackfillConcurrencyPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Proves provider authority admitted before contact cannot authorize stale persistence.</summary>
    [PostgresTheory(Timeout = 30_000)]
    [InlineData(AuthorityMutation.ReplaceCredential)]
    [InlineData(AuthorityMutation.RevokeCredential)]
    [InlineData(AuthorityMutation.ChangeSelection)]
    [InlineData(AuthorityMutation.ChangeCapabilityGeneration)]
    public async Task AuthorityMutationDuringContactDiscardsProviderResult(AuthorityMutation mutation)
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, null, protection);
        var handler = new CoordinatedHandler(user.Id, null);
        await using var runDb = fixture.CreateContext();
        var run = Service(runDb, protection, handler).RunAsync(user.Id);
        await handler.FirstUserRequestEntered;

        await using (var mutate = fixture.CreateContext())
        {
            var profile = await mutate.PersonalLocationProviderProfiles
                .SingleAsync(item => item.UserId == user.Id && item.ProviderKey == "geoapify");
            var selection = await mutate.PersonalLocationProviderSelections.SingleAsync(item => item.UserId == user.Id);
            if (mutation == AuthorityMutation.ReplaceCredential)
                new PersonalProviderCredentialService(protection).Replace(profile, "replacement");
            else if (mutation == AuthorityMutation.RevokeCredential)
                new PersonalProviderCredentialService(protection).Revoke(profile);
            else if (mutation == AuthorityMutation.ChangeSelection)
                selection.Select(PersonalProviderCapability.Geocoding, null);
            else
                profile.SetAuthorization(PersonalProviderCapability.Geocoding, false);
            await mutate.SaveChangesAsync();
        }

        handler.Release();
        var result = await run;
        await using var verify = fixture.CreateContext();
        Assert.Equal(0, result.Succeeded);
        Assert.True(GeoapifyLocationBackfillService.IsWhollyUnenriched(
            await verify.Locations.SingleAsync(item => item.UserId == user.Id)));
        Assert.Single(await verify.GeoapifyUsageAdmissions.Where(item => item.UserId == user.Id).ToListAsync());
        Assert.NotNull((await verify.LocationEnrichmentAttempts.SingleAsync(item => item.UserId == user.Id)).OperationId);
    }

    /// <summary>Proves a manual edit committed after response inspection wins over scheduled enrichment.</summary>
    [PostgresFact(Timeout = 30_000)]
    public async Task ManualEditDuringContactWinsAtomicLocationEligibility()
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, null, protection);
        var handler = new CoordinatedHandler(user.Id, null);
        await using var runDb = fixture.CreateContext();
        var run = Service(runDb, protection, handler).RunAsync(user.Id);
        await handler.FirstUserRequestEntered;
        await using (var edit = fixture.CreateContext())
        {
            var location = await edit.Locations.SingleAsync(item => item.UserId == user.Id);
            location.FullAddress = "Manual address";
            await edit.SaveChangesAsync();
        }
        handler.Release();
        var result = await run;

        await using var verify = fixture.CreateContext();
        var saved = await verify.Locations.SingleAsync(item => item.UserId == user.Id);
        Assert.Equal("Manual address", saved.FullAddress);
        Assert.Null(saved.ReverseGeocodingProvider);
        Assert.Equal(0, result.Succeeded);
        Assert.Equal(1, result.Skipped);
    }

    /// <summary>Proves superseded provider-dependent attempts are reconsidered without reviving permanent same-generation rows.</summary>
    [PostgresFact]
    public async Task SupersededAuthorityAttemptIsEligibleButSameGenerationPermanentAttemptIsNot()
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, null, protection);
        await using var db = fixture.CreateContext();
        var location = await db.Locations.SingleAsync(item => item.UserId == user.Id);
        var workflow = LocationEnrichmentWorkflow.Create(user.Id, DateTime.UtcNow);
        workflow.Start(DateTime.UtcNow);
        db.Add(workflow);
        db.Add(new LocationEnrichmentAttempt
        {
            UserId = user.Id, LocationId = location.Id, ProviderKey = "geoapify",
            CredentialGeneration = 1, ConfigurationGeneration = 1, SelectionGeneration = 1,
            Outcome = LocationEnrichmentOutcome.NoResult, AdmittedAttemptCount = 1,
            LastAttemptAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var superseded = await GeoapifyLocationBackfillService.LoadCandidateIdsAsync(db, user.Id,
            new("geoapify", 2, 1, 1), 10);
        var sameGeneration = await GeoapifyLocationBackfillService.LoadCandidateIdsAsync(db, user.Id,
            new("geoapify", 1, 1, 1), 10);

        Assert.Contains(location.Id, superseded);
        Assert.DoesNotContain(location.Id, sameGeneration);
    }
    [PostgresTheory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseOrCancelDuringContactFencesEnrichmentAndRetainsAdmittedAttempt(bool cancel)
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, null, protection);
        int epoch;
        await using (var setup = fixture.CreateContext())
        {
            var workflow = LocationEnrichmentWorkflow.Create(user.Id, DateTime.UtcNow);
            workflow.Start(DateTime.UtcNow); epoch = workflow.Epoch;
            setup.Add(workflow); await setup.SaveChangesAsync();
        }
        var handler = new CoordinatedHandler(user.Id, null);
        await using var runDb = fixture.CreateContext();
        var run = Service(runDb, protection, handler).RunAsync(user.Id, epoch);
        await handler.FirstUserRequestEntered;
        await using (var command = fixture.CreateContext())
        {
            var workflow = await command.LocationEnrichmentWorkflows.SingleAsync(item => item.UserId == user.Id);
            if (cancel) workflow.Cancel(DateTime.UtcNow); else workflow.Pause(DateTime.UtcNow);
            await command.SaveChangesAsync();
        }
        handler.Release();
        await run;

        await using var verify = fixture.CreateContext();
        Assert.Single(await verify.GeoapifyUsageAdmissions.Where(item => item.UserId == user.Id).ToListAsync());
        var attempt = await verify.LocationEnrichmentAttempts.SingleAsync(item => item.UserId == user.Id);
        Assert.NotNull(attempt.OperationId);
        Assert.NotNull(attempt.NextAttemptAtUtc);
        Assert.True(GeoapifyLocationBackfillService.IsWhollyUnenriched(
            await verify.Locations.SingleAsync(item => item.UserId == user.Id)));
    }

    [PostgresFact(Timeout = 30_000)]
    public async Task ScheduledPoisonRowDoesNotStarveLaterCandidate()
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, null, protection);
        int laterId;
        int epoch;
        await using (var setup = fixture.CreateContext())
        {
            var oldest = await setup.Locations.SingleAsync(item => item.UserId == user.Id);
            oldest.Timestamp = DateTime.UtcNow.AddMinutes(-2);
            var later = new Location
            {
                UserId = user.Id, Timestamp = DateTime.UtcNow.AddMinutes(-1), LocalTimestamp = DateTime.UtcNow,
                TimeZoneId = "UTC", Coordinates = new Point(21, 11) { SRID = 4326 }
            };
            setup.Add(later);
            var workflow = LocationEnrichmentWorkflow.Create(user.Id, DateTime.UtcNow);
            workflow.Start(DateTime.UtcNow);
            epoch = workflow.Epoch;
            Assert.True(workflow.TryClaim(epoch, DateTime.UtcNow));
            setup.Add(workflow);
            setup.Add(new LocationEnrichmentAttempt
            {
                UserId = user.Id, Location = oldest, ProviderKey = "geoapify", CredentialGeneration = 2,
                ConfigurationGeneration = 1, SelectionGeneration = 1,
                Outcome = LocationEnrichmentOutcome.NoResult, AdmittedAttemptCount = 1,
                LastAttemptAtUtc = DateTime.UtcNow.AddMinutes(-1)
            });
            await setup.SaveChangesAsync();
            laterId = later.Id;
        }
        var handler = new CoordinatedHandler(user.Id, null);
        await using var run = fixture.CreateContext();
        var task = Service(run, protection, handler).RunAsync(user.Id, epoch);
        var entered = await Task.WhenAny(handler.FirstUserRequestEntered, task);
        if (entered == task) await task;
        await handler.FirstUserRequestEntered;
        handler.Release();
        var result = await task;

        await using var verify = fixture.CreateContext();
        Assert.Equal("geoapify", (await verify.Locations.SingleAsync(item => item.Id == laterId)).ReverseGeocodingProvider);
        Assert.Single(await verify.LocationEnrichmentAttempts.Where(item => item.UserId == user.Id).ToListAsync());
        Assert.Equal(0, result.RemainingEstimate);
    }

    [PostgresFact(Timeout = 30_000)]
    public async Task ScheduledTransientFailurePersistsGenerationBoundAttemptAndAdmission()
    {
        var user = await fixture.CreateUserAsync();
        var protection = new EphemeralDataProtectionProvider();
        await SeedAsync(user.Id, null, protection);
        int epoch;
        await using (var setup = fixture.CreateContext())
        {
            var workflow = LocationEnrichmentWorkflow.Create(user.Id, DateTime.UtcNow);
            workflow.Start(DateTime.UtcNow); epoch = workflow.Epoch;
            Assert.True(workflow.TryClaim(epoch, DateTime.UtcNow));
            setup.Add(workflow); await setup.SaveChangesAsync();
        }
        var handler = new CoordinatedHandler(user.Id, null, ContactOutcome.ProviderFailure);
        await using var run = fixture.CreateContext();
        var task = Service(run, protection, handler).RunAsync(user.Id, epoch);
        await handler.FirstUserRequestEntered; handler.Release(); var result = await task;

        await using var verify = fixture.CreateContext();
        var attempt = await verify.LocationEnrichmentAttempts.SingleAsync(item => item.UserId == user.Id);
        Assert.Equal(LocationEnrichmentOutcome.RetryableFailure, attempt.Outcome);
        Assert.Equal(1, attempt.AdmittedAttemptCount);
        Assert.Equal("geoapify", attempt.ProviderKey);
        Assert.NotNull(attempt.NextAttemptAtUtc);
        Assert.Equal(1, result.RemainingEstimate);
        Assert.Equal(attempt.NextAttemptAtUtc, result.NextEligibleAt?.UtcDateTime);
        Assert.Single(await verify.GeoapifyUsageAdmissions.Where(item => item.UserId == user.Id).ToListAsync());
    }

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
    public async Task AdmittedFailuresRetainAdmissionAndDeferImmediateRetry()
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
            await Service(retryDb, protection, retryHandler).RunAsync(user.Id);

            await using var verify = fixture.CreateContext();
            Assert.Single(await verify.Set<GeoapifyUsageAdmission>().Where(item => item.UserId == user.Id).ToListAsync());
            Assert.Equal(0, retryHandler.RequestsFor(user.Id));
            Assert.True(GeoapifyLocationBackfillService.IsWhollyUnenriched(
                await verify.Locations.SingleAsync(item => item.UserId == user.Id)));
        }
    }

    /// <summary>Proves cancellation after contact retains admission and releases durable ownership.</summary>
    [PostgresFact]
    public async Task CancellationAfterContactRetainsAdmissionAndDefersRetry()
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
        var cancelled = await cancelledRun;
        Assert.Equal(1, cancelled.Unavailable);

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
        await Service(retryDb, protection, retryHandler).RunAsync(user.Id);

        await using var final = fixture.CreateContext();
        Assert.Single(await final.Set<GeoapifyUsageAdmission>().Where(item => item.UserId == user.Id).ToListAsync());
        Assert.Equal(0, retryHandler.RequestsFor(user.Id));
        Assert.True(GeoapifyLocationBackfillService.IsWhollyUnenriched(
            await final.Locations.SingleAsync(item => item.UserId == user.Id)));

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
            await secondRun;
            Assert.Equal(1, handler.RequestsFor(user.Id));
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
        var contextFactory = new FixtureDbContextFactory(fixture);
        var services = new ServiceCollection()
            .AddScoped(_ => fixture.CreateContext())
            .AddSingleton(credentials)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddScoped<LegacyMapboxMigrationService>()
            .AddScoped<PersonalProviderContactGate>()
            .BuildServiceProvider();
        var authority = new LocationEnrichmentExecutionAuthority(contextFactory);
        return new GeoapifyLocationBackfillService(contextFactory, services.GetRequiredService<IServiceScopeFactory>(),
            new TestHttpClientFactory(handler), NullLogger<BaseApiController>.Instance, authority);
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        { Timeout = TimeSpan.FromSeconds(15) };
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

    public enum AuthorityMutation { ReplaceCredential, RevokeCredential, ChangeSelection, ChangeCapabilityGeneration }
    private enum ContactOutcome { Success, Timeout, ProviderFailure }
}
