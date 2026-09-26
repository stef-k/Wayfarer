using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Deterministic active-work ceilings and released mobile cadence contracts.</summary>
public class ApiWorkAdmissionTests
{
    /// <summary>Global saturation takes precedence and does not allocate denied identity buckets.</summary>
    [Theory]
    [InlineData(32, 16)]
    [InlineData(64, 8)]
    public void CeilingsAndExactOnceRelease(int global, int perIdentity)
    {
        var owner = new ApiWorkAdmission(global, perIdentity);
        var leases = new List<IDisposable>();
        try
        {
            for (var i = 0; i < perIdentity; i++)
                leases.Add(Assert.IsAssignableFrom<IDisposable>(owner.TryAcquire("same", out _)));
            Assert.Null(owner.TryAcquire("same", out var identityStatus));
            Assert.Equal(429, identityStatus);
            for (var i = perIdentity; i < global; i++)
                leases.Add(Assert.IsAssignableFrom<IDisposable>(owner.TryAcquire(i.ToString(), out _)));
            Assert.Null(owner.TryAcquire("new", out var globalStatus));
            Assert.Equal(503, globalStatus);
            Assert.Equal(global - perIdentity + 1, owner.IdentityCount);
        }
        finally
        {
            foreach (var lease in leases) { lease.Dispose(); lease.Dispose(); }
        }
        Assert.Equal(0, owner.IdentityCount);
        using var next = owner.TryAcquire("same", out var status);
        Assert.NotNull(next);
        Assert.Equal(0, status);
    }

    /// <summary>Two devices can recover 300/hour alongside GPS and direct UI without time-window debt.</summary>
    [Theory]
    [InlineData(60)]
    [InlineData(300)]
    public void ReleasedCadenceAndReconnectHaveNoTimeDebt(int gpsIntervalSeconds)
    {
        var owner = new ApiWorkAdmission(64, 8);
        var queueAttempts = 0;
        for (var second = 0; second < 3600; second += 12)
        {
            var live = new List<IDisposable>();
            // Queue from each device, plus direct UI overlap and reconnect/reset bursts.
            var count = 3 + (second % gpsIntervalSeconds == 0 ? 2 : 0) + (second == 0 ? 3 : 0);
            for (var i = 0; i < count; i++)
                live.Add(Assert.IsAssignableFrom<IDisposable>(owner.TryAcquire("one-user", out _)));
            foreach (var lease in live) lease.Dispose();
            queueAttempts++;
        }
        Assert.Equal(300, queueAttempts);
        Assert.Equal(0, owner.IdentityCount);
    }
}
