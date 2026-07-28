using Wayfarer.Models;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Verifies the approved starter transport-profile catalog.</summary>
public sealed class TransportProfileSeedDataTests
{
    /// <summary>Proves every approved key, expanded rail profile, speed, and deterministic order is present.</summary>
    [Fact]
    public void Create_ReturnsCompleteApprovedCatalog()
    {
        var profiles = TransportProfileSeedData.Create();

        Assert.Equal(15, profiles.Count);
        Assert.Equal(
            ["walk", "bicycle", "bike", "car", "bus", "tram", "metro", "regional-train", "train", "intercity-train", "high-speed-train", "ferry", "boat", "flight", "helicopter"],
            profiles.Select(profile => profile.Key));
        Assert.Equal(250, profiles.Single(profile => profile.Key == "high-speed-train").PlanningSpeedKmh);
        Assert.All(profiles, profile => Assert.True(profile.IsSeeded && profile.IsActive));
    }
}
