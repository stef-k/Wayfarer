using Wayfarer.Services.ExternalRouting;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves deterministic provider-scoped pacing without wall-clock sleeps.</summary>
public sealed class RoutingProviderPacerTests
{
    [Fact]
    public void ProductionAuthorityExposesBoundedContract()
    {
        Assert.Equal(32, RoutingProviderPacer.MaximumQueuedWaiters);
        Assert.Equal(TimeSpan.FromSeconds(120), RoutingProviderPacer.MaximumWait);
        Assert.Equal(TimeSpan.FromMinutes(5), RoutingProviderPacer.MinimumIdleLifetime);
    }

    [Fact]
    public async Task AttemptStartsAreExactlySpacedAndFifo()
    {
        var time = new ManualTimeProvider();
        var pacer = new RoutingProviderPacer(time);
        var provider = Guid.NewGuid();
        Assert.True(pacer.ApplyConfiguration(provider, 1, 1100));
        var first = await pacer.WaitAsync(provider, 1, CancellationToken.None);
        first.Turn!.RecordAttemptStart(); first.Turn.Dispose();
        var secondTask = pacer.WaitAsync(provider, 1, CancellationToken.None);
        var thirdTask = pacer.WaitAsync(provider, 1, CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(1099));
        Assert.False(secondTask.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        var second = await secondTask;
        Assert.False(thirdTask.IsCompleted);
        second.Turn!.RecordAttemptStart(); second.Turn.Dispose();
        time.Advance(TimeSpan.FromMilliseconds(1100));
        Assert.True((await thirdTask).Succeeded);
    }

    [Fact]
    public async Task ZeroIntervalIsAtomicAndProvidersAreIndependent()
    {
        var pacer = new RoutingProviderPacer(new ManualTimeProvider());
        var firstProvider = Guid.NewGuid(); var secondProvider = Guid.NewGuid();
        pacer.ApplyConfiguration(firstProvider, 1, 0); pacer.ApplyConfiguration(secondProvider, 1, 60000);
        var first = await pacer.WaitAsync(firstProvider, 1, CancellationToken.None);
        var queued = pacer.WaitAsync(firstProvider, 1, CancellationToken.None);
        var independent = await pacer.WaitAsync(secondProvider, 1, CancellationToken.None);
        Assert.False(queued.IsCompleted); Assert.True(independent.Succeeded);
        first.Turn!.Dispose(); Assert.True((await queued).Succeeded); independent.Turn!.Dispose();
    }

    [Fact]
    public async Task QueueCapCancellationAndStaleVersionsAreBounded()
    {
        var pacer = new RoutingProviderPacer(new ManualTimeProvider()); var provider = Guid.NewGuid();
        pacer.ApplyConfiguration(provider, 2, 0);
        Assert.False(pacer.ApplyConfiguration(provider, 1, 60000));
        Assert.Equal("provider-configuration-stale", (await pacer.WaitAsync(provider, 1, CancellationToken.None)).ErrorCode);
        var owner = await pacer.WaitAsync(provider, 2, CancellationToken.None);
        var cancellations = Enumerable.Range(0, 32).Select(_ => new CancellationTokenSource()).ToArray();
        var queued = cancellations.Select(source => pacer.WaitAsync(provider, 2, source.Token)).ToArray();
        Assert.Equal("routing-rate-limited", (await pacer.WaitAsync(provider, 2, CancellationToken.None)).ErrorCode);
        cancellations[0].Cancel(); Assert.Equal("request-cancelled", (await queued[0]).ErrorCode);
        owner.Turn!.Dispose(); Assert.True((await queued[1]).Succeeded);
        foreach (var source in cancellations.Skip(2)) source.Cancel();
    }

    [Fact]
    public async Task IntervalChangesPreserveLastStartAndRejectStaleWaiters()
    {
        var time = new ManualTimeProvider(); var pacer = new RoutingProviderPacer(time); var provider = Guid.NewGuid();
        pacer.ApplyConfiguration(provider, 1, 1000);
        var first = await pacer.WaitAsync(provider, 1, CancellationToken.None);
        first.Turn!.RecordAttemptStart(); first.Turn.Dispose(); time.Advance(TimeSpan.FromMilliseconds(500));
        var waiting = pacer.WaitAsync(provider, 1, CancellationToken.None);
        pacer.ApplyConfiguration(provider, 2, 2000);
        Assert.Equal("provider-configuration-stale", (await waiting).ErrorCode);
        var increased = pacer.WaitAsync(provider, 2, CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(1499)); Assert.False(increased.IsCompleted);
        pacer.ApplyConfiguration(provider, 3, 1000);
        Assert.Equal("provider-configuration-stale", (await increased).ErrorCode);
        Assert.True((await pacer.WaitAsync(provider, 3, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task WaitTimeoutAndIdleCleanupReleaseAllState()
    {
        var time = new ManualTimeProvider(); var pacer = new RoutingProviderPacer(time); var provider = Guid.NewGuid();
        pacer.ApplyConfiguration(provider, 1, 0);
        var owner = await pacer.WaitAsync(provider, 1, CancellationToken.None);
        var waiting = pacer.WaitAsync(provider, 1, CancellationToken.None);
        time.Advance(RoutingProviderPacer.MaximumWait);
        Assert.Equal("routing-timeout", (await waiting).ErrorCode);
        owner.Turn!.Dispose();
        time.Advance(RoutingProviderPacer.MinimumIdleLifetime);
        Assert.Equal(1, pacer.CleanupIdle());
        Assert.Equal(0, pacer.GateCount);
        Assert.True(pacer.ApplyConfiguration(provider, 2, 0));
        Assert.Equal(1, pacer.GateCount);
    }

    [Fact]
    public async Task AbsoluteDeadlineStillWinsAfterWaiterBecomesHead()
    {
        var time = new ManualTimeProvider(); var pacer = new RoutingProviderPacer(time); var provider = Guid.NewGuid();
        pacer.ApplyConfiguration(provider, 1, 60000);
        var owner = await pacer.WaitAsync(provider, 1, CancellationToken.None);
        owner.Turn!.RecordAttemptStart();
        var waiting = pacer.WaitAsync(provider, 1, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(119));
        owner.Turn.Dispose();
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal("routing-timeout", (await waiting).ErrorCode);
    }

    [Fact]
    public async Task AbsoluteDeadlineWinsWhenHeadReadinessCompletesSimultaneously()
    {
        var time = new ManualTimeProvider(); var pacer = new RoutingProviderPacer(time); var provider = Guid.NewGuid();
        pacer.ApplyConfiguration(provider, 1, 0);
        var owner = await pacer.WaitAsync(provider, 1, CancellationToken.None);
        var waiting = pacer.WaitAsync(provider, 1, CancellationToken.None);
        time.Advance(RoutingProviderPacer.MaximumWait);
        owner.Turn!.Dispose();

        Assert.Equal("routing-timeout", (await waiting).ErrorCode);
    }

    [Fact]
    public async Task CommittedDecreaseReevaluatesQueuedWaiterAndStalePublicationIsIgnored()
    {
        var time = new ManualTimeProvider(); var pacer = new RoutingProviderPacer(time); var provider = Guid.NewGuid();
        pacer.ApplyConfiguration(provider, 1, 2000);
        var first = await pacer.WaitAsync(provider, 1, CancellationToken.None);
        first.Turn!.RecordAttemptStart(); first.Turn.Dispose();
        var waiting = pacer.WaitAsync(provider, 1, CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(1000));

        Assert.True(pacer.ApplyConfiguration(provider, 2, 1000));
        Assert.False(pacer.ApplyConfiguration(provider, 1, 60000));
        Assert.True((await waiting).Succeeded);
    }

    [Fact]
    public async Task CommittedIncreasePreventsQueuedWaiterUsingOlderInterval()
    {
        var time = new ManualTimeProvider(); var pacer = new RoutingProviderPacer(time); var provider = Guid.NewGuid();
        pacer.ApplyConfiguration(provider, 1, 1000);
        var first = await pacer.WaitAsync(provider, 1, CancellationToken.None);
        first.Turn!.RecordAttemptStart(); first.Turn.Dispose();
        var waiting = pacer.WaitAsync(provider, 1, CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(500));

        Assert.True(pacer.ApplyConfiguration(provider, 2, 2000));
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.False(waiting.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1000));
        Assert.True((await waiting).Succeeded);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _sync = new(); private readonly List<ManualTimer> _timers = []; private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(_timestamp);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, _timestamp + dueTime.Ticks);
            lock (_sync) _timers.Add(timer); return timer;
        }
        public void Advance(TimeSpan duration)
        {
            _timestamp += duration.Ticks; ManualTimer[] due;
            lock (_sync) due = _timers.Where(timer => !timer.Disposed && timer.Due <= _timestamp).ToArray();
            foreach (var timer in due) timer.Fire();
        }
        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, long due) : ITimer
        {
            public long Due { get; private set; } = due; public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period) { Due = owner._timestamp + dueTime.Ticks; return true; }
            public void Fire() { if (!Disposed) callback(state); }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
