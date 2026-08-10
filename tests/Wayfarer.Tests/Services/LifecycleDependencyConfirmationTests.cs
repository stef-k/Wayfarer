using Microsoft.AspNetCore.DataProtection;
using System.Text.Json;
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

    /// <summary>Requires warning counts to represent associations rather than distinct dependent Places.</summary>
    [Fact]
    public void WarningCountsEveryWaypointAssociationAndKeepsSamplesStable()
    {
        var confirmation = new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider());
        var sharedPlaceId = Guid.NewGuid();
        var segmentIds = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).Order().ToArray();
        var dependencies = new LifecycleDependencies(
            [],
            segmentIds,
            segmentIds.Reverse().Select(segmentId => (segmentId, sharedPlaceId)).ToArray(),
            [],
            []);

        var warning = confirmation.Create(
            "place-delete-dependencies",
            "place-delete",
            "owner",
            Guid.NewGuid(),
            sharedPlaceId,
            dependencies);

        Assert.Equal(7, warning.WaypointAssociations.Count);
        Assert.Equal(segmentIds.Take(5), warning.WaypointAssociations.Ids);
        Assert.True(warning.WaypointAssociations.HasMore);
    }

    /// <summary>Proves added, removed, and role-changed identities invalidate an otherwise scoped token.</summary>
    [Fact]
    public void DependencyAdditionRemovalAndRoleChangeInvalidateTokenEvenWhenCountsMatch()
    {
        var confirmation = new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider());
        var tripId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var segmentA = Guid.NewGuid();
        var segmentB = Guid.NewGuid();
        var placeId = Guid.NewGuid();
        var original = new LifecycleDependencies([], [segmentA], [(segmentA, placeId)], [], []);
        var warning = confirmation.Create("place-delete-dependencies", "place-delete", "owner", tripId, targetId, original);

        Assert.False(confirmation.IsValid(warning.ConfirmationToken, warning.Operation, "owner", tripId, targetId,
            new([], [segmentA, segmentB], [(segmentA, placeId), (segmentB, placeId)], [], [])));
        Assert.False(confirmation.IsValid(warning.ConfirmationToken, warning.Operation, "owner", tripId, targetId,
            new([], [], [], [], [])));
        Assert.False(confirmation.IsValid(warning.ConfirmationToken, warning.Operation, "owner", tripId, targetId,
            new([segmentA], [], [(segmentA, placeId)], [], [])));
        Assert.False(confirmation.IsValid(warning.ConfirmationToken, warning.Operation, "owner", tripId, targetId,
            new([], [segmentB], [(segmentB, placeId)], [], [])));
    }

    /// <summary>Restricts the serialized warning to bounded identifiers and opaque token metadata.</summary>
    [Fact]
    public void WarningSerializationContainsNoPrivateLifecycleContent()
    {
        var confirmation = new LifecycleDependencyConfirmation(new EphemeralDataProtectionProvider());
        var warning = confirmation.Create(
            "region-delete-dependencies",
            "region-delete",
            "owner",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Dependencies());

        var json = JsonSerializer.Serialize(warning);

        Assert.DoesNotContain("name", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("notes", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("address", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("coordinate", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("geometry", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("owner", json, StringComparison.Ordinal);
        Assert.DoesNotContain("LIFE-1", json, StringComparison.Ordinal);
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
