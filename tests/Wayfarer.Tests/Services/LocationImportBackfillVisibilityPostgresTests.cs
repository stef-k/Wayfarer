using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Services.LocationImports;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves committed import rows cross directly into scheduled enrichment ownership.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class LocationImportBackfillVisibilityPostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresFact(Timeout = 30_000)]
    public async Task ImportRows_AreInvisibleBeforeInsertCommitAndVisibleAfterProgressCommit()
    {
        await using var seed = await SeedImportAsync(51, includeAddress: false, includeKeys: false);
        var insertGate = new AddedLocationSaveGate();
        var batchGate = new FirstBatchObserver();
        var transport = new RejectingHandler();
        await using var workerDb = fixture.CreateContext(insertGate);
        var running = Service(workerDb, transport, batchGate)
            .ProcessImportExecution(seed.ImportId, seed.Epoch, CancellationToken.None);

        await insertGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(insertGate.TrackedAddedLocations > 0);
        await using (var beforeCommit = fixture.CreateContext())
        {
            Assert.Equal(0, await CandidateCountAsync(beforeCommit, seed.UserId));
            Assert.Equal(0, await beforeCommit.Locations.CountAsync(x => x.UserId == seed.UserId));
        }
        Assert.Equal(0, transport.Requests);

        insertGate.Release();
        await batchGate.Entered.WaitAsync(TimeSpan.FromSeconds(10));
        await using (var committed = fixture.CreateContext())
        {
            Assert.Equal(50, await committed.Locations.CountAsync(x => x.UserId == seed.UserId));
            Assert.Equal(50, await CandidateCountAsync(committed, seed.UserId));
            Assert.Equal(50, (await committed.LocationImports.SingleAsync(x => x.Id == seed.ImportId)).LastProcessedIndex);
        }
        Assert.Equal(0, transport.Requests);
        batchGate.Release();
        Assert.Equal(LocationImportExecutionOutcome.Completed, await running);
    }

    [PostgresFact(Timeout = 30_000)]
    public async Task CommittedEnrichedRowsAreExcludedWhileBlankRowsAreProcessedOnceAndReplayIsDeduplicated()
    {
        var protection = new EphemeralDataProtectionProvider();
        var user = await fixture.CreateUserAsync();
        await SeedProviderAsync(user.Id, protection);
        await using var enriched = await SeedImportAsync(1, includeAddress: true, includeKeys: true, user.Id);
        await using (var db = fixture.CreateContext())
            Assert.Equal(LocationImportExecutionOutcome.Completed,
                await Service(db, new RejectingHandler(), NullLocationImportLifecycleObserver.Instance)
                    .ProcessImportExecution(enriched.ImportId, enriched.Epoch, CancellationToken.None));
        await using (var verify = fixture.CreateContext())
        {
            Assert.Equal(0, await CandidateCountAsync(verify, user.Id));
            Assert.Equal("Imported address", (await verify.Locations.SingleAsync(x => x.UserId == user.Id)).Address);
        }

        await using var blank = await SeedImportAsync(1, includeAddress: false, includeKeys: true, user.Id,
            minuteOffset: 2);
        await using (var db = fixture.CreateContext())
            Assert.Equal(LocationImportExecutionOutcome.Completed,
                await Service(db, new RejectingHandler(), NullLocationImportLifecycleObserver.Instance)
                    .ProcessImportExecution(blank.ImportId, blank.Epoch, CancellationToken.None));
        await using (var visible = fixture.CreateContext())
            Assert.Equal(1, await CandidateCountAsync(visible, user.Id));

        var handler = new CountingSuccessHandler();
        var backfill = Backfill(protection, handler);
        var first = await backfill.RunAsync(user.Id);
        var second = await backfill.RunAsync(user.Id);
        Assert.Equal(1, first.Succeeded);
        Assert.Equal(0, second.Succeeded);
        Assert.Equal(1, handler.Requests);

        await using var replay = await SeedImportAsync(1, includeAddress: false, includeKeys: true, user.Id,
            minuteOffset: 2);
        await using (var db = fixture.CreateContext())
            Assert.Equal(LocationImportExecutionOutcome.Completed,
                await Service(db, new RejectingHandler(), NullLocationImportLifecycleObserver.Instance)
                    .ProcessImportExecution(replay.ImportId, replay.Epoch, CancellationToken.None));
        await using var final = fixture.CreateContext();
        Assert.Equal(2, await final.Locations.CountAsync(x => x.UserId == user.Id));
        Assert.Equal("Address", (await final.Locations.SingleAsync(x => x.UserId == user.Id
            && x.Timestamp.Minute == 2)).Address);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(1, await final.GeoapifyUsageAdmissions.CountAsync(x => x.UserId == user.Id));
    }

    private static Task<int> CandidateCountAsync(ApplicationDbContext db, string userId) =>
        LocationEnrichmentProgressQuery.WhollyUnenriched(db.Locations.Where(x => x.UserId == userId)).CountAsync();

    private LocationImportService Service(ApplicationDbContext db, HttpMessageHandler handler,
        ILocationImportLifecycleObserver observer) => new(db,
        new ReverseGeocodingService(new HttpClient(handler), NullLogger<BaseApiController>.Instance),
        NullLogger<LocationImportService>.Instance, new LocationDataParserFactory(NullLoggerFactory.Instance),
        new SseService(), null, observer);

    private GeoapifyLocationBackfillService Backfill(
        IDataProtectionProvider protection, HttpMessageHandler handler)
    {
        var credentials = new PersonalProviderCredentialService(protection);
        var services = new ServiceCollection()
            .AddScoped(_ => fixture.CreateContext())
            .AddSingleton(credentials)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddScoped<LegacyMapboxMigrationService>()
            .AddScoped<PersonalProviderContactGate>()
            .BuildServiceProvider();
        var factory = new FixtureFactory(fixture);
        return new GeoapifyLocationBackfillService(factory, services.GetRequiredService<IServiceScopeFactory>(),
            new TestHttpClientFactory(handler), NullLogger<BaseApiController>.Instance,
            new LocationEnrichmentExecutionAuthority(factory));
    }

    private async Task SeedProviderAsync(string userId, IDataProtectionProvider protection)
    {
        await using var db = fixture.CreateContext();
        var profile = PersonalLocationProviderProfile.Create(userId, PersonalLocationProvider.Geoapify);
        new PersonalProviderCredentialService(protection).Replace(profile, $"key-{userId}");
        profile.SetAuthorization(PersonalProviderCapability.Geocoding, true);
        new PersonalProviderCredentialService(protection).RecordVerification(profile,
            PersonalProviderCapability.Geocoding, PersonalProviderVerification.Verified);
        var selection = PersonalLocationProviderSelection.Create(userId);
        selection.Select(PersonalProviderCapability.Geocoding, PersonalLocationProvider.Geoapify);
        db.AddRange(profile, selection, new GeoapifyUsageGuard { UserId = userId, CreditLimit = 10 });
        await db.SaveChangesAsync();
    }

    private async Task<ImportSeed> SeedImportAsync(int count, bool includeAddress, bool includeKeys,
        string? existingUserId = null, int minuteOffset = 0)
    {
        var userId = existingUserId ?? (await fixture.CreateUserAsync()).Id;
        var directory = Path.Combine(Path.GetTempPath(), "wayfarer-512-import-visibility", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "locations.csv");
        var header = "Latitude,Longitude,TimestampUtc,Address,IdempotencyKey";
        var rows = Enumerable.Range(0, count).Select(index =>
            $"{37 + index * .001},{23 + index * .001},2026-08-25T12:{minuteOffset:00}:{index:00}Z,"
            + $"{(includeAddress ? "Imported address" : string.Empty)},"
            + $"{(includeKeys ? $"00000000-0000-0000-{minuteOffset:0000}-{index:000000000000}" : null)}");
        await File.WriteAllLinesAsync(path, [header, .. rows]);
        await using var db = fixture.CreateContext();
        var import = new LocationImport
        {
            UserId = userId, FilePath = path, FileType = LocationImportFileType.Csv,
            Status = ImportStatus.InProgress, ExecutionEpoch = 12, EnrichmentRequested = true,
            TotalRecords = 0, LastProcessedIndex = 0
        };
        db.Add(import);
        await db.SaveChangesAsync();
        return new ImportSeed(userId, import.Id, import.ExecutionEpoch, directory);
    }

    private sealed class AddedLocationSaveGate : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int gated;
        internal Task Entered => entered.Task;
        internal int TrackedAddedLocations { get; private set; }
        internal void Release() => release.TrySetResult();
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var added = eventData.Context!.ChangeTracker.Entries<Wayfarer.Models.Location>()
                .Count(x => x.State == EntityState.Added);
            if (added > 0 && Interlocked.Exchange(ref gated, 1) == 0)
            {
                TrackedAddedLocations = added;
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class FirstBatchObserver : ILocationImportLifecycleObserver
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Entered => entered.Task;
        internal void Release() => release.TrySetResult();
        public async Task AfterBatchCommittedAsync(int importId, int epoch, int processed, CancellationToken token)
        { if (processed == 50) { entered.TrySetResult(); await release.Task.WaitAsync(token); } }
        public Task BeforeTerminalPersistenceAsync(int importId, int epoch,
            LocationImportExecutionOutcome outcome, CancellationToken token) => Task.CompletedTask;
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Requests++; return Task.FromException<HttpResponseMessage>(new InvalidOperationException("HTTP forbidden")); }
    }

    private sealed class CountingSuccessHandler : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"type":"FeatureCollection","features":[{"properties":{"formatted":"Address","address_line1":"Address"}}]}""")
            });
        }
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FixtureFactory(PostgresImportTestFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateContext();
    }

    private sealed class ImportSeed(string userId, int importId, int epoch, string directory) : IAsyncDisposable
    {
        internal string UserId { get; } = userId;
        internal int ImportId { get; } = importId;
        internal int Epoch { get; } = epoch;
        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
