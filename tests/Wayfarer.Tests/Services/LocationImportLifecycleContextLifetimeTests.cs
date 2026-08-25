using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Services.LocationImports;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Observes lifecycle context ownership at coordinator and Quartz boundaries.</summary>
public sealed class LocationImportLifecycleContextLifetimeTests
{
    [Fact]
    public async Task Delete_FileSystemBoundaryHasNoLiveContextAndFinalDeleteUsesFreshContext()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wayfarer-context-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, "fixture");
        try
        {
            var factory = new RecordingFactory();
            await factory.SeedAsync(Import(path));
            factory.ResetObservation();
            var coordinator = new LocationImportProjectionCoordinator();
            var observer = new FileBoundaryObserver(factory, coordinator, path);
            var scheduler = DeleteScheduler(factory);
            var lifecycle = new LocationImportLifecycle(factory, scheduler.Object,
                NullLogger<LocationImportLifecycle>.Instance, coordinator, observer);

            var result = await lifecycle.DeleteAsync("owner", 1);
            observer.CapturePostFileIds();

            Assert.Equal(LocationImportCommandCode.Accepted, result.Code);
            Assert.False(File.Exists(path));
            Assert.Equal(0, factory.Alive);
            Assert.NotEmpty(observer.PreFileIds);
            Assert.All(observer.PreFileIds, id => Assert.Contains($"dispose:{id}", observer.EventsAtBoundary));
            Assert.Single(observer.PostFileIds);
            Assert.DoesNotContain(observer.PostFileIds[0], observer.PreFileIds);
            Assert.Equal(0, await factory.CountImportsAsync());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Delete_ConcurrencyReloadDisposesConflictedContextBeforeFreshAuthorityContext()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wayfarer-conflict-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(path, "fixture");
        try
        {
            var factory = new RecordingFactory();
            await factory.SeedAsync(Import(path));
            factory.ResetObservation();
            factory.ArmDeletionIntentConflict();
            var scheduler = DeleteScheduler(factory);
            var coordinator = new LocationImportProjectionCoordinator();
            var observer = new FileBoundaryObserver(factory, coordinator, path);
            var lifecycle = new LocationImportLifecycle(factory, scheduler.Object,
                NullLogger<LocationImportLifecycle>.Instance, coordinator, observer);

            var result = await lifecycle.DeleteAsync("owner", 1);
            observer.CapturePostFileIds();

            Assert.Equal(LocationImportCommandCode.Accepted, result.Code);
            var conflict = Assert.Single(factory.Events,
                value => value.StartsWith("conflict:", StringComparison.Ordinal));
            var conflictedId = int.Parse(conflict["conflict:".Length..]);
            var reload = factory.Events
                .SkipWhile(value => value != conflict)
                .First(value => value.StartsWith("create:", StringComparison.Ordinal));
            var reloadParts = reload.Split(':');
            var reloadId = int.Parse(reloadParts[1]);
            Assert.NotEqual(conflictedId, reloadId);
            Assert.Equal("0", reloadParts[2]);
            Assert.True(factory.Events.IndexOf($"dispose:{conflictedId}") < factory.Events.IndexOf(reload));
            Assert.Contains(reloadId, observer.PreFileIds);
            Assert.Equal(1, factory.InjectedConflictCount);
            Assert.Equal(0, factory.Alive);
            Assert.Equal(0, await factory.CountImportsAsync());
            scheduler.Verify(x => x.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Start_DisposesIntentBeforeCoordinatorAndUsesFreshPostQuartzContexts()
    {
        var factory = new RecordingFactory();
        await factory.SeedAsync(new LocationImport
        {
            Id = 1, UserId = "owner", FilePath = "upload", FileType = LocationImportFileType.Csv,
            Status = ImportStatus.Stopped, TotalRecords = 0, LastProcessedIndex = 0
        });
        factory.ResetObservation();
        var coordinator = new LocationImportProjectionCoordinator();
        await using var held = await coordinator.AcquireAsync(1);
        var scheduler = Scheduler(factory);
        var lifecycle = new LocationImportLifecycle(factory, scheduler.Object,
            NullLogger<LocationImportLifecycle>.Instance, coordinator);

        var command = lifecycle.StartAsync("owner", 1);
        await factory.FirstDisposal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, factory.Alive);
        Assert.False(command.IsCompleted);
        await held.DisposeAsync();
        Assert.True((await command).Succeeded);
        Assert.Equal(0, factory.Alive);
        Assert.True(factory.CreatedIds.Distinct().Count() >= 4);
    }

    private static Mock<IScheduler> Scheduler(RecordingFactory factory)
    {
        var scheduler = new Mock<IScheduler>();
        scheduler.Setup(x => x.CheckExists(It.IsAny<JobKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        scheduler.Setup(x => x.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(),
                It.IsAny<CancellationToken>()))
            .Returns((IJobDetail _, ITrigger _, CancellationToken _) =>
            {
                Assert.Equal(0, factory.Alive);
                return Task.FromResult(DateTimeOffset.UtcNow);
            });
        return scheduler;
    }

    private static Mock<IScheduler> DeleteScheduler(RecordingFactory factory)
    {
        var scheduler = new Mock<IScheduler>();
        scheduler.Setup(x => x.GetCurrentlyExecutingJobs(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        scheduler.Setup(x => x.GetJobKeys(It.IsAny<Quartz.Impl.Matchers.GroupMatcher<JobKey>>(),
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Assert.Equal(0, factory.Alive);
                return Task.FromResult<IReadOnlyCollection<JobKey>>(new HashSet<JobKey>());
            });
        return scheduler;
    }

    private static LocationImport Import(string path) => new()
    {
        Id = 1, UserId = "owner", FilePath = path, FileType = LocationImportFileType.Csv,
        Status = ImportStatus.Stopped, TotalRecords = 0, LastProcessedIndex = 0
    };

    private sealed class RecordingFactory : IDbContextFactory<ApplicationDbContext>
    {
        private readonly DbContextOptions<ApplicationDbContext> plainOptions;
        private readonly DbContextOptions<ApplicationDbContext> recordingOptions;
        private readonly IServiceProvider services = new ServiceCollection().BuildServiceProvider();
        private int alive;
        private int injectConflict;
        private int injectedConflictCount;
        private int nextId;
        private readonly List<int> createdIds = [];
        private readonly List<string> events = [];

        internal RecordingFactory()
        {
            var database = Guid.NewGuid().ToString();
            plainOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(database).Options;
            recordingOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(database)
                .AddInterceptors(new ConflictInterceptor(this)).Options;
        }

        internal int Alive => Volatile.Read(ref alive);
        internal int InjectedConflictCount => Volatile.Read(ref injectedConflictCount);
        internal List<int> CreatedIds { get { lock (createdIds) return [.. createdIds]; } }
        internal List<string> Events { get { lock (events) return [.. events]; } }
        internal TaskCompletionSource FirstDisposal { get; private set; } = NewSignal();

        public ApplicationDbContext CreateDbContext()
        {
            var id = Interlocked.Increment(ref nextId);
            lock (createdIds) createdIds.Add(id);
            var aliveBefore = Interlocked.Increment(ref alive) - 1;
            Record($"create:{id}:{aliveBefore}");
            return new RecordingContext(recordingOptions, services, id, () =>
            {
                Interlocked.Decrement(ref alive);
                Record($"dispose:{id}");
                FirstDisposal.TrySetResult();
            });
        }

        internal async Task SeedAsync(LocationImport import)
        {
            await using var db = CreateDbContext();
            db.LocationImports.Add(import);
            await db.SaveChangesAsync();
        }

        internal void ResetObservation()
        {
            lock (createdIds) createdIds.Clear();
            lock (events) events.Clear();
            FirstDisposal = NewSignal();
        }

        internal void ArmDeletionIntentConflict() => Interlocked.Exchange(ref injectConflict, 1);

        internal async Task<int> CountImportsAsync()
        {
            await using var db = new ApplicationDbContext(plainOptions, services);
            return await db.LocationImports.CountAsync();
        }

        internal List<string> SnapshotEvents()
        {
            lock (events) return [.. events];
        }

        private void Record(string value)
        {
            lock (events) events.Add(value);
        }

        private async Task InjectConflictAsync(RecordingContext context, CancellationToken token)
        {
            if (Interlocked.Exchange(ref injectConflict, 0) == 0) return;
            await using var authority = new ApplicationDbContext(plainOptions, services);
            var import = await authority.LocationImports.SingleAsync(item => item.Id == 1, token);
            import.DeletionRequestedAtUtc = DateTime.UtcNow;
            await authority.SaveChangesAsync(token);
            Interlocked.Increment(ref injectedConflictCount);
            Record($"conflict:{context.InstanceId}");
            throw new DbUpdateConcurrencyException("Injected deletion-intent conflict.");
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class ConflictInterceptor(RecordingFactory owner) : SaveChangesInterceptor
        {
            public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
                DbContextEventData eventData, InterceptionResult<int> result,
                CancellationToken cancellationToken = default)
            {
                if (eventData.Context is RecordingContext context)
                    await owner.InjectConflictAsync(context, cancellationToken);
                return result;
            }
        }
    }

    private sealed class RecordingContext(DbContextOptions<ApplicationDbContext> options,
        IServiceProvider services, int instanceId, Action disposed) : ApplicationDbContext(options, services)
    {
        private int reported;
        internal int InstanceId { get; } = instanceId;
        public override void Dispose()
        {
            base.Dispose();
            Report();
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            Report();
        }
        private void Report()
        {
            if (Interlocked.Exchange(ref reported, 1) == 0) disposed();
        }
    }

    private sealed class FileBoundaryObserver(RecordingFactory factory,
        LocationImportProjectionCoordinator coordinator, string expectedPath)
        : ILocationImportLifecycleObserver
    {
        internal List<int> PreFileIds { get; private set; } = [];
        internal List<int> PostFileIds { get; private set; } = [];
        internal List<string> EventsAtBoundary { get; private set; } = [];

        public Task BeforeFileDeletionAsync(int importId, string filePath, CancellationToken token)
        {
            Assert.Equal(1, importId);
            Assert.Equal(expectedPath, filePath);
            Assert.Equal(0, factory.Alive);
            Assert.Equal(1, coordinator.ReferenceCount(importId));
            EventsAtBoundary = factory.SnapshotEvents();
            PreFileIds = [.. factory.CreatedIds];
            return Task.CompletedTask;
        }

        public Task AfterBatchCommittedAsync(int importId, int epoch, int processed, CancellationToken token) =>
            Task.CompletedTask;

        public Task BeforeTerminalPersistenceAsync(
            int importId, int epoch, LocationImportExecutionOutcome outcome, CancellationToken token) =>
            Task.CompletedTask;

        internal void CapturePostFileIds() =>
            PostFileIds = factory.CreatedIds.Except(PreFileIds).ToList();
    }
}
