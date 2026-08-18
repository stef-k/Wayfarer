using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Pure contract tests for #407 opaque Segment editor tokens.</summary>
public sealed class SegmentEditorTokenTests
{
    /// <summary>Aggregate tokens are opaque, scoped, and preserve only the protected row version.</summary>
    [Fact]
    public void AggregateTokenIsOpaqueScopedAndRejectsMalformedValues()
    {
        var service = new SegmentAggregateTokenService(new EphemeralDataProtectionProvider());
        var tripId = Guid.NewGuid();
        var segmentId = Guid.NewGuid();
        var token = service.Issue("owner", tripId, segmentId, 42);

        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.NotEqual("42", token);
        Assert.True(service.TryRead(token, "owner", tripId, segmentId, out var rowVersion));
        Assert.Equal(42u, rowVersion);
        Assert.False(service.TryRead(token, "other", tripId, segmentId, out _));
        Assert.False(service.TryRead(token, "owner", Guid.NewGuid(), segmentId, out _));
        Assert.False(service.TryRead(token, "owner", tripId, Guid.NewGuid(), out _));
        Assert.False(service.TryRead("42", "owner", tripId, segmentId, out _));
        Assert.False(service.TryRead("malformed", "owner", tripId, segmentId, out _));
    }

    /// <summary>Route confirmations are separately protected, fingerprint-bound, and expiring.</summary>
    [Fact]
    public void RouteConfirmationRejectsDriftAndExpiry()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero));
        var service = new SegmentRouteClearConfirmation(new EphemeralDataProtectionProvider(), clock);
        var segmentId = Guid.NewGuid();
        var issued = service.Issue(segmentId, "fingerprint-a");

        Assert.True(service.IsValid(issued.Token, segmentId, "fingerprint-a"));
        Assert.False(service.IsValid(issued.Token, segmentId, "fingerprint-b"));
        Assert.False(service.IsValid(issued.Token, Guid.NewGuid(), "fingerprint-a"));
        clock.UtcNow = issued.ExpiresAt.AddTicks(1);
        Assert.False(service.IsValid(issued.Token, segmentId, "fingerprint-a"));
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
