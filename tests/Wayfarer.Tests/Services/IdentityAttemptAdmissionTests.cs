using System.Net;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Deterministic client budgets, expiry, normalization and bounded saturation.</summary>
public sealed class IdentityAttemptAdmissionTests
{
    [Fact]
    public void Budget_IsSharedAcrossEquivalentAddresses_AndResetsAtExpiry()
    {
        var clock = new ManualClock();
        var admission = new IdentityAttemptAdmission(clock);
        var client = IPAddress.Parse("192.0.2.1");
        for (var attempt = 0; attempt < IdentityAttemptAdmission.AttemptLimit; attempt++)
            Assert.Equal(0, admission.Admit(client));
        Assert.Equal(300, admission.Admit(client.MapToIPv6()));
        Assert.Equal(0, admission.Admit(IPAddress.Parse("192.0.2.2")));
        Assert.Equal(300, admission.Admit(null));
        clock.Now += IdentityAttemptAdmission.Window - TimeSpan.FromMilliseconds(100);
        Assert.Equal(1, admission.Admit(client));
        clock.Now += TimeSpan.FromMilliseconds(100);
        Assert.Equal(0, admission.Admit(client));
    }

    [Fact]
    public void FullStore_DoesNotEvictLiveBudgets_AndExpiredClientsAreReclaimed()
    {
        var clock = new ManualClock();
        var admission = new IdentityAttemptAdmission(clock);
        for (var i = 0; i < IdentityAttemptAdmission.ClientLimit; i++)
            Assert.Equal(0, admission.Admit(new IPAddress(new byte[] { 10, 0, (byte)(i >> 8), (byte)i })));
        var newcomer = IPAddress.Parse("203.0.113.1");
        Assert.Equal(300, admission.Admit(newcomer));
        Assert.Equal(0, admission.Admit(IPAddress.Parse("10.0.0.0")));
        clock.Now += IdentityAttemptAdmission.Window;
        Assert.Equal(0, admission.Admit(newcomer));
    }

    [Fact]
    public async Task ConcurrentAttempts_CannotOverspendBudget()
    {
        var admission = new IdentityAttemptAdmission(new ManualClock());
        var results = await Task.WhenAll(Enumerable.Range(0, 100)
            .Select(_ => Task.Run(() => admission.Admit(IPAddress.Loopback))));
        Assert.Equal(IdentityAttemptAdmission.AttemptLimit, results.Count(result => result == 0));
    }

    /// <summary>Moves only when a test advances time; no wall-clock waits.</summary>
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
