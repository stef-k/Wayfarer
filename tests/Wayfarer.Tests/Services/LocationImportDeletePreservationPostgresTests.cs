using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using Npgsql;
using Quartz;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationImports;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Exercises real relational deletion while protecting independent retained state.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationImportDeletePreservationPostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresFact]
    public async Task RelationalFailureAfterExternalCleanup_RetriesWithoutDeletingProtectedState()
    {
        var seed = await SeedPreservedStateAsync();
        var jobs = new HashSet<JobKey> { LocationImportSchedulerKeys.Job(seed.ImportId, 2) };
        var scheduler = Scheduler(jobs);
        await using (var command = fixture.CreateContext())
        {
            var result = await new LocationImportLifecycle(command, scheduler.Object,
                NullLogger<LocationImportLifecycle>.Instance).DeleteAsync(seed.UserId, seed.ImportId);
            Assert.Equal(LocationImportCommandCode.Accepted, result.Code);
        }

        // Restore only import metadata/file so the reconciler path deterministically fails after external cleanup.
        await File.WriteAllTextAsync(seed.Path, "fixture");
        await using (var restore = fixture.CreateContext())
        {
            restore.LocationImports.Add(new LocationImport
            {
                Id = seed.ImportId, UserId = seed.UserId, FilePath = seed.Path,
                FileType = LocationImportFileType.Csv, Status = ImportStatus.Completed,
                ExecutionEpoch = 2, TotalRecords = 1, LastProcessedIndex = 1,
                DeletionRequestedAtUtc = DateTime.UtcNow
            });
            await restore.SaveChangesAsync();
        }
        jobs.Add(LocationImportSchedulerKeys.Job(seed.ImportId, 2));
        var failure = new DeleteFailureInterceptor();
        var failing = new LocationImportReconciler(new FixtureFactory(fixture, failure), scheduler.Object,
            NullLogger<LocationImportReconciler>.Instance);
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => failing.ReconcileAsync());
        Assert.IsType<NpgsqlException>(exception.InnerException);

        Assert.Empty(jobs);
        Assert.False(File.Exists(seed.Path));
        await AssertPreservedAsync(seed, importExpected: true);

        var retry = new LocationImportReconciler(new FixtureFactory(fixture), scheduler.Object,
            NullLogger<LocationImportReconciler>.Instance);
        await retry.ReconcileAsync();
        await retry.ReconcileAsync();
        await AssertPreservedAsync(seed, importExpected: false);
    }

    private async Task<Seed> SeedPreservedStateAsync()
    {
        var user = await fixture.CreateUserAsync();
        var path = Path.Combine(Path.GetTempPath(), $"wayfarer-511-preserve-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, "fixture");
        var tripId = Guid.NewGuid();
        fixture.RegisterTrip(tripId);
        await using var db = fixture.CreateContext();
        var storedUser = await db.Users.SingleAsync(item => item.Id == user.Id);
        storedUser.IsTimelinePublic = true;
        storedUser.TimelineTitle = "Preserved timeline";
        var location = new Wayfarer.Models.Location
        {
            UserId = user.Id, Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow,
            TimeZoneId = "UTC", Coordinates = new Point(23.72, 37.98) { SRID = 4326 },
            Source = "import", Address = "Preserved address", FullAddress = "Preserved full address",
            ReverseGeocodingProvider = "geoapify", ReverseGeocodingStorageMode = "retained",
            ReverseGeocodedAt = DateTimeOffset.UtcNow
        };
        var workflow = LocationEnrichmentWorkflow.Create(user.Id, DateTime.UtcNow);
        workflow.Start(DateTime.UtcNow);
        workflow.RecordBatch(1, 0, 1, 0, 1, DateTime.UtcNow);
        var profile = PersonalLocationProviderProfile.Create(user.Id, PersonalLocationProvider.Geoapify);
        profile.ProtectedCredential = "fixture-protected";
        profile.SetAuthorization(PersonalProviderCapability.Geocoding, true);
        var selection = PersonalLocationProviderSelection.Create(user.Id);
        selection.Select(PersonalProviderCapability.Geocoding, PersonalLocationProvider.Geoapify);
        var import = new LocationImport
        {
            UserId = user.Id, FilePath = path, FileType = LocationImportFileType.Csv,
            Status = ImportStatus.Completed, ExecutionEpoch = 2, TotalRecords = 1, LastProcessedIndex = 1
        };
        db.AddRange(location, workflow, profile, selection, import,
            new GeoapifyUsageGuard { UserId = user.Id, CreditLimit = 2500 },
            new GeoapifyUsageAdmission
            {
                UserId = user.Id, Credits = 1, Product = PersonalProviderProduct.Geocoding,
                AdmittedAt = DateTimeOffset.UtcNow
            },
            new MapboxProductMeter
            {
                UserId = user.Id, Product = PersonalProviderProduct.PermanentGeocoding,
                CycleStart = DateOnly.FromDateTime(DateTime.UtcNow), AdmittedCount = 1
            },
            new Trip { Id = tripId, UserId = user.Id, Name = "Preserved trip", UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var attempt = new LocationEnrichmentAttempt
        {
            UserId = user.Id, LocationId = location.Id, ProviderKey = "geoapify",
            Outcome = LocationEnrichmentOutcome.RetryableFailure, AdmittedAttemptCount = 1,
            LastAttemptAtUtc = DateTime.UtcNow, NextAttemptAtUtc = DateTime.UtcNow.AddMinutes(5),
            CredentialGeneration = 1, ConfigurationGeneration = 2, SelectionGeneration = 2
        };
        db.Add(attempt);
        await db.SaveChangesAsync();
        return new(user.Id, import.Id, location.Id, profile.Id, attempt.Id, tripId, path);
    }

    private async Task AssertPreservedAsync(Seed seed, bool importExpected)
    {
        await using var db = fixture.CreateContext();
        Assert.Equal(importExpected, await db.LocationImports.AnyAsync(item => item.Id == seed.ImportId));
        var user = await db.Users.SingleAsync(item => item.Id == seed.UserId);
        Assert.True(user.IsTimelinePublic);
        Assert.Equal("Preserved timeline", user.TimelineTitle);
        var location = await db.Locations.SingleAsync(item => item.Id == seed.LocationId);
        Assert.Equal("import", location.Source);
        Assert.Equal("Preserved full address", location.FullAddress);
        Assert.Equal("geoapify", location.ReverseGeocodingProvider);
        Assert.Equal("retained", location.ReverseGeocodingStorageMode);
        Assert.NotNull(location.ReverseGeocodedAt);
        Assert.True(await db.GeoapifyUsageAdmissions.AnyAsync(item => item.UserId == seed.UserId));
        Assert.True(await db.GeoapifyUsageGuards.AnyAsync(item => item.UserId == seed.UserId));
        Assert.True(await db.MapboxProductMeters.AnyAsync(item => item.UserId == seed.UserId));
        Assert.True(await db.PersonalLocationProviderProfiles.AnyAsync(item => item.Id == seed.ProfileId));
        Assert.True(await db.PersonalLocationProviderSelections.AnyAsync(item => item.UserId == seed.UserId));
        var workflow = await db.LocationEnrichmentWorkflows.SingleAsync(item => item.UserId == seed.UserId);
        Assert.Equal(1, workflow.RetryableDeferredCount);
        Assert.True(await db.LocationEnrichmentAttempts.AnyAsync(item => item.Id == seed.AttemptId));
        Assert.True(await db.Trips.AnyAsync(item => item.Id == seed.TripId));
    }

    private static Mock<IScheduler> Scheduler(HashSet<JobKey> jobs)
    {
        var scheduler = new Mock<IScheduler>();
        scheduler.Setup(item => item.GetCurrentlyExecutingJobs(default)).ReturnsAsync([]);
        scheduler.Setup(item => item.GetJobKeys(It.IsAny<Quartz.Impl.Matchers.GroupMatcher<JobKey>>(), default))
            .ReturnsAsync(() => jobs.ToHashSet());
        scheduler.Setup(item => item.GetTriggerKeys(It.IsAny<Quartz.Impl.Matchers.GroupMatcher<TriggerKey>>(), default))
            .ReturnsAsync([]);
        scheduler.Setup(item => item.DeleteJob(It.IsAny<JobKey>(), default)).ReturnsAsync((JobKey key, CancellationToken _) => jobs.Remove(key));
        return scheduler;
    }

    private sealed class FixtureFactory(PostgresImportTestFixture fixture, IInterceptor? interceptor = null)
        : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => interceptor is null
            ? fixture.CreateContext() : fixture.CreateContext(interceptor);
        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class DeleteFailureInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("DELETE FROM \"LocationImports\"", StringComparison.Ordinal))
                throw new NpgsqlException("fixture relational deletion failure");
            return ValueTask.FromResult(result);
        }
    }

    private sealed record Seed(string UserId, int ImportId, int LocationId, Guid ProfileId,
        long AttemptId, Guid TripId, string Path);
}
