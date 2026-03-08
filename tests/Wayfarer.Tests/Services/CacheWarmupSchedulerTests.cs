using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Quartz;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tests for <see cref="CacheWarmupScheduler"/>: new trigger creation,
/// debounce rescheduling, per-trip trigger identity, and immediate mode.
/// </summary>
public class CacheWarmupSchedulerTests
{
    /// <summary>Tolerance for comparing trigger start times.</summary>
    private static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2);
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

    [Fact]
    public async Task ScheduleWarmupAsync_FallsBackToReschedule_WhenScheduleRaces()
    {
        var schedulerMock = new Mock<IScheduler>();
        var tripId = Guid.NewGuid();
        var triggerKey = new TriggerKey($"CacheWarmup-{tripId}", "CacheWarmup");

        // CheckExists returns false (no existing trigger)
        schedulerMock.Setup(s => s.CheckExists(triggerKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // ScheduleJob throws ObjectAlreadyExistsException (TOCTOU race)
        schedulerMock.Setup(s => s.ScheduleJob(
                It.IsAny<IJobDetail>(),
                It.IsAny<ITrigger>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ObjectAlreadyExistsException("trigger already exists"));

        var service = new CacheWarmupScheduler(schedulerMock.Object, NullLogger<CacheWarmupScheduler>.Instance);

        // Should not throw — falls back to RescheduleJob
        await service.ScheduleWarmupAsync(tripId);

        // Verify fallback path: RescheduleJob called once
        schedulerMock.Verify(s => s.RescheduleJob(
            triggerKey,
            It.IsAny<ITrigger>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScheduleWarmupAsync_UsesImmediateDelay_WhenImmediateAndNoTriggerExists()
    {
        var schedulerMock = new Mock<IScheduler>();
        var tripId = Guid.NewGuid();
        var triggerKey = new TriggerKey($"CacheWarmup-{tripId}", "CacheWarmup");
        ITrigger? capturedTrigger = null;

        schedulerMock.Setup(s => s.CheckExists(triggerKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        schedulerMock.Setup(s => s.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()))
            .Callback<IJobDetail, ITrigger, CancellationToken>((_, t, _) => capturedTrigger = t)
            .ReturnsAsync(DateTimeOffset.UtcNow);

        var service = new CacheWarmupScheduler(schedulerMock.Object, NullLogger<CacheWarmupScheduler>.Instance);

        await service.ScheduleWarmupAsync(tripId, immediate: true);

        // Trigger should fire ~5 seconds from now (not 1 minute)
        Assert.NotNull(capturedTrigger);
        var expectedStart = DateTimeOffset.UtcNow.AddSeconds(5);
        Assert.InRange(capturedTrigger!.StartTimeUtc, expectedStart.Subtract(TimeTolerance), expectedStart.Add(TimeTolerance));
    }

    [Fact]
    public async Task ScheduleWarmupAsync_UsesNormalDelay_WhenImmediateButTriggerExists()
    {
        var schedulerMock = new Mock<IScheduler>();
        var tripId = Guid.NewGuid();
        var triggerKey = new TriggerKey($"CacheWarmup-{tripId}", "CacheWarmup");
        ITrigger? capturedTrigger = null;

        schedulerMock.Setup(s => s.CheckExists(triggerKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        schedulerMock.Setup(s => s.RescheduleJob(triggerKey, It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()))
            .Callback<TriggerKey, ITrigger, CancellationToken>((_, t, _) => capturedTrigger = t)
            .ReturnsAsync((DateTimeOffset?)DateTimeOffset.UtcNow);

        var service = new CacheWarmupScheduler(schedulerMock.Object, NullLogger<CacheWarmupScheduler>.Instance);

        await service.ScheduleWarmupAsync(tripId, immediate: true);

        // Debounce always wins — should use 1-minute delay even with immediate flag
        Assert.NotNull(capturedTrigger);
        var expectedStart = DateTimeOffset.UtcNow.AddMinutes(1);
        Assert.InRange(capturedTrigger!.StartTimeUtc, expectedStart.Subtract(TimeTolerance), expectedStart.Add(TimeTolerance));
    }

    [Fact]
    public async Task ScheduleWarmupAsync_RaceFallback_UsesNormalDelay_RegardlessOfImmediate()
    {
        var schedulerMock = new Mock<IScheduler>();
        var tripId = Guid.NewGuid();
        var triggerKey = new TriggerKey($"CacheWarmup-{tripId}", "CacheWarmup");
        ITrigger? capturedTrigger = null;

        schedulerMock.Setup(s => s.CheckExists(triggerKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        schedulerMock.Setup(s => s.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ObjectAlreadyExistsException("trigger already exists"));
        schedulerMock.Setup(s => s.RescheduleJob(triggerKey, It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()))
            .Callback<TriggerKey, ITrigger, CancellationToken>((_, t, _) => capturedTrigger = t)
            .ReturnsAsync((DateTimeOffset?)DateTimeOffset.UtcNow);

        var service = new CacheWarmupScheduler(schedulerMock.Object, NullLogger<CacheWarmupScheduler>.Instance);

        await service.ScheduleWarmupAsync(tripId, immediate: true);

        // Race fallback always uses normal delay
        Assert.NotNull(capturedTrigger);
        var expectedStart = DateTimeOffset.UtcNow.AddMinutes(1);
        Assert.InRange(capturedTrigger!.StartTimeUtc, expectedStart.Subtract(TimeTolerance), expectedStart.Add(TimeTolerance));
    }

    [Fact]
    public async Task ScheduleWarmupAsync_UsesNormalDelay_WhenNotImmediateAndNoTriggerExists()
    {
        var schedulerMock = new Mock<IScheduler>();
        var tripId = Guid.NewGuid();
        var triggerKey = new TriggerKey($"CacheWarmup-{tripId}", "CacheWarmup");
        ITrigger? capturedTrigger = null;

        schedulerMock.Setup(s => s.CheckExists(triggerKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        schedulerMock.Setup(s => s.ScheduleJob(It.IsAny<IJobDetail>(), It.IsAny<ITrigger>(), It.IsAny<CancellationToken>()))
            .Callback<IJobDetail, ITrigger, CancellationToken>((_, t, _) => capturedTrigger = t)
            .ReturnsAsync(DateTimeOffset.UtcNow);

        var service = new CacheWarmupScheduler(schedulerMock.Object, NullLogger<CacheWarmupScheduler>.Instance);

        await service.ScheduleWarmupAsync(tripId, immediate: false);

        // Default (non-immediate) should use 1-minute delay
        Assert.NotNull(capturedTrigger);
        var expectedStart = DateTimeOffset.UtcNow.AddMinutes(1);
        Assert.InRange(capturedTrigger!.StartTimeUtc, expectedStart.Subtract(TimeTolerance), expectedStart.Add(TimeTolerance));
    }
}
