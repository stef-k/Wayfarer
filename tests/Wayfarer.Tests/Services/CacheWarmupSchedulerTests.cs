using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tests for <see cref="CacheWarmupScheduler"/>: new trigger creation,
/// debounce rescheduling, and per-trip trigger identity.
/// </summary>
public class CacheWarmupSchedulerTests
{
    [Fact]
    public async Task ScheduleWarmupAsync_CreatesNewTrigger_WhenNoneExists()
    {
        var schedulerMock = new Mock<IScheduler>();
        var tripId = Guid.NewGuid();
        var triggerKey = new TriggerKey($"CacheWarmup-{tripId}", "CacheWarmup");

        schedulerMock.Setup(s => s.CheckExists(triggerKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var service = new CacheWarmupScheduler(schedulerMock.Object, NullLogger<CacheWarmupScheduler>.Instance);

        await service.ScheduleWarmupAsync(tripId);

        // Should schedule a new job (not reschedule)
        schedulerMock.Verify(s => s.ScheduleJob(
            It.IsAny<IJobDetail>(),
            It.Is<ITrigger>(t => t.Key.Equals(triggerKey)),
            It.IsAny<CancellationToken>()), Times.Once);
        schedulerMock.Verify(s => s.RescheduleJob(
            It.IsAny<TriggerKey>(),
            It.IsAny<ITrigger>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScheduleWarmupAsync_ReschedulesExisting_WhenTriggerExists()
    {
        var schedulerMock = new Mock<IScheduler>();
        var tripId = Guid.NewGuid();
        var triggerKey = new TriggerKey($"CacheWarmup-{tripId}", "CacheWarmup");

        schedulerMock.Setup(s => s.CheckExists(triggerKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var service = new CacheWarmupScheduler(schedulerMock.Object, NullLogger<CacheWarmupScheduler>.Instance);

        await service.ScheduleWarmupAsync(tripId);

        // Should reschedule (debounce), not create new
        schedulerMock.Verify(s => s.RescheduleJob(
            triggerKey,
            It.IsAny<ITrigger>(),
            It.IsAny<CancellationToken>()), Times.Once);
        schedulerMock.Verify(s => s.ScheduleJob(
            It.IsAny<IJobDetail>(),
            It.IsAny<ITrigger>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScheduleWarmupAsync_UsesPerTripTriggerIdentity()
    {
        var schedulerMock = new Mock<IScheduler>();
        var tripId1 = Guid.NewGuid();
        var tripId2 = Guid.NewGuid();

        schedulerMock.Setup(s => s.CheckExists(It.IsAny<TriggerKey>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var service = new CacheWarmupScheduler(schedulerMock.Object, NullLogger<CacheWarmupScheduler>.Instance);

        await service.ScheduleWarmupAsync(tripId1);
        await service.ScheduleWarmupAsync(tripId2);

        // Verify two different trigger keys were used
        schedulerMock.Verify(s => s.ScheduleJob(
            It.IsAny<IJobDetail>(),
            It.Is<ITrigger>(t => t.Key.Name == $"CacheWarmup-{tripId1}"),
            It.IsAny<CancellationToken>()), Times.Once);
        schedulerMock.Verify(s => s.ScheduleJob(
            It.IsAny<IJobDetail>(),
            It.Is<ITrigger>(t => t.Key.Name == $"CacheWarmup-{tripId2}"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
