using Microsoft.AspNetCore.DataProtection;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Verifies opaque lifecycle confirmation scope, exact identity binding, expiry, and bounded samples.</summary>
public sealed class LifecycleDependencyConfirmationTests
{
    [Fact]
    public void TokenIsBoundToUserTripOperationTargetAndExactDependencies()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        var confirmation = new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider(), time);
        var tripId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var dependencies = Dependencies();
        var warning = confirmation.Create("place-delete-dependencies", "place-delete", "owner", tripId, targetId, dependencies);

        Assert.True(confirmation.IsValid(warning.ConfirmationToken, "place-delete", "owner", tripId, targetId, dependencies));
        Assert.False(confirmation.IsValid(warning.ConfirmationToken, "place-delete", "other", tripId, targetId, dependencies));
        Assert.False(confirmation.IsValid(warning.ConfirmationToken, "region-delete", "owner", tripId, targetId, dependencies));
        Assert.False(confirmation.IsValid(warning.ConfirmationToken, "place-delete", "owner", Guid.NewGuid(), targetId, dependencies));
        Assert.False(confirmation.IsValid(warning.ConfirmationToken, "place-delete", "owner", tripId, Guid.NewGuid(), dependencies));
        Assert.False(confirmation.IsValid(warning.ConfirmationToken, "place-delete", "owner", tripId, targetId,
            dependencies with { EndpointSegmentIds = dependencies.EndpointSegmentIds.Skip(1).ToArray() }));
    }

    [Fact]
    public void ExpiredOrMalformedTokenIsRejectedAndSamplesAreSortedAndBounded()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        var confirmation = new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider(), time);
        var dependencies = Dependencies();
        var tripId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var warning = confirmation.Create("place-delete-dependencies", "place-delete", "owner", tripId, targetId, dependencies);

        Assert.Equal(7, warning.EndpointSegments.Count);
        Assert.Equal(5, warning.EndpointSegments.Ids.Count);
        Assert.True(warning.EndpointSegments.HasMore);
        Assert.Equal(warning.EndpointSegments.Ids.Order(), warning.EndpointSegments.Ids);
        Assert.False(confirmation.IsValid("not-a-protected-token", "place-delete", "owner", Guid.NewGuid(), Guid.NewGuid(), dependencies));
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.False(confirmation.IsValid(warning.ConfirmationToken, warning.Operation, "owner", tripId, targetId, dependencies));
    }

    private static LifecycleDependencies Dependencies()
    {
        var endpointIds = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).Reverse().ToArray();
        var waypointId = Guid.NewGuid();
        return new(endpointIds, [waypointId], [(waypointId, Guid.NewGuid())], [Guid.NewGuid()], [Guid.NewGuid()]);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => _utcNow;

        /// <summary>Moves test time forward deterministically.</summary>
        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
