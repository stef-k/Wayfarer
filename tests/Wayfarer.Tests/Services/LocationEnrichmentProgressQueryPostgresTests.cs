using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models.LocationEnrichment;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using Point = NetTopologySuite.Geometries.Point;

namespace Wayfarer.Tests.Services;

/// <summary>Proves enrichment progress remains scalar and cardinality-independent on PostgreSQL.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationEnrichmentProgressQueryPostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresFact]
    public async Task IncompleteCountIncludesOnlyDurableGeoapifyRowsMissingPlace()
    {
        var user = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var now = DateTimeOffset.UtcNow;
        db.Locations.AddRange(
            Partial(user.Id, "geoapify", "persistent", now, addressNumberOnly: true),
            Partial(user.Id, null, null, null),
            Partial(user.Id, "mapbox", "permanent", now),
            Partial(user.Id, "geoapify", "persistent", now, place: "Alexandroupolis"));
        await db.SaveChangesAsync();

        var result = await new LocationEnrichmentProgressQuery(db)
            .ProjectAsync(user.Id, Binding(), DateTime.UtcNow);

        Assert.Equal(1, result.IncompleteProviderAddresses);
        Assert.Equal(0, result.RepairsWithoutLocality);
    }

    [PostgresFact]
    public async Task LargeProjectionExecutesOnlyFixedAggregateSql()
    {
        fixture.RequireAvailable();
        var user = await fixture.CreateUserAsync();
        await SeedAsync(user, 32);
        var firstRecorder = new CommandRecorder();
        await using (var first = fixture.CreateContext(firstRecorder))
        {
            var result = await new LocationEnrichmentProgressQuery(first)
                .ProjectAsync(user.Id, Binding(), DateTime.UtcNow);
            Assert.Equal(32, result.RunnableRemaining);
        }

        await SeedAsync(user, 224);
        var largeRecorder = new CommandRecorder();
        await using (var large = fixture.CreateContext(largeRecorder))
        {
            var result = await new LocationEnrichmentProgressQuery(large)
                .ProjectAsync(user.Id, Binding(), DateTime.UtcNow);
            Assert.Equal(256, result.RunnableRemaining);
        }

        Assert.Equal(firstRecorder.Aggregates.Count, largeRecorder.Aggregates.Count);
        Assert.Equal(7, largeRecorder.Aggregates.Count);
        Assert.Contains(largeRecorder.Aggregates, sql => sql.Contains("count", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(largeRecorder.Aggregates, sql => sql.Contains("min", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(largeRecorder.Aggregates, sql => sql.Contains(" IN (@", StringComparison.OrdinalIgnoreCase));
        Assert.All(largeRecorder.Aggregates, sql => Assert.Contains("Locations", sql));
    }

    /// <summary>Prepared repairs require affirmative authority; operation and terminal facts never become due work.</summary>
    [PostgresFact]
    public async Task RepairEligibilitySeparatesUnpreparedStaleOwnedTerminalAndDueRows()
    {
        var user = await fixture.CreateUserAsync();
        var binding = Binding();
        await using var db = fixture.CreateContext();
        var now = new DateTime(DateTime.UtcNow.Ticks / 10 * 10, DateTimeKind.Utc);
        var workflow = LocationEnrichmentWorkflow.Create(user.Id, now);
        workflow.Start(now);
        var owner = workflow.TryAcquireExecutionLease(now, TimeSpan.FromSeconds(35))!.Value;
        db.Add(workflow);
        var locations = Enumerable.Range(0, 8).Select(_ => Partial(user.Id, "geoapify", "persistent", DateTimeOffset.UtcNow)).ToArray();
        db.AddRange(locations);
        await db.SaveChangesAsync();
        for (var i = 1; i < locations.Length; i++)
        {
            var attempt = new LocationEnrichmentAttempt { UserId = user.Id, LocationId = locations[i].Id };
            attempt.PrepareRepair(binding, now);
            if (i == 1) { attempt.SelectionGeneration++; attempt.NextAttemptAtUtc = now.AddMinutes(1); }
            if (i == 2)
            {
                attempt.OperationId = Guid.NewGuid();
                attempt.OperationLeaseId = owner.LeaseId;
                attempt.OperationFencingGeneration = owner.FencingGeneration;
                attempt.OperationWorkflowEpoch = owner.Epoch;
                attempt.OperationStartedAtUtc = now;
                attempt.OperationAttemptNumber = attempt.AdmittedAttemptCount = 1;
                attempt.LastAttemptAtUtc = now;
                attempt.Outcome = LocationEnrichmentOutcome.RetryableFailure;
                attempt.NextAttemptAtUtc = now.AddMinutes(1);
            }
            if (i == 3) { attempt.Outcome = LocationEnrichmentOutcome.NoResult; attempt.AdmittedAttemptCount = 1; }
            if (i == 4) { attempt.Outcome = LocationEnrichmentOutcome.RetryableFailure; attempt.AdmittedAttemptCount = 3; attempt.NextAttemptAtUtc = now.AddMinutes(1); }
            if (i == 6) { attempt.Outcome = LocationEnrichmentOutcome.RetryableFailure; attempt.NextAttemptAtUtc = now.AddMinutes(5); }
            if (i == 7) attempt.Outcome = LocationEnrichmentOutcome.InvalidCoordinates;
            db.Add(attempt);
        }
        await db.SaveChangesAsync();
        var query = new LocationEnrichmentProgressQuery(db);
        var progress = await query.ProjectAsync(user.Id, binding, now);
        Assert.Equal((1, 1, 0, 8), (progress.RunnableRemaining, progress.FutureDue, progress.ManualRetryAvailable, progress.IncompleteProviderAddresses));
        Assert.True(await query.HasRunnableAsync(user.Id, binding, now));
        var authority = new EnrichmentAuthority(binding.ProviderKey, PersonalProviderCapability.Geocoding,
            binding.CredentialGeneration, binding.CapabilityGeneration, binding.SelectionGeneration,
            binding.ProfileId, binding.Verification, binding.VerifiedCredentialGeneration, binding.VerifiedCapabilityGeneration);
        Assert.Equal(new[] { locations[5].Id }, await GeoapifyLocationBackfillService.CandidateQuery(db, user.Id, authority, now).ToArrayAsync());
        Assert.Equal(new[] { locations[6].Id }, await GeoapifyLocationBackfillService.FutureRetryQuery(db, user.Id, authority, now).Select(x => x.LocationId).ToArrayAsync());
        Assert.Equal(now.AddMinutes(5), progress.NextAttemptAtUtc);
        Assert.Equal(new[] { locations[5].Id, locations[6].Id }, await GeoapifyLocationBackfillService.CandidateQuery(db, user.Id, authority, now.AddMinutes(10)).ToArrayAsync());
        Assert.Equal((1, 0, 1), (progress.InFlight, progress.AwaitingRecovery, progress.RepairsWithoutLocality));
        workflow.Cancel(now);
        await db.SaveChangesAsync();
        progress = await query.ProjectAsync(user.Id, binding, now);
        Assert.Equal((0, 1), (progress.InFlight, progress.AwaitingRecovery));
        Assert.False(workflow.IntentEnabled);
    }

    private async Task SeedAsync(Wayfarer.Models.ApplicationUser user, int count)
    {
        await using var context = fixture.CreateContext();
        for (var index = 0; index < count; index++)
            context.Locations.Add(TestDataFixtures.CreateLocation(user));
        await context.SaveChangesAsync();
    }

    private static PersonalProviderAuthorityBinding Binding() => new("geoapify", Guid.NewGuid(),
        1, 1, 1, PersonalProviderVerification.Verified, 1, 1, null, null, null);

    private static Location Partial(string userId, string? provider, string? storage,
        DateTimeOffset? resolvedAt, string? place = null, bool addressNumberOnly = false) => new()
    {
        UserId = userId, Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow,
        TimeZoneId = "UTC", Coordinates = new Point(25, 40) { SRID = 4326 },
        Address = addressNumberOnly ? null : "Known address", AddressNumber = addressNumberOnly ? "12" : null,
        Country = addressNumberOnly ? null : "Greece", Place = place, ReverseGeocodingProvider = provider,
        ReverseGeocodingStorageMode = storage, ReverseGeocodedAt = resolvedAt
    };

    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Aggregates { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            if (command.CommandText.Contains("Locations", StringComparison.Ordinal))
                Aggregates.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("Locations", StringComparison.Ordinal))
                Aggregates.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
