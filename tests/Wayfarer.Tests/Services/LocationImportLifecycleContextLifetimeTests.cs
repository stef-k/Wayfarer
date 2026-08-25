using Microsoft.EntityFrameworkCore;
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

    private sealed class RecordingFactory : IDbContextFactory<ApplicationDbContext>
    {
        private readonly DbContextOptions<ApplicationDbContext> options =
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        private readonly IServiceProvider services = new ServiceCollection().BuildServiceProvider();
        private int alive;
        private int nextId;

        internal int Alive => Volatile.Read(ref alive);
        internal List<int> CreatedIds { get; } = [];
        internal TaskCompletionSource FirstDisposal { get; private set; } = NewSignal();

        public ApplicationDbContext CreateDbContext()
        {
            var id = Interlocked.Increment(ref nextId);
            lock (CreatedIds) CreatedIds.Add(id);
            Interlocked.Increment(ref alive);
            return new RecordingContext(options, services, () =>
            {
                Interlocked.Decrement(ref alive);
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
            CreatedIds.Clear();
            FirstDisposal = NewSignal();
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RecordingContext(DbContextOptions<ApplicationDbContext> options,
        IServiceProvider services, Action disposed) : ApplicationDbContext(options, services)
    {
        private int reported;
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
}
