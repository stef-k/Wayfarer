using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Services.LocationImports;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Observes worker context disposal at the committed-batch boundary.</summary>
public sealed class LocationImportBatchContextTests
{
    [Fact]
    public async Task Stop_DisposesBatchContextBeforeEnrichmentReconciliation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bounded-stop-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path,
            "Latitude,Longitude,TimestampUtc\r\n40.1,22.2,2026-08-25T00:00:00Z\r\n");
        try
        {
            var factory = new RecordingFactory();
            await factory.SeedAsync(new LocationImport
            {
                Id = 72, UserId = "bounded-owner", FilePath = path,
                FileType = LocationImportFileType.Csv, Status = ImportStatus.InProgress,
                EnrichmentRequested = true, TotalRecords = 0, LastProcessedIndex = 0
            });
            factory.Reset();
            factory.StopImportOnCreation(72, 3);
            var handoff = new ContextBoundaryHandoff(factory);
            var reverse = new ReverseGeocodingService(new HttpClient(), NullLogger<BaseApiController>.Instance);
            var service = new LocationImportService(factory, reverse,
                NullLogger<LocationImportService>.Instance,
                new LocationDataParserFactory(NullLoggerFactory.Instance), new SseService(), handoff);

            var outcome = await service.ProcessImportExecution(72, 0, CancellationToken.None);

            Assert.Equal(LocationImportExecutionOutcome.Cancelled, outcome);
            Assert.InRange(factory.MaxAlive, 0, 1);
            Assert.Equal(0, factory.Alive);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Worker_DisposesEachBatchContextAndBoundsTrackedLocations()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bounded-import-{Guid.NewGuid():N}.csv");
        var rows = Enumerable.Range(0, 51).Select(index =>
            $"40.1,22.2,2026-08-25T00:00:{index % 60:00}Z,{Guid.NewGuid():D}");
        await File.WriteAllTextAsync(path,
            "Latitude,Longitude,TimestampUtc,IdempotencyKey\r\n" + string.Join("\r\n", rows));
        try
        {
            var factory = new RecordingFactory();
            await factory.SeedAsync(new LocationImport
            {
                Id = 71,
                UserId = "bounded-owner",
                FilePath = path,
                FileType = LocationImportFileType.Csv,
                Status = ImportStatus.InProgress,
                LastProcessedIndex = 0,
                TotalRecords = 0
            });
            factory.Reset();
            var observer = new BatchObserver(factory);
            var reverse = new ReverseGeocodingService(new HttpClient(), NullLogger<BaseApiController>.Instance);
            var service = new LocationImportService(factory, reverse,
                NullLogger<LocationImportService>.Instance,
                new LocationDataParserFactory(NullLoggerFactory.Instance), new SseService(), null, observer);

            var outcome = await service.ProcessImportExecution(71, 0, CancellationToken.None);

            Assert.Equal(LocationImportExecutionOutcome.Completed, outcome);
            Assert.Equal(2, observer.Boundaries);
            Assert.Equal(0, factory.Alive);
            Assert.True(factory.Created >= 5);
            Assert.InRange(factory.MaxTrackedLocations, 0, 50);
        }
        finally { File.Delete(path); }
    }

    private sealed class BatchObserver(RecordingFactory factory) : ILocationImportLifecycleObserver
    {
        internal int Boundaries { get; private set; }
        public Task AfterBatchCommittedAsync(int importId, int epoch, int processed, CancellationToken token)
        {
            Assert.Equal(0, factory.Alive);
            Boundaries++;
            return Task.CompletedTask;
        }

        public Task BeforeTerminalPersistenceAsync(
            int importId, int epoch, LocationImportExecutionOutcome outcome, CancellationToken token) =>
            Task.CompletedTask;
    }

    private sealed class RecordingFactory : IDbContextFactory<ApplicationDbContext>
    {
        private readonly DbContextOptions<ApplicationDbContext> options;
        private readonly IServiceProvider services = new ServiceCollection().BuildServiceProvider();
        private int alive;
        private int created;
        private int maxTrackedLocations;
        private int maxAlive;
        private int stopOnCreation;
        private int stopImportId;

        internal RecordingFactory() => options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        internal int Alive => Volatile.Read(ref alive);
        internal int Created => Volatile.Read(ref created);
        internal int MaxTrackedLocations => Volatile.Read(ref maxTrackedLocations);
        internal int MaxAlive => Volatile.Read(ref maxAlive);

        public ApplicationDbContext CreateDbContext()
        {
            var ordinal = Volatile.Read(ref created) + 1;
            if (ordinal == Volatile.Read(ref stopOnCreation))
            {
                using var authority = new ApplicationDbContext(options, services);
                var import = authority.LocationImports.Single(item => item.Id == stopImportId);
                import.Status = ImportStatus.Stopping;
                authority.SaveChanges();
            }
            var currentAlive = Interlocked.Increment(ref alive);
            maxAlive = Math.Max(maxAlive, currentAlive);
            Interlocked.Increment(ref created);
            return new RecordingContext(options, services, this);
        }

        internal async Task SeedAsync(LocationImport import)
        {
            await using var context = CreateDbContext();
            context.LocationImports.Add(import);
            await context.SaveChangesAsync();
        }

        internal void Reset()
        {
            created = 0;
            maxTrackedLocations = 0;
            maxAlive = 0;
        }

        internal void StopImportOnCreation(int importId, int creation)
        {
            stopImportId = importId;
            stopOnCreation = creation;
        }

        private void Disposed(RecordingContext context)
        {
            var tracked = context.ChangeTracker.Entries<Location>().Count();
            maxTrackedLocations = Math.Max(maxTrackedLocations, tracked);
            Interlocked.Decrement(ref alive);
        }

        private sealed class RecordingContext(
            DbContextOptions<ApplicationDbContext> options,
            IServiceProvider services,
            RecordingFactory owner) : ApplicationDbContext(options, services)
        {
            private int reported;
            public override void Dispose()
            {
                if (Interlocked.Exchange(ref reported, 1) == 0) owner.Disposed(this);
                base.Dispose();
            }
            public override async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref reported, 1) == 0) owner.Disposed(this);
                await base.DisposeAsync();
            }
        }
    }


    private sealed class ContextBoundaryHandoff(RecordingFactory factory) : IImportEnrichmentHandoff
    {
        internal bool Invoked { get; private set; }
        public Task EnsureAsync(string userId, CancellationToken cancellationToken = default)
        {
            Invoked = true;
            Assert.Equal(0, factory.Alive);
            return Task.CompletedTask;
        }
        public Task<EnrichmentCommandResult> StartAsync(string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<EnrichmentCommandResult> RetryDeferredAsync(string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<EnrichmentCommandResult> RepairIncompleteAsync(string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<EnrichmentCommandResult> PauseAsync(string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<EnrichmentCommandResult> ResumeAsync(string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<EnrichmentCommandResult> CancelAsync(string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
